using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using BenchmarkDotNet.Configs;
using MemoryPack;
using Nino.Benchmark.Workloads;

#nullable disable
namespace Nino.Benchmark;

// Cases/Collections/CollectionsTest.cs ported to Nino 3.3.13 (NuGet), for the v3 build of this project
// (NINO_BENCH_V3=true): the same data, method names and job, Nino's and MemoryPack's cases. Only the Nino calls differ,
// as in V3/SimpleTest.cs: NinoSerializer/NinoDeserializer -> NinoGen.Serializer/NinoGen.Deserializer,
// NinoArrayBufferWriter -> ArrayBufferWriter<byte>; v3's generator writes the overloads of every collection type named
// in the project. The SelfTree cases are left out: v3's generator recurses without end on a list that holds itself
// (SelfTree : List<SelfTree>, Cases/Collections/CollectionsTest.cs): CollectionSerializerGenerator calls its filters
// (Filter.Interface, Joint, Union) on the element type again and again until the compiler process dies with a stack
// overflow, so a project that names the type does not build.

/// <summary>
/// CollectionsTest's collections of the base class library with 1,000 elements each, serialized by Nino v3 to a reused
/// buffer writer and deserialized into new instances: a list of tuples, an array of key-value pairs, a dictionary of
/// ints, the hashed, queued, linked, sorted and concurrent collections, and nullable values with every fifth one null
/// (<c>long?[]</c>, <c>List&lt;int?&gt;</c>, <c>decimal?[]</c>, <c>Vec3?[]</c>), against MemoryPack (the nullable values as
/// <c>SafeNullable&lt;T&gt;</c>, <see cref="NullableFieldsMemoryPack"/>).
/// </summary>
[PayloadColumn]
[HideColumns("StdDev", "RatioSD", "Error")]
[MinColumn, MaxColumn]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[NinoJob]
[GcServer(true)]
[MemoryDiagnoser]
[MarkdownExporterAttribute.GitHub]
public class CollectionsTest : IReadBack
{
    private const int N = 1000;
    private static readonly List<(int, long)> TupleList = Enumerable.Range(0, N).Select(i => (i, (long)i * 31)).ToList();
    private static readonly KeyValuePair<int, int>[] KeyValuePairs = Enumerable.Range(0, N).Select(i => new KeyValuePair<int, int>(i, i * 7)).ToArray();
    private static readonly HashSet<int> HashSet = new(Enumerable.Range(0, N).Select(i => i * 3));
    private static readonly Dictionary<int, int> IntDictionary = Enumerable.Range(0, N).ToDictionary(i => i * 3, i => i * 7);
    private static readonly Queue<int> Queue = new(Enumerable.Range(0, N));
    private static readonly LinkedList<int> LinkedList = new(Enumerable.Range(0, N));
    private static readonly SortedSet<int> SortedSet = new(Enumerable.Range(0, N).Select(i => i * 7));
    private static readonly SortedDictionary<int, int> SortedDictionary =
        new(Enumerable.Range(0, N).ToDictionary(i => i * 7, i => i));
    private static readonly SortedList<int, int> SortedList = new(Enumerable.Range(0, N).ToDictionary(i => i, i => i * 7));
    private static readonly ConcurrentDictionary<int, int> ConcurrentDictionary = new(Enumerable.Range(0, N).ToDictionary(i => i, i => i * 7));

    // Every fifth element null: the raw nullable stride (spec 6.2), 16 bytes (long?), 8 (int?) and 24 (decimal?).
    private static readonly long?[] NullableLongs = Enumerable.Range(0, N).Select(i => i % 5 == 0 ? null : (long?)i * 31).ToArray();
    private static readonly List<int?> NullableInts = Enumerable.Range(0, N).Select(i => i % 5 == 0 ? null : (int?)i * 7).ToList();
    private static readonly decimal?[] NullableDecimals =
        Enumerable.Range(0, N).Select(i => i % 5 == 0 ? null : (decimal?)i / 8).ToArray();
    // A user's own unmanaged struct (the workloads' Vec3, three floats): its Vec3? is 16 bytes with 4-byte alignment,
    // the stride whose flag has 3 bytes of padding and whose value is not a multiple of 8 bytes.
    private static readonly Vec3?[] NullableVec3s =
        Enumerable.Range(0, N).Select(i => i % 5 == 0 ? null : (Vec3?)new Vec3 { X = i * 0.5f, Y = i * 1.25f, Z = -i }).ToArray();

