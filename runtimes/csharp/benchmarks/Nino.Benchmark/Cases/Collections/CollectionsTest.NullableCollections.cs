using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json;
using BenchmarkDotNet.Attributes;
using MemoryPack;
using MessagePack;
using Nino.Core;
using ProtoBuf;

#nullable disable
namespace Nino.Benchmark;

/// <summary>
/// A raw <c>T?</c> as the element of the collections that are neither an array nor a list: 1,000 <c>int?</c> with
/// every fifth one null (<see cref="NullableIntCollections"/>) in a <c>Queue</c>, a <c>HashSet</c> (a set holds null
/// once), an <c>int?[40, 25]</c>, a <c>LinkedList</c>, a <c>Collection</c>, a <c>ConcurrentQueue</c>, a
/// <c>Memory</c> and an <c>IList</c> (a list behind the interface). Nino writes each element as a fixed-size slot in
/// all of them. Every case in every form (<see cref="NullableForms"/>): Nino with <c>T?</c> and with
/// <see cref="NinoNullable{T}"/> elements; MemoryPack with <c>SafeNullable&lt;T&gt;</c> elements the collection
/// already holds, converting to them and back inside the timed region (<see cref="SafeNullables"/>,
/// <see cref="NullableForms"/>: an element at a time), and given <c>T?</c> as it is (unsafe); MessagePack;
/// protobuf-net through <see cref="NullableIntsIn{TCollection}"/> (each element wrapped, as NullableInts) and
/// System.Text.Json where they have the collection:
///  - protobuf-net has no multi-dimensional array and no <c>Memory</c> of anything but bytes;
///  - System.Text.Json has no multi-dimensional array;
///  - Google.Protobuf has one repeated field for every sequence, so each of these would be its NullableInts case again.
/// A library without the collection has no method rather than other data.
/// </summary>
public partial class CollectionsTest
{
    private static Queue<int?> NullableQueueInts;
    private static Queue<NinoNullable<int>> NullableQueueIntsAsNinoNullable;
    private static Queue<SafeNullable<int>> NullableQueueIntsAsSafeNullable;
    private static NullableIntsIn<Queue<int?>> NullableQueueIntsProtobuf;
    private static byte[] NinoNullableQueueIntsBytes, NinoNullableNullableQueueIntsBytes, MemoryPackNullableQueueIntsBytes,
        MemoryPackUnsafeNullableQueueIntsBytes, MessagePackNullableQueueIntsBytes, ProtobufNullableQueueIntsBytes,
        JsonNullableQueueIntsBytes;
    private static HashSet<int?> NullableHashSetInts;
    private static HashSet<NinoNullable<int>> NullableHashSetIntsAsNinoNullable;
    private static HashSet<SafeNullable<int>> NullableHashSetIntsAsSafeNullable;
    private static NullableIntsIn<HashSet<int?>> NullableHashSetIntsProtobuf;
    private static byte[] NinoNullableHashSetIntsBytes, NinoNullableNullableHashSetIntsBytes,
        MemoryPackNullableHashSetIntsBytes, MemoryPackUnsafeNullableHashSetIntsBytes, MessagePackNullableHashSetIntsBytes,
        ProtobufNullableHashSetIntsBytes, JsonNullableHashSetIntsBytes;
    private static int?[,] NullableGridInts;
    private static NinoNullable<int>[,] NullableGridIntsAsNinoNullable;
    private static SafeNullable<int>[,] NullableGridIntsAsSafeNullable;
    private static byte[] NinoNullableGridIntsBytes, NinoNullableNullableGridIntsBytes, MemoryPackNullableGridIntsBytes,
        MemoryPackUnsafeNullableGridIntsBytes, MessagePackNullableGridIntsBytes;
    private static LinkedList<int?> NullableLinkedListInts;
    private static LinkedList<NinoNullable<int>> NullableLinkedListIntsAsNinoNullable;
    private static LinkedList<SafeNullable<int>> NullableLinkedListIntsAsSafeNullable;
    private static NullableIntsIn<LinkedList<int?>> NullableLinkedListIntsProtobuf;
    private static byte[] NinoNullableLinkedListIntsBytes, NinoNullableNullableLinkedListIntsBytes,
        MemoryPackNullableLinkedListIntsBytes, MemoryPackUnsafeNullableLinkedListIntsBytes,
        MessagePackNullableLinkedListIntsBytes, ProtobufNullableLinkedListIntsBytes, JsonNullableLinkedListIntsBytes;
    private static Collection<int?> NullableCollectionInts;
    private static Collection<NinoNullable<int>> NullableCollectionIntsAsNinoNullable;
    private static Collection<SafeNullable<int>> NullableCollectionIntsAsSafeNullable;
    private static NullableIntsIn<Collection<int?>> NullableCollectionIntsProtobuf;
    private static byte[] NinoNullableCollectionIntsBytes, NinoNullableNullableCollectionIntsBytes,
        MemoryPackNullableCollectionIntsBytes, MemoryPackUnsafeNullableCollectionIntsBytes,
        MessagePackNullableCollectionIntsBytes, ProtobufNullableCollectionIntsBytes, JsonNullableCollectionIntsBytes;
    private static ConcurrentQueue<int?> NullableConcurrentQueueInts;
    private static ConcurrentQueue<NinoNullable<int>> NullableConcurrentQueueIntsAsNinoNullable;
    private static ConcurrentQueue<SafeNullable<int>> NullableConcurrentQueueIntsAsSafeNullable;
    private static NullableIntsIn<ConcurrentQueue<int?>> NullableConcurrentQueueIntsProtobuf;
    private static byte[] NinoNullableConcurrentQueueIntsBytes, NinoNullableNullableConcurrentQueueIntsBytes,
        MemoryPackNullableConcurrentQueueIntsBytes, MemoryPackUnsafeNullableConcurrentQueueIntsBytes,
        MessagePackNullableConcurrentQueueIntsBytes, ProtobufNullableConcurrentQueueIntsBytes,
        JsonNullableConcurrentQueueIntsBytes;
    private static Memory<int?> NullableMemoryInts;
    private static Memory<NinoNullable<int>> NullableMemoryIntsAsNinoNullable;
    private static Memory<SafeNullable<int>> NullableMemoryIntsAsSafeNullable;
    private static byte[] NinoNullableMemoryIntsBytes, NinoNullableNullableMemoryIntsBytes,
        MemoryPackNullableMemoryIntsBytes, MemoryPackUnsafeNullableMemoryIntsBytes, MessagePackNullableMemoryIntsBytes,
        JsonNullableMemoryIntsBytes;
    private static IList<int?> NullableIListInts;
    private static IList<NinoNullable<int>> NullableIListIntsAsNinoNullable;
    private static IList<SafeNullable<int>> NullableIListIntsAsSafeNullable;
    private static NullableIntsIn<IList<int?>> NullableIListIntsProtobuf;
    private static byte[] NinoNullableIListIntsBytes, NinoNullableNullableIListIntsBytes, MemoryPackNullableIListIntsBytes,
        MemoryPackUnsafeNullableIListIntsBytes, MessagePackNullableIListIntsBytes, ProtobufNullableIListIntsBytes,
        JsonNullableIListIntsBytes;

