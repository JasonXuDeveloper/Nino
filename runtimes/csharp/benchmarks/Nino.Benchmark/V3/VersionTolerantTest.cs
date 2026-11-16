using System.Buffers;
using System.Linq;
using BenchmarkDotNet.Configs;
using MemoryPack;

#nullable disable
namespace Nino.Benchmark;

// Cases/Scenarios/VersionTolerantTest.cs ported to Nino 3.3.13 (NuGet), for the v3 build of this project
// (NINO_BENCH_V3=true): the same type (Fixtures/TolerantClass.cs), data, method names and job, Nino's and MemoryPack's
// cases. v3's version tolerance is WEAK_VERSION_TOLERANCE, a symbol its generated code reads for a whole project: the
// type is built in V3/Versioned, which defines it, and the cases call the serializers v3 generates there
// (V3Versioned.NinoGen), as WorkloadTest's versioned workloads do. That project has no SimpleClass, so From (the v4
// type's factory) is copied here.

/// <summary>
/// <see cref="TolerantClass"/> and an array of 100 written by Nino v3 in its version-tolerant format into a reused
/// buffer writer and read back, against MemoryPack's (<c>GenerateType.VersionTolerant</c>), as
/// <see cref="SimpleTest"/> does with <see cref="SimpleClass"/>.
/// </summary>
[PayloadColumn]
[HideColumns("StdDev", "RatioSD", "Error")]
[MinColumn, MaxColumn]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[NinoJob]
[GcServer(true)]
[MemoryDiagnoser]
[MarkdownExporterAttribute.GitHub]
public class VersionTolerantTest
{
    private static readonly TolerantClass TolerantClass = From(SimpleClass.Create());
    private static readonly TolerantClass[] TolerantClasses =
        Enumerable.Range(0, 100).Select(_ => From(SimpleClass.Create())).ToArray();

    private static readonly byte[] NinoTolerantClass = V3Versioned.NinoGen.Serializer.Serialize(TolerantClass);
    private static readonly byte[] MemoryPackTolerantClass = MemoryPackSerializer.Serialize(TolerantClass);
    private static readonly byte[] NinoTolerantClasses = V3Versioned.NinoGen.Serializer.Serialize(TolerantClasses);
    private static readonly byte[] MemoryPackTolerantClasses = MemoryPackSerializer.Serialize(TolerantClasses);

    private ArrayBufferWriter<byte> _ninoBuffer;
    private ArrayBufferWriter<byte> _memoryPackBuffer;

    static VersionTolerantTest()
    {
        BenchmarkPayloadRegistry.Register(nameof(NinoSerializeTolerantClass), NinoTolerantClass.Length);
        BenchmarkPayloadRegistry.Register(nameof(MemoryPackSerializeTolerantClass), MemoryPackTolerantClass.Length);
        BenchmarkPayloadRegistry.Register(nameof(NinoSerializeTolerantClasses), NinoTolerantClasses.Length);
        BenchmarkPayloadRegistry.Register(nameof(MemoryPackSerializeTolerantClasses), MemoryPackTolerantClasses.Length);
    }

    private static TolerantClass From(SimpleClass c) => new()
    {
        Id = c.Id, Tag = c.Tag, Guid = c.Guid, CreateTime = c.CreateTime, Name = c.Name, Desc = c.Desc,
        Numbers = c.Numbers, Dates = c.Dates, Map1 = c.Map1, Map2 = c.Map2,
    };

    [GlobalSetup]
    public void Setup()
    {
        _ninoBuffer = new ArrayBufferWriter<byte>(320 * 1024);
        _memoryPackBuffer = new ArrayBufferWriter<byte>(320 * 1024);
    }

    [Benchmark(Baseline = true), BenchmarkCategory("TolerantClassSerialize")]
    public int NinoSerializeTolerantClass()
    {
        _ninoBuffer.ResetWrittenCount();
        V3Versioned.NinoGen.Serializer.Serialize(TolerantClass, _ninoBuffer);
        return _ninoBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("TolerantClassSerialize")]
    public int MemoryPackSerializeTolerantClass()
    {
        _memoryPackBuffer.ResetWrittenCount();
        MemoryPackSerializer.Serialize(_memoryPackBuffer, TolerantClass);
        return _memoryPackBuffer.WrittenCount;
    }

    [Benchmark(Baseline = true), BenchmarkCategory("TolerantClassesSerialize")]
    public int NinoSerializeTolerantClasses()
    {
        _ninoBuffer.ResetWrittenCount();
        V3Versioned.NinoGen.Serializer.Serialize(TolerantClasses, _ninoBuffer);
        return _ninoBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("TolerantClassesSerialize")]
    public int MemoryPackSerializeTolerantClasses()
    {
        _memoryPackBuffer.ResetWrittenCount();
        MemoryPackSerializer.Serialize(_memoryPackBuffer, TolerantClasses);
        return _memoryPackBuffer.WrittenCount;
    }

    [Benchmark(Baseline = true), BenchmarkCategory("TolerantClassDeserialize")]
    public TolerantClass NinoDeserializeTolerantClass()
    {
        V3Versioned.NinoGen.Deserializer.Deserialize(NinoTolerantClass, out TolerantClass ret);
        return ret;
    }

    [Benchmark, BenchmarkCategory("TolerantClassDeserialize")]
    public TolerantClass MemoryPackDeserializeTolerantClass() =>
        MemoryPackSerializer.Deserialize<TolerantClass>(MemoryPackTolerantClass);

    [Benchmark(Baseline = true), BenchmarkCategory("TolerantClassesDeserialize")]
    public TolerantClass[] NinoDeserializeTolerantClasses()
    {
        V3Versioned.NinoGen.Deserializer.Deserialize(NinoTolerantClasses, out TolerantClass[] ret);
        return ret;
    }

    [Benchmark, BenchmarkCategory("TolerantClassesDeserialize")]
    public TolerantClass[] MemoryPackDeserializeTolerantClasses() =>
        MemoryPackSerializer.Deserialize<TolerantClass[]>(MemoryPackTolerantClasses);
}

