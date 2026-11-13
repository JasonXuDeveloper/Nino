using System;
using System.Buffers;
using System.IO;
using System.Linq;
using System.Text.Json;
using BenchmarkDotNet.Configs;
using Google.Protobuf;
using MemoryPack;
using MessagePack;
using Nino.Core;
using ProtoBuf;
using P = Nino.Benchmark.Proto;

#nullable disable
namespace Nino.Benchmark;

/// <summary>
/// A class of eight nullable members of common types (<c>int?</c>, <c>long?</c>, <c>double?</c>, <c>bool?</c>,
/// <c>DateTime?</c>, <c>Guid?</c>, <c>decimal?</c>, an enum), half of them null (<see cref="NullableFields"/>), alone and
/// in an array of 100 whose objects alternate which half is null. Against MemoryPack (through
/// <see cref="NullableFieldsMemoryPack"/>: <c>SafeNullable&lt;T&gt;</c> members, whose bytes have no uninitialized
/// padding), MessagePack, protobuf-net and Google.Protobuf (google.protobuf's wrappers and Timestamp, the messages
/// cases.proto has for the model's Level300 contract, parsed from protobuf-net's bytes outside the timed region) and
/// System.Text.Json (source generated, UTF-8). Each <c>Serialized*</c> array holds the bytes of MessagePack,
/// MemoryPack, Nino, protobuf-net, Google.Protobuf and System.Text.Json, in that order.
/// </summary>
[PayloadColumn]
[HideColumns("StdDev", "RatioSD", "Error")]
[MinColumn, MaxColumn]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[NinoJob]
[GcServer(true)]
[MemoryDiagnoser]
[MarkdownExporterAttribute.GitHub]
public class NullableTest : IReadBack
{
    private static readonly NullableFields NullableClass;
    private static readonly NullableFields[] NullableClasses;
    private static readonly NullableFieldsMemoryPack NullableClassMemoryPack;
    private static readonly NullableFieldsMemoryPack[] NullableClassesMemoryPack;

    private NinoArrayBufferWriter _ninoBuffer;
    private ArrayBufferWriter<byte> _memoryPackBuffer;
    private ArrayBufferWriter<byte> _messagePackBuffer;
    private MemoryStream _protobufStream;
    private ArrayBufferWriter<byte> _googleProtobufBuffer;
    private ArrayBufferWriter<byte> _jsonBuffer;
    private Utf8JsonWriter _jsonWriter;

    private static readonly byte[][] SerializedNullableClass;
    private static readonly P.NullableFields NullableClassProto;
    private static readonly byte[][] SerializedNullableClasses;
    private static readonly P.NullableFieldsList NullableClassesProto;

    static NullableTest()
    {
        var random = new Random(20240607);
        NullableClass = NullableFields.Create(0, random);
        NullableClasses = Enumerable.Range(0, 100).Select(i => NullableFields.Create(i, random)).ToArray();
        NullableClassMemoryPack = NullableFieldsMemoryPack.From(NullableClass);
        NullableClassesMemoryPack = SafeNullables.From(NullableClasses);

        SerializedNullableClass = new byte[6][];
        NullableClassProto = P.NullableFields.Parser.ParseFrom(ProtobufModel.Serialize(NullableClass));
        SerializedNullableClass[0] = MessagePackSerializer.Serialize(NullableClass);
        SerializedNullableClass[1] = MemoryPackSerializer.Serialize(NullableClassMemoryPack);
        SerializedNullableClass[2] = NinoSerializer.Serialize(NullableClass);
        SerializedNullableClass[3] = ProtobufModel.Serialize(NullableClass);
        SerializedNullableClass[4] = NullableClassProto.ToByteArray();
        SerializedNullableClass[5] = JsonSerializer.SerializeToUtf8Bytes(NullableClass, CaseJson.Default.NullableFields);

        SerializedNullableClasses = new byte[6][];
        NullableClassesProto = P.NullableFieldsList.Parser.ParseFrom(ProtobufModel.Serialize(NullableClasses));
        SerializedNullableClasses[0] = MessagePackSerializer.Serialize(NullableClasses);
        SerializedNullableClasses[1] = MemoryPackSerializer.Serialize(NullableClassesMemoryPack);
        SerializedNullableClasses[2] = NinoSerializer.Serialize(NullableClasses);
        SerializedNullableClasses[3] = ProtobufModel.Serialize(NullableClasses);
        SerializedNullableClasses[4] = NullableClassesProto.ToByteArray();
        SerializedNullableClasses[5] = JsonSerializer.SerializeToUtf8Bytes(NullableClasses, CaseJson.Default.NullableFieldsArray);

        string[] libraries = ["MessagePack", "MemoryPack", "Nino", "Protobuf", "GoogleProtobuf", "Json"];
        for (int i = 0; i < libraries.Length; i++)
        {
            BenchmarkPayloadRegistry.Register($"{libraries[i]}SerializeNullableClass", SerializedNullableClass[i].Length);
            BenchmarkPayloadRegistry.Register($"{libraries[i]}SerializeNullableClasses", SerializedNullableClasses[i].Length);
        }
    }

