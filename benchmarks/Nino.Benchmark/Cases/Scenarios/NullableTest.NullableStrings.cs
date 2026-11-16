using System;
using System.Linq;
using System.Text.Json;
using BenchmarkDotNet.Attributes;
using Google.Protobuf;
using MemoryPack;
using MessagePack;
using Nino.Core;
using ProtoBuf;
using P = Nino.Benchmark.Proto;

#nullable disable
namespace Nino.Benchmark;

/// <summary>
/// Nullable references in a collection of strings: <c>string[1000]</c> with every fifth element null (NullableObjects is
/// the same of a class, SimpleClass[100]). Every library writes a null string natively but protobuf, which has no
/// null in a repeated field: protobuf-net wraps each element (<see cref="NullableStringList"/>), and Google.Protobuf
/// reads the message that generates. A reference has no other form: <c>NinoNullable&lt;T&gt;</c> and
/// <c>SafeNullable&lt;T&gt;</c> take an unmanaged T.
/// </summary>
public partial class NullableTest
{
    private static string[] Strings;
    private static NullableStringList StringsProtobuf;
    private static P.NullableStringList StringsProto;
    private static byte[] NinoNullableStringsBytes, MemoryPackNullableStringsBytes, MessagePackNullableStringsBytes,
        ProtobufNullableStringsBytes, GoogleProtobufNullableStringsBytes, JsonNullableStringsBytes;

    private static void InitNullableStrings()
    {
        Strings = Enumerable.Range(0, 1000).Select(i => i % 5 == 0 ? null : "item-" + i.ToString("D4")).ToArray();
        StringsProtobuf = new NullableStringList { Items = Strings };

        NinoNullableStringsBytes = NinoSerializer.Serialize(Strings);
        MemoryPackNullableStringsBytes = MemoryPackSerializer.Serialize(Strings);
        MessagePackNullableStringsBytes = MessagePackSerializer.Serialize(Strings);
        ProtobufNullableStringsBytes = ProtobufModel.Serialize(StringsProtobuf);
        StringsProto = P.NullableStringList.Parser.ParseFrom(ProtobufNullableStringsBytes);
        GoogleProtobufNullableStringsBytes = StringsProto.ToByteArray();
        JsonNullableStringsBytes = JsonSerializer.SerializeToUtf8Bytes(Strings, CaseJson.Default.StringArray);

        BenchmarkPayloadRegistry.Register(nameof(NinoSerializeNullableStrings), NinoNullableStringsBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(MemoryPackSerializeNullableStrings), MemoryPackNullableStringsBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(MessagePackSerializeNullableStrings), MessagePackNullableStringsBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(ProtobufSerializeNullableStrings), ProtobufNullableStringsBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(GoogleProtobufSerializeNullableStrings), GoogleProtobufNullableStringsBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(JsonSerializeNullableStrings), JsonNullableStringsBytes.Length);
    }

    [Benchmark(Baseline = true), BenchmarkCategory("NullableStringsSerialize")]
    public int NinoSerializeNullableStrings()
    {
        _ninoBuffer.ResetWrittenCount();
        NinoSerializer.Serialize(Strings, _ninoBuffer);
        return _ninoBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("NullableStringsSerialize")]
    public int MemoryPackSerializeNullableStrings()
    {
        _memoryPackBuffer.ResetWrittenCount();
        MemoryPackSerializer.Serialize(_memoryPackBuffer, Strings);
        return _memoryPackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("NullableStringsSerialize")]
    public int MessagePackSerializeNullableStrings()
    {
        _messagePackBuffer.ResetWrittenCount();
        MessagePackSerializer.Serialize(_messagePackBuffer, Strings);
        return _messagePackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("NullableStringsSerialize")]
    public int ProtobufSerializeNullableStrings()
    {
        _protobufStream.SetLength(0);
        Serializer.Serialize(_protobufStream, StringsProtobuf);
        return (int)_protobufStream.Length;
    }

    [Benchmark, BenchmarkCategory("NullableStringsSerialize")]
    public int GoogleProtobufSerializeNullableStrings()
    {
        _googleProtobufBuffer.ResetWrittenCount();
        StringsProto.WriteTo(_googleProtobufBuffer);
        return _googleProtobufBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("NullableStringsSerialize")]
    public int JsonSerializeNullableStrings()
    {
        _jsonBuffer.ResetWrittenCount();
        _jsonWriter.Reset(_jsonBuffer);
        JsonSerializer.Serialize(_jsonWriter, Strings, CaseJson.Default.StringArray);
        return _jsonBuffer.WrittenCount;
    }

    [Benchmark(Baseline = true), BenchmarkCategory("NullableStringsDeserialize")]
    public string[] NinoDeserializeNullableStrings() => NinoDeserializer.Deserialize<string[]>(NinoNullableStringsBytes);

    [Benchmark, BenchmarkCategory("NullableStringsDeserialize")]
    public string[] MemoryPackDeserializeNullableStrings() =>
        MemoryPackSerializer.Deserialize<string[]>(MemoryPackNullableStringsBytes);

    [Benchmark, BenchmarkCategory("NullableStringsDeserialize")]
    public string[] MessagePackDeserializeNullableStrings() =>
        MessagePackSerializer.Deserialize<string[]>(MessagePackNullableStringsBytes);

    [Benchmark, BenchmarkCategory("NullableStringsDeserialize")]
    public string[] ProtobufDeserializeNullableStrings() =>
        Serializer.Deserialize<NullableStringList>(new ReadOnlySpan<byte>(ProtobufNullableStringsBytes)).Items;

    [Benchmark, BenchmarkCategory("NullableStringsDeserialize")]
    public P.NullableStringList GoogleProtobufDeserializeNullableStrings() =>
        P.NullableStringList.Parser.ParseFrom(new ReadOnlySpan<byte>(GoogleProtobufNullableStringsBytes));

    [Benchmark, BenchmarkCategory("NullableStringsDeserialize")]
    public string[] JsonDeserializeNullableStrings() =>
        JsonSerializer.Deserialize(JsonNullableStringsBytes, CaseJson.Default.StringArray);
}
