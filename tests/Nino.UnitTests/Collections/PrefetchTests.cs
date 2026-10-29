using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
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
public class PrefetchHolder
{
    public PrefetchItem[] Items;
    public List<PrefetchItem> List;
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
/// Long top-level arrays and lists of objects not in allocation order are written, and read into their existing
/// objects, in runs that prefetch (NinoPrefetch; x64 and Arm64 on .NET 8+, through the interceptors of the calls here):
/// the lengths here are past this machine's threshold, so the runs are taken whatever its cache, and must give the
/// bytes and objects of the ordinary loop, null slots and partial last runs included. Also: the allocation-order
/// sample, the cache size the threshold is scaled to, and which formatters and calls the generator makes prefetch
/// (this assembly is built with NinoPrefetchLargeCollections; without it, nothing changes).
/// </summary>
[TestClass]
public class PrefetchTests
{
    /// <summary>
    /// Whether PrefetchItem's collections prefetch: x64 or Arm64 on .NET 8+, and generated with the property (not
    /// with -p:NinoRegularCalls=true, which leaves out the generator's build props).
    /// </summary>
    private static bool Prefetches =>
        NinoPrefetch.Of<NinoGen.Nino_UnitTests_PrefetchItemFormatter>.Threshold != int.MaxValue;

    /// <summary>Past the threshold of PrefetchItem's collections, and not a multiple of a run.</summary>
    private static int Long => Threshold + 37;

    private static int Threshold => Prefetches
        ? NinoPrefetch.Of<NinoGen.Nino_UnitTests_PrefetchItemFormatter>.Threshold
        : 2000;

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

                Assert.AreEqual(Prefetches && shuffled && count > Threshold,
                    NinoPrefetch.Takes<PrefetchItem, NinoGen.Nino_UnitTests_PrefetchItemFormatter>(items),
                    $"{count}, shuffled: {shuffled}");

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

        // A list of as many: its objects reused too; of another count, read as it is otherwise.
        var list = Items(count, 3).ToList();
        var objects = list.ToArray();
        NinoDeserializer.Deserialize(bytes, ref list);
        AreEqual(items, list);
        Assert.IsTrue(Enumerable.Range(0, count).Count(i => objects[i] != null && ReferenceEquals(objects[i], list[i])) >
                      count / 2);
        var shorter = Items(count - 1, 4).ToList();
        NinoDeserializer.Deserialize(bytes, ref shorter);
        AreEqual(items, shorter);
        var longer = Items(count + 1, 5);
        NinoDeserializer.Deserialize(bytes, ref longer);
        AreEqual(items, longer);
    }

    [TestMethod]
    public void NestedCollectionsAreWrittenAsTopLevelOnes()
    {
        // A member holding a long collection: the ordinary loop (only top-level calls prefetch), the same bytes.
        var items = Items(Long, 6);
        var holder = new PrefetchHolder { Items = items, List = items.ToList() };
        byte[] bytes = NinoSerializer.Serialize(holder);
        byte[] top = NinoSerializer.Serialize(items);
        CollectionAssert.AreEqual(top, bytes.Skip(bytes.Length - 2 * top.Length).Take(top.Length).ToArray());
        CollectionAssert.AreEqual(top, bytes.Skip(bytes.Length - top.Length).ToArray());
        var back = NinoDeserializer.Deserialize<PrefetchHolder>(bytes);
        AreEqual(items, back.Items);
        AreEqual(items, back.List);
    }

