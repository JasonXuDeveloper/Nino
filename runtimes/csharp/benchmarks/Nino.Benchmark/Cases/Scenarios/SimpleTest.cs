using System;
using System.Buffers;
using System.IO;
using System.Linq;
using System.Numerics;
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
/// Plain classes, structs and vectors, alone and in arrays, against MemoryPack, MessagePack, protobuf-net,
/// Google.Protobuf (the messages protobuf-net's contracts generate, cases.proto, parsed from protobuf-net's bytes outside
/// the timed region) and System.Text.Json (source generated, UTF-8). Each <c>Serialized*</c> array holds the bytes of
/// MessagePack, MemoryPack, Nino, protobuf-net, Google.Protobuf and System.Text.Json, in that order.
/// </summary>
[PayloadColumn]
[HideColumns("StdDev", "RatioSD", "Error")]
[MinColumn, MaxColumn]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[NinoJob]
[GcServer(true)]
[MemoryDiagnoser]
[MarkdownExporterAttribute.GitHub]
public class SimpleTest : IReadBack
{
    private static readonly SimpleClass SimpleClass;
    private static readonly SimpleClass[] SimpleClasses;
    private static readonly SimpleStruct SimpleStruct;
    private static readonly SimpleStruct[] SimpleStructs;
    private static readonly Vector4[] Vectors;

    private NinoArrayBufferWriter _ninoBuffer;
    private ArrayBufferWriter<byte> _memoryPackBuffer;
    private ArrayBufferWriter<byte> _messagePackBuffer;
    private MemoryStream _protobufStream;
    private ArrayBufferWriter<byte> _googleProtobufBuffer;
    private ArrayBufferWriter<byte> _jsonBuffer;
    private Utf8JsonWriter _jsonWriter;

    private static readonly byte[][] SerializedSimpleClass;
    private static readonly P.SimpleClassBase SimpleClassProto;
    private static readonly byte[][] SerializedSimpleStruct;
    private static readonly P.SimpleStruct SimpleStructProto;
    private static readonly byte[][] SerializedSimpleClasses;
    private static readonly P.SimpleClassList SimpleClassesProto;
    private static readonly byte[][] SerializedSimpleStructs;
    private static readonly P.SimpleStructList SimpleStructsProto;
    private static readonly byte[][] SerializedVectors;
    private static readonly P.VectorList VectorsProto;