    private static void InitNullableCollections()
    {
        NullableQueueInts = NullableIntCollections.Queue();
        NullableHashSetInts = NullableIntCollections.HashSet();
        NullableGridInts = NullableIntCollections.Grid();
        NullableLinkedListInts = NullableIntCollections.LinkedList();
        NullableCollectionInts = NullableIntCollections.Collection();
        NullableConcurrentQueueInts = NullableIntCollections.ConcurrentQueue();
        NullableMemoryInts = NullableIntCollections.Memory();
        NullableIListInts = NullableIntCollections.IList();

        NullableQueueIntsAsNinoNullable = NullableForms.Nino(NullableQueueInts);
        NullableQueueIntsAsSafeNullable = SafeNullables.From(NullableQueueInts);
        NullableQueueIntsProtobuf = new NullableIntsIn<Queue<int?>> { Items = NullableQueueInts };
        NinoNullableQueueIntsBytes = NinoSerializer.Serialize(NullableQueueInts);
        NinoNullableNullableQueueIntsBytes = NinoSerializer.Serialize(NullableQueueIntsAsNinoNullable);
        MemoryPackNullableQueueIntsBytes = MemoryPackSerializer.Serialize(NullableQueueIntsAsSafeNullable);
        MemoryPackUnsafeNullableQueueIntsBytes = MemoryPackSerializer.Serialize(NullableQueueInts);
        MessagePackNullableQueueIntsBytes = MessagePackSerializer.Serialize(NullableQueueInts);
        ProtobufNullableQueueIntsBytes = ProtobufModel.Serialize(NullableQueueIntsProtobuf);
        JsonNullableQueueIntsBytes = JsonSerializer.SerializeToUtf8Bytes(NullableQueueInts, CaseJson.Default.QueueNullableInt32);
        NullableHashSetIntsAsNinoNullable = NullableForms.Nino(NullableHashSetInts);
        NullableHashSetIntsAsSafeNullable = SafeNullables.From(NullableHashSetInts);
        NullableHashSetIntsProtobuf = new NullableIntsIn<HashSet<int?>> { Items = NullableHashSetInts };
        NinoNullableHashSetIntsBytes = NinoSerializer.Serialize(NullableHashSetInts);
        NinoNullableNullableHashSetIntsBytes = NinoSerializer.Serialize(NullableHashSetIntsAsNinoNullable);
        MemoryPackNullableHashSetIntsBytes = MemoryPackSerializer.Serialize(NullableHashSetIntsAsSafeNullable);
        MemoryPackUnsafeNullableHashSetIntsBytes = MemoryPackSerializer.Serialize(NullableHashSetInts);
        MessagePackNullableHashSetIntsBytes = MessagePackSerializer.Serialize(NullableHashSetInts);
        ProtobufNullableHashSetIntsBytes = ProtobufModel.Serialize(NullableHashSetIntsProtobuf);
        JsonNullableHashSetIntsBytes = JsonSerializer.SerializeToUtf8Bytes(NullableHashSetInts, CaseJson.Default.HashSetNullableInt32);
        NullableGridIntsAsNinoNullable = NullableForms.Nino(NullableGridInts);
        NullableGridIntsAsSafeNullable = SafeNullables.From(NullableGridInts);
        NinoNullableGridIntsBytes = NinoSerializer.Serialize(NullableGridInts);
        NinoNullableNullableGridIntsBytes = NinoSerializer.Serialize(NullableGridIntsAsNinoNullable);
        MemoryPackNullableGridIntsBytes = MemoryPackSerializer.Serialize(NullableGridIntsAsSafeNullable);
        MemoryPackUnsafeNullableGridIntsBytes = MemoryPackSerializer.Serialize(NullableGridInts);
        MessagePackNullableGridIntsBytes = MessagePackSerializer.Serialize(NullableGridInts);
        NullableLinkedListIntsAsNinoNullable = NullableForms.Nino(NullableLinkedListInts);
        NullableLinkedListIntsAsSafeNullable = SafeNullables.From(NullableLinkedListInts);
        NullableLinkedListIntsProtobuf = new NullableIntsIn<LinkedList<int?>> { Items = NullableLinkedListInts };
        NinoNullableLinkedListIntsBytes = NinoSerializer.Serialize(NullableLinkedListInts);
        NinoNullableNullableLinkedListIntsBytes = NinoSerializer.Serialize(NullableLinkedListIntsAsNinoNullable);
        MemoryPackNullableLinkedListIntsBytes = MemoryPackSerializer.Serialize(NullableLinkedListIntsAsSafeNullable);
        MemoryPackUnsafeNullableLinkedListIntsBytes = MemoryPackSerializer.Serialize(NullableLinkedListInts);
        MessagePackNullableLinkedListIntsBytes = MessagePackSerializer.Serialize(NullableLinkedListInts);
        ProtobufNullableLinkedListIntsBytes = ProtobufModel.Serialize(NullableLinkedListIntsProtobuf);
        JsonNullableLinkedListIntsBytes = JsonSerializer.SerializeToUtf8Bytes(NullableLinkedListInts, CaseJson.Default.LinkedListNullableInt32);
        NullableCollectionIntsAsNinoNullable = NullableForms.Nino(NullableCollectionInts);
        NullableCollectionIntsAsSafeNullable = SafeNullables.From(NullableCollectionInts);
        NullableCollectionIntsProtobuf = new NullableIntsIn<Collection<int?>> { Items = NullableCollectionInts };
        NinoNullableCollectionIntsBytes = NinoSerializer.Serialize(NullableCollectionInts);
        NinoNullableNullableCollectionIntsBytes = NinoSerializer.Serialize(NullableCollectionIntsAsNinoNullable);
        MemoryPackNullableCollectionIntsBytes = MemoryPackSerializer.Serialize(NullableCollectionIntsAsSafeNullable);
        MemoryPackUnsafeNullableCollectionIntsBytes = MemoryPackSerializer.Serialize(NullableCollectionInts);
        MessagePackNullableCollectionIntsBytes = MessagePackSerializer.Serialize(NullableCollectionInts);
        ProtobufNullableCollectionIntsBytes = ProtobufModel.Serialize(NullableCollectionIntsProtobuf);
        JsonNullableCollectionIntsBytes = JsonSerializer.SerializeToUtf8Bytes(NullableCollectionInts, CaseJson.Default.CollectionNullableInt32);
        NullableConcurrentQueueIntsAsNinoNullable = NullableForms.Nino(NullableConcurrentQueueInts);
        NullableConcurrentQueueIntsAsSafeNullable = SafeNullables.From(NullableConcurrentQueueInts);
        NullableConcurrentQueueIntsProtobuf = new NullableIntsIn<ConcurrentQueue<int?>> { Items = NullableConcurrentQueueInts };
        NinoNullableConcurrentQueueIntsBytes = NinoSerializer.Serialize(NullableConcurrentQueueInts);
        NinoNullableNullableConcurrentQueueIntsBytes = NinoSerializer.Serialize(NullableConcurrentQueueIntsAsNinoNullable);
        MemoryPackNullableConcurrentQueueIntsBytes = MemoryPackSerializer.Serialize(NullableConcurrentQueueIntsAsSafeNullable);
        MemoryPackUnsafeNullableConcurrentQueueIntsBytes = MemoryPackSerializer.Serialize(NullableConcurrentQueueInts);
        MessagePackNullableConcurrentQueueIntsBytes = MessagePackSerializer.Serialize(NullableConcurrentQueueInts);
        ProtobufNullableConcurrentQueueIntsBytes = ProtobufModel.Serialize(NullableConcurrentQueueIntsProtobuf);
        JsonNullableConcurrentQueueIntsBytes = JsonSerializer.SerializeToUtf8Bytes(NullableConcurrentQueueInts, CaseJson.Default.ConcurrentQueueNullableInt32);
        NullableMemoryIntsAsNinoNullable = NullableForms.Nino(NullableMemoryInts);
        NullableMemoryIntsAsSafeNullable = SafeNullables.From(NullableMemoryInts);
        NinoNullableMemoryIntsBytes = NinoSerializer.Serialize(NullableMemoryInts);
        NinoNullableNullableMemoryIntsBytes = NinoSerializer.Serialize(NullableMemoryIntsAsNinoNullable);
        MemoryPackNullableMemoryIntsBytes = MemoryPackSerializer.Serialize(NullableMemoryIntsAsSafeNullable);
        MemoryPackUnsafeNullableMemoryIntsBytes = MemoryPackSerializer.Serialize(NullableMemoryInts);
        MessagePackNullableMemoryIntsBytes = MessagePackSerializer.Serialize(NullableMemoryInts);
        JsonNullableMemoryIntsBytes = JsonSerializer.SerializeToUtf8Bytes(NullableMemoryInts, CaseJson.Default.MemoryNullableInt32);
        NullableIListIntsAsNinoNullable = NullableForms.Nino(NullableIListInts);
        NullableIListIntsAsSafeNullable = SafeNullables.From(NullableIListInts);
        NullableIListIntsProtobuf = new NullableIntsIn<IList<int?>> { Items = NullableIListInts };
        NinoNullableIListIntsBytes = NinoSerializer.Serialize(NullableIListInts);
        NinoNullableNullableIListIntsBytes = NinoSerializer.Serialize(NullableIListIntsAsNinoNullable);
        MemoryPackNullableIListIntsBytes = MemoryPackSerializer.Serialize(NullableIListIntsAsSafeNullable);
        MemoryPackUnsafeNullableIListIntsBytes = MemoryPackSerializer.Serialize(NullableIListInts);
        MessagePackNullableIListIntsBytes = MessagePackSerializer.Serialize(NullableIListInts);
        ProtobufNullableIListIntsBytes = ProtobufModel.Serialize(NullableIListIntsProtobuf);
        JsonNullableIListIntsBytes = JsonSerializer.SerializeToUtf8Bytes(NullableIListInts, CaseJson.Default.IListNullableInt32);

        BenchmarkPayloadRegistry.Register(nameof(NinoSerializeNullableQueueInts), NinoNullableQueueIntsBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(NinoNullableSerializeNullableQueueInts), NinoNullableNullableQueueIntsBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(MemoryPackSerializeNullableQueueInts), MemoryPackNullableQueueIntsBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(MemoryPackConvertedSerializeNullableQueueInts), MemoryPackNullableQueueIntsBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(MemoryPackUnsafeSerializeNullableQueueInts), MemoryPackUnsafeNullableQueueIntsBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(MessagePackSerializeNullableQueueInts), MessagePackNullableQueueIntsBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(ProtobufSerializeNullableQueueInts), ProtobufNullableQueueIntsBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(JsonSerializeNullableQueueInts), JsonNullableQueueIntsBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(NinoSerializeNullableHashSetInts), NinoNullableHashSetIntsBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(NinoNullableSerializeNullableHashSetInts), NinoNullableNullableHashSetIntsBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(MemoryPackSerializeNullableHashSetInts), MemoryPackNullableHashSetIntsBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(MemoryPackConvertedSerializeNullableHashSetInts), MemoryPackNullableHashSetIntsBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(MemoryPackUnsafeSerializeNullableHashSetInts), MemoryPackUnsafeNullableHashSetIntsBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(MessagePackSerializeNullableHashSetInts), MessagePackNullableHashSetIntsBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(ProtobufSerializeNullableHashSetInts), ProtobufNullableHashSetIntsBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(JsonSerializeNullableHashSetInts), JsonNullableHashSetIntsBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(NinoSerializeNullableGridInts), NinoNullableGridIntsBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(NinoNullableSerializeNullableGridInts), NinoNullableNullableGridIntsBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(MemoryPackSerializeNullableGridInts), MemoryPackNullableGridIntsBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(MemoryPackConvertedSerializeNullableGridInts), MemoryPackNullableGridIntsBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(MemoryPackUnsafeSerializeNullableGridInts), MemoryPackUnsafeNullableGridIntsBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(MessagePackSerializeNullableGridInts), MessagePackNullableGridIntsBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(NinoSerializeNullableLinkedListInts), NinoNullableLinkedListIntsBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(NinoNullableSerializeNullableLinkedListInts), NinoNullableNullableLinkedListIntsBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(MemoryPackSerializeNullableLinkedListInts), MemoryPackNullableLinkedListIntsBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(MemoryPackConvertedSerializeNullableLinkedListInts), MemoryPackNullableLinkedListIntsBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(MemoryPackUnsafeSerializeNullableLinkedListInts), MemoryPackUnsafeNullableLinkedListIntsBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(MessagePackSerializeNullableLinkedListInts), MessagePackNullableLinkedListIntsBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(ProtobufSerializeNullableLinkedListInts), ProtobufNullableLinkedListIntsBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(JsonSerializeNullableLinkedListInts), JsonNullableLinkedListIntsBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(NinoSerializeNullableCollectionInts), NinoNullableCollectionIntsBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(NinoNullableSerializeNullableCollectionInts), NinoNullableNullableCollectionIntsBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(MemoryPackSerializeNullableCollectionInts), MemoryPackNullableCollectionIntsBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(MemoryPackConvertedSerializeNullableCollectionInts), MemoryPackNullableCollectionIntsBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(MemoryPackUnsafeSerializeNullableCollectionInts), MemoryPackUnsafeNullableCollectionIntsBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(MessagePackSerializeNullableCollectionInts), MessagePackNullableCollectionIntsBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(ProtobufSerializeNullableCollectionInts), ProtobufNullableCollectionIntsBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(JsonSerializeNullableCollectionInts), JsonNullableCollectionIntsBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(NinoSerializeNullableConcurrentQueueInts), NinoNullableConcurrentQueueIntsBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(NinoNullableSerializeNullableConcurrentQueueInts), NinoNullableNullableConcurrentQueueIntsBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(MemoryPackSerializeNullableConcurrentQueueInts), MemoryPackNullableConcurrentQueueIntsBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(MemoryPackConvertedSerializeNullableConcurrentQueueInts), MemoryPackNullableConcurrentQueueIntsBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(MemoryPackUnsafeSerializeNullableConcurrentQueueInts), MemoryPackUnsafeNullableConcurrentQueueIntsBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(MessagePackSerializeNullableConcurrentQueueInts), MessagePackNullableConcurrentQueueIntsBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(ProtobufSerializeNullableConcurrentQueueInts), ProtobufNullableConcurrentQueueIntsBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(JsonSerializeNullableConcurrentQueueInts), JsonNullableConcurrentQueueIntsBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(NinoSerializeNullableMemoryInts), NinoNullableMemoryIntsBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(NinoNullableSerializeNullableMemoryInts), NinoNullableNullableMemoryIntsBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(MemoryPackSerializeNullableMemoryInts), MemoryPackNullableMemoryIntsBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(MemoryPackConvertedSerializeNullableMemoryInts), MemoryPackNullableMemoryIntsBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(MemoryPackUnsafeSerializeNullableMemoryInts), MemoryPackUnsafeNullableMemoryIntsBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(MessagePackSerializeNullableMemoryInts), MessagePackNullableMemoryIntsBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(JsonSerializeNullableMemoryInts), JsonNullableMemoryIntsBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(NinoSerializeNullableIListInts), NinoNullableIListIntsBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(NinoNullableSerializeNullableIListInts), NinoNullableNullableIListIntsBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(MemoryPackSerializeNullableIListInts), MemoryPackNullableIListIntsBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(MemoryPackConvertedSerializeNullableIListInts), MemoryPackNullableIListIntsBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(MemoryPackUnsafeSerializeNullableIListInts), MemoryPackUnsafeNullableIListIntsBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(MessagePackSerializeNullableIListInts), MessagePackNullableIListIntsBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(ProtobufSerializeNullableIListInts), ProtobufNullableIListIntsBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(JsonSerializeNullableIListInts), JsonNullableIListIntsBytes.Length);
    }

