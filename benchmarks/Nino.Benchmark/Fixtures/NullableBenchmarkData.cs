using System;
using System.Collections.Generic;
using MemoryPack;
using MessagePack;
using Nino.Core;
using ProtoBuf;

namespace Nino.Benchmark;

#nullable disable

/// <summary>
/// NullableTest's model (the v3 build's too): eight nullable members of common types, about half of them null.
/// protobuf-net writes it at <c>CompatibilityLevel.Level300</c>, the well-known types Google.Protobuf's users write
/// (as WorkloadTest's models): the nullable scalars through <c>[NullWrappedValue]</c> as <c>google.protobuf.Int32Value</c>
/// and its siblings (Google.Protobuf reads them as <c>int?</c>, <c>long?</c>, ...: null is an absent field, zero a
/// present one), <see cref="DateTime"/> as a <c>google.protobuf.Timestamp</c> (a message: absent is null; every time is
/// UTC, what a timestamp carries), <see cref="Guid"/> and <see cref="decimal"/> as strings (absent is null; a value is
/// never empty). A proto3 enum has no presence (absent reads as its zero member), so <see cref="NullablePriority"/>
/// keeps zero for "unspecified", proto3's convention, and the data never uses it. MemoryPack writes the same values
/// through <see cref="NullableFieldsMemoryPack"/>.
/// </summary>
[NinoType]
[MessagePackObject]
[ProtoContract]
[CompatibilityLevel(CompatibilityLevel.Level300)]
public sealed partial class NullableFields
{
    [Key(0)] [ProtoMember(1), NullWrappedValue] public int? Count;
    [Key(1)] [ProtoMember(2), NullWrappedValue] public long? Total;
    [Key(2)] [ProtoMember(3), NullWrappedValue] public double? Score;
    [Key(3)] [ProtoMember(4), NullWrappedValue] public bool? Enabled;
    [Key(4)] [ProtoMember(5)] public DateTime? UpdatedAt;
    [Key(5)] [ProtoMember(6)] public Guid? OwnerId;
    [Key(6)] [ProtoMember(7)] public decimal? Price;
    [Key(7)] [ProtoMember(8)] public NullablePriority? Priority;

    /// <summary>
    /// The <paramref name="index"/>th object of a sequence: member m has a value when m + index is even, so every
    /// object has four of eight and neighbours have the other four. Its own seeded source, so the v3 and v4 runs and
    /// every process write the same values whatever ran before.
    /// </summary>
    public static NullableFields Create(int index, Random random)
    {
        bool Set(int member) => (member + index) % 2 == 0;
        var bytes = new byte[16];
        random.NextBytes(bytes);
        return new NullableFields
        {
            Count = Set(0) ? random.Next(1, 100_000) : null,
            Total = Set(1) ? random.NextInt64(1, 1L << 40) : null,
            Score = Set(2) ? random.NextDouble() * 100 : null,
            Enabled = Set(3) ? random.Next(2) == 1 : null,
            UpdatedAt = Set(4) ? new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(random.Next(31_536_000)) : null,
            OwnerId = Set(5) ? new Guid(bytes) : null,
            Price = Set(6) ? random.Next(1, 10_000_000) / 100m : null,
            Priority = Set(7) ? (NullablePriority)random.Next(1, 4) : null,
        };
    }
}

/// <summary>
/// <see cref="NullableFields"/> for MemoryPack, its members <see cref="SafeNullable{T}"/> (the owner's choice):
/// MemoryPack writes a <c>Nullable&lt;T&gt;</c> of an unmanaged T as its memory layout (bool, padding, value) and leaves
/// the padding uninitialized, so one value can give different bytes, with leftover memory in them; SafeNullable writes
/// (hasValue as a T, value) with no padding, which matches Nino's canonical bytes. Made from and read back as
/// <see cref="NullableFields"/> outside the timed region (Verify.cs: <see cref="IReadBack"/>).
/// </summary>
[MemoryPackable]
public sealed partial class NullableFieldsMemoryPack
{
    public SafeNullable<int> Count;
    public SafeNullable<long> Total;
    public SafeNullable<double> Score;
    public SafeNullable<bool> Enabled;
    public SafeNullable<DateTime> UpdatedAt;
    public SafeNullable<Guid> OwnerId;
    public SafeNullable<decimal> Price;
    public SafeNullable<NullablePriority> Priority;

    public static NullableFieldsMemoryPack From(NullableFields value) => new()
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
/// The <see cref="SafeNullable{T}"/> copies of nullable data that MemoryPack's cases write and read (see
/// <see cref="NullableFieldsMemoryPack"/> for why), and the way back for Verify.cs.
/// </summary>
public static class SafeNullables
{
    public static SafeNullable<T>[] From<T>(T?[] values) where T : unmanaged =>
        Array.ConvertAll(values, v => (SafeNullable<T>)v);

    public static List<SafeNullable<T>> From<T>(List<T?> values) where T : unmanaged =>
        values.ConvertAll(v => (SafeNullable<T>)v);

    public static NullableFieldsMemoryPack[] From(NullableFields[] values) =>
        Array.ConvertAll(values, NullableFieldsMemoryPack.From);

    /// <summary>A MemoryPack result as the nullable data the other libraries read; any other result as it is.</summary>
    public static object ReadBack(object result) => result switch
    {
        SafeNullable<long>[] longs => Array.ConvertAll(longs, v => v.ToNullable()),
        SafeNullable<decimal>[] decimals => Array.ConvertAll(decimals, v => v.ToNullable()),
        List<SafeNullable<int>> ints => ints.ConvertAll(v => v.ToNullable()),
        SafeNullable<SimpleStruct> single => single.ToNullable(),
        SafeNullable<SimpleStruct>[] structs => Array.ConvertAll(structs, v => v.ToNullable()),
        NullableFieldsMemoryPack one => one.ToNullable(),
        NullableFieldsMemoryPack[] many => Array.ConvertAll(many, v => v.ToNullable()),
        _ => result,
    };
}

/// <summary>NullableFields.Priority: zero is proto3's "unspecified", which the data never uses.</summary>
public enum NullablePriority
{
    Unspecified = 0,
    Low = 1,
    Normal = 2,
    High = 3,
}
