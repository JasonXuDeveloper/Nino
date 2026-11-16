using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using MemoryPack;
#if !NINO_BENCH_V3
using BenchmarkDotNet.Attributes;
using Nino.Core;
using ProtoBuf;
#endif

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
/// A library without the collection has no method rather than other data. The v3 build has the Queue, HashSet,
/// LinkedList, Collection and ConcurrentQueue: v3's generator writes no overload for a multi-dimensional array, a
/// Memory or an IList of a nullable value (CS8377: only its overload of unmanaged values is left, which takes none of
/// them).
/// </summary>
// LuminPack 1.1.9 reads back other values than it wrote for every collection here but the Queue (--verify).
[BenchCase("NullableQueueInts", MemoryPackValue = "{Case}AsSafeNullable", NinoNullableValue = "{Case}AsNinoNullable",
    NullableForms = true, ProtobufValue = "{Case}Protobuf", Json = "QueueNullableInt32")]
[BenchCase("NullableHashSetInts", MemoryPackValue = "{Case}AsSafeNullable", NinoNullableValue = "{Case}AsNinoNullable",
    NullableForms = true, Without = "LuminPack", ProtobufValue = "{Case}Protobuf", Json = "HashSetNullableInt32")]
[BenchCase("NullableGridInts", MemoryPackValue = "{Case}AsSafeNullable", NinoNullableValue = "{Case}AsNinoNullable",
    NullableForms = true, Without = "Protobuf LuminPack", Builds = "v4")]
[BenchCase("NullableLinkedListInts", MemoryPackValue = "{Case}AsSafeNullable", NinoNullableValue = "{Case}AsNinoNullable",
    NullableForms = true, Without = "LuminPack", ProtobufValue = "{Case}Protobuf", Json = "LinkedListNullableInt32")]
[BenchCase("NullableCollectionInts", MemoryPackValue = "{Case}AsSafeNullable", NinoNullableValue = "{Case}AsNinoNullable",
    NullableForms = true, Without = "LuminPack", ProtobufValue = "{Case}Protobuf", Json = "CollectionNullableInt32")]
[BenchCase("NullableConcurrentQueueInts", MemoryPackValue = "{Case}AsSafeNullable", NinoNullableValue = "{Case}AsNinoNullable",
    NullableForms = true, Without = "LuminPack", ProtobufValue = "{Case}Protobuf", Json = "ConcurrentQueueNullableInt32")]
[BenchCase("NullableMemoryInts", MemoryPackValue = "{Case}AsSafeNullable", NinoNullableValue = "{Case}AsNinoNullable",
    NullableForms = true, Without = "Protobuf LuminPack", Json = "MemoryNullableInt32", Builds = "v4")]
[BenchCase("NullableIListInts", MemoryPackValue = "{Case}AsSafeNullable", NinoNullableValue = "{Case}AsNinoNullable",
    NullableForms = true, Without = "LuminPack", ProtobufValue = "{Case}Protobuf", Json = "IListNullableInt32", Builds = "v4")]
