// A/B probe only (not for main): the hand-written class before BenchGen, to time against the generated one.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using BenchmarkDotNet.Attributes;
using MemoryPack;
using Nino.Core;

#nullable disable
namespace Nino.Benchmark.Old;

/// <summary>
/// The nullable cases in the other forms a user can choose (<see cref="NullableForms"/> says what each one is): Nino
/// with <see cref="NinoNullable{T}"/> members and elements, MemoryPack writing <c>T?</c> as it is (its unsafe form)
/// and MemoryPack converting <c>T?</c> to <c>SafeNullable&lt;T&gt;</c> and back inside the timed region.
/// </summary>
public partial class NullableTest
{
    private static NullableFieldsNino FieldsNino;
    private static NullableFieldsNino[] FieldsArrayNino;
    private static NinoNullable<SimpleStruct> StructNino;
    private static NinoNullable<SimpleStruct>[] StructsNino;
    private static NullableFieldsUnsafe FieldsUnsafe;
    private static NullableFieldsUnsafe[] FieldsArrayUnsafe;
    private static byte[] NinoNullableFieldsBytes, NinoNullableFieldsArrayBytes, NinoNullableStructBytes, NinoNullableStructsBytes;
    private static byte[] UnsafeFieldsBytes, UnsafeFieldsArrayBytes, UnsafeStructBytes, UnsafeStructsBytes;

    private static void InitNullableForms()
    {
        FieldsNino = NullableFieldsNino.From(Fields);
        FieldsArrayNino = Array.ConvertAll(FieldsArray, NullableFieldsNino.From);
        StructNino = Struct;
        StructsNino = NullableForms.Nino(Structs);
        FieldsUnsafe = NullableFieldsUnsafe.From(Fields);
        FieldsArrayUnsafe = Array.ConvertAll(FieldsArray, NullableFieldsUnsafe.From);

        NinoNullableFieldsBytes = NinoSerializer.Serialize(FieldsNino);
        NinoNullableFieldsArrayBytes = NinoSerializer.Serialize(FieldsArrayNino);
        NinoNullableStructBytes = NinoSerializer.Serialize(StructNino);
        NinoNullableStructsBytes = NinoSerializer.Serialize(StructsNino);
        UnsafeFieldsBytes = MemoryPackSerializer.Serialize(FieldsUnsafe);
        UnsafeFieldsArrayBytes = MemoryPackSerializer.Serialize(FieldsArrayUnsafe);
        UnsafeStructBytes = MemoryPackSerializer.Serialize(Struct);
        UnsafeStructsBytes = MemoryPackSerializer.Serialize(Structs);

        (string Case, byte[] Nino, byte[] Unsafe, byte[] Converted)[] cases =
        [
            ("NullableFields", NinoNullableFieldsBytes, UnsafeFieldsBytes, SerializedNullableFields[1]),
            ("NullableFieldsArray", NinoNullableFieldsArrayBytes, UnsafeFieldsArrayBytes, SerializedNullableFieldsArray[1]),
            ("NullableStruct", NinoNullableStructBytes, UnsafeStructBytes, SerializedNullableStruct[1]),
            ("NullableStructs", NinoNullableStructsBytes, UnsafeStructsBytes, SerializedNullableStructs[1]),
        ];
        foreach (var (name, nino, @unsafe, converted) in cases)
        {
            BenchmarkPayloadRegistry.Register($"NinoNullableSerialize{name}", nino.Length);
            BenchmarkPayloadRegistry.Register($"MemoryPackUnsafeSerialize{name}", @unsafe.Length);
            BenchmarkPayloadRegistry.Register($"MemoryPackConvertedSerialize{name}", converted.Length);
        }
    }

