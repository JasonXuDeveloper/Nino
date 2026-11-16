using BenchmarkDotNet.Configs;

#nullable disable
namespace Nino.Benchmark.AppData;

/// <summary>
/// The TypeScript benchmark's scenarios (runtimes/typescript/bench) in the .NET suite, each scenario's values as one
/// array (Fixtures/AppDataModels.cs): 1,000 players (numbers, text, a small array, a map and a pet behind a type id),
/// 50 zoos of 100 animals behind type ids two levels deep, 100 order books of 50 orders, 200 articles (mostly text,
/// some of it not Latin-1), 20 meshes (arrays of 500 Vec3 and 1,500 ints), 20 sample sets (1,000 doubles, ints and
/// floats each), 50 lookups (two maps of 100) and 1,000 events. Serialized to a reused buffer writer and deserialized
/// into new instances, against MemoryPack, MessagePack, protobuf-net and LuminPack. The timed methods are
/// AppDataTest.Cases.g.cs, generated from the cases below and the adapters (Adapters/, runtimes/csharp/tools/BenchGen);
/// the v3 build times Nino 3.3.13 and MemoryPack on the same values.
/// </summary>
[BenchCase("Players", Quick = true)]
[BenchCase("Zoos")]
[BenchCase("OrderBooks")]
[BenchCase("Articles", Quick = true)]
[BenchCase("Meshes", Broken = "LuminPack wrong-read: reads 20 meshes back other than it wrote")]
[BenchCase("SampleSets")]
[BenchCase("Lookups")]
[BenchCase("Events")]
[BenchClass(Buffer = "1 << 20", Bytes = "{Library}{Case}Bytes", Order = "case")]
[PayloadColumn]
[HideColumns("StdDev", "RatioSD", "Error")]
[MinColumn, MaxColumn]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[NinoJob]
[GcServer(true)]
[MemoryDiagnoser]
[MarkdownExporterAttribute.GitHub]
public partial class AppDataTest
{
    private static readonly Player[] Players = AppDataValues.Players;
    private static readonly Zoo[] Zoos = AppDataValues.Zoos;
    private static readonly OrderBook[] OrderBooks = AppDataValues.OrderBooks;
    private static readonly Article[] Articles = AppDataValues.Articles;
    private static readonly Mesh[] Meshes = AppDataValues.Meshes;
    private static readonly Samples[] SampleSets = AppDataValues.SampleSets;
    private static readonly Lookup[] Lookups = AppDataValues.Lookups;
    private static readonly Event[] Events = AppDataValues.Events;
}
