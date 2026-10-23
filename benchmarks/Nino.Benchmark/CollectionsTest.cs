using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using MemoryPack;
using Nino.Core;

#nullable disable
namespace Nino.Benchmark;

/// <summary>
/// Collections of the base class library with 1,000 elements each, serialized to a reused buffer writer and
/// deserialized into new instances: a list of tuples (packed by Nino, copied with padding by MemoryPack), an array
/// of key-value pairs, and the hashed, queued, linked, sorted and concurrent collections, whose fast paths depend on
/// the runtime's collection layouts and on SIMD.
/// </summary>
[PayloadColumn]
[HideColumns("StdDev", "RatioSD", "Error")]
[MinColumn, MaxColumn]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[NinoJob]
[GcServer(true)]
[MemoryDiagnoser]
[MarkdownExporterAttribute.GitHub]
public class CollectionsTest
{
    private const int N = 1000;
    private static readonly List<(int, long)> TupleList = Enumerable.Range(0, N).Select(i => (i, (long)i * 31)).ToList();
    private static readonly KeyValuePair<int, int>[] KeyValuePairs = Enumerable.Range(0, N).Select(i => new KeyValuePair<int, int>(i, i * 7)).ToArray();
    private static readonly HashSet<int> HashSet = new HashSet<int>(Enumerable.Range(0, N).Select(i => i * 3));
    private static readonly Queue<int> Queue = new Queue<int>(Enumerable.Range(0, N));
    private static readonly LinkedList<int> LinkedList = new LinkedList<int>(Enumerable.Range(0, N));
    private static readonly SortedSet<int> SortedSet = new SortedSet<int>(Enumerable.Range(0, N).Select(i => i * 7));
    private static readonly SortedDictionary<int, int> SortedDictionary =
        new SortedDictionary<int, int>(Enumerable.Range(0, N).ToDictionary(i => i * 7, i => i));
    private static readonly SortedList<int, int> SortedList = new SortedList<int, int>(Enumerable.Range(0, N).ToDictionary(i => i, i => i * 7));
    private static readonly ConcurrentDictionary<int, int> ConcurrentDictionary = new ConcurrentDictionary<int, int>(Enumerable.Range(0, N).ToDictionary(i => i, i => i * 7));

    private static readonly byte[] NinoTupleListBytes = NinoSerializer.Serialize(TupleList);
    private static readonly byte[] MemoryPackTupleListBytes = MemoryPackSerializer.Serialize(TupleList);
    private static readonly byte[] NinoKeyValuePairsBytes = NinoSerializer.Serialize(KeyValuePairs);
    private static readonly byte[] MemoryPackKeyValuePairsBytes = MemoryPackSerializer.Serialize(KeyValuePairs);
    private static readonly byte[] NinoHashSetBytes = NinoSerializer.Serialize(HashSet);
    private static readonly byte[] MemoryPackHashSetBytes = MemoryPackSerializer.Serialize(HashSet);
    private static readonly byte[] NinoQueueBytes = NinoSerializer.Serialize(Queue);
    private static readonly byte[] MemoryPackQueueBytes = MemoryPackSerializer.Serialize(Queue);
    private static readonly byte[] NinoLinkedListBytes = NinoSerializer.Serialize(LinkedList);
    private static readonly byte[] MemoryPackLinkedListBytes = MemoryPackSerializer.Serialize(LinkedList);
    private static readonly byte[] NinoSortedSetBytes = NinoSerializer.Serialize(SortedSet);
    private static readonly byte[] MemoryPackSortedSetBytes = MemoryPackSerializer.Serialize(SortedSet);
    private static readonly byte[] NinoSortedDictionaryBytes = NinoSerializer.Serialize(SortedDictionary);
    private static readonly byte[] MemoryPackSortedDictionaryBytes = MemoryPackSerializer.Serialize(SortedDictionary);
    private static readonly byte[] NinoSortedListBytes = NinoSerializer.Serialize(SortedList);
    private static readonly byte[] MemoryPackSortedListBytes = MemoryPackSerializer.Serialize(SortedList);
    private static readonly byte[] NinoConcurrentDictionaryBytes = NinoSerializer.Serialize(ConcurrentDictionary);
    private static readonly byte[] MemoryPackConcurrentDictionaryBytes = MemoryPackSerializer.Serialize(ConcurrentDictionary);

