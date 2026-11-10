// Generic polymorphic bases need a generator with the fix for #203 (rc.7): left out of A/B runs against older releases.
#if !NINO_BENCH_LEGACY
using System;
using System.Buffers;
using System.IO;
using System.Linq;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using MemoryPack;
using MemoryPack.Formatters;
using MessagePack;
using Nino.Core;
using ProtoBuf;
using ProtoBuf.Meta;

#nullable disable
namespace Nino.Benchmark;

/// <summary>
/// Arrays of 1,000 subtypes in random order (eight types each), through a non-generic base and through the closed
/// instances of a generic base (<c>ProbeBox&lt;T&gt;</c>) with a primitive, an unmanaged tuple, a managed tuple and
/// tuples holding a sealed and a non-sealed class as the type argument, against MemoryPack: a union attribute on the
/// non-generic base, and a <c>DynamicUnionFormatter</c> registered for each closed generic one, as MemoryPack's union
/// attribute cannot be put on a generic type (MEMPACK013, MEMPACK014). protobuf-net takes <c>[ProtoInclude]</c> on the
/// non-generic base and the subtypes of each closed generic one added to its runtime model; MessagePack only the
/// non-generic base: its unions are attributes on the base type, which cannot name different subtypes for each type
/// argument.
/// </summary>
[PayloadColumn]
[HideColumns("StdDev", "RatioSD", "Error")]
[MinColumn, MaxColumn]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[NinoJob]
[GcServer(true)]
[MemoryDiagnoser]
[MarkdownExporterAttribute.GitHub]
public class GenericPolymorphicTest
{
    private const int N = 1000;
    // First, so that the data below is serialized with them.
    private static readonly bool Unions = RegisterUnions();
    private static readonly string[] Names = Enumerable.Range(0, 8).Select(static i => "name" + i).ToArray();
    private static readonly ProbeSealedItem[] SealedItems =
        Enumerable.Range(0, 8).Select(static i => new ProbeSealedItem { X = i, Y = i * 2L }).ToArray();
    private static readonly ProbeOpenItem[] OpenItems =
        Enumerable.Range(0, 8).Select(static i => new ProbeOpenItem { X = i, Y = i * 2L }).ToArray();

    private static readonly ProbeBase[] NonGenericBase = Make<ProbeBase>(i => i switch { 0 => new ProbeA { V = i }, 1 => new ProbeB { V = i }, 2 => new ProbeC { V = i }, 3 => new ProbeD { V = i }, 4 => new ProbeE { V = i }, 5 => new ProbeF { V = i }, 6 => new ProbeG { V = i }, _ => new ProbeH { V = i } });
    private static readonly ProbeBox<int>[] GenericIntBase = Make<ProbeBox<int>>(i => i switch { 0 => new ProbeBoxA { V = i }, 1 => new ProbeBoxB { V = i }, 2 => new ProbeBoxC { V = i }, 3 => new ProbeBoxD { V = i }, 4 => new ProbeBoxE { V = i }, 5 => new ProbeBoxF { V = i }, 6 => new ProbeBoxG { V = i }, _ => new ProbeBoxH { V = i } });
    private static readonly ProbeBox<(int, long)>[] GenericUnmanagedTupleBase = Make<ProbeBox<(int, long)>>(i => i switch { 0 => new ProbeTupA { V = (i, i * 3L) }, 1 => new ProbeTupB { V = (i, i * 3L) }, 2 => new ProbeTupC { V = (i, i * 3L) }, 3 => new ProbeTupD { V = (i, i * 3L) }, 4 => new ProbeTupE { V = (i, i * 3L) }, 5 => new ProbeTupF { V = (i, i * 3L) }, 6 => new ProbeTupG { V = (i, i * 3L) }, _ => new ProbeTupH { V = (i, i * 3L) } });
    private static readonly ProbeBox<(int, string)>[] GenericManagedTupleBase = Make<ProbeBox<(int, string)>>(i => i switch { 0 => new ProbeStrA { V = (i, Names[i]) }, 1 => new ProbeStrB { V = (i, Names[i]) }, 2 => new ProbeStrC { V = (i, Names[i]) }, 3 => new ProbeStrD { V = (i, Names[i]) }, 4 => new ProbeStrE { V = (i, Names[i]) }, 5 => new ProbeStrF { V = (i, Names[i]) }, 6 => new ProbeStrG { V = (i, Names[i]) }, _ => new ProbeStrH { V = (i, Names[i]) } });
    private static readonly ProbeBox<(ProbeSealedItem, int)>[] GenericSealedClassTupleBase = Make<ProbeBox<(ProbeSealedItem, int)>>(i => i switch { 0 => new ProbeScA { V = (SealedItems[i], i) }, 1 => new ProbeScB { V = (SealedItems[i], i) }, 2 => new ProbeScC { V = (SealedItems[i], i) }, 3 => new ProbeScD { V = (SealedItems[i], i) }, 4 => new ProbeScE { V = (SealedItems[i], i) }, 5 => new ProbeScF { V = (SealedItems[i], i) }, 6 => new ProbeScG { V = (SealedItems[i], i) }, _ => new ProbeScH { V = (SealedItems[i], i) } });
    private static readonly ProbeBox<(ProbeOpenItem, int)>[] GenericOpenClassTupleBase = Make<ProbeBox<(ProbeOpenItem, int)>>(i => i switch { 0 => new ProbeOcA { V = (OpenItems[i], i) }, 1 => new ProbeOcB { V = (OpenItems[i], i) }, 2 => new ProbeOcC { V = (OpenItems[i], i) }, 3 => new ProbeOcD { V = (OpenItems[i], i) }, 4 => new ProbeOcE { V = (OpenItems[i], i) }, 5 => new ProbeOcF { V = (OpenItems[i], i) }, 6 => new ProbeOcG { V = (OpenItems[i], i) }, _ => new ProbeOcH { V = (OpenItems[i], i) } });

