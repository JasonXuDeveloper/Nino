using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BenchmarkDotNet.Configs;
using MemoryPack;
using MessagePack;
using Nino.Core;
using ProtoBuf;

#nullable disable
namespace Nino.Benchmark;

/// <summary>
/// Data shaped like that of common applications (CommonTypes.cs), each serialized to a reused buffer writer and
/// deserialized into new instances: 100 players, 100 orders, 1,000 shapes through their abstract base, a tree of
/// 1,365 nodes, 1,000 padded structs, 1,000 structs of fixed size that are not raw and a dictionary of 1,000 string
/// pairs. Against MemoryPack, MessagePack and protobuf-net (no tree: protobuf cannot tell the leaves' empty child lists
/// from none).
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
    // First, so that the data below is serialized with it (Vector3, DateTimeOffset).
    private static readonly bool ProtobufReady = ProtobufModel.Ready;
    private static readonly List<Player> Players = Enumerable.Range(0, 100).Select(Player.Create).ToList();
    private static readonly List<Order> Orders = Enumerable.Range(0, 100).Select(i => Order.Create(i, new Random(i))).ToList();
    private static readonly List<Shape> Shapes = Enumerable.Range(0, 1000)
        .Select(i => i % 2 == 0 ? (Shape)new Circle { Color = i, Radius = i } : new Rect { Color = i, Width = i, Height = 2 })
        .ToList();
    private static readonly TreeNode Tree = TreeNode.Create(new Random(7), 5);
    private static readonly InventoryItem[] Inventory = Enumerable.Range(0, 1000)
        .Select(i => new InventoryItem { ItemId = i, Count = (short)(i % 100), Bound = i % 3 == 0 }).ToArray();
    private static readonly Waypoint[] Waypoints = Enumerable.Range(0, 1000)
        .Select(i => new Waypoint { X = i * 0.5f, Y = -i, Stamp = (i * 16, (short)(i % 7)), Visited = i % 4 == 0 }).ToArray();
    private static readonly Dictionary<string, string> Strings = Enumerable.Range(0, 1000)
        .ToDictionary(i => "key" + i, i => "value" + i * 7);

    private static readonly byte[] NinoPlayers = NinoSerializer.Serialize(Players);
    private static readonly byte[] MessagePackPlayers = MessagePackSerializer.Serialize(Players);
    private static readonly byte[] ProtobufPlayers = ProtobufModel.Serialize(Players);
    private static readonly byte[] MemoryPackPlayers = MemoryPackSerializer.Serialize(Players);
    private static readonly byte[] NinoOrders = NinoSerializer.Serialize(Orders);
    private static readonly byte[] MessagePackOrders = MessagePackSerializer.Serialize(Orders);
    private static readonly byte[] ProtobufOrders = ProtobufModel.Serialize(Orders);
    private static readonly byte[] MemoryPackOrders = MemoryPackSerializer.Serialize(Orders);
    private static readonly byte[] NinoShapes = NinoSerializer.Serialize(Shapes);
    private static readonly byte[] MessagePackShapes = MessagePackSerializer.Serialize(Shapes);
    private static readonly byte[] ProtobufShapes = ProtobufModel.Serialize(Shapes);
    private static readonly byte[] MemoryPackShapes = MemoryPackSerializer.Serialize(Shapes);
    private static readonly byte[] NinoTree = NinoSerializer.Serialize(Tree);
    private static readonly byte[] MessagePackTree = MessagePackSerializer.Serialize(Tree);
    private static readonly byte[] MemoryPackTree = MemoryPackSerializer.Serialize(Tree);
    private static readonly byte[] NinoInventory = NinoSerializer.Serialize(Inventory);
    private static readonly byte[] MessagePackInventory = MessagePackSerializer.Serialize(Inventory);
    private static readonly byte[] ProtobufInventory = ProtobufModel.Serialize(Inventory);
    private static readonly byte[] MemoryPackInventory = MemoryPackSerializer.Serialize(Inventory);
    private static readonly byte[] NinoWaypoints = NinoSerializer.Serialize(Waypoints);
    private static readonly byte[] MessagePackWaypoints = MessagePackSerializer.Serialize(Waypoints);
    private static readonly byte[] ProtobufWaypoints = ProtobufModel.Serialize(Waypoints);
    private static readonly byte[] MemoryPackWaypoints = MemoryPackSerializer.Serialize(Waypoints);
    private static readonly byte[] NinoStrings = NinoSerializer.Serialize(Strings);
    private static readonly byte[] MessagePackStrings = MessagePackSerializer.Serialize(Strings);
    private static readonly byte[] ProtobufStrings = ProtobufModel.Serialize(Strings);
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
        BenchmarkPayloadRegistry.Register(nameof(NinoSerializeWaypoints), NinoWaypoints.Length);
        BenchmarkPayloadRegistry.Register(nameof(MemoryPackSerializeWaypoints), MemoryPackWaypoints.Length);
        BenchmarkPayloadRegistry.Register(nameof(NinoSerializeStringDictionary), NinoStrings.Length);
        BenchmarkPayloadRegistry.Register(nameof(MemoryPackSerializeStringDictionary), MemoryPackStrings.Length);
        BenchmarkPayloadRegistry.Register(nameof(MessagePackSerializePlayers), MessagePackPlayers.Length);
        BenchmarkPayloadRegistry.Register(nameof(ProtobufSerializePlayers), ProtobufPlayers.Length);
        BenchmarkPayloadRegistry.Register(nameof(MessagePackSerializeOrders), MessagePackOrders.Length);
        BenchmarkPayloadRegistry.Register(nameof(ProtobufSerializeOrders), ProtobufOrders.Length);
        BenchmarkPayloadRegistry.Register(nameof(MessagePackSerializeShapes), MessagePackShapes.Length);
        BenchmarkPayloadRegistry.Register(nameof(ProtobufSerializeShapes), ProtobufShapes.Length);
        BenchmarkPayloadRegistry.Register(nameof(MessagePackSerializeTree), MessagePackTree.Length);
        BenchmarkPayloadRegistry.Register(nameof(MessagePackSerializeInventory), MessagePackInventory.Length);
        BenchmarkPayloadRegistry.Register(nameof(ProtobufSerializeInventory), ProtobufInventory.Length);
        BenchmarkPayloadRegistry.Register(nameof(MessagePackSerializeWaypoints), MessagePackWaypoints.Length);
        BenchmarkPayloadRegistry.Register(nameof(ProtobufSerializeWaypoints), ProtobufWaypoints.Length);
        BenchmarkPayloadRegistry.Register(nameof(MessagePackSerializeStringDictionary), MessagePackStrings.Length);
        BenchmarkPayloadRegistry.Register(nameof(ProtobufSerializeStringDictionary), ProtobufStrings.Length);
    }

    private readonly ArrayBufferWriter<byte> _messagePackBuffer = new(1 << 20);
    private readonly MemoryStream _protobufStream = new(1 << 20);

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

    [Benchmark(Baseline = true), BenchmarkCategory("WaypointsSerialize")]
    public int NinoSerializeWaypoints()
    {
        _ninoBuffer.ResetWrittenCount();
        NinoSerializer.Serialize(Waypoints, _ninoBuffer);
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
    public List<Player> MemoryPackDeserializePlayers() =>
        MemoryPackSerializer.Deserialize<List<Player>>(MemoryPackPlayers);

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

    [Benchmark(Baseline = true), BenchmarkCategory("WaypointsDeserialize")]
    public Waypoint[] NinoDeserializeWaypoints() => NinoDeserializer.Deserialize<Waypoint[]>(NinoWaypoints);

    [Benchmark, BenchmarkCategory("WaypointsDeserialize")]
    public Waypoint[] MemoryPackDeserializeWaypoints() =>
        MemoryPackSerializer.Deserialize<Waypoint[]>(MemoryPackWaypoints);

    [Benchmark(Baseline = true), BenchmarkCategory("StringDictionaryDeserialize")]
    public Dictionary<string, string> NinoDeserializeStringDictionary() =>
        NinoDeserializer.Deserialize<Dictionary<string, string>>(NinoStrings);

    [Benchmark, BenchmarkCategory("StringDictionaryDeserialize")]
    public Dictionary<string, string> MemoryPackDeserializeStringDictionary() =>
        MemoryPackSerializer.Deserialize<Dictionary<string, string>>(MemoryPackStrings);

    // MessagePack and protobuf-net.
    [Benchmark, BenchmarkCategory("PlayersSerialize")]
    public int MessagePackSerializePlayers()
    {
        _messagePackBuffer.ResetWrittenCount();
        MessagePackSerializer.Serialize(_messagePackBuffer, Players);
        return _messagePackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("PlayersSerialize")]
    public int ProtobufSerializePlayers()
    {
        _protobufStream.SetLength(0);
        Serializer.Serialize(_protobufStream, Players);
        return (int)_protobufStream.Length;
    }

    [Benchmark, BenchmarkCategory("OrdersSerialize")]
    public int MessagePackSerializeOrders()
    {
        _messagePackBuffer.ResetWrittenCount();
        MessagePackSerializer.Serialize(_messagePackBuffer, Orders);
        return _messagePackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("OrdersSerialize")]
    public int ProtobufSerializeOrders()
    {
        _protobufStream.SetLength(0);
        Serializer.Serialize(_protobufStream, Orders);
        return (int)_protobufStream.Length;
    }

    [Benchmark, BenchmarkCategory("ShapesSerialize")]
    public int MessagePackSerializeShapes()
    {
        _messagePackBuffer.ResetWrittenCount();
        MessagePackSerializer.Serialize(_messagePackBuffer, Shapes);
        return _messagePackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("ShapesSerialize")]
    public int ProtobufSerializeShapes()
    {
        _protobufStream.SetLength(0);
        Serializer.Serialize(_protobufStream, Shapes);
        return (int)_protobufStream.Length;
    }

    [Benchmark, BenchmarkCategory("TreeSerialize")]
    public int MessagePackSerializeTree()
    {
        _messagePackBuffer.ResetWrittenCount();
        MessagePackSerializer.Serialize(_messagePackBuffer, Tree);
        return _messagePackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("InventorySerialize")]
    public int MessagePackSerializeInventory()
    {
        _messagePackBuffer.ResetWrittenCount();
        MessagePackSerializer.Serialize(_messagePackBuffer, Inventory);
        return _messagePackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("InventorySerialize")]
    public int ProtobufSerializeInventory()
    {
        _protobufStream.SetLength(0);
        Serializer.Serialize(_protobufStream, Inventory);
        return (int)_protobufStream.Length;
    }

    [Benchmark, BenchmarkCategory("WaypointsSerialize")]
    public int MessagePackSerializeWaypoints()
    {
        _messagePackBuffer.ResetWrittenCount();
        MessagePackSerializer.Serialize(_messagePackBuffer, Waypoints);
        return _messagePackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("WaypointsSerialize")]
    public int ProtobufSerializeWaypoints()
    {
        _protobufStream.SetLength(0);
        Serializer.Serialize(_protobufStream, Waypoints);
        return (int)_protobufStream.Length;
    }

    [Benchmark, BenchmarkCategory("StringDictionarySerialize")]
    public int MessagePackSerializeStringDictionary()
    {
        _messagePackBuffer.ResetWrittenCount();
        MessagePackSerializer.Serialize(_messagePackBuffer, Strings);
        return _messagePackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("StringDictionarySerialize")]
    public int ProtobufSerializeStringDictionary()
    {
        _protobufStream.SetLength(0);
        Serializer.Serialize(_protobufStream, Strings);
        return (int)_protobufStream.Length;
    }

    [Benchmark, BenchmarkCategory("PlayersDeserialize")]
    public List<Player> MessagePackDeserializePlayers() =>
        MessagePackSerializer.Deserialize<List<Player>>(MessagePackPlayers);

    [Benchmark, BenchmarkCategory("PlayersDeserialize")]
    public List<Player> ProtobufDeserializePlayers() =>
        Serializer.Deserialize<List<Player>>(new ReadOnlySpan<byte>(ProtobufPlayers));

    [Benchmark, BenchmarkCategory("OrdersDeserialize")]
    public List<Order> MessagePackDeserializeOrders() =>
        MessagePackSerializer.Deserialize<List<Order>>(MessagePackOrders);

    [Benchmark, BenchmarkCategory("OrdersDeserialize")]
    public List<Order> ProtobufDeserializeOrders() =>
        Serializer.Deserialize<List<Order>>(new ReadOnlySpan<byte>(ProtobufOrders));

    [Benchmark, BenchmarkCategory("ShapesDeserialize")]
    public List<Shape> MessagePackDeserializeShapes() =>
        MessagePackSerializer.Deserialize<List<Shape>>(MessagePackShapes);

    [Benchmark, BenchmarkCategory("ShapesDeserialize")]
    public List<Shape> ProtobufDeserializeShapes() =>
        Serializer.Deserialize<List<Shape>>(new ReadOnlySpan<byte>(ProtobufShapes));

    [Benchmark, BenchmarkCategory("TreeDeserialize")]
    public TreeNode MessagePackDeserializeTree() => MessagePackSerializer.Deserialize<TreeNode>(MessagePackTree);

    [Benchmark, BenchmarkCategory("InventoryDeserialize")]
    public InventoryItem[] MessagePackDeserializeInventory() =>
        MessagePackSerializer.Deserialize<InventoryItem[]>(MessagePackInventory);

    [Benchmark, BenchmarkCategory("InventoryDeserialize")]
    public InventoryItem[] ProtobufDeserializeInventory() =>
        Serializer.Deserialize<InventoryItem[]>(new ReadOnlySpan<byte>(ProtobufInventory));

    [Benchmark, BenchmarkCategory("WaypointsDeserialize")]
    public Waypoint[] MessagePackDeserializeWaypoints() =>
        MessagePackSerializer.Deserialize<Waypoint[]>(MessagePackWaypoints);

    [Benchmark, BenchmarkCategory("WaypointsDeserialize")]
    public Waypoint[] ProtobufDeserializeWaypoints() =>
        Serializer.Deserialize<Waypoint[]>(new ReadOnlySpan<byte>(ProtobufWaypoints));

    [Benchmark, BenchmarkCategory("StringDictionaryDeserialize")]
    public Dictionary<string, string> MessagePackDeserializeStringDictionary() =>
        MessagePackSerializer.Deserialize<Dictionary<string, string>>(MessagePackStrings);

    [Benchmark, BenchmarkCategory("StringDictionaryDeserialize")]
    public Dictionary<string, string> ProtobufDeserializeStringDictionary() =>
        Serializer.Deserialize<Dictionary<string, string>>(new ReadOnlySpan<byte>(ProtobufStrings));
}
