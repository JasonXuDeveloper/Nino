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
namespace Nino.Benchmark;

/// <summary>
/// A struct that holds a reference (<see cref="ManagedStruct"/>: no block copy, each member written on its own), with a
/// value and as <c>ManagedStruct?</c>: one value, an array of 100, one <c>ManagedStruct?</c> that holds a value and
/// <c>ManagedStruct?[100]</c> with every fifth element null. Each pair shows what the nullable wrapper costs a struct
/// that is not raw. MemoryPack writes <c>ManagedStruct?</c> with its own nullable formatter (a flag, then the value):
/// SafeNullable takes an unmanaged T only, and nothing here is copied as memory, so there is no padding to leak and no
/// other form to time. protobuf writes the nullable ones as <see cref="OptionalManagedStruct"/> (CaseFormats.cs).
/// Each <c>Serialized*</c> array holds the bytes in NullableTest's order.
/// </summary>
public partial class NullableTest
{
    private static ManagedStruct Managed;
    private static ManagedStruct[] Manageds;
    private static ManagedStruct? NullableManaged;
    private static ManagedStruct?[] NullableManageds;
    private static OptionalManagedStruct NullableManagedProtobuf;
    private static OptionalManagedStructList NullableManagedsProtobuf;
    private static P.ManagedStruct ManagedProto;
    private static P.ManagedStructList ManagedsProto;
    private static P.OptionalManagedStruct NullableManagedProto;
    private static P.OptionalManagedStructList NullableManagedsProto;
    private static byte[][] SerializedManagedStruct, SerializedManagedStructs, SerializedNullableManagedStruct, SerializedNullableManagedStructs;

    private static void InitManagedStructs(string[] libraries)
    {
        Managed = ManagedStruct.Create(0);
        Manageds = ManagedStruct.CreateArray(100);
        NullableManaged = Managed;
        NullableManageds = ManagedStruct.CreateNullableArray(100);
        NullableManagedProtobuf = new OptionalManagedStruct { Value = NullableManaged };
        NullableManagedsProtobuf = new OptionalManagedStructList { Items = OptionalManagedStruct.From(NullableManageds) };

        ManagedProto = P.ManagedStruct.Parser.ParseFrom(ProtobufModel.Serialize(Managed));
        SerializedManagedStruct =
        [
            MessagePackSerializer.Serialize(Managed), MemoryPackSerializer.Serialize(Managed),
            NinoSerializer.Serialize(Managed), ProtobufModel.Serialize(Managed), ManagedProto.ToByteArray(),
            JsonSerializer.SerializeToUtf8Bytes(Managed, CaseJson.Default.ManagedStruct),
        ];
        ManagedsProto = P.ManagedStructList.Parser.ParseFrom(ProtobufModel.Serialize(Manageds));
        SerializedManagedStructs =
        [
            MessagePackSerializer.Serialize(Manageds), MemoryPackSerializer.Serialize(Manageds),
            NinoSerializer.Serialize(Manageds), ProtobufModel.Serialize(Manageds), ManagedsProto.ToByteArray(),
            JsonSerializer.SerializeToUtf8Bytes(Manageds, CaseJson.Default.ManagedStructArray),
        ];
        NullableManagedProto = P.OptionalManagedStruct.Parser.ParseFrom(ProtobufModel.Serialize(NullableManagedProtobuf));
        SerializedNullableManagedStruct =
        [
            MessagePackSerializer.Serialize(NullableManaged), MemoryPackSerializer.Serialize(NullableManaged),
            NinoSerializer.Serialize(NullableManaged), ProtobufModel.Serialize(NullableManagedProtobuf), NullableManagedProto.ToByteArray(),
            JsonSerializer.SerializeToUtf8Bytes(NullableManaged, CaseJson.Default.NullableManagedStruct),
        ];
        NullableManagedsProto = P.OptionalManagedStructList.Parser.ParseFrom(ProtobufModel.Serialize(NullableManagedsProtobuf));
        SerializedNullableManagedStructs =
        [
            MessagePackSerializer.Serialize(NullableManageds), MemoryPackSerializer.Serialize(NullableManageds),
            NinoSerializer.Serialize(NullableManageds), ProtobufModel.Serialize(NullableManagedsProtobuf), NullableManagedsProto.ToByteArray(),
            JsonSerializer.SerializeToUtf8Bytes(NullableManageds, CaseJson.Default.NullableManagedStructArray),
        ];

        for (int i = 0; i < libraries.Length; i++)
        {
            BenchmarkPayloadRegistry.Register($"{libraries[i]}SerializeManagedStruct", SerializedManagedStruct[i].Length);
            BenchmarkPayloadRegistry.Register($"{libraries[i]}SerializeManagedStructs", SerializedManagedStructs[i].Length);
            BenchmarkPayloadRegistry.Register($"{libraries[i]}SerializeNullableManagedStruct", SerializedNullableManagedStruct[i].Length);
            BenchmarkPayloadRegistry.Register($"{libraries[i]}SerializeNullableManagedStructs", SerializedNullableManagedStructs[i].Length);
        }
    }

