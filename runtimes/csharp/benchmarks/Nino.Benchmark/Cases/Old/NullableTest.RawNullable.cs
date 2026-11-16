// A/B probe only (not for main): the hand-written class before BenchGen, to time against the generated one.
using System;
using System.Text.Json;
using BenchmarkDotNet.Attributes;
using Google.Protobuf;
using MemoryPack;
using MessagePack;
using Nino.Core;
using ProtoBuf;
using P = Nino.Benchmark.Proto;

#nullable disable
namespace Nino.Benchmark.Old;

/// <summary>
/// A raw (unmanaged) struct that holds nullable members (<see cref="RawNullable"/>: an int, a <c>long?</c> and a
/// <c>double?</c>), one value and an array of 1,000, in every form (<see cref="NullableForms"/>):
///  - Nino with <c>T?</c> members: each one a presence byte and the value when present, so neither the struct nor
///    the array is one copy;
///  - Nino with <see cref="NinoNullable{T}"/> members (<see cref="RawNullableNino"/>): a raw struct, written as its
///    memory, the array as one block;
///  - MemoryPack with <c>SafeNullable&lt;T&gt;</c> members the model already holds (<see cref="RawNullableMemoryPack"/>),
///    converting to them and back inside the timed region, and given the struct as it is (unsafe: it copies the
///    struct's memory with the uninitialized padding of each <c>T?</c>);
///  - MessagePack, protobuf-net (the members wrapped at Level300, as NullableFields), Google.Protobuf and
///    System.Text.Json.
/// </summary>
public partial class NullableTest
{
    private static RawNullable Raw;
    private static RawNullableNino RawNino;
    private static RawNullableMemoryPack RawMemoryPack;
    private static P.RawNullable RawProto;
    private static byte[] NinoRawNullableBytes, NinoNullableRawNullableBytes, MemoryPackRawNullableBytes,
        MemoryPackUnsafeRawNullableBytes, MessagePackRawNullableBytes, ProtobufRawNullableBytes,
        GoogleProtobufRawNullableBytes, JsonRawNullableBytes;
    private static RawNullable[] Raws;
    private static RawNullableNino[] RawsNino;
    private static RawNullableMemoryPack[] RawsMemoryPack;
    private static P.RawNullableList RawsProto;
    private static byte[] NinoRawNullablesBytes, NinoNullableRawNullablesBytes, MemoryPackRawNullablesBytes,
        MemoryPackUnsafeRawNullablesBytes, MessagePackRawNullablesBytes, ProtobufRawNullablesBytes,
        GoogleProtobufRawNullablesBytes, JsonRawNullablesBytes;

