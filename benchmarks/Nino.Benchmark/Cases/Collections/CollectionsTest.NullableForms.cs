using System;
using System.Collections.Generic;
using BenchmarkDotNet.Attributes;
using MemoryPack;
using Nino.Core;

#nullable disable
namespace Nino.Benchmark;

/// <summary>
/// The nullable collections in the other forms a user can choose (<see cref="NullableForms"/> says what each one is):
/// Nino with <see cref="NinoNullable{T}"/> elements, MemoryPack writing <c>T?</c> as it is (its unsafe form) and
/// MemoryPack converting <c>T?</c> to <c>SafeNullable&lt;T&gt;</c> and back inside the timed region.
/// </summary>
public partial class CollectionsTest
{
    private static NinoNullable<long>[] NinoNullableLongs;
    private static List<NinoNullable<int>> NinoNullableInts;
    private static NinoNullable<decimal>[] NinoNullableDecimals;
    private static byte[] MemoryPackUnsafeNullableLongsBytes;
    private static byte[] MemoryPackUnsafeNullableIntsBytes;
    private static byte[] MemoryPackUnsafeNullableDecimalsBytes;

    private static void InitNullableForms()
    {
        NinoNullableLongs = NullableForms.Nino(NullableLongs);
        NinoNullableInts = NullableForms.Nino(NullableInts);
        NinoNullableDecimals = NullableForms.Nino(NullableDecimals);
        MemoryPackUnsafeNullableLongsBytes = MemoryPackSerializer.Serialize(NullableLongs);
        MemoryPackUnsafeNullableIntsBytes = MemoryPackSerializer.Serialize(NullableInts);
        MemoryPackUnsafeNullableDecimalsBytes = MemoryPackSerializer.Serialize(NullableDecimals);

        // NinoNullable writes T?'s bytes; MemoryPack's conversion writes SafeNullable's.
        BenchmarkPayloadRegistry.Register(nameof(NinoNullableSerializeNullableLongs), NinoNullableLongsBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(NinoNullableSerializeNullableInts), NinoNullableIntsBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(NinoNullableSerializeNullableDecimals), NinoNullableDecimalsBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(MemoryPackUnsafeSerializeNullableLongs), MemoryPackUnsafeNullableLongsBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(MemoryPackUnsafeSerializeNullableInts), MemoryPackUnsafeNullableIntsBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(MemoryPackUnsafeSerializeNullableDecimals), MemoryPackUnsafeNullableDecimalsBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(MemoryPackConvertedSerializeNullableLongs), MemoryPackNullableLongsBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(MemoryPackConvertedSerializeNullableInts), MemoryPackNullableIntsBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(MemoryPackConvertedSerializeNullableDecimals), MemoryPackNullableDecimalsBytes.Length);
    }

    [Benchmark, BenchmarkCategory("NullableLongsSerialize")]
    public int NinoNullableSerializeNullableLongs()
    {
        _ninoBuffer.ResetWrittenCount();
        NinoSerializer.Serialize(NinoNullableLongs, _ninoBuffer);
        return _ninoBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("NullableLongsSerialize")]
    public int MemoryPackUnsafeSerializeNullableLongs()
    {
        _memoryPackBuffer.ResetWrittenCount();
        MemoryPackSerializer.Serialize(_memoryPackBuffer, NullableLongs);
        return _memoryPackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("NullableLongsSerialize")]
    public int MemoryPackConvertedSerializeNullableLongs()
    {
        _memoryPackBuffer.ResetWrittenCount();
        MemoryPackSerializer.Serialize(_memoryPackBuffer, SafeNullables.From(NullableLongs));
        return _memoryPackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("NullableLongsDeserialize")]
    public NinoNullable<long>[] NinoNullableDeserializeNullableLongs() =>
        NinoDeserializer.Deserialize<NinoNullable<long>[]>(NinoNullableLongsBytes);

