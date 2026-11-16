using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BenchmarkDotNet.Configs;
using MemoryPack;
using MessagePack;
using Nino.Core;
using ProtoBuf;

#nullable disable
namespace Nino.Benchmark;

/// <summary>
/// <see cref="TolerantClass"/> and an array of 100, written into a reused buffer writer and read back, as
/// <see cref="SimpleTest"/> does with <see cref="SimpleClass"/>: the difference between the two is the cost of version
/// tolerance.
/// </summary>
[PayloadColumn]
[HideColumns("StdDev", "RatioSD", "Error")]
[MinColumn, MaxColumn]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[NinoJob]
[GcServer(true)]
[MemoryDiagnoser]
[MarkdownExporterAttribute.GitHub]
public class VersionTolerantTest
{
    private static readonly TolerantClass TolerantClass = TolerantClass.From(SimpleClass.Create());
    private static readonly TolerantClass[] TolerantClasses =
        Enumerable.Range(0, 100).Select(_ => TolerantClass.From(SimpleClass.Create())).ToArray();

    private static readonly byte[] NinoTolerantClass = NinoSerializer.Serialize(TolerantClass);
    private static readonly byte[] MemoryPackTolerantClass = MemoryPackSerializer.Serialize(TolerantClass);
    private static readonly byte[] MessagePackTolerantClass = MessagePackSerializer.Serialize(TolerantClass);
    private static readonly byte[] ProtobufTolerantClass = ProtobufModel.Serialize(TolerantClass);
    private static readonly byte[] NinoTolerantClasses = NinoSerializer.Serialize(TolerantClasses);
    private static readonly byte[] MemoryPackTolerantClasses = MemoryPackSerializer.Serialize(TolerantClasses);
    private static readonly byte[] MessagePackTolerantClasses = MessagePackSerializer.Serialize(TolerantClasses);
    private static readonly byte[] ProtobufTolerantClasses = ProtobufModel.Serialize(TolerantClasses);

    private NinoArrayBufferWriter _ninoBuffer;
    private ArrayBufferWriter<byte> _memoryPackBuffer;
    private ArrayBufferWriter<byte> _messagePackBuffer;
    private MemoryStream _protobufStream;

    static VersionTolerantTest()
    {
        BenchmarkPayloadRegistry.Register(nameof(NinoSerializeTolerantClass), NinoTolerantClass.Length);
        BenchmarkPayloadRegistry.Register(nameof(MemoryPackSerializeTolerantClass), MemoryPackTolerantClass.Length);
        BenchmarkPayloadRegistry.Register(nameof(MessagePackSerializeTolerantClass), MessagePackTolerantClass.Length);
        BenchmarkPayloadRegistry.Register(nameof(ProtobufSerializeTolerantClass), ProtobufTolerantClass.Length);
        BenchmarkPayloadRegistry.Register(nameof(NinoSerializeTolerantClasses), NinoTolerantClasses.Length);
        BenchmarkPayloadRegistry.Register(nameof(MemoryPackSerializeTolerantClasses), MemoryPackTolerantClasses.Length);
        BenchmarkPayloadRegistry.Register(nameof(MessagePackSerializeTolerantClasses), MessagePackTolerantClasses.Length);
        BenchmarkPayloadRegistry.Register(nameof(ProtobufSerializeTolerantClasses), ProtobufTolerantClasses.Length);
    }

    [GlobalSetup]
    public void Setup()
    {
        _ninoBuffer = new NinoArrayBufferWriter(320 * 1024);
        _memoryPackBuffer = new ArrayBufferWriter<byte>(320 * 1024);
        _messagePackBuffer = new ArrayBufferWriter<byte>(320 * 1024);
        _protobufStream = new MemoryStream(320 * 1024);
    }

