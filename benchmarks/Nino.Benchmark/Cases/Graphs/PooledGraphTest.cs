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

/// <summary>A pooled leaf: primitives and a list.</summary>
[NinoType, NinoPooled]
[MemoryPackable]
[MessagePackObject]
[ProtoContract]
public sealed partial class PooledLeaf
{
    [Key(0)] [ProtoMember(1)] public int A;
    [Key(1)] [ProtoMember(2)] public float B;
    [Key(2)] [ProtoMember(3)] public List<int> Items;

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
[MessagePackObject]
[ProtoContract]
public sealed partial class PlainLeaf
{
    [Key(0)] [ProtoMember(1)] public int A;
    [Key(1)] [ProtoMember(2)] public float B;
    [Key(2)] [ProtoMember(3)] public List<int> Items;

    public static PlainLeaf Create(Random r) =>
        new() { A = r.Next(), B = r.NextSingle(), Items = Enumerable.Range(0, 8).Select(_ => r.Next()).ToList() };
}

/// <summary>No strings: primitives, an array, a list, a dictionary, a pooled child and a list of pooled children.</summary>
[NinoType, NinoPooled]
[MemoryPackable]
[MessagePackObject]
[ProtoContract]
public sealed partial class PooledNode
{
    [Key(0)] [ProtoMember(1)] public int Id;
    [Key(1)] [ProtoMember(2)] public long Ticks;
    [Key(2)] [ProtoMember(3)] public double X;
    [Key(3)] [ProtoMember(4)] public double Y;
    [Key(4)] [ProtoMember(5)] public bool Flag;
    [Key(5)] [ProtoMember(6)] public Guid Guid;
    [Key(6)] [ProtoMember(7)] public int[] Data;
    [Key(7)] [ProtoMember(8)] public List<long> Values;
    [Key(8)] [ProtoMember(9)] public Dictionary<int, int> Map;
    [Key(9)] [ProtoMember(10)] public PooledLeaf Leaf;
    [Key(10)] [ProtoMember(11)] public List<PooledLeaf> Leaves;

    private static partial IEnumerable<PooledNode> OnNinoPoolWarmup() => null;

    private partial void OnNinoPoolReturn()
    {
    }

    public static PooledNode Create(Random r) => new()
    {
        Id = r.Next(), Ticks = r.NextInt64(), X = r.NextDouble(), Y = r.NextDouble(), Flag = true,
        Guid = BenchmarkRandom.NewGuid(),
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
