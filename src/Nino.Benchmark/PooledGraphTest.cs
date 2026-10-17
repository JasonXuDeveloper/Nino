using System;
using System.Collections.Generic;
using System.Linq;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using MemoryPack;
using Nino.Core;

#nullable disable
namespace Nino.Benchmark;

/// <summary>A pooled leaf: primitives and a list.</summary>
[NinoType, NinoPooled]
[MemoryPackable]
public sealed partial class PooledLeaf
{
    public int A;
    public float B;
    public List<int> Items;

    private static partial IEnumerable<PooledLeaf> OnNinoPoolWarmup() => null;

    private partial void OnNinoPoolReturn()
    {
    }

    public static PooledLeaf Create(Random r) =>
        new() { A = r.Next(), B = r.NextSingle(), Items = Enumerable.Range(0, 8).Select(_ => r.Next()).ToList() };
}

/// <summary>The same as <see cref="PooledLeaf"/>, not pooled.</summary>
[NinoType]
[MemoryPackable]
public sealed partial class PlainLeaf
{
    public int A;
    public float B;
    public List<int> Items;

    public static PlainLeaf Create(Random r) =>
        new() { A = r.Next(), B = r.NextSingle(), Items = Enumerable.Range(0, 8).Select(_ => r.Next()).ToList() };
}

/// <summary>No strings: primitives, an array, a list, a dictionary, a pooled child and a list of pooled children.</summary>
[NinoType, NinoPooled]
[MemoryPackable]
public sealed partial class PooledNode
{
    public int Id;
    public long Ticks;
    public double X, Y;
    public bool Flag;
    public Guid Guid;
    public int[] Data;
    public List<long> Values;
    public Dictionary<int, int> Map;
    public PooledLeaf Leaf;
    public List<PooledLeaf> Leaves;

    private static partial IEnumerable<PooledNode> OnNinoPoolWarmup() => null;

    private partial void OnNinoPoolReturn()
    {
    }

    public static PooledNode Create(Random r) => new()
    {
        Id = r.Next(), Ticks = r.NextInt64(), X = r.NextDouble(), Y = r.NextDouble(), Flag = true,
        Guid = Guid.NewGuid(),
        Data = Enumerable.Range(0, 32).Select(_ => r.Next()).ToArray(),
        Values = Enumerable.Range(0, 16).Select(_ => r.NextInt64()).ToList(),
        Map = Enumerable.Range(0, 16).ToDictionary(i => i, _ => r.Next()),
        Leaf = PooledLeaf.Create(r),
        Leaves = Enumerable.Range(0, 8).Select(_ => PooledLeaf.Create(r)).ToList(),
    };
}

/// <summary>
/// Pooling without strings, so steady state allocates nothing: a pooled graph, and a list of pooled elements.
/// "New" creates everything; "Pooled" returns what it got so the next call reuses it; MemoryPack overwrites the
/// previous result in place.
/// </summary>
[HideColumns("StdDev", "RatioSD", "Error")]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[NinoJob]
[GcServer(true)]
[MemoryDiagnoser]
[MarkdownExporterAttribute.GitHub]
public class PooledGraphTest
{
    private static readonly PooledNode Node = PooledNode.Create(new Random(1));
    private static readonly List<PooledLeaf> Leaves =
        Enumerable.Range(0, 100).Select(_ => PooledLeaf.Create(new Random(2))).ToList();
    private static readonly byte[] NinoNode = NinoSerializer.Serialize(Node);
    private static readonly byte[] MemoryPackNode = MemoryPackSerializer.Serialize(Node);
    private static readonly byte[] NinoLeaves = NinoSerializer.Serialize(Leaves);
    private static readonly byte[] MemoryPackLeaves = MemoryPackSerializer.Serialize(Leaves);

    private static readonly List<PlainLeaf> Plain =
        Enumerable.Range(0, 100).Select(_ => PlainLeaf.Create(new Random(2))).ToList();
    private static readonly byte[] NinoPlain = NinoSerializer.Serialize(Plain);
    private static readonly byte[] MemoryPackPlain = MemoryPackSerializer.Serialize(Plain);

    [Benchmark(Baseline = true), BenchmarkCategory("Plain")]
    public List<PlainLeaf> NinoPlainNew() => NinoDeserializer.Deserialize<List<PlainLeaf>>(NinoPlain);

    [Benchmark, BenchmarkCategory("Plain")]
    public List<PlainLeaf> MemoryPackPlainNew() => MemoryPackSerializer.Deserialize<List<PlainLeaf>>(MemoryPackPlain);

    private PooledNode _memoryPackNode;
    private List<PooledLeaf> _memoryPackLeaves, _ninoLeaves;

    [GlobalSetup]
    public void Setup()
    {
        _memoryPackNode = MemoryPackSerializer.Deserialize<PooledNode>(MemoryPackNode);
        _memoryPackLeaves = MemoryPackSerializer.Deserialize<List<PooledLeaf>>(MemoryPackLeaves);
        _ninoLeaves = NinoDeserializer.Deserialize<List<PooledLeaf>>(NinoLeaves);
    }

    [Benchmark(Baseline = true), BenchmarkCategory("Node")]
    public PooledNode NinoNodeNew() => NinoDeserializer.Deserialize<PooledNode>(NinoNode);

    [Benchmark, BenchmarkCategory("Node")]
    public int NinoNodePooled()
    {
        var node = NinoDeserializer.Deserialize<PooledNode>(NinoNode);
        int id = node.Id;
        node.ReturnToPool();
        return id;
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
    public List<PooledLeaf> NinoLeavesNew() => NinoDeserializer.Deserialize<List<PooledLeaf>>(NinoLeaves);

    /// <summary>A new list each time, its elements taken from the pool and returned after use.</summary>
    [Benchmark, BenchmarkCategory("Leaves")]
    public int NinoLeavesPooledElements()
    {
        var leaves = NinoDeserializer.Deserialize<List<PooledLeaf>>(NinoLeaves);
        int count = leaves.Count;
        foreach (var leaf in leaves)
            leaf.ReturnToPool();
        return count;
    }

    /// <summary>The same list and elements, deserialized into again.</summary>
    [Benchmark, BenchmarkCategory("Leaves")]
    public List<PooledLeaf> NinoLeavesInPlace()
    {
        NinoDeserializer.Deserialize(NinoLeaves, ref _ninoLeaves);
        return _ninoLeaves;
    }

    [Benchmark, BenchmarkCategory("Leaves")]
    public List<PooledLeaf> MemoryPackLeavesNew() => MemoryPackSerializer.Deserialize<List<PooledLeaf>>(MemoryPackLeaves);

    [Benchmark, BenchmarkCategory("Leaves")]
    public List<PooledLeaf> MemoryPackLeavesOverwrite()
    {
        MemoryPackSerializer.Deserialize(MemoryPackLeaves, ref _memoryPackLeaves);
        return _memoryPackLeaves;
    }
}