    [Benchmark(Baseline = true), BenchmarkCategory("TolerantClassSerialize")]
    public int NinoSerializeTolerantClass()
    {
        _ninoBuffer.ResetWrittenCount();
        NinoSerializer.Serialize(TolerantClass, _ninoBuffer);
        return _ninoBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("TolerantClassSerialize")]
    public int MemoryPackSerializeTolerantClass()
    {
        _memoryPackBuffer.ResetWrittenCount();
        MemoryPackSerializer.Serialize(_memoryPackBuffer, TolerantClass);
        return _memoryPackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("TolerantClassSerialize")]
    public int MessagePackSerializeTolerantClass()
    {
        _messagePackBuffer.ResetWrittenCount();
        MessagePackSerializer.Serialize(_messagePackBuffer, TolerantClass);
        return _messagePackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("TolerantClassSerialize")]
    public int ProtobufSerializeTolerantClass()
    {
        _protobufStream.SetLength(0);
        Serializer.Serialize(_protobufStream, TolerantClass);
        return (int)_protobufStream.Length;
    }

    [Benchmark(Baseline = true), BenchmarkCategory("TolerantClassesSerialize")]
    public int NinoSerializeTolerantClasses()
    {
        _ninoBuffer.ResetWrittenCount();
        NinoSerializer.Serialize(TolerantClasses, _ninoBuffer);
        return _ninoBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("TolerantClassesSerialize")]
    public int MemoryPackSerializeTolerantClasses()
    {
        _memoryPackBuffer.ResetWrittenCount();
        MemoryPackSerializer.Serialize(_memoryPackBuffer, TolerantClasses);
        return _memoryPackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("TolerantClassesSerialize")]
    public int MessagePackSerializeTolerantClasses()
    {
        _messagePackBuffer.ResetWrittenCount();
        MessagePackSerializer.Serialize(_messagePackBuffer, TolerantClasses);
        return _messagePackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("TolerantClassesSerialize")]
    public int ProtobufSerializeTolerantClasses()
    {
        _protobufStream.SetLength(0);
        Serializer.Serialize(_protobufStream, TolerantClasses);
        return (int)_protobufStream.Length;
    }

    [Benchmark(Baseline = true), BenchmarkCategory("TolerantClassDeserialize")]
    public TolerantClass NinoDeserializeTolerantClass() =>
        NinoDeserializer.Deserialize<TolerantClass>(NinoTolerantClass);

    [Benchmark, BenchmarkCategory("TolerantClassDeserialize")]
    public TolerantClass MemoryPackDeserializeTolerantClass() =>
        MemoryPackSerializer.Deserialize<TolerantClass>(MemoryPackTolerantClass);

    [Benchmark, BenchmarkCategory("TolerantClassDeserialize")]
    public TolerantClass MessagePackDeserializeTolerantClass() =>
        MessagePackSerializer.Deserialize<TolerantClass>(MessagePackTolerantClass);

    [Benchmark, BenchmarkCategory("TolerantClassDeserialize")]
    public TolerantClass ProtobufDeserializeTolerantClass() =>
        Serializer.Deserialize<TolerantClass>(new ReadOnlySpan<byte>(ProtobufTolerantClass));

    [Benchmark(Baseline = true), BenchmarkCategory("TolerantClassesDeserialize")]
    public TolerantClass[] NinoDeserializeTolerantClasses() =>
        NinoDeserializer.Deserialize<TolerantClass[]>(NinoTolerantClasses);

    [Benchmark, BenchmarkCategory("TolerantClassesDeserialize")]
    public TolerantClass[] MemoryPackDeserializeTolerantClasses() =>
        MemoryPackSerializer.Deserialize<TolerantClass[]>(MemoryPackTolerantClasses);

    [Benchmark, BenchmarkCategory("TolerantClassesDeserialize")]
    public TolerantClass[] MessagePackDeserializeTolerantClasses() =>
        MessagePackSerializer.Deserialize<TolerantClass[]>(MessagePackTolerantClasses);

    [Benchmark, BenchmarkCategory("TolerantClassesDeserialize")]
    public TolerantClass[] ProtobufDeserializeTolerantClasses() =>
        Serializer.Deserialize<TolerantClass[]>(new ReadOnlySpan<byte>(ProtobufTolerantClasses));
}