public partial class CollectionsTest
{
    private static readonly Queue<int?> NullableQueueInts = NullableIntCollections.Queue();
    private static readonly Queue<SafeNullable<int>> NullableQueueIntsAsSafeNullable = SafeNullables.From(NullableQueueInts);
    private static readonly HashSet<int?> NullableHashSetInts = NullableIntCollections.HashSet();
    private static readonly HashSet<SafeNullable<int>> NullableHashSetIntsAsSafeNullable = SafeNullables.From(NullableHashSetInts);
    private static readonly LinkedList<int?> NullableLinkedListInts = NullableIntCollections.LinkedList();
    private static readonly LinkedList<SafeNullable<int>> NullableLinkedListIntsAsSafeNullable = SafeNullables.From(NullableLinkedListInts);
    private static readonly Collection<int?> NullableCollectionInts = NullableIntCollections.Collection();
    private static readonly Collection<SafeNullable<int>> NullableCollectionIntsAsSafeNullable = SafeNullables.From(NullableCollectionInts);
    private static readonly ConcurrentQueue<int?> NullableConcurrentQueueInts = NullableIntCollections.ConcurrentQueue();
    private static readonly ConcurrentQueue<SafeNullable<int>> NullableConcurrentQueueIntsAsSafeNullable = SafeNullables.From(NullableConcurrentQueueInts);
#if !NINO_BENCH_V3
    private static readonly int?[,] NullableGridInts = NullableIntCollections.Grid();
    private static readonly SafeNullable<int>[,] NullableGridIntsAsSafeNullable = SafeNullables.From(NullableGridInts);
    private static readonly Memory<int?> NullableMemoryInts = NullableIntCollections.Memory();
    private static readonly Memory<SafeNullable<int>> NullableMemoryIntsAsSafeNullable = SafeNullables.From(NullableMemoryInts);
    private static readonly IList<int?> NullableIListInts = NullableIntCollections.IList();
    private static readonly IList<SafeNullable<int>> NullableIListIntsAsSafeNullable = SafeNullables.From(NullableIListInts);
    private static readonly Queue<NinoNullable<int>> NullableQueueIntsAsNinoNullable = NullableForms.Nino(NullableQueueInts);
    private static readonly NullableIntsIn<Queue<int?>> NullableQueueIntsProtobuf = new() { Items = NullableQueueInts };
    private static readonly HashSet<NinoNullable<int>> NullableHashSetIntsAsNinoNullable = NullableForms.Nino(NullableHashSetInts);
    private static readonly NullableIntsIn<HashSet<int?>> NullableHashSetIntsProtobuf = new() { Items = NullableHashSetInts };
    private static readonly NinoNullable<int>[,] NullableGridIntsAsNinoNullable = NullableForms.Nino(NullableGridInts);
    private static readonly LinkedList<NinoNullable<int>> NullableLinkedListIntsAsNinoNullable = NullableForms.Nino(NullableLinkedListInts);
    private static readonly NullableIntsIn<LinkedList<int?>> NullableLinkedListIntsProtobuf = new() { Items = NullableLinkedListInts };
    private static readonly Collection<NinoNullable<int>> NullableCollectionIntsAsNinoNullable = NullableForms.Nino(NullableCollectionInts);
    private static readonly NullableIntsIn<Collection<int?>> NullableCollectionIntsProtobuf = new() { Items = NullableCollectionInts };
    private static readonly ConcurrentQueue<NinoNullable<int>> NullableConcurrentQueueIntsAsNinoNullable = NullableForms.Nino(NullableConcurrentQueueInts);
    private static readonly NullableIntsIn<ConcurrentQueue<int?>> NullableConcurrentQueueIntsProtobuf = new() { Items = NullableConcurrentQueueInts };
    private static readonly Memory<NinoNullable<int>> NullableMemoryIntsAsNinoNullable = NullableForms.Nino(NullableMemoryInts);
    private static readonly IList<NinoNullable<int>> NullableIListIntsAsNinoNullable = NullableForms.Nino(NullableIListInts);
    private static readonly NullableIntsIn<IList<int?>> NullableIListIntsProtobuf = new() { Items = NullableIListInts };

    // protobuf-net reads them through their contracts: the collection they hold.
    [Benchmark, BenchmarkCategory("NullableQueueIntsDeserialize")]
    public Queue<int?> ProtobufDeserializeNullableQueueInts() =>
        Serializer.Deserialize<NullableIntsIn<Queue<int?>>>(new ReadOnlySpan<byte>(ProtobufNullableQueueIntsBytes)).Items;

    [Benchmark, BenchmarkCategory("NullableHashSetIntsDeserialize")]
    public HashSet<int?> ProtobufDeserializeNullableHashSetInts() =>
        Serializer.Deserialize<NullableIntsIn<HashSet<int?>>>(new ReadOnlySpan<byte>(ProtobufNullableHashSetIntsBytes)).Items;

    [Benchmark, BenchmarkCategory("NullableLinkedListIntsDeserialize")]
    public LinkedList<int?> ProtobufDeserializeNullableLinkedListInts() =>
        Serializer.Deserialize<NullableIntsIn<LinkedList<int?>>>(new ReadOnlySpan<byte>(ProtobufNullableLinkedListIntsBytes)).Items;

    [Benchmark, BenchmarkCategory("NullableCollectionIntsDeserialize")]
    public Collection<int?> ProtobufDeserializeNullableCollectionInts() =>
        Serializer.Deserialize<NullableIntsIn<Collection<int?>>>(new ReadOnlySpan<byte>(ProtobufNullableCollectionIntsBytes)).Items;

    [Benchmark, BenchmarkCategory("NullableConcurrentQueueIntsDeserialize")]
    public ConcurrentQueue<int?> ProtobufDeserializeNullableConcurrentQueueInts() =>
        Serializer.Deserialize<NullableIntsIn<ConcurrentQueue<int?>>>(new ReadOnlySpan<byte>(ProtobufNullableConcurrentQueueIntsBytes)).Items;

    [Benchmark, BenchmarkCategory("NullableIListIntsDeserialize")]
    public IList<int?> ProtobufDeserializeNullableIListInts() =>
        Serializer.Deserialize<NullableIntsIn<IList<int?>>>(new ReadOnlySpan<byte>(ProtobufNullableIListIntsBytes)).Items;
#endif
}
