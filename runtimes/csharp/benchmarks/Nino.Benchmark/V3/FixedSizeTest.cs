using System;
using System.Linq;
using BenchmarkDotNet.Configs;
using MemoryPack;

#nullable disable
namespace Nino.Benchmark;

// Cases/Collections/FixedSizeTest.cs ported to Nino 3.3.13 (NuGet), for the v3 build of this project
// (NINO_BENCH_V3=true): the same types (Fixtures/FixedSizeBenchmarkData.cs), data, method names and job, Nino's and
// MemoryPack's cases. Only the Nino calls differ, as in V3/SimpleTest.cs. v3 has no GetFixedSize, so the check that
// both types take v4's fixed-size path is left out: v3 writes them through its regular path.

/// <summary>
/// FixedSizeTest's types (unmanaged members only: v4 writes them through its fixed-size path) serialized and
/// deserialized by Nino v3 through its <c>byte[]</c> API, plus an array of 100 of the classes.
/// </summary>
[PayloadColumn]
[HideColumns("StdDev", "RatioSD", "Error")]
[MinColumn, MaxColumn]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[NinoJob]
[GcServer(true)]
[MemoryDiagnoser]
[MarkdownExporterAttribute.GitHub]
public class FixedSizeTest
{
    private static readonly FixedStruct FixedStruct = FixedStruct.Create(new Random(4));
    private static readonly FixedClass FixedClass = FixedClass.Create(new Random(5));
    private static readonly FixedClass[] FixedClasses =
        Enumerable.Range(0, 100).Select(i => FixedClass.Create(new Random(6 + i))).ToArray();

    private static readonly byte[] NinoFixedStruct = NinoGen.Serializer.Serialize(FixedStruct);
    private static readonly byte[] MemoryPackFixedStruct = MemoryPackSerializer.Serialize(FixedStruct);
    private static readonly byte[] NinoFixedClass = NinoGen.Serializer.Serialize(FixedClass);
    private static readonly byte[] MemoryPackFixedClass = MemoryPackSerializer.Serialize(FixedClass);
    private static readonly byte[] NinoFixedClasses = NinoGen.Serializer.Serialize(FixedClasses);
    private static readonly byte[] MemoryPackFixedClasses = MemoryPackSerializer.Serialize(FixedClasses);

    static FixedSizeTest()
    {
        BenchmarkPayloadRegistry.Register(nameof(NinoSerializeFixedStruct), NinoFixedStruct.Length);
        BenchmarkPayloadRegistry.Register(nameof(MemoryPackSerializeFixedStruct), MemoryPackFixedStruct.Length);
        BenchmarkPayloadRegistry.Register(nameof(NinoSerializeFixedClass), NinoFixedClass.Length);
        BenchmarkPayloadRegistry.Register(nameof(MemoryPackSerializeFixedClass), MemoryPackFixedClass.Length);
        BenchmarkPayloadRegistry.Register(nameof(NinoSerializeFixedClasses), NinoFixedClasses.Length);
        BenchmarkPayloadRegistry.Register(nameof(MemoryPackSerializeFixedClasses), MemoryPackFixedClasses.Length);
    }

    [Benchmark(Baseline = true), BenchmarkCategory("FixedStructSerialize")]
    public byte[] NinoSerializeFixedStruct() => NinoGen.Serializer.Serialize(FixedStruct);

    [Benchmark, BenchmarkCategory("FixedStructSerialize")]
    public byte[] MemoryPackSerializeFixedStruct() => MemoryPackSerializer.Serialize(FixedStruct);

    [Benchmark(Baseline = true), BenchmarkCategory("FixedClassSerialize")]
    public byte[] NinoSerializeFixedClass() => NinoGen.Serializer.Serialize(FixedClass);

    [Benchmark, BenchmarkCategory("FixedClassSerialize")]
    public byte[] MemoryPackSerializeFixedClass() => MemoryPackSerializer.Serialize(FixedClass);

    [Benchmark(Baseline = true), BenchmarkCategory("FixedClassesSerialize")]
    public byte[] NinoSerializeFixedClasses() => NinoGen.Serializer.Serialize(FixedClasses);

    [Benchmark, BenchmarkCategory("FixedClassesSerialize")]
    public byte[] MemoryPackSerializeFixedClasses() => MemoryPackSerializer.Serialize(FixedClasses);

    [Benchmark(Baseline = true), BenchmarkCategory("FixedStructDeserialize")]
    public FixedStruct NinoDeserializeFixedStruct()
    {
        NinoGen.Deserializer.Deserialize(NinoFixedStruct, out FixedStruct ret);
        return ret;
    }

    [Benchmark, BenchmarkCategory("FixedStructDeserialize")]
    public FixedStruct MemoryPackDeserializeFixedStruct() =>
        MemoryPackSerializer.Deserialize<FixedStruct>(MemoryPackFixedStruct);

    [Benchmark(Baseline = true), BenchmarkCategory("FixedClassDeserialize")]
    public FixedClass NinoDeserializeFixedClass()
    {
        NinoGen.Deserializer.Deserialize(NinoFixedClass, out FixedClass ret);
        return ret;
    }

    [Benchmark, BenchmarkCategory("FixedClassDeserialize")]
    public FixedClass MemoryPackDeserializeFixedClass() =>
        MemoryPackSerializer.Deserialize<FixedClass>(MemoryPackFixedClass);

    [Benchmark(Baseline = true), BenchmarkCategory("FixedClassesDeserialize")]
    public FixedClass[] NinoDeserializeFixedClasses()
    {
        NinoGen.Deserializer.Deserialize(NinoFixedClasses, out FixedClass[] ret);
        return ret;
    }

    [Benchmark, BenchmarkCategory("FixedClassesDeserialize")]
    public FixedClass[] MemoryPackDeserializeFixedClasses() =>
        MemoryPackSerializer.Deserialize<FixedClass[]>(MemoryPackFixedClasses);
}

