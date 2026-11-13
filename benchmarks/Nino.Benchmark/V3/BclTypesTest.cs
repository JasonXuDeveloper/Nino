using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Linq;
using System.Runtime.InteropServices;
using BenchmarkDotNet.Configs;
using MemoryPack;

#nullable disable
namespace Nino.Benchmark;

// Cases/Collections/BclTypesTest.cs ported to Nino 3.3.13 (NuGet), for the v3 build of this project
// (NINO_BENCH_V3=true): the same data, method names and job, Nino's and MemoryPack's cases. Only the Nino calls differ,
// as in V3/SimpleTest.cs, and where v3 writes no overload for the type, the case takes the shortest way through one it
// does write (its generator's selector, CollectionSerializerGenerator.Selector, accepts unmanaged types, [NinoType]s,
// strings, key-value pairs, tuples, nullables and IEnumerable<T> of those; anything else binds to the generic
// Serialize<T>(T) where T : unmanaged, CS8377, and v3 has no API for a formatter of one's own):
//  - Memory<int>, ReadOnlySequence<int> (one segment): written through v3's Serialize(Span<T>) (the value's span,
//    nothing copied), read as int[] and wrapped.
//  - IReadOnlyList<string> (InterfaceStrings): v3 writes no IReadOnlyList<T> (CS8377), so the case writes it as the
//    ICollection<string> it also is, and reads a List<string>, which is an IReadOnlyList<string>.
//  - ImmutableQueue<int>, ImmutableStack<int>: v3 writes neither (CS8377), so the cases write ToArray() of them, a
//    copy timed with them.
//  - The immutable collections read back: v3 reads none of them (CS8377, it builds a collection only through a
//    constructor or Add), so the cases read an int[] (KeyValuePair<int, int>[] for the dictionaries) and build the
//    collection from it with CreateRange (the stack pushed back from its bottom).
// Left out, as protobuf-net is from all of them but Uri (it has no formatter for the others either):
//  - BigInteger, Uri, Version, StringBuilder, BitArray, CultureInfo, TimeZoneInfo, Lazy<string>: v3 cannot write or
//    read them (CS8377 both ways), and writing them would mean converting each into another type by hand (a string, a
//    byte[]), which would time that conversion rather than v3.
//  - ILookup<int, int>: naming the type makes v3's generator write code that does not compile (CS8377 on
//    IGrouping<int, int> in its NinoSerializer.Collection.g.cs), and it reads none (CS8377).

/// <summary>
/// BclTypesTest's memory and sequences, read-only, blocking and immutable collections with 1,000 elements, serialized
/// by Nino v3 to a reused buffer writer and deserialized into new instances, against MemoryPack. Deserialize cases
/// return <c>object</c>, as in the v4 class.
/// </summary>
[PayloadColumn]
[HideColumns("StdDev", "RatioSD", "Error")]
[MinColumn, MaxColumn]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[NinoJob]
[GcServer(true)]
[MemoryDiagnoser]
[MarkdownExporterAttribute.GitHub]
public class BclTypesTest
{
    private const int N = 1000;

    private static readonly Memory<int> MemoryValue = new(Enumerable.Range(0, N).ToArray());
    private static readonly ReadOnlySequence<int> ReadOnlySequenceValue = new(Enumerable.Range(0, N).ToArray());
    private static readonly ReadOnlyCollection<int> ReadOnlyCollectionValue = new(Enumerable.Range(0, N).ToList());
    // Strings behind an interface, as in the v4 class.
    private static readonly IReadOnlyList<string> InterfaceStringsValue = new ReadOnlyCollection<string>(Enumerable.Range(0, N).Select(i => "item" + i).ToList());
    private static readonly BlockingCollection<int> BlockingCollectionValue = MakeBlockingCollection();
    private static readonly ImmutableHashSet<int> ImmutableHashSetValue = ImmutableHashSet.CreateRange(Enumerable.Range(0, N));
    private static readonly ImmutableSortedSet<int> ImmutableSortedSetValue = ImmutableSortedSet.CreateRange(Enumerable.Range(0, N));
    private static readonly ImmutableQueue<int> ImmutableQueueValue = ImmutableQueue.CreateRange(Enumerable.Range(0, N));
    private static readonly ImmutableStack<int> ImmutableStackValue = ImmutableStack.CreateRange(Enumerable.Range(0, N));
    private static readonly ImmutableDictionary<int, int> ImmutableDictionaryValue = ImmutableDictionary.CreateRange(Enumerable.Range(0, N).Select(i => new KeyValuePair<int, int>(i, i * 7)));
    private static readonly ImmutableSortedDictionary<int, int> ImmutableSortedDictionaryValue = ImmutableSortedDictionary.CreateRange(Enumerable.Range(0, N).Select(i => new KeyValuePair<int, int>(i, i * 7)));