    static SimpleTest()
    {
        _ = ProtobufModel.Ready; // Vector4's fields
        SimpleClass = SimpleClass.Create();
        SimpleClasses = Enumerable.Range(0, 100).Select(_ => SimpleClass.Create()).ToArray();
        SimpleStruct = SimpleStruct.Create();
        SimpleStructs = Enumerable.Range(0, 100).Select(_ => SimpleStruct.Create()).ToArray();
        var r = BenchmarkRandom.Shared;
        Vectors = Enumerable.Range(0, 10000)
            .Select(_ => new Vector4(r.NextSingle(), r.NextSingle(),
                r.NextSingle(), r.NextSingle())).ToArray();

        SerializedSimpleClass = new byte[6][];
        SimpleClassProto = P.SimpleClassBase.Parser.ParseFrom(ProtobufModel.Serialize(SimpleClass));
        SerializedSimpleClass[0] = MessagePackSerializer.Serialize(SimpleClass);
        SerializedSimpleClass[1] = MemoryPackSerializer.Serialize(SimpleClass);
        SerializedSimpleClass[2] = NinoSerializer.Serialize(SimpleClass);
        SerializedSimpleClass[3] = ProtobufModel.Serialize(SimpleClass);
        SerializedSimpleClass[4] = SimpleClassProto.ToByteArray();
        SerializedSimpleClass[5] = JsonSerializer.SerializeToUtf8Bytes(SimpleClass, CaseJson.Default.SimpleClass);

        SerializedSimpleStruct = new byte[6][];
        SimpleStructProto = P.SimpleStruct.Parser.ParseFrom(ProtobufModel.Serialize(SimpleStruct));
        SerializedSimpleStruct[0] = MessagePackSerializer.Serialize(SimpleStruct);
        SerializedSimpleStruct[1] = MemoryPackSerializer.Serialize(SimpleStruct);
        SerializedSimpleStruct[2] = NinoSerializer.Serialize(SimpleStruct);
        SerializedSimpleStruct[3] = ProtobufModel.Serialize(SimpleStruct);
        SerializedSimpleStruct[4] = SimpleStructProto.ToByteArray();
        SerializedSimpleStruct[5] = JsonSerializer.SerializeToUtf8Bytes(SimpleStruct, CaseJson.Default.SimpleStruct);

        SerializedSimpleClasses = new byte[6][];
        SimpleClassesProto = P.SimpleClassList.Parser.ParseFrom(ProtobufModel.Serialize(SimpleClasses));
        SerializedSimpleClasses[0] = MessagePackSerializer.Serialize(SimpleClasses);
        SerializedSimpleClasses[1] = MemoryPackSerializer.Serialize(SimpleClasses);
        SerializedSimpleClasses[2] = NinoSerializer.Serialize(SimpleClasses);
        SerializedSimpleClasses[3] = ProtobufModel.Serialize(SimpleClasses);
        SerializedSimpleClasses[4] = SimpleClassesProto.ToByteArray();
        SerializedSimpleClasses[5] = JsonSerializer.SerializeToUtf8Bytes(SimpleClasses, CaseJson.Default.SimpleClassArray);

        SerializedSimpleStructs = new byte[6][];
        SimpleStructsProto = P.SimpleStructList.Parser.ParseFrom(ProtobufModel.Serialize(SimpleStructs));
        SerializedSimpleStructs[0] = MessagePackSerializer.Serialize(SimpleStructs);
        SerializedSimpleStructs[1] = MemoryPackSerializer.Serialize(SimpleStructs);
        SerializedSimpleStructs[2] = NinoSerializer.Serialize(SimpleStructs);
        SerializedSimpleStructs[3] = ProtobufModel.Serialize(SimpleStructs);
        SerializedSimpleStructs[4] = SimpleStructsProto.ToByteArray();
        SerializedSimpleStructs[5] = JsonSerializer.SerializeToUtf8Bytes(SimpleStructs, CaseJson.Default.SimpleStructArray);

        SerializedVectors = new byte[6][];
        VectorsProto = P.VectorList.Parser.ParseFrom(ProtobufModel.Serialize(Vectors));
        SerializedVectors[0] = MessagePackSerializer.Serialize(Vectors);
        SerializedVectors[1] = MemoryPackSerializer.Serialize(Vectors);
        SerializedVectors[2] = NinoSerializer.Serialize(Vectors);
        SerializedVectors[3] = ProtobufModel.Serialize(Vectors);
        SerializedVectors[4] = VectorsProto.ToByteArray();
        SerializedVectors[5] = JsonSerializer.SerializeToUtf8Bytes(Vectors, CaseJson.Default.Vector4Array);

        BenchmarkPayloadRegistry.Register(nameof(NinoSerializeSimpleClass), SerializedSimpleClass[2].Length);
        BenchmarkPayloadRegistry.Register(nameof(NinoSerializeSimpleStruct), SerializedSimpleStruct[2].Length);
        BenchmarkPayloadRegistry.Register(nameof(NinoSerializeSimpleClasses), SerializedSimpleClasses[2].Length);
        BenchmarkPayloadRegistry.Register(nameof(NinoSerializeSimpleStructs), SerializedSimpleStructs[2].Length);
        BenchmarkPayloadRegistry.Register(nameof(NinoSerializeVectors), SerializedVectors[2].Length);

        BenchmarkPayloadRegistry.Register(nameof(MemoryPackSerializeSimpleClass), SerializedSimpleClass[1].Length);
        BenchmarkPayloadRegistry.Register(nameof(MemoryPackSerializeSimpleStruct), SerializedSimpleStruct[1].Length);
        BenchmarkPayloadRegistry.Register(nameof(MemoryPackSerializeSimpleClasses), SerializedSimpleClasses[1].Length);
        BenchmarkPayloadRegistry.Register(nameof(MemoryPackSerializeSimpleStructs), SerializedSimpleStructs[1].Length);
        BenchmarkPayloadRegistry.Register(nameof(MemoryPackSerializeVectors), SerializedVectors[1].Length);

        BenchmarkPayloadRegistry.Register(nameof(MessagePackSerializeSimpleClass), SerializedSimpleClass[0].Length);
        BenchmarkPayloadRegistry.Register(nameof(ProtobufSerializeSimpleClass), SerializedSimpleClass[3].Length);
        BenchmarkPayloadRegistry.Register(nameof(GoogleProtobufSerializeSimpleClass), SerializedSimpleClass[4].Length);
        BenchmarkPayloadRegistry.Register(nameof(JsonSerializeSimpleClass), SerializedSimpleClass[5].Length);
        BenchmarkPayloadRegistry.Register(nameof(MessagePackSerializeSimpleStruct), SerializedSimpleStruct[0].Length);
        BenchmarkPayloadRegistry.Register(nameof(ProtobufSerializeSimpleStruct), SerializedSimpleStruct[3].Length);
        BenchmarkPayloadRegistry.Register(nameof(GoogleProtobufSerializeSimpleStruct), SerializedSimpleStruct[4].Length);
        BenchmarkPayloadRegistry.Register(nameof(JsonSerializeSimpleStruct), SerializedSimpleStruct[5].Length);
        BenchmarkPayloadRegistry.Register(nameof(MessagePackSerializeSimpleClasses), SerializedSimpleClasses[0].Length);
        BenchmarkPayloadRegistry.Register(nameof(ProtobufSerializeSimpleClasses), SerializedSimpleClasses[3].Length);
        BenchmarkPayloadRegistry.Register(nameof(GoogleProtobufSerializeSimpleClasses), SerializedSimpleClasses[4].Length);
        BenchmarkPayloadRegistry.Register(nameof(JsonSerializeSimpleClasses), SerializedSimpleClasses[5].Length);
        BenchmarkPayloadRegistry.Register(nameof(MessagePackSerializeSimpleStructs), SerializedSimpleStructs[0].Length);
        BenchmarkPayloadRegistry.Register(nameof(ProtobufSerializeSimpleStructs), SerializedSimpleStructs[3].Length);
        BenchmarkPayloadRegistry.Register(nameof(GoogleProtobufSerializeSimpleStructs), SerializedSimpleStructs[4].Length);
        BenchmarkPayloadRegistry.Register(nameof(JsonSerializeSimpleStructs), SerializedSimpleStructs[5].Length);
        BenchmarkPayloadRegistry.Register(nameof(MessagePackSerializeVectors), SerializedVectors[0].Length);
        BenchmarkPayloadRegistry.Register(nameof(ProtobufSerializeVectors), SerializedVectors[3].Length);
        BenchmarkPayloadRegistry.Register(nameof(GoogleProtobufSerializeVectors), SerializedVectors[4].Length);
        BenchmarkPayloadRegistry.Register(nameof(JsonSerializeVectors), SerializedVectors[5].Length);
    }

