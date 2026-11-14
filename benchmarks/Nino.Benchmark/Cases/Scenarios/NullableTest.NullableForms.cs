using System;
using System.Collections.Generic;
using BenchmarkDotNet.Attributes;
using MemoryPack;
using Nino.Core;

#nullable disable
namespace Nino.Benchmark;

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

/// <summary>
/// <see cref="NullableFields"/> with <see cref="NinoNullable{T}"/> members: what a user who picks Nino's form of a
/// nullable value writes (the bytes of <see cref="NullableFields"/>).
/// </summary>
[NinoType]
public sealed class NullableFieldsNino
{
    public NinoNullable<int> Count;
    public NinoNullable<long> Total;
    public NinoNullable<double> Score;
    public NinoNullable<bool> Enabled;
    public NinoNullable<DateTime> UpdatedAt;
    public NinoNullable<Guid> OwnerId;
    public NinoNullable<decimal> Price;
    public NinoNullable<NullablePriority> Priority;

    public static NullableFieldsNino From(NullableFields value) => new()
    {
        Count = value.Count, Total = value.Total, Score = value.Score, Enabled = value.Enabled,
        UpdatedAt = value.UpdatedAt, OwnerId = value.OwnerId, Price = value.Price, Priority = value.Priority,
    };

    public NullableFields ToNullable() => new()
    {
        Count = Count, Total = Total, Score = Score, Enabled = Enabled,
        UpdatedAt = UpdatedAt, OwnerId = OwnerId, Price = Price, Priority = Priority,
    };
}

/// <summary>
/// <see cref="NullableFields"/> for MemoryPack with plain <c>T?</c> members: MemoryPack copies each one's memory,
/// uninitialized padding included (<see cref="NullableFieldsMemoryPack"/> says why its own cases use SafeNullable).
/// </summary>
[MemoryPackable]
public sealed partial class NullableFieldsUnsafe
{
    public int? Count;
    public long? Total;
    public double? Score;
    public bool? Enabled;
    public DateTime? UpdatedAt;
    public Guid? OwnerId;
    public decimal? Price;
    public NullablePriority? Priority;

    public static NullableFieldsUnsafe From(NullableFields value) => new()
    {
        Count = value.Count, Total = value.Total, Score = value.Score, Enabled = value.Enabled,
        UpdatedAt = value.UpdatedAt, OwnerId = value.OwnerId, Price = value.Price, Priority = value.Priority,
    };

    public NullableFields ToNullable() => new()
    {
        Count = Count, Total = Total, Score = Score, Enabled = Enabled,
        UpdatedAt = UpdatedAt, OwnerId = OwnerId, Price = Price, Priority = Priority,
    };
}

/// <summary>
/// The forms of a nullable value the nullable cases time besides each library's own (CollectionsTest, NullableTest):
///  - <b>NinoNullable</b>: the user declares <see cref="NinoNullable{T}"/> (Nino.Core) instead of <c>T?</c>; same bytes
///    as Nino's <c>T?</c>, its memory always clean, so arrays of it are one block copy. Safe.
///  - <b>MemoryPack (unsafe)</b>: MemoryPack given <c>T?</c> as it is: it copies the value's memory, uninitialized
///    padding included, so one value can give different bytes, with leftover memory of the process in them.
///  - <b>MemoryPack (converted)</b>: the user's <c>T?</c> converted to <c>SafeNullable&lt;T&gt;</c> before writing and
///    back after reading, inside the timed region: what MemoryPack's safe form costs a model that holds <c>T?</c>.
///  The MemoryPack cases without a suffix write SafeNullable data the model already holds (converted outside the
///  timed region). Nino's <c>T?</c> is always safe: it zeroes the bytes of an absent value.
/// </summary>
public static class NullableForms
{
    public static NinoNullable<T>[] Nino<T>(T?[] values) where T : unmanaged =>
        Array.ConvertAll(values, v => (NinoNullable<T>)v);

    public static List<NinoNullable<T>> Nino<T>(List<T?> values) where T : unmanaged =>
        values.ConvertAll(v => (NinoNullable<T>)v);

    public static T?[] ToNullable<T>(SafeNullable<T>[] values) where T : unmanaged =>
        Array.ConvertAll(values, v => v.ToNullable());

    public static List<T?> ToNullable<T>(List<SafeNullable<T>> values) where T : unmanaged =>
        values.ConvertAll(v => v.ToNullable());

    /// <summary>A result of these forms as the nullable data the other cases read; any other result as it is.</summary>
    public static object ReadBack(object result) => result switch
    {
        NinoNullable<long>[] longs => Array.ConvertAll(longs, v => v.ToNullable()),
        List<NinoNullable<int>> ints => ints.ConvertAll(v => v.ToNullable()),
        NinoNullable<decimal>[] decimals => Array.ConvertAll(decimals, v => v.ToNullable()),
        NinoNullable<SimpleStruct> single => single.ToNullable(),
        NinoNullable<SimpleStruct>[] structs => Array.ConvertAll(structs, v => v.ToNullable()),
        NullableFieldsNino one => one.ToNullable(),
        NullableFieldsNino[] many => Array.ConvertAll(many, v => v.ToNullable()),
        NullableFieldsUnsafe one => one.ToNullable(),
        NullableFieldsUnsafe[] many => Array.ConvertAll(many, v => v.ToNullable()),
        _ => SafeNullables.ReadBack(result),
    };
}
