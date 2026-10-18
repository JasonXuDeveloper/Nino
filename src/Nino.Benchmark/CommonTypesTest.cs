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

/// <summary>
/// Data shaped like that of common applications (CommonTypes.cs), each serialized to a reused buffer writer and
/// deserialized into new instances: 100 players, 100 orders, 1,000 shapes through their abstract base, a tree of
/// 1,365 nodes, 1,000 padded structs and a dictionary of 1,000 string pairs.
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
    private static readonly List<Player> Players = Enumerable.Range(0, 100).Select(Player.Create).ToList();
    private static readonly List<Order> Orders = Enumerable.Range(0, 100).Select(i => Order.Create(i, new Random(i))).ToList();
    private static readonly List<Shape> Shapes = Enumerable.Range(0, 1000)
        .Select(i => i % 2 == 0 ? (Shape)new Circle { Color = i, Radius = i } : new Rect { Color = i, Width = i, Height = 2 })
        .ToList();
    private static readonly TreeNode Tree = TreeNode.Create(new Random(7), 5);
    private static readonly InventoryItem[] Inventory = Enumerable.Range(0, 1000)
        .Select(i => new InventoryItem { ItemId = i, Count = (short)(i % 100), Bound = i % 3 == 0 }).ToArray();
    private static readonly Dictionary<string, string> Strings = Enumerable.Range(0, 1000)
        .ToDictionary(i => "key" + i, i => "value" + i * 7);

    private static readonly byte[] NinoPlayers = NinoSerializer.Serialize(Players);
    private static readonly byte[] MemoryPackPlayers = MemoryPackSerializer.Serialize(Players);
    private static readonly byte[] NinoOrders = NinoSerializer.Serialize(Orders);
    private static readonly byte[] MemoryPackOrders = MemoryPackSerializer.Serialize(Orders);
    private static readonly byte[] NinoShapes = NinoSerializer.Serialize(Shapes);
    private static readonly byte[] MemoryPackShapes = MemoryPackSerializer.Serialize(Shapes);
    private static readonly byte[] NinoTree = NinoSerializer.Serialize(Tree);
    private static readonly byte[] MemoryPackTree = MemoryPackSerializer.Serialize(Tree);
    private static readonly byte[] NinoInventory = NinoSerializer.Serialize(Inventory);
    private static readonly byte[] MemoryPackInventory = MemoryPackSerializer.Serialize(Inventory);
    private static readonly byte[] NinoStrings = NinoSerializer.Serialize(Strings);
    private static readonly byte[] MemoryPackStrings = MemoryPackSerializer.Serialize(Strings);

    private NinoArrayBufferWriter _ninoBuffer;
    private ArrayBufferWriter<byte> _memoryPackBuffer;

    static CommonTypesTest()
    {
        BenchmarkPayloadRegistry.Register(nameof(NinoSerializePlayers), NinoPlayers.Length);
        BenchmarkPayloadRegistry.Register(nameof(MemoryPackSerializePlayers), MemoryPackPlayers.Length);
        BenchmarkPayloadRegistry.Register(nameof(NinoSerializeOrders), NinoOrders.Length);
        BenchmarkPayloadRegistry.Register(nameof(MemoryPackSerializeOrders), MemoryPackOrders.Length);
        BenchmarkPayloadRegistry.Register(nameof(NinoSerializeShapes), NinoShapes.Length);
        BenchmarkPayloadRegistry.Register(nameof(MemoryPackSerializeShapes), MemoryPackShapes.Length);
        BenchmarkPayloadRegistry.Register(nameof(NinoSerializeTree), NinoTree.Length);
        BenchmarkPayloadRegistry.Register(nameof(MemoryPackSerializeTree), MemoryPackTree.Length);
        BenchmarkPayloadRegistry.Register(nameof(NinoSerializeInventory), NinoInventory.Length);
        BenchmarkPayloadRegistry.Register(nameof(MemoryPackSerializeInventory), MemoryPackInventory.Length);
        BenchmarkPayloadRegistry.Register(nameof(NinoSerializeStringDictionary), NinoStrings.Length);
        BenchmarkPayloadRegistry.Register(nameof(MemoryPackSerializeStringDictionary), MemoryPackStrings.Length);
    }

    [GlobalSetup]
    public void Setup()
    {
        _ninoBuffer = new NinoArrayBufferWriter(1 << 20);
        _memoryPackBuffer = new ArrayBufferWriter<byte>(1 << 20);
    }

    [Benchmark(Baseline = true), BenchmarkCategory("PlayersSerialize")]
    public int NinoSerializePlayers()
    {
        _ninoBuffer.ResetWrittenCount();
        NinoSerializer.Serialize(Players, _ninoBuffer);
        return _ninoBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("PlayersSerialize")]
    public int MemoryPackSerializePlayers()
    {
        _memoryPackBuffer.ResetWrittenCount();
        MemoryPackSerializer.Serialize(_memoryPackBuffer, Players);
        return _memoryPackBuffer.WrittenCount;
    }

    [Benchmark(Baseline = true), BenchmarkCategory("OrdersSerialize")]
    public int NinoSerializeOrders()
    {
        _ninoBuffer.ResetWrittenCount();
        NinoSerializer.Serialize(Orders, _ninoBuffer);
        return _ninoBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("OrdersSerialize")]
    public int MemoryPackSerializeOrders()
    {
        _memoryPackBuffer.ResetWrittenCount();
        MemoryPackSerializer.Serialize(_memoryPackBuffer, Orders);
        return _memoryPackBuffer.WrittenCount;
    }

    [Benchmark(Baseline = true), BenchmarkCategory("ShapesSerialize")]
    public int NinoSerializeShapes()
    {
        _ninoBuffer.ResetWrittenCount();
        NinoSerializer.Serialize(Shapes, _ninoBuffer);
        return _ninoBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("ShapesSerialize")]
    public int MemoryPackSerializeShapes()
    {
        _memoryPackBuffer.ResetWrittenCount();
        MemoryPackSerializer.Serialize(_memoryPackBuffer, Shapes);
        return _memoryPackBuffer.WrittenCount;
    }

    [Benchmark(Baseline = true), BenchmarkCategory("TreeSerialize")]
    public int NinoSerializeTree()
    {
        _ninoBuffer.ResetWrittenCount();
        NinoSerializer.Serialize(Tree, _ninoBuffer);
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
        NinoSerializer.Serialize(Inventory, _ninoBuffer);
        return _ninoBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("InventorySerialize")]
    public int MemoryPackSerializeInventory()
    {
        _memoryPackBuffer.ResetWrittenCount();
        MemoryPackSerializer.Serialize(_memoryPackBuffer, Inventory);
        return _memoryPackBuffer.WrittenCount;
    }

    [Benchmark(Baseline = true), BenchmarkCategory("StringDictionarySerialize")]
    public int NinoSerializeStringDictionary()
    {
        _ninoBuffer.ResetWrittenCount();
        NinoSerializer.Serialize(Strings, _ninoBuffer);
        return _ninoBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("StringDictionarySerialize")]
    public int MemoryPackSerializeStringDictionary()
    {
        _memoryPackBuffer.ResetWrittenCount();
        MemoryPackSerializer.Serialize(_memoryPackBuffer, Strings);
        return _memoryPackBuffer.WrittenCount;
    }

    [Benchmark(Baseline = true), BenchmarkCategory("PlayersDeserialize")]
    public List<Player> NinoDeserializePlayers() => NinoDeserializer.Deserialize<List<Player>>(NinoPlayers);

    [Benchmark, BenchmarkCategory("PlayersDeserialize")]
    public List<Player> MemoryPackDeserializePlayers() => MemoryPackSerializer.Deserialize<List<Player>>(MemoryPackPlayers);

    [Benchmark(Baseline = true), BenchmarkCategory("OrdersDeserialize")]
    public List<Order> NinoDeserializeOrders() => NinoDeserializer.Deserialize<List<Order>>(NinoOrders);

    [Benchmark, BenchmarkCategory("OrdersDeserialize")]
    public List<Order> MemoryPackDeserializeOrders() => MemoryPackSerializer.Deserialize<List<Order>>(MemoryPackOrders);

    [Benchmark(Baseline = true), BenchmarkCategory("ShapesDeserialize")]
    public List<Shape> NinoDeserializeShapes() => NinoDeserializer.Deserialize<List<Shape>>(NinoShapes);

    [Benchmark, BenchmarkCategory("ShapesDeserialize")]
    public List<Shape> MemoryPackDeserializeShapes() => MemoryPackSerializer.Deserialize<List<Shape>>(MemoryPackShapes);

    [Benchmark(Baseline = true), BenchmarkCategory("TreeDeserialize")]
    public TreeNode NinoDeserializeTree() => NinoDeserializer.Deserialize<TreeNode>(NinoTree);

    [Benchmark, BenchmarkCategory("TreeDeserialize")]
    public TreeNode MemoryPackDeserializeTree() => MemoryPackSerializer.Deserialize<TreeNode>(MemoryPackTree);

    [Benchmark(Baseline = true), BenchmarkCategory("InventoryDeserialize")]
    public InventoryItem[] NinoDeserializeInventory() => NinoDeserializer.Deserialize<InventoryItem[]>(NinoInventory);

    [Benchmark, BenchmarkCategory("InventoryDeserialize")]
    public InventoryItem[] MemoryPackDeserializeInventory() =>
        MemoryPackSerializer.Deserialize<InventoryItem[]>(MemoryPackInventory);

    [Benchmark(Baseline = true), BenchmarkCategory("StringDictionaryDeserialize")]
    public Dictionary<string, string> NinoDeserializeStringDictionary() =>
        NinoDeserializer.Deserialize<Dictionary<string, string>>(NinoStrings);

    [Benchmark, BenchmarkCategory("StringDictionaryDeserialize")]
    public Dictionary<string, string> MemoryPackDeserializeStringDictionary() =>
        MemoryPackSerializer.Deserialize<Dictionary<string, string>>(MemoryPackStrings);
}