    [GlobalSetup]
    public void Setup()
    {
        ProtoFile.Cases.Check();
        _ninoBuffer = new NinoArrayBufferWriter(320 * 1024);
        _memoryPackBuffer = new ArrayBufferWriter<byte>(320 * 1024);
        _messagePackBuffer = new ArrayBufferWriter<byte>(320 * 1024);
        _protobufStream = new MemoryStream(320 * 1024);
        _googleProtobufBuffer = new ArrayBufferWriter<byte>(320 * 1024);
        _jsonBuffer = new ArrayBufferWriter<byte>(320 * 1024);
        // What JsonSerializer's own writers use: no validation of the writer's calls.
        _jsonWriter = new Utf8JsonWriter(_jsonBuffer, new JsonWriterOptions { SkipValidation = true });
    }

    /// <summary>Verify.cs: a Google.Protobuf message read back through protobuf-net as the case's model.</summary>
    public object ReadBack(object result, Type model) => ProtobufModel.ReadBack(result, model);

    [Benchmark(Baseline = true), BenchmarkCategory("SimpleClassSerialize")]
    public int NinoSerializeSimpleClass()
    {
        _ninoBuffer.ResetWrittenCount();
        NinoSerializer.Serialize(SimpleClass, _ninoBuffer);
        return _ninoBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("SimpleClassSerialize")]
    public int MemoryPackSerializeSimpleClass()
    {
        _memoryPackBuffer.ResetWrittenCount();
        MemoryPackSerializer.Serialize(_memoryPackBuffer, SimpleClass);
        return _memoryPackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("SimpleClassSerialize")]
    public int MessagePackSerializeSimpleClass()
    {
        _messagePackBuffer.ResetWrittenCount();
        MessagePackSerializer.Serialize(_messagePackBuffer, SimpleClass);
        return _messagePackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("SimpleClassSerialize")]
    public int ProtobufSerializeSimpleClass()
    {
        _protobufStream.SetLength(0);
        Serializer.Serialize(_protobufStream, SimpleClass);
        return (int)_protobufStream.Length;
    }

