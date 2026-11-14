using System;
using System.Buffers;
using System.Linq;
using BenchmarkDotNet.Configs;
using MemoryPack;
using MessagePack;
using Nino.Core;

#nullable disable
namespace Nino.Benchmark;

// NullableTest.cs ported to Nino 3.3.13 (NuGet), for the v3 build of this project (NINO_BENCH_V3=true), with the same
// models (Fixtures/NullableBenchmarkData.cs, Fixtures/SimpleBenchmarkData.cs) and data. Only the Nino calls differ, as
// in V3/SimpleTest.cs: NinoSerializer/NinoDeserializer -> NinoGen.Serializer/NinoGen.Deserializer,
// NinoArrayBufferWriter -> ArrayBufferWriter<byte>; and the protobuf-net, Google.Protobuf and System.Text.Json cases are
// left out (the v3 run times Nino and MemoryPack only), and so are the other nullable forms (NullableTest.NullableForms.cs:
// NinoNullable<T> is v4's, and MemoryPack's other forms compare with it). Everything else is verbatim.

/// <summary>
/// Nullable values: a class of eight nullable members, half of them null, alone and in an array of 100; one
/// <c>SimpleStruct?</c> with a value, <c>SimpleStruct?[100]</c> and <c>SimpleClass[100]</c> with every fifth element null,
/// and one null <c>SimpleClass</c>; a struct that holds a reference (<see cref="ManagedStruct"/>), with a value and
/// nullable, alone and in an array of 100. Against MemoryPack (SafeNullable for the nullable structs) and MessagePack.
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

    // NullableTest.ManagedStruct.cs: a struct that holds a reference, with a value and nullable (MemoryPack and Nino;
    // each array holds MemoryPack's bytes, then Nino's).
    private static readonly ManagedStruct Managed = ManagedStruct.Create(0);
    private static readonly ManagedStruct[] Manageds = ManagedStruct.CreateArray(100);
    private static readonly ManagedStruct? NullableManaged = Managed;
    private static readonly ManagedStruct?[] NullableManageds = ManagedStruct.CreateNullableArray(100);
    private static readonly byte[][] SerializedManagedStruct = [MemoryPackSerializer.Serialize(Managed), NinoGen.Serializer.Serialize(Managed)];
    private static readonly byte[][] SerializedManagedStructs = [MemoryPackSerializer.Serialize(Manageds), NinoGen.Serializer.Serialize(Manageds)];
    private static readonly byte[][] SerializedNullableManagedStruct = [MemoryPackSerializer.Serialize(NullableManaged), NinoGen.Serializer.Serialize(NullableManaged)];
    private static readonly byte[][] SerializedNullableManagedStructs = [MemoryPackSerializer.Serialize(NullableManageds), NinoGen.Serializer.Serialize(NullableManageds)];

    // v3 3.3.13 has no NinoArrayBufferWriter; its IBufferWriter<byte> overload is the reused-buffer path.
    private ArrayBufferWriter<byte> _ninoBuffer;
    private ArrayBufferWriter<byte> _memoryPackBuffer;
    private ArrayBufferWriter<byte> _messagePackBuffer;

    private static readonly byte[][] SerializedNullableFields;
    private static readonly byte[][] SerializedNullableFieldsArray;
    private static readonly byte[][] SerializedNullableStruct;
    private static readonly byte[][] SerializedNullableStructs;
    private static readonly byte[][] SerializedNullableObject;
    private static readonly byte[][] SerializedNullableObjects;

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
        NullObject = null;
        Objects = Enumerable.Range(0, 100).Select(i => i % 5 == 0 ? null : SimpleClass.Create()).ToArray();
        StructMemoryPack = Struct;
        StructsMemoryPack = SafeNullables.From(Structs);

        SerializedNullableFields = [MessagePackSerializer.Serialize(Fields), MemoryPackSerializer.Serialize(FieldsMemoryPack), NinoGen.Serializer.Serialize(Fields)];
        SerializedNullableFieldsArray = [MessagePackSerializer.Serialize(FieldsArray), MemoryPackSerializer.Serialize(FieldsArrayMemoryPack), NinoGen.Serializer.Serialize(FieldsArray)];
        SerializedNullableStruct = [MessagePackSerializer.Serialize(Struct), MemoryPackSerializer.Serialize(StructMemoryPack), NinoGen.Serializer.Serialize(Struct)];
        SerializedNullableStructs = [MessagePackSerializer.Serialize(Structs), MemoryPackSerializer.Serialize(StructsMemoryPack), NinoGen.Serializer.Serialize(Structs)];
        SerializedNullableObject = [MessagePackSerializer.Serialize(NullObject), MemoryPackSerializer.Serialize(NullObject), NinoGen.Serializer.Serialize(NullObject)];
        SerializedNullableObjects = [MessagePackSerializer.Serialize(Objects), MemoryPackSerializer.Serialize(Objects), NinoGen.Serializer.Serialize(Objects)];

        string[] libraries = ["MessagePack", "MemoryPack", "Nino"];
        for (int i = 0; i < libraries.Length; i++)
        {
            BenchmarkPayloadRegistry.Register($"{libraries[i]}SerializeNullableFields", SerializedNullableFields[i].Length);
            BenchmarkPayloadRegistry.Register($"{libraries[i]}SerializeNullableFieldsArray", SerializedNullableFieldsArray[i].Length);
            BenchmarkPayloadRegistry.Register($"{libraries[i]}SerializeNullableStruct", SerializedNullableStruct[i].Length);
            BenchmarkPayloadRegistry.Register($"{libraries[i]}SerializeNullableStructs", SerializedNullableStructs[i].Length);
            BenchmarkPayloadRegistry.Register($"{libraries[i]}SerializeNullableObject", SerializedNullableObject[i].Length);
            BenchmarkPayloadRegistry.Register($"{libraries[i]}SerializeNullableObjects", SerializedNullableObjects[i].Length);
        }

        string[] managed = ["MemoryPack", "Nino"];
        for (int i = 0; i < managed.Length; i++)
        {
            BenchmarkPayloadRegistry.Register($"{managed[i]}SerializeManagedStruct", SerializedManagedStruct[i].Length);
            BenchmarkPayloadRegistry.Register($"{managed[i]}SerializeManagedStructs", SerializedManagedStructs[i].Length);
            BenchmarkPayloadRegistry.Register($"{managed[i]}SerializeNullableManagedStruct", SerializedNullableManagedStruct[i].Length);
            BenchmarkPayloadRegistry.Register($"{managed[i]}SerializeNullableManagedStructs", SerializedNullableManagedStructs[i].Length);
        }
    }

    [GlobalSetup]
    public void Setup()
    {
        _ninoBuffer = new ArrayBufferWriter<byte>(320 * 1024);
        _memoryPackBuffer = new ArrayBufferWriter<byte>(320 * 1024);
        _messagePackBuffer = new ArrayBufferWriter<byte>(320 * 1024);
    }

    /// <summary>Verify.cs: MemoryPack's SafeNullable data as the case's nullable values.</summary>
    public object ReadBack(object result, Type model) => SafeNullables.ReadBack(result);

    [Benchmark(Baseline = true), BenchmarkCategory("NullableFieldsSerialize")]
    public int NinoSerializeNullableFields()
    {
        _ninoBuffer.ResetWrittenCount();
        NinoGen.Serializer.Serialize(Fields, _ninoBuffer);
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

    [Benchmark(Baseline = true), BenchmarkCategory("NullableFieldsArraySerialize")]
    public int NinoSerializeNullableFieldsArray()
    {
        _ninoBuffer.ResetWrittenCount();
        NinoGen.Serializer.Serialize(FieldsArray, _ninoBuffer);
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

    [Benchmark(Baseline = true), BenchmarkCategory("NullableStructSerialize")]
    public int NinoSerializeNullableStruct()
    {
        _ninoBuffer.ResetWrittenCount();
        NinoGen.Serializer.Serialize(Struct, _ninoBuffer);
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

    [Benchmark(Baseline = true), BenchmarkCategory("NullableStructsSerialize")]
    public int NinoSerializeNullableStructs()
    {
        _ninoBuffer.ResetWrittenCount();
        NinoGen.Serializer.Serialize(Structs, _ninoBuffer);
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

    [Benchmark(Baseline = true), BenchmarkCategory("NullableObjectSerialize")]
    public int NinoSerializeNullableObject()
    {
        _ninoBuffer.ResetWrittenCount();
        NinoGen.Serializer.Serialize(NullObject, _ninoBuffer);
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

    [Benchmark(Baseline = true), BenchmarkCategory("NullableObjectsSerialize")]
    public int NinoSerializeNullableObjects()
    {
        _ninoBuffer.ResetWrittenCount();
        NinoGen.Serializer.Serialize(Objects, _ninoBuffer);
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

    [Benchmark(Baseline = true), BenchmarkCategory("NullableFieldsDeserialize")]
    public NullableFields NinoDeserializeNullableFields()
    {
        NinoGen.Deserializer.Deserialize(SerializedNullableFields[2], out NullableFields ret);
        return ret;
    }

    [Benchmark, BenchmarkCategory("NullableFieldsDeserialize")]
    public NullableFieldsMemoryPack MemoryPackDeserializeNullableFields() =>
        MemoryPackSerializer.Deserialize<NullableFieldsMemoryPack>(SerializedNullableFields[1]);

    [Benchmark, BenchmarkCategory("NullableFieldsDeserialize")]
    public NullableFields MessagePackDeserializeNullableFields() =>
        MessagePackSerializer.Deserialize<NullableFields>(SerializedNullableFields[0]);

    [Benchmark(Baseline = true), BenchmarkCategory("NullableFieldsArrayDeserialize")]
    public NullableFields[] NinoDeserializeNullableFieldsArray()
    {
        NinoGen.Deserializer.Deserialize(SerializedNullableFieldsArray[2], out NullableFields[] ret);
        return ret;
    }

    [Benchmark, BenchmarkCategory("NullableFieldsArrayDeserialize")]
    public NullableFieldsMemoryPack[] MemoryPackDeserializeNullableFieldsArray() =>
        MemoryPackSerializer.Deserialize<NullableFieldsMemoryPack[]>(SerializedNullableFieldsArray[1]);

    [Benchmark, BenchmarkCategory("NullableFieldsArrayDeserialize")]
    public NullableFields[] MessagePackDeserializeNullableFieldsArray() =>
        MessagePackSerializer.Deserialize<NullableFields[]>(SerializedNullableFieldsArray[0]);

    [Benchmark(Baseline = true), BenchmarkCategory("NullableStructDeserialize")]
    public SimpleStruct? NinoDeserializeNullableStruct()
    {
        NinoGen.Deserializer.Deserialize(SerializedNullableStruct[2], out SimpleStruct? ret);
        return ret;
    }

    [Benchmark, BenchmarkCategory("NullableStructDeserialize")]
    public SafeNullable<SimpleStruct> MemoryPackDeserializeNullableStruct() =>
        MemoryPackSerializer.Deserialize<SafeNullable<SimpleStruct>>(SerializedNullableStruct[1]);

    [Benchmark, BenchmarkCategory("NullableStructDeserialize")]
    public SimpleStruct? MessagePackDeserializeNullableStruct() =>
        MessagePackSerializer.Deserialize<SimpleStruct?>(SerializedNullableStruct[0]);

    [Benchmark(Baseline = true), BenchmarkCategory("NullableStructsDeserialize")]
    public SimpleStruct?[] NinoDeserializeNullableStructs()
    {
        NinoGen.Deserializer.Deserialize(SerializedNullableStructs[2], out SimpleStruct?[] ret);
        return ret;
    }

    [Benchmark, BenchmarkCategory("NullableStructsDeserialize")]
    public SafeNullable<SimpleStruct>[] MemoryPackDeserializeNullableStructs() =>
        MemoryPackSerializer.Deserialize<SafeNullable<SimpleStruct>[]>(SerializedNullableStructs[1]);

    [Benchmark, BenchmarkCategory("NullableStructsDeserialize")]
    public SimpleStruct?[] MessagePackDeserializeNullableStructs() =>
        MessagePackSerializer.Deserialize<SimpleStruct?[]>(SerializedNullableStructs[0]);

    [Benchmark(Baseline = true), BenchmarkCategory("NullableObjectDeserialize")]
    [ReadsNull]
    public SimpleClass NinoDeserializeNullableObject()
    {
        NinoGen.Deserializer.Deserialize(SerializedNullableObject[2], out SimpleClass ret);
        return ret;
    }

    [Benchmark, BenchmarkCategory("NullableObjectDeserialize")]
    public SimpleClass MemoryPackDeserializeNullableObject() =>
        MemoryPackSerializer.Deserialize<SimpleClass>(SerializedNullableObject[1]);

    [Benchmark, BenchmarkCategory("NullableObjectDeserialize")]
    public SimpleClass MessagePackDeserializeNullableObject() =>
        MessagePackSerializer.Deserialize<SimpleClass>(SerializedNullableObject[0]);

    [Benchmark(Baseline = true), BenchmarkCategory("NullableObjectsDeserialize")]
    public SimpleClass[] NinoDeserializeNullableObjects()
    {
        NinoGen.Deserializer.Deserialize(SerializedNullableObjects[2], out SimpleClass[] ret);
        return ret;
    }

    [Benchmark, BenchmarkCategory("NullableObjectsDeserialize")]
    public SimpleClass[] MemoryPackDeserializeNullableObjects() =>
        MemoryPackSerializer.Deserialize<SimpleClass[]>(SerializedNullableObjects[1]);

    [Benchmark, BenchmarkCategory("NullableObjectsDeserialize")]
    public SimpleClass[] MessagePackDeserializeNullableObjects() =>
        MessagePackSerializer.Deserialize<SimpleClass[]>(SerializedNullableObjects[0]);

    [Benchmark(Baseline = true), BenchmarkCategory("ManagedStructSerialize")]
    public int NinoSerializeManagedStruct()
    {
        _ninoBuffer.ResetWrittenCount();
        NinoGen.Serializer.Serialize(Managed, _ninoBuffer);
        return _ninoBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("ManagedStructSerialize")]
    public int MemoryPackSerializeManagedStruct()
    {
        _memoryPackBuffer.ResetWrittenCount();
        MemoryPackSerializer.Serialize(_memoryPackBuffer, Managed);
        return _memoryPackBuffer.WrittenCount;
    }

    [Benchmark(Baseline = true), BenchmarkCategory("ManagedStructsSerialize")]
    public int NinoSerializeManagedStructs()
    {
        _ninoBuffer.ResetWrittenCount();
        NinoGen.Serializer.Serialize(Manageds, _ninoBuffer);
        return _ninoBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("ManagedStructsSerialize")]
    public int MemoryPackSerializeManagedStructs()
    {
        _memoryPackBuffer.ResetWrittenCount();
        MemoryPackSerializer.Serialize(_memoryPackBuffer, Manageds);
        return _memoryPackBuffer.WrittenCount;
    }

    [Benchmark(Baseline = true), BenchmarkCategory("NullableManagedStructSerialize")]
    public int NinoSerializeNullableManagedStruct()
    {
        _ninoBuffer.ResetWrittenCount();
        NinoGen.Serializer.Serialize(NullableManaged, _ninoBuffer);
        return _ninoBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("NullableManagedStructSerialize")]
    public int MemoryPackSerializeNullableManagedStruct()
    {
        _memoryPackBuffer.ResetWrittenCount();
        MemoryPackSerializer.Serialize(_memoryPackBuffer, NullableManaged);
        return _memoryPackBuffer.WrittenCount;
    }

    [Benchmark(Baseline = true), BenchmarkCategory("NullableManagedStructsSerialize")]
    public int NinoSerializeNullableManagedStructs()
    {
        _ninoBuffer.ResetWrittenCount();
        NinoGen.Serializer.Serialize(NullableManageds, _ninoBuffer);
        return _ninoBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("NullableManagedStructsSerialize")]
    public int MemoryPackSerializeNullableManagedStructs()
    {
        _memoryPackBuffer.ResetWrittenCount();
        MemoryPackSerializer.Serialize(_memoryPackBuffer, NullableManageds);
        return _memoryPackBuffer.WrittenCount;
    }

    [Benchmark(Baseline = true), BenchmarkCategory("ManagedStructDeserialize")]
    public ManagedStruct NinoDeserializeManagedStruct()
    {
        NinoGen.Deserializer.Deserialize(SerializedManagedStruct[1], out ManagedStruct ret);
        return ret;
    }

    [Benchmark, BenchmarkCategory("ManagedStructDeserialize")]
    public ManagedStruct MemoryPackDeserializeManagedStruct() =>
        MemoryPackSerializer.Deserialize<ManagedStruct>(SerializedManagedStruct[0]);

    [Benchmark(Baseline = true), BenchmarkCategory("ManagedStructsDeserialize")]
    public ManagedStruct[] NinoDeserializeManagedStructs()
    {
        NinoGen.Deserializer.Deserialize(SerializedManagedStructs[1], out ManagedStruct[] ret);
        return ret;
    }

    [Benchmark, BenchmarkCategory("ManagedStructsDeserialize")]
    public ManagedStruct[] MemoryPackDeserializeManagedStructs() =>
        MemoryPackSerializer.Deserialize<ManagedStruct[]>(SerializedManagedStructs[0]);

    [Benchmark(Baseline = true), BenchmarkCategory("NullableManagedStructDeserialize")]
    public ManagedStruct? NinoDeserializeNullableManagedStruct()
    {
        NinoGen.Deserializer.Deserialize(SerializedNullableManagedStruct[1], out ManagedStruct? ret);
        return ret;
    }

    [Benchmark, BenchmarkCategory("NullableManagedStructDeserialize")]
    public ManagedStruct? MemoryPackDeserializeNullableManagedStruct() =>
        MemoryPackSerializer.Deserialize<ManagedStruct?>(SerializedNullableManagedStruct[0]);

    [Benchmark(Baseline = true), BenchmarkCategory("NullableManagedStructsDeserialize")]
    public ManagedStruct?[] NinoDeserializeNullableManagedStructs()
    {
        NinoGen.Deserializer.Deserialize(SerializedNullableManagedStructs[1], out ManagedStruct?[] ret);
        return ret;
    }

    [Benchmark, BenchmarkCategory("NullableManagedStructsDeserialize")]
    public ManagedStruct?[] MemoryPackDeserializeNullableManagedStructs() =>
        MemoryPackSerializer.Deserialize<ManagedStruct?[]>(SerializedNullableManagedStructs[0]);
}