    private static void InitRawNullables()
    {
        // The fourth value of the sequence: a Total and no Score.
        Raw = RawNullable.Create(3);
        Raws = RawNullable.CreateArray(1000);
        RawNino = RawNullableNino.From(Raw);
        RawsNino = Array.ConvertAll(Raws, RawNullableNino.From);
        RawMemoryPack = RawNullableMemoryPack.From(Raw);
        RawsMemoryPack = SafeNullables.From(Raws);

        NinoRawNullableBytes = NinoSerializer.Serialize(Raw);
        NinoNullableRawNullableBytes = NinoSerializer.Serialize(RawNino);
        MemoryPackRawNullableBytes = MemoryPackSerializer.Serialize(RawMemoryPack);
        MemoryPackUnsafeRawNullableBytes = MemoryPackSerializer.Serialize(Raw);
        MessagePackRawNullableBytes = MessagePackSerializer.Serialize(Raw);
        ProtobufRawNullableBytes = ProtobufModel.Serialize(Raw);
        RawProto = P.RawNullable.Parser.ParseFrom(ProtobufRawNullableBytes);
        GoogleProtobufRawNullableBytes = RawProto.ToByteArray();
        JsonRawNullableBytes = JsonSerializer.SerializeToUtf8Bytes(Raw, CaseJson.Default.RawNullable);
        NinoRawNullablesBytes = NinoSerializer.Serialize(Raws);
        NinoNullableRawNullablesBytes = NinoSerializer.Serialize(RawsNino);
        MemoryPackRawNullablesBytes = MemoryPackSerializer.Serialize(RawsMemoryPack);
        MemoryPackUnsafeRawNullablesBytes = MemoryPackSerializer.Serialize(Raws);
        MessagePackRawNullablesBytes = MessagePackSerializer.Serialize(Raws);
        ProtobufRawNullablesBytes = ProtobufModel.Serialize(Raws);
        RawsProto = P.RawNullableList.Parser.ParseFrom(ProtobufRawNullablesBytes);
        GoogleProtobufRawNullablesBytes = RawsProto.ToByteArray();
        JsonRawNullablesBytes = JsonSerializer.SerializeToUtf8Bytes(Raws, CaseJson.Default.RawNullableArray);

        BenchmarkPayloadRegistry.Register(nameof(NinoSerializeRawNullable), NinoRawNullableBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(NinoNullableSerializeRawNullable), NinoNullableRawNullableBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(MemoryPackSerializeRawNullable), MemoryPackRawNullableBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(MemoryPackConvertedSerializeRawNullable), MemoryPackRawNullableBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(MemoryPackUnsafeSerializeRawNullable), MemoryPackUnsafeRawNullableBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(MessagePackSerializeRawNullable), MessagePackRawNullableBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(ProtobufSerializeRawNullable), ProtobufRawNullableBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(GoogleProtobufSerializeRawNullable), GoogleProtobufRawNullableBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(JsonSerializeRawNullable), JsonRawNullableBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(NinoSerializeRawNullables), NinoRawNullablesBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(NinoNullableSerializeRawNullables), NinoNullableRawNullablesBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(MemoryPackSerializeRawNullables), MemoryPackRawNullablesBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(MemoryPackConvertedSerializeRawNullables), MemoryPackRawNullablesBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(MemoryPackUnsafeSerializeRawNullables), MemoryPackUnsafeRawNullablesBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(MessagePackSerializeRawNullables), MessagePackRawNullablesBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(ProtobufSerializeRawNullables), ProtobufRawNullablesBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(GoogleProtobufSerializeRawNullables), GoogleProtobufRawNullablesBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(JsonSerializeRawNullables), JsonRawNullablesBytes.Length);
    }

    [Benchmark(Baseline = true), BenchmarkCategory("RawNullableSerialize")]
    public int NinoSerializeRawNullable()
    {
        _ninoBuffer.ResetWrittenCount();
        NinoSerializer.Serialize(Raw, _ninoBuffer);
        return _ninoBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("RawNullableSerialize")]
    public int NinoNullableSerializeRawNullable()
    {
        _ninoBuffer.ResetWrittenCount();
        NinoSerializer.Serialize(RawNino, _ninoBuffer);
        return _ninoBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("RawNullableSerialize")]
    public int MemoryPackSerializeRawNullable()
    {
        _memoryPackBuffer.ResetWrittenCount();
        MemoryPackSerializer.Serialize(_memoryPackBuffer, RawMemoryPack);
        return _memoryPackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("RawNullableSerialize")]
    public int MemoryPackConvertedSerializeRawNullable()
    {
        _memoryPackBuffer.ResetWrittenCount();
        MemoryPackSerializer.Serialize(_memoryPackBuffer, RawNullableMemoryPack.From(Raw));
        return _memoryPackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("RawNullableSerialize")]
    public int MemoryPackUnsafeSerializeRawNullable()
    {
        _memoryPackBuffer.ResetWrittenCount();
        MemoryPackSerializer.Serialize(_memoryPackBuffer, Raw);
        return _memoryPackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("RawNullableSerialize")]
    public int MessagePackSerializeRawNullable()
    {
        _messagePackBuffer.ResetWrittenCount();
        MessagePackSerializer.Serialize(_messagePackBuffer, Raw);
        return _messagePackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("RawNullableSerialize")]
    public int ProtobufSerializeRawNullable()
    {
        _protobufStream.SetLength(0);
        Serializer.Serialize(_protobufStream, Raw);
        return (int)_protobufStream.Length;
    }

