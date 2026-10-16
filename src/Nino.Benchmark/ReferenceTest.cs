using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using MemoryPack;
using Nino.Core;

#nullable disable
namespace Nino.Benchmark;

/// <summary>A node of a tracked graph: a back reference to its graph (a cycle), a shared tag and a peer.</summary>
[NinoType(trackReferences: true)]
[MemoryPackable(GenerateType.CircularReference)]
public sealed partial class RefNode
{
    [MemoryPackOrder(0)] public int Id;
    [MemoryPackOrder(1)] public double Weight;
    [MemoryPackOrder(2)] public RefGraph Graph;
    [MemoryPackOrder(3)] public RefTag Tag;
    [MemoryPackOrder(4)] public RefNode Peer;
}

/// <summary>Shared by many nodes.</summary>
[NinoType(trackReferences: true)]
[MemoryPackable(GenerateType.CircularReference)]
public sealed partial class RefTag
{
    [MemoryPackOrder(0)] public int Id;
    [MemoryPackOrder(1)] public string Name;
}

/// <summary>The root: every node points back to it.</summary>
[NinoType(trackReferences: true)]
[MemoryPackable(GenerateType.CircularReference)]
public sealed partial class RefGraph
{
    [MemoryPackOrder(0)] public int Version;
    [MemoryPackOrder(1)] public List<RefNode> Nodes;
    [MemoryPackOrder(2)] public List<RefTag> Tags;

    /// <summary>
    /// <paramref name="count"/> nodes sharing 16 tags; each node's peer is an earlier node (a back reference, so the
    /// nesting stays shallow), and every node refers back to the graph.
    /// </summary>
    public static RefGraph Create(int count)
    {
        var r = new Random(3);
        var graph = new RefGraph { Version = 1, Nodes = new List<RefNode>(count) };
        graph.Tags = Enumerable.Range(0, 16).Select(i => new RefTag { Id = i, Name = "tag" + i }).ToList();
        for (int i = 0; i < count; i++)
        {
            graph.Nodes.Add(new RefNode
            {
                Id = i, Weight = r.NextDouble(), Graph = graph, Tag = graph.Tags[r.Next(graph.Tags.Count)],
                Peer = i == 0 ? null : graph.Nodes[r.Next(i)]
            });
        }

        return graph;
    }
}

/// <summary>
/// Reference tracking: a graph of 200 nodes with shared tags, shared peers and cycles through the root, written into
/// a reused buffer and read back. Nino tracks references with <c>[NinoType(trackReferences: true)]</c>, MemoryPack
/// with <see cref="GenerateType.CircularReference"/> (which needs an explicit <see cref="MemoryPackOrderAttribute"/>
/// on each member); both write each object once and read shared references and cycles back as one instance.
/// </summary>
[PayloadColumn]
[HideColumns("StdDev", "RatioSD", "Error")]
[MinColumn, MaxColumn]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[ShortRunJob(RuntimeMoniker.Net10_0)]
[GcServer(true)]
[MemoryDiagnoser]
[MarkdownExporterAttribute.GitHub]
public class ReferenceTest
{
    private static readonly RefGraph Graph = RefGraph.Create(200);
    private static readonly byte[] NinoGraph = NinoSerializer.Serialize(Graph);
    private static readonly byte[] MemoryPackGraph = MemoryPackSerializer.Serialize(Graph);

    private NinoArrayBufferWriter _ninoBuffer;
    private ArrayBufferWriter<byte> _memoryPackBuffer;

    static ReferenceTest()
    {
        BenchmarkPayloadRegistry.Register(nameof(NinoSerializeGraph), NinoGraph.Length);
        BenchmarkPayloadRegistry.Register(nameof(MemoryPackSerializeGraph), MemoryPackGraph.Length);
    }

    [GlobalSetup]
    public void Setup()
    {
        _ninoBuffer = new NinoArrayBufferWriter(64 * 1024);
        _memoryPackBuffer = new ArrayBufferWriter<byte>(64 * 1024);
    }

    [Benchmark(Baseline = true), BenchmarkCategory("GraphSerialize")]
    public int NinoSerializeGraph()
    {
        _ninoBuffer.ResetWrittenCount();
        NinoSerializer.Serialize(Graph, _ninoBuffer);
        return _ninoBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("GraphSerialize")]
    public int MemoryPackSerializeGraph()
    {
        _memoryPackBuffer.ResetWrittenCount();
        MemoryPackSerializer.Serialize(_memoryPackBuffer, Graph);
        return _memoryPackBuffer.WrittenCount;
    }

    [Benchmark(Baseline = true), BenchmarkCategory("GraphDeserialize")]
    public RefGraph NinoDeserializeGraph() => NinoDeserializer.Deserialize<RefGraph>(NinoGraph);

    [Benchmark, BenchmarkCategory("GraphDeserialize")]
    public RefGraph MemoryPackDeserializeGraph() => MemoryPackSerializer.Deserialize<RefGraph>(MemoryPackGraph);
}