    [Benchmark(Baseline = true), BenchmarkCategory("NullableQueueIntsSerialize")]
    public int NinoSerializeNullableQueueInts()
    {
        _ninoBuffer.ResetWrittenCount();
        NinoSerializer.Serialize(NullableQueueInts, _ninoBuffer);
        return _ninoBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("NullableQueueIntsSerialize")]
    public int NinoNullableSerializeNullableQueueInts()
    {
        _ninoBuffer.ResetWrittenCount();
        NinoSerializer.Serialize(NullableQueueIntsAsNinoNullable, _ninoBuffer);
        return _ninoBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("NullableQueueIntsSerialize")]
    public int MemoryPackSerializeNullableQueueInts()
    {
        _memoryPackBuffer.ResetWrittenCount();
        MemoryPackSerializer.Serialize(_memoryPackBuffer, NullableQueueIntsAsSafeNullable);
        return _memoryPackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("NullableQueueIntsSerialize")]
    public int MemoryPackConvertedSerializeNullableQueueInts()
    {
        _memoryPackBuffer.ResetWrittenCount();
        MemoryPackSerializer.Serialize(_memoryPackBuffer, SafeNullables.From(NullableQueueInts));
        return _memoryPackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("NullableQueueIntsSerialize")]
    public int MemoryPackUnsafeSerializeNullableQueueInts()
    {
        _memoryPackBuffer.ResetWrittenCount();
        MemoryPackSerializer.Serialize(_memoryPackBuffer, NullableQueueInts);
        return _memoryPackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("NullableQueueIntsSerialize")]
    public int MessagePackSerializeNullableQueueInts()
    {
        _messagePackBuffer.ResetWrittenCount();
        MessagePackSerializer.Serialize(_messagePackBuffer, NullableQueueInts);
        return _messagePackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("NullableQueueIntsSerialize")]
    public int ProtobufSerializeNullableQueueInts()
    {
        _protobufStream.SetLength(0);
        Serializer.Serialize(_protobufStream, NullableQueueIntsProtobuf);
        return (int)_protobufStream.Length;
    }