    private static readonly byte[] NinoTupleListBytes = NinoGen.Serializer.Serialize(TupleList);
    private static readonly byte[] MemoryPackTupleListBytes = MemoryPackSerializer.Serialize(TupleList);
    private static readonly byte[] NinoKeyValuePairsBytes = NinoGen.Serializer.Serialize(KeyValuePairs);
    private static readonly byte[] MemoryPackKeyValuePairsBytes = MemoryPackSerializer.Serialize(KeyValuePairs);
    private static readonly byte[] NinoHashSetBytes = NinoGen.Serializer.Serialize(HashSet);
    private static readonly byte[] MemoryPackHashSetBytes = MemoryPackSerializer.Serialize(HashSet);
    private static readonly byte[] NinoIntDictionaryBytes = NinoGen.Serializer.Serialize(IntDictionary);
    private static readonly byte[] MemoryPackIntDictionaryBytes = MemoryPackSerializer.Serialize(IntDictionary);
    private static readonly byte[] NinoQueueBytes = NinoGen.Serializer.Serialize(Queue);
    private static readonly byte[] MemoryPackQueueBytes = MemoryPackSerializer.Serialize(Queue);
    private static readonly byte[] NinoLinkedListBytes = NinoGen.Serializer.Serialize(LinkedList);
    private static readonly byte[] MemoryPackLinkedListBytes = MemoryPackSerializer.Serialize(LinkedList);
    private static readonly byte[] NinoSortedSetBytes = NinoGen.Serializer.Serialize(SortedSet);
    private static readonly byte[] MemoryPackSortedSetBytes = MemoryPackSerializer.Serialize(SortedSet);
    private static readonly byte[] NinoSortedDictionaryBytes = NinoGen.Serializer.Serialize(SortedDictionary);
    private static readonly byte[] MemoryPackSortedDictionaryBytes = MemoryPackSerializer.Serialize(SortedDictionary);
    private static readonly byte[] NinoSortedListBytes = NinoGen.Serializer.Serialize(SortedList);
    private static readonly byte[] MemoryPackSortedListBytes = MemoryPackSerializer.Serialize(SortedList);
    private static readonly byte[] NinoConcurrentDictionaryBytes = NinoGen.Serializer.Serialize(ConcurrentDictionary);
    private static readonly byte[] MemoryPackConcurrentDictionaryBytes = MemoryPackSerializer.Serialize(ConcurrentDictionary);

    // MemoryPack's copies (SafeNullable<T>).
    private static readonly SafeNullable<long>[] MemoryPackNullableLongs = SafeNullables.From(NullableLongs);
    private static readonly List<SafeNullable<int>> MemoryPackNullableInts = SafeNullables.From(NullableInts);
    private static readonly SafeNullable<decimal>[] MemoryPackNullableDecimals = SafeNullables.From(NullableDecimals);
    private static readonly SafeNullable<Vec3>[] MemoryPackNullableVec3s = SafeNullables.From(NullableVec3s);