    private static readonly byte[] NinoNonGenericBaseBytes = NinoSerializer.Serialize(NonGenericBase);
    private static readonly byte[] MessagePackNonGenericBaseBytes = MessagePackSerializer.Serialize(NonGenericBase);
    private static readonly byte[] ProtobufNonGenericBaseBytes = ProtobufModel.Serialize(NonGenericBase);
    private static readonly byte[] MemoryPackNonGenericBaseBytes = MemoryPackSerializer.Serialize(NonGenericBase);
    private static readonly byte[] NinoGenericIntBaseBytes = NinoSerializer.Serialize(GenericIntBase);
    private static readonly byte[] ProtobufGenericIntBaseBytes = ProtobufModel.Serialize(GenericIntBase);
    private static readonly byte[] MemoryPackGenericIntBaseBytes = MemoryPackSerializer.Serialize(GenericIntBase);
    private static readonly byte[] NinoGenericUnmanagedTupleBaseBytes = NinoSerializer.Serialize(GenericUnmanagedTupleBase);
    private static readonly byte[] ProtobufGenericUnmanagedTupleBaseBytes = ProtobufModel.Serialize(GenericUnmanagedTupleBase);
    private static readonly byte[] MemoryPackGenericUnmanagedTupleBaseBytes = MemoryPackSerializer.Serialize(GenericUnmanagedTupleBase);
    private static readonly byte[] NinoGenericManagedTupleBaseBytes = NinoSerializer.Serialize(GenericManagedTupleBase);
    private static readonly byte[] ProtobufGenericManagedTupleBaseBytes = ProtobufModel.Serialize(GenericManagedTupleBase);
    private static readonly byte[] MemoryPackGenericManagedTupleBaseBytes = MemoryPackSerializer.Serialize(GenericManagedTupleBase);
    private static readonly byte[] NinoGenericSealedClassTupleBaseBytes = NinoSerializer.Serialize(GenericSealedClassTupleBase);
    private static readonly byte[] ProtobufGenericSealedClassTupleBaseBytes = ProtobufModel.Serialize(GenericSealedClassTupleBase);
    private static readonly byte[] MemoryPackGenericSealedClassTupleBaseBytes = MemoryPackSerializer.Serialize(GenericSealedClassTupleBase);
    private static readonly byte[] NinoGenericOpenClassTupleBaseBytes = NinoSerializer.Serialize(GenericOpenClassTupleBase);
    private static readonly byte[] ProtobufGenericOpenClassTupleBaseBytes = ProtobufModel.Serialize(GenericOpenClassTupleBase);
    private static readonly byte[] MemoryPackGenericOpenClassTupleBaseBytes = MemoryPackSerializer.Serialize(GenericOpenClassTupleBase);

    private readonly NinoArrayBufferWriter _buffer = new(1 << 16);

