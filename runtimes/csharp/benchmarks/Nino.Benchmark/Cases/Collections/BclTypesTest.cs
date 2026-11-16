using System;
using System.Buffers;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Linq;
using BenchmarkDotNet.Configs;
#if !NINO_BENCH_V3
using System.Globalization;
using System.Numerics;
using System.Text;
using MessagePack;
using MessagePack.ImmutableCollection;
using MessagePack.Resolvers;
#else
using System.Runtime.InteropServices;
using BenchmarkDotNet.Attributes;
#endif

#nullable disable
namespace Nino.Benchmark;

/// <summary>
/// The base class library types both libraries have formatters for beyond the common collections: BCL classes
/// (<c>BigInteger</c>, <c>Uri</c>, <c>Version</c>, <c>StringBuilder</c>, <c>BitArray</c>, <c>CultureInfo</c>,
/// <c>TimeZoneInfo</c>, <c>Lazy&lt;T&gt;</c>), memory and sequences, read-only and lookup collections, and the
/// immutable collections (Nino.Immutable), collections with 1,000 elements, serialized to a reused buffer writer and
/// deserialized into new instances. Deserialize cases return <c>object</c>: BenchmarkDotNet refuses to time a method
/// that returns a lazy or enumerable result. MessagePack has formatters for all but <c>CultureInfo</c>,
/// <c>TimeZoneInfo</c> and <c>BlockingCollection&lt;T&gt;</c> (it throws on them); protobuf-net for <c>Uri</c> and
/// the immutable collections only.
/// The timed methods are BclTypesTest.Cases.g.cs, generated from the cases below and the adapters (Adapters/,
/// runtimes/csharp/tools/BenchGen). The v3 build times Nino 3.3.13 and MemoryPack on the memory and sequences, the
/// read-only, blocking and immutable collections; where 3.3.13 writes no overload for the type, its case takes the
/// shortest way through one it does write (its generator's selector, CollectionSerializerGenerator.Selector, accepts
/// unmanaged types, [NinoType]s, strings, key-value pairs, tuples, nullables and IEnumerable&lt;T&gt; of those; anything
/// else binds to the generic Serialize&lt;T&gt;(T) where T : unmanaged, CS8377, and v3 has no API for a formatter of
/// one's own), in the methods below:
///  - Memory&lt;int&gt;, ReadOnlySequence&lt;int&gt; (one segment): written through v3's Serialize(Span&lt;T&gt;) (the
///    value's span, nothing copied), read as int[] and wrapped.
///  - IReadOnlyList&lt;string&gt; (InterfaceStrings): written as the ICollection&lt;string&gt; it also is, read as a
///    List&lt;string&gt;, which is an IReadOnlyList&lt;string&gt;.
///  - ImmutableQueue&lt;int&gt;, ImmutableStack&lt;int&gt;: v3 writes neither, so the cases write ToArray() of them, a
///    copy timed with them.
///  - The immutable collections read back: v3 reads none of them (it builds a collection only through a constructor
///    or Add), so the cases read an int[] (KeyValuePair&lt;int, int&gt;[] for the dictionaries) and build the
///    collection from it with CreateRange (the stack pushed back from its bottom).
/// Left out of the v3 build: BigInteger, Uri, Version, StringBuilder, BitArray, CultureInfo, TimeZoneInfo and
/// Lazy&lt;string&gt;, which v3 cannot write or read (converting each into another type by hand would time that
/// conversion rather than v3), and ILookup&lt;int, int&gt;: naming the type makes v3's generator write code that does
/// not compile (CS8377 on IGrouping&lt;int, int&gt; in its NinoSerializer.Collection.g.cs).
/// </summary>
[BenchCase("BigInteger", Without = "MemoryPack Protobuf", Builds = "v4")]
[BenchCase("Uri", Builds = "v4")]
[BenchCase("Version", Without = "Protobuf", Builds = "v4")]
[BenchCase("StringBuilder", Quick = true, Without = "Protobuf", Builds = "v4")]
[BenchCase("BitArray", Without = "Protobuf", Builds = "v4", Broken = "LuminPack crash: the process dies reading it (SIGSEGV)")]
[BenchCase("CultureInfo", Without = "MessagePack Protobuf", Builds = "v4")]
[BenchCase("TimeZoneInfo", Without = "MessagePack Protobuf", Builds = "v4")]
[BenchCase("Lazy", Without = "Protobuf", Builds = "v4")]
[BenchCase("Memory", Without = "Protobuf")]
[BenchCase("ReadOnlySequence", Without = "Protobuf")]
[BenchCase("ReadOnlyCollection", Without = "Protobuf")]
[BenchCase("InterfaceStrings", Without = "Protobuf")]
[BenchCase("Lookup", Without = "Protobuf", Builds = "v4")]
[BenchCase("BlockingCollection", Without = "MessagePack Protobuf")]
[BenchCase("ImmutableHashSet", MessagePackOptions = "ImmutableOptions")]
[BenchCase("ImmutableSortedSet", MessagePackOptions = "ImmutableOptions")]
[BenchCase("ImmutableQueue", MessagePackOptions = "ImmutableOptions")]
[BenchCase("ImmutableStack", MessagePackOptions = "ImmutableOptions")]
[BenchCase("ImmutableDictionary", MessagePackOptions = "ImmutableOptions")]
[BenchCase("ImmutableSortedDictionary", MessagePackOptions = "ImmutableOptions")]
[BenchClass(Buffer = "1 << 16", Value = "{Case}Value", Bytes = "{Library}{Case}Bytes", Returns = "object", Order = "case")]
[PayloadColumn]
[HideColumns("StdDev", "RatioSD", "Error")]
[MinColumn, MaxColumn]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[NinoJob]
[GcServer(true)]
[MemoryDiagnoser]
[MarkdownExporterAttribute.GitHub]
public partial class BclTypesTest
{
    private const int N = 1000;

#if !NINO_BENCH_V3
    // MessagePack's formatters for the immutable collections are a package of their own (MessagePack.ImmutableCollection).
    private static readonly MessagePackSerializerOptions ImmutableOptions = MessagePackSerializerOptions.Standard
        .WithResolver(CompositeResolver.Create(ImmutableCollectionResolver.Instance, StandardResolver.Instance));