    [GlobalSetup]
    public void Setup()
    {
        ProtoFile.Cases.Check();
        _ninoBuffer = new NinoArrayBufferWriter(64 * 1024);
        _memoryPackBuffer = new ArrayBufferWriter<byte>(64 * 1024);
        _messagePackBuffer = new ArrayBufferWriter<byte>(64 * 1024);
        _protobufStream = new MemoryStream(64 * 1024);
        _googleProtobufBuffer = new ArrayBufferWriter<byte>(64 * 1024);
        _jsonBuffer = new ArrayBufferWriter<byte>(64 * 1024);
        // What JsonSerializer's own writers use: no validation of the writer's calls.
        _jsonWriter = new Utf8JsonWriter(_jsonBuffer, new JsonWriterOptions { SkipValidation = true });
    }

    /// <summary>Verify.cs: MemoryPack's SafeNullable model and Google.Protobuf's messages as <see cref="NullableFields"/>.</summary>
    public object ReadBack(object result, Type model) => ProtobufModel.ReadBack(SafeNullables.ReadBack(result), model);

    [Benchmark(Baseline = true), BenchmarkCategory("NullableClassSerialize")]
    public int NinoSerializeNullableClass()
    {
        _ninoBuffer.ResetWrittenCount();
        NinoSerializer.Serialize(NullableClass, _ninoBuffer);
        return _ninoBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("NullableClassSerialize")]
    public int MemoryPackSerializeNullableClass()
    {
        _memoryPackBuffer.ResetWrittenCount();
        MemoryPackSerializer.Serialize(_memoryPackBuffer, NullableClassMemoryPack);
        return _memoryPackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("NullableClassSerialize")]
    public int MessagePackSerializeNullableClass()
    {
        _messagePackBuffer.ResetWrittenCount();
        MessagePackSerializer.Serialize(_messagePackBuffer, NullableClass);
        return _messagePackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("NullableClassSerialize")]
    public int ProtobufSerializeNullableClass()
    {
        _protobufStream.SetLength(0);
        Serializer.Serialize(_protobufStream, NullableClass);
        return (int)_protobufStream.Length;
    }

    [Benchmark, BenchmarkCategory("NullableClassSerialize")]
    public int GoogleProtobufSerializeNullableClass()
    {
        _googleProtobufBuffer.ResetWrittenCount();
        NullableClassProto.WriteTo(_googleProtobufBuffer);
        return _googleProtobufBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("NullableClassSerialize")]
    public int JsonSerializeNullableClass()
    {
        _jsonBuffer.ResetWrittenCount();
        _jsonWriter.Reset(_jsonBuffer);
        JsonSerializer.Serialize(_jsonWriter, NullableClass, CaseJson.Default.NullableFields);
        return _jsonBuffer.WrittenCount;
    }

    [Benchmark(Baseline = true), BenchmarkCategory("NullableClassesSerialize")]
    public int NinoSerializeNullableClasses()
    {
        _ninoBuffer.ResetWrittenCount();
        NinoSerializer.Serialize(NullableClasses, _ninoBuffer);
        return _ninoBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("NullableClassesSerialize")]
    public int MemoryPackSerializeNullableClasses()
    {
        _memoryPackBuffer.ResetWrittenCount();
        MemoryPackSerializer.Serialize(_memoryPackBuffer, NullableClassesMemoryPack);
        return _memoryPackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("NullableClassesSerialize")]
    public int MessagePackSerializeNullableClasses()
    {
        _messagePackBuffer.ResetWrittenCount();
        MessagePackSerializer.Serialize(_messagePackBuffer, NullableClasses);
        return _messagePackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("NullableClassesSerialize")]
    public int ProtobufSerializeNullableClasses()
    {
        _protobufStream.SetLength(0);
        Serializer.Serialize(_protobufStream, NullableClasses);
        return (int)_protobufStream.Length;
    }

