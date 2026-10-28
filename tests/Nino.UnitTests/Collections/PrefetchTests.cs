using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using System.Runtime.CompilerServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nino.Core;
using Nino.Core.CompilerServices;

#nullable disable
namespace Nino.UnitTests;

[NinoType]
public class PrefetchItem
{
    public int Id;
    public string Name;
    public List<int> Values;
    public long Value;
}

[NinoType]
public abstract class PrefetchShape
{
    public string Tag;
}

[NinoType]
public sealed class PrefetchCircle : PrefetchShape
{
    public float Radius;
}

[NinoType]
public sealed class PrefetchSquare : PrefetchShape
{
    public int Side;
}

/// <summary>
/// The loops that prefetch long collections of objects (NinoPrefetched, taken for more elements than the last-level
/// cache holds: x64 and Arm64 on .NET 8+, netstandard with prefetching kernels), called directly so that they run
/// whatever the machine's cache: the bytes of the ordinary loop, null slots kept, existing objects reused; arrays and
/// lists of every length through the public API; the cache size the threshold is scaled to; and which formatters the
/// generator gives the prefetching loops.
/// </summary>
[TestClass]
public class PrefetchTests
{
    // Not a multiple of the prefetch distances; prefetched where the last-level cache holds fewer of the elements.
    private const int Long = 70_001;

    private static PrefetchItem[] Items(int count, int seed)
    {
        var random = new Random(seed);
        var items = new PrefetchItem[count];
        for (int i = 0; i < count; i++)
            items[i] = i % 7 == 3
                ? null
                : new PrefetchItem { Id = i, Name = i % 5 == 0 ? null : "item " + i, Values = i % 3 == 0 ? new List<int> { i, -i } : null, Value = i * 7L };
        Shuffle(items, random);
        return items;
    }

    // Random.Shuffle is .NET 8+; this assembly also builds for netstandard2.1.
    private static void Shuffle<T>(T[] values, Random random)
    {
        for (int i = values.Length - 1; i > 0; i--)
        {
            int j = random.Next(i + 1);
            (values[i], values[j]) = (values[j], values[i]);
        }
    }

    private static void AreEqual(PrefetchItem[] expected, IList<PrefetchItem> actual)
    {
        Assert.AreEqual(expected.Length, actual.Count);
        for (int i = 0; i < expected.Length; i++)
        {
            if (expected[i] == null)
            {
                Assert.IsNull(actual[i], $"[{i}]");
                continue;
            }

            Assert.AreEqual(expected[i].Id, actual[i].Id, $"[{i}]");
            Assert.AreEqual(expected[i].Name, actual[i].Name, $"[{i}]");
            CollectionAssert.AreEqual(expected[i].Values, actual[i].Values, $"[{i}]");
            Assert.AreEqual(expected[i].Value, actual[i].Value, $"[{i}]");
        }
    }

