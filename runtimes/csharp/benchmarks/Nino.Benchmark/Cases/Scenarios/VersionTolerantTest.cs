using System.Linq;
using BenchmarkDotNet.Configs;

#nullable disable
namespace Nino.Benchmark;

/// <summary>
/// <see cref="TolerantClass"/> and an array of 100, written into a reused buffer writer and read back, as
/// <see cref="SimpleTest"/> does with <see cref="SimpleClass"/>: the difference between the two is the cost of version
/// tolerance.
/// The timed methods are VersionTolerantTest.Cases.g.cs, generated from the cases below and the adapters (Adapters/,
/// runtimes/csharp/tools/BenchGen). The v3 build times Nino 3.3.13's version-tolerant format, WEAK_VERSION_TOLERANCE, a
/// symbol its generated code reads for a whole project: the type is built in V3/Versioned, which defines it, and the
/// cases call the serializers v3 generates there (V3Versioned.NinoGen), as WorkloadTest's versioned workloads do.
/// </summary>
[BenchCase("TolerantClass")]
[BenchCase("TolerantClasses", Quick = true)]
[BenchClass(Bytes = "{Library}{Case}Bytes", NinoGen = "V3Versioned.NinoGen")]
[PayloadColumn]
[HideColumns("StdDev", "RatioSD", "Error")]
[MinColumn, MaxColumn]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[NinoJob]
[GcServer(true)]
[MemoryDiagnoser]
[MarkdownExporterAttribute.GitHub]
public partial class VersionTolerantTest
{
    private static readonly TolerantClass TolerantClass = From(SimpleClass.Create());
    private static readonly TolerantClass[] TolerantClasses =
        Enumerable.Range(0, 100).Select(_ => From(SimpleClass.Create())).ToArray();

#if !NINO_BENCH_V3
    private static TolerantClass From(SimpleClass c) => TolerantClass.From(c);
#else
    // V3/Versioned builds TolerantClass without SimpleClass, so its From is not there.
    private static TolerantClass From(SimpleClass c) => new()
    {
        Id = c.Id, Tag = c.Tag, Guid = c.Guid, CreateTime = c.CreateTime, Name = c.Name, Desc = c.Desc,
        Numbers = c.Numbers, Dates = c.Dates, Map1 = c.Map1, Map2 = c.Map2,
    };
#endif
}