    private static readonly byte[] NinoNullableLongsBytes = NinoGen.Serializer.Serialize(NullableLongs);
    private static readonly byte[] MemoryPackNullableLongsBytes = MemoryPackSerializer.Serialize(MemoryPackNullableLongs);
    private static readonly byte[] NinoNullableIntsBytes = NinoGen.Serializer.Serialize(NullableInts);
    private static readonly byte[] MemoryPackNullableIntsBytes = MemoryPackSerializer.Serialize(MemoryPackNullableInts);
    private static readonly byte[] NinoNullableDecimalsBytes = NinoGen.Serializer.Serialize(NullableDecimals);
    private static readonly byte[] MemoryPackNullableDecimalsBytes = MemoryPackSerializer.Serialize(MemoryPackNullableDecimals);
    private static readonly byte[] NinoNullableVec3sBytes = NinoGen.Serializer.Serialize(NullableVec3s);
    private static readonly byte[] MemoryPackNullableVec3sBytes = MemoryPackSerializer.Serialize(MemoryPackNullableVec3s);

    // v3 3.3.13 has no NinoArrayBufferWriter; its IBufferWriter<byte> overload is the reused-buffer path.
    private ArrayBufferWriter<byte> _ninoBuffer;
    private ArrayBufferWriter<byte> _memoryPackBuffer;

    static CollectionsTest()
    {
        BenchmarkPayloadRegistry.Register(nameof(NinoSerializeTupleList), NinoTupleListBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(MemoryPackSerializeTupleList), MemoryPackTupleListBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(NinoSerializeKeyValuePairs), NinoKeyValuePairsBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(MemoryPackSerializeKeyValuePairs), MemoryPackKeyValuePairsBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(NinoSerializeHashSet), NinoHashSetBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(MemoryPackSerializeHashSet), MemoryPackHashSetBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(NinoSerializeIntDictionary), NinoIntDictionaryBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(MemoryPackSerializeIntDictionary), MemoryPackIntDictionaryBytes.Length);
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
        BenchmarkPayloadRegistry.Register(nameof(NinoSerializeNullableLongs), NinoNullableLongsBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(MemoryPackSerializeNullableLongs), MemoryPackNullableLongsBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(NinoSerializeNullableInts), NinoNullableIntsBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(MemoryPackSerializeNullableInts), MemoryPackNullableIntsBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(NinoSerializeNullableDecimals), NinoNullableDecimalsBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(MemoryPackSerializeNullableDecimals), MemoryPackNullableDecimalsBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(NinoSerializeNullableVec3s), NinoNullableVec3sBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(MemoryPackSerializeNullableVec3s), MemoryPackNullableVec3sBytes.Length);
    }

    [GlobalSetup]
    public void Setup()
    {
        _ninoBuffer = new ArrayBufferWriter<byte>(1 << 16);
        _memoryPackBuffer = new ArrayBufferWriter<byte>(1 << 16);
    }

    /// <summary>Verify.cs: MemoryPack's SafeNullable collections as the nullable collections.</summary>
    public object ReadBack(object result, Type model) => SafeNullables.ReadBack(result);

    [Benchmark(Baseline = true), BenchmarkCategory("NullableLongsSerialize")]
    public int NinoSerializeNullableLongs()
    {
        _ninoBuffer.ResetWrittenCount();
        NinoGen.Serializer.Serialize(NullableLongs, _ninoBuffer);
        return _ninoBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("NullableLongsSerialize")]
    public int MemoryPackSerializeNullableLongs()
    {
        _memoryPackBuffer.ResetWrittenCount();
        MemoryPackSerializer.Serialize(_memoryPackBuffer, MemoryPackNullableLongs);
        return _memoryPackBuffer.WrittenCount;
    }

    [Benchmark(Baseline = true), BenchmarkCategory("NullableLongsDeserialize")]
    public long?[] NinoDeserializeNullableLongs()
    {
        NinoGen.Deserializer.Deserialize(NinoNullableLongsBytes, out long?[] ret);
        return ret;
    }

    [Benchmark, BenchmarkCategory("NullableLongsDeserialize")]
    public SafeNullable<long>[] MemoryPackDeserializeNullableLongs() =>
        MemoryPackSerializer.Deserialize<SafeNullable<long>[]>(MemoryPackNullableLongsBytes);

