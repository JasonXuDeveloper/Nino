using System;
using MemoryPack;
#if !NINO_BENCH_V3
using BenchmarkDotNet.Attributes;
using Nino.Core;
#endif

#nullable disable
namespace Nino.Benchmark;

/// <summary>
/// A raw (unmanaged) struct that holds nullable members (<see cref="RawNullable"/>: an int, a <c>long?</c> and a
/// <c>double?</c>), one value and an array of 1,000, in every form (<see cref="NullableForms"/>):
///  - Nino with <c>T?</c> members: each one a presence byte and the value when present, so neither the struct nor
///    the array is one copy;
///  - Nino with <see cref="NinoNullable{T}"/> members (<see cref="RawNullableNino"/>): a raw struct, written as its
///    memory, the array as one block;
///  - MemoryPack with <c>SafeNullable&lt;T&gt;</c> members the model already holds (<see cref="RawNullableMemoryPack"/>),
///    converting to them and back inside the timed region, and given the struct as it is (unsafe: it copies the
///    struct's memory with the uninitialized padding of each <c>T?</c>);
///  - MessagePack, protobuf-net (the members wrapped at Level300, as NullableFields), Google.Protobuf and
///    System.Text.Json.
/// </summary>
[BenchCase("RawNullable", Value = "Raw", MemoryPackValue = "RawMemoryPack", NinoNullableValue = "RawNino",
    NullableForms = true, Proto = "RawNullable", Json = "RawNullable")]
[BenchCase("RawNullables", Value = "Raws", MemoryPackValue = "RawsMemoryPack", NinoNullableValue = "RawsNino",
    NullableForms = true, Proto = "RawNullableList", Json = "RawNullableArray")]
public partial class NullableTest
{
    // The fourth value of the sequence: a Total and no Score.
    private static readonly RawNullable Raw = RawNullable.Create(3);
    private static readonly RawNullable[] Raws = RawNullable.CreateArray(1000);
    private static readonly RawNullableMemoryPack RawMemoryPack = RawNullableMemoryPack.From(Raw);
    private static readonly RawNullableMemoryPack[] RawsMemoryPack = SafeNullables.From(Raws);
#if !NINO_BENCH_V3
    private static readonly RawNullableNino RawNino = RawNullableNino.From(Raw);
    private static readonly RawNullableNino[] RawsNino = Array.ConvertAll(Raws, RawNullableNino.From);

    // MemoryPack's conversion of a model of its own (RawNullableMemoryPack).
    [Benchmark, BenchmarkCategory("RawNullableSerialize")]
    public int MemoryPackConvertedSerializeRawNullable()
    {
        _memoryPackBuffer.ResetWrittenCount();
        MemoryPackSerializer.Serialize(_memoryPackBuffer, RawNullableMemoryPack.From(Raw));
        return _memoryPackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("RawNullableDeserialize")]
    public RawNullable MemoryPackConvertedDeserializeRawNullable() =>
        MemoryPackSerializer.Deserialize<RawNullableMemoryPack>(MemoryPackRawNullableBytes).ToNullable();

    [Benchmark, BenchmarkCategory("RawNullablesDeserialize")]
    public RawNullable[] MemoryPackConvertedDeserializeRawNullables() =>
        Array.ConvertAll(MemoryPackSerializer.Deserialize<RawNullableMemoryPack[]>(MemoryPackRawNullablesBytes),
            v => v.ToNullable());
#endif
}

#if !NINO_BENCH_V3
/// <summary>
/// <see cref="RawNullable"/> with <see cref="NinoNullable{T}"/> members: what a user who picks Nino's form of a
/// nullable value writes. Every member is raw, so the struct is, and Nino writes it and an array of it as memory.
/// Here as <see cref="NullableFieldsNino"/> is, not in Fixtures/: the v3 build has no NinoNullable.
/// </summary>
[NinoType]
public struct RawNullableNino
{
    public int Id;
    public NinoNullable<long> Total;
    public NinoNullable<double> Score;

    public static RawNullableNino From(RawNullable value) =>
        new() { Id = value.Id, Total = value.Total, Score = value.Score };

    public RawNullable ToNullable() => new() { Id = Id, Total = Total, Score = Score };
}
#endif