    [Benchmark, BenchmarkCategory("SimpleClassSerialize")]
    public int GoogleProtobufSerializeSimpleClass()
    {
        _googleProtobufBuffer.ResetWrittenCount();
        SimpleClassProto.WriteTo(_googleProtobufBuffer);
        return _googleProtobufBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("SimpleClassSerialize")]
    public int JsonSerializeSimpleClass()
    {
        _jsonBuffer.ResetWrittenCount();
        _jsonWriter.Reset(_jsonBuffer);
        JsonSerializer.Serialize(_jsonWriter, SimpleClass, CaseJson.Default.SimpleClass);
        return _jsonBuffer.WrittenCount;
    }

    [Benchmark(Baseline = true), BenchmarkCategory("SimpleStructSerialize")]
    public int NinoSerializeSimpleStruct()
    {
        _ninoBuffer.ResetWrittenCount();
        NinoSerializer.Serialize(SimpleStruct, _ninoBuffer);
        return _ninoBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("SimpleStructSerialize")]
    public int MemoryPackSerializeSimpleStruct()
    {
        _memoryPackBuffer.ResetWrittenCount();
        MemoryPackSerializer.Serialize(_memoryPackBuffer, SimpleStruct);
        return _memoryPackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("SimpleStructSerialize")]
    public int MessagePackSerializeSimpleStruct()
    {
        _messagePackBuffer.ResetWrittenCount();
        MessagePackSerializer.Serialize(_messagePackBuffer, SimpleStruct);
        return _messagePackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("SimpleStructSerialize")]
    public int ProtobufSerializeSimpleStruct()
    {
        _protobufStream.SetLength(0);
        Serializer.Serialize(_protobufStream, SimpleStruct);
        return (int)_protobufStream.Length;
    }

    [Benchmark, BenchmarkCategory("SimpleStructSerialize")]
    public int GoogleProtobufSerializeSimpleStruct()
    {
        _googleProtobufBuffer.ResetWrittenCount();
        SimpleStructProto.WriteTo(_googleProtobufBuffer);
        return _googleProtobufBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("SimpleStructSerialize")]
    public int JsonSerializeSimpleStruct()
    {
        _jsonBuffer.ResetWrittenCount();
        _jsonWriter.Reset(_jsonBuffer);
        JsonSerializer.Serialize(_jsonWriter, SimpleStruct, CaseJson.Default.SimpleStruct);
        return _jsonBuffer.WrittenCount;
    }

    [Benchmark(Baseline = true), BenchmarkCategory("SimpleClassesSerialize")]
    public int NinoSerializeSimpleClasses()
    {
        _ninoBuffer.ResetWrittenCount();
        NinoSerializer.Serialize(SimpleClasses, _ninoBuffer);
        return _ninoBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("SimpleClassesSerialize")]
    public int MemoryPackSerializeSimpleClasses()
    {
        _memoryPackBuffer.ResetWrittenCount();
        MemoryPackSerializer.Serialize(_memoryPackBuffer, SimpleClasses);
        return _memoryPackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("SimpleClassesSerialize")]
    public int MessagePackSerializeSimpleClasses()
    {
        _messagePackBuffer.ResetWrittenCount();
        MessagePackSerializer.Serialize(_messagePackBuffer, SimpleClasses);
        return _messagePackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("SimpleClassesSerialize")]
    public int ProtobufSerializeSimpleClasses()
    {
        _protobufStream.SetLength(0);
        Serializer.Serialize(_protobufStream, SimpleClasses);
        return (int)_protobufStream.Length;
    }