    [Benchmark(Baseline = true), BenchmarkCategory("ManagedStructSerialize")]
    public int NinoSerializeManagedStruct()
    {
        _ninoBuffer.ResetWrittenCount();
        NinoSerializer.Serialize(Managed, _ninoBuffer);
        return _ninoBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("ManagedStructSerialize")]
    public int MemoryPackSerializeManagedStruct()
    {
        _memoryPackBuffer.ResetWrittenCount();
        MemoryPackSerializer.Serialize(_memoryPackBuffer, Managed);
        return _memoryPackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("ManagedStructSerialize")]
    public int MessagePackSerializeManagedStruct()
    {
        _messagePackBuffer.ResetWrittenCount();
        MessagePackSerializer.Serialize(_messagePackBuffer, Managed);
        return _messagePackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("ManagedStructSerialize")]
    public int ProtobufSerializeManagedStruct()
    {
        _protobufStream.SetLength(0);
        Serializer.Serialize(_protobufStream, Managed);
        return (int)_protobufStream.Length;
    }

    [Benchmark, BenchmarkCategory("ManagedStructSerialize")]
    public int GoogleProtobufSerializeManagedStruct()
    {
        _googleProtobufBuffer.ResetWrittenCount();
        ManagedProto.WriteTo(_googleProtobufBuffer);
        return _googleProtobufBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("ManagedStructSerialize")]
    public int JsonSerializeManagedStruct()
    {
        _jsonBuffer.ResetWrittenCount();
        _jsonWriter.Reset(_jsonBuffer);
        JsonSerializer.Serialize(_jsonWriter, Managed, CaseJson.Default.ManagedStruct);
        return _jsonBuffer.WrittenCount;
    }

    [Benchmark(Baseline = true), BenchmarkCategory("ManagedStructsSerialize")]
    public int NinoSerializeManagedStructs()
    {
        _ninoBuffer.ResetWrittenCount();
        NinoSerializer.Serialize(Manageds, _ninoBuffer);
        return _ninoBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("ManagedStructsSerialize")]
    public int MemoryPackSerializeManagedStructs()
    {
        _memoryPackBuffer.ResetWrittenCount();
        MemoryPackSerializer.Serialize(_memoryPackBuffer, Manageds);
        return _memoryPackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("ManagedStructsSerialize")]
    public int MessagePackSerializeManagedStructs()
    {
        _messagePackBuffer.ResetWrittenCount();
        MessagePackSerializer.Serialize(_messagePackBuffer, Manageds);
        return _messagePackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("ManagedStructsSerialize")]
    public int ProtobufSerializeManagedStructs()
    {
        _protobufStream.SetLength(0);
        Serializer.Serialize(_protobufStream, Manageds);
        return (int)_protobufStream.Length;
    }

    [Benchmark, BenchmarkCategory("ManagedStructsSerialize")]
    public int GoogleProtobufSerializeManagedStructs()
    {
        _googleProtobufBuffer.ResetWrittenCount();
        ManagedsProto.WriteTo(_googleProtobufBuffer);
        return _googleProtobufBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("ManagedStructsSerialize")]
    public int JsonSerializeManagedStructs()
    {
        _jsonBuffer.ResetWrittenCount();
        _jsonWriter.Reset(_jsonBuffer);
        JsonSerializer.Serialize(_jsonWriter, Manageds, CaseJson.Default.ManagedStructArray);
        return _jsonBuffer.WrittenCount;
    }

    [Benchmark(Baseline = true), BenchmarkCategory("NullableManagedStructSerialize")]
    public int NinoSerializeNullableManagedStruct()
    {
        _ninoBuffer.ResetWrittenCount();
        NinoSerializer.Serialize(NullableManaged, _ninoBuffer);
        return _ninoBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("NullableManagedStructSerialize")]
    public int MemoryPackSerializeNullableManagedStruct()
    {
        _memoryPackBuffer.ResetWrittenCount();
        MemoryPackSerializer.Serialize(_memoryPackBuffer, NullableManaged);
        return _memoryPackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("NullableManagedStructSerialize")]
    public int MessagePackSerializeNullableManagedStruct()
    {
        _messagePackBuffer.ResetWrittenCount();
        MessagePackSerializer.Serialize(_messagePackBuffer, NullableManaged);
        return _messagePackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("NullableManagedStructSerialize")]
    public int ProtobufSerializeNullableManagedStruct()
    {
        _protobufStream.SetLength(0);
        Serializer.Serialize(_protobufStream, NullableManagedProtobuf);
        return (int)_protobufStream.Length;
    }