    static GenericPolymorphicTest()
    {
        BenchmarkPayloadRegistry.Register(nameof(NinoSerializeNonGenericBase), NinoNonGenericBaseBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(MemoryPackSerializeNonGenericBase), MemoryPackNonGenericBaseBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(NinoSerializeGenericIntBase), NinoGenericIntBaseBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(MemoryPackSerializeGenericIntBase), MemoryPackGenericIntBaseBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(NinoSerializeGenericUnmanagedTupleBase), NinoGenericUnmanagedTupleBaseBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(MemoryPackSerializeGenericUnmanagedTupleBase), MemoryPackGenericUnmanagedTupleBaseBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(NinoSerializeGenericManagedTupleBase), NinoGenericManagedTupleBaseBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(MemoryPackSerializeGenericManagedTupleBase), MemoryPackGenericManagedTupleBaseBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(NinoSerializeGenericSealedClassTupleBase), NinoGenericSealedClassTupleBaseBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(MemoryPackSerializeGenericSealedClassTupleBase), MemoryPackGenericSealedClassTupleBaseBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(NinoSerializeGenericOpenClassTupleBase), NinoGenericOpenClassTupleBaseBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(MemoryPackSerializeGenericOpenClassTupleBase), MemoryPackGenericOpenClassTupleBaseBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(MessagePackSerializeNonGenericBase), MessagePackNonGenericBaseBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(ProtobufSerializeNonGenericBase), ProtobufNonGenericBaseBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(ProtobufSerializeGenericIntBase), ProtobufGenericIntBaseBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(ProtobufSerializeGenericUnmanagedTupleBase), ProtobufGenericUnmanagedTupleBaseBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(ProtobufSerializeGenericManagedTupleBase), ProtobufGenericManagedTupleBaseBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(ProtobufSerializeGenericSealedClassTupleBase), ProtobufGenericSealedClassTupleBaseBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(ProtobufSerializeGenericOpenClassTupleBase), ProtobufGenericOpenClassTupleBaseBytes.Length);
    }

    private static bool RegisterUnions()
    {
        MemoryPackFormatterProvider.Register(new DynamicUnionFormatter<ProbeBox<int>>((0, typeof(ProbeBoxA)), (1, typeof(ProbeBoxB)), (2, typeof(ProbeBoxC)), (3, typeof(ProbeBoxD)), (4, typeof(ProbeBoxE)), (5, typeof(ProbeBoxF)), (6, typeof(ProbeBoxG)), (7, typeof(ProbeBoxH))));
        MemoryPackFormatterProvider.Register(new DynamicUnionFormatter<ProbeBox<(int, long)>>((0, typeof(ProbeTupA)), (1, typeof(ProbeTupB)), (2, typeof(ProbeTupC)), (3, typeof(ProbeTupD)), (4, typeof(ProbeTupE)), (5, typeof(ProbeTupF)), (6, typeof(ProbeTupG)), (7, typeof(ProbeTupH))));
        MemoryPackFormatterProvider.Register(new DynamicUnionFormatter<ProbeBox<(int, string)>>((0, typeof(ProbeStrA)), (1, typeof(ProbeStrB)), (2, typeof(ProbeStrC)), (3, typeof(ProbeStrD)), (4, typeof(ProbeStrE)), (5, typeof(ProbeStrF)), (6, typeof(ProbeStrG)), (7, typeof(ProbeStrH))));
        MemoryPackFormatterProvider.Register(new DynamicUnionFormatter<ProbeBox<(ProbeSealedItem, int)>>((0, typeof(ProbeScA)), (1, typeof(ProbeScB)), (2, typeof(ProbeScC)), (3, typeof(ProbeScD)), (4, typeof(ProbeScE)), (5, typeof(ProbeScF)), (6, typeof(ProbeScG)), (7, typeof(ProbeScH))));
        MemoryPackFormatterProvider.Register(new DynamicUnionFormatter<ProbeBox<(ProbeOpenItem, int)>>((0, typeof(ProbeOcA)), (1, typeof(ProbeOcB)), (2, typeof(ProbeOcC)), (3, typeof(ProbeOcD)), (4, typeof(ProbeOcE)), (5, typeof(ProbeOcF)), (6, typeof(ProbeOcG)), (7, typeof(ProbeOcH))));
        ProtobufSubtypes<int>(typeof(ProbeBoxA), typeof(ProbeBoxB), typeof(ProbeBoxC), typeof(ProbeBoxD), typeof(ProbeBoxE), typeof(ProbeBoxF), typeof(ProbeBoxG), typeof(ProbeBoxH));
        ProtobufSubtypes<(int, long)>(typeof(ProbeTupA), typeof(ProbeTupB), typeof(ProbeTupC), typeof(ProbeTupD), typeof(ProbeTupE), typeof(ProbeTupF), typeof(ProbeTupG), typeof(ProbeTupH));
        ProtobufSubtypes<(int, string)>(typeof(ProbeStrA), typeof(ProbeStrB), typeof(ProbeStrC), typeof(ProbeStrD), typeof(ProbeStrE), typeof(ProbeStrF), typeof(ProbeStrG), typeof(ProbeStrH));
        ProtobufSubtypes<(ProbeSealedItem, int)>(typeof(ProbeScA), typeof(ProbeScB), typeof(ProbeScC), typeof(ProbeScD), typeof(ProbeScE), typeof(ProbeScF), typeof(ProbeScG), typeof(ProbeScH));
        ProtobufSubtypes<(ProbeOpenItem, int)>(typeof(ProbeOcA), typeof(ProbeOcB), typeof(ProbeOcC), typeof(ProbeOcD), typeof(ProbeOcE), typeof(ProbeOcF), typeof(ProbeOcG), typeof(ProbeOcH));
        return true;
    }

