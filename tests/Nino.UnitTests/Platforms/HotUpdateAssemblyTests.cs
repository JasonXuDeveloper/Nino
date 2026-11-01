using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nino.Core;

namespace Nino.UnitTests;

/// <summary>
/// Hot-update runtimes (HybridCLR) load assemblies with <c>Assembly.Load(bytes)</c> and run neither module
/// initializers nor Unity's load hooks. Registration must then happen on its own, from the
/// <c>[assembly: NinoGenerated]</c> attribute, when Nino first meets a type of the assembly or an unknown type id.
/// </summary>
[TestClass]
public class HotUpdateAssemblyTests
{
    private const string Source = @"
using System.Collections.Generic;
using Nino.Core;
namespace Hot
{
    [NinoType] public class Item { public int X; public List<Part> Parts; }
    [NinoType] public class Part { public string S; }
    [NinoType] public class Shape { public int Id; }
    [NinoType] public class Circle : Shape { public float R; }

    public static class Api
    {
        public static byte[] SerializeCircle() => NinoSerializer.Serialize<object>(new Circle { Id = 1, R = 2 });
        public static string RoundTripItem()
        {
            var bytes = NinoSerializer.Serialize(new Item { X = 3, Parts = new List<Part> { new Part { S = ""p"" } } });
            var item = NinoDeserializer.Deserialize<Item>(bytes);
            return item.X + item.Parts[0].S;
        }
    }
}";

    /// <param name="slowRegistry">
    /// Registers the generated registry from the static constructor of another one, which first sleeps: concurrent
    /// lookups then find the assembly while its registration is still running.
    /// </param>
    private static Assembly CompileWithoutInitializers(bool slowRegistry = false, string source = Source)
    {
        string name = "Hot" + Guid.NewGuid().ToString("N");
        var sources = new[] { source };
        if (slowRegistry)
        {
            // Its own namespace: type ids are names, and the other test loads the same types.
            sources = new[]
            {
                Source.Replace("namespace Hot", "namespace HotSlow"), $@"
[assembly: Nino.Core.CompilerServices.NinoGenerated(typeof(HotSlow.SlowRegistry))]
namespace HotSlow
{{
    static class SlowRegistry
    {{
        static SlowRegistry()
        {{
            System.Threading.Thread.Sleep(300);
            NinoGen.NinoGenRegistry_{name}.Init();
        }}
    }}
}}"
            };
        }

        var compilation = TestCompilation.Create(name,
            sources.Select(source => CSharpSyntaxTree.ParseText(source, TestCompilation.Parse)),
            extraReferences: new[] { MetadataReference.CreateFromFile(typeof(HotUpdateAssemblyTests).Assembly.Location) });

        var run = TestCompilation.Generate(compilation);
        // What a hot-update runtime skips: drop the entry points that would register eagerly.
        var generated = run.GeneratedTrees.Select(t =>
        {
            var text = t.GetText().ToString()
                .Replace("[global::System.Runtime.CompilerServices.ModuleInitializer]", "");
            if (slowRegistry)
                text = text.Replace("[assembly: global::Nino.Core.CompilerServices.NinoGenerated(", "// [assembly: global::Nino.Core.CompilerServices.NinoGenerated(");
            return CSharpSyntaxTree.ParseText(text, TestCompilation.Parse, t.FilePath);
        });
        var output = compilation.AddSyntaxTrees(generated);

        var assembly = TestCompilation.EmitAndLoad(output);
        StringAssert.Contains(string.Concat(run.GeneratedTrees.Select(t => t.ToString())), "[assembly: global::Nino.Core.CompilerServices.NinoGenerated(");
        return assembly;
    }

    [TestMethod]
    public void LoadedAssemblyRegistersWithoutInitializers()
    {
        var api = CompileWithoutInitializers().GetType("Hot.Api")!;

        // An object of the loaded assembly read through a type Nino.Core knows: found by its unknown type id.
        var circle = NinoDeserializer.Deserialize<object>((byte[])api.GetMethod("SerializeCircle")!.Invoke(null, null)!);
        Assert.AreEqual("Hot.Circle", circle.GetType().FullName);
        Assert.AreEqual(2f, circle.GetType().GetField("R")!.GetValue(circle));

        // Types first met by the generated code of the loaded assembly itself.
        Assert.AreEqual("3p", api.GetMethod("RoundTripItem")!.Invoke(null, null));
    }

    [TestMethod]
    public void ConcurrentFirstReadsWaitForTheRegistration()
    {
        var assembly = CompileWithoutInitializers(slowRegistry: true);
        // A circle written without touching the assembly: type id, then Id and R.
        var bytes = new byte[12];
        BitConverter.GetBytes(NinoTypeId.Compute("HotSlow.Circle")).CopyTo(bytes, 0);
        BitConverter.GetBytes(1).CopyTo(bytes, 4);
        BitConverter.GetBytes(2f).CopyTo(bytes, 8);

        // Every thread meets the unknown id while the first one to scan is still running the registry: none of them
        // may give up before it has finished.
        const int threads = 4;
        using var start = new System.Threading.Barrier(threads);
        var reads = Enumerable.Range(0, threads).Select(_ => System.Threading.Tasks.Task.Factory.StartNew(() =>
        {
            start.SignalAndWait();
            return NinoDeserializer.Deserialize<object>(bytes);
        }, System.Threading.Tasks.TaskCreationOptions.LongRunning)).ToArray();
        System.Threading.Tasks.Task.WaitAll(reads);

        var circle = assembly.GetType("HotSlow.Circle")!;
        foreach (var read in reads)
        {
            Assert.AreEqual(circle, read.Result.GetType());
            Assert.AreEqual(2f, circle.GetField("R")!.GetValue(read.Result));
        }
    }