    private static readonly byte[] NinoMemoryBytes = NinoGen.Serializer.Serialize(MemoryValue.Span);
    private static readonly byte[] MemoryPackMemoryBytes = MemoryPackSerializer.Serialize(MemoryValue);
    private static readonly byte[] NinoReadOnlySequenceBytes = NinoGen.Serializer.Serialize(MemoryMarshal.AsMemory(ReadOnlySequenceValue.First).Span);
    private static readonly byte[] MemoryPackReadOnlySequenceBytes = MemoryPackSerializer.Serialize(ReadOnlySequenceValue);
    private static readonly byte[] NinoReadOnlyCollectionBytes = NinoGen.Serializer.Serialize(ReadOnlyCollectionValue);
    private static readonly byte[] MemoryPackReadOnlyCollectionBytes = MemoryPackSerializer.Serialize(ReadOnlyCollectionValue);
    private static readonly byte[] NinoInterfaceStringsBytes = NinoGen.Serializer.Serialize((ICollection<string>)InterfaceStringsValue);
    private static readonly byte[] MemoryPackInterfaceStringsBytes = MemoryPackSerializer.Serialize(InterfaceStringsValue);
    private static readonly byte[] NinoBlockingCollectionBytes = NinoGen.Serializer.Serialize(BlockingCollectionValue);
    private static readonly byte[] MemoryPackBlockingCollectionBytes = MemoryPackSerializer.Serialize(BlockingCollectionValue);
    private static readonly byte[] NinoImmutableHashSetBytes = NinoGen.Serializer.Serialize(ImmutableHashSetValue);
    private static readonly byte[] MemoryPackImmutableHashSetBytes = MemoryPackSerializer.Serialize(ImmutableHashSetValue);
    private static readonly byte[] NinoImmutableSortedSetBytes = NinoGen.Serializer.Serialize(ImmutableSortedSetValue);
    private static readonly byte[] MemoryPackImmutableSortedSetBytes = MemoryPackSerializer.Serialize(ImmutableSortedSetValue);
    private static readonly byte[] NinoImmutableQueueBytes = NinoGen.Serializer.Serialize(ImmutableQueueValue.ToArray());
    private static readonly byte[] MemoryPackImmutableQueueBytes = MemoryPackSerializer.Serialize(ImmutableQueueValue);
    private static readonly byte[] NinoImmutableStackBytes = NinoGen.Serializer.Serialize(ImmutableStackValue.ToArray());
    private static readonly byte[] MemoryPackImmutableStackBytes = MemoryPackSerializer.Serialize(ImmutableStackValue);
    private static readonly byte[] NinoImmutableDictionaryBytes = NinoGen.Serializer.Serialize(ImmutableDictionaryValue);
    private static readonly byte[] MemoryPackImmutableDictionaryBytes = MemoryPackSerializer.Serialize(ImmutableDictionaryValue);
    private static readonly byte[] NinoImmutableSortedDictionaryBytes = NinoGen.Serializer.Serialize(ImmutableSortedDictionaryValue);
    private static readonly byte[] MemoryPackImmutableSortedDictionaryBytes = MemoryPackSerializer.Serialize(ImmutableSortedDictionaryValue);

    private ArrayBufferWriter<byte> _ninoBuffer;
    private ArrayBufferWriter<byte> _memoryPackBuffer;