    /// <summary>protobuf-net's way for a generic base: the subtypes of one closed type, added to its model.</summary>
    private static void ProtobufSubtypes<T>(params Type[] subtypes)
    {
        var meta = RuntimeTypeModel.Default[typeof(ProbeBox<T>)];
        for (int i = 0; i < subtypes.Length; i++)
            meta.AddSubType(10 + i, subtypes[i]);
    }

    /// <summary>Both libraries read back what was written: the values read are written by Nino as the originals are.</summary>
    private readonly ArrayBufferWriter<byte> _messagePackBuffer = new(1 << 16);
    private readonly MemoryStream _protobufStream = new(1 << 16);

    [GlobalSetup]
    public void Verify()
    {
        Check(NonGenericBase, MemoryPackNonGenericBaseBytes);
        Check(GenericIntBase, MemoryPackGenericIntBaseBytes);
        Check(GenericUnmanagedTupleBase, MemoryPackGenericUnmanagedTupleBaseBytes);
        Check(GenericManagedTupleBase, MemoryPackGenericManagedTupleBaseBytes);
        Check(GenericSealedClassTupleBase, MemoryPackGenericSealedClassTupleBaseBytes);
        Check(GenericOpenClassTupleBase, MemoryPackGenericOpenClassTupleBaseBytes);
    }

    private static void Check<T>(T[] values, byte[] memoryPack)
    {
        byte[] nino = NinoSerializer.Serialize(values);
        if (!nino.AsSpan().SequenceEqual(NinoSerializer.Serialize(NinoDeserializer.Deserialize<T[]>(nino))) ||
            !nino.AsSpan().SequenceEqual(NinoSerializer.Serialize(MemoryPackSerializer.Deserialize<T[]>(memoryPack))))
            throw new InvalidOperationException($"{typeof(T)} does not round-trip");
    }

    private static T[] Make<T>(Func<int, T> create)
    {
        var random = new Random(42);
        return Enumerable.Range(0, N).Select(_ => create(random.Next(8))).ToArray();
    }

    [Benchmark(Baseline = true), BenchmarkCategory("NonGenericBaseSerialize")]
    public int NinoSerializeNonGenericBase()
    {
        _buffer.ResetWrittenCount();
        NinoSerializer.Serialize(NonGenericBase, _buffer);
        return _buffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("NonGenericBaseSerialize")]
    public int MemoryPackSerializeNonGenericBase()
    {
        _buffer.ResetWrittenCount();
        MemoryPackSerializer.Serialize(_buffer, NonGenericBase);
        return _buffer.WrittenCount;
    }

    [Benchmark(Baseline = true), BenchmarkCategory("GenericIntBaseSerialize")]
    public int NinoSerializeGenericIntBase()
    {
        _buffer.ResetWrittenCount();
        NinoSerializer.Serialize(GenericIntBase, _buffer);
        return _buffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("GenericIntBaseSerialize")]
    public int MemoryPackSerializeGenericIntBase()
    {
        _buffer.ResetWrittenCount();
        MemoryPackSerializer.Serialize(_buffer, GenericIntBase);
        return _buffer.WrittenCount;
    }

    [Benchmark(Baseline = true), BenchmarkCategory("GenericUnmanagedTupleBaseSerialize")]
    public int NinoSerializeGenericUnmanagedTupleBase()
    {
        _buffer.ResetWrittenCount();
        NinoSerializer.Serialize(GenericUnmanagedTupleBase, _buffer);
        return _buffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("GenericUnmanagedTupleBaseSerialize")]
    public int MemoryPackSerializeGenericUnmanagedTupleBase()
    {
        _buffer.ResetWrittenCount();
        MemoryPackSerializer.Serialize(_buffer, GenericUnmanagedTupleBase);
        return _buffer.WrittenCount;
    }

    [Benchmark(Baseline = true), BenchmarkCategory("GenericManagedTupleBaseSerialize")]
    public int NinoSerializeGenericManagedTupleBase()
    {
        _buffer.ResetWrittenCount();
        NinoSerializer.Serialize(GenericManagedTupleBase, _buffer);
        return _buffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("GenericManagedTupleBaseSerialize")]
    public int MemoryPackSerializeGenericManagedTupleBase()
    {
        _buffer.ResetWrittenCount();
        MemoryPackSerializer.Serialize(_buffer, GenericManagedTupleBase);
        return _buffer.WrittenCount;
    }

