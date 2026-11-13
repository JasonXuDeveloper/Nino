using System.Collections.Generic;
using System.Linq;
using BenchmarkDotNet.Configs;
using MemoryPack;
using MessagePack;
using Nino.Core;

#nullable disable
namespace Nino.Benchmark;

// PooledGraphTest.cs's Leaves cases ported to Nino 3.3.13 (NuGet), for the v3 build of this project
// (NINO_BENCH_V3=true), with the same element (Fixtures/PooledLeaf.cs) and data. What v3 can express of them:
//  - NinoLeavesNew: NinoGen.Deserializer.Deserialize(bytes, out List<PooledLeaf>).
//  - NinoLeavesInPlace: v3's only way to read into an existing object, the overload its generator writes for a
//    collection with Clear and Add (Deserialize(List<PooledLeaf> value, ref Reader reader): value.Clear(), then
//    value.Add of each element read). It keeps the list and reads new elements: v3 has no overload that reads into an
//    existing [NinoType] object (its generator writes only Deserialize(out T value, ref Reader reader) and
//    Deserialize(ReadOnlySpan<byte> data, out T value) for one), so the elements are not reused as v4's are.
//  - NinoLeavesPooledElements is left out: 3.3.13 has no pool ([NinoPooled], ReturnToPool are v4's).
// The Node and Plain categories are left out: the release's chart and README use only the Leaves pair.
// protobuf-net is left out, as in the other v3 ports (the v3 run times Nino and MemoryPack).

/// <summary>A list of 100 elements read back new and into the list a program holds.</summary>
[HideColumns("StdDev", "RatioSD", "Error")]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[NinoJob]
[GcServer(true)]
[MemoryDiagnoser]
[MarkdownExporterAttribute.GitHub]
public class PooledGraphTest
{
    private static readonly List<PooledLeaf> Leaves =
        Enumerable.Range(0, 100).Select(_ => PooledLeaf.Create(new System.Random(2))).ToList();
    private static readonly byte[] NinoLeaves = NinoGen.Serializer.Serialize(Leaves);
    private static readonly byte[] MemoryPackLeaves = MemoryPackSerializer.Serialize(Leaves);
    private static readonly byte[] MessagePackLeaves = MessagePackSerializer.Serialize(Leaves);

    private List<PooledLeaf> _memoryPackLeaves, _ninoLeaves;

    [GlobalSetup]
    public void Setup()
    {
        _memoryPackLeaves = MemoryPackSerializer.Deserialize<List<PooledLeaf>>(MemoryPackLeaves);
        NinoGen.Deserializer.Deserialize(NinoLeaves, out _ninoLeaves);
    }

    [Benchmark(Baseline = true), BenchmarkCategory("Leaves")]
    public List<PooledLeaf> NinoLeavesNew()
    {
        NinoGen.Deserializer.Deserialize(NinoLeaves, out List<PooledLeaf> ret);
        return ret;
    }

    /// <summary>The same list, deserialized into again (its elements read new: see above).</summary>
    [Benchmark, BenchmarkCategory("Leaves")]
    public List<PooledLeaf> NinoLeavesInPlace()
    {
        var reader = new Reader(NinoLeaves);
        NinoGen.Deserializer.Deserialize(_ninoLeaves, ref reader);
        return _ninoLeaves;
    }

    [Benchmark, BenchmarkCategory("Leaves")]
    public List<PooledLeaf> MemoryPackLeavesNew() => MemoryPackSerializer.Deserialize<List<PooledLeaf>>(MemoryPackLeaves);

    [Benchmark, BenchmarkCategory("Leaves")]
    public List<PooledLeaf> MessagePackLeavesNew() => MessagePackSerializer.Deserialize<List<PooledLeaf>>(MessagePackLeaves);

    [Benchmark, BenchmarkCategory("Leaves")]
    public List<PooledLeaf> MemoryPackLeavesOverwrite()
    {
        MemoryPackSerializer.Deserialize(MemoryPackLeaves, ref _memoryPackLeaves);
        return _memoryPackLeaves;
    }
}