    static BclTypesTest()
    {
        BenchmarkPayloadRegistry.Register(nameof(NinoSerializeMemory), NinoMemoryBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(MemoryPackSerializeMemory), MemoryPackMemoryBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(NinoSerializeReadOnlySequence), NinoReadOnlySequenceBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(MemoryPackSerializeReadOnlySequence), MemoryPackReadOnlySequenceBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(NinoSerializeReadOnlyCollection), NinoReadOnlyCollectionBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(MemoryPackSerializeReadOnlyCollection), MemoryPackReadOnlyCollectionBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(NinoSerializeInterfaceStrings), NinoInterfaceStringsBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(MemoryPackSerializeInterfaceStrings), MemoryPackInterfaceStringsBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(NinoSerializeBlockingCollection), NinoBlockingCollectionBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(MemoryPackSerializeBlockingCollection), MemoryPackBlockingCollectionBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(NinoSerializeImmutableHashSet), NinoImmutableHashSetBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(MemoryPackSerializeImmutableHashSet), MemoryPackImmutableHashSetBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(NinoSerializeImmutableSortedSet), NinoImmutableSortedSetBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(MemoryPackSerializeImmutableSortedSet), MemoryPackImmutableSortedSetBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(NinoSerializeImmutableQueue), NinoImmutableQueueBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(MemoryPackSerializeImmutableQueue), MemoryPackImmutableQueueBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(NinoSerializeImmutableStack), NinoImmutableStackBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(MemoryPackSerializeImmutableStack), MemoryPackImmutableStackBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(NinoSerializeImmutableDictionary), NinoImmutableDictionaryBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(MemoryPackSerializeImmutableDictionary), MemoryPackImmutableDictionaryBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(NinoSerializeImmutableSortedDictionary), NinoImmutableSortedDictionaryBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(MemoryPackSerializeImmutableSortedDictionary), MemoryPackImmutableSortedDictionaryBytes.Length);
    }

    private static BlockingCollection<int> MakeBlockingCollection()
    {
        var collection = new BlockingCollection<int>();
        for (int i = 0; i < N; i++)
            collection.Add(i);
        return collection;
    }

    [GlobalSetup]
    public void Setup()
    {
        _ninoBuffer = new ArrayBufferWriter<byte>(1 << 16);
        _memoryPackBuffer = new ArrayBufferWriter<byte>(1 << 16);
    }

    [Benchmark(Baseline = true), BenchmarkCategory("MemorySerialize")]
    public int NinoSerializeMemory()
    {
        _ninoBuffer.ResetWrittenCount();
        NinoGen.Serializer.Serialize(MemoryValue.Span, _ninoBuffer);
        return _ninoBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("MemorySerialize")]
    public int MemoryPackSerializeMemory()
    {
        _memoryPackBuffer.ResetWrittenCount();
        MemoryPackSerializer.Serialize(_memoryPackBuffer, MemoryValue);
        return _memoryPackBuffer.WrittenCount;
    }

    [Benchmark(Baseline = true), BenchmarkCategory("MemoryDeserialize")]
    public object NinoDeserializeMemory()
    {
        NinoGen.Deserializer.Deserialize(NinoMemoryBytes, out int[] ret);
        return new Memory<int>(ret);
    }

    [Benchmark, BenchmarkCategory("MemoryDeserialize")]
    public object MemoryPackDeserializeMemory() => MemoryPackSerializer.Deserialize<Memory<int>>(MemoryPackMemoryBytes);

    [Benchmark(Baseline = true), BenchmarkCategory("ReadOnlySequenceSerialize")]
    public int NinoSerializeReadOnlySequence()
    {
        _ninoBuffer.ResetWrittenCount();
        // One segment: its span, through v3's Span<T> overload (a sequence of several would be copied into one).
        NinoGen.Serializer.Serialize(MemoryMarshal.AsMemory(ReadOnlySequenceValue.First).Span, _ninoBuffer);
        return _ninoBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("ReadOnlySequenceSerialize")]
    public int MemoryPackSerializeReadOnlySequence()
    {
        _memoryPackBuffer.ResetWrittenCount();
        MemoryPackSerializer.Serialize(_memoryPackBuffer, ReadOnlySequenceValue);
        return _memoryPackBuffer.WrittenCount;
    }