    [Benchmark, BenchmarkCategory("SimpleClassesSerialize")]
    public int GoogleProtobufSerializeSimpleClasses()
    {
        _googleProtobufBuffer.ResetWrittenCount();
        SimpleClassesProto.WriteTo(_googleProtobufBuffer);
        return _googleProtobufBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("SimpleClassesSerialize")]
    public int JsonSerializeSimpleClasses()
    {
        _jsonBuffer.ResetWrittenCount();
        _jsonWriter.Reset(_jsonBuffer);
        JsonSerializer.Serialize(_jsonWriter, SimpleClasses, CaseJson.Default.SimpleClassArray);
        return _jsonBuffer.WrittenCount;
    }

    [Benchmark(Baseline = true), BenchmarkCategory("SimpleStructsSerialize")]
    public int NinoSerializeSimpleStructs()
    {
        _ninoBuffer.ResetWrittenCount();
        NinoSerializer.Serialize(SimpleStructs, _ninoBuffer);
        return _ninoBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("SimpleStructsSerialize")]
    public int MemoryPackSerializeSimpleStructs()
    {
        _memoryPackBuffer.ResetWrittenCount();
        MemoryPackSerializer.Serialize(_memoryPackBuffer, SimpleStructs);
        return _memoryPackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("SimpleStructsSerialize")]
    public int MessagePackSerializeSimpleStructs()
    {
        _messagePackBuffer.ResetWrittenCount();
        MessagePackSerializer.Serialize(_messagePackBuffer, SimpleStructs);
        return _messagePackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("SimpleStructsSerialize")]
    public int ProtobufSerializeSimpleStructs()
    {
        _protobufStream.SetLength(0);
        Serializer.Serialize(_protobufStream, SimpleStructs);
        return (int)_protobufStream.Length;
    }

    [Benchmark, BenchmarkCategory("SimpleStructsSerialize")]
    public int GoogleProtobufSerializeSimpleStructs()
    {
        _googleProtobufBuffer.ResetWrittenCount();
        SimpleStructsProto.WriteTo(_googleProtobufBuffer);
        return _googleProtobufBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("SimpleStructsSerialize")]
    public int JsonSerializeSimpleStructs()
    {
        _jsonBuffer.ResetWrittenCount();
        _jsonWriter.Reset(_jsonBuffer);
        JsonSerializer.Serialize(_jsonWriter, SimpleStructs, CaseJson.Default.SimpleStructArray);
        return _jsonBuffer.WrittenCount;
    }

    [Benchmark(Baseline = true), BenchmarkCategory("VectorsSerialize")]
    public int NinoSerializeVectors()
    {
        _ninoBuffer.ResetWrittenCount();
        NinoSerializer.Serialize(Vectors, _ninoBuffer);
        return _ninoBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("VectorsSerialize")]
    public int MemoryPackSerializeVectors()
    {
        _memoryPackBuffer.ResetWrittenCount();
        MemoryPackSerializer.Serialize(_memoryPackBuffer, Vectors);
        return _memoryPackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("VectorsSerialize")]
    public int MessagePackSerializeVectors()
    {
        _messagePackBuffer.ResetWrittenCount();
        MessagePackSerializer.Serialize(_messagePackBuffer, Vectors);
        return _messagePackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("VectorsSerialize")]
    public int ProtobufSerializeVectors()
    {
        _protobufStream.SetLength(0);
        Serializer.Serialize(_protobufStream, Vectors);
        return (int)_protobufStream.Length;
    }

    [Benchmark, BenchmarkCategory("VectorsSerialize")]
    public int GoogleProtobufSerializeVectors()
    {
        _googleProtobufBuffer.ResetWrittenCount();
        VectorsProto.WriteTo(_googleProtobufBuffer);
        return _googleProtobufBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("VectorsSerialize")]
    public int JsonSerializeVectors()
    {
        _jsonBuffer.ResetWrittenCount();
        _jsonWriter.Reset(_jsonBuffer);
        JsonSerializer.Serialize(_jsonWriter, Vectors, CaseJson.Default.Vector4Array);
        return _jsonBuffer.WrittenCount;
    }

