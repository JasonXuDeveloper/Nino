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

namespace Nino.Benchmark;

#nullable disable

/// <summary>
/// Polymorphic data written through its base type, against MemoryPack's and MessagePack's unions, protobuf-net's
/// <c>[ProtoInclude]</c> (a nested message per level of the hierarchy), Google.Protobuf's messages of the same schema
/// (a <c>oneof</c> per level, cases.proto) and System.Text.Json's <c>[JsonDerivedType]</c> (a <c>"$type"</c> property):
/// one class holding 1,000 elements of 20 subtypes (one to nine levels below their base), an array of 1,000 such
/// elements, and an array of 100 classes holding 50 each. Each <c>Serialized*</c> array holds the bytes of MessagePack,
/// MemoryPack, Nino, protobuf-net, Google.Protobuf and System.Text.Json, in that order; Google.Protobuf's messages are
/// parsed from protobuf-net's bytes outside the timed region.
/// </summary>
[PayloadColumn]
[HideColumns("StdDev", "RatioSD", "Error")]
[MinColumn, MaxColumn]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[NinoJob]
[GcServer(true)]
[MemoryDiagnoser]
[MarkdownExporterAttribute.GitHub]
public class PolymorphicTest : IReadBack
{
    private static readonly PolymorphicClassBase PolymorphicInstance;
    private static readonly PolymorphicFooBase[] PolymorphicFooArray;
    private static readonly PolymorphicClassBase[] PolymorphicClassArray;

    private NinoArrayBufferWriter _ninoBuffer;
    private ArrayBufferWriter<byte> _memoryPackBuffer;
    private ArrayBufferWriter<byte> _messagePackBuffer;
    private MemoryStream _protobufStream;
    private ArrayBufferWriter<byte> _googleProtobufBuffer;
    private ArrayBufferWriter<byte> _jsonBuffer;
    private Utf8JsonWriter _jsonWriter;

    private static readonly byte[][] SerializedPolymorphicClass;
    private static readonly P.PolymorphicClassBase PolymorphicClassProto;
    private static readonly byte[][] SerializedPolymorphicFooArray;
    private static readonly P.PolymorphicFooList PolymorphicFooArrayProto;
    private static readonly byte[][] SerializedPolymorphicClassArray;
    private static readonly P.PolymorphicClassList PolymorphicClassArrayProto;