    [Benchmark, BenchmarkCategory("NullableQueueIntsSerialize")]
    public int JsonSerializeNullableQueueInts()
    {
        _jsonBuffer.ResetWrittenCount();
        _jsonWriter.Reset(_jsonBuffer);
        JsonSerializer.Serialize(_jsonWriter, NullableQueueInts, CaseJson.Default.QueueNullableInt32);
        return _jsonBuffer.WrittenCount;
    }

    [Benchmark(Baseline = true), BenchmarkCategory("NullableQueueIntsDeserialize")]
    public Queue<int?> NinoDeserializeNullableQueueInts() =>
        NinoDeserializer.Deserialize<Queue<int?>>(NinoNullableQueueIntsBytes);

    [Benchmark, BenchmarkCategory("NullableQueueIntsDeserialize")]
    public Queue<NinoNullable<int>> NinoNullableDeserializeNullableQueueInts() =>
        NinoDeserializer.Deserialize<Queue<NinoNullable<int>>>(NinoNullableNullableQueueIntsBytes);

    [Benchmark, BenchmarkCategory("NullableQueueIntsDeserialize")]
    public Queue<SafeNullable<int>> MemoryPackDeserializeNullableQueueInts() =>
        MemoryPackSerializer.Deserialize<Queue<SafeNullable<int>>>(MemoryPackNullableQueueIntsBytes);

    [Benchmark, BenchmarkCategory("NullableQueueIntsDeserialize")]
    public Queue<int?> MemoryPackConvertedDeserializeNullableQueueInts() =>
        NullableForms.ToNullable(MemoryPackSerializer.Deserialize<Queue<SafeNullable<int>>>(MemoryPackNullableQueueIntsBytes));

    [Benchmark, BenchmarkCategory("NullableQueueIntsDeserialize")]
    public Queue<int?> MemoryPackUnsafeDeserializeNullableQueueInts() =>
        MemoryPackSerializer.Deserialize<Queue<int?>>(MemoryPackUnsafeNullableQueueIntsBytes);

    [Benchmark, BenchmarkCategory("NullableQueueIntsDeserialize")]
    public Queue<int?> MessagePackDeserializeNullableQueueInts() =>
        MessagePackSerializer.Deserialize<Queue<int?>>(MessagePackNullableQueueIntsBytes);

    [Benchmark, BenchmarkCategory("NullableQueueIntsDeserialize")]
    public Queue<int?> ProtobufDeserializeNullableQueueInts() =>
        Serializer.Deserialize<NullableIntsIn<Queue<int?>>>(new ReadOnlySpan<byte>(ProtobufNullableQueueIntsBytes)).Items;

    [Benchmark, BenchmarkCategory("NullableQueueIntsDeserialize")]
    public Queue<int?> JsonDeserializeNullableQueueInts() =>
        JsonSerializer.Deserialize(JsonNullableQueueIntsBytes, CaseJson.Default.QueueNullableInt32);

    [Benchmark(Baseline = true), BenchmarkCategory("NullableHashSetIntsSerialize")]
    public int NinoSerializeNullableHashSetInts()
    {
        _ninoBuffer.ResetWrittenCount();
        NinoSerializer.Serialize(NullableHashSetInts, _ninoBuffer);
        return _ninoBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("NullableHashSetIntsSerialize")]
    public int NinoNullableSerializeNullableHashSetInts()
    {
        _ninoBuffer.ResetWrittenCount();
        NinoSerializer.Serialize(NullableHashSetIntsAsNinoNullable, _ninoBuffer);
        return _ninoBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("NullableHashSetIntsSerialize")]
    public int MemoryPackSerializeNullableHashSetInts()
    {
        _memoryPackBuffer.ResetWrittenCount();
        MemoryPackSerializer.Serialize(_memoryPackBuffer, NullableHashSetIntsAsSafeNullable);
        return _memoryPackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("NullableHashSetIntsSerialize")]
    public int MemoryPackConvertedSerializeNullableHashSetInts()
    {
        _memoryPackBuffer.ResetWrittenCount();
        MemoryPackSerializer.Serialize(_memoryPackBuffer, SafeNullables.From(NullableHashSetInts));
        return _memoryPackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("NullableHashSetIntsSerialize")]
    public int MemoryPackUnsafeSerializeNullableHashSetInts()
    {
        _memoryPackBuffer.ResetWrittenCount();
        MemoryPackSerializer.Serialize(_memoryPackBuffer, NullableHashSetInts);
        return _memoryPackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("NullableHashSetIntsSerialize")]
    public int MessagePackSerializeNullableHashSetInts()
    {
        _messagePackBuffer.ResetWrittenCount();
        MessagePackSerializer.Serialize(_messagePackBuffer, NullableHashSetInts);
        return _messagePackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("NullableHashSetIntsSerialize")]
    public int ProtobufSerializeNullableHashSetInts()
    {
        _protobufStream.SetLength(0);
        Serializer.Serialize(_protobufStream, NullableHashSetIntsProtobuf);
        return (int)_protobufStream.Length;
    }

    [Benchmark, BenchmarkCategory("NullableHashSetIntsSerialize")]
    public int JsonSerializeNullableHashSetInts()
    {
        _jsonBuffer.ResetWrittenCount();
        _jsonWriter.Reset(_jsonBuffer);
        JsonSerializer.Serialize(_jsonWriter, NullableHashSetInts, CaseJson.Default.HashSetNullableInt32);
        return _jsonBuffer.WrittenCount;
    }

    [Benchmark(Baseline = true), BenchmarkCategory("NullableHashSetIntsDeserialize")]
    public HashSet<int?> NinoDeserializeNullableHashSetInts() =>
        NinoDeserializer.Deserialize<HashSet<int?>>(NinoNullableHashSetIntsBytes);

