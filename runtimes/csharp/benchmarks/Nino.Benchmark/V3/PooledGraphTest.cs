using System.Collections.Generic;
using System.Linq;
using BenchmarkDotNet.Configs;
using MemoryPack;
using MessagePack;
using Nino.Core;

#nullable disable
namespace Nino.Benchmark;

// Cases/Graphs/PooledGraphTest.cs ported to Nino 3.3.13 (NuGet), for the v3 build of this project (NINO_BENCH_V3=true),
// with the same types (Fixtures/PooledBenchmarkData.cs, plain [NinoType] there), data, method names and job. What v3
// can express of them:
//  - NinoNodeNew, NinoPlainNew, NinoLeavesNew: NinoGen.Deserializer.Deserialize(bytes, out T).
//  - NinoLeavesInPlace: v3's only way to read into an existing object, the overload its generator writes for a
//    collection with Clear and Add (Deserialize(List<PooledLeaf> value, ref Reader reader): value.Clear(), then
//    value.Add of each element read). It keeps the list and reads new elements: v3 has no overload that reads into an
//    existing [NinoType] object (its generator writes only Deserialize(out T value, ref Reader reader) and
//    Deserialize(ReadOnlySpan<byte> data, out T value) for one), so the elements are not reused as v4's are.
//  - NinoNodePooled and NinoLeavesPooledElements are left out: 3.3.13 has no pool ([NinoPooled], ReturnToPool and
//    NinoPool are v4's; its runtime exports only the attributes, NinoTuple, Reader, Writer and TypeCollector).
// protobuf-net is left out, as in the other v3 ports (the v3 run times Nino and MemoryPack).

/// <summary>
/// A graph and lists of 100 elements read back new by Nino v3, and a list read into the list a program holds, against
/// MemoryPack (new and overwritten in place) and, for the list, MessagePack (new).
/// </summary>
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
    private static readonly PooledNode Node = PooledNode.Create(new System.Random(1));
    private static readonly byte[] NinoNode = NinoGen.Serializer.Serialize(Node);
    private static readonly byte[] MemoryPackNode = MemoryPackSerializer.Serialize(Node);
    private static readonly List<PlainLeaf> Plain =
        Enumerable.Range(0, 100).Select(_ => PlainLeaf.Create(new System.Random(2))).ToList();
    private static readonly byte[] NinoPlain = NinoGen.Serializer.Serialize(Plain);
    private static readonly byte[] MemoryPackPlain = MemoryPackSerializer.Serialize(Plain);

    private PooledNode _memoryPackNode;
    private List<PooledLeaf> _memoryPackLeaves, _ninoLeaves;

    [GlobalSetup]
    public void Setup()
    {
        _memoryPackNode = MemoryPackSerializer.Deserialize<PooledNode>(MemoryPackNode);
        _memoryPackLeaves = MemoryPackSerializer.Deserialize<List<PooledLeaf>>(MemoryPackLeaves);
        NinoGen.Deserializer.Deserialize(NinoLeaves, out _ninoLeaves);
    }

    [Benchmark(Baseline = true), BenchmarkCategory("Plain")]
    public List<PlainLeaf> NinoPlainNew()
    {
        NinoGen.Deserializer.Deserialize(NinoPlain, out List<PlainLeaf> ret);
        return ret;
    }

    [Benchmark, BenchmarkCategory("Plain")]
    public List<PlainLeaf> MemoryPackPlainNew() => MemoryPackSerializer.Deserialize<List<PlainLeaf>>(MemoryPackPlain);

    [Benchmark(Baseline = true), BenchmarkCategory("Node")]
    public PooledNode NinoNodeNew()
    {
        NinoGen.Deserializer.Deserialize(NinoNode, out PooledNode ret);
        return ret;
    }

    [Benchmark, BenchmarkCategory("Node")]
    public PooledNode MemoryPackNodeNew() => MemoryPackSerializer.Deserialize<PooledNode>(MemoryPackNode);

    [Benchmark, BenchmarkCategory("Node")]
    public PooledNode MemoryPackNodeOverwrite()
    {
        MemoryPackSerializer.Deserialize(MemoryPackNode, ref _memoryPackNode);
        return _memoryPackNode;
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