    [Benchmark, BenchmarkCategory("NullableFieldsSerialize")]
    public int NinoNullableSerializeNullableFields()
    {
        _ninoBuffer.ResetWrittenCount();
        NinoSerializer.Serialize(FieldsNino, _ninoBuffer);
        return _ninoBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("NullableFieldsSerialize")]
    public int MemoryPackUnsafeSerializeNullableFields()
    {
        _memoryPackBuffer.ResetWrittenCount();
        MemoryPackSerializer.Serialize(_memoryPackBuffer, FieldsUnsafe);
        return _memoryPackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("NullableFieldsSerialize")]
    public int MemoryPackConvertedSerializeNullableFields()
    {
        _memoryPackBuffer.ResetWrittenCount();
        MemoryPackSerializer.Serialize(_memoryPackBuffer, NullableFieldsMemoryPack.From(Fields));
        return _memoryPackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("NullableFieldsArraySerialize")]
    public int NinoNullableSerializeNullableFieldsArray()
    {
        _ninoBuffer.ResetWrittenCount();
        NinoSerializer.Serialize(FieldsArrayNino, _ninoBuffer);
        return _ninoBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("NullableFieldsArraySerialize")]
    public int MemoryPackUnsafeSerializeNullableFieldsArray()
    {
        _memoryPackBuffer.ResetWrittenCount();
        MemoryPackSerializer.Serialize(_memoryPackBuffer, FieldsArrayUnsafe);
        return _memoryPackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("NullableFieldsArraySerialize")]
    public int MemoryPackConvertedSerializeNullableFieldsArray()
    {
        _memoryPackBuffer.ResetWrittenCount();
        MemoryPackSerializer.Serialize(_memoryPackBuffer, SafeNullables.From(FieldsArray));
        return _memoryPackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("NullableStructSerialize")]
    public int NinoNullableSerializeNullableStruct()
    {
        _ninoBuffer.ResetWrittenCount();
        NinoSerializer.Serialize(StructNino, _ninoBuffer);
        return _ninoBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("NullableStructSerialize")]
    public int MemoryPackUnsafeSerializeNullableStruct()
    {
        _memoryPackBuffer.ResetWrittenCount();
        MemoryPackSerializer.Serialize(_memoryPackBuffer, Struct);
        return _memoryPackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("NullableStructSerialize")]
    public int MemoryPackConvertedSerializeNullableStruct()
    {
        _memoryPackBuffer.ResetWrittenCount();
        MemoryPackSerializer.Serialize(_memoryPackBuffer, (SafeNullable<SimpleStruct>)Struct);
        return _memoryPackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("NullableStructsSerialize")]
    public int NinoNullableSerializeNullableStructs()
    {
        _ninoBuffer.ResetWrittenCount();
        NinoSerializer.Serialize(StructsNino, _ninoBuffer);
        return _ninoBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("NullableStructsSerialize")]
    public int MemoryPackUnsafeSerializeNullableStructs()
    {
        _memoryPackBuffer.ResetWrittenCount();
        MemoryPackSerializer.Serialize(_memoryPackBuffer, Structs);
        return _memoryPackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("NullableStructsSerialize")]
    public int MemoryPackConvertedSerializeNullableStructs()
    {
        _memoryPackBuffer.ResetWrittenCount();
        MemoryPackSerializer.Serialize(_memoryPackBuffer, SafeNullables.From(Structs));
        return _memoryPackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("NullableFieldsDeserialize")]
    public NullableFieldsNino NinoNullableDeserializeNullableFields() =>
        NinoDeserializer.Deserialize<NullableFieldsNino>(NinoNullableFieldsBytes);

    [Benchmark, BenchmarkCategory("NullableFieldsDeserialize")]
    public NullableFieldsUnsafe MemoryPackUnsafeDeserializeNullableFields() =>
        MemoryPackSerializer.Deserialize<NullableFieldsUnsafe>(UnsafeFieldsBytes);

    [Benchmark, BenchmarkCategory("NullableFieldsDeserialize")]
    public NullableFields MemoryPackConvertedDeserializeNullableFields() =>
        MemoryPackSerializer.Deserialize<NullableFieldsMemoryPack>(SerializedNullableFields[1]).ToNullable();

    [Benchmark, BenchmarkCategory("NullableFieldsArrayDeserialize")]
    public NullableFieldsNino[] NinoNullableDeserializeNullableFieldsArray() =>
        NinoDeserializer.Deserialize<NullableFieldsNino[]>(NinoNullableFieldsArrayBytes);

    [Benchmark, BenchmarkCategory("NullableFieldsArrayDeserialize")]
    public NullableFieldsUnsafe[] MemoryPackUnsafeDeserializeNullableFieldsArray() =>
        MemoryPackSerializer.Deserialize<NullableFieldsUnsafe[]>(UnsafeFieldsArrayBytes);

    [Benchmark, BenchmarkCategory("NullableFieldsArrayDeserialize")]
    public NullableFields[] MemoryPackConvertedDeserializeNullableFieldsArray() =>
        Array.ConvertAll(MemoryPackSerializer.Deserialize<NullableFieldsMemoryPack[]>(SerializedNullableFieldsArray[1]),
            v => v.ToNullable());

    [Benchmark, BenchmarkCategory("NullableStructDeserialize")]
    public NinoNullable<SimpleStruct> NinoNullableDeserializeNullableStruct() =>
        NinoDeserializer.Deserialize<NinoNullable<SimpleStruct>>(NinoNullableStructBytes);

    [Benchmark, BenchmarkCategory("NullableStructDeserialize")]
    public SimpleStruct? MemoryPackUnsafeDeserializeNullableStruct() =>
        MemoryPackSerializer.Deserialize<SimpleStruct?>(UnsafeStructBytes);

    [Benchmark, BenchmarkCategory("NullableStructDeserialize")]
    public SimpleStruct? MemoryPackConvertedDeserializeNullableStruct() =>
        MemoryPackSerializer.Deserialize<SafeNullable<SimpleStruct>>(SerializedNullableStruct[1]).ToNullable();

    [Benchmark, BenchmarkCategory("NullableStructsDeserialize")]
    public NinoNullable<SimpleStruct>[] NinoNullableDeserializeNullableStructs() =>
        NinoDeserializer.Deserialize<NinoNullable<SimpleStruct>[]>(NinoNullableStructsBytes);

    [Benchmark, BenchmarkCategory("NullableStructsDeserialize")]
    public SimpleStruct?[] MemoryPackUnsafeDeserializeNullableStructs() =>
        MemoryPackSerializer.Deserialize<SimpleStruct?[]>(UnsafeStructsBytes);

    [Benchmark, BenchmarkCategory("NullableStructsDeserialize")]
    public SimpleStruct?[] MemoryPackConvertedDeserializeNullableStructs() =>
        NullableForms.ToNullable(MemoryPackSerializer.Deserialize<SafeNullable<SimpleStruct>[]>(SerializedNullableStructs[1]));
}