    [Benchmark(Baseline = true), BenchmarkCategory("ReadOnlySequenceDeserialize")]
    public object NinoDeserializeReadOnlySequence()
    {
        NinoGen.Deserializer.Deserialize(NinoReadOnlySequenceBytes, out int[] ret);
        return new ReadOnlySequence<int>(ret);
    }

    [Benchmark, BenchmarkCategory("ReadOnlySequenceDeserialize")]
    public object MemoryPackDeserializeReadOnlySequence() =>
        MemoryPackSerializer.Deserialize<ReadOnlySequence<int>>(MemoryPackReadOnlySequenceBytes);

    [Benchmark(Baseline = true), BenchmarkCategory("ReadOnlyCollectionSerialize")]
    public int NinoSerializeReadOnlyCollection()
    {
        _ninoBuffer.ResetWrittenCount();
        NinoGen.Serializer.Serialize(ReadOnlyCollectionValue, _ninoBuffer);
        return _ninoBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("ReadOnlyCollectionSerialize")]
    public int MemoryPackSerializeReadOnlyCollection()
    {
        _memoryPackBuffer.ResetWrittenCount();
        MemoryPackSerializer.Serialize(_memoryPackBuffer, ReadOnlyCollectionValue);
        return _memoryPackBuffer.WrittenCount;
    }

    [Benchmark(Baseline = true), BenchmarkCategory("ReadOnlyCollectionDeserialize")]
    public object NinoDeserializeReadOnlyCollection()
    {
        NinoGen.Deserializer.Deserialize(NinoReadOnlyCollectionBytes, out ReadOnlyCollection<int> ret);
        return ret;
    }

    [Benchmark, BenchmarkCategory("ReadOnlyCollectionDeserialize")]
    public object MemoryPackDeserializeReadOnlyCollection() =>
        MemoryPackSerializer.Deserialize<ReadOnlyCollection<int>>(MemoryPackReadOnlyCollectionBytes);

    [Benchmark(Baseline = true), BenchmarkCategory("InterfaceStringsSerialize")]
    public int NinoSerializeInterfaceStrings()
    {
        _ninoBuffer.ResetWrittenCount();
        // v3 writes no IReadOnlyList<T> (CS8377), but writes ICollection<T>, which the list is too.
        NinoGen.Serializer.Serialize((ICollection<string>)InterfaceStringsValue, _ninoBuffer);
        return _ninoBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("InterfaceStringsSerialize")]
    public int MemoryPackSerializeInterfaceStrings()
    {
        _memoryPackBuffer.ResetWrittenCount();
        MemoryPackSerializer.Serialize(_memoryPackBuffer, InterfaceStringsValue);
        return _memoryPackBuffer.WrittenCount;
    }

    [Benchmark(Baseline = true), BenchmarkCategory("InterfaceStringsDeserialize")]
    public object NinoDeserializeInterfaceStrings()
    {
        // v3 reads no IReadOnlyList<T> (CS8377): a List<string>, which is one.
        NinoGen.Deserializer.Deserialize(NinoInterfaceStringsBytes, out List<string> ret);
        return ret;
    }

    [Benchmark, BenchmarkCategory("InterfaceStringsDeserialize")]
    public object MemoryPackDeserializeInterfaceStrings() =>
        MemoryPackSerializer.Deserialize<IReadOnlyList<string>>(MemoryPackInterfaceStringsBytes);

    [Benchmark(Baseline = true), BenchmarkCategory("BlockingCollectionSerialize")]
    public int NinoSerializeBlockingCollection()
    {
        _ninoBuffer.ResetWrittenCount();
        NinoGen.Serializer.Serialize(BlockingCollectionValue, _ninoBuffer);
        return _ninoBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("BlockingCollectionSerialize")]
    public int MemoryPackSerializeBlockingCollection()
    {
        _memoryPackBuffer.ResetWrittenCount();
        MemoryPackSerializer.Serialize(_memoryPackBuffer, BlockingCollectionValue);
        return _memoryPackBuffer.WrittenCount;
    }

    [Benchmark(Baseline = true), BenchmarkCategory("BlockingCollectionDeserialize")]
    public object NinoDeserializeBlockingCollection()
    {
        NinoGen.Deserializer.Deserialize(NinoBlockingCollectionBytes, out BlockingCollection<int> ret);
        return ret;
    }

