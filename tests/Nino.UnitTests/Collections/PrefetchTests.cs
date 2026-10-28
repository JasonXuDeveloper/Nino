using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nino.Core;
using Nino.Core.Internal;

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
/// Long collections of objects not in allocation order are written, and read into their existing objects, in runs that
/// prefetch (NinoPrefetch; x64 and Arm64 on .NET 8+): the lengths here are past this machine's threshold, so the runs
/// are taken whatever its cache, and must give the bytes and objects of the ordinary loop, null slots and partial last
/// runs included. Also: the allocation-order sample, the cache size the threshold is scaled to, and which formatters
/// the generator makes prefetch.
/// </summary>
[TestClass]
public class PrefetchTests
{
    /// <summary>Past the threshold of PrefetchItem's collections, and not a multiple of a run.</summary>
    private static int Long => NinoPrefetch.Of<NinoGen.Nino_UnitTests_PrefetchItemFormatter>.Threshold + 37;

    private static PrefetchItem[] Items(int count, int seed, bool shuffled = true)
    {
        var items = new PrefetchItem[count];
        for (int i = 0; i < count; i++)
            items[i] = i % 7 == 3
                ? null
                : new PrefetchItem
                {
                    Id = i, Name = i % 5 == 0 ? null : "item " + i,
                    Values = i % 3 == 0 ? new List<int> { i, -i } : null, Value = i * 7L,
                };
        if (shuffled)
            Shuffle(items, new Random(seed));
        return items;
    }

    // Random.Shuffle is .NET 8+; this assembly also builds for netstandard2.1. No null among the first elements: they
    // decide whether a collection is in allocation order, and a null there turns the runs off.
    private static void Shuffle<T>(T[] values, Random random) where T : class
    {
        for (int i = values.Length - 1; i > 0; i--)
        {
            int j = random.Next(i + 1);
            (values[i], values[j]) = (values[j], values[i]);
        }

        for (int i = 0, from = 17; i < Math.Min(values.Length, 17); i++)
        {
            while (values[i] == null && from < values.Length)
            {
                (values[i], values[from]) = (values[from], values[i]);
                from++;
            }
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

    /// <summary>The bytes the ordinary loop writes: each element on its own, after the length.</summary>
    private static byte[] OneByOne(PrefetchItem[] items)
    {
        var bytes = new List<byte>(BitConverter.GetBytes(items.Length));
        foreach (var item in items)
            bytes.AddRange(NinoSerializer.Serialize(item));
        return bytes.ToArray();
    }

    [TestMethod]
    public void TheFirstObjectsDecide()
    {
        object[] ordered = Enumerable.Range(0, 64).Select(i => (object)new PrefetchItem { Id = i }).ToArray();
        Assert.IsFalse(NinoPrefetch.Scattered(ref ordered[0]), "allocation order");
        object[] scattered = (object[])ordered.Clone();
        Shuffle(scattered, new Random(5));
        Assert.IsTrue(NinoPrefetch.Scattered(ref scattered[0]), "shuffled");
        object[] reversed = ordered.Reverse().ToArray();
        Assert.IsTrue(NinoPrefetch.Scattered(ref reversed[0]), "reversed");
        scattered[3] = null;
        Assert.IsFalse(NinoPrefetch.Scattered(ref scattered[0]), "a null slot");
    }

    [TestMethod]
    public void LongArraysAndListsRoundTrip()
    {
        foreach (int count in new[] { 0, 1, 16, 17, 1023, 1024, 5000, Long, Long + 15 })
        {
            foreach (bool shuffled in new[] { true, false })
            {
                var items = Items(count, count, shuffled);
                if (count >= 1024)
                {
                    // What decides that the runs are taken (with the length, past the threshold for Long).
                    object[] first = items.Take(17).ToArray<object>();
                    Assert.AreEqual(shuffled, NinoPrefetch.Scattered(ref first[0]), $"{count}, shuffled: {shuffled}");
                }

                byte[] bytes = NinoSerializer.Serialize(items);
                CollectionAssert.AreEqual(OneByOne(items), bytes, $"PrefetchItem[{count}]");
                AreEqual(items, NinoDeserializer.Deserialize<PrefetchItem[]>(bytes));
                var list = items.ToList();
                byte[] listBytes = NinoSerializer.Serialize(list);
                CollectionAssert.AreEqual(bytes, listBytes, $"List<PrefetchItem> x {count}");
                AreEqual(items, NinoDeserializer.Deserialize<List<PrefetchItem>>(listBytes));
            }
        }
    }

    [TestMethod]
    public void LongArraysAreReadIntoTheirObjects()
    {
        int count = Long;
        var items = Items(count, 1);
        byte[] bytes = NinoSerializer.Serialize(items);
        var target = Items(count, 2);
        var before = (PrefetchItem[])target.Clone();
        NinoDeserializer.Deserialize(bytes, ref target);
        AreEqual(items, target);
        int reused = 0;
        for (int i = 0; i < count; i++)
        {
            if (before[i] != null && items[i] != null)
            {
                Assert.AreSame(before[i], target[i], $"[{i}]");
                reused++;
            }
        }

        Assert.IsTrue(reused > count / 2);
    }

    [TestMethod]
    public void LongPolymorphicArraysRoundTrip()
    {
        int count = NinoPrefetch.Of<NinoGen.Nino_UnitTests_PrefetchShapeFormatter>.Threshold + 21;
        var shapes = new PrefetchShape[count];
        for (int i = 0; i < count; i++)
            shapes[i] = (i % 11) switch
            {
                0 => null,
                < 6 => new PrefetchCircle { Tag = "c" + i, Radius = i },
                _ => new PrefetchSquare { Tag = i % 2 == 0 ? null : "s" + i, Side = -i },
            };
        Shuffle(shapes, new Random(3));
        var back = NinoDeserializer.Deserialize<PrefetchShape[]>(NinoSerializer.Serialize(shapes));
        for (int i = 0; i < count; i++)
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
    public void TheCacheIsTheLastLevel()
    {
        long bytes = NinoPrefetch.CacheBytes;
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
    public void OnlyNonGenericClassesOfVaryingSizePrefetch()
    {
        const string source = """
            using System.Collections.Generic;
            using Nino.Core;
            namespace Pf {
            [NinoType] public class Item { public int Id; public string Name; public List<int> Values { get; set; } public long Value; }
            [NinoType] public class Computed { public string Name { get => _name; set => _name = value; } private string _name; public List<int> Other; }
            [NinoType] public class Plain { public int A; public List<int> B; }
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
        StringAssert.Contains(item,
            "sum += global::Nino.Core.CompilerServices.NinoPrefetched.At(v.Name) + global::Nino.Core.CompilerServices.NinoPrefetched.At(v.Values);");
        string computed = Output("Computed");
        StringAssert.Contains(computed, "sum += global::Nino.Core.CompilerServices.NinoPrefetched.At(v.Other);");
        Assert.IsFalse(computed.Contains("v.Name"), "a property with a body of its own");

        foreach (string type in new[] { "Fixed", "Value", "Box_1" })
            Assert.IsFalse(Output(type).Contains("INinoPrefetchFormatter"), type);
    }
}