    [Benchmark, BenchmarkCategory("NullableManagedStructSerialize")]
    public int GoogleProtobufSerializeNullableManagedStruct()
    {
        _googleProtobufBuffer.ResetWrittenCount();
        NullableManagedProto.WriteTo(_googleProtobufBuffer);
        return _googleProtobufBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("NullableManagedStructSerialize")]
    public int JsonSerializeNullableManagedStruct()
    {
        _jsonBuffer.ResetWrittenCount();
        _jsonWriter.Reset(_jsonBuffer);
        JsonSerializer.Serialize(_jsonWriter, NullableManaged, CaseJson.Default.NullableManagedStruct);
        return _jsonBuffer.WrittenCount;
    }

    [Benchmark(Baseline = true), BenchmarkCategory("NullableManagedStructsSerialize")]
    public int NinoSerializeNullableManagedStructs()
    {
        _ninoBuffer.ResetWrittenCount();
        NinoSerializer.Serialize(NullableManageds, _ninoBuffer);
        return _ninoBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("NullableManagedStructsSerialize")]
    public int MemoryPackSerializeNullableManagedStructs()
    {
        _memoryPackBuffer.ResetWrittenCount();
        MemoryPackSerializer.Serialize(_memoryPackBuffer, NullableManageds);
        return _memoryPackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("NullableManagedStructsSerialize")]
    public int MessagePackSerializeNullableManagedStructs()
    {
        _messagePackBuffer.ResetWrittenCount();
        MessagePackSerializer.Serialize(_messagePackBuffer, NullableManageds);
        return _messagePackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("NullableManagedStructsSerialize")]
    public int ProtobufSerializeNullableManagedStructs()
    {
        _protobufStream.SetLength(0);
        Serializer.Serialize(_protobufStream, NullableManagedsProtobuf);
        return (int)_protobufStream.Length;
    }

    [Benchmark, BenchmarkCategory("NullableManagedStructsSerialize")]
    public int GoogleProtobufSerializeNullableManagedStructs()
    {
        _googleProtobufBuffer.ResetWrittenCount();
        NullableManagedsProto.WriteTo(_googleProtobufBuffer);
        return _googleProtobufBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("NullableManagedStructsSerialize")]
    public int JsonSerializeNullableManagedStructs()
    {
        _jsonBuffer.ResetWrittenCount();
        _jsonWriter.Reset(_jsonBuffer);
        JsonSerializer.Serialize(_jsonWriter, NullableManageds, CaseJson.Default.NullableManagedStructArray);
        return _jsonBuffer.WrittenCount;
    }

    [Benchmark(Baseline = true), BenchmarkCategory("ManagedStructDeserialize")]
    public ManagedStruct NinoDeserializeManagedStruct() =>
        NinoDeserializer.Deserialize<ManagedStruct>(SerializedManagedStruct[2]);

    [Benchmark, BenchmarkCategory("ManagedStructDeserialize")]
    public ManagedStruct MemoryPackDeserializeManagedStruct() =>
        MemoryPackSerializer.Deserialize<ManagedStruct>(SerializedManagedStruct[1]);

    [Benchmark, BenchmarkCategory("ManagedStructDeserialize")]
    public ManagedStruct MessagePackDeserializeManagedStruct() =>
        MessagePackSerializer.Deserialize<ManagedStruct>(SerializedManagedStruct[0]);

    [Benchmark, BenchmarkCategory("ManagedStructDeserialize")]
    public ManagedStruct ProtobufDeserializeManagedStruct() =>
        Serializer.Deserialize<ManagedStruct>(new ReadOnlySpan<byte>(SerializedManagedStruct[3]));

    [Benchmark, BenchmarkCategory("ManagedStructDeserialize")]
    public P.ManagedStruct GoogleProtobufDeserializeManagedStruct() =>
        P.ManagedStruct.Parser.ParseFrom(new ReadOnlySpan<byte>(SerializedManagedStruct[4]));

    [Benchmark, BenchmarkCategory("ManagedStructDeserialize")]
    public ManagedStruct JsonDeserializeManagedStruct() =>
        JsonSerializer.Deserialize(SerializedManagedStruct[5], CaseJson.Default.ManagedStruct);

    [Benchmark(Baseline = true), BenchmarkCategory("ManagedStructsDeserialize")]
    public ManagedStruct[] NinoDeserializeManagedStructs() =>
        NinoDeserializer.Deserialize<ManagedStruct[]>(SerializedManagedStructs[2]);

    [Benchmark, BenchmarkCategory("ManagedStructsDeserialize")]
    public ManagedStruct[] MemoryPackDeserializeManagedStructs() =>
        MemoryPackSerializer.Deserialize<ManagedStruct[]>(SerializedManagedStructs[1]);