    private const string SubtypeSource = @"
using Nino.Core;
namespace HotSub
{
    public sealed class Freeze : Nino.UnitTests.HotEffect { public int Turns; }
    public static class Api
    {
        public static Nino.UnitTests.HotEffect Create() => new Freeze { Duration = 1.5f, Turns = 2 };
    }
}";

    [TestMethod]
    public void LoadedSubtypeOfAnAotBaseIsReadByAotCode()
    {
        var api = CompileWithoutInitializers(source: SubtypeSource).GetType("HotSub.Api")!;
        var freeze = (HotEffect)api.GetMethod("Create")!.Invoke(null, null)!;

        // Written and read by code of this (AOT) assembly, which only knows the base class.
        var effects = new System.Collections.Generic.List<HotEffect> { new HotDamage { Duration = 1, Amount = 40 }, freeze };
        var copy = NinoDeserializer.Deserialize<System.Collections.Generic.List<HotEffect>>(NinoSerializer.Serialize(effects));

        Assert.AreEqual(40, ((HotDamage)copy[0]).Amount);
        Assert.AreEqual("HotSub.Freeze", copy[1].GetType().FullName);
        Assert.AreEqual(1.5f, copy[1].Duration);
        Assert.AreEqual(2, copy[1].GetType().GetField("Turns")!.GetValue(copy[1]));
    }

    private const string GenericSource = @"
using System.Collections.Generic;
using Nino.Core;
namespace HotGeneric
{
    [NinoType] public class Item { public int X; }
    [NinoType] public class Bag<T> { public T Value; public List<T> Values; }
    [NinoType] public class Leaf : Nino.UnitTests.HotGenericBase<Item> { public int Y; }
    [NinoType] public class Twin<T> : Nino.UnitTests.HotGenericBase<T> { public T Z; }

    public static class Api
    {
        // An AOT generic over a loaded type, a loaded generic over an AOT one and over a loaded one, and a loaded
        // subtype of an AOT generic base: their formatters take formatters of the other assembly.
        public static string RoundTrip()
        {
            var a = NinoDeserializer.Deserialize<Nino.UnitTests.Box<Item>>(NinoSerializer.Serialize(new Nino.UnitTests.Box<Item> { Item = new Item { X = 1 } }));
            var b = NinoDeserializer.Deserialize<Bag<Nino.UnitTests.Box<int>>>(NinoSerializer.Serialize(new Bag<Nino.UnitTests.Box<int>> { Value = new Nino.UnitTests.Box<int> { Item = 2 } }));
            var c = NinoDeserializer.Deserialize<Bag<Item>>(NinoSerializer.Serialize(new Bag<Item> { Value = new Item { X = 3 }, Values = new List<Item> { new Item { X = 4 } } }));
            Nino.UnitTests.HotGenericBase<Item> leaf = new Leaf { B = new Item { X = 5 }, Y = 6 };
            var d = (Leaf)NinoDeserializer.Deserialize<Nino.UnitTests.HotGenericBase<Item>>(NinoSerializer.Serialize(leaf));
            return """" + a.Item.X + b.Value.Item + c.Value.X + c.Values[0].X + d.B.X + d.Y;
        }

        public static object Box() => new Nino.UnitTests.Box<Item> { Item = new Item { X = 7 } };
        public static object Bag() => new Bag<int> { Value = 8 };
        public static Nino.UnitTests.HotGenericBase<int> Twin() => new Twin<int> { B = 9, Z = 10 };
    }
}";

    [TestMethod]
    public void LoadedGenericsAcrossTheAotBoundary()
    {
        var api = CompileWithoutInitializers(source: GenericSource).GetType("HotGeneric.Api")!;
        Assert.AreEqual("123456", api.GetMethod("RoundTrip")!.Invoke(null, null));

        // Closed generics of the loaded assembly read by AOT code through object: found by their type ids.
        foreach (string name in new[] { "Box", "Bag" })
        {
            var value = api.GetMethod(name)!.Invoke(null, null)!;
            Assert.AreEqual(value.GetType(), NinoDeserializer.Deserialize<object>(NinoSerializer.Serialize<object>(value)).GetType(), name);
        }

        // A loaded generic subtype next to an AOT one, in a list AOT code writes and reads.
        var twin = (HotGenericBase<int>)api.GetMethod("Twin")!.Invoke(null, null)!;
        var copy = NinoDeserializer.Deserialize<System.Collections.Generic.List<HotGenericBase<int>>>(
            NinoSerializer.Serialize(new System.Collections.Generic.List<HotGenericBase<int>> { twin, new HotGenericLeaf { B = 11 } }));
        Assert.AreEqual(9, copy[0].B);
        Assert.AreEqual(10, copy[0].GetType().GetField("Z")!.GetValue(copy[0]));
        Assert.AreEqual(11, copy[1].B);
    }
}

[NinoType]
public abstract class HotGenericBase<T> { public T B; }

[NinoType]
public class HotGenericLeaf : HotGenericBase<int> { }

[NinoType]
public abstract class HotEffect { public float Duration; }

public sealed class HotDamage : HotEffect { public int Amount; }
