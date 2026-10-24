using System;
using System.Buffers;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using MemoryPack;
using MessagePack;
using MessagePack.ImmutableCollection;
using MessagePack.Resolvers;
using Nino.Core;
using ProtoBuf;

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

    // MessagePack's formatters for the immutable collections are a package of their own (MessagePack.ImmutableCollection).
    private static readonly MessagePackSerializerOptions ImmutableOptions = MessagePackSerializerOptions.Standard
        .WithResolver(CompositeResolver.Create(ImmutableCollectionResolver.Instance, StandardResolver.Instance));

    private static readonly BigInteger BigIntegerValue = BigInteger.Pow(3, 1000);
    private static readonly Uri UriValue = new Uri("https://example.com/api/v1/items?id=42&sort=name#top");
    private static readonly Version VersionValue = new Version(4, 0, 1, 7);
    private static readonly StringBuilder StringBuilderValue = new StringBuilder(string.Concat(Enumerable.Range(0, N).Select(i => (char)('a' + i % 26))));
    private static readonly BitArray BitArrayValue = new BitArray(Enumerable.Range(0, N).Select(i => i % 3 == 0).ToArray());
    private static readonly CultureInfo CultureInfoValue = CultureInfo.GetCultureInfo("en-US");
    private static readonly TimeZoneInfo TimeZoneInfoValue = TimeZoneInfo.Utc;
    private static readonly Lazy<string> LazyValue = new Lazy<string>(() => "a lazily created value");
    private static readonly Memory<int> MemoryValue = new Memory<int>(Enumerable.Range(0, N).ToArray());
    private static readonly ReadOnlySequence<int> ReadOnlySequenceValue = new ReadOnlySequence<int>(Enumerable.Range(0, N).ToArray());
    private static readonly ReadOnlyCollection<int> ReadOnlyCollectionValue = new ReadOnlyCollection<int>(Enumerable.Range(0, N).ToList());
    private static readonly ILookup<int, int> LookupValue = Enumerable.Range(0, N).ToLookup(i => i % 10);
    private static readonly BlockingCollection<int> BlockingCollectionValue = MakeBlockingCollection();
    private static readonly ImmutableHashSet<int> ImmutableHashSetValue = ImmutableHashSet.CreateRange(Enumerable.Range(0, N));
    private static readonly ImmutableSortedSet<int> ImmutableSortedSetValue = ImmutableSortedSet.CreateRange(Enumerable.Range(0, N));
    private static readonly ImmutableQueue<int> ImmutableQueueValue = ImmutableQueue.CreateRange(Enumerable.Range(0, N));
    private static readonly ImmutableStack<int> ImmutableStackValue = ImmutableStack.CreateRange(Enumerable.Range(0, N));
    private static readonly ImmutableDictionary<int, int> ImmutableDictionaryValue = ImmutableDictionary.CreateRange(Enumerable.Range(0, N).Select(i => new KeyValuePair<int, int>(i, i * 7)));
    private static readonly ImmutableSortedDictionary<int, int> ImmutableSortedDictionaryValue = ImmutableSortedDictionary.CreateRange(Enumerable.Range(0, N).Select(i => new KeyValuePair<int, int>(i, i * 7)));

    private static readonly byte[] NinoBigIntegerBytes = NinoSerializer.Serialize(BigIntegerValue);
    private static readonly byte[] MessagePackBigIntegerBytes = MessagePackSerializer.Serialize(BigIntegerValue);
    private static readonly byte[] NinoUriBytes = NinoSerializer.Serialize(UriValue);
    private static readonly byte[] MessagePackUriBytes = MessagePackSerializer.Serialize(UriValue);
    private static readonly byte[] ProtobufUriBytes = ProtobufModel.Serialize(UriValue);
    private static readonly byte[] MemoryPackUriBytes = MemoryPackSerializer.Serialize(UriValue);
    private static readonly byte[] NinoVersionBytes = NinoSerializer.Serialize(VersionValue);
    private static readonly byte[] MessagePackVersionBytes = MessagePackSerializer.Serialize(VersionValue);
    private static readonly byte[] MemoryPackVersionBytes = MemoryPackSerializer.Serialize(VersionValue);
    private static readonly byte[] NinoStringBuilderBytes = NinoSerializer.Serialize(StringBuilderValue);
    private static readonly byte[] MessagePackStringBuilderBytes = MessagePackSerializer.Serialize(StringBuilderValue);
    private static readonly byte[] MemoryPackStringBuilderBytes = MemoryPackSerializer.Serialize(StringBuilderValue);
    private static readonly byte[] NinoBitArrayBytes = NinoSerializer.Serialize(BitArrayValue);
    private static readonly byte[] MessagePackBitArrayBytes = MessagePackSerializer.Serialize(BitArrayValue);
    private static readonly byte[] NinoCultureInfoBytes = NinoSerializer.Serialize(CultureInfoValue);
    private static readonly byte[] MemoryPackCultureInfoBytes = MemoryPackSerializer.Serialize(CultureInfoValue);
    private static readonly byte[] NinoTimeZoneInfoBytes = NinoSerializer.Serialize(TimeZoneInfoValue);
    private static readonly byte[] MemoryPackTimeZoneInfoBytes = MemoryPackSerializer.Serialize(TimeZoneInfoValue);
    private static readonly byte[] NinoLazyBytes = NinoSerializer.Serialize(LazyValue);
    private static readonly byte[] MessagePackLazyBytes = MessagePackSerializer.Serialize(LazyValue);
    private static readonly byte[] MemoryPackLazyBytes = MemoryPackSerializer.Serialize(LazyValue);
    private static readonly byte[] NinoMemoryBytes = NinoSerializer.Serialize(MemoryValue);
    private static readonly byte[] MessagePackMemoryBytes = MessagePackSerializer.Serialize(MemoryValue);
    private static readonly byte[] MemoryPackMemoryBytes = MemoryPackSerializer.Serialize(MemoryValue);
    private static readonly byte[] NinoReadOnlySequenceBytes = NinoSerializer.Serialize(ReadOnlySequenceValue);
    private static readonly byte[] MessagePackReadOnlySequenceBytes = MessagePackSerializer.Serialize(ReadOnlySequenceValue);
    private static readonly byte[] MemoryPackReadOnlySequenceBytes = MemoryPackSerializer.Serialize(ReadOnlySequenceValue);
    private static readonly byte[] NinoReadOnlyCollectionBytes = NinoSerializer.Serialize(ReadOnlyCollectionValue);
    private static readonly byte[] MessagePackReadOnlyCollectionBytes = MessagePackSerializer.Serialize(ReadOnlyCollectionValue);
    private static readonly byte[] MemoryPackReadOnlyCollectionBytes = MemoryPackSerializer.Serialize(ReadOnlyCollectionValue);
    private static readonly byte[] NinoLookupBytes = NinoSerializer.Serialize(LookupValue);
    private static readonly byte[] MessagePackLookupBytes = MessagePackSerializer.Serialize(LookupValue);
    private static readonly byte[] MemoryPackLookupBytes = MemoryPackSerializer.Serialize(LookupValue);
    private static readonly byte[] NinoBlockingCollectionBytes = NinoSerializer.Serialize(BlockingCollectionValue);
    private static readonly byte[] MemoryPackBlockingCollectionBytes = MemoryPackSerializer.Serialize(BlockingCollectionValue);
    private static readonly byte[] NinoImmutableHashSetBytes = NinoSerializer.Serialize(ImmutableHashSetValue);
    private static readonly byte[] MessagePackImmutableHashSetBytes = MessagePackSerializer.Serialize(ImmutableHashSetValue, ImmutableOptions);
    private static readonly byte[] ProtobufImmutableHashSetBytes = ProtobufModel.Serialize(ImmutableHashSetValue);
    private static readonly byte[] MemoryPackImmutableHashSetBytes = MemoryPackSerializer.Serialize(ImmutableHashSetValue);
    private static readonly byte[] NinoImmutableSortedSetBytes = NinoSerializer.Serialize(ImmutableSortedSetValue);
    private static readonly byte[] MessagePackImmutableSortedSetBytes = MessagePackSerializer.Serialize(ImmutableSortedSetValue, ImmutableOptions);
    private static readonly byte[] ProtobufImmutableSortedSetBytes = ProtobufModel.Serialize(ImmutableSortedSetValue);
    private static readonly byte[] MemoryPackImmutableSortedSetBytes = MemoryPackSerializer.Serialize(ImmutableSortedSetValue);
    private static readonly byte[] NinoImmutableQueueBytes = NinoSerializer.Serialize(ImmutableQueueValue);
    private static readonly byte[] MessagePackImmutableQueueBytes = MessagePackSerializer.Serialize(ImmutableQueueValue, ImmutableOptions);
    private static readonly byte[] ProtobufImmutableQueueBytes = ProtobufModel.Serialize(ImmutableQueueValue);
    private static readonly byte[] MemoryPackImmutableQueueBytes = MemoryPackSerializer.Serialize(ImmutableQueueValue);
    private static readonly byte[] NinoImmutableStackBytes = NinoSerializer.Serialize(ImmutableStackValue);
    private static readonly byte[] MessagePackImmutableStackBytes = MessagePackSerializer.Serialize(ImmutableStackValue, ImmutableOptions);
    private static readonly byte[] ProtobufImmutableStackBytes = ProtobufModel.Serialize(ImmutableStackValue);
    private static readonly byte[] MemoryPackImmutableStackBytes = MemoryPackSerializer.Serialize(ImmutableStackValue);
    private static readonly byte[] NinoImmutableDictionaryBytes = NinoSerializer.Serialize(ImmutableDictionaryValue);
    private static readonly byte[] MessagePackImmutableDictionaryBytes = MessagePackSerializer.Serialize(ImmutableDictionaryValue, ImmutableOptions);
    private static readonly byte[] ProtobufImmutableDictionaryBytes = ProtobufModel.Serialize(ImmutableDictionaryValue);
    private static readonly byte[] MemoryPackImmutableDictionaryBytes = MemoryPackSerializer.Serialize(ImmutableDictionaryValue);
    private static readonly byte[] NinoImmutableSortedDictionaryBytes = NinoSerializer.Serialize(ImmutableSortedDictionaryValue);
    private static readonly byte[] MessagePackImmutableSortedDictionaryBytes = MessagePackSerializer.Serialize(ImmutableSortedDictionaryValue, ImmutableOptions);
    private static readonly byte[] ProtobufImmutableSortedDictionaryBytes = ProtobufModel.Serialize(ImmutableSortedDictionaryValue);
    private static readonly byte[] MemoryPackImmutableSortedDictionaryBytes = MemoryPackSerializer.Serialize(ImmutableSortedDictionaryValue);

    private NinoArrayBufferWriter _ninoBuffer;
    private ArrayBufferWriter<byte> _memoryPackBuffer;

    static BclTypesTest()
    {
        BenchmarkPayloadRegistry.Register(nameof(NinoSerializeBigInteger), NinoBigIntegerBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(NinoSerializeUri), NinoUriBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(MemoryPackSerializeUri), MemoryPackUriBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(NinoSerializeVersion), NinoVersionBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(MemoryPackSerializeVersion), MemoryPackVersionBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(NinoSerializeStringBuilder), NinoStringBuilderBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(MemoryPackSerializeStringBuilder), MemoryPackStringBuilderBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(NinoSerializeBitArray), NinoBitArrayBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(NinoSerializeCultureInfo), NinoCultureInfoBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(MemoryPackSerializeCultureInfo), MemoryPackCultureInfoBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(NinoSerializeTimeZoneInfo), NinoTimeZoneInfoBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(MemoryPackSerializeTimeZoneInfo), MemoryPackTimeZoneInfoBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(NinoSerializeLazy), NinoLazyBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(MemoryPackSerializeLazy), MemoryPackLazyBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(NinoSerializeMemory), NinoMemoryBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(MemoryPackSerializeMemory), MemoryPackMemoryBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(NinoSerializeReadOnlySequence), NinoReadOnlySequenceBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(MemoryPackSerializeReadOnlySequence), MemoryPackReadOnlySequenceBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(NinoSerializeReadOnlyCollection), NinoReadOnlyCollectionBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(MemoryPackSerializeReadOnlyCollection), MemoryPackReadOnlyCollectionBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(NinoSerializeLookup), NinoLookupBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(MemoryPackSerializeLookup), MemoryPackLookupBytes.Length);
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
        BenchmarkPayloadRegistry.Register(nameof(MessagePackSerializeBigInteger), MessagePackBigIntegerBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(MessagePackSerializeUri), MessagePackUriBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(ProtobufSerializeUri), ProtobufUriBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(MessagePackSerializeVersion), MessagePackVersionBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(MessagePackSerializeStringBuilder), MessagePackStringBuilderBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(MessagePackSerializeBitArray), MessagePackBitArrayBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(MessagePackSerializeLazy), MessagePackLazyBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(MessagePackSerializeMemory), MessagePackMemoryBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(MessagePackSerializeReadOnlySequence), MessagePackReadOnlySequenceBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(MessagePackSerializeReadOnlyCollection), MessagePackReadOnlyCollectionBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(MessagePackSerializeLookup), MessagePackLookupBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(MessagePackSerializeImmutableHashSet), MessagePackImmutableHashSetBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(ProtobufSerializeImmutableHashSet), ProtobufImmutableHashSetBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(MessagePackSerializeImmutableSortedSet), MessagePackImmutableSortedSetBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(ProtobufSerializeImmutableSortedSet), ProtobufImmutableSortedSetBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(MessagePackSerializeImmutableQueue), MessagePackImmutableQueueBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(ProtobufSerializeImmutableQueue), ProtobufImmutableQueueBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(MessagePackSerializeImmutableStack), MessagePackImmutableStackBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(ProtobufSerializeImmutableStack), ProtobufImmutableStackBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(MessagePackSerializeImmutableDictionary), MessagePackImmutableDictionaryBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(ProtobufSerializeImmutableDictionary), ProtobufImmutableDictionaryBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(MessagePackSerializeImmutableSortedDictionary), MessagePackImmutableSortedDictionaryBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(ProtobufSerializeImmutableSortedDictionary), ProtobufImmutableSortedDictionaryBytes.Length);
    }

    private static BlockingCollection<int> MakeBlockingCollection()
    {
        var collection = new BlockingCollection<int>();
        for (int i = 0; i < N; i++)
            collection.Add(i);
        return collection;
    }

    private readonly ArrayBufferWriter<byte> _messagePackBuffer = new(1 << 16);
    private readonly MemoryStream _protobufStream = new(1 << 16);

    [GlobalSetup]
    public void Setup()
    {
        _ninoBuffer = new NinoArrayBufferWriter(1 << 16);
        _memoryPackBuffer = new ArrayBufferWriter<byte>(1 << 16);
    }

    // No MemoryPack cases for BigInteger: MemoryPack 1.21.4 writes the unused end of its 255-byte buffer
    // (temp.Slice(written) for temp.Slice(0, written)), so a value of up to 255 bytes reads back as another one.
    [Benchmark(Baseline = true), BenchmarkCategory("BigIntegerSerialize")]
    public int NinoSerializeBigInteger()
    {
        _ninoBuffer.ResetWrittenCount();
        NinoSerializer.Serialize(BigIntegerValue, _ninoBuffer);
        return _ninoBuffer.WrittenCount;
    }


    [Benchmark(Baseline = true), BenchmarkCategory("BigIntegerDeserialize")]
    public object NinoDeserializeBigInteger() => NinoDeserializer.Deserialize<BigInteger>(NinoBigIntegerBytes);


    [Benchmark(Baseline = true), BenchmarkCategory("UriSerialize")]
    public int NinoSerializeUri()
    {
        _ninoBuffer.ResetWrittenCount();
        NinoSerializer.Serialize(UriValue, _ninoBuffer);
        return _ninoBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("UriSerialize")]
    public int MemoryPackSerializeUri()
    {
        _memoryPackBuffer.ResetWrittenCount();
        MemoryPackSerializer.Serialize(_memoryPackBuffer, UriValue);
        return _memoryPackBuffer.WrittenCount;
    }

    [Benchmark(Baseline = true), BenchmarkCategory("UriDeserialize")]
    public object NinoDeserializeUri() => NinoDeserializer.Deserialize<Uri>(NinoUriBytes);

    [Benchmark, BenchmarkCategory("UriDeserialize")]
    public object MemoryPackDeserializeUri() => MemoryPackSerializer.Deserialize<Uri>(MemoryPackUriBytes);

    [Benchmark(Baseline = true), BenchmarkCategory("VersionSerialize")]
    public int NinoSerializeVersion()
    {
        _ninoBuffer.ResetWrittenCount();
        NinoSerializer.Serialize(VersionValue, _ninoBuffer);
        return _ninoBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("VersionSerialize")]
    public int MemoryPackSerializeVersion()
    {
        _memoryPackBuffer.ResetWrittenCount();
        MemoryPackSerializer.Serialize(_memoryPackBuffer, VersionValue);
        return _memoryPackBuffer.WrittenCount;
    }

    [Benchmark(Baseline = true), BenchmarkCategory("VersionDeserialize")]
    public object NinoDeserializeVersion() => NinoDeserializer.Deserialize<Version>(NinoVersionBytes);

    [Benchmark, BenchmarkCategory("VersionDeserialize")]
    public object MemoryPackDeserializeVersion() => MemoryPackSerializer.Deserialize<Version>(MemoryPackVersionBytes);

    [Benchmark(Baseline = true), BenchmarkCategory("StringBuilderSerialize")]
    public int NinoSerializeStringBuilder()
    {
        _ninoBuffer.ResetWrittenCount();
        NinoSerializer.Serialize(StringBuilderValue, _ninoBuffer);
        return _ninoBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("StringBuilderSerialize")]
    public int MemoryPackSerializeStringBuilder()
    {
        _memoryPackBuffer.ResetWrittenCount();
        MemoryPackSerializer.Serialize(_memoryPackBuffer, StringBuilderValue);
        return _memoryPackBuffer.WrittenCount;
    }

    [Benchmark(Baseline = true), BenchmarkCategory("StringBuilderDeserialize")]
    public object NinoDeserializeStringBuilder() => NinoDeserializer.Deserialize<StringBuilder>(NinoStringBuilderBytes);

    [Benchmark, BenchmarkCategory("StringBuilderDeserialize")]
    public object MemoryPackDeserializeStringBuilder() =>
        MemoryPackSerializer.Deserialize<StringBuilder>(MemoryPackStringBuilderBytes);

    // No MemoryPack cases for BitArray: MemoryPack 1.21.4 reinterprets it as { int[] m_array; int m_length }, its
    // layout on .NET 8; .NET 10's BitArray holds a byte[] _array, so it writes the wrong bytes and reading one
    // corrupts the heap (a segmentation fault).
    [Benchmark(Baseline = true), BenchmarkCategory("BitArraySerialize")]
    public int NinoSerializeBitArray()
    {
        _ninoBuffer.ResetWrittenCount();
        NinoSerializer.Serialize(BitArrayValue, _ninoBuffer);
        return _ninoBuffer.WrittenCount;
    }


    [Benchmark(Baseline = true), BenchmarkCategory("BitArrayDeserialize")]
    public object NinoDeserializeBitArray() => NinoDeserializer.Deserialize<BitArray>(NinoBitArrayBytes);


    [Benchmark(Baseline = true), BenchmarkCategory("CultureInfoSerialize")]
    public int NinoSerializeCultureInfo()
    {
        _ninoBuffer.ResetWrittenCount();
        NinoSerializer.Serialize(CultureInfoValue, _ninoBuffer);
        return _ninoBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("CultureInfoSerialize")]
    public int MemoryPackSerializeCultureInfo()
    {
        _memoryPackBuffer.ResetWrittenCount();
        MemoryPackSerializer.Serialize(_memoryPackBuffer, CultureInfoValue);
        return _memoryPackBuffer.WrittenCount;
    }

    [Benchmark(Baseline = true), BenchmarkCategory("CultureInfoDeserialize")]
    public object NinoDeserializeCultureInfo() => NinoDeserializer.Deserialize<CultureInfo>(NinoCultureInfoBytes);

    [Benchmark, BenchmarkCategory("CultureInfoDeserialize")]
    public object MemoryPackDeserializeCultureInfo() =>
        MemoryPackSerializer.Deserialize<CultureInfo>(MemoryPackCultureInfoBytes);

    [Benchmark(Baseline = true), BenchmarkCategory("TimeZoneInfoSerialize")]
    public int NinoSerializeTimeZoneInfo()
    {
        _ninoBuffer.ResetWrittenCount();
        NinoSerializer.Serialize(TimeZoneInfoValue, _ninoBuffer);
        return _ninoBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("TimeZoneInfoSerialize")]
    public int MemoryPackSerializeTimeZoneInfo()
    {
        _memoryPackBuffer.ResetWrittenCount();
        MemoryPackSerializer.Serialize(_memoryPackBuffer, TimeZoneInfoValue);
        return _memoryPackBuffer.WrittenCount;
    }

    [Benchmark(Baseline = true), BenchmarkCategory("TimeZoneInfoDeserialize")]
    public object NinoDeserializeTimeZoneInfo() => NinoDeserializer.Deserialize<TimeZoneInfo>(NinoTimeZoneInfoBytes);

    [Benchmark, BenchmarkCategory("TimeZoneInfoDeserialize")]
    public object MemoryPackDeserializeTimeZoneInfo() =>
        MemoryPackSerializer.Deserialize<TimeZoneInfo>(MemoryPackTimeZoneInfoBytes);

    [Benchmark(Baseline = true), BenchmarkCategory("LazySerialize")]
    public int NinoSerializeLazy()
    {
        _ninoBuffer.ResetWrittenCount();
        NinoSerializer.Serialize(LazyValue, _ninoBuffer);
        return _ninoBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("LazySerialize")]
    public int MemoryPackSerializeLazy()
    {
        _memoryPackBuffer.ResetWrittenCount();
        MemoryPackSerializer.Serialize(_memoryPackBuffer, LazyValue);
        return _memoryPackBuffer.WrittenCount;
    }

    [Benchmark(Baseline = true), BenchmarkCategory("LazyDeserialize")]
    public object NinoDeserializeLazy() => NinoDeserializer.Deserialize<Lazy<string>>(NinoLazyBytes);

    [Benchmark, BenchmarkCategory("LazyDeserialize")]
    public object MemoryPackDeserializeLazy() => MemoryPackSerializer.Deserialize<Lazy<string>>(MemoryPackLazyBytes);

    [Benchmark(Baseline = true), BenchmarkCategory("MemorySerialize")]
    public int NinoSerializeMemory()
    {
        _ninoBuffer.ResetWrittenCount();
        NinoSerializer.Serialize(MemoryValue, _ninoBuffer);
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
    public object NinoDeserializeMemory() => NinoDeserializer.Deserialize<Memory<int>>(NinoMemoryBytes);

    [Benchmark, BenchmarkCategory("MemoryDeserialize")]
    public object MemoryPackDeserializeMemory() => MemoryPackSerializer.Deserialize<Memory<int>>(MemoryPackMemoryBytes);

    [Benchmark(Baseline = true), BenchmarkCategory("ReadOnlySequenceSerialize")]
    public int NinoSerializeReadOnlySequence()
    {
        _ninoBuffer.ResetWrittenCount();
        NinoSerializer.Serialize(ReadOnlySequenceValue, _ninoBuffer);
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
    public object NinoDeserializeReadOnlySequence() =>
        NinoDeserializer.Deserialize<ReadOnlySequence<int>>(NinoReadOnlySequenceBytes);

    [Benchmark, BenchmarkCategory("ReadOnlySequenceDeserialize")]
    public object MemoryPackDeserializeReadOnlySequence() =>
        MemoryPackSerializer.Deserialize<ReadOnlySequence<int>>(MemoryPackReadOnlySequenceBytes);

    [Benchmark(Baseline = true), BenchmarkCategory("ReadOnlyCollectionSerialize")]
    public int NinoSerializeReadOnlyCollection()
    {
        _ninoBuffer.ResetWrittenCount();
        NinoSerializer.Serialize(ReadOnlyCollectionValue, _ninoBuffer);
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
    public object NinoDeserializeReadOnlyCollection() =>
        NinoDeserializer.Deserialize<ReadOnlyCollection<int>>(NinoReadOnlyCollectionBytes);

    [Benchmark, BenchmarkCategory("ReadOnlyCollectionDeserialize")]
    public object MemoryPackDeserializeReadOnlyCollection() =>
        MemoryPackSerializer.Deserialize<ReadOnlyCollection<int>>(MemoryPackReadOnlyCollectionBytes);

    [Benchmark(Baseline = true), BenchmarkCategory("LookupSerialize")]
    public int NinoSerializeLookup()
    {
        _ninoBuffer.ResetWrittenCount();
        NinoSerializer.Serialize(LookupValue, _ninoBuffer);
        return _ninoBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("LookupSerialize")]
    public int MemoryPackSerializeLookup()
    {
        _memoryPackBuffer.ResetWrittenCount();
        MemoryPackSerializer.Serialize(_memoryPackBuffer, LookupValue);
        return _memoryPackBuffer.WrittenCount;
    }

    [Benchmark(Baseline = true), BenchmarkCategory("LookupDeserialize")]
    public object NinoDeserializeLookup() => NinoDeserializer.Deserialize<ILookup<int, int>>(NinoLookupBytes);

    [Benchmark, BenchmarkCategory("LookupDeserialize")]
    public object MemoryPackDeserializeLookup() =>
        MemoryPackSerializer.Deserialize<ILookup<int, int>>(MemoryPackLookupBytes);

    [Benchmark(Baseline = true), BenchmarkCategory("BlockingCollectionSerialize")]
    public int NinoSerializeBlockingCollection()
    {
        _ninoBuffer.ResetWrittenCount();
        NinoSerializer.Serialize(BlockingCollectionValue, _ninoBuffer);
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
    public object NinoDeserializeBlockingCollection() =>
        NinoDeserializer.Deserialize<BlockingCollection<int>>(NinoBlockingCollectionBytes);

    [Benchmark, BenchmarkCategory("BlockingCollectionDeserialize")]
    public object MemoryPackDeserializeBlockingCollection() =>
        MemoryPackSerializer.Deserialize<BlockingCollection<int>>(MemoryPackBlockingCollectionBytes);

    [Benchmark(Baseline = true), BenchmarkCategory("ImmutableHashSetSerialize")]
    public int NinoSerializeImmutableHashSet()
    {
        _ninoBuffer.ResetWrittenCount();
        NinoSerializer.Serialize(ImmutableHashSetValue, _ninoBuffer);
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
    public object NinoDeserializeImmutableHashSet() =>
        NinoDeserializer.Deserialize<ImmutableHashSet<int>>(NinoImmutableHashSetBytes);

    [Benchmark, BenchmarkCategory("ImmutableHashSetDeserialize")]
    public object MemoryPackDeserializeImmutableHashSet() =>
        MemoryPackSerializer.Deserialize<ImmutableHashSet<int>>(MemoryPackImmutableHashSetBytes);

    [Benchmark(Baseline = true), BenchmarkCategory("ImmutableSortedSetSerialize")]
    public int NinoSerializeImmutableSortedSet()
    {
        _ninoBuffer.ResetWrittenCount();
        NinoSerializer.Serialize(ImmutableSortedSetValue, _ninoBuffer);
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
    public object NinoDeserializeImmutableSortedSet() =>
        NinoDeserializer.Deserialize<ImmutableSortedSet<int>>(NinoImmutableSortedSetBytes);

    [Benchmark, BenchmarkCategory("ImmutableSortedSetDeserialize")]
    public object MemoryPackDeserializeImmutableSortedSet() =>
        MemoryPackSerializer.Deserialize<ImmutableSortedSet<int>>(MemoryPackImmutableSortedSetBytes);

    [Benchmark(Baseline = true), BenchmarkCategory("ImmutableQueueSerialize")]
    public int NinoSerializeImmutableQueue()
    {
        _ninoBuffer.ResetWrittenCount();
        NinoSerializer.Serialize(ImmutableQueueValue, _ninoBuffer);
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
    public object NinoDeserializeImmutableQueue() =>
        NinoDeserializer.Deserialize<ImmutableQueue<int>>(NinoImmutableQueueBytes);

    [Benchmark, BenchmarkCategory("ImmutableQueueDeserialize")]
    public object MemoryPackDeserializeImmutableQueue() =>
        MemoryPackSerializer.Deserialize<ImmutableQueue<int>>(MemoryPackImmutableQueueBytes);

    [Benchmark(Baseline = true), BenchmarkCategory("ImmutableStackSerialize")]
    public int NinoSerializeImmutableStack()
    {
        _ninoBuffer.ResetWrittenCount();
        NinoSerializer.Serialize(ImmutableStackValue, _ninoBuffer);
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
    public object NinoDeserializeImmutableStack() =>
        NinoDeserializer.Deserialize<ImmutableStack<int>>(NinoImmutableStackBytes);

    [Benchmark, BenchmarkCategory("ImmutableStackDeserialize")]
    public object MemoryPackDeserializeImmutableStack() =>
        MemoryPackSerializer.Deserialize<ImmutableStack<int>>(MemoryPackImmutableStackBytes);

    [Benchmark(Baseline = true), BenchmarkCategory("ImmutableDictionarySerialize")]
    public int NinoSerializeImmutableDictionary()
    {
        _ninoBuffer.ResetWrittenCount();
        NinoSerializer.Serialize(ImmutableDictionaryValue, _ninoBuffer);
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
    public object NinoDeserializeImmutableDictionary() =>
        NinoDeserializer.Deserialize<ImmutableDictionary<int, int>>(NinoImmutableDictionaryBytes);

    [Benchmark, BenchmarkCategory("ImmutableDictionaryDeserialize")]
    public object MemoryPackDeserializeImmutableDictionary() =>
        MemoryPackSerializer.Deserialize<ImmutableDictionary<int, int>>(MemoryPackImmutableDictionaryBytes);

    [Benchmark(Baseline = true), BenchmarkCategory("ImmutableSortedDictionarySerialize")]
    public int NinoSerializeImmutableSortedDictionary()
    {
        _ninoBuffer.ResetWrittenCount();
        NinoSerializer.Serialize(ImmutableSortedDictionaryValue, _ninoBuffer);
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
    public object NinoDeserializeImmutableSortedDictionary() =>
        NinoDeserializer.Deserialize<ImmutableSortedDictionary<int, int>>(NinoImmutableSortedDictionaryBytes);

    [Benchmark, BenchmarkCategory("ImmutableSortedDictionaryDeserialize")]
    public object MemoryPackDeserializeImmutableSortedDictionary() =>
        MemoryPackSerializer.Deserialize<ImmutableSortedDictionary<int, int>>(MemoryPackImmutableSortedDictionaryBytes);

    // MessagePack and protobuf-net.
    [Benchmark, BenchmarkCategory("BigIntegerSerialize")]
    public int MessagePackSerializeBigInteger()
    {
        _messagePackBuffer.ResetWrittenCount();
        MessagePackSerializer.Serialize(_messagePackBuffer, BigIntegerValue);
        return _messagePackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("UriSerialize")]
    public int MessagePackSerializeUri()
    {
        _messagePackBuffer.ResetWrittenCount();
        MessagePackSerializer.Serialize(_messagePackBuffer, UriValue);
        return _messagePackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("UriSerialize")]
    public int ProtobufSerializeUri()
    {
        _protobufStream.SetLength(0);
        Serializer.Serialize(_protobufStream, UriValue);
        return (int)_protobufStream.Length;
    }

    [Benchmark, BenchmarkCategory("VersionSerialize")]
    public int MessagePackSerializeVersion()
    {
        _messagePackBuffer.ResetWrittenCount();
        MessagePackSerializer.Serialize(_messagePackBuffer, VersionValue);
        return _messagePackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("StringBuilderSerialize")]
    public int MessagePackSerializeStringBuilder()
    {
        _messagePackBuffer.ResetWrittenCount();
        MessagePackSerializer.Serialize(_messagePackBuffer, StringBuilderValue);
        return _messagePackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("BitArraySerialize")]
    public int MessagePackSerializeBitArray()
    {
        _messagePackBuffer.ResetWrittenCount();
        MessagePackSerializer.Serialize(_messagePackBuffer, BitArrayValue);
        return _messagePackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("LazySerialize")]
    public int MessagePackSerializeLazy()
    {
        _messagePackBuffer.ResetWrittenCount();
        MessagePackSerializer.Serialize(_messagePackBuffer, LazyValue);
        return _messagePackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("MemorySerialize")]
    public int MessagePackSerializeMemory()
    {
        _messagePackBuffer.ResetWrittenCount();
        MessagePackSerializer.Serialize(_messagePackBuffer, MemoryValue);
        return _messagePackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("ReadOnlySequenceSerialize")]
    public int MessagePackSerializeReadOnlySequence()
    {
        _messagePackBuffer.ResetWrittenCount();
        MessagePackSerializer.Serialize(_messagePackBuffer, ReadOnlySequenceValue);
        return _messagePackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("ReadOnlyCollectionSerialize")]
    public int MessagePackSerializeReadOnlyCollection()
    {
        _messagePackBuffer.ResetWrittenCount();
        MessagePackSerializer.Serialize(_messagePackBuffer, ReadOnlyCollectionValue);
        return _messagePackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("LookupSerialize")]
    public int MessagePackSerializeLookup()
    {
        _messagePackBuffer.ResetWrittenCount();
        MessagePackSerializer.Serialize(_messagePackBuffer, LookupValue);
        return _messagePackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("BigIntegerDeserialize")]
    public object MessagePackDeserializeBigInteger() =>
        MessagePackSerializer.Deserialize<BigInteger>(MessagePackBigIntegerBytes);

    [Benchmark, BenchmarkCategory("UriDeserialize")]
    public object MessagePackDeserializeUri() => MessagePackSerializer.Deserialize<Uri>(MessagePackUriBytes);

    [Benchmark, BenchmarkCategory("UriDeserialize")]
    public object ProtobufDeserializeUri() => Serializer.Deserialize<Uri>(new ReadOnlySpan<byte>(ProtobufUriBytes));

    [Benchmark, BenchmarkCategory("VersionDeserialize")]
    public object MessagePackDeserializeVersion() =>
        MessagePackSerializer.Deserialize<Version>(MessagePackVersionBytes);

    [Benchmark, BenchmarkCategory("StringBuilderDeserialize")]
    public object MessagePackDeserializeStringBuilder() =>
        MessagePackSerializer.Deserialize<StringBuilder>(MessagePackStringBuilderBytes);

    [Benchmark, BenchmarkCategory("BitArrayDeserialize")]
    public object MessagePackDeserializeBitArray() =>
        MessagePackSerializer.Deserialize<BitArray>(MessagePackBitArrayBytes);

    [Benchmark, BenchmarkCategory("LazyDeserialize")]
    public object MessagePackDeserializeLazy() => MessagePackSerializer.Deserialize<Lazy<string>>(MessagePackLazyBytes);

    [Benchmark, BenchmarkCategory("MemoryDeserialize")]
    public object MessagePackDeserializeMemory() =>
        MessagePackSerializer.Deserialize<Memory<int>>(MessagePackMemoryBytes);

    [Benchmark, BenchmarkCategory("ReadOnlySequenceDeserialize")]
    public object MessagePackDeserializeReadOnlySequence() =>
        MessagePackSerializer.Deserialize<ReadOnlySequence<int>>(MessagePackReadOnlySequenceBytes);

    [Benchmark, BenchmarkCategory("ReadOnlyCollectionDeserialize")]
    public object MessagePackDeserializeReadOnlyCollection() =>
        MessagePackSerializer.Deserialize<ReadOnlyCollection<int>>(MessagePackReadOnlyCollectionBytes);

    [Benchmark, BenchmarkCategory("LookupDeserialize")]
    public object MessagePackDeserializeLookup() =>
        MessagePackSerializer.Deserialize<ILookup<int, int>>(MessagePackLookupBytes);
    [Benchmark, BenchmarkCategory("ImmutableHashSetSerialize")]
    public int MessagePackSerializeImmutableHashSet()
    {
        _messagePackBuffer.ResetWrittenCount();
        MessagePackSerializer.Serialize(_messagePackBuffer, ImmutableHashSetValue, ImmutableOptions);
        return _messagePackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("ImmutableHashSetSerialize")]
    public int ProtobufSerializeImmutableHashSet()
    {
        _protobufStream.SetLength(0);
        Serializer.Serialize(_protobufStream, ImmutableHashSetValue);
        return (int)_protobufStream.Length;
    }

    [Benchmark, BenchmarkCategory("ImmutableSortedSetSerialize")]
    public int MessagePackSerializeImmutableSortedSet()
    {
        _messagePackBuffer.ResetWrittenCount();
        MessagePackSerializer.Serialize(_messagePackBuffer, ImmutableSortedSetValue, ImmutableOptions);
        return _messagePackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("ImmutableSortedSetSerialize")]
    public int ProtobufSerializeImmutableSortedSet()
    {
        _protobufStream.SetLength(0);
        Serializer.Serialize(_protobufStream, ImmutableSortedSetValue);
        return (int)_protobufStream.Length;
    }

    [Benchmark, BenchmarkCategory("ImmutableQueueSerialize")]
    public int MessagePackSerializeImmutableQueue()
    {
        _messagePackBuffer.ResetWrittenCount();
        MessagePackSerializer.Serialize(_messagePackBuffer, ImmutableQueueValue, ImmutableOptions);
        return _messagePackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("ImmutableQueueSerialize")]
    public int ProtobufSerializeImmutableQueue()
    {
        _protobufStream.SetLength(0);
        Serializer.Serialize(_protobufStream, ImmutableQueueValue);
        return (int)_protobufStream.Length;
    }

    [Benchmark, BenchmarkCategory("ImmutableStackSerialize")]
    public int MessagePackSerializeImmutableStack()
    {
        _messagePackBuffer.ResetWrittenCount();
        MessagePackSerializer.Serialize(_messagePackBuffer, ImmutableStackValue, ImmutableOptions);
        return _messagePackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("ImmutableStackSerialize")]
    public int ProtobufSerializeImmutableStack()
    {
        _protobufStream.SetLength(0);
        Serializer.Serialize(_protobufStream, ImmutableStackValue);
        return (int)_protobufStream.Length;
    }

    [Benchmark, BenchmarkCategory("ImmutableDictionarySerialize")]
    public int MessagePackSerializeImmutableDictionary()
    {
        _messagePackBuffer.ResetWrittenCount();
        MessagePackSerializer.Serialize(_messagePackBuffer, ImmutableDictionaryValue, ImmutableOptions);
        return _messagePackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("ImmutableDictionarySerialize")]
    public int ProtobufSerializeImmutableDictionary()
    {
        _protobufStream.SetLength(0);
        Serializer.Serialize(_protobufStream, ImmutableDictionaryValue);
        return (int)_protobufStream.Length;
    }

    [Benchmark, BenchmarkCategory("ImmutableSortedDictionarySerialize")]
    public int MessagePackSerializeImmutableSortedDictionary()
    {
        _messagePackBuffer.ResetWrittenCount();
        MessagePackSerializer.Serialize(_messagePackBuffer, ImmutableSortedDictionaryValue, ImmutableOptions);
        return _messagePackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("ImmutableSortedDictionarySerialize")]
    public int ProtobufSerializeImmutableSortedDictionary()
    {
        _protobufStream.SetLength(0);
        Serializer.Serialize(_protobufStream, ImmutableSortedDictionaryValue);
        return (int)_protobufStream.Length;
    }

    [Benchmark, BenchmarkCategory("ImmutableHashSetDeserialize")]
    public object MessagePackDeserializeImmutableHashSet() =>
        MessagePackSerializer.Deserialize<ImmutableHashSet<int>>(MessagePackImmutableHashSetBytes, ImmutableOptions);

    [Benchmark, BenchmarkCategory("ImmutableHashSetDeserialize")]
    public object ProtobufDeserializeImmutableHashSet() =>
        Serializer.Deserialize<ImmutableHashSet<int>>(new ReadOnlySpan<byte>(ProtobufImmutableHashSetBytes));

    [Benchmark, BenchmarkCategory("ImmutableSortedSetDeserialize")]
    public object MessagePackDeserializeImmutableSortedSet() =>
        MessagePackSerializer.Deserialize<ImmutableSortedSet<int>>(MessagePackImmutableSortedSetBytes, ImmutableOptions);

    [Benchmark, BenchmarkCategory("ImmutableSortedSetDeserialize")]
    public object ProtobufDeserializeImmutableSortedSet() =>
        Serializer.Deserialize<ImmutableSortedSet<int>>(new ReadOnlySpan<byte>(ProtobufImmutableSortedSetBytes));

    [Benchmark, BenchmarkCategory("ImmutableQueueDeserialize")]
    public object MessagePackDeserializeImmutableQueue() =>
        MessagePackSerializer.Deserialize<ImmutableQueue<int>>(MessagePackImmutableQueueBytes, ImmutableOptions);

    [Benchmark, BenchmarkCategory("ImmutableQueueDeserialize")]
    public object ProtobufDeserializeImmutableQueue() =>
        Serializer.Deserialize<ImmutableQueue<int>>(new ReadOnlySpan<byte>(ProtobufImmutableQueueBytes));

    [Benchmark, BenchmarkCategory("ImmutableStackDeserialize")]
    public object MessagePackDeserializeImmutableStack() =>
        MessagePackSerializer.Deserialize<ImmutableStack<int>>(MessagePackImmutableStackBytes, ImmutableOptions);

    [Benchmark, BenchmarkCategory("ImmutableStackDeserialize")]
    public object ProtobufDeserializeImmutableStack() =>
        Serializer.Deserialize<ImmutableStack<int>>(new ReadOnlySpan<byte>(ProtobufImmutableStackBytes));

    [Benchmark, BenchmarkCategory("ImmutableDictionaryDeserialize")]
    public object MessagePackDeserializeImmutableDictionary() =>
        MessagePackSerializer.Deserialize<ImmutableDictionary<int, int>>(MessagePackImmutableDictionaryBytes, ImmutableOptions);

    [Benchmark, BenchmarkCategory("ImmutableDictionaryDeserialize")]
    public object ProtobufDeserializeImmutableDictionary() =>
        Serializer.Deserialize<ImmutableDictionary<int, int>>(new ReadOnlySpan<byte>(ProtobufImmutableDictionaryBytes));

    [Benchmark, BenchmarkCategory("ImmutableSortedDictionaryDeserialize")]
    public object MessagePackDeserializeImmutableSortedDictionary() =>
        MessagePackSerializer.Deserialize<ImmutableSortedDictionary<int, int>>(MessagePackImmutableSortedDictionaryBytes, ImmutableOptions);

    [Benchmark, BenchmarkCategory("ImmutableSortedDictionaryDeserialize")]
    public object ProtobufDeserializeImmutableSortedDictionary() =>
        Serializer.Deserialize<ImmutableSortedDictionary<int, int>>(new ReadOnlySpan<byte>(ProtobufImmutableSortedDictionaryBytes));
}
