using System;
using System.Collections.Generic;
using System.Linq;
using BenchmarkDotNet.Configs;
using MemoryPack;
using MessagePack;
using Nino.Core;
using ProtoBuf;

#nullable disable
namespace Nino.Benchmark;

/// <summary>
/// Pooling without strings, so steady state allocates nothing: a pooled graph, and a list of pooled elements.
/// "New" creates everything; "Pooled" returns what it got so the next call reuses it; MemoryPack overwrites the
/// previous result in place. MessagePack and protobuf-net read new instances only: neither overwrites one in place.
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
    private static readonly byte[] MessagePackNode = MessagePackSerializer.Serialize(Node);
    private static readonly byte[] ProtobufNode = ProtobufModel.Serialize(Node);
    private static readonly byte[] MessagePackLeaves = MessagePackSerializer.Serialize(Leaves);
    private static readonly byte[] ProtobufLeaves = ProtobufModel.Serialize(Leaves);
    private static readonly byte[] MessagePackPlain = MessagePackSerializer.Serialize(Plain);
    private static readonly byte[] ProtobufPlain = ProtobufModel.Serialize(Plain);

    [Benchmark(Baseline = true), BenchmarkCategory("Plain")]
    public List<PlainLeaf> NinoPlainNew() => NinoDeserializer.Deserialize<List<PlainLeaf>>(NinoPlain);

    [Benchmark, BenchmarkCategory("Plain")]
    public List<PlainLeaf> MemoryPackPlainNew() => MemoryPackSerializer.Deserialize<List<PlainLeaf>>(MemoryPackPlain);

    [Benchmark, BenchmarkCategory("Plain")]
    public List<PlainLeaf> MessagePackPlainNew() => MessagePackSerializer.Deserialize<List<PlainLeaf>>(MessagePackPlain);

    [Benchmark, BenchmarkCategory("Plain")]
    public List<PlainLeaf> ProtobufPlainNew() => Serializer.Deserialize<List<PlainLeaf>>(new ReadOnlySpan<byte>(ProtobufPlain));

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
    public PooledNode MessagePackNodeNew() => MessagePackSerializer.Deserialize<PooledNode>(MessagePackNode);

    [Benchmark, BenchmarkCategory("Node")]
    public PooledNode ProtobufNodeNew() => Serializer.Deserialize<PooledNode>(new ReadOnlySpan<byte>(ProtobufNode));

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
    public List<PooledLeaf> MessagePackLeavesNew() => MessagePackSerializer.Deserialize<List<PooledLeaf>>(MessagePackLeaves);

    [Benchmark, BenchmarkCategory("Leaves")]
    public List<PooledLeaf> ProtobufLeavesNew() =>
        Serializer.Deserialize<List<PooledLeaf>>(new ReadOnlySpan<byte>(ProtobufLeaves));

    [Benchmark, BenchmarkCategory("Leaves")]
    public List<PooledLeaf> MemoryPackLeavesOverwrite()
    {
        MemoryPackSerializer.Deserialize(MemoryPackLeaves, ref _memoryPackLeaves);
        return _memoryPackLeaves;
    }
}
