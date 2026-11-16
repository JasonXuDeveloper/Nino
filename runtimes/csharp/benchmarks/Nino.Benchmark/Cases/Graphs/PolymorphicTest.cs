using System;
using System.Linq;
using BenchmarkDotNet.Configs;
#if !NINO_BENCH_V3
using P = Nino.Benchmark.Proto;
#endif

namespace Nino.Benchmark;

#nullable disable

/// <summary>
/// Polymorphic data written through its base type, against MemoryPack's and MessagePack's unions, protobuf-net's
/// <c>[ProtoInclude]</c> (a nested message per level of the hierarchy), Google.Protobuf's messages of the same schema
/// (a <c>oneof</c> per level, cases.proto) and System.Text.Json's <c>[JsonDerivedType]</c> (a <c>"$type"</c> property):
/// one class holding 1,000 elements of 20 subtypes (one to nine levels below their base), an array of 1,000 such
/// elements, and an array of 100 classes holding 50 each. The timed methods are PolymorphicTest.Cases.g.cs, generated
/// from the cases below and the adapters (Adapters/, runtimes/csharp/tools/BenchGen); the v3 build times Nino 3.3.13
/// and MemoryPack on the same values.
/// </summary>
[BenchClass(Buffer = "1024 * 1024")]
[BenchCase("PolymorphicClass", Value = "PolymorphicInstance", Proto = "PolymorphicClassBase", Json = "PolymorphicClassBase")]
[BenchCase("PolymorphicFooArray", Proto = "PolymorphicFooList", Json = "PolymorphicFooBaseArray")]
[BenchCase("PolymorphicClassArray", Quick = true, Proto = "PolymorphicClassList", Json = "PolymorphicClassBaseArray")]
[PayloadColumn]
[HideColumns("StdDev", "RatioSD", "Error")]
[MinColumn, MaxColumn]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[NinoJob]
[GcServer(true)]
[MemoryDiagnoser]
[MarkdownExporterAttribute.GitHub]
public partial class PolymorphicTest
#if !NINO_BENCH_V3
    : IReadBack
#endif
{
    // The values, made before the generated static constructor makes their bytes, in this order.
    private static readonly PolymorphicClassBase PolymorphicInstance = CreateInstance();
    private static readonly PolymorphicFooBase[] PolymorphicFooArray = CreateFooArray();
    private static readonly PolymorphicClassBase[] PolymorphicClassArray = CreateClassArray();

    /// <summary>One polymorphic class holding 1,000 polymorphic elements.</summary>
    private static PolymorphicClassBase CreateInstance()
    {
        var instance = PolymorphicClass.Create();
        var polyClass = (PolymorphicClass)instance;
        for (int i = 0; i < 1000; i++)
            polyClass.Numbers2.Add(Foo(i));
        return instance;
    }

    /// <summary>
    /// The key polymorphism case: an array of 1,000 elements of the base type, its 20 subtypes in turn, one to nine
    /// levels deep.
    /// </summary>
    private static PolymorphicFooBase[] CreateFooArray()
    {
        var array = new PolymorphicFooBase[1000];
        for (int i = 0; i < 1000; i++)
            array[i] = Foo(i);
        return array;
    }

    /// <summary>100 polymorphic classes (one subtype, through the base type), each holding 50 polymorphic elements.</summary>
    private static PolymorphicClassBase[] CreateClassArray() => Enumerable.Range(0, 100).Select(_ =>
    {
        var instance = (PolymorphicClassBase)PolymorphicClass.Create();
        var pClass = (PolymorphicClass)instance;
        for (int i = 0; i < 50; i++)
            pClass.Numbers2.Add(Foo(i));
        return instance;
    }).ToArray();

    /// <summary>The <paramref name="i"/>th of the 20 subtypes in turn (FooA is one level below the base, FooI to FooZ nine).</summary>
    private static PolymorphicFooBase Foo(int i) => (i % 20) switch
    {
        0 => FooA.Create(),
        1 => FooB.Create(),
        2 => FooC.Create(),
        3 => FooD.Create(),
        4 => FooE.Create(),
        5 => FooF.Create(),
        6 => FooG.Create(),
        7 => FooH.Create(),
        8 => FooI.Create(),
        9 => FooJ.Create(),
        10 => FooK.Create(),
        11 => FooL.Create(),
        12 => FooM.Create(),
        13 => FooN.Create(),
        14 => FooO.Create(),
        15 => FooP.Create(),
        16 => FooQ.Create(),
        17 => FooR.Create(),
        18 => FooS.Create(),
        _ => FooZ.Create(),
    };

#if !NINO_BENCH_V3
    partial void OnSetup() => ProtoFile.Cases.Check();

    /// <summary>Verify.cs: a Google.Protobuf message read back through protobuf-net as the case's model.</summary>
    public object ReadBack(object result, Type model) => ProtobufModel.ReadBack(result, model);
#endif
}
