using System;
using System.Buffers;
using System.IO;
using BenchmarkDotNet.Configs;
using MemoryPack;
using MessagePack;
using Nino.Core;
using ProtoBuf;

#nullable disable
namespace Nino.Benchmark;

/// <summary>A small class with a string, for <see cref="ScatteredTest"/>.</summary>
[NinoType]
[MemoryPackable]
[MessagePackObject]
[ProtoContract]
public sealed partial class ScatteredItem
{
    [Key(0)] [ProtoMember(1)] public int Id;
    [Key(1)] [ProtoMember(2)] public string Name;
    [Key(2)] [ProtoMember(3)] public long Value;
}

/// <summary>
/// Arrays of a million objects whose order in the array is not their order in memory, as objects created and replaced
/// over a program's life are: the array is shuffled after its objects were allocated, so each element is a cache miss
/// the hardware prefetcher cannot predict. Every other class allocates its objects in order and fits in the cache.
/// Shapes (CommonTypes.cs, through their abstract base) and <see cref="ScatteredItem"/>s (their names allocated in an
/// order of their own, elsewhere again) are serialized, and the items
/// read into an existing shuffled array (Nino and MemoryPack, which reuse its objects); the same shapes in allocation
/// order show what the hardware prefetcher does alone, and 8,192 of them in order, written again and again, stay in
/// the cache. Against MemoryPack, MessagePack and protobuf-net.
/// </summary>
[PayloadColumn]
[HideColumns("StdDev", "RatioSD", "Error")]
[MinColumn, MaxColumn]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[NinoJob]
[GcServer(true)]
[MemoryDiagnoser]
[MarkdownExporterAttribute.GitHub]
public class ScatteredTest
{
    private const int Count = 1_000_000;
    private const int HotCount = 8192;

    private static readonly Shape[] InOrderShapes = new Shape[Count];
    private static readonly Shape[] ScatteredShapes;
    private static readonly Shape[] HotShapes;
    private static readonly ScatteredItem[] Items = new ScatteredItem[Count];

    private static readonly byte[] NinoItems;
    private static readonly byte[] MemoryPackItems;

    private ScatteredItem[] _ninoTarget;
    private ScatteredItem[] _memoryPackTarget;
    private NinoArrayBufferWriter _ninoBuffer;
    private ArrayBufferWriter<byte> _memoryPackBuffer;
    private ArrayBufferWriter<byte> _messagePackBuffer;
    private MemoryStream _protobufStream;

    static ScatteredTest()
    {
        var random = new Random(20240607);
        for (int i = 0; i < Count; i++)
            InOrderShapes[i] = i % 2 == 0
                ? new Circle { Color = i, Radius = i * 0.5f }
                : new Rect { Color = i, Width = i, Height = -i };
        ScatteredShapes = (Shape[])InOrderShapes.Clone();
        random.Shuffle(ScatteredShapes);
        HotShapes = InOrderShapes.AsSpan(0, HotCount).ToArray();
        for (int i = 0; i < Count; i++)
            Items[i] = new ScatteredItem { Id = i, Value = i * 7L };
        // The names allocated in another order than the items, as members assigned after their object was created.
        int[] order = new int[Count];
        for (int i = 0; i < Count; i++)
            order[i] = i;
        random.Shuffle(order);
        foreach (int i in order)
            Items[i].Name = "item " + i;
        random.Shuffle(Items);

        NinoItems = NinoSerializer.Serialize(Items);
        MemoryPackItems = MemoryPackSerializer.Serialize(Items);

        Register(ScatteredShapes, nameof(NinoSerializeScatteredShapes), nameof(MemoryPackSerializeScatteredShapes),
            nameof(MessagePackSerializeScatteredShapes), nameof(ProtobufSerializeScatteredShapes));
        Register(InOrderShapes, nameof(NinoSerializeInOrderShapes), nameof(MemoryPackSerializeInOrderShapes),
            nameof(MessagePackSerializeInOrderShapes), nameof(ProtobufSerializeInOrderShapes));
        Register(HotShapes, nameof(NinoSerializeHotShapes), nameof(MemoryPackSerializeHotShapes),
            nameof(MessagePackSerializeHotShapes), nameof(ProtobufSerializeHotShapes));
        Register(Items, nameof(NinoSerializeScatteredItems), nameof(MemoryPackSerializeScatteredItems),
            nameof(MessagePackSerializeScatteredItems), nameof(ProtobufSerializeScatteredItems));
    }

    private static void Register<T>(T[] value, string nino, string memoryPack, string messagePack, string protobuf)
    {
        BenchmarkPayloadRegistry.Register(nino, NinoSerializer.Serialize(value).Length);
        BenchmarkPayloadRegistry.Register(memoryPack, MemoryPackSerializer.Serialize(value).Length);
        BenchmarkPayloadRegistry.Register(messagePack, MessagePackSerializer.Serialize(value).Length);
        BenchmarkPayloadRegistry.Register(protobuf, ProtobufModel.Serialize(value).Length);
    }

    [GlobalSetup]
    public void Setup()
    {
        _ninoBuffer = new NinoArrayBufferWriter(Count * 32);
        _memoryPackBuffer = new ArrayBufferWriter<byte>(Count * 32);
        _messagePackBuffer = new ArrayBufferWriter<byte>(Count * 32);
        _protobufStream = new MemoryStream(Count * 32);
        _ninoTarget = Targets(1);
        _memoryPackTarget = Targets(2);
    }

