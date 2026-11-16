using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using BenchmarkDotNet.Configs;
using MemoryPack;
using Nino.Benchmark.Workloads;
#if !NINO_BENCH_V3
using BenchmarkDotNet.Attributes;
using Nino.Core;
using ProtoBuf;
using P = Nino.Benchmark.Proto;
#endif

#nullable disable
namespace Nino.Benchmark;

/// <summary>
/// Collections of the base class library with 1,000 elements each, serialized to a reused buffer writer and
/// deserialized into new instances: a list of tuples (packed by Nino, copied with padding by MemoryPack), an array
/// of key-value pairs, a dictionary of ints, and the hashed, queued, linked, sorted and concurrent collections, whose fast paths depend on
/// the runtime's collection layouts and on SIMD. Against MemoryPack, MessagePack and protobuf-net. Also a list that
/// holds itself (<see cref="SelfTree"/>, 5,461 lists), which counts the depth at every level: against MemoryPack only
/// (MessagePack fails to read it back, protobuf-net has no list of lists). And nullable values with every fifth one null
/// (<c>long?[]</c>, <c>List&lt;int?&gt;</c>, <c>decimal?[]</c> and <c>Vec3?[]</c>, a user's struct of three floats: 16
/// bytes with 4-byte alignment), which Nino writes as a fixed stride with each absent value
/// zeroed: against every library. MemoryPack writes them as <c>SafeNullable&lt;T&gt;</c> (no uninitialized padding in
/// its bytes, <see cref="NullableFieldsMemoryPack"/>); protobuf, which has no null in a repeated field, as a message per
/// element with an optional value (protobuf-net's <c>[NullWrappedValue]</c>, the contracts in CaseFormats.cs, whose
/// messages Google.Protobuf reads, parsed from protobuf-net's bytes outside the timed region); System.Text.Json source
/// generated, UTF-8. And a raw <c>int?</c> as the element of the other collections (a queue, a set, a linked list, a
/// <c>Collection</c>, a concurrent queue, a two-dimensional array, a <c>Memory</c>, an <c>IList</c>:
/// CollectionsTest.NullableCollections.cs).
/// The timed methods are CollectionsTest.Cases.g.cs, generated from the cases below and the adapters (Adapters/,
/// runtimes/csharp/tools/BenchGen). The v3 build times Nino 3.3.13 and MemoryPack on the same values, without the
/// SelfTree cases: v3's generator recurses without end on a list that holds itself (CollectionSerializerGenerator calls
/// its filters on the element type again and again until the compiler process dies with a stack overflow).
/// </summary>
[BenchCase("NullableLongs", MemoryPackValue = "{Case}AsSafeNullable", ProtobufValue = "{Case}Protobuf",
    NinoNullableValue = "{Case}AsNinoNullable", NullableForms = true, Proto = "NullableLongList", Json = "NullableInt64Array")]
[BenchCase("NullableInts", MemoryPackValue = "{Case}AsSafeNullable", ProtobufValue = "{Case}Protobuf",
    NinoNullableValue = "{Case}AsNinoNullable", NullableForms = true, Proto = "NullableIntList", Json = "ListNullableInt32")]
[BenchCase("NullableDecimals", MemoryPackValue = "{Case}AsSafeNullable", ProtobufValue = "{Case}Protobuf",
    NinoNullableValue = "{Case}AsNinoNullable", NullableForms = true, Proto = "NullableDecimalList",
    Json = "NullableDecimalArray")]
[BenchCase("NullableVec3s", MemoryPackValue = "{Case}AsSafeNullable", ProtobufValue = "{Case}Protobuf",
    NinoNullableValue = "{Case}AsNinoNullable", NullableForms = true, Proto = "OptionalVec3List", Json = "NullableVec3Array")]
[BenchCase("TupleList")]
[BenchCase("KeyValuePairs")]
[BenchCase("HashSet")]
[BenchCase("RandomIntSet")]
[BenchCase("StringSet")]
[BenchCase("IntDictionary")]
[BenchCase("Queue")]
[BenchCase("LinkedList")]
[BenchCase("SortedSet")]
[BenchCase("SortedDictionary")]
[BenchCase("SortedList")]
[BenchCase("ConcurrentDictionary")]
[BenchCase("SelfTree", Libraries = "Nino MemoryPack", Builds = "v4")] // LuminPack 1.1.9 writes it as an object without members (--verify)
[BenchClass(Buffer = "1 << 16", Bytes = "{Library}{Case}Bytes", Order = "case")]
[PayloadColumn]
[HideColumns("StdDev", "RatioSD", "Error")]
[MinColumn, MaxColumn]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[NinoJob]
[GcServer(true)]
[MemoryDiagnoser]
[MarkdownExporterAttribute.GitHub]
public partial class CollectionsTest : IReadBack
{
    private const int N = 1000;
    private static readonly List<(int, long)> TupleList = Enumerable.Range(0, N).Select(i => (i, (long)i * 31)).ToList();
    private static readonly KeyValuePair<int, int>[] KeyValuePairs = Enumerable.Range(0, N).Select(i => new KeyValuePair<int, int>(i, i * 7)).ToArray();
    private static readonly HashSet<int> HashSet = new(Enumerable.Range(0, N).Select(i => i * 3));
    // The Go, Rust and TypeScript benchmarks' sets: 1,000 distinct non-negative ints in no order, and "member 0" to
    // "member 999" added in no order (HashSet above holds multiples of 3, added in order).
    private static readonly HashSet<int> RandomIntSet = RandomInts(new Random(42), N);
    private static readonly HashSet<string> StringSet =
        new(Enumerable.Range(0, N).OrderBy(i => (i * 7919) % N).Select(i => "member " + i));