    /// <summary>The bytes of <paramref name="items"/> as an array, its elements written by the prefetching loop.</summary>
    private static byte[] WritePrefetched(PrefetchItem[] items)
    {
        var buffer = new NinoArrayBufferWriter();
        var writer = new Writer(buffer);
        writer.WriteLength(items.Length);
        if (items.Length > 0)
            writer = NinoPrefetched.Write<PrefetchItem, NinoGen.Nino_UnitTests_PrefetchItemFormatter>(
                ref Unsafe.As<PrefetchItem, nint>(ref items[0]), items.Length, writer);
        writer.Flush();
        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>Reads <paramref name="bytes"/> (an array) into <paramref name="target"/> by the prefetching loop.</summary>
    private static void ReadPrefetched(byte[] bytes, PrefetchItem[] target)
    {
        var reader = new Reader(bytes);
        Assert.AreEqual(target.Length, reader.ReadLength());
        if (target.Length > 0)
            reader = NinoPrefetched.Read<PrefetchItem, NinoGen.Nino_UnitTests_PrefetchItemFormatter>(
                ref Unsafe.As<PrefetchItem, nint>(ref target[0]), target.Length, reader);
    }

    [TestMethod]
    public void PrefetchingLoopsWriteAndReadTheSameBytes()
    {
        foreach (int count in new[] { 0, 1, 7, 8, 9, 16, 17, 33, 5000 })
        {
            var items = Items(count, count);
            byte[] bytes = NinoSerializer.Serialize(items);
            CollectionAssert.AreEqual(bytes, WritePrefetched(items), $"PrefetchItem[{count}]");

            var target = Items(count, count + 1);
            var before = (PrefetchItem[])target.Clone();
            ReadPrefetched(bytes, target);
            AreEqual(items, target);
            for (int i = 0; i < count; i++)
            {
                if (before[i] != null && items[i] != null)
                    Assert.AreSame(before[i], target[i], $"[{i}]");
            }
        }
    }

    [TestMethod]
    public void TheCacheIsTheLastLevel()
    {
        long bytes = Nino.Core.Internal.NinoPrefetch.CacheBytes;
        Assert.IsTrue(bytes >= 1 << 20, bytes.ToString());
        const string sys = "/sys/devices/system/cpu/cpu0/cache";
        if (!System.IO.Directory.Exists(sys))
            return;
        // The largest size of the highest level, as /sys lists them ("32768K").
        var caches = System.IO.Directory.GetDirectories(sys, "index*")
            .Where(d => System.IO.File.Exists(d + "/level") && System.IO.File.Exists(d + "/size"))
            .Select(d => (Level: int.Parse(System.IO.File.ReadAllText(d + "/level").Trim()),
                Size: System.IO.File.ReadAllText(d + "/size").Trim()))
            .ToList();
        if (caches.Count == 0)
            return;
        int top = caches.Max(c => c.Level);
        long expected = caches.Where(c => c.Level == top).Max(c =>
            c.Size.EndsWith("K") ? long.Parse(c.Size[..^1]) << 10 :
            c.Size.EndsWith("M") ? long.Parse(c.Size[..^1]) << 20 : long.Parse(c.Size));
        Assert.AreEqual(expected, bytes);
    }

    [TestMethod]
    public void LongArraysAndListsRoundTrip()
    {
        foreach (int count in new[] { 0, 1, 16, 17, 1023, 1024, 5000, Long })
        {
            var items = Items(count, count);
            byte[] bytes = NinoSerializer.Serialize(items);
            AreEqual(items, NinoDeserializer.Deserialize<PrefetchItem[]>(bytes));
            var list = items.ToList();
            byte[] listBytes = NinoSerializer.Serialize(list);
            CollectionAssert.AreEqual(bytes, listBytes, $"List<PrefetchItem> x {count}");
            AreEqual(items, NinoDeserializer.Deserialize<List<PrefetchItem>>(listBytes));
        }
    }

    [TestMethod]
    public void LongArraysAreReadIntoTheirObjects()
    {
        var items = Items(Long, 1);
        byte[] bytes = NinoSerializer.Serialize(items);
        var target = Items(Long, 2);
        var before = (PrefetchItem[])target.Clone();
        NinoDeserializer.Deserialize(bytes, ref target);
        AreEqual(items, target);
        int reused = 0;
        for (int i = 0; i < Long; i++)
        {
            if (before[i] != null && items[i] != null)
            {
                Assert.AreSame(before[i], target[i], $"[{i}]");
                reused++;
            }
        }

        Assert.IsTrue(reused > Long / 2);
    }

    [TestMethod]
    public void LongPolymorphicArraysRoundTrip()
    {
        var shapes = new PrefetchShape[Long];
        for (int i = 0; i < Long; i++)
            shapes[i] = (i % 11) switch
            {
                0 => null,
                < 6 => new PrefetchCircle { Tag = "c" + i, Radius = i },
                _ => new PrefetchSquare { Tag = i % 2 == 0 ? null : "s" + i, Side = -i },
            };
        Shuffle(shapes, new Random(3));
        var back = NinoDeserializer.Deserialize<PrefetchShape[]>(NinoSerializer.Serialize(shapes));
        for (int i = 0; i < Long; i++)
        {
            Assert.AreEqual(shapes[i]?.GetType(), back[i]?.GetType(), $"[{i}]");
            Assert.AreEqual(shapes[i]?.Tag, back[i]?.Tag, $"[{i}]");
            if (shapes[i] is PrefetchCircle c)
                Assert.AreEqual(c.Radius, ((PrefetchCircle)back[i]).Radius);
            else if (shapes[i] is PrefetchSquare s)
                Assert.AreEqual(s.Side, ((PrefetchSquare)back[i]).Side);
        }
    }

    [TestMethod]
    public void OnlyNonGenericClassesOfVaryingSizePrefetch()
    {
        const string source = """
            using System.Collections.Generic;
            using Nino.Core;
            namespace Pf {
            [NinoType] public class Item { public int Id; public string Name; public List<int> Values { get; set; } public long Value; }
            [NinoType] public class Computed { public string Name { get => _name; set => _name = value; } private string _name; public List<int> Other; }
            [NinoType] public sealed class Fixed { public int A; public long B; }
            [NinoType] public struct Value { public int A; public string S; }
            [NinoType] public class Box<T> { public T Item; }
            }
            """;
        var compilation = TestCompilation.Create("Pf" + Guid.NewGuid().ToString("N"),
            new[] { CSharpSyntaxTree.ParseText(source, TestCompilation.Parse) });
        GeneratorDriver driver = CSharpGeneratorDriver.Create(new Generator.Generation.NinoGenerator());
        var sources = driver.RunGenerators(compilation).GetRunResult().Results[0].GeneratedSources;
        string Output(string type) =>
            sources.Single(s => s.HintName == $"NinoGen.Pf_{type}Formatter.g.cs").SourceText.ToString();

        // Two reference members at most, fields and auto-properties only, read directly.
        string item = Output("Item");
        // 16 (header) + 4 + 8 + two references of 8 + 64: 172 bytes.
        StringAssert.Contains(item, "INinoPrefetchFormatter.PrefetchBytes => 172;");
        StringAssert.Contains(item, "NinoPrefetched.At(v.Name) + global::Nino.Core.CompilerServices.NinoPrefetched.At(v.Values)");
        string computed = Output("Computed");
        StringAssert.Contains(computed, "NinoPrefetched.At(v.Other);");
        Assert.IsFalse(computed.Contains("v.Name"), "a property with a body of its own");

        foreach (string type in new[] { "Fixed", "Value", "Box_1" })
            Assert.IsFalse(Output(type).Contains("INinoPrefetchFormatter"), type);
    }
}
