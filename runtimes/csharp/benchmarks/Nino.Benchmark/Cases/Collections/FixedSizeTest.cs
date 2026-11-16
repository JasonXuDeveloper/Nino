using System;
using System.Linq;
using BenchmarkDotNet.Configs;
#if !NINO_BENCH_V3
using Nino.Core;
#endif

#nullable disable
namespace Nino.Benchmark;

/// <summary>
/// Types of fixed size (<see cref="NinoSerializer.GetFixedSize{T}"/> is not -1): <c>Serialize(value)</c> returns a new
/// <c>byte[]</c> allocated at exactly that size and writes the value straight into it, with no scratch buffer, writer
/// or copy. Measured through the <c>byte[]</c> APIs of both serializers (the allocation of the result is part of every
/// serialize call), plus an array of 100 of the classes, whose length varies and which takes the regular path.
/// MessagePack through its <c>byte[]</c> API too; protobuf-net has none, so it writes to a reused stream and copies
/// the result into a new array of its size.
/// The timed methods are FixedSizeTest.Cases.g.cs, generated from the cases below and the adapters (Adapters/,
/// runtimes/csharp/tools/BenchGen); the v3 build times Nino 3.3.13 (its regular path: it has no fixed-size one) and
/// MemoryPack on the same values.
/// </summary>
[BenchCase("FixedStruct")]
[BenchCase("FixedClass")]
[BenchCase("FixedClasses", Quick = true)]
[BenchClass(Buffer = "16 * 1024", Serialize = "array", Bytes = "{Library}{Case}Bytes")]
[PayloadColumn]
[HideColumns("StdDev", "RatioSD", "Error")]
[MinColumn, MaxColumn]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[NinoJob]
[GcServer(true)]
[MemoryDiagnoser]
[MarkdownExporterAttribute.GitHub]
public partial class FixedSizeTest
{
#if !NINO_BENCH_V3
    // First, so that the data below is serialized with it.
    private static readonly bool ProtobufReady = ProtobufModel.Ready;
#endif
    private static readonly FixedStruct FixedStruct = FixedStruct.Create(new Random(4));
    private static readonly FixedClass FixedClass = FixedClass.Create(new Random(5));
    private static readonly FixedClass[] FixedClasses =
        Enumerable.Range(0, 100).Select(i => FixedClass.Create(new Random(6 + i))).ToArray();
#if !NINO_BENCH_V3

    static partial void OnStaticInit()
    {
        // These cases measure the fixed-size path: stop if either type ever stops taking it.
        if (NinoSerializer.GetFixedSize<FixedStruct>() < 0 || NinoSerializer.GetFixedSize<FixedClass>() < 0)
            throw new InvalidOperationException("FixedStruct and FixedClass must be of fixed size");
    }
#endif
}