    [Benchmark, BenchmarkCategory("NullableClassesSerialize")]
    public int GoogleProtobufSerializeNullableClasses()
    {
        _googleProtobufBuffer.ResetWrittenCount();
        NullableClassesProto.WriteTo(_googleProtobufBuffer);
        return _googleProtobufBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("NullableClassesSerialize")]
    public int JsonSerializeNullableClasses()
    {
        _jsonBuffer.ResetWrittenCount();
        _jsonWriter.Reset(_jsonBuffer);
        JsonSerializer.Serialize(_jsonWriter, NullableClasses, CaseJson.Default.NullableFieldsArray);
        return _jsonBuffer.WrittenCount;
    }

    [Benchmark(Baseline = true), BenchmarkCategory("NullableClassDeserialize")]
    public NullableFields NinoDeserializeNullableClass() =>
        NinoDeserializer.Deserialize<NullableFields>(SerializedNullableClass[2]);

    [Benchmark, BenchmarkCategory("NullableClassDeserialize")]
    public NullableFieldsMemoryPack MemoryPackDeserializeNullableClass() =>
        MemoryPackSerializer.Deserialize<NullableFieldsMemoryPack>(SerializedNullableClass[1]);

    [Benchmark, BenchmarkCategory("NullableClassDeserialize")]
    public NullableFields MessagePackDeserializeNullableClass() =>
        MessagePackSerializer.Deserialize<NullableFields>(SerializedNullableClass[0]);

    [Benchmark, BenchmarkCategory("NullableClassDeserialize")]
    public NullableFields ProtobufDeserializeNullableClass() =>
        Serializer.Deserialize<NullableFields>(new ReadOnlySpan<byte>(SerializedNullableClass[3]));

    [Benchmark, BenchmarkCategory("NullableClassDeserialize")]
    public P.NullableFields GoogleProtobufDeserializeNullableClass() =>
        P.NullableFields.Parser.ParseFrom(new ReadOnlySpan<byte>(SerializedNullableClass[4]));

    [Benchmark, BenchmarkCategory("NullableClassDeserialize")]
    public NullableFields JsonDeserializeNullableClass() =>
        JsonSerializer.Deserialize(SerializedNullableClass[5], CaseJson.Default.NullableFields);

    [Benchmark(Baseline = true), BenchmarkCategory("NullableClassesDeserialize")]
    public NullableFields[] NinoDeserializeNullableClasses() =>
        NinoDeserializer.Deserialize<NullableFields[]>(SerializedNullableClasses[2]);

    [Benchmark, BenchmarkCategory("NullableClassesDeserialize")]
    public NullableFieldsMemoryPack[] MemoryPackDeserializeNullableClasses() =>
        MemoryPackSerializer.Deserialize<NullableFieldsMemoryPack[]>(SerializedNullableClasses[1]);

    [Benchmark, BenchmarkCategory("NullableClassesDeserialize")]
    public NullableFields[] MessagePackDeserializeNullableClasses() =>
        MessagePackSerializer.Deserialize<NullableFields[]>(SerializedNullableClasses[0]);

    [Benchmark, BenchmarkCategory("NullableClassesDeserialize")]
    public NullableFields[] ProtobufDeserializeNullableClasses() =>
        Serializer.Deserialize<NullableFields[]>(new ReadOnlySpan<byte>(SerializedNullableClasses[3]));

    [Benchmark, BenchmarkCategory("NullableClassesDeserialize")]
    public P.NullableFieldsList GoogleProtobufDeserializeNullableClasses() =>
        P.NullableFieldsList.Parser.ParseFrom(new ReadOnlySpan<byte>(SerializedNullableClasses[4]));

    [Benchmark, BenchmarkCategory("NullableClassesDeserialize")]
    public NullableFields[] JsonDeserializeNullableClasses() =>
        JsonSerializer.Deserialize(SerializedNullableClasses[5], CaseJson.Default.NullableFieldsArray);
}