    static PolymorphicTest()
    {
        // One polymorphic class holding 1,000 polymorphic elements.
        PolymorphicInstance = PolymorphicClass.Create();
        var polyClass = (PolymorphicClass)PolymorphicInstance;
        for (int i = 0; i < 1000; i++)
            polyClass.Numbers2.Add(Foo(i));

        // The key polymorphism case: an array of 1,000 elements of the base type, its 20 subtypes in turn, one to
        // nine levels deep.
        PolymorphicFooArray = new PolymorphicFooBase[1000];
        for (int i = 0; i < 1000; i++)
            PolymorphicFooArray[i] = Foo(i);

        // 100 polymorphic classes (one subtype, through the base type), each holding 50 polymorphic elements.
        PolymorphicClassArray = Enumerable.Range(0, 100).Select(_ =>
        {
            var instance = (PolymorphicClassBase)PolymorphicClass.Create();
            var pClass = (PolymorphicClass)instance;
            for (int i = 0; i < 50; i++)
                pClass.Numbers2.Add(Foo(i));
            return instance;
        }).ToArray();

        SerializedPolymorphicClass = new byte[6][];
        PolymorphicClassProto = P.PolymorphicClassBase.Parser.ParseFrom(ProtobufModel.Serialize(PolymorphicInstance));
        SerializedPolymorphicClass[0] = MessagePackSerializer.Serialize(PolymorphicInstance);
        SerializedPolymorphicClass[1] = MemoryPackSerializer.Serialize(PolymorphicInstance);
        SerializedPolymorphicClass[2] = NinoSerializer.Serialize(PolymorphicInstance);
        SerializedPolymorphicClass[3] = ProtobufModel.Serialize(PolymorphicInstance);
        SerializedPolymorphicClass[4] = PolymorphicClassProto.ToByteArray();
        SerializedPolymorphicClass[5] = JsonSerializer.SerializeToUtf8Bytes(PolymorphicInstance, CaseJson.Default.PolymorphicClassBase);

        SerializedPolymorphicFooArray = new byte[6][];
        PolymorphicFooArrayProto = P.PolymorphicFooList.Parser.ParseFrom(ProtobufModel.Serialize(PolymorphicFooArray));
        SerializedPolymorphicFooArray[0] = MessagePackSerializer.Serialize(PolymorphicFooArray);
        SerializedPolymorphicFooArray[1] = MemoryPackSerializer.Serialize(PolymorphicFooArray);
        SerializedPolymorphicFooArray[2] = NinoSerializer.Serialize(PolymorphicFooArray);
        SerializedPolymorphicFooArray[3] = ProtobufModel.Serialize(PolymorphicFooArray);
        SerializedPolymorphicFooArray[4] = PolymorphicFooArrayProto.ToByteArray();
        SerializedPolymorphicFooArray[5] = JsonSerializer.SerializeToUtf8Bytes(PolymorphicFooArray, CaseJson.Default.PolymorphicFooBaseArray);

        SerializedPolymorphicClassArray = new byte[6][];
        PolymorphicClassArrayProto = P.PolymorphicClassList.Parser.ParseFrom(ProtobufModel.Serialize(PolymorphicClassArray));
        SerializedPolymorphicClassArray[0] = MessagePackSerializer.Serialize(PolymorphicClassArray);
        SerializedPolymorphicClassArray[1] = MemoryPackSerializer.Serialize(PolymorphicClassArray);
        SerializedPolymorphicClassArray[2] = NinoSerializer.Serialize(PolymorphicClassArray);
        SerializedPolymorphicClassArray[3] = ProtobufModel.Serialize(PolymorphicClassArray);
        SerializedPolymorphicClassArray[4] = PolymorphicClassArrayProto.ToByteArray();
        SerializedPolymorphicClassArray[5] = JsonSerializer.SerializeToUtf8Bytes(PolymorphicClassArray, CaseJson.Default.PolymorphicClassBaseArray);

        BenchmarkPayloadRegistry.Register(nameof(NinoSerializePolymorphicClass), SerializedPolymorphicClass[2].Length);
        BenchmarkPayloadRegistry.Register(nameof(NinoSerializePolymorphicFooArray), SerializedPolymorphicFooArray[2].Length);
        BenchmarkPayloadRegistry.Register(nameof(NinoSerializePolymorphicClassArray), SerializedPolymorphicClassArray[2].Length);

        BenchmarkPayloadRegistry.Register(nameof(MemoryPackSerializePolymorphicClass), SerializedPolymorphicClass[1].Length);
        BenchmarkPayloadRegistry.Register(nameof(MemoryPackSerializePolymorphicFooArray), SerializedPolymorphicFooArray[1].Length);
        BenchmarkPayloadRegistry.Register(nameof(MemoryPackSerializePolymorphicClassArray), SerializedPolymorphicClassArray[1].Length);

        BenchmarkPayloadRegistry.Register(nameof(MessagePackSerializePolymorphicClass), SerializedPolymorphicClass[0].Length);
        BenchmarkPayloadRegistry.Register(nameof(ProtobufSerializePolymorphicClass), SerializedPolymorphicClass[3].Length);
        BenchmarkPayloadRegistry.Register(nameof(GoogleProtobufSerializePolymorphicClass), SerializedPolymorphicClass[4].Length);
        BenchmarkPayloadRegistry.Register(nameof(JsonSerializePolymorphicClass), SerializedPolymorphicClass[5].Length);
        BenchmarkPayloadRegistry.Register(nameof(MessagePackSerializePolymorphicFooArray), SerializedPolymorphicFooArray[0].Length);
        BenchmarkPayloadRegistry.Register(nameof(ProtobufSerializePolymorphicFooArray), SerializedPolymorphicFooArray[3].Length);
        BenchmarkPayloadRegistry.Register(nameof(GoogleProtobufSerializePolymorphicFooArray), SerializedPolymorphicFooArray[4].Length);
        BenchmarkPayloadRegistry.Register(nameof(JsonSerializePolymorphicFooArray), SerializedPolymorphicFooArray[5].Length);
        BenchmarkPayloadRegistry.Register(nameof(MessagePackSerializePolymorphicClassArray), SerializedPolymorphicClassArray[0].Length);
        BenchmarkPayloadRegistry.Register(nameof(ProtobufSerializePolymorphicClassArray), SerializedPolymorphicClassArray[3].Length);
        BenchmarkPayloadRegistry.Register(nameof(GoogleProtobufSerializePolymorphicClassArray), SerializedPolymorphicClassArray[4].Length);
        BenchmarkPayloadRegistry.Register(nameof(JsonSerializePolymorphicClassArray), SerializedPolymorphicClassArray[5].Length);
    }