    /// <summary>The objects a read into an existing array reuses, shuffled as <see cref="Items"/> are.</summary>
    private static ScatteredItem[] Targets(int seed)
    {
        var targets = new ScatteredItem[Count];
        for (int i = 0; i < Count; i++)
            targets[i] = new ScatteredItem();
        new Random(seed).Shuffle(targets);
        return targets;
    }

    [Benchmark(Baseline = true), BenchmarkCategory("ScatteredShapesSerialize")]
    public int NinoSerializeScatteredShapes()
    {
        _ninoBuffer.ResetWrittenCount();
        NinoSerializer.Serialize(ScatteredShapes, _ninoBuffer);
        return _ninoBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("ScatteredShapesSerialize")]
    public int MemoryPackSerializeScatteredShapes()
    {
        _memoryPackBuffer.ResetWrittenCount();
        MemoryPackSerializer.Serialize(_memoryPackBuffer, ScatteredShapes);
        return _memoryPackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("ScatteredShapesSerialize")]
    public int MessagePackSerializeScatteredShapes()
    {
        _messagePackBuffer.ResetWrittenCount();
        MessagePackSerializer.Serialize(_messagePackBuffer, ScatteredShapes);
        return _messagePackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("ScatteredShapesSerialize")]
    public int ProtobufSerializeScatteredShapes()
    {
        _protobufStream.SetLength(0);
        Serializer.Serialize(_protobufStream, ScatteredShapes);
        return (int)_protobufStream.Length;
    }

    [Benchmark(Baseline = true), BenchmarkCategory("InOrderShapesSerialize")]
    public int NinoSerializeInOrderShapes()
    {
        _ninoBuffer.ResetWrittenCount();
        NinoSerializer.Serialize(InOrderShapes, _ninoBuffer);
        return _ninoBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("InOrderShapesSerialize")]
    public int MemoryPackSerializeInOrderShapes()
    {
        _memoryPackBuffer.ResetWrittenCount();
        MemoryPackSerializer.Serialize(_memoryPackBuffer, InOrderShapes);
        return _memoryPackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("InOrderShapesSerialize")]
    public int MessagePackSerializeInOrderShapes()
    {
        _messagePackBuffer.ResetWrittenCount();
        MessagePackSerializer.Serialize(_messagePackBuffer, InOrderShapes);
        return _messagePackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("InOrderShapesSerialize")]
    public int ProtobufSerializeInOrderShapes()
    {
        _protobufStream.SetLength(0);
        Serializer.Serialize(_protobufStream, InOrderShapes);
        return (int)_protobufStream.Length;
    }

    [Benchmark(Baseline = true), BenchmarkCategory("HotShapesSerialize")]
    public int NinoSerializeHotShapes()
    {
        _ninoBuffer.ResetWrittenCount();
        NinoSerializer.Serialize(HotShapes, _ninoBuffer);
        return _ninoBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("HotShapesSerialize")]
    public int MemoryPackSerializeHotShapes()
    {
        _memoryPackBuffer.ResetWrittenCount();
        MemoryPackSerializer.Serialize(_memoryPackBuffer, HotShapes);
        return _memoryPackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("HotShapesSerialize")]
    public int MessagePackSerializeHotShapes()
    {
        _messagePackBuffer.ResetWrittenCount();
        MessagePackSerializer.Serialize(_messagePackBuffer, HotShapes);
        return _messagePackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("HotShapesSerialize")]
    public int ProtobufSerializeHotShapes()
    {
        _protobufStream.SetLength(0);
        Serializer.Serialize(_protobufStream, HotShapes);
        return (int)_protobufStream.Length;
    }

    [Benchmark(Baseline = true), BenchmarkCategory("ScatteredItemsSerialize")]
    public int NinoSerializeScatteredItems()
    {
        _ninoBuffer.ResetWrittenCount();
        NinoSerializer.Serialize(Items, _ninoBuffer);
        return _ninoBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("ScatteredItemsSerialize")]
    public int MemoryPackSerializeScatteredItems()
    {
        _memoryPackBuffer.ResetWrittenCount();
        MemoryPackSerializer.Serialize(_memoryPackBuffer, Items);
        return _memoryPackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("ScatteredItemsSerialize")]
    public int MessagePackSerializeScatteredItems()
    {
        _messagePackBuffer.ResetWrittenCount();
        MessagePackSerializer.Serialize(_messagePackBuffer, Items);
        return _messagePackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("ScatteredItemsSerialize")]
    public int ProtobufSerializeScatteredItems()
    {
        _protobufStream.SetLength(0);
        Serializer.Serialize(_protobufStream, Items);
        return (int)_protobufStream.Length;
    }

    [Benchmark(Baseline = true), BenchmarkCategory("ScatteredItemsDeserializeInto")]
    public ScatteredItem[] NinoDeserializeScatteredItemsInto()
    {
        NinoDeserializer.Deserialize(NinoItems, ref _ninoTarget);
        return _ninoTarget;
    }

    [Benchmark, BenchmarkCategory("ScatteredItemsDeserializeInto")]
    public ScatteredItem[] MemoryPackDeserializeScatteredItemsInto()
    {
        MemoryPackSerializer.Deserialize(MemoryPackItems, ref _memoryPackTarget);
        return _memoryPackTarget;
    }
}