    [Benchmark(Baseline = true), BenchmarkCategory("GenericSealedClassTupleBaseSerialize")]
    public int NinoSerializeGenericSealedClassTupleBase()
    {
        _buffer.ResetWrittenCount();
        NinoSerializer.Serialize(GenericSealedClassTupleBase, _buffer);
        return _buffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("GenericSealedClassTupleBaseSerialize")]
    public int MemoryPackSerializeGenericSealedClassTupleBase()
    {
        _buffer.ResetWrittenCount();
        MemoryPackSerializer.Serialize(_buffer, GenericSealedClassTupleBase);
        return _buffer.WrittenCount;
    }

    [Benchmark(Baseline = true), BenchmarkCategory("GenericOpenClassTupleBaseSerialize")]
    public int NinoSerializeGenericOpenClassTupleBase()
    {
        _buffer.ResetWrittenCount();
        NinoSerializer.Serialize(GenericOpenClassTupleBase, _buffer);
        return _buffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("GenericOpenClassTupleBaseSerialize")]
    public int MemoryPackSerializeGenericOpenClassTupleBase()
    {
        _buffer.ResetWrittenCount();
        MemoryPackSerializer.Serialize(_buffer, GenericOpenClassTupleBase);
        return _buffer.WrittenCount;
    }

    [Benchmark(Baseline = true), BenchmarkCategory("NonGenericBaseDeserialize")]
    public ProbeBase[] NinoDeserializeNonGenericBase() =>
        NinoDeserializer.Deserialize<ProbeBase[]>(NinoNonGenericBaseBytes);

    [Benchmark, BenchmarkCategory("NonGenericBaseDeserialize")]
    public ProbeBase[] MemoryPackDeserializeNonGenericBase() =>
        MemoryPackSerializer.Deserialize<ProbeBase[]>(MemoryPackNonGenericBaseBytes);

    [Benchmark(Baseline = true), BenchmarkCategory("GenericIntBaseDeserialize")]
    public ProbeBox<int>[] NinoDeserializeGenericIntBase() =>
        NinoDeserializer.Deserialize<ProbeBox<int>[]>(NinoGenericIntBaseBytes);

    [Benchmark, BenchmarkCategory("GenericIntBaseDeserialize")]
    public ProbeBox<int>[] MemoryPackDeserializeGenericIntBase() =>
        MemoryPackSerializer.Deserialize<ProbeBox<int>[]>(MemoryPackGenericIntBaseBytes);

    [Benchmark(Baseline = true), BenchmarkCategory("GenericUnmanagedTupleBaseDeserialize")]
    public ProbeBox<(int, long)>[] NinoDeserializeGenericUnmanagedTupleBase() =>
        NinoDeserializer.Deserialize<ProbeBox<(int, long)>[]>(NinoGenericUnmanagedTupleBaseBytes);

    [Benchmark, BenchmarkCategory("GenericUnmanagedTupleBaseDeserialize")]
    public ProbeBox<(int, long)>[] MemoryPackDeserializeGenericUnmanagedTupleBase() =>
        MemoryPackSerializer.Deserialize<ProbeBox<(int, long)>[]>(MemoryPackGenericUnmanagedTupleBaseBytes);

    [Benchmark(Baseline = true), BenchmarkCategory("GenericManagedTupleBaseDeserialize")]
    public ProbeBox<(int, string)>[] NinoDeserializeGenericManagedTupleBase() =>
        NinoDeserializer.Deserialize<ProbeBox<(int, string)>[]>(NinoGenericManagedTupleBaseBytes);

    [Benchmark, BenchmarkCategory("GenericManagedTupleBaseDeserialize")]
    public ProbeBox<(int, string)>[] MemoryPackDeserializeGenericManagedTupleBase() =>
        MemoryPackSerializer.Deserialize<ProbeBox<(int, string)>[]>(MemoryPackGenericManagedTupleBaseBytes);

    [Benchmark(Baseline = true), BenchmarkCategory("GenericSealedClassTupleBaseDeserialize")]
    public ProbeBox<(ProbeSealedItem, int)>[] NinoDeserializeGenericSealedClassTupleBase() =>
        NinoDeserializer.Deserialize<ProbeBox<(ProbeSealedItem, int)>[]>(NinoGenericSealedClassTupleBaseBytes);

    [Benchmark, BenchmarkCategory("GenericSealedClassTupleBaseDeserialize")]
    public ProbeBox<(ProbeSealedItem, int)>[] MemoryPackDeserializeGenericSealedClassTupleBase() =>
        MemoryPackSerializer.Deserialize<ProbeBox<(ProbeSealedItem, int)>[]>(MemoryPackGenericSealedClassTupleBaseBytes);