    /// <summary>The <paramref name="i"/>th of the 20 subtypes in turn (FooA is one level below the base, FooI to FooZ nine).</summary>
    private static PolymorphicFooBase Foo(int i) => (i % 20) switch
    {
        0 => FooA.Create(),
        1 => FooB.Create(),
        2 => FooC.Create(),
        3 => FooD.Create(),
        4 => FooE.Create(),
        5 => FooF.Create(),
        6 => FooG.Create(),
        7 => FooH.Create(),
        8 => FooI.Create(),
        9 => FooJ.Create(),
        10 => FooK.Create(),
        11 => FooL.Create(),
        12 => FooM.Create(),
        13 => FooN.Create(),
        14 => FooO.Create(),
        15 => FooP.Create(),
        16 => FooQ.Create(),
        17 => FooR.Create(),
        18 => FooS.Create(),
        _ => FooZ.Create(),
    };

    [GlobalSetup]
    public void Setup()
    {
        ProtoFile.Cases.Check();
        _ninoBuffer = new NinoArrayBufferWriter(1024 * 1024);
        _memoryPackBuffer = new ArrayBufferWriter<byte>(1024 * 1024);
        _messagePackBuffer = new ArrayBufferWriter<byte>(1024 * 1024);
        _protobufStream = new MemoryStream(1024 * 1024);
        _googleProtobufBuffer = new ArrayBufferWriter<byte>(1024 * 1024);
        _jsonBuffer = new ArrayBufferWriter<byte>(1024 * 1024);
        // What JsonSerializer's own writers use: no validation of the writer's calls.
        _jsonWriter = new Utf8JsonWriter(_jsonBuffer, new JsonWriterOptions { SkipValidation = true });
    }

    /// <summary>Verify.cs: a Google.Protobuf message read back through protobuf-net as the case's model.</summary>
    public object ReadBack(object result, Type model) => ProtobufModel.ReadBack(result, model);

    [Benchmark(Baseline = true), BenchmarkCategory("PolymorphicClassSerialize")]
    public int NinoSerializePolymorphicClass()
    {
        _ninoBuffer.ResetWrittenCount();
        NinoSerializer.Serialize(PolymorphicInstance, _ninoBuffer);
        return _ninoBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("PolymorphicClassSerialize")]
    public int MemoryPackSerializePolymorphicClass()
    {
        _memoryPackBuffer.ResetWrittenCount();
        MemoryPackSerializer.Serialize(_memoryPackBuffer, PolymorphicInstance);
        return _memoryPackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("PolymorphicClassSerialize")]
    public int MessagePackSerializePolymorphicClass()
    {
        _messagePackBuffer.ResetWrittenCount();
        MessagePackSerializer.Serialize(_messagePackBuffer, PolymorphicInstance);
        return _messagePackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("PolymorphicClassSerialize")]
    public int ProtobufSerializePolymorphicClass()
    {
        _protobufStream.SetLength(0);
        Serializer.Serialize(_protobufStream, PolymorphicInstance);
        return (int)_protobufStream.Length;
    }

    [Benchmark, BenchmarkCategory("PolymorphicClassSerialize")]
    public int GoogleProtobufSerializePolymorphicClass()
    {
        _googleProtobufBuffer.ResetWrittenCount();
        PolymorphicClassProto.WriteTo(_googleProtobufBuffer);
        return _googleProtobufBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("PolymorphicClassSerialize")]
    public int JsonSerializePolymorphicClass()
    {
        _jsonBuffer.ResetWrittenCount();
        _jsonWriter.Reset(_jsonBuffer);
        JsonSerializer.Serialize(_jsonWriter, PolymorphicInstance, CaseJson.Default.PolymorphicClassBase);
        return _jsonBuffer.WrittenCount;
    }

    [Benchmark(Baseline = true), BenchmarkCategory("PolymorphicFooArraySerialize")]
    public int NinoSerializePolymorphicFooArray()
    {
        _ninoBuffer.ResetWrittenCount();
        NinoSerializer.Serialize(PolymorphicFooArray, _ninoBuffer);
        return _ninoBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("PolymorphicFooArraySerialize")]
    public int MemoryPackSerializePolymorphicFooArray()
    {
        _memoryPackBuffer.ResetWrittenCount();
        MemoryPackSerializer.Serialize(_memoryPackBuffer, PolymorphicFooArray);
        return _memoryPackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("PolymorphicFooArraySerialize")]
    public int MessagePackSerializePolymorphicFooArray()
    {
        _messagePackBuffer.ResetWrittenCount();
        MessagePackSerializer.Serialize(_messagePackBuffer, PolymorphicFooArray);
        return _messagePackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("PolymorphicFooArraySerialize")]
    public int ProtobufSerializePolymorphicFooArray()
    {
        _protobufStream.SetLength(0);
        Serializer.Serialize(_protobufStream, PolymorphicFooArray);
        return (int)_protobufStream.Length;
    }

