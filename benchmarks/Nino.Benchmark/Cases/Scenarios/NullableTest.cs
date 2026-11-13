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
/// Nullable values against MemoryPack, MessagePack, protobuf-net, Google.Protobuf (the messages cases.proto has for the
/// contracts in CaseFormats.cs, parsed from protobuf-net's bytes outside the timed region) and System.Text.Json (source
/// generated, UTF-8):
///  - nullable fields: a class of eight nullable members of common types (<c>int?</c>, <c>long?</c>, <c>double?</c>,
///    <c>bool?</c>, <c>DateTime?</c>, <c>Guid?</c>, <c>decimal?</c>, an enum), half of them null
///    (<see cref="NullableFields"/>), alone and in an array of 100 whose objects alternate which half is null;
///  - nullable values on their own: one <c>SimpleStruct?</c> that holds a value, <c>SimpleStruct?[100]</c> and
///    <c>SimpleClass[100]</c> with every fifth element null, and one <c>SimpleClass</c> reference that is null (SimpleTest's
///    SimpleClass case is its counterpart with a value).
/// MemoryPack writes the nullable structs as <c>SafeNullable&lt;T&gt;</c> (<see cref="NullableFieldsMemoryPack"/> says
/// why), copied outside the timed region; protobuf a null as an absent message field (CaseFormats.cs). Each
/// <c>Serialized*</c> array holds the bytes of MessagePack, MemoryPack, Nino, protobuf-net, Google.Protobuf and
/// System.Text.Json, in that order.
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
    private static readonly NullableFields Fields;
    private static readonly NullableFields[] FieldsArray;
    private static readonly NullableFieldsMemoryPack FieldsMemoryPack;
    private static readonly NullableFieldsMemoryPack[] FieldsArrayMemoryPack;
    private static readonly SimpleStruct? Struct;
    private static readonly SimpleStruct?[] Structs;
    private static readonly SimpleClass NullObject;
    private static readonly SimpleClass[] Objects;
    private static readonly SafeNullable<SimpleStruct> StructMemoryPack;
    private static readonly SafeNullable<SimpleStruct>[] StructsMemoryPack;
    private static readonly OptionalSimpleStruct StructProtobuf;
    private static readonly OptionalSimpleStructList StructsProtobuf;
    private static readonly OptionalSimpleClass NullObjectProtobuf;
    private static readonly NullableObjectList ObjectsProtobuf;

    private NinoArrayBufferWriter _ninoBuffer;
    private ArrayBufferWriter<byte> _memoryPackBuffer;
    private ArrayBufferWriter<byte> _messagePackBuffer;
    private MemoryStream _protobufStream;
    private ArrayBufferWriter<byte> _googleProtobufBuffer;
    private ArrayBufferWriter<byte> _jsonBuffer;
    private Utf8JsonWriter _jsonWriter;

    private static readonly byte[][] SerializedNullableFields;
    private static readonly P.NullableFields FieldsProto;
    private static readonly byte[][] SerializedNullableFieldsArray;
    private static readonly P.NullableFieldsList FieldsArrayProto;
    private static readonly byte[][] SerializedNullableStruct;
    private static readonly P.OptionalSimpleStruct StructProto;
    private static readonly byte[][] SerializedNullableStructs;
    private static readonly P.OptionalSimpleStructList StructsProto;
    private static readonly byte[][] SerializedNullableObject;
    private static readonly P.OptionalSimpleClass NullObjectProto;
    private static readonly byte[][] SerializedNullableObjects;
    private static readonly P.NullableObjectList ObjectsProto;

    static NullableTest()
    {
        var random = new Random(20240607);
        Fields = NullableFields.Create(0, random);
        FieldsArray = Enumerable.Range(0, 100).Select(i => NullableFields.Create(i, random)).ToArray();
        FieldsMemoryPack = NullableFieldsMemoryPack.From(Fields);
        FieldsArrayMemoryPack = SafeNullables.From(FieldsArray);
        // Every fifth element null, as CollectionsTest's nullable collections.
        Struct = SimpleStruct.Create();
        Structs = Enumerable.Range(0, 100).Select(i => i % 5 == 0 ? null : (SimpleStruct?)SimpleStruct.Create()).ToArray();
        Objects = Enumerable.Range(0, 100).Select(i => i % 5 == 0 ? null : SimpleClass.Create()).ToArray();
        StructMemoryPack = Struct;
        StructsMemoryPack = SafeNullables.From(Structs);
        StructProtobuf = new OptionalSimpleStruct { Value = Struct };
        StructsProtobuf = new OptionalSimpleStructList { Items = OptionalSimpleStruct.From(Structs) };
        NullObject = null;
        NullObjectProtobuf = new OptionalSimpleClass { Value = NullObject };
        ObjectsProtobuf = new NullableObjectList { Items = Objects };

        SerializedNullableFields = new byte[6][];
        FieldsProto = P.NullableFields.Parser.ParseFrom(ProtobufModel.Serialize(Fields));
        SerializedNullableFields[0] = MessagePackSerializer.Serialize(Fields);
        SerializedNullableFields[1] = MemoryPackSerializer.Serialize(FieldsMemoryPack);
        SerializedNullableFields[2] = NinoSerializer.Serialize(Fields);
        SerializedNullableFields[3] = ProtobufModel.Serialize(Fields);
        SerializedNullableFields[4] = FieldsProto.ToByteArray();
        SerializedNullableFields[5] = JsonSerializer.SerializeToUtf8Bytes(Fields, CaseJson.Default.NullableFields);

        SerializedNullableFieldsArray = new byte[6][];
        FieldsArrayProto = P.NullableFieldsList.Parser.ParseFrom(ProtobufModel.Serialize(FieldsArray));
        SerializedNullableFieldsArray[0] = MessagePackSerializer.Serialize(FieldsArray);
        SerializedNullableFieldsArray[1] = MemoryPackSerializer.Serialize(FieldsArrayMemoryPack);
        SerializedNullableFieldsArray[2] = NinoSerializer.Serialize(FieldsArray);
        SerializedNullableFieldsArray[3] = ProtobufModel.Serialize(FieldsArray);
        SerializedNullableFieldsArray[4] = FieldsArrayProto.ToByteArray();
        SerializedNullableFieldsArray[5] = JsonSerializer.SerializeToUtf8Bytes(FieldsArray, CaseJson.Default.NullableFieldsArray);

        SerializedNullableStruct = new byte[6][];
        StructProto = P.OptionalSimpleStruct.Parser.ParseFrom(ProtobufModel.Serialize(StructProtobuf));
        SerializedNullableStruct[0] = MessagePackSerializer.Serialize(Struct);
        SerializedNullableStruct[1] = MemoryPackSerializer.Serialize(StructMemoryPack);
        SerializedNullableStruct[2] = NinoSerializer.Serialize(Struct);
        SerializedNullableStruct[3] = ProtobufModel.Serialize(StructProtobuf);
        SerializedNullableStruct[4] = StructProto.ToByteArray();
        SerializedNullableStruct[5] = JsonSerializer.SerializeToUtf8Bytes(Struct, CaseJson.Default.NullableSimpleStruct);

        SerializedNullableStructs = new byte[6][];
        StructsProto = P.OptionalSimpleStructList.Parser.ParseFrom(ProtobufModel.Serialize(StructsProtobuf));
        SerializedNullableStructs[0] = MessagePackSerializer.Serialize(Structs);
        SerializedNullableStructs[1] = MemoryPackSerializer.Serialize(StructsMemoryPack);
        SerializedNullableStructs[2] = NinoSerializer.Serialize(Structs);
        SerializedNullableStructs[3] = ProtobufModel.Serialize(StructsProtobuf);
        SerializedNullableStructs[4] = StructsProto.ToByteArray();
        SerializedNullableStructs[5] = JsonSerializer.SerializeToUtf8Bytes(Structs, CaseJson.Default.NullableSimpleStructArray);

        SerializedNullableObject = new byte[6][];
        NullObjectProto = P.OptionalSimpleClass.Parser.ParseFrom(ProtobufModel.Serialize(NullObjectProtobuf));
        SerializedNullableObject[0] = MessagePackSerializer.Serialize(NullObject);
        SerializedNullableObject[1] = MemoryPackSerializer.Serialize(NullObject);
        SerializedNullableObject[2] = NinoSerializer.Serialize(NullObject);
        SerializedNullableObject[3] = ProtobufModel.Serialize(NullObjectProtobuf);
        SerializedNullableObject[4] = NullObjectProto.ToByteArray();
        SerializedNullableObject[5] = JsonSerializer.SerializeToUtf8Bytes(NullObject, CaseJson.Default.SimpleClass);

        SerializedNullableObjects = new byte[6][];
        ObjectsProto = P.NullableObjectList.Parser.ParseFrom(ProtobufModel.Serialize(ObjectsProtobuf));
        SerializedNullableObjects[0] = MessagePackSerializer.Serialize(Objects);
        SerializedNullableObjects[1] = MemoryPackSerializer.Serialize(Objects);
        SerializedNullableObjects[2] = NinoSerializer.Serialize(Objects);
        SerializedNullableObjects[3] = ProtobufModel.Serialize(ObjectsProtobuf);
        SerializedNullableObjects[4] = ObjectsProto.ToByteArray();
        SerializedNullableObjects[5] = JsonSerializer.SerializeToUtf8Bytes(Objects, CaseJson.Default.SimpleClassArray);

        string[] libraries = ["MessagePack", "MemoryPack", "Nino", "Protobuf", "GoogleProtobuf", "Json"];
        for (int i = 0; i < libraries.Length; i++)
        {
            BenchmarkPayloadRegistry.Register($"{libraries[i]}SerializeNullableFields", SerializedNullableFields[i].Length);
            BenchmarkPayloadRegistry.Register($"{libraries[i]}SerializeNullableFieldsArray", SerializedNullableFieldsArray[i].Length);
            BenchmarkPayloadRegistry.Register($"{libraries[i]}SerializeNullableStruct", SerializedNullableStruct[i].Length);
            BenchmarkPayloadRegistry.Register($"{libraries[i]}SerializeNullableStructs", SerializedNullableStructs[i].Length);
            BenchmarkPayloadRegistry.Register($"{libraries[i]}SerializeNullableObject", SerializedNullableObject[i].Length);
            BenchmarkPayloadRegistry.Register($"{libraries[i]}SerializeNullableObjects", SerializedNullableObjects[i].Length);
        }
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

    /// <summary>
    /// Verify.cs: MemoryPack's SafeNullable data, protobuf-net's OptionalSimpleStruct array and Google.Protobuf's messages
    /// as the case's nullable values.
    /// </summary>
    public object ReadBack(object result, Type model) => result switch
    {
        P.OptionalSimpleStruct => ((OptionalSimpleStruct)ProtobufModel.ReadBack(result, typeof(OptionalSimpleStruct))).Value,
        P.OptionalSimpleStructList => OptionalSimpleStruct.ToNullable(
            ((OptionalSimpleStructList)ProtobufModel.ReadBack(result, typeof(OptionalSimpleStructList))).Items),
        P.OptionalSimpleClass => ((OptionalSimpleClass)ProtobufModel.ReadBack(result, typeof(OptionalSimpleClass))).Value,
        P.NullableObjectList => ((NullableObjectList)ProtobufModel.ReadBack(result, typeof(NullableObjectList))).Items,
        OptionalSimpleStruct[] items => OptionalSimpleStruct.ToNullable(items),
        _ => ProtobufModel.ReadBack(SafeNullables.ReadBack(result), model),
    };

    [Benchmark(Baseline = true), BenchmarkCategory("NullableFieldsSerialize")]
    public int NinoSerializeNullableFields()
    {
        _ninoBuffer.ResetWrittenCount();
        NinoSerializer.Serialize(Fields, _ninoBuffer);
        return _ninoBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("NullableFieldsSerialize")]
    public int MemoryPackSerializeNullableFields()
    {
        _memoryPackBuffer.ResetWrittenCount();
        MemoryPackSerializer.Serialize(_memoryPackBuffer, FieldsMemoryPack);
        return _memoryPackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("NullableFieldsSerialize")]
    public int MessagePackSerializeNullableFields()
    {
        _messagePackBuffer.ResetWrittenCount();
        MessagePackSerializer.Serialize(_messagePackBuffer, Fields);
        return _messagePackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("NullableFieldsSerialize")]
    public int ProtobufSerializeNullableFields()
    {
        _protobufStream.SetLength(0);
        Serializer.Serialize(_protobufStream, Fields);
        return (int)_protobufStream.Length;
    }

    [Benchmark, BenchmarkCategory("NullableFieldsSerialize")]
    public int GoogleProtobufSerializeNullableFields()
    {
        _googleProtobufBuffer.ResetWrittenCount();
        FieldsProto.WriteTo(_googleProtobufBuffer);
        return _googleProtobufBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("NullableFieldsSerialize")]
    public int JsonSerializeNullableFields()
    {
        _jsonBuffer.ResetWrittenCount();
        _jsonWriter.Reset(_jsonBuffer);
        JsonSerializer.Serialize(_jsonWriter, Fields, CaseJson.Default.NullableFields);
        return _jsonBuffer.WrittenCount;
    }

    [Benchmark(Baseline = true), BenchmarkCategory("NullableFieldsArraySerialize")]
    public int NinoSerializeNullableFieldsArray()
    {
        _ninoBuffer.ResetWrittenCount();
        NinoSerializer.Serialize(FieldsArray, _ninoBuffer);
        return _ninoBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("NullableFieldsArraySerialize")]
    public int MemoryPackSerializeNullableFieldsArray()
    {
        _memoryPackBuffer.ResetWrittenCount();
        MemoryPackSerializer.Serialize(_memoryPackBuffer, FieldsArrayMemoryPack);
        return _memoryPackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("NullableFieldsArraySerialize")]
    public int MessagePackSerializeNullableFieldsArray()
    {
        _messagePackBuffer.ResetWrittenCount();
        MessagePackSerializer.Serialize(_messagePackBuffer, FieldsArray);
        return _messagePackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("NullableFieldsArraySerialize")]
    public int ProtobufSerializeNullableFieldsArray()
    {
        _protobufStream.SetLength(0);
        Serializer.Serialize(_protobufStream, FieldsArray);
        return (int)_protobufStream.Length;
    }

    [Benchmark, BenchmarkCategory("NullableFieldsArraySerialize")]
    public int GoogleProtobufSerializeNullableFieldsArray()
    {
        _googleProtobufBuffer.ResetWrittenCount();
        FieldsArrayProto.WriteTo(_googleProtobufBuffer);
        return _googleProtobufBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("NullableFieldsArraySerialize")]
    public int JsonSerializeNullableFieldsArray()
    {
        _jsonBuffer.ResetWrittenCount();
        _jsonWriter.Reset(_jsonBuffer);
        JsonSerializer.Serialize(_jsonWriter, FieldsArray, CaseJson.Default.NullableFieldsArray);
        return _jsonBuffer.WrittenCount;
    }

    [Benchmark(Baseline = true), BenchmarkCategory("NullableStructSerialize")]
    public int NinoSerializeNullableStruct()
    {
        _ninoBuffer.ResetWrittenCount();
        NinoSerializer.Serialize(Struct, _ninoBuffer);
        return _ninoBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("NullableStructSerialize")]
    public int MemoryPackSerializeNullableStruct()
    {
        _memoryPackBuffer.ResetWrittenCount();
        MemoryPackSerializer.Serialize(_memoryPackBuffer, StructMemoryPack);
        return _memoryPackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("NullableStructSerialize")]
    public int MessagePackSerializeNullableStruct()
    {
        _messagePackBuffer.ResetWrittenCount();
        MessagePackSerializer.Serialize(_messagePackBuffer, Struct);
        return _messagePackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("NullableStructSerialize")]
    public int ProtobufSerializeNullableStruct()
    {
        _protobufStream.SetLength(0);
        Serializer.Serialize(_protobufStream, StructProtobuf);
        return (int)_protobufStream.Length;
    }

    [Benchmark, BenchmarkCategory("NullableStructSerialize")]
    public int GoogleProtobufSerializeNullableStruct()
    {
        _googleProtobufBuffer.ResetWrittenCount();
        StructProto.WriteTo(_googleProtobufBuffer);
        return _googleProtobufBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("NullableStructSerialize")]
    public int JsonSerializeNullableStruct()
    {
        _jsonBuffer.ResetWrittenCount();
        _jsonWriter.Reset(_jsonBuffer);
        JsonSerializer.Serialize(_jsonWriter, Struct, CaseJson.Default.NullableSimpleStruct);
        return _jsonBuffer.WrittenCount;
    }

    [Benchmark(Baseline = true), BenchmarkCategory("NullableStructsSerialize")]
    public int NinoSerializeNullableStructs()
    {
        _ninoBuffer.ResetWrittenCount();
        NinoSerializer.Serialize(Structs, _ninoBuffer);
        return _ninoBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("NullableStructsSerialize")]
    public int MemoryPackSerializeNullableStructs()
    {
        _memoryPackBuffer.ResetWrittenCount();
        MemoryPackSerializer.Serialize(_memoryPackBuffer, StructsMemoryPack);
        return _memoryPackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("NullableStructsSerialize")]
    public int MessagePackSerializeNullableStructs()
    {
        _messagePackBuffer.ResetWrittenCount();
        MessagePackSerializer.Serialize(_messagePackBuffer, Structs);
        return _messagePackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("NullableStructsSerialize")]
    public int ProtobufSerializeNullableStructs()
    {
        _protobufStream.SetLength(0);
        Serializer.Serialize(_protobufStream, StructsProtobuf);
        return (int)_protobufStream.Length;
    }

    [Benchmark, BenchmarkCategory("NullableStructsSerialize")]
    public int GoogleProtobufSerializeNullableStructs()
    {
        _googleProtobufBuffer.ResetWrittenCount();
        StructsProto.WriteTo(_googleProtobufBuffer);
        return _googleProtobufBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("NullableStructsSerialize")]
    public int JsonSerializeNullableStructs()
    {
        _jsonBuffer.ResetWrittenCount();
        _jsonWriter.Reset(_jsonBuffer);
        JsonSerializer.Serialize(_jsonWriter, Structs, CaseJson.Default.NullableSimpleStructArray);
        return _jsonBuffer.WrittenCount;
    }

    [Benchmark(Baseline = true), BenchmarkCategory("NullableObjectSerialize")]
    public int NinoSerializeNullableObject()
    {
        _ninoBuffer.ResetWrittenCount();
        NinoSerializer.Serialize(NullObject, _ninoBuffer);
        return _ninoBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("NullableObjectSerialize")]
    public int MemoryPackSerializeNullableObject()
    {
        _memoryPackBuffer.ResetWrittenCount();
        MemoryPackSerializer.Serialize(_memoryPackBuffer, NullObject);
        return _memoryPackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("NullableObjectSerialize")]
    public int MessagePackSerializeNullableObject()
    {
        _messagePackBuffer.ResetWrittenCount();
        MessagePackSerializer.Serialize(_messagePackBuffer, NullObject);
        return _messagePackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("NullableObjectSerialize")]
    public int ProtobufSerializeNullableObject()
    {
        _protobufStream.SetLength(0);
        Serializer.Serialize(_protobufStream, NullObjectProtobuf);
        return (int)_protobufStream.Length;
    }

    [Benchmark, BenchmarkCategory("NullableObjectSerialize")]
    public int GoogleProtobufSerializeNullableObject()
    {
        _googleProtobufBuffer.ResetWrittenCount();
        NullObjectProto.WriteTo(_googleProtobufBuffer);
        return _googleProtobufBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("NullableObjectSerialize")]
    public int JsonSerializeNullableObject()
    {
        _jsonBuffer.ResetWrittenCount();
        _jsonWriter.Reset(_jsonBuffer);
        JsonSerializer.Serialize(_jsonWriter, NullObject, CaseJson.Default.SimpleClass);
        return _jsonBuffer.WrittenCount;
    }

    [Benchmark(Baseline = true), BenchmarkCategory("NullableObjectsSerialize")]
    public int NinoSerializeNullableObjects()
    {
        _ninoBuffer.ResetWrittenCount();
        NinoSerializer.Serialize(Objects, _ninoBuffer);
        return _ninoBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("NullableObjectsSerialize")]
    public int MemoryPackSerializeNullableObjects()
    {
        _memoryPackBuffer.ResetWrittenCount();
        MemoryPackSerializer.Serialize(_memoryPackBuffer, Objects);
        return _memoryPackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("NullableObjectsSerialize")]
    public int MessagePackSerializeNullableObjects()
    {
        _messagePackBuffer.ResetWrittenCount();
        MessagePackSerializer.Serialize(_messagePackBuffer, Objects);
        return _messagePackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("NullableObjectsSerialize")]
    public int ProtobufSerializeNullableObjects()
    {
        _protobufStream.SetLength(0);
        Serializer.Serialize(_protobufStream, ObjectsProtobuf);
        return (int)_protobufStream.Length;
    }

    [Benchmark, BenchmarkCategory("NullableObjectsSerialize")]
    public int GoogleProtobufSerializeNullableObjects()
    {
        _googleProtobufBuffer.ResetWrittenCount();
        ObjectsProto.WriteTo(_googleProtobufBuffer);
        return _googleProtobufBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("NullableObjectsSerialize")]
    public int JsonSerializeNullableObjects()
    {
        _jsonBuffer.ResetWrittenCount();
        _jsonWriter.Reset(_jsonBuffer);
        JsonSerializer.Serialize(_jsonWriter, Objects, CaseJson.Default.SimpleClassArray);
        return _jsonBuffer.WrittenCount;
    }

    [Benchmark(Baseline = true), BenchmarkCategory("NullableFieldsDeserialize")]
    public NullableFields NinoDeserializeNullableFields() =>
        NinoDeserializer.Deserialize<NullableFields>(SerializedNullableFields[2]);

    [Benchmark, BenchmarkCategory("NullableFieldsDeserialize")]
    public NullableFieldsMemoryPack MemoryPackDeserializeNullableFields() =>
        MemoryPackSerializer.Deserialize<NullableFieldsMemoryPack>(SerializedNullableFields[1]);

    [Benchmark, BenchmarkCategory("NullableFieldsDeserialize")]
    public NullableFields MessagePackDeserializeNullableFields() =>
        MessagePackSerializer.Deserialize<NullableFields>(SerializedNullableFields[0]);

    [Benchmark, BenchmarkCategory("NullableFieldsDeserialize")]
    public NullableFields ProtobufDeserializeNullableFields() =>
        Serializer.Deserialize<NullableFields>(new ReadOnlySpan<byte>(SerializedNullableFields[3]));

    [Benchmark, BenchmarkCategory("NullableFieldsDeserialize")]
    public P.NullableFields GoogleProtobufDeserializeNullableFields() =>
        P.NullableFields.Parser.ParseFrom(new ReadOnlySpan<byte>(SerializedNullableFields[4]));

    [Benchmark, BenchmarkCategory("NullableFieldsDeserialize")]
    public NullableFields JsonDeserializeNullableFields() =>
        JsonSerializer.Deserialize(SerializedNullableFields[5], CaseJson.Default.NullableFields);

    [Benchmark(Baseline = true), BenchmarkCategory("NullableFieldsArrayDeserialize")]
    public NullableFields[] NinoDeserializeNullableFieldsArray() =>
        NinoDeserializer.Deserialize<NullableFields[]>(SerializedNullableFieldsArray[2]);

    [Benchmark, BenchmarkCategory("NullableFieldsArrayDeserialize")]
    public NullableFieldsMemoryPack[] MemoryPackDeserializeNullableFieldsArray() =>
        MemoryPackSerializer.Deserialize<NullableFieldsMemoryPack[]>(SerializedNullableFieldsArray[1]);

    [Benchmark, BenchmarkCategory("NullableFieldsArrayDeserialize")]
    public NullableFields[] MessagePackDeserializeNullableFieldsArray() =>
        MessagePackSerializer.Deserialize<NullableFields[]>(SerializedNullableFieldsArray[0]);

    [Benchmark, BenchmarkCategory("NullableFieldsArrayDeserialize")]
    public NullableFields[] ProtobufDeserializeNullableFieldsArray() =>
        Serializer.Deserialize<NullableFields[]>(new ReadOnlySpan<byte>(SerializedNullableFieldsArray[3]));

    [Benchmark, BenchmarkCategory("NullableFieldsArrayDeserialize")]
    public P.NullableFieldsList GoogleProtobufDeserializeNullableFieldsArray() =>
        P.NullableFieldsList.Parser.ParseFrom(new ReadOnlySpan<byte>(SerializedNullableFieldsArray[4]));

    [Benchmark, BenchmarkCategory("NullableFieldsArrayDeserialize")]
    public NullableFields[] JsonDeserializeNullableFieldsArray() =>
        JsonSerializer.Deserialize(SerializedNullableFieldsArray[5], CaseJson.Default.NullableFieldsArray);

    [Benchmark(Baseline = true), BenchmarkCategory("NullableStructDeserialize")]
    public SimpleStruct? NinoDeserializeNullableStruct() =>
        NinoDeserializer.Deserialize<SimpleStruct?>(SerializedNullableStruct[2]);

    [Benchmark, BenchmarkCategory("NullableStructDeserialize")]
    public SafeNullable<SimpleStruct> MemoryPackDeserializeNullableStruct() =>
        MemoryPackSerializer.Deserialize<SafeNullable<SimpleStruct>>(SerializedNullableStruct[1]);

    [Benchmark, BenchmarkCategory("NullableStructDeserialize")]
    public SimpleStruct? MessagePackDeserializeNullableStruct() =>
        MessagePackSerializer.Deserialize<SimpleStruct?>(SerializedNullableStruct[0]);

    [Benchmark, BenchmarkCategory("NullableStructDeserialize")]
    public SimpleStruct? ProtobufDeserializeNullableStruct() =>
        Serializer.Deserialize<OptionalSimpleStruct>(new ReadOnlySpan<byte>(SerializedNullableStruct[3])).Value;

    [Benchmark, BenchmarkCategory("NullableStructDeserialize")]
    public P.OptionalSimpleStruct GoogleProtobufDeserializeNullableStruct() =>
        P.OptionalSimpleStruct.Parser.ParseFrom(new ReadOnlySpan<byte>(SerializedNullableStruct[4]));

    [Benchmark, BenchmarkCategory("NullableStructDeserialize")]
    public SimpleStruct? JsonDeserializeNullableStruct() =>
        JsonSerializer.Deserialize(SerializedNullableStruct[5], CaseJson.Default.NullableSimpleStruct);

    [Benchmark(Baseline = true), BenchmarkCategory("NullableStructsDeserialize")]
    public SimpleStruct?[] NinoDeserializeNullableStructs() =>
        NinoDeserializer.Deserialize<SimpleStruct?[]>(SerializedNullableStructs[2]);

    [Benchmark, BenchmarkCategory("NullableStructsDeserialize")]
    public SafeNullable<SimpleStruct>[] MemoryPackDeserializeNullableStructs() =>
        MemoryPackSerializer.Deserialize<SafeNullable<SimpleStruct>[]>(SerializedNullableStructs[1]);

    [Benchmark, BenchmarkCategory("NullableStructsDeserialize")]
    public SimpleStruct?[] MessagePackDeserializeNullableStructs() =>
        MessagePackSerializer.Deserialize<SimpleStruct?[]>(SerializedNullableStructs[0]);

    [Benchmark, BenchmarkCategory("NullableStructsDeserialize")]
    public OptionalSimpleStruct[] ProtobufDeserializeNullableStructs() =>
        Serializer.Deserialize<OptionalSimpleStructList>(new ReadOnlySpan<byte>(SerializedNullableStructs[3])).Items;

    [Benchmark, BenchmarkCategory("NullableStructsDeserialize")]
    public P.OptionalSimpleStructList GoogleProtobufDeserializeNullableStructs() =>
        P.OptionalSimpleStructList.Parser.ParseFrom(new ReadOnlySpan<byte>(SerializedNullableStructs[4]));

    [Benchmark, BenchmarkCategory("NullableStructsDeserialize")]
    public SimpleStruct?[] JsonDeserializeNullableStructs() =>
        JsonSerializer.Deserialize(SerializedNullableStructs[5], CaseJson.Default.NullableSimpleStructArray);

    [Benchmark(Baseline = true), BenchmarkCategory("NullableObjectDeserialize")]
    [ReadsNull]
    public SimpleClass NinoDeserializeNullableObject() =>
        NinoDeserializer.Deserialize<SimpleClass>(SerializedNullableObject[2]);

    [Benchmark, BenchmarkCategory("NullableObjectDeserialize")]
    public SimpleClass MemoryPackDeserializeNullableObject() =>
        MemoryPackSerializer.Deserialize<SimpleClass>(SerializedNullableObject[1]);

    [Benchmark, BenchmarkCategory("NullableObjectDeserialize")]
    public SimpleClass MessagePackDeserializeNullableObject() =>
        MessagePackSerializer.Deserialize<SimpleClass>(SerializedNullableObject[0]);

    [Benchmark, BenchmarkCategory("NullableObjectDeserialize")]
    public SimpleClass ProtobufDeserializeNullableObject() =>
        Serializer.Deserialize<OptionalSimpleClass>(new ReadOnlySpan<byte>(SerializedNullableObject[3])).Value;

    [Benchmark, BenchmarkCategory("NullableObjectDeserialize")]
    public P.OptionalSimpleClass GoogleProtobufDeserializeNullableObject() =>
        P.OptionalSimpleClass.Parser.ParseFrom(new ReadOnlySpan<byte>(SerializedNullableObject[4]));

    [Benchmark, BenchmarkCategory("NullableObjectDeserialize")]
    public SimpleClass JsonDeserializeNullableObject() =>
        JsonSerializer.Deserialize(SerializedNullableObject[5], CaseJson.Default.SimpleClass);

    [Benchmark(Baseline = true), BenchmarkCategory("NullableObjectsDeserialize")]
    public SimpleClass[] NinoDeserializeNullableObjects() =>
        NinoDeserializer.Deserialize<SimpleClass[]>(SerializedNullableObjects[2]);

    [Benchmark, BenchmarkCategory("NullableObjectsDeserialize")]
    public SimpleClass[] MemoryPackDeserializeNullableObjects() =>
        MemoryPackSerializer.Deserialize<SimpleClass[]>(SerializedNullableObjects[1]);

    [Benchmark, BenchmarkCategory("NullableObjectsDeserialize")]
    public SimpleClass[] MessagePackDeserializeNullableObjects() =>
        MessagePackSerializer.Deserialize<SimpleClass[]>(SerializedNullableObjects[0]);

    [Benchmark, BenchmarkCategory("NullableObjectsDeserialize")]
    public SimpleClass[] ProtobufDeserializeNullableObjects() =>
        Serializer.Deserialize<NullableObjectList>(new ReadOnlySpan<byte>(SerializedNullableObjects[3])).Items;

    [Benchmark, BenchmarkCategory("NullableObjectsDeserialize")]
    public P.NullableObjectList GoogleProtobufDeserializeNullableObjects() =>
        P.NullableObjectList.Parser.ParseFrom(new ReadOnlySpan<byte>(SerializedNullableObjects[4]));

    [Benchmark, BenchmarkCategory("NullableObjectsDeserialize")]
    public SimpleClass[] JsonDeserializeNullableObjects() =>
        JsonSerializer.Deserialize(SerializedNullableObjects[5], CaseJson.Default.SimpleClassArray);
}
