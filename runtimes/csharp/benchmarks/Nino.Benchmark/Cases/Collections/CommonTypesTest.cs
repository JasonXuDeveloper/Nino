using System;
using System.Collections.Generic;
using System.Linq;
using BenchmarkDotNet.Configs;

#nullable disable
namespace Nino.Benchmark;

/// <summary>
/// Common type shapes (CommonTypes.cs), each serialized to a reused buffer writer and deserialized into new
/// instances: a tree of 1,365 nodes, 1,000 padded structs, 1,000 structs of fixed size that are not raw and a
/// dictionary of 1,000 string pairs. Against MemoryPack, MessagePack and protobuf-net (no tree: protobuf cannot tell
/// the leaves' empty child lists from none). Payloads shaped like whole workloads are WorkloadTest's.
/// The timed methods are CommonTypesTest.Cases.g.cs, generated from the cases below and the adapters (Adapters/,
/// runtimes/csharp/tools/BenchGen); the v3 build times Nino 3.3.13 and MemoryPack on the same values.
/// </summary>
[BenchCase("Tree", Without = "Protobuf")]
[BenchCase("Inventory")]
[BenchCase("Waypoints", Without = "LuminPack")] // LuminPack 1.1.9: its generator writes ReadValue and WriteValue twice (CS0111)
[BenchCase("StringDictionary", Value = "Strings")]
[BenchClass(Buffer = "1 << 20", Bytes = "{Library}{Case}Bytes", Order = "case")]
[PayloadColumn]
[HideColumns("StdDev", "RatioSD", "Error")]
[MinColumn, MaxColumn]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[NinoJob]
[GcServer(true)]
[MemoryDiagnoser]
[MarkdownExporterAttribute.GitHub]
public partial class CommonTypesTest
{
    private static readonly TreeNode Tree = TreeNode.Create(new Random(7), 5);
    private static readonly InventoryItem[] Inventory = Enumerable.Range(0, 1000)
        .Select(i => new InventoryItem { ItemId = i, Count = (short)(i % 100), Bound = i % 3 == 0 }).ToArray();
    private static readonly Waypoint[] Waypoints = Enumerable.Range(0, 1000)
        .Select(i => new Waypoint { X = i * 0.5f, Y = -i, Stamp = (i * 16, (short)(i % 7)), Visited = i % 4 == 0 }).ToArray();
    private static readonly Dictionary<string, string> Strings = Enumerable.Range(0, 1000)
        .ToDictionary(i => "key" + i, i => "value" + i * 7);
}