    [Benchmark, BenchmarkCategory("NullableHashSetIntsDeserialize")]
    public HashSet<NinoNullable<int>> NinoNullableDeserializeNullableHashSetInts() =>
        NinoDeserializer.Deserialize<HashSet<NinoNullable<int>>>(NinoNullableNullableHashSetIntsBytes);

    [Benchmark, BenchmarkCategory("NullableHashSetIntsDeserialize")]
    public HashSet<SafeNullable<int>> MemoryPackDeserializeNullableHashSetInts() =>
        MemoryPackSerializer.Deserialize<HashSet<SafeNullable<int>>>(MemoryPackNullableHashSetIntsBytes);

    [Benchmark, BenchmarkCategory("NullableHashSetIntsDeserialize")]
    public HashSet<int?> MemoryPackConvertedDeserializeNullableHashSetInts() =>
        NullableForms.ToNullable(MemoryPackSerializer.Deserialize<HashSet<SafeNullable<int>>>(MemoryPackNullableHashSetIntsBytes));

    [Benchmark, BenchmarkCategory("NullableHashSetIntsDeserialize")]
    public HashSet<int?> MemoryPackUnsafeDeserializeNullableHashSetInts() =>
        MemoryPackSerializer.Deserialize<HashSet<int?>>(MemoryPackUnsafeNullableHashSetIntsBytes);

    [Benchmark, BenchmarkCategory("NullableHashSetIntsDeserialize")]
    public HashSet<int?> MessagePackDeserializeNullableHashSetInts() =>
        MessagePackSerializer.Deserialize<HashSet<int?>>(MessagePackNullableHashSetIntsBytes);

    [Benchmark, BenchmarkCategory("NullableHashSetIntsDeserialize")]
    public HashSet<int?> ProtobufDeserializeNullableHashSetInts() =>
        Serializer.Deserialize<NullableIntsIn<HashSet<int?>>>(new ReadOnlySpan<byte>(ProtobufNullableHashSetIntsBytes)).Items;

    [Benchmark, BenchmarkCategory("NullableHashSetIntsDeserialize")]
    public HashSet<int?> JsonDeserializeNullableHashSetInts() =>
        JsonSerializer.Deserialize(JsonNullableHashSetIntsBytes, CaseJson.Default.HashSetNullableInt32);

    [Benchmark(Baseline = true), BenchmarkCategory("NullableGridIntsSerialize")]
    public int NinoSerializeNullableGridInts()
    {
        _ninoBuffer.ResetWrittenCount();
        NinoSerializer.Serialize(NullableGridInts, _ninoBuffer);
        return _ninoBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("NullableGridIntsSerialize")]
    public int NinoNullableSerializeNullableGridInts()
    {
        _ninoBuffer.ResetWrittenCount();
        NinoSerializer.Serialize(NullableGridIntsAsNinoNullable, _ninoBuffer);
        return _ninoBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("NullableGridIntsSerialize")]
    public int MemoryPackSerializeNullableGridInts()
    {
        _memoryPackBuffer.ResetWrittenCount();
        MemoryPackSerializer.Serialize(_memoryPackBuffer, NullableGridIntsAsSafeNullable);
        return _memoryPackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("NullableGridIntsSerialize")]
    public int MemoryPackConvertedSerializeNullableGridInts()
    {
        _memoryPackBuffer.ResetWrittenCount();
        MemoryPackSerializer.Serialize(_memoryPackBuffer, SafeNullables.From(NullableGridInts));
        return _memoryPackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("NullableGridIntsSerialize")]
    public int MemoryPackUnsafeSerializeNullableGridInts()
    {
        _memoryPackBuffer.ResetWrittenCount();
        MemoryPackSerializer.Serialize(_memoryPackBuffer, NullableGridInts);
        return _memoryPackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("NullableGridIntsSerialize")]
    public int MessagePackSerializeNullableGridInts()
    {
        _messagePackBuffer.ResetWrittenCount();
        MessagePackSerializer.Serialize(_messagePackBuffer, NullableGridInts);
        return _messagePackBuffer.WrittenCount;
    }

    [Benchmark(Baseline = true), BenchmarkCategory("NullableGridIntsDeserialize")]
    public int?[,] NinoDeserializeNullableGridInts() => NinoDeserializer.Deserialize<int?[,]>(NinoNullableGridIntsBytes);

    [Benchmark, BenchmarkCategory("NullableGridIntsDeserialize")]
    public NinoNullable<int>[,] NinoNullableDeserializeNullableGridInts() =>
        NinoDeserializer.Deserialize<NinoNullable<int>[,]>(NinoNullableNullableGridIntsBytes);

    [Benchmark, BenchmarkCategory("NullableGridIntsDeserialize")]
    public SafeNullable<int>[,] MemoryPackDeserializeNullableGridInts() =>
        MemoryPackSerializer.Deserialize<SafeNullable<int>[,]>(MemoryPackNullableGridIntsBytes);

    [Benchmark, BenchmarkCategory("NullableGridIntsDeserialize")]
    public int?[,] MemoryPackConvertedDeserializeNullableGridInts() =>
        NullableForms.ToNullable(MemoryPackSerializer.Deserialize<SafeNullable<int>[,]>(MemoryPackNullableGridIntsBytes));

    [Benchmark, BenchmarkCategory("NullableGridIntsDeserialize")]
    public int?[,] MemoryPackUnsafeDeserializeNullableGridInts() =>
        MemoryPackSerializer.Deserialize<int?[,]>(MemoryPackUnsafeNullableGridIntsBytes);

    [Benchmark, BenchmarkCategory("NullableGridIntsDeserialize")]
    public int?[,] MessagePackDeserializeNullableGridInts() =>
        MessagePackSerializer.Deserialize<int?[,]>(MessagePackNullableGridIntsBytes);

    [Benchmark(Baseline = true), BenchmarkCategory("NullableLinkedListIntsSerialize")]
    public int NinoSerializeNullableLinkedListInts()
    {
        _ninoBuffer.ResetWrittenCount();
        NinoSerializer.Serialize(NullableLinkedListInts, _ninoBuffer);
        return _ninoBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("NullableLinkedListIntsSerialize")]
    public int NinoNullableSerializeNullableLinkedListInts()
    {
        _ninoBuffer.ResetWrittenCount();
        NinoSerializer.Serialize(NullableLinkedListIntsAsNinoNullable, _ninoBuffer);
        return _ninoBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("NullableLinkedListIntsSerialize")]
    public int MemoryPackSerializeNullableLinkedListInts()
    {
        _memoryPackBuffer.ResetWrittenCount();
        MemoryPackSerializer.Serialize(_memoryPackBuffer, NullableLinkedListIntsAsSafeNullable);
        return _memoryPackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("NullableLinkedListIntsSerialize")]
    public int MemoryPackConvertedSerializeNullableLinkedListInts()
    {
        _memoryPackBuffer.ResetWrittenCount();
        MemoryPackSerializer.Serialize(_memoryPackBuffer, SafeNullables.From(NullableLinkedListInts));
        return _memoryPackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("NullableLinkedListIntsSerialize")]
    public int MemoryPackUnsafeSerializeNullableLinkedListInts()
    {
        _memoryPackBuffer.ResetWrittenCount();
        MemoryPackSerializer.Serialize(_memoryPackBuffer, NullableLinkedListInts);
        return _memoryPackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("NullableLinkedListIntsSerialize")]
    public int MessagePackSerializeNullableLinkedListInts()
    {
        _messagePackBuffer.ResetWrittenCount();
        MessagePackSerializer.Serialize(_messagePackBuffer, NullableLinkedListInts);
        return _messagePackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("NullableLinkedListIntsSerialize")]
    public int ProtobufSerializeNullableLinkedListInts()
    {
        _protobufStream.SetLength(0);
        Serializer.Serialize(_protobufStream, NullableLinkedListIntsProtobuf);
        return (int)_protobufStream.Length;
    }

