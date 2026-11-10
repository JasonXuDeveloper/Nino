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
/// The members of <see cref="SimpleClass"/> in a version-tolerant type: members can be appended later and data written
/// before still reads. Nino prefixes the body with its length (<see cref="NinoFormat.VersionTolerant"/>); MemoryPack
/// writes a member count and the length of every member (<see cref="GenerateType.VersionTolerant"/>, which needs an
/// explicit <see cref="MemoryPackOrderAttribute"/> on each member). MessagePack (integer keys: an array that later
/// versions extend) and protobuf (numbered fields) are version tolerant as they are.
/// </summary>
[NinoType(format: NinoFormat.VersionTolerant)]
[MemoryPackable(GenerateType.VersionTolerant)]
[MessagePackObject]
[ProtoContract]
public sealed partial class TolerantClass
{
    [MemoryPackOrder(0)] [Key(0)] [ProtoMember(1)] public int Id;
    [MemoryPackOrder(1)] [Key(1)] [ProtoMember(2)] public bool Tag;
    [MemoryPackOrder(2)] [Key(2)] [ProtoMember(3)] public Guid Guid;
    [MemoryPackOrder(3)] [Key(3)] [ProtoMember(4)] public DateTime CreateTime;
    [MemoryPackOrder(4)] [Key(4)] [ProtoMember(5)] public string Name { get; set; }
    [MemoryPackOrder(5)] [Key(5)] [ProtoMember(6)] [NinoUtf8] public string Desc;
    [MemoryPackOrder(6)] [Key(6)] [ProtoMember(7)] public int[] Numbers { get; set; }
    [MemoryPackOrder(7)] [Key(7)] [ProtoMember(8)] public List<DateTime> Dates { get; set; }
    [MemoryPackOrder(8)] [Key(8)] [ProtoMember(9)] public Dictionary<int, string> Map1;
    [MemoryPackOrder(9)] [Key(9)] [ProtoMember(10)] public Dictionary<int, int> Map2 { get; set; }

    public static TolerantClass From(SimpleClass c) => new()
    {
        Id = c.Id, Tag = c.Tag, Guid = c.Guid, CreateTime = c.CreateTime, Name = c.Name, Desc = c.Desc,
        Numbers = c.Numbers, Dates = c.Dates, Map1 = c.Map1, Map2 = c.Map2,
    };
}

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