    [Benchmark, BenchmarkCategory("BlockingCollectionDeserialize")]
    public object MemoryPackDeserializeBlockingCollection() =>
        MemoryPackSerializer.Deserialize<BlockingCollection<int>>(MemoryPackBlockingCollectionBytes);

    [Benchmark(Baseline = true), BenchmarkCategory("ImmutableHashSetSerialize")]
    public int NinoSerializeImmutableHashSet()
    {
        _ninoBuffer.ResetWrittenCount();
        NinoGen.Serializer.Serialize(ImmutableHashSetValue, _ninoBuffer);
        return _ninoBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("ImmutableHashSetSerialize")]
    public int MemoryPackSerializeImmutableHashSet()
    {
        _memoryPackBuffer.ResetWrittenCount();
        MemoryPackSerializer.Serialize(_memoryPackBuffer, ImmutableHashSetValue);
        return _memoryPackBuffer.WrittenCount;
    }

    [Benchmark(Baseline = true), BenchmarkCategory("ImmutableHashSetDeserialize")]
    public object NinoDeserializeImmutableHashSet()
    {
        NinoGen.Deserializer.Deserialize(NinoImmutableHashSetBytes, out int[] ret);
        return ImmutableHashSet.CreateRange(ret);
    }

    [Benchmark, BenchmarkCategory("ImmutableHashSetDeserialize")]
    public object MemoryPackDeserializeImmutableHashSet() =>
        MemoryPackSerializer.Deserialize<ImmutableHashSet<int>>(MemoryPackImmutableHashSetBytes);

    [Benchmark(Baseline = true), BenchmarkCategory("ImmutableSortedSetSerialize")]
    public int NinoSerializeImmutableSortedSet()
    {
        _ninoBuffer.ResetWrittenCount();
        NinoGen.Serializer.Serialize(ImmutableSortedSetValue, _ninoBuffer);
        return _ninoBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("ImmutableSortedSetSerialize")]
    public int MemoryPackSerializeImmutableSortedSet()
    {
        _memoryPackBuffer.ResetWrittenCount();
        MemoryPackSerializer.Serialize(_memoryPackBuffer, ImmutableSortedSetValue);
        return _memoryPackBuffer.WrittenCount;
    }

    [Benchmark(Baseline = true), BenchmarkCategory("ImmutableSortedSetDeserialize")]
    public object NinoDeserializeImmutableSortedSet()
    {
        NinoGen.Deserializer.Deserialize(NinoImmutableSortedSetBytes, out int[] ret);
        return ImmutableSortedSet.CreateRange(ret);
    }

    [Benchmark, BenchmarkCategory("ImmutableSortedSetDeserialize")]
    public object MemoryPackDeserializeImmutableSortedSet() =>
        MemoryPackSerializer.Deserialize<ImmutableSortedSet<int>>(MemoryPackImmutableSortedSetBytes);

    [Benchmark(Baseline = true), BenchmarkCategory("ImmutableQueueSerialize")]
    public int NinoSerializeImmutableQueue()
    {
        _ninoBuffer.ResetWrittenCount();
        NinoGen.Serializer.Serialize(ImmutableQueueValue.ToArray(), _ninoBuffer);
        return _ninoBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("ImmutableQueueSerialize")]
    public int MemoryPackSerializeImmutableQueue()
    {
        _memoryPackBuffer.ResetWrittenCount();
        MemoryPackSerializer.Serialize(_memoryPackBuffer, ImmutableQueueValue);
        return _memoryPackBuffer.WrittenCount;
    }

    [Benchmark(Baseline = true), BenchmarkCategory("ImmutableQueueDeserialize")]
    public object NinoDeserializeImmutableQueue()
    {
        NinoGen.Deserializer.Deserialize(NinoImmutableQueueBytes, out int[] ret);
        return ImmutableQueue.CreateRange(ret);
    }

    [Benchmark, BenchmarkCategory("ImmutableQueueDeserialize")]
    public object MemoryPackDeserializeImmutableQueue() =>
        MemoryPackSerializer.Deserialize<ImmutableQueue<int>>(MemoryPackImmutableQueueBytes);