    [Benchmark(Baseline = true), BenchmarkCategory("SimpleClassDeserialize")]
    public SimpleClass NinoDeserializeSimpleClass()
    {
        return NinoDeserializer.Deserialize<SimpleClass>(SerializedSimpleClass[2]);
    }

    [Benchmark, BenchmarkCategory("SimpleClassDeserialize")]
    public SimpleClass MemoryPackDeserializeSimpleClass()
    {
        return MemoryPackSerializer.Deserialize<SimpleClass>(SerializedSimpleClass[1]);
    }

    [Benchmark, BenchmarkCategory("SimpleClassDeserialize")]
    public SimpleClass MessagePackDeserializeSimpleClass()
    {
        return MessagePackSerializer.Deserialize<SimpleClass>(SerializedSimpleClass[0]);
    }

    [Benchmark, BenchmarkCategory("SimpleClassDeserialize")]
    public SimpleClass ProtobufDeserializeSimpleClass()
    {
        return Serializer.Deserialize<SimpleClass>(new ReadOnlySpan<byte>(SerializedSimpleClass[3]));
    }

    [Benchmark, BenchmarkCategory("SimpleClassDeserialize")]
    public P.SimpleClassBase GoogleProtobufDeserializeSimpleClass()
    {
        return P.SimpleClassBase.Parser.ParseFrom(new ReadOnlySpan<byte>(SerializedSimpleClass[4]));
    }

    [Benchmark, BenchmarkCategory("SimpleClassDeserialize")]
    public SimpleClass JsonDeserializeSimpleClass()
    {
        return JsonSerializer.Deserialize(SerializedSimpleClass[5], CaseJson.Default.SimpleClass);
    }

    [Benchmark(Baseline = true), BenchmarkCategory("SimpleStructDeserialize")]
    public SimpleStruct NinoDeserializeSimpleStruct()
    {
        return NinoDeserializer.Deserialize<SimpleStruct>(SerializedSimpleStruct[2]);
    }

    [Benchmark, BenchmarkCategory("SimpleStructDeserialize")]
    public SimpleStruct MemoryPackDeserializeSimpleStruct()
    {
        return MemoryPackSerializer.Deserialize<SimpleStruct>(SerializedSimpleStruct[1]);
    }

    [Benchmark, BenchmarkCategory("SimpleStructDeserialize")]
    public SimpleStruct MessagePackDeserializeSimpleStruct()
    {
        return MessagePackSerializer.Deserialize<SimpleStruct>(SerializedSimpleStruct[0]);
    }

    [Benchmark, BenchmarkCategory("SimpleStructDeserialize")]
    public SimpleStruct ProtobufDeserializeSimpleStruct()
    {
        return Serializer.Deserialize<SimpleStruct>(new ReadOnlySpan<byte>(SerializedSimpleStruct[3]));
    }

    [Benchmark, BenchmarkCategory("SimpleStructDeserialize")]
    public P.SimpleStruct GoogleProtobufDeserializeSimpleStruct()
    {
        return P.SimpleStruct.Parser.ParseFrom(new ReadOnlySpan<byte>(SerializedSimpleStruct[4]));
    }

    [Benchmark, BenchmarkCategory("SimpleStructDeserialize")]
    public SimpleStruct JsonDeserializeSimpleStruct()
    {
        return JsonSerializer.Deserialize(SerializedSimpleStruct[5], CaseJson.Default.SimpleStruct);
    }

    [Benchmark(Baseline = true), BenchmarkCategory("SimpleClassesDeserialize")]
    public SimpleClass[] NinoDeserializeSimpleClasses()
    {
        return NinoDeserializer.Deserialize<SimpleClass[]>(SerializedSimpleClasses[2]);
    }

    [Benchmark, BenchmarkCategory("SimpleClassesDeserialize")]
    public SimpleClass[] MemoryPackDeserializeSimpleClasses()
    {
        return MemoryPackSerializer.Deserialize<SimpleClass[]>(SerializedSimpleClasses[1]);
    }

    [Benchmark, BenchmarkCategory("SimpleClassesDeserialize")]
    public SimpleClass[] MessagePackDeserializeSimpleClasses()
    {
        return MessagePackSerializer.Deserialize<SimpleClass[]>(SerializedSimpleClasses[0]);
    }