    private NinoArrayBufferWriter _ninoBuffer;
    private ArrayBufferWriter<byte> _memoryPackBuffer;

    static CollectionsTest()
    {
        BenchmarkPayloadRegistry.Register(nameof(NinoSerializeTupleList), NinoTupleListBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(MemoryPackSerializeTupleList), MemoryPackTupleListBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(NinoSerializeKeyValuePairs), NinoKeyValuePairsBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(MemoryPackSerializeKeyValuePairs), MemoryPackKeyValuePairsBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(NinoSerializeHashSet), NinoHashSetBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(MemoryPackSerializeHashSet), MemoryPackHashSetBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(NinoSerializeQueue), NinoQueueBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(MemoryPackSerializeQueue), MemoryPackQueueBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(NinoSerializeLinkedList), NinoLinkedListBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(MemoryPackSerializeLinkedList), MemoryPackLinkedListBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(NinoSerializeSortedSet), NinoSortedSetBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(MemoryPackSerializeSortedSet), MemoryPackSortedSetBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(NinoSerializeSortedDictionary), NinoSortedDictionaryBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(MemoryPackSerializeSortedDictionary), MemoryPackSortedDictionaryBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(NinoSerializeSortedList), NinoSortedListBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(MemoryPackSerializeSortedList), MemoryPackSortedListBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(NinoSerializeConcurrentDictionary), NinoConcurrentDictionaryBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(MemoryPackSerializeConcurrentDictionary), MemoryPackConcurrentDictionaryBytes.Length);
    }

    [GlobalSetup]
    public void Setup()
    {
        _ninoBuffer = new NinoArrayBufferWriter(1 << 16);
        _memoryPackBuffer = new ArrayBufferWriter<byte>(1 << 16);
    }

    [Benchmark(Baseline = true), BenchmarkCategory("TupleListSerialize")]
    public int NinoSerializeTupleList()
    {
        _ninoBuffer.ResetWrittenCount();
        NinoSerializer.Serialize(TupleList, _ninoBuffer);
        return _ninoBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("TupleListSerialize")]
    public int MemoryPackSerializeTupleList()
    {
        _memoryPackBuffer.ResetWrittenCount();
        MemoryPackSerializer.Serialize(_memoryPackBuffer, TupleList);
        return _memoryPackBuffer.WrittenCount;
    }

    [Benchmark(Baseline = true), BenchmarkCategory("TupleListDeserialize")]
    public List<(int, long)> NinoDeserializeTupleList() => NinoDeserializer.Deserialize<List<(int, long)>>(NinoTupleListBytes);

    [Benchmark, BenchmarkCategory("TupleListDeserialize")]
    public List<(int, long)> MemoryPackDeserializeTupleList() => MemoryPackSerializer.Deserialize<List<(int, long)>>(MemoryPackTupleListBytes);

    [Benchmark(Baseline = true), BenchmarkCategory("KeyValuePairsSerialize")]
    public int NinoSerializeKeyValuePairs()
    {
        _ninoBuffer.ResetWrittenCount();
        NinoSerializer.Serialize(KeyValuePairs, _ninoBuffer);
        return _ninoBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("KeyValuePairsSerialize")]
    public int MemoryPackSerializeKeyValuePairs()
    {
        _memoryPackBuffer.ResetWrittenCount();
        MemoryPackSerializer.Serialize(_memoryPackBuffer, KeyValuePairs);
        return _memoryPackBuffer.WrittenCount;
    }

    [Benchmark(Baseline = true), BenchmarkCategory("KeyValuePairsDeserialize")]
    public KeyValuePair<int, int>[] NinoDeserializeKeyValuePairs() => NinoDeserializer.Deserialize<KeyValuePair<int, int>[]>(NinoKeyValuePairsBytes);

