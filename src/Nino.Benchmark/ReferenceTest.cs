using System;
using System.Collections.Generic;
using System.Linq;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using Nino.Core;

#nullable disable
namespace Nino.Benchmark;

/// <summary>A node of a tracked graph: a back reference to its graph (a cycle), a shared tag and a peer.</summary>
[NinoType(trackReferences: true)]
public sealed partial class RefNode
{
    public int Id;
    public double Weight;
    public RefGraph Graph;
    public RefTag Tag;
    public RefNode Peer;
}

/// <summary>Shared by many nodes.</summary>
[NinoType(trackReferences: true)]
public sealed partial class RefTag
{
    public int Id;
    public string Name;
}

/// <summary>The root: every node points back to it.</summary>
[NinoType(trackReferences: true)]
public sealed partial class RefGraph
{
    public int Version;
    public List<RefNode> Nodes;
    public List<RefTag> Tags;

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
/// Reference tracking (<c>trackReferences</c>): a graph of 200 nodes with shared tags, shared peers and cycles through
/// the root, written into a reused buffer and read back. Nino only: the other serializers in this project do not
/// track references.
/// </summary>
[HideColumns("StdDev", "RatioSD", "Error")]
[ShortRunJob(RuntimeMoniker.Net10_0)]
[GcServer(true)]
[MemoryDiagnoser]
[MarkdownExporterAttribute.GitHub]
public class ReferenceTest
{
    private static readonly RefGraph Graph = RefGraph.Create(200);
    private static readonly byte[] NinoGraph = NinoSerializer.Serialize(Graph);
    private NinoArrayBufferWriter _ninoBuffer;

    [GlobalSetup]
    public void Setup() => _ninoBuffer = new NinoArrayBufferWriter(64 * 1024);

    [Benchmark]
    public int NinoSerializeGraph()
    {
        _ninoBuffer.ResetWrittenCount();
        NinoSerializer.Serialize(Graph, _ninoBuffer);
        return _ninoBuffer.WrittenCount;
    }

    [Benchmark]
    public RefGraph NinoDeserializeGraph() => NinoDeserializer.Deserialize<RefGraph>(NinoGraph);
}
