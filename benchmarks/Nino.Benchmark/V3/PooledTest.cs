using BenchmarkDotNet.Configs;
using MemoryPack;

#nullable disable
namespace Nino.Benchmark;

// Cases/Graphs/PooledTest.cs ported to Nino 3.3.13 (NuGet), for the v3 build of this project (NINO_BENCH_V3=true): the
// same type (Fixtures/PooledBenchmarkData.cs, plain [NinoType] there), data, method names and job, Nino's and
// MemoryPack's cases. NinoDeserializePooled is left out: v3 has no pool ([NinoPooled], ReturnToPool and NinoPool are
// v4's; 3.3.13's runtime exports only the attributes, NinoTuple, Reader, Writer and TypeCollector), so it reads a new
// graph each call, which is NinoDeserializeNew, as MessagePack and protobuf-net do.

[HideColumns("StdDev", "RatioSD", "Error")]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[NinoJob]
[GcServer(true)]
[MemoryDiagnoser]
[MarkdownExporterAttribute.GitHub]
public class PooledTest
{
    private static readonly PooledSimpleClass Value = PooledSimpleClass.From(SimpleClass.Create());
    private static readonly byte[] NinoData = NinoGen.Serializer.Serialize(Value);
    private static readonly byte[] MemoryPackData = MemoryPackSerializer.Serialize(Value);

    private PooledSimpleClass _memoryPackTarget;

    [GlobalSetup]
    public void Setup() => _memoryPackTarget = MemoryPackSerializer.Deserialize<PooledSimpleClass>(MemoryPackData);

    [Benchmark(Baseline = true), BenchmarkCategory("PooledDeserialize")]
    public PooledSimpleClass NinoDeserializeNew()
    {
        NinoGen.Deserializer.Deserialize(NinoData, out PooledSimpleClass value);
        return value;
    }

    [Benchmark, BenchmarkCategory("PooledDeserialize")]
    public PooledSimpleClass MemoryPackDeserializeNew() => MemoryPackSerializer.Deserialize<PooledSimpleClass>(MemoryPackData);

    [Benchmark, BenchmarkCategory("PooledDeserialize")]
    public PooledSimpleClass MemoryPackDeserializeOverwrite()
    {
        MemoryPackSerializer.Deserialize(MemoryPackData, ref _memoryPackTarget);
        return _memoryPackTarget;
    }
}