    [Benchmark, BenchmarkCategory("KeyValuePairsDeserialize")]
    public KeyValuePair<int, int>[] MemoryPackDeserializeKeyValuePairs() => MemoryPackSerializer.Deserialize<KeyValuePair<int, int>[]>(MemoryPackKeyValuePairsBytes);

    [Benchmark(Baseline = true), BenchmarkCategory("HashSetSerialize")]
    public int NinoSerializeHashSet()
    {
        _ninoBuffer.ResetWrittenCount();
        NinoSerializer.Serialize(HashSet, _ninoBuffer);
        return _ninoBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("HashSetSerialize")]
    public int MemoryPackSerializeHashSet()
    {
        _memoryPackBuffer.ResetWrittenCount();
        MemoryPackSerializer.Serialize(_memoryPackBuffer, HashSet);
        return _memoryPackBuffer.WrittenCount;
    }

    [Benchmark(Baseline = true), BenchmarkCategory("HashSetDeserialize")]
    public HashSet<int> NinoDeserializeHashSet() => NinoDeserializer.Deserialize<HashSet<int>>(NinoHashSetBytes);

    [Benchmark, BenchmarkCategory("HashSetDeserialize")]
    public HashSet<int> MemoryPackDeserializeHashSet() => MemoryPackSerializer.Deserialize<HashSet<int>>(MemoryPackHashSetBytes);

    [Benchmark(Baseline = true), BenchmarkCategory("QueueSerialize")]
    public int NinoSerializeQueue()
    {
        _ninoBuffer.ResetWrittenCount();
        NinoSerializer.Serialize(Queue, _ninoBuffer);
        return _ninoBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("QueueSerialize")]
    public int MemoryPackSerializeQueue()
    {
        _memoryPackBuffer.ResetWrittenCount();
        MemoryPackSerializer.Serialize(_memoryPackBuffer, Queue);
        return _memoryPackBuffer.WrittenCount;
    }

    [Benchmark(Baseline = true), BenchmarkCategory("QueueDeserialize")]
    public Queue<int> NinoDeserializeQueue() => NinoDeserializer.Deserialize<Queue<int>>(NinoQueueBytes);

    [Benchmark, BenchmarkCategory("QueueDeserialize")]
    public Queue<int> MemoryPackDeserializeQueue() => MemoryPackSerializer.Deserialize<Queue<int>>(MemoryPackQueueBytes);

    [Benchmark(Baseline = true), BenchmarkCategory("LinkedListSerialize")]
    public int NinoSerializeLinkedList()
    {
        _ninoBuffer.ResetWrittenCount();
        NinoSerializer.Serialize(LinkedList, _ninoBuffer);
        return _ninoBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("LinkedListSerialize")]
    public int MemoryPackSerializeLinkedList()
    {
        _memoryPackBuffer.ResetWrittenCount();
        MemoryPackSerializer.Serialize(_memoryPackBuffer, LinkedList);
        return _memoryPackBuffer.WrittenCount;
    }

    [Benchmark(Baseline = true), BenchmarkCategory("LinkedListDeserialize")]
    public LinkedList<int> NinoDeserializeLinkedList() => NinoDeserializer.Deserialize<LinkedList<int>>(NinoLinkedListBytes);

    [Benchmark, BenchmarkCategory("LinkedListDeserialize")]
    public LinkedList<int> MemoryPackDeserializeLinkedList() => MemoryPackSerializer.Deserialize<LinkedList<int>>(MemoryPackLinkedListBytes);

    [Benchmark(Baseline = true), BenchmarkCategory("SortedSetSerialize")]
    public int NinoSerializeSortedSet()
    {
        _ninoBuffer.ResetWrittenCount();
        NinoSerializer.Serialize(SortedSet, _ninoBuffer);
        return _ninoBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("SortedSetSerialize")]
    public int MemoryPackSerializeSortedSet()
    {
        _memoryPackBuffer.ResetWrittenCount();
        MemoryPackSerializer.Serialize(_memoryPackBuffer, SortedSet);
        return _memoryPackBuffer.WrittenCount;
    }