    [Benchmark(Baseline = true), BenchmarkCategory("GenericOpenClassTupleBaseDeserialize")]
    public ProbeBox<(ProbeOpenItem, int)>[] NinoDeserializeGenericOpenClassTupleBase() =>
        NinoDeserializer.Deserialize<ProbeBox<(ProbeOpenItem, int)>[]>(NinoGenericOpenClassTupleBaseBytes);

    [Benchmark, BenchmarkCategory("GenericOpenClassTupleBaseDeserialize")]
    public ProbeBox<(ProbeOpenItem, int)>[] MemoryPackDeserializeGenericOpenClassTupleBase() =>
        MemoryPackSerializer.Deserialize<ProbeBox<(ProbeOpenItem, int)>[]>(MemoryPackGenericOpenClassTupleBaseBytes);

    // MessagePack and protobuf-net.
    [Benchmark, BenchmarkCategory("NonGenericBaseSerialize")]
    public int MessagePackSerializeNonGenericBase()
    {
        _messagePackBuffer.ResetWrittenCount();
        MessagePackSerializer.Serialize(_messagePackBuffer, NonGenericBase);
        return _messagePackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("NonGenericBaseSerialize")]
    public int ProtobufSerializeNonGenericBase()
    {
        _protobufStream.SetLength(0);
        Serializer.Serialize(_protobufStream, NonGenericBase);
        return (int)_protobufStream.Length;
    }

    [Benchmark, BenchmarkCategory("GenericIntBaseSerialize")]
    public int ProtobufSerializeGenericIntBase()
    {
        _protobufStream.SetLength(0);
        Serializer.Serialize(_protobufStream, GenericIntBase);
        return (int)_protobufStream.Length;
    }

    [Benchmark, BenchmarkCategory("GenericUnmanagedTupleBaseSerialize")]
    public int ProtobufSerializeGenericUnmanagedTupleBase()
    {
        _protobufStream.SetLength(0);
        Serializer.Serialize(_protobufStream, GenericUnmanagedTupleBase);
        return (int)_protobufStream.Length;
    }

    [Benchmark, BenchmarkCategory("GenericManagedTupleBaseSerialize")]
    public int ProtobufSerializeGenericManagedTupleBase()
    {
        _protobufStream.SetLength(0);
        Serializer.Serialize(_protobufStream, GenericManagedTupleBase);
        return (int)_protobufStream.Length;
    }

    [Benchmark, BenchmarkCategory("GenericSealedClassTupleBaseSerialize")]
    public int ProtobufSerializeGenericSealedClassTupleBase()
    {
        _protobufStream.SetLength(0);
        Serializer.Serialize(_protobufStream, GenericSealedClassTupleBase);
        return (int)_protobufStream.Length;
    }

    [Benchmark, BenchmarkCategory("GenericOpenClassTupleBaseSerialize")]
    public int ProtobufSerializeGenericOpenClassTupleBase()
    {
        _protobufStream.SetLength(0);
        Serializer.Serialize(_protobufStream, GenericOpenClassTupleBase);
        return (int)_protobufStream.Length;
    }

    [Benchmark, BenchmarkCategory("NonGenericBaseDeserialize")]
    public ProbeBase[] MessagePackDeserializeNonGenericBase() =>
        MessagePackSerializer.Deserialize<ProbeBase[]>(MessagePackNonGenericBaseBytes);

    [Benchmark, BenchmarkCategory("NonGenericBaseDeserialize")]
    public ProbeBase[] ProtobufDeserializeNonGenericBase() =>
        Serializer.Deserialize<ProbeBase[]>(new ReadOnlySpan<byte>(ProtobufNonGenericBaseBytes));

    [Benchmark, BenchmarkCategory("GenericIntBaseDeserialize")]
    public ProbeBox<int>[] ProtobufDeserializeGenericIntBase() =>
        Serializer.Deserialize<ProbeBox<int>[]>(new ReadOnlySpan<byte>(ProtobufGenericIntBaseBytes));

    [Benchmark, BenchmarkCategory("GenericUnmanagedTupleBaseDeserialize")]
    public ProbeBox<(int, long)>[] ProtobufDeserializeGenericUnmanagedTupleBase() =>
        Serializer.Deserialize<ProbeBox<(int, long)>[]>(new ReadOnlySpan<byte>(ProtobufGenericUnmanagedTupleBaseBytes));

    [Benchmark, BenchmarkCategory("GenericManagedTupleBaseDeserialize")]
    public ProbeBox<(int, string)>[] ProtobufDeserializeGenericManagedTupleBase() =>
        Serializer.Deserialize<ProbeBox<(int, string)>[]>(new ReadOnlySpan<byte>(ProtobufGenericManagedTupleBaseBytes));