    [Benchmark, BenchmarkCategory("NullableLinkedListIntsSerialize")]
    public int JsonSerializeNullableLinkedListInts()
    {
        _jsonBuffer.ResetWrittenCount();
        _jsonWriter.Reset(_jsonBuffer);
        JsonSerializer.Serialize(_jsonWriter, NullableLinkedListInts, CaseJson.Default.LinkedListNullableInt32);
        return _jsonBuffer.WrittenCount;
    }

    [Benchmark(Baseline = true), BenchmarkCategory("NullableLinkedListIntsDeserialize")]
    public LinkedList<int?> NinoDeserializeNullableLinkedListInts() =>
        NinoDeserializer.Deserialize<LinkedList<int?>>(NinoNullableLinkedListIntsBytes);

    [Benchmark, BenchmarkCategory("NullableLinkedListIntsDeserialize")]
    public LinkedList<NinoNullable<int>> NinoNullableDeserializeNullableLinkedListInts() =>
        NinoDeserializer.Deserialize<LinkedList<NinoNullable<int>>>(NinoNullableNullableLinkedListIntsBytes);

    [Benchmark, BenchmarkCategory("NullableLinkedListIntsDeserialize")]
    public LinkedList<SafeNullable<int>> MemoryPackDeserializeNullableLinkedListInts() =>
        MemoryPackSerializer.Deserialize<LinkedList<SafeNullable<int>>>(MemoryPackNullableLinkedListIntsBytes);

    [Benchmark, BenchmarkCategory("NullableLinkedListIntsDeserialize")]
    public LinkedList<int?> MemoryPackConvertedDeserializeNullableLinkedListInts() =>
        NullableForms.ToNullable(MemoryPackSerializer.Deserialize<LinkedList<SafeNullable<int>>>(MemoryPackNullableLinkedListIntsBytes));

    [Benchmark, BenchmarkCategory("NullableLinkedListIntsDeserialize")]
    public LinkedList<int?> MemoryPackUnsafeDeserializeNullableLinkedListInts() =>
        MemoryPackSerializer.Deserialize<LinkedList<int?>>(MemoryPackUnsafeNullableLinkedListIntsBytes);

    [Benchmark, BenchmarkCategory("NullableLinkedListIntsDeserialize")]
    public LinkedList<int?> MessagePackDeserializeNullableLinkedListInts() =>
        MessagePackSerializer.Deserialize<LinkedList<int?>>(MessagePackNullableLinkedListIntsBytes);

    [Benchmark, BenchmarkCategory("NullableLinkedListIntsDeserialize")]
    public LinkedList<int?> ProtobufDeserializeNullableLinkedListInts() =>
        Serializer.Deserialize<NullableIntsIn<LinkedList<int?>>>(new ReadOnlySpan<byte>(ProtobufNullableLinkedListIntsBytes)).Items;

    [Benchmark, BenchmarkCategory("NullableLinkedListIntsDeserialize")]
    public LinkedList<int?> JsonDeserializeNullableLinkedListInts() =>
        JsonSerializer.Deserialize(JsonNullableLinkedListIntsBytes, CaseJson.Default.LinkedListNullableInt32);

    [Benchmark(Baseline = true), BenchmarkCategory("NullableCollectionIntsSerialize")]
    public int NinoSerializeNullableCollectionInts()
    {
        _ninoBuffer.ResetWrittenCount();
        NinoSerializer.Serialize(NullableCollectionInts, _ninoBuffer);
        return _ninoBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("NullableCollectionIntsSerialize")]
    public int NinoNullableSerializeNullableCollectionInts()
    {
        _ninoBuffer.ResetWrittenCount();
        NinoSerializer.Serialize(NullableCollectionIntsAsNinoNullable, _ninoBuffer);
        return _ninoBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("NullableCollectionIntsSerialize")]
    public int MemoryPackSerializeNullableCollectionInts()
    {
        _memoryPackBuffer.ResetWrittenCount();
        MemoryPackSerializer.Serialize(_memoryPackBuffer, NullableCollectionIntsAsSafeNullable);
        return _memoryPackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("NullableCollectionIntsSerialize")]
    public int MemoryPackConvertedSerializeNullableCollectionInts()
    {
        _memoryPackBuffer.ResetWrittenCount();
        MemoryPackSerializer.Serialize(_memoryPackBuffer, SafeNullables.From(NullableCollectionInts));
        return _memoryPackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("NullableCollectionIntsSerialize")]
    public int MemoryPackUnsafeSerializeNullableCollectionInts()
    {
        _memoryPackBuffer.ResetWrittenCount();
        MemoryPackSerializer.Serialize(_memoryPackBuffer, NullableCollectionInts);
        return _memoryPackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("NullableCollectionIntsSerialize")]
    public int MessagePackSerializeNullableCollectionInts()
    {
        _messagePackBuffer.ResetWrittenCount();
        MessagePackSerializer.Serialize(_messagePackBuffer, NullableCollectionInts);
        return _messagePackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("NullableCollectionIntsSerialize")]
    public int ProtobufSerializeNullableCollectionInts()
    {
        _protobufStream.SetLength(0);
        Serializer.Serialize(_protobufStream, NullableCollectionIntsProtobuf);
        return (int)_protobufStream.Length;
    }

    [Benchmark, BenchmarkCategory("NullableCollectionIntsSerialize")]
    public int JsonSerializeNullableCollectionInts()
    {
        _jsonBuffer.ResetWrittenCount();
        _jsonWriter.Reset(_jsonBuffer);
        JsonSerializer.Serialize(_jsonWriter, NullableCollectionInts, CaseJson.Default.CollectionNullableInt32);
        return _jsonBuffer.WrittenCount;
    }

    [Benchmark(Baseline = true), BenchmarkCategory("NullableCollectionIntsDeserialize")]
    public Collection<int?> NinoDeserializeNullableCollectionInts() =>
        NinoDeserializer.Deserialize<Collection<int?>>(NinoNullableCollectionIntsBytes);

    [Benchmark, BenchmarkCategory("NullableCollectionIntsDeserialize")]
    public Collection<NinoNullable<int>> NinoNullableDeserializeNullableCollectionInts() =>
        NinoDeserializer.Deserialize<Collection<NinoNullable<int>>>(NinoNullableNullableCollectionIntsBytes);

    [Benchmark, BenchmarkCategory("NullableCollectionIntsDeserialize")]
    public Collection<SafeNullable<int>> MemoryPackDeserializeNullableCollectionInts() =>
        MemoryPackSerializer.Deserialize<Collection<SafeNullable<int>>>(MemoryPackNullableCollectionIntsBytes);

    [Benchmark, BenchmarkCategory("NullableCollectionIntsDeserialize")]
    public Collection<int?> MemoryPackConvertedDeserializeNullableCollectionInts() =>
        NullableForms.ToNullable(MemoryPackSerializer.Deserialize<Collection<SafeNullable<int>>>(MemoryPackNullableCollectionIntsBytes));

    [Benchmark, BenchmarkCategory("NullableCollectionIntsDeserialize")]
    public Collection<int?> MemoryPackUnsafeDeserializeNullableCollectionInts() =>
        MemoryPackSerializer.Deserialize<Collection<int?>>(MemoryPackUnsafeNullableCollectionIntsBytes);

    [Benchmark, BenchmarkCategory("NullableCollectionIntsDeserialize")]
    public Collection<int?> MessagePackDeserializeNullableCollectionInts() =>
        MessagePackSerializer.Deserialize<Collection<int?>>(MessagePackNullableCollectionIntsBytes);

