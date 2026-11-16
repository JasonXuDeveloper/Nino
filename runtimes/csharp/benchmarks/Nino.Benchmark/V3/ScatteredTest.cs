using System;
using System.Buffers;
using BenchmarkDotNet.Configs;
using MemoryPack;

#nullable disable
namespace Nino.Benchmark;

// Cases/Graphs/ScatteredTest.cs ported to Nino 3.3.13 (NuGet), for the v3 build of this project (NINO_BENCH_V3=true):
// the same types (Fixtures/CommonTypes.cs, Fixtures/ScatteredBenchmarkData.cs), data, method names and job, Nino's and
// MemoryPack's cases. Only the Nino calls differ, as in V3/SimpleTest.cs, and NinoDeserializeScatteredItemsInto reads
// a new array of new items: v3 has no overload that reads into an existing array (its generator writes
// Deserialize(TCollection value, ref Reader reader) only for a collection with Clear and Add) or [NinoType] object
// (only Deserialize(out T value, ...)), so it reads new ones, as MessagePack and protobuf-net do.

/// <summary>
/// ScatteredTest's arrays of a million objects whose order in the array is not their order in memory (and the same
/// shapes in allocation order, and 8,192 of them that stay in the cache), serialized by Nino v3, and the items read
/// back, against MemoryPack, which reads them into an existing shuffled array.
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

    private ScatteredItem[] _memoryPackTarget;
    private ArrayBufferWriter<byte> _ninoBuffer;
    private ArrayBufferWriter<byte> _memoryPackBuffer;

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

        NinoItems = NinoGen.Serializer.Serialize(Items);
        MemoryPackItems = MemoryPackSerializer.Serialize(Items);

        BenchmarkPayloadRegistry.Register(nameof(NinoSerializeScatteredShapes), NinoGen.Serializer.Serialize(ScatteredShapes).Length);
        BenchmarkPayloadRegistry.Register(nameof(MemoryPackSerializeScatteredShapes), MemoryPackSerializer.Serialize(ScatteredShapes).Length);
        BenchmarkPayloadRegistry.Register(nameof(NinoSerializeInOrderShapes), NinoGen.Serializer.Serialize(InOrderShapes).Length);
        BenchmarkPayloadRegistry.Register(nameof(MemoryPackSerializeInOrderShapes), MemoryPackSerializer.Serialize(InOrderShapes).Length);
        BenchmarkPayloadRegistry.Register(nameof(NinoSerializeHotShapes), NinoGen.Serializer.Serialize(HotShapes).Length);
        BenchmarkPayloadRegistry.Register(nameof(MemoryPackSerializeHotShapes), MemoryPackSerializer.Serialize(HotShapes).Length);
        BenchmarkPayloadRegistry.Register(nameof(NinoSerializeScatteredItems), NinoItems.Length);
        BenchmarkPayloadRegistry.Register(nameof(MemoryPackSerializeScatteredItems), MemoryPackItems.Length);
    }

    [GlobalSetup]
    public void Setup()
    {
        _ninoBuffer = new ArrayBufferWriter<byte>(Count * 32);
        _memoryPackBuffer = new ArrayBufferWriter<byte>(Count * 32);
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
        NinoGen.Serializer.Serialize(ScatteredShapes, _ninoBuffer);
        return _ninoBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("ScatteredShapesSerialize")]
    public int MemoryPackSerializeScatteredShapes()
    {
        _memoryPackBuffer.ResetWrittenCount();
        MemoryPackSerializer.Serialize(_memoryPackBuffer, ScatteredShapes);
        return _memoryPackBuffer.WrittenCount;
    }

    [Benchmark(Baseline = true), BenchmarkCategory("InOrderShapesSerialize")]
    public int NinoSerializeInOrderShapes()
    {
        _ninoBuffer.ResetWrittenCount();
        NinoGen.Serializer.Serialize(InOrderShapes, _ninoBuffer);
        return _ninoBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("InOrderShapesSerialize")]
    public int MemoryPackSerializeInOrderShapes()
    {
        _memoryPackBuffer.ResetWrittenCount();
        MemoryPackSerializer.Serialize(_memoryPackBuffer, InOrderShapes);
        return _memoryPackBuffer.WrittenCount;
    }

    [Benchmark(Baseline = true), BenchmarkCategory("HotShapesSerialize")]
    public int NinoSerializeHotShapes()
    {
        _ninoBuffer.ResetWrittenCount();
        NinoGen.Serializer.Serialize(HotShapes, _ninoBuffer);
        return _ninoBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("HotShapesSerialize")]
    public int MemoryPackSerializeHotShapes()
    {
        _memoryPackBuffer.ResetWrittenCount();
        MemoryPackSerializer.Serialize(_memoryPackBuffer, HotShapes);
        return _memoryPackBuffer.WrittenCount;
    }

    [Benchmark(Baseline = true), BenchmarkCategory("ScatteredItemsSerialize")]
    public int NinoSerializeScatteredItems()
    {
        _ninoBuffer.ResetWrittenCount();
        NinoGen.Serializer.Serialize(Items, _ninoBuffer);
        return _ninoBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("ScatteredItemsSerialize")]
    public int MemoryPackSerializeScatteredItems()
    {
        _memoryPackBuffer.ResetWrittenCount();
        MemoryPackSerializer.Serialize(_memoryPackBuffer, Items);
        return _memoryPackBuffer.WrittenCount;
    }

    [Benchmark(Baseline = true), BenchmarkCategory("ScatteredItemsDeserializeInto")]
    public ScatteredItem[] NinoDeserializeScatteredItemsInto()
    {
        // A new array and new items: v3 has no overload that reads into an existing array or [NinoType] object.
        NinoGen.Deserializer.Deserialize(NinoItems, out ScatteredItem[] ret);
        return ret;
    }

    [Benchmark, BenchmarkCategory("ScatteredItemsDeserializeInto")]
    public ScatteredItem[] MemoryPackDeserializeScatteredItemsInto()
    {
        MemoryPackSerializer.Deserialize(MemoryPackItems, ref _memoryPackTarget);
        return _memoryPackTarget;
    }
}