    [Benchmark, BenchmarkCategory("RawNullableSerialize")]
    public int GoogleProtobufSerializeRawNullable()
    {
        _googleProtobufBuffer.ResetWrittenCount();
        RawProto.WriteTo(_googleProtobufBuffer);
        return _googleProtobufBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("RawNullableSerialize")]
    public int JsonSerializeRawNullable()
    {
        _jsonBuffer.ResetWrittenCount();
        _jsonWriter.Reset(_jsonBuffer);
        JsonSerializer.Serialize(_jsonWriter, Raw, CaseJson.Default.RawNullable);
        return _jsonBuffer.WrittenCount;
    }

    [Benchmark(Baseline = true), BenchmarkCategory("RawNullableDeserialize")]
    public RawNullable NinoDeserializeRawNullable() => NinoDeserializer.Deserialize<RawNullable>(NinoRawNullableBytes);

    [Benchmark, BenchmarkCategory("RawNullableDeserialize")]
    public RawNullableNino NinoNullableDeserializeRawNullable() =>
        NinoDeserializer.Deserialize<RawNullableNino>(NinoNullableRawNullableBytes);

    [Benchmark, BenchmarkCategory("RawNullableDeserialize")]
    public RawNullableMemoryPack MemoryPackDeserializeRawNullable() =>
        MemoryPackSerializer.Deserialize<RawNullableMemoryPack>(MemoryPackRawNullableBytes);

    [Benchmark, BenchmarkCategory("RawNullableDeserialize")]
    public RawNullable MemoryPackConvertedDeserializeRawNullable() =>
        MemoryPackSerializer.Deserialize<RawNullableMemoryPack>(MemoryPackRawNullableBytes).ToNullable();

    [Benchmark, BenchmarkCategory("RawNullableDeserialize")]
    public RawNullable MemoryPackUnsafeDeserializeRawNullable() =>
        MemoryPackSerializer.Deserialize<RawNullable>(MemoryPackUnsafeRawNullableBytes);

    [Benchmark, BenchmarkCategory("RawNullableDeserialize")]
    public RawNullable MessagePackDeserializeRawNullable() =>
        MessagePackSerializer.Deserialize<RawNullable>(MessagePackRawNullableBytes);

    [Benchmark, BenchmarkCategory("RawNullableDeserialize")]
    public RawNullable ProtobufDeserializeRawNullable() =>
        Serializer.Deserialize<RawNullable>(new ReadOnlySpan<byte>(ProtobufRawNullableBytes));

    [Benchmark, BenchmarkCategory("RawNullableDeserialize")]
    public P.RawNullable GoogleProtobufDeserializeRawNullable() =>
        P.RawNullable.Parser.ParseFrom(new ReadOnlySpan<byte>(GoogleProtobufRawNullableBytes));

    [Benchmark, BenchmarkCategory("RawNullableDeserialize")]
    public RawNullable JsonDeserializeRawNullable() =>
        JsonSerializer.Deserialize(JsonRawNullableBytes, CaseJson.Default.RawNullable);

    [Benchmark(Baseline = true), BenchmarkCategory("RawNullablesSerialize")]
    public int NinoSerializeRawNullables()
    {
        _ninoBuffer.ResetWrittenCount();
        NinoSerializer.Serialize(Raws, _ninoBuffer);
        return _ninoBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("RawNullablesSerialize")]
    public int NinoNullableSerializeRawNullables()
    {
        _ninoBuffer.ResetWrittenCount();
        NinoSerializer.Serialize(RawsNino, _ninoBuffer);
        return _ninoBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("RawNullablesSerialize")]
    public int MemoryPackSerializeRawNullables()
    {
        _memoryPackBuffer.ResetWrittenCount();
        MemoryPackSerializer.Serialize(_memoryPackBuffer, RawsMemoryPack);
        return _memoryPackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("RawNullablesSerialize")]
    public int MemoryPackConvertedSerializeRawNullables()
    {
        _memoryPackBuffer.ResetWrittenCount();
        MemoryPackSerializer.Serialize(_memoryPackBuffer, SafeNullables.From(Raws));
        return _memoryPackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("RawNullablesSerialize")]
    public int MemoryPackUnsafeSerializeRawNullables()
    {
        _memoryPackBuffer.ResetWrittenCount();
        MemoryPackSerializer.Serialize(_memoryPackBuffer, Raws);
        return _memoryPackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("RawNullablesSerialize")]
    public int MessagePackSerializeRawNullables()
    {
        _messagePackBuffer.ResetWrittenCount();
        MessagePackSerializer.Serialize(_messagePackBuffer, Raws);
        return _messagePackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("RawNullablesSerialize")]
    public int ProtobufSerializeRawNullables()
    {
        _protobufStream.SetLength(0);
        Serializer.Serialize(_protobufStream, Raws);
        return (int)_protobufStream.Length;
    }