    [Benchmark(Baseline = true), BenchmarkCategory("ImmutableStackSerialize")]
    public int NinoSerializeImmutableStack()
    {
        _ninoBuffer.ResetWrittenCount();
        NinoGen.Serializer.Serialize(ImmutableStackValue.ToArray(), _ninoBuffer);
        return _ninoBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("ImmutableStackSerialize")]
    public int MemoryPackSerializeImmutableStack()
    {
        _memoryPackBuffer.ResetWrittenCount();
        MemoryPackSerializer.Serialize(_memoryPackBuffer, ImmutableStackValue);
        return _memoryPackBuffer.WrittenCount;
    }

    [Benchmark(Baseline = true), BenchmarkCategory("ImmutableStackDeserialize")]
    public object NinoDeserializeImmutableStack()
    {
        // Written top first: pushed back from the bottom.
        NinoGen.Deserializer.Deserialize(NinoImmutableStackBytes, out int[] ret);
        var stack = ImmutableStack<int>.Empty;
        for (int i = ret.Length - 1; i >= 0; i--)
            stack = stack.Push(ret[i]);
        return stack;
    }

    [Benchmark, BenchmarkCategory("ImmutableStackDeserialize")]
    public object MemoryPackDeserializeImmutableStack() =>
        MemoryPackSerializer.Deserialize<ImmutableStack<int>>(MemoryPackImmutableStackBytes);

    [Benchmark(Baseline = true), BenchmarkCategory("ImmutableDictionarySerialize")]
    public int NinoSerializeImmutableDictionary()
    {
        _ninoBuffer.ResetWrittenCount();
        NinoGen.Serializer.Serialize(ImmutableDictionaryValue, _ninoBuffer);
        return _ninoBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("ImmutableDictionarySerialize")]
    public int MemoryPackSerializeImmutableDictionary()
    {
        _memoryPackBuffer.ResetWrittenCount();
        MemoryPackSerializer.Serialize(_memoryPackBuffer, ImmutableDictionaryValue);
        return _memoryPackBuffer.WrittenCount;
    }

    [Benchmark(Baseline = true), BenchmarkCategory("ImmutableDictionaryDeserialize")]
    public object NinoDeserializeImmutableDictionary()
    {
        NinoGen.Deserializer.Deserialize(NinoImmutableDictionaryBytes, out KeyValuePair<int, int>[] ret);
        return ImmutableDictionary.CreateRange(ret);
    }

    [Benchmark, BenchmarkCategory("ImmutableDictionaryDeserialize")]
    public object MemoryPackDeserializeImmutableDictionary() =>
        MemoryPackSerializer.Deserialize<ImmutableDictionary<int, int>>(MemoryPackImmutableDictionaryBytes);

    [Benchmark(Baseline = true), BenchmarkCategory("ImmutableSortedDictionarySerialize")]
    public int NinoSerializeImmutableSortedDictionary()
    {
        _ninoBuffer.ResetWrittenCount();
        NinoGen.Serializer.Serialize(ImmutableSortedDictionaryValue, _ninoBuffer);
        return _ninoBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("ImmutableSortedDictionarySerialize")]
    public int MemoryPackSerializeImmutableSortedDictionary()
    {
        _memoryPackBuffer.ResetWrittenCount();
        MemoryPackSerializer.Serialize(_memoryPackBuffer, ImmutableSortedDictionaryValue);
        return _memoryPackBuffer.WrittenCount;
    }

    [Benchmark(Baseline = true), BenchmarkCategory("ImmutableSortedDictionaryDeserialize")]
    public object NinoDeserializeImmutableSortedDictionary()
    {
        NinoGen.Deserializer.Deserialize(NinoImmutableSortedDictionaryBytes, out KeyValuePair<int, int>[] ret);
        return ImmutableSortedDictionary.CreateRange(ret);
    }

    [Benchmark, BenchmarkCategory("ImmutableSortedDictionaryDeserialize")]
    public object MemoryPackDeserializeImmutableSortedDictionary() =>
        MemoryPackSerializer.Deserialize<ImmutableSortedDictionary<int, int>>(MemoryPackImmutableSortedDictionaryBytes);
}