    [Benchmark, BenchmarkCategory("PolymorphicFooArraySerialize")]
    public int GoogleProtobufSerializePolymorphicFooArray()
    {
        _googleProtobufBuffer.ResetWrittenCount();
        PolymorphicFooArrayProto.WriteTo(_googleProtobufBuffer);
        return _googleProtobufBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("PolymorphicFooArraySerialize")]
    public int JsonSerializePolymorphicFooArray()
    {
        _jsonBuffer.ResetWrittenCount();
        _jsonWriter.Reset(_jsonBuffer);
        JsonSerializer.Serialize(_jsonWriter, PolymorphicFooArray, CaseJson.Default.PolymorphicFooBaseArray);
        return _jsonBuffer.WrittenCount;
    }

    [Benchmark(Baseline = true), BenchmarkCategory("PolymorphicClassArraySerialize")]
    public int NinoSerializePolymorphicClassArray()
    {
        _ninoBuffer.ResetWrittenCount();
        NinoSerializer.Serialize(PolymorphicClassArray, _ninoBuffer);
        return _ninoBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("PolymorphicClassArraySerialize")]
    public int MemoryPackSerializePolymorphicClassArray()
    {
        _memoryPackBuffer.ResetWrittenCount();
        MemoryPackSerializer.Serialize(_memoryPackBuffer, PolymorphicClassArray);
        return _memoryPackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("PolymorphicClassArraySerialize")]
    public int MessagePackSerializePolymorphicClassArray()
    {
        _messagePackBuffer.ResetWrittenCount();
        MessagePackSerializer.Serialize(_messagePackBuffer, PolymorphicClassArray);
        return _messagePackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("PolymorphicClassArraySerialize")]
    public int ProtobufSerializePolymorphicClassArray()
    {
        _protobufStream.SetLength(0);
        Serializer.Serialize(_protobufStream, PolymorphicClassArray);
        return (int)_protobufStream.Length;
    }

    [Benchmark, BenchmarkCategory("PolymorphicClassArraySerialize")]
    public int GoogleProtobufSerializePolymorphicClassArray()
    {
        _googleProtobufBuffer.ResetWrittenCount();
        PolymorphicClassArrayProto.WriteTo(_googleProtobufBuffer);
        return _googleProtobufBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("PolymorphicClassArraySerialize")]
    public int JsonSerializePolymorphicClassArray()
    {
        _jsonBuffer.ResetWrittenCount();
        _jsonWriter.Reset(_jsonBuffer);
        JsonSerializer.Serialize(_jsonWriter, PolymorphicClassArray, CaseJson.Default.PolymorphicClassBaseArray);
        return _jsonBuffer.WrittenCount;
    }

    // Deserialization tests
    [Benchmark(Baseline = true), BenchmarkCategory("PolymorphicClassDeserialize")]
    public PolymorphicClassBase NinoDeserializePolymorphicClass()
    {
        return NinoDeserializer.Deserialize<PolymorphicClassBase>(SerializedPolymorphicClass[2]);
    }

    [Benchmark, BenchmarkCategory("PolymorphicClassDeserialize")]
    public PolymorphicClassBase MemoryPackDeserializePolymorphicClass()
    {
        return MemoryPackSerializer.Deserialize<PolymorphicClassBase>(SerializedPolymorphicClass[1]);
    }

    [Benchmark, BenchmarkCategory("PolymorphicClassDeserialize")]
    public PolymorphicClassBase MessagePackDeserializePolymorphicClass()
    {
        return MessagePackSerializer.Deserialize<PolymorphicClassBase>(SerializedPolymorphicClass[0]);
    }

    [Benchmark, BenchmarkCategory("PolymorphicClassDeserialize")]
    public PolymorphicClassBase ProtobufDeserializePolymorphicClass()
    {
        return Serializer.Deserialize<PolymorphicClassBase>(new ReadOnlySpan<byte>(SerializedPolymorphicClass[3]));
    }

    [Benchmark, BenchmarkCategory("PolymorphicClassDeserialize")]
    public P.PolymorphicClassBase GoogleProtobufDeserializePolymorphicClass()
    {
        return P.PolymorphicClassBase.Parser.ParseFrom(new ReadOnlySpan<byte>(SerializedPolymorphicClass[4]));
    }

