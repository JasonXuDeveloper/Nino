using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
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
/// NullableTest's struct that holds a reference (the v3 build's too): not raw, so no library copies it as memory and
/// each writes its members one by one, with a value and as <c>ManagedStruct?</c>. The values depend on the index only,
/// so the v3 and v4 runs and every process write the same ones; a name is never empty (protobuf writes no empty string).
/// </summary>
[NinoType]
[MemoryPackable]
[MessagePackObject]
[ProtoContract]
public partial struct ManagedStruct
{
    [Key(0)] [ProtoMember(1)] public int Id;
    [Key(1)] [ProtoMember(2)] public string Name;
    [Key(2)] [ProtoMember(3)] public double Score;

    public static ManagedStruct Create(int index) =>
        new() { Id = index * 7919 + 1, Name = "player-" + index.ToString("D4"), Score = index * 1.5 + 0.25 };

    public static ManagedStruct[] CreateArray(int count) => Enumerable.Range(0, count).Select(Create).ToArray();

    /// <summary>Every fifth element null, as the other nullable arrays.</summary>
    public static ManagedStruct?[] CreateNullableArray(int count) =>
        Enumerable.Range(0, count).Select(i => i % 5 == 0 ? null : (ManagedStruct?)Create(i)).ToArray();
}

/// <summary>
/// NullableTest's raw struct with nullable members (the v3 build's too): unmanaged, so a library that copies memory
/// takes it whole, <c>T?</c> members and their padding included. Nino writes each <c>T?</c> member as a presence byte
/// and the value when present, which is not one copy; with <c>NinoNullable&lt;T&gt;</c> members (RawNullableNino,
/// NullableTest.RawNullable.cs) the struct is its memory. MemoryPack given this struct as it is copies it, the
/// uninitialized padding of each <c>T?</c> included (its unsafe form); <see cref="RawNullableMemoryPack"/> is its safe
/// one. protobuf-net writes the nullable members through <c>[NullWrappedValue]</c> at Level300, as
/// <see cref="NullableFields"/>. The values depend on the index only: of every five neighbours one has no
/// <see cref="Total"/> and another no <see cref="Score"/>.
/// </summary>
[NinoType]
[MemoryPackable]
[MessagePackObject]
[ProtoContract]
[CompatibilityLevel(CompatibilityLevel.Level300)]
public partial struct RawNullable
{
    [Key(0)] [ProtoMember(1)] public int Id;
    [Key(1)] [ProtoMember(2), NullWrappedValue] public long? Total;
    [Key(2)] [ProtoMember(3), NullWrappedValue] public double? Score;

    public static RawNullable Create(int index) => new()
    {
        Id = index * 7919 + 1,
        Total = index % 5 == 0 ? null : (long)index * 1_000_003,
        Score = index % 5 == 3 ? null : index * 1.5 + 0.25,
    };

    public static RawNullable[] CreateArray(int count) => Enumerable.Range(0, count).Select(Create).ToArray();
}

/// <summary>
/// <see cref="RawNullable"/> for MemoryPack's safe form, its members <see cref="SafeNullable{T}"/> (no uninitialized
/// padding in the bytes, <see cref="NullableFieldsMemoryPack"/>): still unmanaged, so MemoryPack copies it whole.
/// </summary>
[MemoryPackable]
public partial struct RawNullableMemoryPack
{
    public int Id;
    public SafeNullable<long> Total;
    public SafeNullable<double> Score;

    public static RawNullableMemoryPack From(RawNullable value) =>
        new() { Id = value.Id, Total = value.Total, Score = value.Score };

    public RawNullable ToNullable() => new() { Id = Id, Total = Total, Score = Score };
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

    public static RawNullableMemoryPack[] From(RawNullable[] values) =>
        Array.ConvertAll(values, RawNullableMemoryPack.From);

    // CollectionsTest's other collections of T?: an element at a time into a collection of the same size, what a
    // model that holds T? does before MemoryPack writes it (the MemoryPackConverted cases time these).

    public static Queue<SafeNullable<T>> From<T>(Queue<T?> values) where T : unmanaged
    {
        var result = new Queue<SafeNullable<T>>(values.Count);
        foreach (var value in values) result.Enqueue(value);
        return result;
    }

    public static HashSet<SafeNullable<T>> From<T>(HashSet<T?> values) where T : unmanaged
    {
        var result = new HashSet<SafeNullable<T>>(values.Count);
        foreach (var value in values) result.Add(value);
        return result;
    }

