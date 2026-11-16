using System;
using System.Linq;
using System.Numerics;
using BenchmarkDotNet.Configs;
#if !NINO_BENCH_V3
using P = Nino.Benchmark.Proto;
#endif

#nullable disable
namespace Nino.Benchmark;

/// <summary>
/// Plain classes, structs and vectors, alone and in arrays, against MemoryPack, MessagePack, protobuf-net,
/// Google.Protobuf (the messages protobuf-net's contracts generate, cases.proto, parsed from protobuf-net's bytes outside
/// the timed region) and System.Text.Json (source generated, UTF-8). The timed methods are SimpleTest.Cases.g.cs,
/// generated from the cases below and the adapters (Adapters/, runtimes/csharp/tools/BenchGen); the v3 build times
/// Nino 3.3.13 and MemoryPack on the same values.
/// </summary>
[BenchCase("SimpleClass", Proto = "SimpleClassBase", Json = "SimpleClass")]
[BenchCase("SimpleStruct", Proto = "SimpleStruct", Json = "SimpleStruct")]
[BenchCase("SimpleClasses", Proto = "SimpleClassList", Json = "SimpleClassArray")]
[BenchCase("SimpleStructs", Proto = "SimpleStructList", Json = "SimpleStructArray")]
[BenchCase("Vectors", Proto = "VectorList", Json = "Vector4Array")]
[PayloadColumn]
[HideColumns("StdDev", "RatioSD", "Error")]
[MinColumn, MaxColumn]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[NinoJob]
[GcServer(true)]
[MemoryDiagnoser]
[MarkdownExporterAttribute.GitHub]
public partial class SimpleTest
#if !NINO_BENCH_V3
    : IReadBack
#endif
{
    // The values, made before the generated static constructor makes their bytes, in this order.
#if !NINO_BENCH_V3
    private static readonly bool ProtobufReady = ProtobufModel.Ready; // Vector4's fields
#endif
    private static readonly SimpleClass SimpleClass = SimpleClass.Create();
    private static readonly SimpleClass[] SimpleClasses = Enumerable.Range(0, 100).Select(_ => SimpleClass.Create()).ToArray();
    private static readonly SimpleStruct SimpleStruct = SimpleStruct.Create();
    private static readonly SimpleStruct[] SimpleStructs = Enumerable.Range(0, 100).Select(_ => SimpleStruct.Create()).ToArray();
    private static readonly Vector4[] Vectors = CreateVectors();

    private static Vector4[] CreateVectors()
    {
        var r = BenchmarkRandom.Shared;
        return Enumerable.Range(0, 10000)
            .Select(_ => new Vector4(r.NextSingle(), r.NextSingle(),
                r.NextSingle(), r.NextSingle())).ToArray();
    }

#if !NINO_BENCH_V3
    partial void OnSetup() => ProtoFile.Cases.Check();

    /// <summary>Verify.cs: a Google.Protobuf message read back through protobuf-net as the case's model.</summary>
    public object ReadBack(object result, Type model) => ProtobufModel.ReadBack(result, model);
#endif
}