    [Benchmark, BenchmarkCategory("NullableCollectionIntsDeserialize")]
    public Collection<int?> ProtobufDeserializeNullableCollectionInts() =>
        Serializer.Deserialize<NullableIntsIn<Collection<int?>>>(new ReadOnlySpan<byte>(ProtobufNullableCollectionIntsBytes)).Items;

    [Benchmark, BenchmarkCategory("NullableCollectionIntsDeserialize")]
    public Collection<int?> JsonDeserializeNullableCollectionInts() =>
        JsonSerializer.Deserialize(JsonNullableCollectionIntsBytes, CaseJson.Default.CollectionNullableInt32);

    [Benchmark(Baseline = true), BenchmarkCategory("NullableConcurrentQueueIntsSerialize")]
    public int NinoSerializeNullableConcurrentQueueInts()
    {
        _ninoBuffer.ResetWrittenCount();
        NinoSerializer.Serialize(NullableConcurrentQueueInts, _ninoBuffer);
        return _ninoBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("NullableConcurrentQueueIntsSerialize")]
    public int NinoNullableSerializeNullableConcurrentQueueInts()
    {
        _ninoBuffer.ResetWrittenCount();
        NinoSerializer.Serialize(NullableConcurrentQueueIntsAsNinoNullable, _ninoBuffer);
        return _ninoBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("NullableConcurrentQueueIntsSerialize")]
    public int MemoryPackSerializeNullableConcurrentQueueInts()
    {
        _memoryPackBuffer.ResetWrittenCount();
        MemoryPackSerializer.Serialize(_memoryPackBuffer, NullableConcurrentQueueIntsAsSafeNullable);
        return _memoryPackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("NullableConcurrentQueueIntsSerialize")]
    public int MemoryPackConvertedSerializeNullableConcurrentQueueInts()
    {
        _memoryPackBuffer.ResetWrittenCount();
        MemoryPackSerializer.Serialize(_memoryPackBuffer, SafeNullables.From(NullableConcurrentQueueInts));
        return _memoryPackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("NullableConcurrentQueueIntsSerialize")]
    public int MemoryPackUnsafeSerializeNullableConcurrentQueueInts()
    {
        _memoryPackBuffer.ResetWrittenCount();
        MemoryPackSerializer.Serialize(_memoryPackBuffer, NullableConcurrentQueueInts);
        return _memoryPackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("NullableConcurrentQueueIntsSerialize")]
    public int MessagePackSerializeNullableConcurrentQueueInts()
    {
        _messagePackBuffer.ResetWrittenCount();
        MessagePackSerializer.Serialize(_messagePackBuffer, NullableConcurrentQueueInts);
        return _messagePackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("NullableConcurrentQueueIntsSerialize")]
    public int ProtobufSerializeNullableConcurrentQueueInts()
    {
        _protobufStream.SetLength(0);
        Serializer.Serialize(_protobufStream, NullableConcurrentQueueIntsProtobuf);
        return (int)_protobufStream.Length;
    }

    [Benchmark, BenchmarkCategory("NullableConcurrentQueueIntsSerialize")]
    public int JsonSerializeNullableConcurrentQueueInts()
    {
        _jsonBuffer.ResetWrittenCount();
        _jsonWriter.Reset(_jsonBuffer);
        JsonSerializer.Serialize(_jsonWriter, NullableConcurrentQueueInts, CaseJson.Default.ConcurrentQueueNullableInt32);
        return _jsonBuffer.WrittenCount;
    }

    [Benchmark(Baseline = true), BenchmarkCategory("NullableConcurrentQueueIntsDeserialize")]
    public ConcurrentQueue<int?> NinoDeserializeNullableConcurrentQueueInts() =>
        NinoDeserializer.Deserialize<ConcurrentQueue<int?>>(NinoNullableConcurrentQueueIntsBytes);

    [Benchmark, BenchmarkCategory("NullableConcurrentQueueIntsDeserialize")]
    public ConcurrentQueue<NinoNullable<int>> NinoNullableDeserializeNullableConcurrentQueueInts() =>
        NinoDeserializer.Deserialize<ConcurrentQueue<NinoNullable<int>>>(NinoNullableNullableConcurrentQueueIntsBytes);

    [Benchmark, BenchmarkCategory("NullableConcurrentQueueIntsDeserialize")]
    public ConcurrentQueue<SafeNullable<int>> MemoryPackDeserializeNullableConcurrentQueueInts() =>
        MemoryPackSerializer.Deserialize<ConcurrentQueue<SafeNullable<int>>>(MemoryPackNullableConcurrentQueueIntsBytes);

    [Benchmark, BenchmarkCategory("NullableConcurrentQueueIntsDeserialize")]
    public ConcurrentQueue<int?> MemoryPackConvertedDeserializeNullableConcurrentQueueInts() =>
        NullableForms.ToNullable(MemoryPackSerializer.Deserialize<ConcurrentQueue<SafeNullable<int>>>(MemoryPackNullableConcurrentQueueIntsBytes));

    [Benchmark, BenchmarkCategory("NullableConcurrentQueueIntsDeserialize")]
    public ConcurrentQueue<int?> MemoryPackUnsafeDeserializeNullableConcurrentQueueInts() =>
        MemoryPackSerializer.Deserialize<ConcurrentQueue<int?>>(MemoryPackUnsafeNullableConcurrentQueueIntsBytes);

    [Benchmark, BenchmarkCategory("NullableConcurrentQueueIntsDeserialize")]
    public ConcurrentQueue<int?> MessagePackDeserializeNullableConcurrentQueueInts() =>
        MessagePackSerializer.Deserialize<ConcurrentQueue<int?>>(MessagePackNullableConcurrentQueueIntsBytes);

    [Benchmark, BenchmarkCategory("NullableConcurrentQueueIntsDeserialize")]
    public ConcurrentQueue<int?> ProtobufDeserializeNullableConcurrentQueueInts() =>
        Serializer.Deserialize<NullableIntsIn<ConcurrentQueue<int?>>>(new ReadOnlySpan<byte>(ProtobufNullableConcurrentQueueIntsBytes)).Items;

    [Benchmark, BenchmarkCategory("NullableConcurrentQueueIntsDeserialize")]
    public ConcurrentQueue<int?> JsonDeserializeNullableConcurrentQueueInts() =>
        JsonSerializer.Deserialize(JsonNullableConcurrentQueueIntsBytes, CaseJson.Default.ConcurrentQueueNullableInt32);

    [Benchmark(Baseline = true), BenchmarkCategory("NullableMemoryIntsSerialize")]
    public int NinoSerializeNullableMemoryInts()
    {
        _ninoBuffer.ResetWrittenCount();
        NinoSerializer.Serialize(NullableMemoryInts, _ninoBuffer);
        return _ninoBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("NullableMemoryIntsSerialize")]
    public int NinoNullableSerializeNullableMemoryInts()
    {
        _ninoBuffer.ResetWrittenCount();
        NinoSerializer.Serialize(NullableMemoryIntsAsNinoNullable, _ninoBuffer);
        return _ninoBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("NullableMemoryIntsSerialize")]
    public int MemoryPackSerializeNullableMemoryInts()
    {
        _memoryPackBuffer.ResetWrittenCount();
        MemoryPackSerializer.Serialize(_memoryPackBuffer, NullableMemoryIntsAsSafeNullable);
        return _memoryPackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("NullableMemoryIntsSerialize")]
    public int MemoryPackConvertedSerializeNullableMemoryInts()
    {
        _memoryPackBuffer.ResetWrittenCount();
        MemoryPackSerializer.Serialize(_memoryPackBuffer, SafeNullables.From(NullableMemoryInts));
        return _memoryPackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("NullableMemoryIntsSerialize")]
    public int MemoryPackUnsafeSerializeNullableMemoryInts()
    {
        _memoryPackBuffer.ResetWrittenCount();
        MemoryPackSerializer.Serialize(_memoryPackBuffer, NullableMemoryInts);
        return _memoryPackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("NullableMemoryIntsSerialize")]
    public int MessagePackSerializeNullableMemoryInts()
    {
        _messagePackBuffer.ResetWrittenCount();
        MessagePackSerializer.Serialize(_messagePackBuffer, NullableMemoryInts);
        return _messagePackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("NullableMemoryIntsSerialize")]
    public int JsonSerializeNullableMemoryInts()
    {
        _jsonBuffer.ResetWrittenCount();
        _jsonWriter.Reset(_jsonBuffer);
        JsonSerializer.Serialize(_jsonWriter, NullableMemoryInts, CaseJson.Default.MemoryNullableInt32);
        return _jsonBuffer.WrittenCount;
    }