    private static HashSet<int> RandomInts(Random random, int count)
    {
        var set = new HashSet<int>();
        while (set.Count < count) set.Add(random.Next());
        return set;
    }
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


#if !NINO_BENCH_V3
    private static readonly SelfTree SelfTree = SelfTree.Create(6, 4);
#endif

    // MemoryPack's copies (SafeNullable<T>).
    private static readonly SafeNullable<long>[] NullableLongsAsSafeNullable = SafeNullables.From(NullableLongs);
    private static readonly List<SafeNullable<int>> NullableIntsAsSafeNullable = SafeNullables.From(NullableInts);
    private static readonly SafeNullable<decimal>[] NullableDecimalsAsSafeNullable = SafeNullables.From(NullableDecimals);
    private static readonly SafeNullable<Vec3>[] NullableVec3sAsSafeNullable = SafeNullables.From(NullableVec3s);
#if !NINO_BENCH_V3
    // Nino's NinoNullable<T> copies (NullableForms), and protobuf's contracts holding the same collections
    // (CaseFormats.cs), whose messages Google.Protobuf reads. A Nullable<T> of a message type cannot be null-wrapped in
    // a repeated field: an array of an optional field, as NullableTest's SimpleStruct?[] (CaseFormats.cs).
    private static readonly NinoNullable<long>[] NullableLongsAsNinoNullable = NullableForms.Nino(NullableLongs);
    private static readonly List<NinoNullable<int>> NullableIntsAsNinoNullable = NullableForms.Nino(NullableInts);
    private static readonly NinoNullable<decimal>[] NullableDecimalsAsNinoNullable = NullableForms.Nino(NullableDecimals);
    private static readonly NinoNullable<Vec3>[] NullableVec3sAsNinoNullable = NullableForms.Nino(NullableVec3s);
    private static readonly NullableLongList NullableLongsProtobuf = new() { Items = NullableLongs };
    private static readonly NullableIntList NullableIntsProtobuf = new() { Items = NullableInts };
    private static readonly NullableDecimalList NullableDecimalsProtobuf = new() { Items = NullableDecimals };
    private static readonly OptionalVec3List NullableVec3sProtobuf = new() { Items = OptionalVec3.From(NullableVec3s) };

    partial void OnSetup() => ProtoFile.Cases.Check();

    /// <summary>Verify.cs: MemoryPack's SafeNullable collections and Google.Protobuf's messages as the nullable collections.</summary>
    public object ReadBack(object result, Type model) => result switch
    {
        P.NullableLongList => ((NullableLongList)ProtobufModel.ReadBack(result, typeof(NullableLongList))).Items,
        P.NullableIntList => ((NullableIntList)ProtobufModel.ReadBack(result, typeof(NullableIntList))).Items,
        P.NullableDecimalList => ((NullableDecimalList)ProtobufModel.ReadBack(result, typeof(NullableDecimalList))).Items,
        P.OptionalVec3List => OptionalVec3.ToNullable(
            ((OptionalVec3List)ProtobufModel.ReadBack(result, typeof(OptionalVec3List))).Items),
        OptionalVec3[] items => OptionalVec3.ToNullable(items),
        _ => NullableForms.ReadBack(result),
    };

    // protobuf-net reads the nullable collections through their contracts: the collection they hold.
    [Benchmark, BenchmarkCategory("NullableLongsDeserialize")]
    public long?[] ProtobufDeserializeNullableLongs() =>
        Serializer.Deserialize<NullableLongList>(new ReadOnlySpan<byte>(ProtobufNullableLongsBytes)).Items;

    [Benchmark, BenchmarkCategory("NullableIntsDeserialize")]
    public List<int?> ProtobufDeserializeNullableInts() =>
        Serializer.Deserialize<NullableIntList>(new ReadOnlySpan<byte>(ProtobufNullableIntsBytes)).Items;

    [Benchmark, BenchmarkCategory("NullableDecimalsDeserialize")]
    public decimal?[] ProtobufDeserializeNullableDecimals() =>
        Serializer.Deserialize<NullableDecimalList>(new ReadOnlySpan<byte>(ProtobufNullableDecimalsBytes)).Items;

    [Benchmark, BenchmarkCategory("NullableVec3sDeserialize")]
    public OptionalVec3[] ProtobufDeserializeNullableVec3s() =>
        Serializer.Deserialize<OptionalVec3List>(new ReadOnlySpan<byte>(ProtobufNullableVec3sBytes)).Items;
#else
    /// <summary>Verify.cs: MemoryPack's SafeNullable collections as the nullable collections.</summary>
    public object ReadBack(object result, Type model) => SafeNullables.ReadBack(result);
#endif
}

#if !NINO_BENCH_V3
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
#endif