    private static readonly BigInteger BigIntegerValue = BigInteger.Pow(3, 1000);
    private static readonly Uri UriValue = new("https://example.com/api/v1/items?id=42&sort=name#top");
    private static readonly Version VersionValue = new(4, 0, 1, 7);
    private static readonly StringBuilder StringBuilderValue = new(string.Concat(Enumerable.Range(0, N).Select(i => (char)('a' + i % 26))));
    // MemoryPack 1.21.4 reinterpreted a BitArray as { int[] m_array; int m_length }, its layout on .NET 8; .NET 10's
    // BitArray holds a byte[] _array, so it wrote the wrong bytes and reading one corrupted the heap. 1.22.0 goes
    // through BitArray's public API, so both libraries are timed.
    private static readonly BitArray BitArrayValue = new(Enumerable.Range(0, N).Select(i => i % 3 == 0).ToArray());
    private static readonly CultureInfo CultureInfoValue = CultureInfo.GetCultureInfo("en-US");
    private static readonly TimeZoneInfo TimeZoneInfoValue = TimeZoneInfo.Utc;
    private static readonly Lazy<string> LazyValue = new(() => "a lazily created value");
    private static readonly ILookup<int, int> LookupValue = Enumerable.Range(0, N).ToLookup(i => i % 10);
#endif
    private static readonly Memory<int> MemoryValue = new(Enumerable.Range(0, N).ToArray());
    private static readonly ReadOnlySequence<int> ReadOnlySequenceValue = new(Enumerable.Range(0, N).ToArray());
    private static readonly ReadOnlyCollection<int> ReadOnlyCollectionValue = new(Enumerable.Range(0, N).ToList());
    // Strings behind an interface: elements written one by one, through ICollection<T> (Nino copies them out from
    // NinoCollections.CopiedFrom elements on, instead of enumerating).
    private static readonly IReadOnlyList<string> InterfaceStringsValue = new ReadOnlyCollection<string>(Enumerable.Range(0, N).Select(i => "item" + i).ToList());
    private static readonly BlockingCollection<int> BlockingCollectionValue = MakeBlockingCollection();
    private static readonly ImmutableHashSet<int> ImmutableHashSetValue = ImmutableHashSet.CreateRange(Enumerable.Range(0, N));
    private static readonly ImmutableSortedSet<int> ImmutableSortedSetValue = ImmutableSortedSet.CreateRange(Enumerable.Range(0, N));
    private static readonly ImmutableQueue<int> ImmutableQueueValue = ImmutableQueue.CreateRange(Enumerable.Range(0, N));
    private static readonly ImmutableStack<int> ImmutableStackValue = ImmutableStack.CreateRange(Enumerable.Range(0, N));
    private static readonly ImmutableDictionary<int, int> ImmutableDictionaryValue = ImmutableDictionary.CreateRange(Enumerable.Range(0, N).Select(i => new KeyValuePair<int, int>(i, i * 7)));
    private static readonly ImmutableSortedDictionary<int, int> ImmutableSortedDictionaryValue = ImmutableSortedDictionary.CreateRange(Enumerable.Range(0, N).Select(i => new KeyValuePair<int, int>(i, i * 7)));

    private static BlockingCollection<int> MakeBlockingCollection()
    {
        var collection = new BlockingCollection<int>();
        for (int i = 0; i < N; i++)
            collection.Add(i);
        return collection;
    }
#if NINO_BENCH_V3

    // Nino 3.3.13's ways through the types it writes no overload for (above).
    private static readonly byte[] NinoMemoryBytes = NinoGen.Serializer.Serialize(MemoryValue.Span);
    private static readonly byte[] NinoReadOnlySequenceBytes =
        NinoGen.Serializer.Serialize(MemoryMarshal.AsMemory(ReadOnlySequenceValue.First).Span);
    private static readonly byte[] NinoInterfaceStringsBytes =
        NinoGen.Serializer.Serialize((ICollection<string>)InterfaceStringsValue);
    private static readonly byte[] NinoImmutableQueueBytes = NinoGen.Serializer.Serialize(ImmutableQueueValue.ToArray());
    private static readonly byte[] NinoImmutableStackBytes = NinoGen.Serializer.Serialize(ImmutableStackValue.ToArray());