    [Benchmark(Baseline = true), BenchmarkCategory("NullableIntsSerialize")]
    public int NinoSerializeNullableInts()
    {
        _ninoBuffer.ResetWrittenCount();
        NinoGen.Serializer.Serialize(NullableInts, _ninoBuffer);
        return _ninoBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("NullableIntsSerialize")]
    public int MemoryPackSerializeNullableInts()
    {
        _memoryPackBuffer.ResetWrittenCount();
        MemoryPackSerializer.Serialize(_memoryPackBuffer, MemoryPackNullableInts);
        return _memoryPackBuffer.WrittenCount;
    }

    [Benchmark(Baseline = true), BenchmarkCategory("NullableIntsDeserialize")]
    public List<int?> NinoDeserializeNullableInts()
    {
        NinoGen.Deserializer.Deserialize(NinoNullableIntsBytes, out List<int?> ret);
        return ret;
    }

    [Benchmark, BenchmarkCategory("NullableIntsDeserialize")]
    public List<SafeNullable<int>> MemoryPackDeserializeNullableInts() =>
        MemoryPackSerializer.Deserialize<List<SafeNullable<int>>>(MemoryPackNullableIntsBytes);

    [Benchmark(Baseline = true), BenchmarkCategory("NullableDecimalsSerialize")]
    public int NinoSerializeNullableDecimals()
    {
        _ninoBuffer.ResetWrittenCount();
        NinoGen.Serializer.Serialize(NullableDecimals, _ninoBuffer);
        return _ninoBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("NullableDecimalsSerialize")]
    public int MemoryPackSerializeNullableDecimals()
    {
        _memoryPackBuffer.ResetWrittenCount();
        MemoryPackSerializer.Serialize(_memoryPackBuffer, MemoryPackNullableDecimals);
        return _memoryPackBuffer.WrittenCount;
    }

    [Benchmark(Baseline = true), BenchmarkCategory("NullableDecimalsDeserialize")]
    public decimal?[] NinoDeserializeNullableDecimals()
    {
        NinoGen.Deserializer.Deserialize(NinoNullableDecimalsBytes, out decimal?[] ret);
        return ret;
    }

    [Benchmark, BenchmarkCategory("NullableDecimalsDeserialize")]
    public SafeNullable<decimal>[] MemoryPackDeserializeNullableDecimals() =>
        MemoryPackSerializer.Deserialize<SafeNullable<decimal>[]>(MemoryPackNullableDecimalsBytes);

    [Benchmark(Baseline = true), BenchmarkCategory("NullableVec3sSerialize")]
    public int NinoSerializeNullableVec3s()
    {
        _ninoBuffer.ResetWrittenCount();
        NinoGen.Serializer.Serialize(NullableVec3s, _ninoBuffer);
        return _ninoBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("NullableVec3sSerialize")]
    public int MemoryPackSerializeNullableVec3s()
    {
        _memoryPackBuffer.ResetWrittenCount();
        MemoryPackSerializer.Serialize(_memoryPackBuffer, MemoryPackNullableVec3s);
        return _memoryPackBuffer.WrittenCount;
    }

    [Benchmark(Baseline = true), BenchmarkCategory("NullableVec3sDeserialize")]
    public Vec3?[] NinoDeserializeNullableVec3s()
    {
        NinoGen.Deserializer.Deserialize(NinoNullableVec3sBytes, out Vec3?[] ret);
        return ret;
    }

    [Benchmark, BenchmarkCategory("NullableVec3sDeserialize")]
    public SafeNullable<Vec3>[] MemoryPackDeserializeNullableVec3s() =>
        MemoryPackSerializer.Deserialize<SafeNullable<Vec3>[]>(MemoryPackNullableVec3sBytes);

