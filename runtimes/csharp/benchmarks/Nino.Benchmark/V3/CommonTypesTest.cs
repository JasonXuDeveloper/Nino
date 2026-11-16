using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using BenchmarkDotNet.Configs;
using MemoryPack;

#nullable disable
namespace Nino.Benchmark;

// Cases/Collections/CommonTypesTest.cs ported to Nino 3.3.13 (NuGet), for the v3 build of this project
// (NINO_BENCH_V3=true): the same types (Fixtures/CommonTypes.cs), data, method names and job, Nino's and MemoryPack's
// cases. Only the Nino calls differ, as in V3/SimpleTest.cs.

/// <summary>
/// CommonTypesTest's shapes serialized to a reused buffer writer and deserialized into new instances by Nino v3: a tree
/// of 1,365 nodes, 1,000 padded structs, 1,000 structs of fixed size that are not raw and a dictionary of 1,000 string
/// pairs, against MemoryPack.
/// </summary>
[PayloadColumn]
[HideColumns("StdDev", "RatioSD", "Error")]
[MinColumn, MaxColumn]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[NinoJob]
[GcServer(true)]
[MemoryDiagnoser]
[MarkdownExporterAttribute.GitHub]
public class CommonTypesTest
{
    private static readonly TreeNode Tree = TreeNode.Create(new Random(7), 5);
    private static readonly InventoryItem[] Inventory = Enumerable.Range(0, 1000)
        .Select(i => new InventoryItem { ItemId = i, Count = (short)(i % 100), Bound = i % 3 == 0 }).ToArray();
    private static readonly Waypoint[] Waypoints = Enumerable.Range(0, 1000)
        .Select(i => new Waypoint { X = i * 0.5f, Y = -i, Stamp = (i * 16, (short)(i % 7)), Visited = i % 4 == 0 }).ToArray();
    private static readonly Dictionary<string, string> Strings = Enumerable.Range(0, 1000)
        .ToDictionary(i => "key" + i, i => "value" + i * 7);

    private static readonly byte[] NinoTree = NinoGen.Serializer.Serialize(Tree);
    private static readonly byte[] MemoryPackTree = MemoryPackSerializer.Serialize(Tree);
    private static readonly byte[] NinoInventory = NinoGen.Serializer.Serialize(Inventory);
    private static readonly byte[] MemoryPackInventory = MemoryPackSerializer.Serialize(Inventory);
    private static readonly byte[] NinoWaypoints = NinoGen.Serializer.Serialize(Waypoints);
    private static readonly byte[] MemoryPackWaypoints = MemoryPackSerializer.Serialize(Waypoints);
    private static readonly byte[] NinoStrings = NinoGen.Serializer.Serialize(Strings);
    private static readonly byte[] MemoryPackStrings = MemoryPackSerializer.Serialize(Strings);

    private ArrayBufferWriter<byte> _ninoBuffer;
    private ArrayBufferWriter<byte> _memoryPackBuffer;

    static CommonTypesTest()
    {
        BenchmarkPayloadRegistry.Register(nameof(NinoSerializeTree), NinoTree.Length);
        BenchmarkPayloadRegistry.Register(nameof(MemoryPackSerializeTree), MemoryPackTree.Length);
        BenchmarkPayloadRegistry.Register(nameof(NinoSerializeInventory), NinoInventory.Length);
        BenchmarkPayloadRegistry.Register(nameof(MemoryPackSerializeInventory), MemoryPackInventory.Length);
        BenchmarkPayloadRegistry.Register(nameof(NinoSerializeWaypoints), NinoWaypoints.Length);
        BenchmarkPayloadRegistry.Register(nameof(MemoryPackSerializeWaypoints), MemoryPackWaypoints.Length);
        BenchmarkPayloadRegistry.Register(nameof(NinoSerializeStringDictionary), NinoStrings.Length);
        BenchmarkPayloadRegistry.Register(nameof(MemoryPackSerializeStringDictionary), MemoryPackStrings.Length);
    }

    [GlobalSetup]
    public void Setup()
    {
        _ninoBuffer = new ArrayBufferWriter<byte>(1 << 20);
        _memoryPackBuffer = new ArrayBufferWriter<byte>(1 << 20);
    }