    [Benchmark, BenchmarkCategory("PolymorphicClassDeserialize")]
    public PolymorphicClassBase JsonDeserializePolymorphicClass()
    {
        return JsonSerializer.Deserialize(SerializedPolymorphicClass[5], CaseJson.Default.PolymorphicClassBase);
    }

    [Benchmark(Baseline = true), BenchmarkCategory("PolymorphicFooArrayDeserialize")]
    public PolymorphicFooBase[] NinoDeserializePolymorphicFooArray()
    {
        return NinoDeserializer.Deserialize<PolymorphicFooBase[]>(SerializedPolymorphicFooArray[2]);
    }

    [Benchmark, BenchmarkCategory("PolymorphicFooArrayDeserialize")]
    public PolymorphicFooBase[] MemoryPackDeserializePolymorphicFooArray()
    {
        return MemoryPackSerializer.Deserialize<PolymorphicFooBase[]>(SerializedPolymorphicFooArray[1]);
    }

    [Benchmark, BenchmarkCategory("PolymorphicFooArrayDeserialize")]
    public PolymorphicFooBase[] MessagePackDeserializePolymorphicFooArray()
    {
        return MessagePackSerializer.Deserialize<PolymorphicFooBase[]>(SerializedPolymorphicFooArray[0]);
    }

    [Benchmark, BenchmarkCategory("PolymorphicFooArrayDeserialize")]
    public PolymorphicFooBase[] ProtobufDeserializePolymorphicFooArray()
    {
        return Serializer.Deserialize<PolymorphicFooBase[]>(new ReadOnlySpan<byte>(SerializedPolymorphicFooArray[3]));
    }

    [Benchmark, BenchmarkCategory("PolymorphicFooArrayDeserialize")]
    public P.PolymorphicFooList GoogleProtobufDeserializePolymorphicFooArray()
    {
        return P.PolymorphicFooList.Parser.ParseFrom(new ReadOnlySpan<byte>(SerializedPolymorphicFooArray[4]));
    }

    [Benchmark, BenchmarkCategory("PolymorphicFooArrayDeserialize")]
    public PolymorphicFooBase[] JsonDeserializePolymorphicFooArray()
    {
        return JsonSerializer.Deserialize(SerializedPolymorphicFooArray[5], CaseJson.Default.PolymorphicFooBaseArray);
    }

    [Benchmark(Baseline = true), BenchmarkCategory("PolymorphicClassArrayDeserialize")]
    public PolymorphicClassBase[] NinoDeserializePolymorphicClassArray()
    {
        return NinoDeserializer.Deserialize<PolymorphicClassBase[]>(SerializedPolymorphicClassArray[2]);
    }

    [Benchmark, BenchmarkCategory("PolymorphicClassArrayDeserialize")]
    public PolymorphicClassBase[] MemoryPackDeserializePolymorphicClassArray()
    {
        return MemoryPackSerializer.Deserialize<PolymorphicClassBase[]>(SerializedPolymorphicClassArray[1]);
    }

    [Benchmark, BenchmarkCategory("PolymorphicClassArrayDeserialize")]
    public PolymorphicClassBase[] MessagePackDeserializePolymorphicClassArray()
    {
        return MessagePackSerializer.Deserialize<PolymorphicClassBase[]>(SerializedPolymorphicClassArray[0]);
    }

    [Benchmark, BenchmarkCategory("PolymorphicClassArrayDeserialize")]
    public PolymorphicClassBase[] ProtobufDeserializePolymorphicClassArray()
    {
        return Serializer.Deserialize<PolymorphicClassBase[]>(new ReadOnlySpan<byte>(SerializedPolymorphicClassArray[3]));
    }

    [Benchmark, BenchmarkCategory("PolymorphicClassArrayDeserialize")]
    public P.PolymorphicClassList GoogleProtobufDeserializePolymorphicClassArray()
    {
        return P.PolymorphicClassList.Parser.ParseFrom(new ReadOnlySpan<byte>(SerializedPolymorphicClassArray[4]));
    }

    [Benchmark, BenchmarkCategory("PolymorphicClassArrayDeserialize")]
    public PolymorphicClassBase[] JsonDeserializePolymorphicClassArray()
    {
        return JsonSerializer.Deserialize(SerializedPolymorphicClassArray[5], CaseJson.Default.PolymorphicClassBaseArray);
    }
}