    [Benchmark, BenchmarkCategory("GenericSealedClassTupleBaseDeserialize")]
    public ProbeBox<(ProbeSealedItem, int)>[] ProtobufDeserializeGenericSealedClassTupleBase() =>
        Serializer.Deserialize<ProbeBox<(ProbeSealedItem, int)>[]>(new ReadOnlySpan<byte>(ProtobufGenericSealedClassTupleBaseBytes));

    [Benchmark, BenchmarkCategory("GenericOpenClassTupleBaseDeserialize")]
    public ProbeBox<(ProbeOpenItem, int)>[] ProtobufDeserializeGenericOpenClassTupleBase() =>
        Serializer.Deserialize<ProbeBox<(ProbeOpenItem, int)>[]>(new ReadOnlySpan<byte>(ProtobufGenericOpenClassTupleBaseBytes));
}

[NinoType, MemoryPackable, ProtoContract] public sealed partial class ProbeSealedItem { [ProtoMember(1)] public int X; [ProtoMember(2)] public long Y; }
[NinoType, MemoryPackable, ProtoContract] public partial class ProbeOpenItem { [ProtoMember(1)] public int X; [ProtoMember(2)] public long Y; }

[NinoType, MemoryPackable]
[MemoryPackUnion(0, typeof(ProbeA))]
[MemoryPackUnion(1, typeof(ProbeB))]
[MemoryPackUnion(2, typeof(ProbeC))]
[MemoryPackUnion(3, typeof(ProbeD))]
[MemoryPackUnion(4, typeof(ProbeE))]
[MemoryPackUnion(5, typeof(ProbeF))]
[MemoryPackUnion(6, typeof(ProbeG))]
[MemoryPackUnion(7, typeof(ProbeH))]
[MessagePackObject]
[Union(0, typeof(ProbeA))]
[Union(1, typeof(ProbeB))]
[Union(2, typeof(ProbeC))]
[Union(3, typeof(ProbeD))]
[Union(4, typeof(ProbeE))]
[Union(5, typeof(ProbeF))]
[Union(6, typeof(ProbeG))]
[Union(7, typeof(ProbeH))]
[ProtoContract]
[ProtoInclude(10, typeof(ProbeA))]
[ProtoInclude(11, typeof(ProbeB))]
[ProtoInclude(12, typeof(ProbeC))]
[ProtoInclude(13, typeof(ProbeD))]
[ProtoInclude(14, typeof(ProbeE))]
[ProtoInclude(15, typeof(ProbeF))]
[ProtoInclude(16, typeof(ProbeG))]
[ProtoInclude(17, typeof(ProbeH))]
public abstract partial class ProbeBase { [Key(0)] [ProtoMember(1)] public int V; }
[NinoType, MemoryPackable, MessagePackObject, ProtoContract] public sealed partial class ProbeA : ProbeBase { }
[NinoType, MemoryPackable, MessagePackObject, ProtoContract] public sealed partial class ProbeB : ProbeBase { }
[NinoType, MemoryPackable, MessagePackObject, ProtoContract] public sealed partial class ProbeC : ProbeBase { }
[NinoType, MemoryPackable, MessagePackObject, ProtoContract] public sealed partial class ProbeD : ProbeBase { }
[NinoType, MemoryPackable, MessagePackObject, ProtoContract] public sealed partial class ProbeE : ProbeBase { }
[NinoType, MemoryPackable, MessagePackObject, ProtoContract] public sealed partial class ProbeF : ProbeBase { }
[NinoType, MemoryPackable, MessagePackObject, ProtoContract] public sealed partial class ProbeG : ProbeBase { }
[NinoType, MemoryPackable, MessagePackObject, ProtoContract] public sealed partial class ProbeH : ProbeBase { }

