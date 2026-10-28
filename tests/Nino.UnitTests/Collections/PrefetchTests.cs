using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nino.Core;

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
/// Arrays and lists of objects long enough for the loops that prefetch them (INinoPrefetchFormatter: at most 65,536
/// elements, x64 and Arm64 on .NET 8+): the same values back, null slots kept, existing objects reused; and which
/// formatters the generator gives the prefetching loops.
/// </summary>
[TestClass]
public class PrefetchTests
{
    // Past the largest threshold (PrefetchRules.Max), and not a multiple of the prefetch distance.
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
        StringAssert.Contains(item, "INinoPrefetchFormatter.PrefetchThreshold =>");
        StringAssert.Contains(item, "NinoPrefetched.At(v.Name) + global::Nino.Core.CompilerServices.NinoPrefetched.At(v.Values)");
        string computed = Output("Computed");
        StringAssert.Contains(computed, "NinoPrefetched.At(v.Other);");
        Assert.IsFalse(computed.Contains("v.Name"), "a property with a body of its own");

        foreach (string type in new[] { "Fixed", "Value", "Box_1" })
            Assert.IsFalse(Output(type).Contains("INinoPrefetchFormatter"), type);
    }
}