    [Benchmark, BenchmarkCategory("RawNullablesSerialize")]
    public int GoogleProtobufSerializeRawNullables()
    {
        _googleProtobufBuffer.ResetWrittenCount();
        RawsProto.WriteTo(_googleProtobufBuffer);
        return _googleProtobufBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("RawNullablesSerialize")]
    public int JsonSerializeRawNullables()
    {
        _jsonBuffer.ResetWrittenCount();
        _jsonWriter.Reset(_jsonBuffer);
        JsonSerializer.Serialize(_jsonWriter, Raws, CaseJson.Default.RawNullableArray);
        return _jsonBuffer.WrittenCount;
    }

    [Benchmark(Baseline = true), BenchmarkCategory("RawNullablesDeserialize")]
    public RawNullable[] NinoDeserializeRawNullables() => NinoDeserializer.Deserialize<RawNullable[]>(NinoRawNullablesBytes);

    [Benchmark, BenchmarkCategory("RawNullablesDeserialize")]
    public RawNullableNino[] NinoNullableDeserializeRawNullables() =>
        NinoDeserializer.Deserialize<RawNullableNino[]>(NinoNullableRawNullablesBytes);

    [Benchmark, BenchmarkCategory("RawNullablesDeserialize")]
    public RawNullableMemoryPack[] MemoryPackDeserializeRawNullables() =>
        MemoryPackSerializer.Deserialize<RawNullableMemoryPack[]>(MemoryPackRawNullablesBytes);

    [Benchmark, BenchmarkCategory("RawNullablesDeserialize")]
    public RawNullable[] MemoryPackConvertedDeserializeRawNullables() =>
        Array.ConvertAll(MemoryPackSerializer.Deserialize<RawNullableMemoryPack[]>(MemoryPackRawNullablesBytes), v => v.ToNullable());

    [Benchmark, BenchmarkCategory("RawNullablesDeserialize")]
    public RawNullable[] MemoryPackUnsafeDeserializeRawNullables() =>
        MemoryPackSerializer.Deserialize<RawNullable[]>(MemoryPackUnsafeRawNullablesBytes);

    [Benchmark, BenchmarkCategory("RawNullablesDeserialize")]
    public RawNullable[] MessagePackDeserializeRawNullables() =>
        MessagePackSerializer.Deserialize<RawNullable[]>(MessagePackRawNullablesBytes);

    [Benchmark, BenchmarkCategory("RawNullablesDeserialize")]
    public RawNullable[] ProtobufDeserializeRawNullables() =>
        Serializer.Deserialize<RawNullable[]>(new ReadOnlySpan<byte>(ProtobufRawNullablesBytes));

    [Benchmark, BenchmarkCategory("RawNullablesDeserialize")]
    public P.RawNullableList GoogleProtobufDeserializeRawNullables() =>
        P.RawNullableList.Parser.ParseFrom(new ReadOnlySpan<byte>(GoogleProtobufRawNullablesBytes));

    [Benchmark, BenchmarkCategory("RawNullablesDeserialize")]
    public RawNullable[] JsonDeserializeRawNullables() =>
        JsonSerializer.Deserialize(JsonRawNullablesBytes, CaseJson.Default.RawNullableArray);
}