// protobuf-net: the subtypes of each closed ProbeBox<T> are added to its model at startup (RegisterUnions).
[NinoType, ProtoContract] public abstract class ProbeBox<T> { [ProtoMember(1)] public T V; }
// PBN0013 (ProbeBox<T> does not declare them): protobuf-net's reflection model takes them from RegisterUnions.
#pragma warning disable PBN0013
[NinoType, MemoryPackable, ProtoContract] public sealed partial class ProbeBoxA : ProbeBox<int> { }
[NinoType, MemoryPackable, ProtoContract] public sealed partial class ProbeBoxB : ProbeBox<int> { }
[NinoType, MemoryPackable, ProtoContract] public sealed partial class ProbeBoxC : ProbeBox<int> { }
[NinoType, MemoryPackable, ProtoContract] public sealed partial class ProbeBoxD : ProbeBox<int> { }
[NinoType, MemoryPackable, ProtoContract] public sealed partial class ProbeBoxE : ProbeBox<int> { }
[NinoType, MemoryPackable, ProtoContract] public sealed partial class ProbeBoxF : ProbeBox<int> { }
[NinoType, MemoryPackable, ProtoContract] public sealed partial class ProbeBoxG : ProbeBox<int> { }
[NinoType, MemoryPackable, ProtoContract] public sealed partial class ProbeBoxH : ProbeBox<int> { }
[NinoType, MemoryPackable, ProtoContract] public sealed partial class ProbeTupA : ProbeBox<(int, long)> { }
[NinoType, MemoryPackable, ProtoContract] public sealed partial class ProbeTupB : ProbeBox<(int, long)> { }
[NinoType, MemoryPackable, ProtoContract] public sealed partial class ProbeTupC : ProbeBox<(int, long)> { }
[NinoType, MemoryPackable, ProtoContract] public sealed partial class ProbeTupD : ProbeBox<(int, long)> { }
[NinoType, MemoryPackable, ProtoContract] public sealed partial class ProbeTupE : ProbeBox<(int, long)> { }
[NinoType, MemoryPackable, ProtoContract] public sealed partial class ProbeTupF : ProbeBox<(int, long)> { }
[NinoType, MemoryPackable, ProtoContract] public sealed partial class ProbeTupG : ProbeBox<(int, long)> { }
[NinoType, MemoryPackable, ProtoContract] public sealed partial class ProbeTupH : ProbeBox<(int, long)> { }
[NinoType, MemoryPackable, ProtoContract] public sealed partial class ProbeStrA : ProbeBox<(int, string)> { }
[NinoType, MemoryPackable, ProtoContract] public sealed partial class ProbeStrB : ProbeBox<(int, string)> { }
[NinoType, MemoryPackable, ProtoContract] public sealed partial class ProbeStrC : ProbeBox<(int, string)> { }
[NinoType, MemoryPackable, ProtoContract] public sealed partial class ProbeStrD : ProbeBox<(int, string)> { }
[NinoType, MemoryPackable, ProtoContract] public sealed partial class ProbeStrE : ProbeBox<(int, string)> { }
[NinoType, MemoryPackable, ProtoContract] public sealed partial class ProbeStrF : ProbeBox<(int, string)> { }
[NinoType, MemoryPackable, ProtoContract] public sealed partial class ProbeStrG : ProbeBox<(int, string)> { }
[NinoType, MemoryPackable, ProtoContract] public sealed partial class ProbeStrH : ProbeBox<(int, string)> { }
[NinoType, MemoryPackable, ProtoContract] public sealed partial class ProbeScA : ProbeBox<(ProbeSealedItem, int)> { }
[NinoType, MemoryPackable, ProtoContract] public sealed partial class ProbeScB : ProbeBox<(ProbeSealedItem, int)> { }
[NinoType, MemoryPackable, ProtoContract] public sealed partial class ProbeScC : ProbeBox<(ProbeSealedItem, int)> { }
[NinoType, MemoryPackable, ProtoContract] public sealed partial class ProbeScD : ProbeBox<(ProbeSealedItem, int)> { }
[NinoType, MemoryPackable, ProtoContract] public sealed partial class ProbeScE : ProbeBox<(ProbeSealedItem, int)> { }
[NinoType, MemoryPackable, ProtoContract] public sealed partial class ProbeScF : ProbeBox<(ProbeSealedItem, int)> { }
[NinoType, MemoryPackable, ProtoContract] public sealed partial class ProbeScG : ProbeBox<(ProbeSealedItem, int)> { }
[NinoType, MemoryPackable, ProtoContract] public sealed partial class ProbeScH : ProbeBox<(ProbeSealedItem, int)> { }
[NinoType, MemoryPackable, ProtoContract] public sealed partial class ProbeOcA : ProbeBox<(ProbeOpenItem, int)> { }
[NinoType, MemoryPackable, ProtoContract] public sealed partial class ProbeOcB : ProbeBox<(ProbeOpenItem, int)> { }
[NinoType, MemoryPackable, ProtoContract] public sealed partial class ProbeOcC : ProbeBox<(ProbeOpenItem, int)> { }
[NinoType, MemoryPackable, ProtoContract] public sealed partial class ProbeOcD : ProbeBox<(ProbeOpenItem, int)> { }
[NinoType, MemoryPackable, ProtoContract] public sealed partial class ProbeOcE : ProbeBox<(ProbeOpenItem, int)> { }
[NinoType, MemoryPackable, ProtoContract] public sealed partial class ProbeOcF : ProbeBox<(ProbeOpenItem, int)> { }
[NinoType, MemoryPackable, ProtoContract] public sealed partial class ProbeOcG : ProbeBox<(ProbeOpenItem, int)> { }
[NinoType, MemoryPackable, ProtoContract] public sealed partial class ProbeOcH : ProbeBox<(ProbeOpenItem, int)> { }
#pragma warning restore PBN0013
#endif