    [Benchmark(Baseline = true), BenchmarkCategory("NullableMemoryIntsDeserialize")]
    public Memory<int?> NinoDeserializeNullableMemoryInts() =>
        NinoDeserializer.Deserialize<Memory<int?>>(NinoNullableMemoryIntsBytes);

    [Benchmark, BenchmarkCategory("NullableMemoryIntsDeserialize")]
    public Memory<NinoNullable<int>> NinoNullableDeserializeNullableMemoryInts() =>
        NinoDeserializer.Deserialize<Memory<NinoNullable<int>>>(NinoNullableNullableMemoryIntsBytes);

    [Benchmark, BenchmarkCategory("NullableMemoryIntsDeserialize")]
    public Memory<SafeNullable<int>> MemoryPackDeserializeNullableMemoryInts() =>
        MemoryPackSerializer.Deserialize<Memory<SafeNullable<int>>>(MemoryPackNullableMemoryIntsBytes);

    [Benchmark, BenchmarkCategory("NullableMemoryIntsDeserialize")]
    public Memory<int?> MemoryPackConvertedDeserializeNullableMemoryInts() =>
        NullableForms.ToNullable(MemoryPackSerializer.Deserialize<Memory<SafeNullable<int>>>(MemoryPackNullableMemoryIntsBytes));

    [Benchmark, BenchmarkCategory("NullableMemoryIntsDeserialize")]
    public Memory<int?> MemoryPackUnsafeDeserializeNullableMemoryInts() =>
        MemoryPackSerializer.Deserialize<Memory<int?>>(MemoryPackUnsafeNullableMemoryIntsBytes);

    [Benchmark, BenchmarkCategory("NullableMemoryIntsDeserialize")]
    public Memory<int?> MessagePackDeserializeNullableMemoryInts() =>
        MessagePackSerializer.Deserialize<Memory<int?>>(MessagePackNullableMemoryIntsBytes);

    [Benchmark, BenchmarkCategory("NullableMemoryIntsDeserialize")]
    public Memory<int?> JsonDeserializeNullableMemoryInts() =>
        JsonSerializer.Deserialize(JsonNullableMemoryIntsBytes, CaseJson.Default.MemoryNullableInt32);

    [Benchmark(Baseline = true), BenchmarkCategory("NullableIListIntsSerialize")]
    public int NinoSerializeNullableIListInts()
    {
        _ninoBuffer.ResetWrittenCount();
        NinoSerializer.Serialize(NullableIListInts, _ninoBuffer);
        return _ninoBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("NullableIListIntsSerialize")]
    public int NinoNullableSerializeNullableIListInts()
    {
        _ninoBuffer.ResetWrittenCount();
        NinoSerializer.Serialize(NullableIListIntsAsNinoNullable, _ninoBuffer);
        return _ninoBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("NullableIListIntsSerialize")]
    public int MemoryPackSerializeNullableIListInts()
    {
        _memoryPackBuffer.ResetWrittenCount();
        MemoryPackSerializer.Serialize(_memoryPackBuffer, NullableIListIntsAsSafeNullable);
        return _memoryPackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("NullableIListIntsSerialize")]
    public int MemoryPackConvertedSerializeNullableIListInts()
    {
        _memoryPackBuffer.ResetWrittenCount();
        MemoryPackSerializer.Serialize(_memoryPackBuffer, SafeNullables.From(NullableIListInts));
        return _memoryPackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("NullableIListIntsSerialize")]
    public int MemoryPackUnsafeSerializeNullableIListInts()
    {
        _memoryPackBuffer.ResetWrittenCount();
        MemoryPackSerializer.Serialize(_memoryPackBuffer, NullableIListInts);
        return _memoryPackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("NullableIListIntsSerialize")]
    public int MessagePackSerializeNullableIListInts()
    {
        _messagePackBuffer.ResetWrittenCount();
        MessagePackSerializer.Serialize(_messagePackBuffer, NullableIListInts);
        return _messagePackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("NullableIListIntsSerialize")]
    public int ProtobufSerializeNullableIListInts()
    {
        _protobufStream.SetLength(0);
        Serializer.Serialize(_protobufStream, NullableIListIntsProtobuf);
        return (int)_protobufStream.Length;
    }

    [Benchmark, BenchmarkCategory("NullableIListIntsSerialize")]
    public int JsonSerializeNullableIListInts()
    {
        _jsonBuffer.ResetWrittenCount();
        _jsonWriter.Reset(_jsonBuffer);
        JsonSerializer.Serialize(_jsonWriter, NullableIListInts, CaseJson.Default.IListNullableInt32);
        return _jsonBuffer.WrittenCount;
    }

    [Benchmark(Baseline = true), BenchmarkCategory("NullableIListIntsDeserialize")]
    public IList<int?> NinoDeserializeNullableIListInts() =>
        NinoDeserializer.Deserialize<IList<int?>>(NinoNullableIListIntsBytes);

    [Benchmark, BenchmarkCategory("NullableIListIntsDeserialize")]
    public IList<NinoNullable<int>> NinoNullableDeserializeNullableIListInts() =>
        NinoDeserializer.Deserialize<IList<NinoNullable<int>>>(NinoNullableNullableIListIntsBytes);

    [Benchmark, BenchmarkCategory("NullableIListIntsDeserialize")]
    public IList<SafeNullable<int>> MemoryPackDeserializeNullableIListInts() =>
        MemoryPackSerializer.Deserialize<IList<SafeNullable<int>>>(MemoryPackNullableIListIntsBytes);

    [Benchmark, BenchmarkCategory("NullableIListIntsDeserialize")]
    public IList<int?> MemoryPackConvertedDeserializeNullableIListInts() =>
        NullableForms.ToNullable(MemoryPackSerializer.Deserialize<IList<SafeNullable<int>>>(MemoryPackNullableIListIntsBytes));

    [Benchmark, BenchmarkCategory("NullableIListIntsDeserialize")]
    public IList<int?> MemoryPackUnsafeDeserializeNullableIListInts() =>
        MemoryPackSerializer.Deserialize<IList<int?>>(MemoryPackUnsafeNullableIListIntsBytes);

    [Benchmark, BenchmarkCategory("NullableIListIntsDeserialize")]
    public IList<int?> MessagePackDeserializeNullableIListInts() =>
        MessagePackSerializer.Deserialize<IList<int?>>(MessagePackNullableIListIntsBytes);

    [Benchmark, BenchmarkCategory("NullableIListIntsDeserialize")]
    public IList<int?> ProtobufDeserializeNullableIListInts() =>
        Serializer.Deserialize<NullableIntsIn<IList<int?>>>(new ReadOnlySpan<byte>(ProtobufNullableIListIntsBytes)).Items;

    [Benchmark, BenchmarkCategory("NullableIListIntsDeserialize")]
    public IList<int?> JsonDeserializeNullableIListInts() =>
        JsonSerializer.Deserialize(JsonNullableIListIntsBytes, CaseJson.Default.IListNullableInt32);
}