    [Benchmark(Baseline = true), BenchmarkCategory("SortedSetDeserialize")]
    public SortedSet<int> NinoDeserializeSortedSet() => NinoDeserializer.Deserialize<SortedSet<int>>(NinoSortedSetBytes);

    [Benchmark, BenchmarkCategory("SortedSetDeserialize")]
    public SortedSet<int> MemoryPackDeserializeSortedSet() => MemoryPackSerializer.Deserialize<SortedSet<int>>(MemoryPackSortedSetBytes);

    [Benchmark(Baseline = true), BenchmarkCategory("SortedDictionarySerialize")]
    public int NinoSerializeSortedDictionary()
    {
        _ninoBuffer.ResetWrittenCount();
        NinoSerializer.Serialize(SortedDictionary, _ninoBuffer);
        return _ninoBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("SortedDictionarySerialize")]
    public int MemoryPackSerializeSortedDictionary()
    {
        _memoryPackBuffer.ResetWrittenCount();
        MemoryPackSerializer.Serialize(_memoryPackBuffer, SortedDictionary);
        return _memoryPackBuffer.WrittenCount;
    }

    [Benchmark(Baseline = true), BenchmarkCategory("SortedDictionaryDeserialize")]
    public SortedDictionary<int, int> NinoDeserializeSortedDictionary() =>
        NinoDeserializer.Deserialize<SortedDictionary<int, int>>(NinoSortedDictionaryBytes);

    [Benchmark, BenchmarkCategory("SortedDictionaryDeserialize")]
    public SortedDictionary<int, int> MemoryPackDeserializeSortedDictionary() =>
        MemoryPackSerializer.Deserialize<SortedDictionary<int, int>>(MemoryPackSortedDictionaryBytes);

    [Benchmark(Baseline = true), BenchmarkCategory("SortedListSerialize")]
    public int NinoSerializeSortedList()
    {
        _ninoBuffer.ResetWrittenCount();
        NinoSerializer.Serialize(SortedList, _ninoBuffer);
        return _ninoBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("SortedListSerialize")]
    public int MemoryPackSerializeSortedList()
    {
        _memoryPackBuffer.ResetWrittenCount();
        MemoryPackSerializer.Serialize(_memoryPackBuffer, SortedList);
        return _memoryPackBuffer.WrittenCount;
    }

    [Benchmark(Baseline = true), BenchmarkCategory("SortedListDeserialize")]
    public SortedList<int, int> NinoDeserializeSortedList() => NinoDeserializer.Deserialize<SortedList<int, int>>(NinoSortedListBytes);

    [Benchmark, BenchmarkCategory("SortedListDeserialize")]
    public SortedList<int, int> MemoryPackDeserializeSortedList() => MemoryPackSerializer.Deserialize<SortedList<int, int>>(MemoryPackSortedListBytes);

    [Benchmark(Baseline = true), BenchmarkCategory("ConcurrentDictionarySerialize")]
    public int NinoSerializeConcurrentDictionary()
    {
        _ninoBuffer.ResetWrittenCount();
        NinoSerializer.Serialize(ConcurrentDictionary, _ninoBuffer);
        return _ninoBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("ConcurrentDictionarySerialize")]
    public int MemoryPackSerializeConcurrentDictionary()
    {
        _memoryPackBuffer.ResetWrittenCount();
        MemoryPackSerializer.Serialize(_memoryPackBuffer, ConcurrentDictionary);
        return _memoryPackBuffer.WrittenCount;
    }

    [Benchmark(Baseline = true), BenchmarkCategory("ConcurrentDictionaryDeserialize")]
    public ConcurrentDictionary<int, int> NinoDeserializeConcurrentDictionary() => NinoDeserializer.Deserialize<ConcurrentDictionary<int, int>>(NinoConcurrentDictionaryBytes);

    [Benchmark, BenchmarkCategory("ConcurrentDictionaryDeserialize")]
    public ConcurrentDictionary<int, int> MemoryPackDeserializeConcurrentDictionary() => MemoryPackSerializer.Deserialize<ConcurrentDictionary<int, int>>(MemoryPackConcurrentDictionaryBytes);
}