    [Benchmark, BenchmarkCategory("NullableLongsDeserialize")]
    public long?[] MemoryPackUnsafeDeserializeNullableLongs() =>
        MemoryPackSerializer.Deserialize<long?[]>(MemoryPackUnsafeNullableLongsBytes);

    [Benchmark, BenchmarkCategory("NullableLongsDeserialize")]
    public long?[] MemoryPackConvertedDeserializeNullableLongs() =>
        NullableForms.ToNullable(MemoryPackSerializer.Deserialize<SafeNullable<long>[]>(MemoryPackNullableLongsBytes));

    [Benchmark, BenchmarkCategory("NullableIntsSerialize")]
    public int NinoNullableSerializeNullableInts()
    {
        _ninoBuffer.ResetWrittenCount();
        NinoSerializer.Serialize(NinoNullableInts, _ninoBuffer);
        return _ninoBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("NullableIntsSerialize")]
    public int MemoryPackUnsafeSerializeNullableInts()
    {
        _memoryPackBuffer.ResetWrittenCount();
        MemoryPackSerializer.Serialize(_memoryPackBuffer, NullableInts);
        return _memoryPackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("NullableIntsSerialize")]
    public int MemoryPackConvertedSerializeNullableInts()
    {
        _memoryPackBuffer.ResetWrittenCount();
        MemoryPackSerializer.Serialize(_memoryPackBuffer, SafeNullables.From(NullableInts));
        return _memoryPackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("NullableIntsDeserialize")]
    public List<NinoNullable<int>> NinoNullableDeserializeNullableInts() =>
        NinoDeserializer.Deserialize<List<NinoNullable<int>>>(NinoNullableIntsBytes);

    [Benchmark, BenchmarkCategory("NullableIntsDeserialize")]
    public List<int?> MemoryPackUnsafeDeserializeNullableInts() =>
        MemoryPackSerializer.Deserialize<List<int?>>(MemoryPackUnsafeNullableIntsBytes);

    [Benchmark, BenchmarkCategory("NullableIntsDeserialize")]
    public List<int?> MemoryPackConvertedDeserializeNullableInts() =>
        NullableForms.ToNullable(MemoryPackSerializer.Deserialize<List<SafeNullable<int>>>(MemoryPackNullableIntsBytes));

    [Benchmark, BenchmarkCategory("NullableDecimalsSerialize")]
    public int NinoNullableSerializeNullableDecimals()
    {
        _ninoBuffer.ResetWrittenCount();
        NinoSerializer.Serialize(NinoNullableDecimals, _ninoBuffer);
        return _ninoBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("NullableDecimalsSerialize")]
    public int MemoryPackUnsafeSerializeNullableDecimals()
    {
        _memoryPackBuffer.ResetWrittenCount();
        MemoryPackSerializer.Serialize(_memoryPackBuffer, NullableDecimals);
        return _memoryPackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("NullableDecimalsSerialize")]
    public int MemoryPackConvertedSerializeNullableDecimals()
    {
        _memoryPackBuffer.ResetWrittenCount();
        MemoryPackSerializer.Serialize(_memoryPackBuffer, SafeNullables.From(NullableDecimals));
        return _memoryPackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("NullableDecimalsDeserialize")]
    public NinoNullable<decimal>[] NinoNullableDeserializeNullableDecimals() =>
        NinoDeserializer.Deserialize<NinoNullable<decimal>[]>(NinoNullableDecimalsBytes);

    [Benchmark, BenchmarkCategory("NullableDecimalsDeserialize")]
    public decimal?[] MemoryPackUnsafeDeserializeNullableDecimals() =>
        MemoryPackSerializer.Deserialize<decimal?[]>(MemoryPackUnsafeNullableDecimalsBytes);

    [Benchmark, BenchmarkCategory("NullableDecimalsDeserialize")]
    public decimal?[] MemoryPackConvertedDeserializeNullableDecimals() =>
        NullableForms.ToNullable(MemoryPackSerializer.Deserialize<SafeNullable<decimal>[]>(MemoryPackNullableDecimalsBytes));
}