    [Benchmark(Baseline = true), BenchmarkCategory("TupleListSerialize")]
    public int NinoSerializeTupleList()
    {
        _ninoBuffer.ResetWrittenCount();
        NinoGen.Serializer.Serialize(TupleList, _ninoBuffer);
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
    public List<(int, long)> NinoDeserializeTupleList()
    {
        NinoGen.Deserializer.Deserialize(NinoTupleListBytes, out List<(int, long)> ret);
        return ret;
    }

    [Benchmark, BenchmarkCategory("TupleListDeserialize")]
    public List<(int, long)> MemoryPackDeserializeTupleList() =>
        MemoryPackSerializer.Deserialize<List<(int, long)>>(MemoryPackTupleListBytes);

    [Benchmark(Baseline = true), BenchmarkCategory("KeyValuePairsSerialize")]
    public int NinoSerializeKeyValuePairs()
    {
        _ninoBuffer.ResetWrittenCount();
        NinoGen.Serializer.Serialize(KeyValuePairs, _ninoBuffer);
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
    public KeyValuePair<int, int>[] NinoDeserializeKeyValuePairs()
    {
        NinoGen.Deserializer.Deserialize(NinoKeyValuePairsBytes, out KeyValuePair<int, int>[] ret);
        return ret;
    }

    [Benchmark, BenchmarkCategory("KeyValuePairsDeserialize")]
    public KeyValuePair<int, int>[] MemoryPackDeserializeKeyValuePairs() =>
        MemoryPackSerializer.Deserialize<KeyValuePair<int, int>[]>(MemoryPackKeyValuePairsBytes);

    [Benchmark(Baseline = true), BenchmarkCategory("HashSetSerialize")]
    public int NinoSerializeHashSet()
    {
        _ninoBuffer.ResetWrittenCount();
        NinoGen.Serializer.Serialize(HashSet, _ninoBuffer);
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
    public HashSet<int> NinoDeserializeHashSet()
    {
        NinoGen.Deserializer.Deserialize(NinoHashSetBytes, out HashSet<int> ret);
        return ret;
    }

    [Benchmark, BenchmarkCategory("HashSetDeserialize")]
    public HashSet<int> MemoryPackDeserializeHashSet() =>
        MemoryPackSerializer.Deserialize<HashSet<int>>(MemoryPackHashSetBytes);

    [Benchmark(Baseline = true), BenchmarkCategory("IntDictionarySerialize")]
    public int NinoSerializeIntDictionary()
    {
        _ninoBuffer.ResetWrittenCount();
        NinoGen.Serializer.Serialize(IntDictionary, _ninoBuffer);
        return _ninoBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("IntDictionarySerialize")]
    public int MemoryPackSerializeIntDictionary()
    {
        _memoryPackBuffer.ResetWrittenCount();
        MemoryPackSerializer.Serialize(_memoryPackBuffer, IntDictionary);
        return _memoryPackBuffer.WrittenCount;
    }

    [Benchmark(Baseline = true), BenchmarkCategory("IntDictionaryDeserialize")]
    public Dictionary<int, int> NinoDeserializeIntDictionary()
    {
        NinoGen.Deserializer.Deserialize(NinoIntDictionaryBytes, out Dictionary<int, int> ret);
        return ret;
    }

    [Benchmark, BenchmarkCategory("IntDictionaryDeserialize")]
    public Dictionary<int, int> MemoryPackDeserializeIntDictionary() =>
        MemoryPackSerializer.Deserialize<Dictionary<int, int>>(MemoryPackIntDictionaryBytes);

    [Benchmark(Baseline = true), BenchmarkCategory("QueueSerialize")]
    public int NinoSerializeQueue()
    {
        _ninoBuffer.ResetWrittenCount();
        NinoGen.Serializer.Serialize(Queue, _ninoBuffer);
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
    public Queue<int> NinoDeserializeQueue()
    {
        NinoGen.Deserializer.Deserialize(NinoQueueBytes, out Queue<int> ret);
        return ret;
    }

    [Benchmark, BenchmarkCategory("QueueDeserialize")]
    public Queue<int> MemoryPackDeserializeQueue() =>
        MemoryPackSerializer.Deserialize<Queue<int>>(MemoryPackQueueBytes);

    [Benchmark(Baseline = true), BenchmarkCategory("LinkedListSerialize")]
    public int NinoSerializeLinkedList()
    {
        _ninoBuffer.ResetWrittenCount();
        NinoGen.Serializer.Serialize(LinkedList, _ninoBuffer);
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
    public LinkedList<int> NinoDeserializeLinkedList()
    {
        NinoGen.Deserializer.Deserialize(NinoLinkedListBytes, out LinkedList<int> ret);
        return ret;
    }

    [Benchmark, BenchmarkCategory("LinkedListDeserialize")]
    public LinkedList<int> MemoryPackDeserializeLinkedList() =>
        MemoryPackSerializer.Deserialize<LinkedList<int>>(MemoryPackLinkedListBytes);

    [Benchmark(Baseline = true), BenchmarkCategory("SortedSetSerialize")]
    public int NinoSerializeSortedSet()
    {
        _ninoBuffer.ResetWrittenCount();
        NinoGen.Serializer.Serialize(SortedSet, _ninoBuffer);
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
    public SortedSet<int> NinoDeserializeSortedSet()
    {
        NinoGen.Deserializer.Deserialize(NinoSortedSetBytes, out SortedSet<int> ret);
        return ret;
    }

    [Benchmark, BenchmarkCategory("SortedSetDeserialize")]
    public SortedSet<int> MemoryPackDeserializeSortedSet() =>
        MemoryPackSerializer.Deserialize<SortedSet<int>>(MemoryPackSortedSetBytes);

    [Benchmark(Baseline = true), BenchmarkCategory("SortedDictionarySerialize")]
    public int NinoSerializeSortedDictionary()
    {
        _ninoBuffer.ResetWrittenCount();
        NinoGen.Serializer.Serialize(SortedDictionary, _ninoBuffer);
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
    public SortedDictionary<int, int> NinoDeserializeSortedDictionary()
    {
        NinoGen.Deserializer.Deserialize(NinoSortedDictionaryBytes, out SortedDictionary<int, int> ret);
        return ret;
    }

    [Benchmark, BenchmarkCategory("SortedDictionaryDeserialize")]
    public SortedDictionary<int, int> MemoryPackDeserializeSortedDictionary() =>
        MemoryPackSerializer.Deserialize<SortedDictionary<int, int>>(MemoryPackSortedDictionaryBytes);

    [Benchmark(Baseline = true), BenchmarkCategory("SortedListSerialize")]
    public int NinoSerializeSortedList()
    {
        _ninoBuffer.ResetWrittenCount();
        NinoGen.Serializer.Serialize(SortedList, _ninoBuffer);
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
    public SortedList<int, int> NinoDeserializeSortedList()
    {
        NinoGen.Deserializer.Deserialize(NinoSortedListBytes, out SortedList<int, int> ret);
        return ret;
    }

    [Benchmark, BenchmarkCategory("SortedListDeserialize")]
    public SortedList<int, int> MemoryPackDeserializeSortedList() =>
        MemoryPackSerializer.Deserialize<SortedList<int, int>>(MemoryPackSortedListBytes);

    [Benchmark(Baseline = true), BenchmarkCategory("ConcurrentDictionarySerialize")]
    public int NinoSerializeConcurrentDictionary()
    {
        _ninoBuffer.ResetWrittenCount();
        NinoGen.Serializer.Serialize(ConcurrentDictionary, _ninoBuffer);
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
    public ConcurrentDictionary<int, int> NinoDeserializeConcurrentDictionary()
    {
        NinoGen.Deserializer.Deserialize(NinoConcurrentDictionaryBytes, out ConcurrentDictionary<int, int> ret);
        return ret;
    }

    [Benchmark, BenchmarkCategory("ConcurrentDictionaryDeserialize")]
    public ConcurrentDictionary<int, int> MemoryPackDeserializeConcurrentDictionary() =>
        MemoryPackSerializer.Deserialize<ConcurrentDictionary<int, int>>(MemoryPackConcurrentDictionaryBytes);
}

