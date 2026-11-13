using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using Google.Protobuf;
using MemoryPack;
using MessagePack;
using Nino.Core;
using ProtoBuf;
using P = Nino.Benchmark.Proto;

#nullable disable
namespace Nino.Benchmark;

/// <summary>
/// Collections of the base class library with 1,000 elements each, serialized to a reused buffer writer and
/// deserialized into new instances: a list of tuples (packed by Nino, copied with padding by MemoryPack), an array
/// of key-value pairs, a dictionary of ints, and the hashed, queued, linked, sorted and concurrent collections, whose fast paths depend on
/// the runtime's collection layouts and on SIMD. Against MemoryPack, MessagePack and protobuf-net. Also a list that
/// holds itself (<see cref="SelfTree"/>, 5,461 lists), which counts the depth at every level: against MemoryPack only
/// (MessagePack fails to read it back, protobuf-net has no list of lists). And nullable values with every fifth one null
/// (<c>long?[]</c>, <c>List&lt;int?&gt;</c>, <c>decimal?[]</c>), which Nino writes as a fixed stride with each absent value
/// zeroed: against every library. MemoryPack writes them as <c>SafeNullable&lt;T&gt;</c> (no uninitialized padding in
/// its bytes, <see cref="NullableFieldsMemoryPack"/>); protobuf, which has no null in a repeated field, as a message per
/// element with an optional value (protobuf-net's <c>[NullWrappedValue]</c>, the contracts in CaseFormats.cs, whose
/// messages Google.Protobuf reads, parsed from protobuf-net's bytes outside the timed region); System.Text.Json source
/// generated, UTF-8.
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

    private static readonly SelfTree SelfTree = SelfTree.Create(6, 4);

    private static readonly byte[] NinoTupleListBytes = NinoSerializer.Serialize(TupleList);
    private static readonly byte[] MessagePackTupleListBytes = MessagePackSerializer.Serialize(TupleList);
    private static readonly byte[] ProtobufTupleListBytes = ProtobufModel.Serialize(TupleList);
    private static readonly byte[] MemoryPackTupleListBytes = MemoryPackSerializer.Serialize(TupleList);
    private static readonly byte[] NinoKeyValuePairsBytes = NinoSerializer.Serialize(KeyValuePairs);
    private static readonly byte[] MessagePackKeyValuePairsBytes = MessagePackSerializer.Serialize(KeyValuePairs);
    private static readonly byte[] ProtobufKeyValuePairsBytes = ProtobufModel.Serialize(KeyValuePairs);
    private static readonly byte[] MemoryPackKeyValuePairsBytes = MemoryPackSerializer.Serialize(KeyValuePairs);
    private static readonly byte[] NinoHashSetBytes = NinoSerializer.Serialize(HashSet);
    private static readonly byte[] MessagePackHashSetBytes = MessagePackSerializer.Serialize(HashSet);
    private static readonly byte[] ProtobufHashSetBytes = ProtobufModel.Serialize(HashSet);
    private static readonly byte[] MemoryPackHashSetBytes = MemoryPackSerializer.Serialize(HashSet);
    private static readonly byte[] NinoIntDictionaryBytes = NinoSerializer.Serialize(IntDictionary);
    private static readonly byte[] MessagePackIntDictionaryBytes = MessagePackSerializer.Serialize(IntDictionary);
    private static readonly byte[] ProtobufIntDictionaryBytes = ProtobufModel.Serialize(IntDictionary);
    private static readonly byte[] MemoryPackIntDictionaryBytes = MemoryPackSerializer.Serialize(IntDictionary);
    private static readonly byte[] NinoQueueBytes = NinoSerializer.Serialize(Queue);
    private static readonly byte[] MessagePackQueueBytes = MessagePackSerializer.Serialize(Queue);
    private static readonly byte[] ProtobufQueueBytes = ProtobufModel.Serialize(Queue);
    private static readonly byte[] MemoryPackQueueBytes = MemoryPackSerializer.Serialize(Queue);
    private static readonly byte[] NinoLinkedListBytes = NinoSerializer.Serialize(LinkedList);
    private static readonly byte[] MessagePackLinkedListBytes = MessagePackSerializer.Serialize(LinkedList);
    private static readonly byte[] ProtobufLinkedListBytes = ProtobufModel.Serialize(LinkedList);
    private static readonly byte[] MemoryPackLinkedListBytes = MemoryPackSerializer.Serialize(LinkedList);
    private static readonly byte[] NinoSortedSetBytes = NinoSerializer.Serialize(SortedSet);
    private static readonly byte[] MessagePackSortedSetBytes = MessagePackSerializer.Serialize(SortedSet);
    private static readonly byte[] ProtobufSortedSetBytes = ProtobufModel.Serialize(SortedSet);
    private static readonly byte[] MemoryPackSortedSetBytes = MemoryPackSerializer.Serialize(SortedSet);
    private static readonly byte[] NinoSortedDictionaryBytes = NinoSerializer.Serialize(SortedDictionary);
    private static readonly byte[] MessagePackSortedDictionaryBytes = MessagePackSerializer.Serialize(SortedDictionary);
    private static readonly byte[] ProtobufSortedDictionaryBytes = ProtobufModel.Serialize(SortedDictionary);
    private static readonly byte[] MemoryPackSortedDictionaryBytes = MemoryPackSerializer.Serialize(SortedDictionary);
    private static readonly byte[] NinoSortedListBytes = NinoSerializer.Serialize(SortedList);
    private static readonly byte[] MessagePackSortedListBytes = MessagePackSerializer.Serialize(SortedList);
    private static readonly byte[] ProtobufSortedListBytes = ProtobufModel.Serialize(SortedList);
    private static readonly byte[] MemoryPackSortedListBytes = MemoryPackSerializer.Serialize(SortedList);
    private static readonly byte[] NinoConcurrentDictionaryBytes = NinoSerializer.Serialize(ConcurrentDictionary);
    private static readonly byte[] MessagePackConcurrentDictionaryBytes = MessagePackSerializer.Serialize(ConcurrentDictionary);
    private static readonly byte[] ProtobufConcurrentDictionaryBytes = ProtobufModel.Serialize(ConcurrentDictionary);
    private static readonly byte[] MemoryPackConcurrentDictionaryBytes = MemoryPackSerializer.Serialize(ConcurrentDictionary);

    // MemoryPack's copies (SafeNullable<T>), and protobuf's contracts holding the same collections (CaseFormats.cs).
    private static readonly SafeNullable<long>[] MemoryPackNullableLongs = SafeNullables.From(NullableLongs);
    private static readonly List<SafeNullable<int>> MemoryPackNullableInts = SafeNullables.From(NullableInts);
    private static readonly SafeNullable<decimal>[] MemoryPackNullableDecimals = SafeNullables.From(NullableDecimals);
    private static readonly NullableLongList ProtobufNullableLongs = new() { Items = NullableLongs };
    private static readonly NullableIntList ProtobufNullableInts = new() { Items = NullableInts };
    private static readonly NullableDecimalList ProtobufNullableDecimals = new() { Items = NullableDecimals };

    private static readonly byte[] NinoNullableLongsBytes = NinoSerializer.Serialize(NullableLongs);
    private static readonly byte[] MemoryPackNullableLongsBytes = MemoryPackSerializer.Serialize(MemoryPackNullableLongs);
    private static readonly byte[] MessagePackNullableLongsBytes = MessagePackSerializer.Serialize(NullableLongs);
    private static readonly byte[] ProtobufNullableLongsBytes = ProtobufModel.Serialize(ProtobufNullableLongs);
    private static readonly P.NullableLongList NullableLongsProto = P.NullableLongList.Parser.ParseFrom(ProtobufNullableLongsBytes);
    private static readonly byte[] GoogleProtobufNullableLongsBytes = NullableLongsProto.ToByteArray();
    private static readonly byte[] JsonNullableLongsBytes = JsonSerializer.SerializeToUtf8Bytes(NullableLongs, CaseJson.Default.NullableInt64Array);
    private static readonly byte[] NinoNullableIntsBytes = NinoSerializer.Serialize(NullableInts);
    private static readonly byte[] MemoryPackNullableIntsBytes = MemoryPackSerializer.Serialize(MemoryPackNullableInts);
    private static readonly byte[] MessagePackNullableIntsBytes = MessagePackSerializer.Serialize(NullableInts);
    private static readonly byte[] ProtobufNullableIntsBytes = ProtobufModel.Serialize(ProtobufNullableInts);
    private static readonly P.NullableIntList NullableIntsProto = P.NullableIntList.Parser.ParseFrom(ProtobufNullableIntsBytes);
    private static readonly byte[] GoogleProtobufNullableIntsBytes = NullableIntsProto.ToByteArray();
    private static readonly byte[] JsonNullableIntsBytes = JsonSerializer.SerializeToUtf8Bytes(NullableInts, CaseJson.Default.ListNullableInt32);
    private static readonly byte[] NinoNullableDecimalsBytes = NinoSerializer.Serialize(NullableDecimals);
    private static readonly byte[] MemoryPackNullableDecimalsBytes = MemoryPackSerializer.Serialize(MemoryPackNullableDecimals);
    private static readonly byte[] MessagePackNullableDecimalsBytes = MessagePackSerializer.Serialize(NullableDecimals);
    private static readonly byte[] ProtobufNullableDecimalsBytes = ProtobufModel.Serialize(ProtobufNullableDecimals);
    private static readonly P.NullableDecimalList NullableDecimalsProto = P.NullableDecimalList.Parser.ParseFrom(ProtobufNullableDecimalsBytes);
    private static readonly byte[] GoogleProtobufNullableDecimalsBytes = NullableDecimalsProto.ToByteArray();
    private static readonly byte[] JsonNullableDecimalsBytes = JsonSerializer.SerializeToUtf8Bytes(NullableDecimals, CaseJson.Default.NullableDecimalArray);

    private static readonly byte[] NinoSelfTreeBytes = NinoSerializer.Serialize(SelfTree);
    private static readonly byte[] MemoryPackSelfTreeBytes = MemoryPackSerializer.Serialize(SelfTree);

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
        BenchmarkPayloadRegistry.Register(nameof(MessagePackSerializeTupleList), MessagePackTupleListBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(ProtobufSerializeTupleList), ProtobufTupleListBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(MessagePackSerializeKeyValuePairs), MessagePackKeyValuePairsBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(ProtobufSerializeKeyValuePairs), ProtobufKeyValuePairsBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(MessagePackSerializeHashSet), MessagePackHashSetBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(ProtobufSerializeHashSet), ProtobufHashSetBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(MessagePackSerializeIntDictionary), MessagePackIntDictionaryBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(ProtobufSerializeIntDictionary), ProtobufIntDictionaryBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(MessagePackSerializeQueue), MessagePackQueueBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(ProtobufSerializeQueue), ProtobufQueueBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(MessagePackSerializeLinkedList), MessagePackLinkedListBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(ProtobufSerializeLinkedList), ProtobufLinkedListBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(MessagePackSerializeSortedSet), MessagePackSortedSetBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(ProtobufSerializeSortedSet), ProtobufSortedSetBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(MessagePackSerializeSortedDictionary), MessagePackSortedDictionaryBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(ProtobufSerializeSortedDictionary), ProtobufSortedDictionaryBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(MessagePackSerializeSortedList), MessagePackSortedListBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(ProtobufSerializeSortedList), ProtobufSortedListBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(MessagePackSerializeConcurrentDictionary), MessagePackConcurrentDictionaryBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(ProtobufSerializeConcurrentDictionary), ProtobufConcurrentDictionaryBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(NinoSerializeNullableLongs), NinoNullableLongsBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(MemoryPackSerializeNullableLongs), MemoryPackNullableLongsBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(MessagePackSerializeNullableLongs), MessagePackNullableLongsBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(ProtobufSerializeNullableLongs), ProtobufNullableLongsBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(GoogleProtobufSerializeNullableLongs), GoogleProtobufNullableLongsBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(JsonSerializeNullableLongs), JsonNullableLongsBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(NinoSerializeNullableInts), NinoNullableIntsBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(MemoryPackSerializeNullableInts), MemoryPackNullableIntsBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(MessagePackSerializeNullableInts), MessagePackNullableIntsBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(ProtobufSerializeNullableInts), ProtobufNullableIntsBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(GoogleProtobufSerializeNullableInts), GoogleProtobufNullableIntsBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(JsonSerializeNullableInts), JsonNullableIntsBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(NinoSerializeNullableDecimals), NinoNullableDecimalsBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(MemoryPackSerializeNullableDecimals), MemoryPackNullableDecimalsBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(MessagePackSerializeNullableDecimals), MessagePackNullableDecimalsBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(ProtobufSerializeNullableDecimals), ProtobufNullableDecimalsBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(GoogleProtobufSerializeNullableDecimals), GoogleProtobufNullableDecimalsBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(JsonSerializeNullableDecimals), JsonNullableDecimalsBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(NinoSerializeSelfTree), NinoSelfTreeBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(MemoryPackSerializeSelfTree), MemoryPackSelfTreeBytes.Length);
    }

    private readonly ArrayBufferWriter<byte> _messagePackBuffer = new(1 << 16);
    private readonly MemoryStream _protobufStream = new(1 << 16);
    private readonly ArrayBufferWriter<byte> _googleProtobufBuffer = new(1 << 16);
    private ArrayBufferWriter<byte> _jsonBuffer;
    private Utf8JsonWriter _jsonWriter;

    [GlobalSetup]
    public void Setup()
    {
        _ninoBuffer = new NinoArrayBufferWriter(1 << 16);
        _memoryPackBuffer = new ArrayBufferWriter<byte>(1 << 16);
        ProtoFile.Cases.Check();
        _jsonBuffer = new ArrayBufferWriter<byte>(1 << 16);
        // What JsonSerializer's own writers use: no validation of the writer's calls.
        _jsonWriter = new Utf8JsonWriter(_jsonBuffer, new JsonWriterOptions { SkipValidation = true });
    }

    /// <summary>Verify.cs: MemoryPack's SafeNullable collections and Google.Protobuf's messages as the nullable collections.</summary>
    public object ReadBack(object result, Type model) => result switch
    {
        P.NullableLongList => ((NullableLongList)ProtobufModel.ReadBack(result, typeof(NullableLongList))).Items,
        P.NullableIntList => ((NullableIntList)ProtobufModel.ReadBack(result, typeof(NullableIntList))).Items,
        P.NullableDecimalList => ((NullableDecimalList)ProtobufModel.ReadBack(result, typeof(NullableDecimalList))).Items,
        _ => SafeNullables.ReadBack(result),
    };

    [Benchmark(Baseline = true), BenchmarkCategory("NullableLongsSerialize")]
    public int NinoSerializeNullableLongs()
    {
        _ninoBuffer.ResetWrittenCount();
        NinoSerializer.Serialize(NullableLongs, _ninoBuffer);
        return _ninoBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("NullableLongsSerialize")]
    public int MemoryPackSerializeNullableLongs()
    {
        _memoryPackBuffer.ResetWrittenCount();
        MemoryPackSerializer.Serialize(_memoryPackBuffer, MemoryPackNullableLongs);
        return _memoryPackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("NullableLongsSerialize")]
    public int MessagePackSerializeNullableLongs()
    {
        _messagePackBuffer.ResetWrittenCount();
        MessagePackSerializer.Serialize(_messagePackBuffer, NullableLongs);
        return _messagePackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("NullableLongsSerialize")]
    public int ProtobufSerializeNullableLongs()
    {
        _protobufStream.SetLength(0);
        Serializer.Serialize(_protobufStream, ProtobufNullableLongs);
        return (int)_protobufStream.Length;
    }

    [Benchmark, BenchmarkCategory("NullableLongsSerialize")]
    public int GoogleProtobufSerializeNullableLongs()
    {
        _googleProtobufBuffer.ResetWrittenCount();
        NullableLongsProto.WriteTo(_googleProtobufBuffer);
        return _googleProtobufBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("NullableLongsSerialize")]
    public int JsonSerializeNullableLongs()
    {
        _jsonBuffer.ResetWrittenCount();
        _jsonWriter.Reset(_jsonBuffer);
        JsonSerializer.Serialize(_jsonWriter, NullableLongs, CaseJson.Default.NullableInt64Array);
        return _jsonBuffer.WrittenCount;
    }

    [Benchmark(Baseline = true), BenchmarkCategory("NullableLongsDeserialize")]
    public long?[] NinoDeserializeNullableLongs() =>
        NinoDeserializer.Deserialize<long?[]>(NinoNullableLongsBytes);

    [Benchmark, BenchmarkCategory("NullableLongsDeserialize")]
    public SafeNullable<long>[] MemoryPackDeserializeNullableLongs() =>
        MemoryPackSerializer.Deserialize<SafeNullable<long>[]>(MemoryPackNullableLongsBytes);

    [Benchmark, BenchmarkCategory("NullableLongsDeserialize")]
    public long?[] MessagePackDeserializeNullableLongs() =>
        MessagePackSerializer.Deserialize<long?[]>(MessagePackNullableLongsBytes);

    [Benchmark, BenchmarkCategory("NullableLongsDeserialize")]
    public long?[] ProtobufDeserializeNullableLongs() =>
        Serializer.Deserialize<NullableLongList>(new ReadOnlySpan<byte>(ProtobufNullableLongsBytes)).Items;

    [Benchmark, BenchmarkCategory("NullableLongsDeserialize")]
    public P.NullableLongList GoogleProtobufDeserializeNullableLongs() =>
        P.NullableLongList.Parser.ParseFrom(new ReadOnlySpan<byte>(GoogleProtobufNullableLongsBytes));

    [Benchmark, BenchmarkCategory("NullableLongsDeserialize")]
    public long?[] JsonDeserializeNullableLongs() =>
        JsonSerializer.Deserialize(JsonNullableLongsBytes, CaseJson.Default.NullableInt64Array);

    [Benchmark(Baseline = true), BenchmarkCategory("NullableIntsSerialize")]
    public int NinoSerializeNullableInts()
    {
        _ninoBuffer.ResetWrittenCount();
        NinoSerializer.Serialize(NullableInts, _ninoBuffer);
        return _ninoBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("NullableIntsSerialize")]
    public int MemoryPackSerializeNullableInts()
    {
        _memoryPackBuffer.ResetWrittenCount();
        MemoryPackSerializer.Serialize(_memoryPackBuffer, MemoryPackNullableInts);
        return _memoryPackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("NullableIntsSerialize")]
    public int MessagePackSerializeNullableInts()
    {
        _messagePackBuffer.ResetWrittenCount();
        MessagePackSerializer.Serialize(_messagePackBuffer, NullableInts);
        return _messagePackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("NullableIntsSerialize")]
    public int ProtobufSerializeNullableInts()
    {
        _protobufStream.SetLength(0);
        Serializer.Serialize(_protobufStream, ProtobufNullableInts);
        return (int)_protobufStream.Length;
    }

    [Benchmark, BenchmarkCategory("NullableIntsSerialize")]
    public int GoogleProtobufSerializeNullableInts()
    {
        _googleProtobufBuffer.ResetWrittenCount();
        NullableIntsProto.WriteTo(_googleProtobufBuffer);
        return _googleProtobufBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("NullableIntsSerialize")]
    public int JsonSerializeNullableInts()
    {
        _jsonBuffer.ResetWrittenCount();
        _jsonWriter.Reset(_jsonBuffer);
        JsonSerializer.Serialize(_jsonWriter, NullableInts, CaseJson.Default.ListNullableInt32);
        return _jsonBuffer.WrittenCount;
    }

    [Benchmark(Baseline = true), BenchmarkCategory("NullableIntsDeserialize")]
    public List<int?> NinoDeserializeNullableInts() =>
        NinoDeserializer.Deserialize<List<int?>>(NinoNullableIntsBytes);

    [Benchmark, BenchmarkCategory("NullableIntsDeserialize")]
    public List<SafeNullable<int>> MemoryPackDeserializeNullableInts() =>
        MemoryPackSerializer.Deserialize<List<SafeNullable<int>>>(MemoryPackNullableIntsBytes);

    [Benchmark, BenchmarkCategory("NullableIntsDeserialize")]
    public List<int?> MessagePackDeserializeNullableInts() =>
        MessagePackSerializer.Deserialize<List<int?>>(MessagePackNullableIntsBytes);

    [Benchmark, BenchmarkCategory("NullableIntsDeserialize")]
    public List<int?> ProtobufDeserializeNullableInts() =>
        Serializer.Deserialize<NullableIntList>(new ReadOnlySpan<byte>(ProtobufNullableIntsBytes)).Items;

    [Benchmark, BenchmarkCategory("NullableIntsDeserialize")]
    public P.NullableIntList GoogleProtobufDeserializeNullableInts() =>
        P.NullableIntList.Parser.ParseFrom(new ReadOnlySpan<byte>(GoogleProtobufNullableIntsBytes));

    [Benchmark, BenchmarkCategory("NullableIntsDeserialize")]
    public List<int?> JsonDeserializeNullableInts() =>
        JsonSerializer.Deserialize(JsonNullableIntsBytes, CaseJson.Default.ListNullableInt32);

    [Benchmark(Baseline = true), BenchmarkCategory("NullableDecimalsSerialize")]
    public int NinoSerializeNullableDecimals()
    {
        _ninoBuffer.ResetWrittenCount();
        NinoSerializer.Serialize(NullableDecimals, _ninoBuffer);
        return _ninoBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("NullableDecimalsSerialize")]
    public int MemoryPackSerializeNullableDecimals()
    {
        _memoryPackBuffer.ResetWrittenCount();
        MemoryPackSerializer.Serialize(_memoryPackBuffer, MemoryPackNullableDecimals);
        return _memoryPackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("NullableDecimalsSerialize")]
    public int MessagePackSerializeNullableDecimals()
    {
        _messagePackBuffer.ResetWrittenCount();
        MessagePackSerializer.Serialize(_messagePackBuffer, NullableDecimals);
        return _messagePackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("NullableDecimalsSerialize")]
    public int ProtobufSerializeNullableDecimals()
    {
        _protobufStream.SetLength(0);
        Serializer.Serialize(_protobufStream, ProtobufNullableDecimals);
        return (int)_protobufStream.Length;
    }

    [Benchmark, BenchmarkCategory("NullableDecimalsSerialize")]
    public int GoogleProtobufSerializeNullableDecimals()
    {
        _googleProtobufBuffer.ResetWrittenCount();
        NullableDecimalsProto.WriteTo(_googleProtobufBuffer);
        return _googleProtobufBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("NullableDecimalsSerialize")]
    public int JsonSerializeNullableDecimals()
    {
        _jsonBuffer.ResetWrittenCount();
        _jsonWriter.Reset(_jsonBuffer);
        JsonSerializer.Serialize(_jsonWriter, NullableDecimals, CaseJson.Default.NullableDecimalArray);
        return _jsonBuffer.WrittenCount;
    }

    [Benchmark(Baseline = true), BenchmarkCategory("NullableDecimalsDeserialize")]
    public decimal?[] NinoDeserializeNullableDecimals() =>
        NinoDeserializer.Deserialize<decimal?[]>(NinoNullableDecimalsBytes);

    [Benchmark, BenchmarkCategory("NullableDecimalsDeserialize")]
    public SafeNullable<decimal>[] MemoryPackDeserializeNullableDecimals() =>
        MemoryPackSerializer.Deserialize<SafeNullable<decimal>[]>(MemoryPackNullableDecimalsBytes);

    [Benchmark, BenchmarkCategory("NullableDecimalsDeserialize")]
    public decimal?[] MessagePackDeserializeNullableDecimals() =>
        MessagePackSerializer.Deserialize<decimal?[]>(MessagePackNullableDecimalsBytes);

    [Benchmark, BenchmarkCategory("NullableDecimalsDeserialize")]
    public decimal?[] ProtobufDeserializeNullableDecimals() =>
        Serializer.Deserialize<NullableDecimalList>(new ReadOnlySpan<byte>(ProtobufNullableDecimalsBytes)).Items;

    [Benchmark, BenchmarkCategory("NullableDecimalsDeserialize")]
    public P.NullableDecimalList GoogleProtobufDeserializeNullableDecimals() =>
        P.NullableDecimalList.Parser.ParseFrom(new ReadOnlySpan<byte>(GoogleProtobufNullableDecimalsBytes));

    [Benchmark, BenchmarkCategory("NullableDecimalsDeserialize")]
    public decimal?[] JsonDeserializeNullableDecimals() =>
        JsonSerializer.Deserialize(JsonNullableDecimalsBytes, CaseJson.Default.NullableDecimalArray);

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
    public List<(int, long)> NinoDeserializeTupleList() =>
        NinoDeserializer.Deserialize<List<(int, long)>>(NinoTupleListBytes);

    [Benchmark, BenchmarkCategory("TupleListDeserialize")]
    public List<(int, long)> MemoryPackDeserializeTupleList() =>
        MemoryPackSerializer.Deserialize<List<(int, long)>>(MemoryPackTupleListBytes);

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
    public KeyValuePair<int, int>[] NinoDeserializeKeyValuePairs() =>
        NinoDeserializer.Deserialize<KeyValuePair<int, int>[]>(NinoKeyValuePairsBytes);

    [Benchmark, BenchmarkCategory("KeyValuePairsDeserialize")]
    public KeyValuePair<int, int>[] MemoryPackDeserializeKeyValuePairs() =>
        MemoryPackSerializer.Deserialize<KeyValuePair<int, int>[]>(MemoryPackKeyValuePairsBytes);

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
    public HashSet<int> MemoryPackDeserializeHashSet() =>
        MemoryPackSerializer.Deserialize<HashSet<int>>(MemoryPackHashSetBytes);

    [Benchmark(Baseline = true), BenchmarkCategory("IntDictionarySerialize")]
    public int NinoSerializeIntDictionary()
    {
        _ninoBuffer.ResetWrittenCount();
        NinoSerializer.Serialize(IntDictionary, _ninoBuffer);
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
    public Dictionary<int, int> NinoDeserializeIntDictionary() =>
        NinoDeserializer.Deserialize<Dictionary<int, int>>(NinoIntDictionaryBytes);

    [Benchmark, BenchmarkCategory("IntDictionaryDeserialize")]
    public Dictionary<int, int> MemoryPackDeserializeIntDictionary() =>
        MemoryPackSerializer.Deserialize<Dictionary<int, int>>(MemoryPackIntDictionaryBytes);

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
    public Queue<int> MemoryPackDeserializeQueue() =>
        MemoryPackSerializer.Deserialize<Queue<int>>(MemoryPackQueueBytes);

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
    public LinkedList<int> NinoDeserializeLinkedList() =>
        NinoDeserializer.Deserialize<LinkedList<int>>(NinoLinkedListBytes);

    [Benchmark, BenchmarkCategory("LinkedListDeserialize")]
    public LinkedList<int> MemoryPackDeserializeLinkedList() =>
        MemoryPackSerializer.Deserialize<LinkedList<int>>(MemoryPackLinkedListBytes);

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
    public SortedSet<int> NinoDeserializeSortedSet() =>
        NinoDeserializer.Deserialize<SortedSet<int>>(NinoSortedSetBytes);

    [Benchmark, BenchmarkCategory("SortedSetDeserialize")]
    public SortedSet<int> MemoryPackDeserializeSortedSet() =>
        MemoryPackSerializer.Deserialize<SortedSet<int>>(MemoryPackSortedSetBytes);

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
    public SortedList<int, int> NinoDeserializeSortedList() =>
        NinoDeserializer.Deserialize<SortedList<int, int>>(NinoSortedListBytes);

    [Benchmark, BenchmarkCategory("SortedListDeserialize")]
    public SortedList<int, int> MemoryPackDeserializeSortedList() =>
        MemoryPackSerializer.Deserialize<SortedList<int, int>>(MemoryPackSortedListBytes);

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
    public ConcurrentDictionary<int, int> NinoDeserializeConcurrentDictionary() =>
        NinoDeserializer.Deserialize<ConcurrentDictionary<int, int>>(NinoConcurrentDictionaryBytes);

    [Benchmark, BenchmarkCategory("ConcurrentDictionaryDeserialize")]
    public ConcurrentDictionary<int, int> MemoryPackDeserializeConcurrentDictionary() =>
        MemoryPackSerializer.Deserialize<ConcurrentDictionary<int, int>>(MemoryPackConcurrentDictionaryBytes);

    // MessagePack and protobuf-net.
    [Benchmark, BenchmarkCategory("TupleListSerialize")]
    public int MessagePackSerializeTupleList()
    {
        _messagePackBuffer.ResetWrittenCount();
        MessagePackSerializer.Serialize(_messagePackBuffer, TupleList);
        return _messagePackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("TupleListSerialize")]
    public int ProtobufSerializeTupleList()
    {
        _protobufStream.SetLength(0);
        Serializer.Serialize(_protobufStream, TupleList);
        return (int)_protobufStream.Length;
    }

    [Benchmark, BenchmarkCategory("KeyValuePairsSerialize")]
    public int MessagePackSerializeKeyValuePairs()
    {
        _messagePackBuffer.ResetWrittenCount();
        MessagePackSerializer.Serialize(_messagePackBuffer, KeyValuePairs);
        return _messagePackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("KeyValuePairsSerialize")]
    public int ProtobufSerializeKeyValuePairs()
    {
        _protobufStream.SetLength(0);
        Serializer.Serialize(_protobufStream, KeyValuePairs);
        return (int)_protobufStream.Length;
    }

    [Benchmark, BenchmarkCategory("HashSetSerialize")]
    public int MessagePackSerializeHashSet()
    {
        _messagePackBuffer.ResetWrittenCount();
        MessagePackSerializer.Serialize(_messagePackBuffer, HashSet);
        return _messagePackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("HashSetSerialize")]
    public int ProtobufSerializeHashSet()
    {
        _protobufStream.SetLength(0);
        Serializer.Serialize(_protobufStream, HashSet);
        return (int)_protobufStream.Length;
    }

    [Benchmark, BenchmarkCategory("IntDictionarySerialize")]
    public int MessagePackSerializeIntDictionary()
    {
        _messagePackBuffer.ResetWrittenCount();
        MessagePackSerializer.Serialize(_messagePackBuffer, IntDictionary);
        return _messagePackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("IntDictionarySerialize")]
    public int ProtobufSerializeIntDictionary()
    {
        _protobufStream.SetLength(0);
        Serializer.Serialize(_protobufStream, IntDictionary);
        return (int)_protobufStream.Length;
    }

    [Benchmark, BenchmarkCategory("QueueSerialize")]
    public int MessagePackSerializeQueue()
    {
        _messagePackBuffer.ResetWrittenCount();
        MessagePackSerializer.Serialize(_messagePackBuffer, Queue);
        return _messagePackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("QueueSerialize")]
    public int ProtobufSerializeQueue()
    {
        _protobufStream.SetLength(0);
        Serializer.Serialize(_protobufStream, Queue);
        return (int)_protobufStream.Length;
    }

    [Benchmark, BenchmarkCategory("LinkedListSerialize")]
    public int MessagePackSerializeLinkedList()
    {
        _messagePackBuffer.ResetWrittenCount();
        MessagePackSerializer.Serialize(_messagePackBuffer, LinkedList);
        return _messagePackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("LinkedListSerialize")]
    public int ProtobufSerializeLinkedList()
    {
        _protobufStream.SetLength(0);
        Serializer.Serialize(_protobufStream, LinkedList);
        return (int)_protobufStream.Length;
    }

    [Benchmark, BenchmarkCategory("SortedSetSerialize")]
    public int MessagePackSerializeSortedSet()
    {
        _messagePackBuffer.ResetWrittenCount();
        MessagePackSerializer.Serialize(_messagePackBuffer, SortedSet);
        return _messagePackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("SortedSetSerialize")]
    public int ProtobufSerializeSortedSet()
    {
        _protobufStream.SetLength(0);
        Serializer.Serialize(_protobufStream, SortedSet);
        return (int)_protobufStream.Length;
    }

    [Benchmark, BenchmarkCategory("SortedDictionarySerialize")]
    public int MessagePackSerializeSortedDictionary()
    {
        _messagePackBuffer.ResetWrittenCount();
        MessagePackSerializer.Serialize(_messagePackBuffer, SortedDictionary);
        return _messagePackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("SortedDictionarySerialize")]
    public int ProtobufSerializeSortedDictionary()
    {
        _protobufStream.SetLength(0);
        Serializer.Serialize(_protobufStream, SortedDictionary);
        return (int)_protobufStream.Length;
    }

    [Benchmark, BenchmarkCategory("SortedListSerialize")]
    public int MessagePackSerializeSortedList()
    {
        _messagePackBuffer.ResetWrittenCount();
        MessagePackSerializer.Serialize(_messagePackBuffer, SortedList);
        return _messagePackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("SortedListSerialize")]
    public int ProtobufSerializeSortedList()
    {
        _protobufStream.SetLength(0);
        Serializer.Serialize(_protobufStream, SortedList);
        return (int)_protobufStream.Length;
    }

    [Benchmark, BenchmarkCategory("ConcurrentDictionarySerialize")]
    public int MessagePackSerializeConcurrentDictionary()
    {
        _messagePackBuffer.ResetWrittenCount();
        MessagePackSerializer.Serialize(_messagePackBuffer, ConcurrentDictionary);
        return _messagePackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("ConcurrentDictionarySerialize")]
    public int ProtobufSerializeConcurrentDictionary()
    {
        _protobufStream.SetLength(0);
        Serializer.Serialize(_protobufStream, ConcurrentDictionary);
        return (int)_protobufStream.Length;
    }

    [Benchmark, BenchmarkCategory("TupleListDeserialize")]
    public List<(int, long)> MessagePackDeserializeTupleList() =>
        MessagePackSerializer.Deserialize<List<(int, long)>>(MessagePackTupleListBytes);

    [Benchmark, BenchmarkCategory("TupleListDeserialize")]
    public List<(int, long)> ProtobufDeserializeTupleList() =>
        Serializer.Deserialize<List<(int, long)>>(new ReadOnlySpan<byte>(ProtobufTupleListBytes));

    [Benchmark, BenchmarkCategory("KeyValuePairsDeserialize")]
    public KeyValuePair<int, int>[] MessagePackDeserializeKeyValuePairs() =>
        MessagePackSerializer.Deserialize<KeyValuePair<int, int>[]>(MessagePackKeyValuePairsBytes);

    [Benchmark, BenchmarkCategory("KeyValuePairsDeserialize")]
    public KeyValuePair<int, int>[] ProtobufDeserializeKeyValuePairs() =>
        Serializer.Deserialize<KeyValuePair<int, int>[]>(new ReadOnlySpan<byte>(ProtobufKeyValuePairsBytes));

    [Benchmark, BenchmarkCategory("HashSetDeserialize")]
    public HashSet<int> MessagePackDeserializeHashSet() =>
        MessagePackSerializer.Deserialize<HashSet<int>>(MessagePackHashSetBytes);

    [Benchmark, BenchmarkCategory("HashSetDeserialize")]
    public HashSet<int> ProtobufDeserializeHashSet() =>
        Serializer.Deserialize<HashSet<int>>(new ReadOnlySpan<byte>(ProtobufHashSetBytes));

    [Benchmark, BenchmarkCategory("IntDictionaryDeserialize")]
    public Dictionary<int, int> MessagePackDeserializeIntDictionary() =>
        MessagePackSerializer.Deserialize<Dictionary<int, int>>(MessagePackIntDictionaryBytes);

    [Benchmark, BenchmarkCategory("IntDictionaryDeserialize")]
    public Dictionary<int, int> ProtobufDeserializeIntDictionary() =>
        Serializer.Deserialize<Dictionary<int, int>>(new ReadOnlySpan<byte>(ProtobufIntDictionaryBytes));

    [Benchmark, BenchmarkCategory("QueueDeserialize")]
    public Queue<int> MessagePackDeserializeQueue() =>
        MessagePackSerializer.Deserialize<Queue<int>>(MessagePackQueueBytes);

    [Benchmark, BenchmarkCategory("QueueDeserialize")]
    public Queue<int> ProtobufDeserializeQueue() =>
        Serializer.Deserialize<Queue<int>>(new ReadOnlySpan<byte>(ProtobufQueueBytes));

    [Benchmark, BenchmarkCategory("LinkedListDeserialize")]
    public LinkedList<int> MessagePackDeserializeLinkedList() =>
        MessagePackSerializer.Deserialize<LinkedList<int>>(MessagePackLinkedListBytes);

    [Benchmark, BenchmarkCategory("LinkedListDeserialize")]
    public LinkedList<int> ProtobufDeserializeLinkedList() =>
        Serializer.Deserialize<LinkedList<int>>(new ReadOnlySpan<byte>(ProtobufLinkedListBytes));

    [Benchmark, BenchmarkCategory("SortedSetDeserialize")]
    public SortedSet<int> MessagePackDeserializeSortedSet() =>
        MessagePackSerializer.Deserialize<SortedSet<int>>(MessagePackSortedSetBytes);

    [Benchmark, BenchmarkCategory("SortedSetDeserialize")]
    public SortedSet<int> ProtobufDeserializeSortedSet() =>
        Serializer.Deserialize<SortedSet<int>>(new ReadOnlySpan<byte>(ProtobufSortedSetBytes));

    [Benchmark, BenchmarkCategory("SortedDictionaryDeserialize")]
    public SortedDictionary<int, int> MessagePackDeserializeSortedDictionary() =>
        MessagePackSerializer.Deserialize<SortedDictionary<int, int>>(MessagePackSortedDictionaryBytes);

    [Benchmark, BenchmarkCategory("SortedDictionaryDeserialize")]
    public SortedDictionary<int, int> ProtobufDeserializeSortedDictionary() =>
        Serializer.Deserialize<SortedDictionary<int, int>>(new ReadOnlySpan<byte>(ProtobufSortedDictionaryBytes));

    [Benchmark, BenchmarkCategory("SortedListDeserialize")]
    public SortedList<int, int> MessagePackDeserializeSortedList() =>
        MessagePackSerializer.Deserialize<SortedList<int, int>>(MessagePackSortedListBytes);

    [Benchmark, BenchmarkCategory("SortedListDeserialize")]
    public SortedList<int, int> ProtobufDeserializeSortedList() =>
        Serializer.Deserialize<SortedList<int, int>>(new ReadOnlySpan<byte>(ProtobufSortedListBytes));

    [Benchmark, BenchmarkCategory("ConcurrentDictionaryDeserialize")]
    public ConcurrentDictionary<int, int> MessagePackDeserializeConcurrentDictionary() =>
        MessagePackSerializer.Deserialize<ConcurrentDictionary<int, int>>(MessagePackConcurrentDictionaryBytes);

    [Benchmark, BenchmarkCategory("ConcurrentDictionaryDeserialize")]
    public ConcurrentDictionary<int, int> ProtobufDeserializeConcurrentDictionary() =>
        Serializer.Deserialize<ConcurrentDictionary<int, int>>(new ReadOnlySpan<byte>(ProtobufConcurrentDictionaryBytes));

    [Benchmark(Baseline = true), BenchmarkCategory("SelfTreeSerialize")]
    public int NinoSerializeSelfTree()
    {
        _ninoBuffer.ResetWrittenCount();
        NinoSerializer.Serialize(SelfTree, _ninoBuffer);
        return _ninoBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("SelfTreeSerialize")]
    public int MemoryPackSerializeSelfTree()
    {
        _memoryPackBuffer.ResetWrittenCount();
        MemoryPackSerializer.Serialize(_memoryPackBuffer, SelfTree);
        return _memoryPackBuffer.WrittenCount;
    }

    [Benchmark(Baseline = true), BenchmarkCategory("SelfTreeDeserialize")]
    public SelfTree NinoDeserializeSelfTree() => NinoDeserializer.Deserialize<SelfTree>(NinoSelfTreeBytes);

    [Benchmark, BenchmarkCategory("SelfTreeDeserialize")]
    public SelfTree MemoryPackDeserializeSelfTree() => MemoryPackSerializer.Deserialize<SelfTree>(MemoryPackSelfTreeBytes);
}

/// <summary>A list that holds itself, with no [NinoType] body between its levels (CollectionsTest).</summary>
[MemoryPackable(GenerateType.Collection)]
public sealed partial class SelfTree : List<SelfTree>
{
    /// <summary>A tree <paramref name="depth"/> levels deep, every inner list holding <paramref name="width"/>.</summary>
    public static SelfTree Create(int depth, int width)
    {
        var tree = new SelfTree();
        for (int i = 0; depth > 0 && i < width; i++)
            tree.Add(Create(depth - 1, width));
        return tree;
    }
}