    public static SafeNullable<T>[,] From<T>(T?[,] values) where T : unmanaged
    {
        var result = new SafeNullable<T>[values.GetLength(0), values.GetLength(1)];
        for (int row = 0; row < values.GetLength(0); row++)
        for (int column = 0; column < values.GetLength(1); column++)
            result[row, column] = values[row, column];
        return result;
    }

    public static LinkedList<SafeNullable<T>> From<T>(LinkedList<T?> values) where T : unmanaged
    {
        var result = new LinkedList<SafeNullable<T>>();
        foreach (var value in values) result.AddLast(value);
        return result;
    }

    public static Collection<SafeNullable<T>> From<T>(Collection<T?> values) where T : unmanaged
    {
        var result = new List<SafeNullable<T>>(values.Count);
        foreach (var value in values) result.Add(value);
        return new Collection<SafeNullable<T>>(result);
    }

    public static ConcurrentQueue<SafeNullable<T>> From<T>(ConcurrentQueue<T?> values) where T : unmanaged
    {
        var result = new ConcurrentQueue<SafeNullable<T>>();
        foreach (var value in values) result.Enqueue(value);
        return result;
    }

    public static Memory<SafeNullable<T>> From<T>(Memory<T?> values) where T : unmanaged
    {
        var span = values.Span;
        var result = new SafeNullable<T>[span.Length];
        for (int i = 0; i < span.Length; i++) result[i] = span[i];
        return result;
    }

    public static IList<SafeNullable<T>> From<T>(IList<T?> values) where T : unmanaged
    {
        var result = new List<SafeNullable<T>>(values.Count);
        foreach (var value in values) result.Add(value);
        return result;
    }

    /// <summary>A MemoryPack result as the nullable data the other libraries read; any other result as it is.</summary>
    public static object ReadBack(object result) => result switch
    {
        SafeNullable<long>[] longs => Array.ConvertAll(longs, v => v.ToNullable()),
        SafeNullable<decimal>[] decimals => Array.ConvertAll(decimals, v => v.ToNullable()),
        // The elements in order, which is how Verify compares two collections of any types.
        SafeNullable<int>[,] grid => grid.Cast<SafeNullable<int>>().Select(v => v.ToNullable()).ToList(),
        Memory<SafeNullable<int>> memory => new Memory<int?>(Array.ConvertAll(memory.ToArray(), v => v.ToNullable())),
        IEnumerable<SafeNullable<int>> ints => ints.Select(v => v.ToNullable()).ToList(),
        SafeNullable<SimpleStruct> single => single.ToNullable(),
        SafeNullable<SimpleStruct>[] structs => Array.ConvertAll(structs, v => v.ToNullable()),
        SafeNullable<Workloads.Vec3>[] vectors => Array.ConvertAll(vectors, v => v.ToNullable()),
        NullableFieldsMemoryPack one => one.ToNullable(),
        NullableFieldsMemoryPack[] many => Array.ConvertAll(many, v => v.ToNullable()),
        RawNullableMemoryPack one => one.ToNullable(),
        RawNullableMemoryPack[] many => Array.ConvertAll(many, v => v.ToNullable()),
        _ => result,
    };
}

/// <summary>
/// CollectionsTest's collections of a raw <c>int?</c> that are neither an array nor a list (the v3 build's too):
/// 1,000 elements, every fifth one null (the values of its <c>List&lt;int?&gt;</c>). A set holds null once, so the
/// <c>HashSet</c> has it as its first element only.
/// </summary>
public static class NullableIntCollections
{
    private const int N = 1000;

    private static IEnumerable<int?> Values() => Enumerable.Range(0, N).Select(i => i % 5 == 0 ? null : (int?)i * 7);

    public static Queue<int?> Queue() => new(Values());
    public static HashSet<int?> HashSet() => new(Enumerable.Range(0, N).Select(i => i == 0 ? null : (int?)i * 7));
    public static LinkedList<int?> LinkedList() => new(Values());
    public static Collection<int?> Collection() => new(Values().ToList());
    public static ConcurrentQueue<int?> ConcurrentQueue() => new(Values());
    public static Memory<int?> Memory() => Values().ToArray();
    public static IList<int?> IList() => Values().ToList();

    /// <summary>40 rows of 25.</summary>
    public static int?[,] Grid()
    {
        var grid = new int?[40, 25];
        int i = 0;
        foreach (var value in Values())
        {
            grid[i / 25, i % 25] = value;
            i++;
        }

        return grid;
    }
}

/// <summary>NullableFields.Priority: zero is proto3's "unspecified", which the data never uses.</summary>
public enum NullablePriority
{
    Unspecified = 0,
    Low = 1,
    Normal = 2,
    High = 3,
}
