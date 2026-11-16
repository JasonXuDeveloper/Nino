// The other forms of the nullable cases: NinoNullable<T> is v4's, so the v3 build leaves this file out.
#if !NINO_BENCH_V3
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using MemoryPack;
using Nino.Core;

#nullable disable
namespace Nino.Benchmark;

/// <summary>
/// <see cref="NullableFields"/> with <see cref="NinoNullable{T}"/> members: what a user who picks Nino's form of a
/// nullable value writes (each member its memory, two <c>T</c>: not the bytes of <see cref="NullableFields"/>).
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
///  - <b>NinoNullable</b>: the user declares <see cref="NinoNullable{T}"/> (Nino.Core) instead of <c>T?</c>: a raw
///    struct written as its memory, two <c>T</c> a value and always clean, so a value, an array of it and a raw struct
///    that holds it are one block copy (not the bytes of <c>T?</c>). Safe.
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

    // CollectionsTest's other collections of T? (CollectionsTest.NullableCollections.cs): NinoNullable copies, made
    // outside the timed region, and the way back from MemoryPack's SafeNullable, inside it (the MemoryPackConverted
    // cases; SafeNullables.From is the way there), an element at a time into a collection of the same size.

    public static Queue<NinoNullable<T>> Nino<T>(Queue<T?> values) where T : unmanaged =>
        new(values.Select(v => (NinoNullable<T>)v));

    public static HashSet<NinoNullable<T>> Nino<T>(HashSet<T?> values) where T : unmanaged =>
        new(values.Select(v => (NinoNullable<T>)v));

    public static NinoNullable<T>[,] Nino<T>(T?[,] values) where T : unmanaged
    {
        var result = new NinoNullable<T>[values.GetLength(0), values.GetLength(1)];
        for (int row = 0; row < values.GetLength(0); row++)
        for (int column = 0; column < values.GetLength(1); column++)
            result[row, column] = values[row, column];
        return result;
    }

    public static LinkedList<NinoNullable<T>> Nino<T>(LinkedList<T?> values) where T : unmanaged =>
        new(values.Select(v => (NinoNullable<T>)v));

    public static Collection<NinoNullable<T>> Nino<T>(Collection<T?> values) where T : unmanaged =>
        new(values.Select(v => (NinoNullable<T>)v).ToList());

    public static ConcurrentQueue<NinoNullable<T>> Nino<T>(ConcurrentQueue<T?> values) where T : unmanaged =>
        new(values.Select(v => (NinoNullable<T>)v));

    public static Memory<NinoNullable<T>> Nino<T>(Memory<T?> values) where T : unmanaged =>
        Nino(values.ToArray());

    public static IList<NinoNullable<T>> Nino<T>(IList<T?> values) where T : unmanaged =>
        values.Select(v => (NinoNullable<T>)v).ToList();

    public static Queue<T?> ToNullable<T>(Queue<SafeNullable<T>> values) where T : unmanaged
    {
        var result = new Queue<T?>(values.Count);
        foreach (var value in values) result.Enqueue(value.ToNullable());
        return result;
    }

    public static HashSet<T?> ToNullable<T>(HashSet<SafeNullable<T>> values) where T : unmanaged
    {
        var result = new HashSet<T?>(values.Count);
        foreach (var value in values) result.Add(value.ToNullable());
        return result;
    }

    public static T?[,] ToNullable<T>(SafeNullable<T>[,] values) where T : unmanaged
    {
        var result = new T?[values.GetLength(0), values.GetLength(1)];
        for (int row = 0; row < values.GetLength(0); row++)
        for (int column = 0; column < values.GetLength(1); column++)
            result[row, column] = values[row, column].ToNullable();
        return result;
    }

    public static LinkedList<T?> ToNullable<T>(LinkedList<SafeNullable<T>> values) where T : unmanaged
    {
        var result = new LinkedList<T?>();
        foreach (var value in values) result.AddLast(value.ToNullable());
        return result;
    }

    public static Collection<T?> ToNullable<T>(Collection<SafeNullable<T>> values) where T : unmanaged
    {
        var result = new List<T?>(values.Count);
        foreach (var value in values) result.Add(value.ToNullable());
        return new Collection<T?>(result);
    }

    public static ConcurrentQueue<T?> ToNullable<T>(ConcurrentQueue<SafeNullable<T>> values) where T : unmanaged
    {
        var result = new ConcurrentQueue<T?>();
        foreach (var value in values) result.Enqueue(value.ToNullable());
        return result;
    }

    public static Memory<T?> ToNullable<T>(Memory<SafeNullable<T>> values) where T : unmanaged
    {
        var span = values.Span;
        var result = new T?[span.Length];
        for (int i = 0; i < span.Length; i++) result[i] = span[i].ToNullable();
        return result;
    }

    public static IList<T?> ToNullable<T>(IList<SafeNullable<T>> values) where T : unmanaged
    {
        var result = new List<T?>(values.Count);
        foreach (var value in values) result.Add(value.ToNullable());
        return result;
    }

    /// <summary>A result of these forms as the nullable data the other cases read; any other result as it is.</summary>
    public static object ReadBack(object result) => result switch
    {
        NinoNullable<long>[] longs => Array.ConvertAll(longs, v => v.ToNullable()),
        NinoNullable<int>[,] grid => grid.Cast<NinoNullable<int>>().Select(v => v.ToNullable()).ToList(),
        Memory<NinoNullable<int>> memory => new Memory<int?>(Array.ConvertAll(memory.ToArray(), v => v.ToNullable())),
        IEnumerable<NinoNullable<int>> ints => ints.Select(v => v.ToNullable()).ToList(),
        NinoNullable<decimal>[] decimals => Array.ConvertAll(decimals, v => v.ToNullable()),
        NinoNullable<SimpleStruct> single => single.ToNullable(),
        NinoNullable<SimpleStruct>[] structs => Array.ConvertAll(structs, v => v.ToNullable()),
        NinoNullable<Workloads.Vec3>[] vectors => Array.ConvertAll(vectors, v => v.ToNullable()),
        NullableFieldsNino one => one.ToNullable(),
        NullableFieldsNino[] many => Array.ConvertAll(many, v => v.ToNullable()),
        NullableFieldsUnsafe one => one.ToNullable(),
        NullableFieldsUnsafe[] many => Array.ConvertAll(many, v => v.ToNullable()),
        RawNullableNino one => one.ToNullable(),
        RawNullableNino[] many => Array.ConvertAll(many, v => v.ToNullable()),
        _ => SafeNullables.ReadBack(result),
    };
}
#endif