    [Benchmark(Baseline = true), BenchmarkCategory("TreeSerialize")]
    public int NinoSerializeTree()
    {
        _ninoBuffer.ResetWrittenCount();
        NinoGen.Serializer.Serialize(Tree, _ninoBuffer);
        return _ninoBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("TreeSerialize")]
    public int MemoryPackSerializeTree()
    {
        _memoryPackBuffer.ResetWrittenCount();
        MemoryPackSerializer.Serialize(_memoryPackBuffer, Tree);
        return _memoryPackBuffer.WrittenCount;
    }

    [Benchmark(Baseline = true), BenchmarkCategory("InventorySerialize")]
    public int NinoSerializeInventory()
    {
        _ninoBuffer.ResetWrittenCount();
        NinoGen.Serializer.Serialize(Inventory, _ninoBuffer);
        return _ninoBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("InventorySerialize")]
    public int MemoryPackSerializeInventory()
    {
        _memoryPackBuffer.ResetWrittenCount();
        MemoryPackSerializer.Serialize(_memoryPackBuffer, Inventory);
        return _memoryPackBuffer.WrittenCount;
    }

    [Benchmark(Baseline = true), BenchmarkCategory("WaypointsSerialize")]
    public int NinoSerializeWaypoints()
    {
        _ninoBuffer.ResetWrittenCount();
        NinoGen.Serializer.Serialize(Waypoints, _ninoBuffer);
        return _ninoBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("WaypointsSerialize")]
    public int MemoryPackSerializeWaypoints()
    {
        _memoryPackBuffer.ResetWrittenCount();
        MemoryPackSerializer.Serialize(_memoryPackBuffer, Waypoints);
        return _memoryPackBuffer.WrittenCount;
    }

    [Benchmark(Baseline = true), BenchmarkCategory("StringDictionarySerialize")]
    public int NinoSerializeStringDictionary()
    {
        _ninoBuffer.ResetWrittenCount();
        NinoGen.Serializer.Serialize(Strings, _ninoBuffer);
        return _ninoBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("StringDictionarySerialize")]
    public int MemoryPackSerializeStringDictionary()
    {
        _memoryPackBuffer.ResetWrittenCount();
        MemoryPackSerializer.Serialize(_memoryPackBuffer, Strings);
        return _memoryPackBuffer.WrittenCount;
    }

    [Benchmark(Baseline = true), BenchmarkCategory("TreeDeserialize")]
    public TreeNode NinoDeserializeTree()
    {
        NinoGen.Deserializer.Deserialize(NinoTree, out TreeNode ret);
        return ret;
    }

    [Benchmark, BenchmarkCategory("TreeDeserialize")]
    public TreeNode MemoryPackDeserializeTree() => MemoryPackSerializer.Deserialize<TreeNode>(MemoryPackTree);

    [Benchmark(Baseline = true), BenchmarkCategory("InventoryDeserialize")]
    public InventoryItem[] NinoDeserializeInventory()
    {
        NinoGen.Deserializer.Deserialize(NinoInventory, out InventoryItem[] ret);
        return ret;
    }

    [Benchmark, BenchmarkCategory("InventoryDeserialize")]
    public InventoryItem[] MemoryPackDeserializeInventory() =>
        MemoryPackSerializer.Deserialize<InventoryItem[]>(MemoryPackInventory);

    [Benchmark(Baseline = true), BenchmarkCategory("WaypointsDeserialize")]
    public Waypoint[] NinoDeserializeWaypoints()
    {
        NinoGen.Deserializer.Deserialize(NinoWaypoints, out Waypoint[] ret);
        return ret;
    }

    [Benchmark, BenchmarkCategory("WaypointsDeserialize")]
    public Waypoint[] MemoryPackDeserializeWaypoints() =>
        MemoryPackSerializer.Deserialize<Waypoint[]>(MemoryPackWaypoints);

    [Benchmark(Baseline = true), BenchmarkCategory("StringDictionaryDeserialize")]
    public Dictionary<string, string> NinoDeserializeStringDictionary()
    {
        NinoGen.Deserializer.Deserialize(NinoStrings, out Dictionary<string, string> ret);
        return ret;
    }

    [Benchmark, BenchmarkCategory("StringDictionaryDeserialize")]
    public Dictionary<string, string> MemoryPackDeserializeStringDictionary() =>
        MemoryPackSerializer.Deserialize<Dictionary<string, string>>(MemoryPackStrings);
}