    [TestMethod]
    public void LongPolymorphicArraysRoundTrip()
    {
        int count = (Prefetches ? NinoPrefetch.Of<NinoGen.Nino_UnitTests_PrefetchShapeFormatter>.Threshold : 2000) + 21;
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
    public void TopLevelCallsOfArraysAndListsPrefetch()
    {
        // The interceptors of this assembly's calls (the Roslyn 4.8+ generator): writes of arrays and lists of a class
        // that prefetches, and reads into them, go through NinoDirect's ...Prefetched methods; reads of new ones and
        // calls with other elements do not.
        var assembly = typeof(PrefetchTests).Assembly;
        var interceptors = assembly.GetTypes().FirstOrDefault(t => t.Name.EndsWith("NinoInterceptors"));
        if (interceptors == null)
            return; // built with -p:NinoRegularCalls=true
        var called = new HashSet<(string, Type)>();
        foreach (var method in interceptors.GetMethods(System.Reflection.BindingFlags.Public |
                                                       System.Reflection.BindingFlags.NonPublic |
                                                       System.Reflection.BindingFlags.Static))
        {
            var il = method.GetMethodBody()?.GetILAsByteArray();
            for (int i = 0; il != null && i + 4 < il.Length; i++)
            {
                // call <MethodSpec token>: a generic method's instantiation
                if (il[i] != 0x28 || il[i + 4] != 0x2B)
                    continue;
                try
                {
                    if (method.Module.ResolveMethod(BitConverter.ToInt32(il, i + 1)) is System.Reflection.MethodInfo
                        {
                            IsGenericMethod: true, DeclaringType.Name: "NinoDirect",
                        } target)
                        called.Add((target.Name, Value(target)));
                }
                catch (ArgumentException)
                {
                    // not an instruction boundary
                }
            }
        }

        string all = string.Join(", ", called.Select(c => c.Item1 + "(" + c.Item2.Name + ")"));
        Assert.IsTrue(called.Contains(("SerializePrefetched", typeof(PrefetchItem[]))), all);
        Assert.IsTrue(called.Contains(("SerializePrefetched", typeof(List<PrefetchItem>))), all);
        Assert.IsTrue(called.Contains(("DeserializePrefetched", typeof(PrefetchItem[]))), all);
        Assert.IsTrue(called.Contains(("DeserializePrefetched", typeof(List<PrefetchItem>))), all);
        Assert.IsTrue(called.Contains(("Deserialize", typeof(PrefetchItem[]))), all);
        Assert.IsFalse(called.Any(c => c.Item1.EndsWith("Prefetched") && c.Item2 == typeof(PrefetchHolder)), all);
    }

    /// <summary>The collection a NinoDirect method writes or reads: its value parameter, or what it returns.</summary>
    private static Type Value(System.Reflection.MethodInfo method)
    {
        var parameters = method.GetParameters();
        var type = method.Name.StartsWith("Serialize") ? parameters[0].ParameterType
            : method.ReturnType != typeof(void) ? method.ReturnType
            : parameters[parameters.Length - 1].ParameterType;
        return type.IsByRef ? type.GetElementType() : type;
    }

    /// <summary>The build properties the generator reads, as MSBuild hands them over (CompilerVisibleProperty).</summary>
    private sealed class BuildProperties : AnalyzerConfigOptionsProvider
    {
        private sealed class Options : AnalyzerConfigOptions
        {
            private readonly Dictionary<string, string> _values;
            public Options(Dictionary<string, string> values) => _values = values;
            public override bool TryGetValue(string key, out string value) => _values.TryGetValue(key, out value);
        }

        private static readonly AnalyzerConfigOptions None = new Options(new Dictionary<string, string>());
        public BuildProperties(Dictionary<string, string> values) => GlobalOptions = new Options(values);
        public override AnalyzerConfigOptions GlobalOptions { get; }
        public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => None;
        public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) => None;
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
        IEnumerable<GeneratedSourceResult> Run(string property)
        {
            var properties = new Dictionary<string, string>();
            if (property != null)
                properties["build_property.NinoPrefetchLargeCollections"] = property;
            GeneratorDriver driver = CSharpGeneratorDriver.Create(
                new[] { new Generator.Generation.NinoGenerator().AsSourceGenerator() },
                optionsProvider: new BuildProperties(properties));
            return driver.RunGenerators(compilation).GetRunResult().Results[0].GeneratedSources;
        }

        // Without the property (or with it false), no formatter prefetches: the generated code is that of before.
        foreach (string off in new[] { null, "false" })
            Assert.IsFalse(Run(off).Any(s => s.SourceText.ToString().Contains("Prefetch")), off ?? "unset");
        var sources = Run("true");
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