    [Benchmark, BenchmarkCategory("ManagedStructsDeserialize")]
    public ManagedStruct[] MessagePackDeserializeManagedStructs() =>
        MessagePackSerializer.Deserialize<ManagedStruct[]>(SerializedManagedStructs[0]);

    [Benchmark, BenchmarkCategory("ManagedStructsDeserialize")]
    public ManagedStruct[] ProtobufDeserializeManagedStructs() =>
        Serializer.Deserialize<ManagedStruct[]>(new ReadOnlySpan<byte>(SerializedManagedStructs[3]));

    [Benchmark, BenchmarkCategory("ManagedStructsDeserialize")]
    public P.ManagedStructList GoogleProtobufDeserializeManagedStructs() =>
        P.ManagedStructList.Parser.ParseFrom(new ReadOnlySpan<byte>(SerializedManagedStructs[4]));

    [Benchmark, BenchmarkCategory("ManagedStructsDeserialize")]
    public ManagedStruct[] JsonDeserializeManagedStructs() =>
        JsonSerializer.Deserialize(SerializedManagedStructs[5], CaseJson.Default.ManagedStructArray);

    [Benchmark(Baseline = true), BenchmarkCategory("NullableManagedStructDeserialize")]
    public ManagedStruct? NinoDeserializeNullableManagedStruct() =>
        NinoDeserializer.Deserialize<ManagedStruct?>(SerializedNullableManagedStruct[2]);

    [Benchmark, BenchmarkCategory("NullableManagedStructDeserialize")]
    public ManagedStruct? MemoryPackDeserializeNullableManagedStruct() =>
        MemoryPackSerializer.Deserialize<ManagedStruct?>(SerializedNullableManagedStruct[1]);

    [Benchmark, BenchmarkCategory("NullableManagedStructDeserialize")]
    public ManagedStruct? MessagePackDeserializeNullableManagedStruct() =>
        MessagePackSerializer.Deserialize<ManagedStruct?>(SerializedNullableManagedStruct[0]);

    [Benchmark, BenchmarkCategory("NullableManagedStructDeserialize")]
    public ManagedStruct? ProtobufDeserializeNullableManagedStruct() =>
        Serializer.Deserialize<OptionalManagedStruct>(new ReadOnlySpan<byte>(SerializedNullableManagedStruct[3])).Value;

    [Benchmark, BenchmarkCategory("NullableManagedStructDeserialize")]
    public P.OptionalManagedStruct GoogleProtobufDeserializeNullableManagedStruct() =>
        P.OptionalManagedStruct.Parser.ParseFrom(new ReadOnlySpan<byte>(SerializedNullableManagedStruct[4]));

    [Benchmark, BenchmarkCategory("NullableManagedStructDeserialize")]
    public ManagedStruct? JsonDeserializeNullableManagedStruct() =>
        JsonSerializer.Deserialize(SerializedNullableManagedStruct[5], CaseJson.Default.NullableManagedStruct);

    [Benchmark(Baseline = true), BenchmarkCategory("NullableManagedStructsDeserialize")]
    public ManagedStruct?[] NinoDeserializeNullableManagedStructs() =>
        NinoDeserializer.Deserialize<ManagedStruct?[]>(SerializedNullableManagedStructs[2]);

    [Benchmark, BenchmarkCategory("NullableManagedStructsDeserialize")]
    public ManagedStruct?[] MemoryPackDeserializeNullableManagedStructs() =>
        MemoryPackSerializer.Deserialize<ManagedStruct?[]>(SerializedNullableManagedStructs[1]);

    [Benchmark, BenchmarkCategory("NullableManagedStructsDeserialize")]
    public ManagedStruct?[] MessagePackDeserializeNullableManagedStructs() =>
        MessagePackSerializer.Deserialize<ManagedStruct?[]>(SerializedNullableManagedStructs[0]);

    [Benchmark, BenchmarkCategory("NullableManagedStructsDeserialize")]
    public OptionalManagedStruct[] ProtobufDeserializeNullableManagedStructs() =>
        Serializer.Deserialize<OptionalManagedStructList>(new ReadOnlySpan<byte>(SerializedNullableManagedStructs[3])).Items;

    [Benchmark, BenchmarkCategory("NullableManagedStructsDeserialize")]
    public P.OptionalManagedStructList GoogleProtobufDeserializeNullableManagedStructs() =>
        P.OptionalManagedStructList.Parser.ParseFrom(new ReadOnlySpan<byte>(SerializedNullableManagedStructs[4]));

    [Benchmark, BenchmarkCategory("NullableManagedStructsDeserialize")]
    public ManagedStruct?[] JsonDeserializeNullableManagedStructs() =>
        JsonSerializer.Deserialize(SerializedNullableManagedStructs[5], CaseJson.Default.NullableManagedStructArray);
}