    [Benchmark, BenchmarkCategory("SimpleClassesDeserialize")]
    public SimpleClass[] ProtobufDeserializeSimpleClasses()
    {
        return Serializer.Deserialize<SimpleClass[]>(new ReadOnlySpan<byte>(SerializedSimpleClasses[3]));
    }

    [Benchmark, BenchmarkCategory("SimpleClassesDeserialize")]
    public P.SimpleClassList GoogleProtobufDeserializeSimpleClasses()
    {
        return P.SimpleClassList.Parser.ParseFrom(new ReadOnlySpan<byte>(SerializedSimpleClasses[4]));
    }

    [Benchmark, BenchmarkCategory("SimpleClassesDeserialize")]
    public SimpleClass[] JsonDeserializeSimpleClasses()
    {
        return JsonSerializer.Deserialize(SerializedSimpleClasses[5], CaseJson.Default.SimpleClassArray);
    }

    [Benchmark(Baseline = true), BenchmarkCategory("SimpleStructsDeserialize")]
    public SimpleStruct[] NinoDeserializeSimpleStructs()
    {
        return NinoDeserializer.Deserialize<SimpleStruct[]>(SerializedSimpleStructs[2]);
    }

    [Benchmark, BenchmarkCategory("SimpleStructsDeserialize")]
    public SimpleStruct[] MemoryPackDeserializeSimpleStructs()
    {
        return MemoryPackSerializer.Deserialize<SimpleStruct[]>(SerializedSimpleStructs[1]);
    }

    [Benchmark, BenchmarkCategory("SimpleStructsDeserialize")]
    public SimpleStruct[] MessagePackDeserializeSimpleStructs()
    {
        return MessagePackSerializer.Deserialize<SimpleStruct[]>(SerializedSimpleStructs[0]);
    }

    [Benchmark, BenchmarkCategory("SimpleStructsDeserialize")]
    public SimpleStruct[] ProtobufDeserializeSimpleStructs()
    {
        return Serializer.Deserialize<SimpleStruct[]>(new ReadOnlySpan<byte>(SerializedSimpleStructs[3]));
    }

    [Benchmark, BenchmarkCategory("SimpleStructsDeserialize")]
    public P.SimpleStructList GoogleProtobufDeserializeSimpleStructs()
    {
        return P.SimpleStructList.Parser.ParseFrom(new ReadOnlySpan<byte>(SerializedSimpleStructs[4]));
    }

    [Benchmark, BenchmarkCategory("SimpleStructsDeserialize")]
    public SimpleStruct[] JsonDeserializeSimpleStructs()
    {
        return JsonSerializer.Deserialize(SerializedSimpleStructs[5], CaseJson.Default.SimpleStructArray);
    }

    [Benchmark(Baseline = true), BenchmarkCategory("VectorsDeserialize")]
    public Vector4[] NinoDeserializeVectors()
    {
        return NinoDeserializer.Deserialize<Vector4[]>(SerializedVectors[2]);
    }

    [Benchmark, BenchmarkCategory("VectorsDeserialize")]
    public Vector4[] MemoryPackDeserializeVectors()
    {
        return MemoryPackSerializer.Deserialize<Vector4[]>(SerializedVectors[1]);
    }

    [Benchmark, BenchmarkCategory("VectorsDeserialize")]
    public Vector4[] MessagePackDeserializeVectors()
    {
        return MessagePackSerializer.Deserialize<Vector4[]>(SerializedVectors[0]);
    }

    [Benchmark, BenchmarkCategory("VectorsDeserialize")]
    public Vector4[] ProtobufDeserializeVectors()
    {
        return Serializer.Deserialize<Vector4[]>(new ReadOnlySpan<byte>(SerializedVectors[3]));
    }

    [Benchmark, BenchmarkCategory("VectorsDeserialize")]
    public P.VectorList GoogleProtobufDeserializeVectors()
    {
        return P.VectorList.Parser.ParseFrom(new ReadOnlySpan<byte>(SerializedVectors[4]));
    }

    [Benchmark, BenchmarkCategory("VectorsDeserialize")]
    public Vector4[] JsonDeserializeVectors()
    {
        return JsonSerializer.Deserialize(SerializedVectors[5], CaseJson.Default.Vector4Array);
    }
}