    [Benchmark(Baseline = true), BenchmarkCategory("MemorySerialize")]
    public int NinoSerializeMemory()
    {
        _ninoBuffer.ResetWrittenCount();
        NinoGen.Serializer.Serialize(MemoryValue.Span, _ninoBuffer);
        return _ninoBuffer.WrittenCount;
    }

    [Benchmark(Baseline = true), BenchmarkCategory("MemoryDeserialize")]
    public object NinoDeserializeMemory()
    {
        NinoGen.Deserializer.Deserialize(NinoMemoryBytes, out int[] ret);
        return new Memory<int>(ret);
    }

    [Benchmark(Baseline = true), BenchmarkCategory("ReadOnlySequenceSerialize")]
    public int NinoSerializeReadOnlySequence()
    {
        _ninoBuffer.ResetWrittenCount();
        // One segment: its span, through v3's Span<T> overload (a sequence of several would be copied into one).
        NinoGen.Serializer.Serialize(MemoryMarshal.AsMemory(ReadOnlySequenceValue.First).Span, _ninoBuffer);
        return _ninoBuffer.WrittenCount;
    }

    [Benchmark(Baseline = true), BenchmarkCategory("ReadOnlySequenceDeserialize")]
    public object NinoDeserializeReadOnlySequence()
    {
        NinoGen.Deserializer.Deserialize(NinoReadOnlySequenceBytes, out int[] ret);
        return new ReadOnlySequence<int>(ret);
    }

    [Benchmark(Baseline = true), BenchmarkCategory("InterfaceStringsSerialize")]
    public int NinoSerializeInterfaceStrings()
    {
        _ninoBuffer.ResetWrittenCount();
        // v3 writes no IReadOnlyList<T> (CS8377), but writes ICollection<T>, which the list is too.
        NinoGen.Serializer.Serialize((ICollection<string>)InterfaceStringsValue, _ninoBuffer);
        return _ninoBuffer.WrittenCount;
    }

    [Benchmark(Baseline = true), BenchmarkCategory("InterfaceStringsDeserialize")]
    public object NinoDeserializeInterfaceStrings()
    {
        // v3 reads no IReadOnlyList<T> (CS8377): a List<string>, which is one.
        NinoGen.Deserializer.Deserialize(NinoInterfaceStringsBytes, out List<string> ret);
        return ret;
    }

    [Benchmark(Baseline = true), BenchmarkCategory("ImmutableHashSetDeserialize")]
    public object NinoDeserializeImmutableHashSet()
    {
        NinoGen.Deserializer.Deserialize(NinoImmutableHashSetBytes, out int[] ret);
        return ImmutableHashSet.CreateRange(ret);
    }

    [Benchmark(Baseline = true), BenchmarkCategory("ImmutableSortedSetDeserialize")]
    public object NinoDeserializeImmutableSortedSet()
    {
        NinoGen.Deserializer.Deserialize(NinoImmutableSortedSetBytes, out int[] ret);
        return ImmutableSortedSet.CreateRange(ret);
    }

    [Benchmark(Baseline = true), BenchmarkCategory("ImmutableQueueSerialize")]
    public int NinoSerializeImmutableQueue()
    {
        _ninoBuffer.ResetWrittenCount();
        NinoGen.Serializer.Serialize(ImmutableQueueValue.ToArray(), _ninoBuffer);
        return _ninoBuffer.WrittenCount;
    }

    [Benchmark(Baseline = true), BenchmarkCategory("ImmutableQueueDeserialize")]
    public object NinoDeserializeImmutableQueue()
    {
        NinoGen.Deserializer.Deserialize(NinoImmutableQueueBytes, out int[] ret);
        return ImmutableQueue.CreateRange(ret);
    }

    [Benchmark(Baseline = true), BenchmarkCategory("ImmutableStackSerialize")]
    public int NinoSerializeImmutableStack()
    {
        _ninoBuffer.ResetWrittenCount();
        NinoGen.Serializer.Serialize(ImmutableStackValue.ToArray(), _ninoBuffer);
        return _ninoBuffer.WrittenCount;
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

    [Benchmark(Baseline = true), BenchmarkCategory("ImmutableDictionaryDeserialize")]
    public object NinoDeserializeImmutableDictionary()
    {
        NinoGen.Deserializer.Deserialize(NinoImmutableDictionaryBytes, out KeyValuePair<int, int>[] ret);
        return ImmutableDictionary.CreateRange(ret);
    }

    [Benchmark(Baseline = true), BenchmarkCategory("ImmutableSortedDictionaryDeserialize")]
    public object NinoDeserializeImmutableSortedDictionary()
    {
        NinoGen.Deserializer.Deserialize(NinoImmutableSortedDictionaryBytes, out KeyValuePair<int, int>[] ret);
        return ImmutableSortedDictionary.CreateRange(ret);
    }
#endif
}
