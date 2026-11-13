// The v4 class is left out of A/B runs against releases older than its setting (NINO_BENCH_LEGACY): so is this one.
#if !NINO_BENCH_LEGACY
using System.Collections.Generic;
using System.Linq;
using BenchmarkDotNet.Configs;
using MemoryPack;

#nullable disable
namespace Nino.Benchmark;

// Cases/Scenarios/UntrustedTest.cs ported to Nino 3.3.13 (NuGet), for the v3 build of this project
// (NINO_BENCH_V3=true): the same type (Fixtures/CheckedSample.cs), data, method names and job, Nino's and MemoryPack's
// cases. v3 has no untrusted mode (NinoSettings is v4's; 3.3.13's runtime exports only the attributes, NinoTuple,
// Reader, Writer and TypeCollector): it copies the raw structs unchecked, as MemoryPack does, so the case reads the
// same bytes the only way v3 reads them.

/// <summary>A list of 1,000 raw structs with a DateTime and a bool read back by Nino v3, against MemoryPack.</summary>
[HideColumns("StdDev", "RatioSD", "Error")]
[MinColumn, MaxColumn]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[NinoJob]
[GcServer(true)]
[MemoryDiagnoser]
[MarkdownExporterAttribute.GitHub]
public class UntrustedTest
{
    private const int N = 1000;
    private static readonly List<CheckedSample> Samples = Enumerable.Range(0, N).Select(i => new CheckedSample
    {
        At = BenchmarkRandom.Now.AddSeconds(i * 37), Value = i * 0.25, Id = i * 7, Valid = i % 3 != 0,
    }).ToList();
    private static readonly byte[] NinoSamplesBytes = NinoGen.Serializer.Serialize(Samples);
    private static readonly byte[] MemoryPackSamplesBytes = MemoryPackSerializer.Serialize(Samples);

    [Benchmark(Baseline = true), BenchmarkCategory("UntrustedListDeserialize")]
    public List<CheckedSample> NinoDeserializeUntrustedList()
    {
        NinoGen.Deserializer.Deserialize(NinoSamplesBytes, out List<CheckedSample> ret);
        return ret;
    }

    [Benchmark, BenchmarkCategory("UntrustedListDeserialize")]
    public List<CheckedSample> MemoryPackDeserializeUntrustedList() =>
        MemoryPackSerializer.Deserialize<List<CheckedSample>>(MemoryPackSamplesBytes);
}
#endif
