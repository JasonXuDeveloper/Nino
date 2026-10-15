using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;

#nullable disable
namespace Nino.Benchmark;

/// <summary>
/// Quick correctness check (`dotnet run -c Release -- --verify`). Uses SimpleTest's own static data and its own
/// benchmark methods (via reflection, so SimpleTest.cs stays a 1:1 port of v4's), and checks that
///  - each Nino*Serialize* method writes exactly the bytes NinoGen.Serializer.Serialize(x) returns;
///  - each *Deserialize* method (Nino, MemoryPack, MessagePack) returns a value deep-equal to the source data;
/// then prints the payload sizes.
/// </summary>
public static class Verify
{
    private static int _failures;

    public static int Run()
    {
        const BindingFlags S = BindingFlags.Static | BindingFlags.NonPublic;
        const BindingFlags I = BindingFlags.Instance | BindingFlags.NonPublic;
        var t = typeof(SimpleTest);
        T Get<T>(string name) => (T)t.GetField(name, S)!.GetValue(null);

        var simpleClass = Get<SimpleClass>("SimpleClass");
        var simpleClasses = Get<SimpleClass[]>("SimpleClasses");
        var simpleStruct = Get<SimpleStruct>("SimpleStruct");
        var simpleStructs = Get<SimpleStruct[]>("SimpleStructs");
        var vectors = Get<Vector4[]>("Vectors");

        var bench = new SimpleTest();
        bench.Setup();
        var ninoBuffer = (ArrayBufferWriter<byte>)t.GetField("_ninoBuffer", I)!.GetValue(bench);

        var cases = new (string Name, string Field, object Source, Func<object, object, bool> Eq)[]
        {
            ("SimpleClass", "SerializedSimpleClass", simpleClass, (a, b) => Eq((SimpleClass)a, (SimpleClass)b)),
            ("SimpleStruct", "SerializedSimpleStruct", simpleStruct, (a, b) => Eq((SimpleStruct)a, (SimpleStruct)b)),
            ("SimpleClasses", "SerializedSimpleClasses", simpleClasses,
                (a, b) => SeqEq((SimpleClass[])a, (SimpleClass[])b, Eq)),
            ("SimpleStructs", "SerializedSimpleStructs", simpleStructs,
                (a, b) => SeqEq((SimpleStruct[])a, (SimpleStruct[])b, Eq)),
            ("Vectors", "SerializedVectors", vectors,
                (a, b) => ((Vector4[])a).AsSpan().SequenceEqual((Vector4[])b)),
        };

        Console.WriteLine("Nino 3.3.13 (NuGet) round-trip check on SimpleTest's data");
        Console.WriteLine();
        Console.WriteLine($"{"Case",-14} {"Nino v3",10} {"MemoryPack",11} {"MessagePack",12}");
        foreach (var c in cases)
        {
            var payloads = Get<byte[][]>(c.Field); // [0]=MessagePack, [1]=MemoryPack, [2]=Nino

            // 1) the benchmarked serialize method writes the same bytes as the byte[] API
            var written = (int)t.GetMethod("NinoSerialize" + c.Name)!.Invoke(bench, null)!;
            Check($"NinoSerialize{c.Name}: buffer writer bytes == byte[] API bytes",
                written == payloads[2].Length && ninoBuffer.WrittenSpan.SequenceEqual(payloads[2]));
            // second call on the reused buffer must give the same bytes again
            t.GetMethod("NinoSerialize" + c.Name)!.Invoke(bench, null);
            Check($"NinoSerialize{c.Name}: reused buffer gives identical bytes",
                ninoBuffer.WrittenSpan.SequenceEqual(payloads[2]));

            // 2) every deserialize benchmark returns a value equal to the source
            foreach (var lib in new[] { "Nino", "MemoryPack", "MessagePack" })
            {
                var result = t.GetMethod(lib + "Deserialize" + c.Name)!.Invoke(bench, null);
                Check($"{lib}Deserialize{c.Name}: round-trip equal", c.Eq(c.Source, result));
            }

            Console.WriteLine($"{c.Name,-14} {payloads[2].Length,10} {payloads[1].Length,11} {payloads[0].Length,12}");
        }

        Console.WriteLine();
        Console.WriteLine(_failures == 0 ? "VERIFY OK" : $"VERIFY FAILED ({_failures} failures)");
        return _failures == 0 ? 0 : 1;
    }

    private static void Check(string what, bool ok)
    {
        if (ok) return;
        _failures++;
        Console.WriteLine("FAIL: " + what);
    }

    private static bool SeqEq<T>(T[] a, T[] b, Func<T, T, bool> eq) =>
        a != null && b != null && a.Length == b.Length && a.Zip(b).All(p => eq(p.First, p.Second));

    private static bool Eq(SimpleStruct a, SimpleStruct b) =>
        a.Id == b.Id && a.CreateTime == b.CreateTime;

    private static bool Eq(SimpleClass a, SimpleClass b)
    {
        if (a is null || b is null) return ReferenceEquals(a, b);
        return a.Id == b.Id && a.Tag == b.Tag && a.Guid == b.Guid && a.CreateTime == b.CreateTime
               && a.Name == b.Name && a.Desc == b.Desc
               && a.Numbers.AsSpan().SequenceEqual(b.Numbers)
               && a.Dates.SequenceEqual(b.Dates)
               && DictEq(a.Map1, b.Map1) && DictEq(a.Map2, b.Map2);
    }

    private static bool DictEq<TK, TV>(Dictionary<TK, TV> a, Dictionary<TK, TV> b) =>
        a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var v) && EqualityComparer<TV>.Default.Equals(kv.Value, v));
}
