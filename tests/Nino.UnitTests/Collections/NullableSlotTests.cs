using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nino.Core;
using Nino.Core.Internal;

#nullable disable
namespace Nino.UnitTests;

/// <summary>A raw struct with padding (5 bytes of data in 8): its nullable slot is 12 bytes.</summary>
public struct SlotPoint : IEquatable<SlotPoint>
{
    public int X;
    public byte Y;

    public bool Equals(SlotPoint other) => X == other.X && Y == other.Y;
    public override bool Equals(object obj) => obj is SlotPoint other && Equals(other);
    public override int GetHashCode() => X * 31 + Y;
}

/// <summary>
/// Every sequence of a raw <c>T?</c> or a <see cref="NinoNullable{T}"/> writes its elements as slots of a fixed size
/// (spec 6.2), the bytes of a <c>T?[]</c> of them: one block through NinoElements where the collection is
/// contiguous, a rented copy where it is not. A single value (a member, a dictionary's value, a pair's or a tuple's
/// item) stays a presence byte and the value when present.
/// </summary>
[TestClass]
public class NullableSlotTests
{
    /// <summary>0, 1, 3 elements, and enough for every vector width and a queue that wraps; every third is null.</summary>
    private static readonly int[] Lengths = { 0, 1, 2, 3, 7, 37, 300 };

    private static T?[] Values<T>(int length, Func<int, T> value) where T : struct =>
        Enumerable.Range(0, length).Select(i => i % 3 == 1 ? (T?)null : value(i + 1)).ToArray();

    private static NinoNullable<T>[] Wrapped<T>(T?[] values) where T : unmanaged =>
        values.Select(v => (NinoNullable<T>)v).ToArray();

    private static readonly Func<int, int> Int = i => i * 0x01010101;
    private static readonly Func<int, long> Long = i => i * 0x0101010101L;
    private static readonly Func<int, decimal> Decimal = i => i * 1.25m;
    private static readonly Func<int, SlotPoint> Point = i => new SlotPoint { X = i * 7, Y = (byte)i };
    private static readonly Func<int, byte> Byte = i => (byte)i;
    private static readonly Func<int, bool> Bool = i => i % 2 == 0;
    private static readonly Func<int, DateTime> Date = i => new DateTime(2020, 1, 1).AddHours(i);

    [TestMethod]
    public void IntSequences() => Each(Int, 8, comparable: true);

    [TestMethod]
    public void LongSequences() => Each(Long, 16, comparable: true);

    [TestMethod]
    public void DecimalSequences() => Each(Decimal, 24, comparable: true);

    [TestMethod]
    public void StructSequences() => Each(Point, 12, comparable: false);

    [TestMethod]
    public void ByteSequences() => Each(Byte, 2, comparable: true);

    [TestMethod]
    public void BoolSequences() => Each(Bool, 2, comparable: true);

    [TestMethod]
    public void DateTimeSequences() => Each(Date, 16, comparable: true);

    [TestMethod]
    public void NinoNullableIntSequences() => EachWrapped(Int, 8);

    [TestMethod]
    public void NinoNullableLongSequences() => EachWrapped(Long, 16);

    [TestMethod]
    public void NinoNullableDecimalSequences() => EachWrapped(Decimal, 24);

    [TestMethod]
    public void NinoNullableStructSequences() => EachWrapped(Point, 12);

    private static void Each<T>(Func<int, T> value, int stride, bool comparable) where T : struct
    {
        foreach (int length in Lengths)
            Sequences(Values(length, value), stride, comparable);
    }

    private static void EachWrapped<T>(Func<int, T> value, int stride) where T : unmanaged
    {
        foreach (int length in Lengths)
        {
            var values = Values(length, value);
            var wrapped = Wrapped(values);
            // The bytes of the T? run.
            CollectionAssert.AreEqual(NinoSerializer.Serialize(values), NinoSerializer.Serialize(wrapped));
            Sequences(wrapped, stride, comparable: false);
        }
    }

    /// <summary>
    /// Every sequence of <paramref name="items"/> is the bytes of the array (a count, then <paramref name="stride"/>
    /// bytes an element), reads back to the same elements, into a new collection and into one that holds others.
    /// </summary>
    private static void Sequences<T>(T[] items, int stride, bool comparable)
    {
        byte[] array = NinoSerializer.Serialize(items);
        Assert.AreEqual(4 + items.Length * stride, array.Length, typeof(T).Name);
        CollectionAssert.AreEqual(items, NinoDeserializer.Deserialize<T[]>(array));

        Same(array, items.ToList(), l => l);
        Same(array, new Queue<T>(items), q => q);
        Same(array, WrappedQueue(items), q => q);
        Same(array, new Stack<T>(items), s => s.Reverse());
        Same(array, new LinkedList<T>(items), l => l);
        Same(array, new Collection<T>(items.ToList()), c => c);
        Same(array, new ObservableCollection<T>(items), c => c);
        Same(array, (IList<T>)items.ToList(), l => l);
        Same(array, (ICollection<T>)items.ToList(), l => l);
        Same(array, (IEnumerable<T>)items.ToList(), l => l);
        Same(array, (IEnumerable<T>)new LinkedList<T>(items), l => l);
        Same(array, (IReadOnlyList<T>)items.ToList(), l => l);
        Same(array, (IReadOnlyCollection<T>)items.ToList(), l => l);
        Same(array, new Memory<T>(items), m => m.ToArray());
        Same(array, new ReadOnlyMemory<T>(items), m => m.ToArray());
        Same(array, new ArraySegment<T>(items), s => s);
        Same(array, new ReadOnlySequence<T>(items), s => s.ToArray());
        Same(array, new ConcurrentQueue<T>(items), q => q);
        Same(array, new ConcurrentStack<T>(items), s => s.Reverse());
        Same(array, new BlockingCollection<T>(new ConcurrentQueue<T>(items)), c => c);
        Same(array, ImmutableArray.Create(items), a => a);
        Same(array, ImmutableList.Create(items), l => l);
        Same(array, ImmutableQueue.Create(items), q => q);
        Same(array, ImmutableStack.Create(items), s => s.Reverse());

        // A bag's order is its own: the same elements in slots.
        var bag = NinoSerializer.Serialize(new ConcurrentBag<T>(items));
        Assert.AreEqual(array.Length, bag.Length);
        CollectionAssert.AreEquivalent(items, NinoDeserializer.Deserialize<ConcurrentBag<T>>(bag).ToArray());

        // Sets hold each element once, in their own order: the array of what they enumerate.
        var set = new HashSet<T>(items);
        byte[] distinct = NinoSerializer.Serialize(set.ToArray());
        Same(distinct, set, s => s);
        Same(distinct, (ISet<T>)set, s => s);
        var immutableSet = ImmutableHashSet.CreateRange(items);
        byte[] immutableBytes = NinoSerializer.Serialize(immutableSet);
        Assert.AreEqual(distinct.Length, immutableBytes.Length);
        Assert.IsTrue(immutableSet.SetEquals(NinoDeserializer.Deserialize<ImmutableHashSet<T>>(immutableBytes)));
        if (comparable)
        {
            var sorted = new SortedSet<T>(items);
            byte[] ordered = NinoSerializer.Serialize(sorted.ToArray());
            Same(ordered, sorted, s => s);
            Same(ordered, ImmutableSortedSet.CreateRange(items), s => s);
        }

        // Multi-dimensional arrays: the lengths, then the slots in row order.
        if (items.Length % 3 == 0 && items.Length != 0)
        {
            var grid = new T[items.Length / 3, 3];
            for (int i = 0; i < items.Length; i++)
                grid[i / 3, i % 3] = items[i];
            byte[] bytes = NinoSerializer.Serialize(grid);
            CollectionAssert.AreEqual(array.AsSpan(4).ToArray(), bytes.AsSpan(bytes.Length - items.Length * stride).ToArray());
            CollectionAssert.AreEqual(items, NinoDeserializer.Deserialize<T[,]>(bytes).Cast<T>().ToArray());
        }

        // A lookup's groups: each a count, the key, then the slots.
        var lookup = items.ToLookup(_ => 7);
        byte[] groups = NinoSerializer.Serialize(lookup);
        if (items.Length != 0)
        {
            Assert.AreEqual(4 + 4 + 4 + items.Length * stride, groups.Length);
            CollectionAssert.AreEqual(items, NinoDeserializer.Deserialize<ILookup<int, T>>(groups)[7].ToArray());
        }

        // Nested: each inner sequence its own count and slots.
        byte[] nested = NinoSerializer.Serialize(new List<Queue<T>> { new Queue<T>(items), new Queue<T>(items) });
        Assert.AreEqual(4 + 2 * array.Length, nested.Length);
        CollectionAssert.AreEqual(array, nested.AsSpan(4, array.Length).ToArray());
        CollectionAssert.AreEqual(items, NinoDeserializer.Deserialize<List<Queue<T>>>(nested)[1].ToArray());
    }

    /// <summary>A queue whose elements wrap around the end of its backing array.</summary>
    private static Queue<T> WrappedQueue<T>(T[] items)
    {
        var queue = new Queue<T>(items.Length + 2);
        queue.Enqueue(default);
        queue.Enqueue(default);
        queue.Enqueue(default);
        queue.Dequeue();
        queue.Dequeue();
        queue.Dequeue();
        foreach (var item in items)
            queue.Enqueue(item);
        return queue;
    }

    private static void Same<TCollection, T>(byte[] expected, TCollection value, Func<TCollection, IEnumerable<T>> items)
    {
        string name = typeof(TCollection).Name + " of " + typeof(T).Name;
        byte[] bytes = NinoSerializer.Serialize(value);
        CollectionAssert.AreEqual(expected, bytes, name);

        var read = NinoDeserializer.Deserialize<TCollection>(bytes);
        CollectionAssert.AreEqual(items(value).ToArray(), items(read).ToArray(), name);
        CollectionAssert.AreEqual(expected, NinoSerializer.Serialize(read), name + " written again");

        // Into the collection just read (reuse), which holds as many elements, and at an odd offset.
        if (typeof(TCollection).IsValueType || read == null)
            return;
        byte[] shifted = new byte[bytes.Length + 1];
        bytes.CopyTo(shifted, 1);
        NinoDeserializer.Deserialize(shifted.AsSpan(1), ref read);
        CollectionAssert.AreEqual(items(value).ToArray(), items(read).ToArray(), name + " reused");
    }

    /// <summary>The generated formatters (interceptors), where the element type is named.</summary>
    [TestMethod]
    public void GeneratedFormattersWriteSlots()
    {
        int?[] ints = { 1, null, 3 };
        byte[] array = Rt.FromHex("03000000 01000000 01000000 00000000 00000000 01000000 03000000");
        CollectionAssert.AreEqual(array, NinoSerializer.Serialize(ints));
        CollectionAssert.AreEqual(array, NinoSerializer.Serialize(new List<int?>(ints)));
        CollectionAssert.AreEqual(array, NinoSerializer.Serialize(new Queue<int?>(ints)));
        CollectionAssert.AreEqual(array, NinoSerializer.Serialize(new Stack<int?>(ints)));
        CollectionAssert.AreEqual(array, NinoSerializer.Serialize(new LinkedList<int?>(ints)));
        CollectionAssert.AreEqual(array, NinoSerializer.Serialize(new HashSet<int?>(ints)));
        CollectionAssert.AreEqual(Rt.FromHex("03000000 00000000 00000000 01000000 01000000 01000000 03000000"),
            NinoSerializer.Serialize(new SortedSet<int?>(ints)));
        CollectionAssert.AreEqual(array, NinoSerializer.Serialize<IList<int?>>(ints));
        CollectionAssert.AreEqual(array, NinoSerializer.Serialize<IEnumerable<int?>>(new Queue<int?>(ints)));
        CollectionAssert.AreEqual(array, NinoSerializer.Serialize<IReadOnlyList<int?>>(ints));
        CollectionAssert.AreEqual(array, NinoSerializer.Serialize(new Memory<int?>(ints)));
        CollectionAssert.AreEqual(array, NinoSerializer.Serialize(new ReadOnlyMemory<int?>(ints)));
        CollectionAssert.AreEqual(array, NinoSerializer.Serialize(new ArraySegment<int?>(ints)));
        CollectionAssert.AreEqual(array, NinoSerializer.Serialize(new ConcurrentQueue<int?>(ints)));
        CollectionAssert.AreEqual(array, NinoSerializer.Serialize(ImmutableArray.Create(ints)));
        CollectionAssert.AreEqual(array, NinoSerializer.Serialize(ImmutableQueue.Create(ints)));
        CollectionAssert.AreEqual(Rt.FromHex("01000000 03000000").Concat(array.Skip(4)).ToArray(),
            NinoSerializer.Serialize(new int?[,] { { 1, null, 3 } }));

        NinoNullable<long>[] longs = { 1, default };
        byte[] wide = Rt.FromHex("02000000 0100000000000000 0100000000000000 0000000000000000 0000000000000000");
        CollectionAssert.AreEqual(wide, NinoSerializer.Serialize(longs));
        CollectionAssert.AreEqual(wide, NinoSerializer.Serialize(new Queue<NinoNullable<long>>(longs)));
        CollectionAssert.AreEqual(wide, NinoSerializer.Serialize(new HashSet<NinoNullable<long>>(longs)));
        CollectionAssert.AreEqual(wide, NinoSerializer.Serialize(new LinkedList<NinoNullable<long>>(longs)));
        CollectionAssert.AreEqual(wide, NinoSerializer.Serialize<IList<NinoNullable<long>>>(longs));
        CollectionAssert.AreEqual(wide, NinoSerializer.Serialize(new Queue<long?>(new long?[] { 1, null })));

        SlotPoint?[] points = { new SlotPoint { X = 3, Y = 4 }, null };
        byte[] padded = Rt.FromHex("02000000 01000000 03000000 04000000 00000000 00000000 00000000");
        CollectionAssert.AreEqual(padded, NinoSerializer.Serialize(points));
        CollectionAssert.AreEqual(padded, NinoSerializer.Serialize(new Queue<SlotPoint?>(points)));
        CollectionAssert.AreEqual(padded, NinoSerializer.Serialize(new Stack<SlotPoint?>(points)));
        CollectionAssert.AreEqual(padded, NinoSerializer.Serialize(new Memory<SlotPoint?>(points)));
        CollectionAssert.AreEqual(padded, NinoSerializer.Serialize(new Queue<NinoNullable<SlotPoint>>(Wrapped(points))));

        decimal?[] decimals = { 1m, null };
        byte[] three = Rt.FromHex("02000000 0100000000000000 00000000 00000000 01000000 00000000" +
                                  "0000000000000000 0000000000000000 0000000000000000");
        CollectionAssert.AreEqual(three, NinoSerializer.Serialize(decimals));
        CollectionAssert.AreEqual(three, NinoSerializer.Serialize(new Queue<decimal?>(decimals)));
        CollectionAssert.AreEqual(three, NinoSerializer.Serialize(new LinkedList<decimal?>(decimals)));
        CollectionAssert.AreEqual(three, NinoSerializer.Serialize(new Queue<NinoNullable<decimal>>(Wrapped(decimals))));
    }

    /// <summary>
    /// A single value keeps its own encoding (a presence byte, then the value when present), whatever holds it: a
    /// member, a dictionary's key or value, a pair's or a tuple's item, a priority queue's element.
    /// </summary>
    [TestMethod]
    public void SingleValuesKeepThePresenceByte()
    {
        CollectionAssert.AreEqual(Rt.FromHex("01 07000000"), NinoSerializer.Serialize((int?)7));
        CollectionAssert.AreEqual(Rt.FromHex("00"), NinoSerializer.Serialize((int?)null));
        CollectionAssert.AreEqual(Rt.FromHex("01 07000000"), NinoSerializer.Serialize((NinoNullable<int>)7));
        CollectionAssert.AreEqual(Rt.FromHex("02000000 01000000 01 07000000 02000000 00"),
            NinoSerializer.Serialize(new Dictionary<int, int?> { { 1, 7 }, { 2, null } }));
        CollectionAssert.AreEqual(Rt.FromHex("02000000 01000000 01 07000000 02000000 00"),
            NinoSerializer.Serialize(new SortedDictionary<int, int?> { { 1, 7 }, { 2, null } }));
        CollectionAssert.AreEqual(Rt.FromHex("02000000 01000000 01 07000000 02000000 00"),
            NinoSerializer.Serialize(new Dictionary<int, NinoNullable<int>> { { 1, 7 }, { 2, default } }));
        CollectionAssert.AreEqual(Rt.FromHex("01000000 00"),
            NinoSerializer.Serialize(new KeyValuePair<int, int?>(1, null)));
        CollectionAssert.AreEqual(Rt.FromHex("02000000 01000000 01 07000000 02000000 00"),
            NinoSerializer.Serialize(new[] { new KeyValuePair<int, int?>(1, 7), new KeyValuePair<int, int?>(2, null) }));
        CollectionAssert.AreEqual(Rt.FromHex("01000000 00"), NinoSerializer.Serialize((1, (int?)null)));
        CollectionAssert.AreEqual(Rt.FromHex("02000000 01000000 01 07000000 02000000 00"),
            NinoSerializer.Serialize(new List<(int, int?)> { (1, 7), (2, null) }));

        var read = NinoDeserializer.Deserialize<Dictionary<int, int?>>(
            Rt.FromHex("02000000 01000000 01 07000000 02000000 00"));
        Assert.AreEqual(7, read[1]);
        Assert.IsNull(read[2]);
        // The fewest bytes a pair takes is its key and a presence byte: 300 pairs of absent values are 1,500 bytes.
        var absent = Enumerable.Range(0, 300).ToDictionary(i => i, _ => (long?)null);
        byte[] bytes = NinoSerializer.Serialize(absent);
        Assert.AreEqual(4 + 300 * 5, bytes.Length);
        Assert.AreEqual(300, NinoDeserializer.Deserialize<Dictionary<int, long?>>(bytes).Count);
#if NET8_0_OR_GREATER
        var queue = new PriorityQueue<int?, int>();
        queue.Enqueue(null, 1);
        queue.Enqueue(7, 2);
        CollectionAssert.AreEqual(Rt.FromHex("02000000 00 01000000 01 07000000 02000000"),
            NinoSerializer.Serialize(queue));
        Assert.AreEqual(2, NinoDeserializer.Deserialize<PriorityQueue<int?, int>>(NinoSerializer.Serialize(queue)).Count);
#endif
    }

    /// <summary>One element written or read apart from its sequence is its slot too, not its single encoding.</summary>
    [TestMethod]
    public void OneElementIsItsSlot()
    {
        One<int?, RawNullableFormatter<int>>(new int?[] { 5, null, -1 }, 8);
        One<decimal?, RawNullableFormatter<decimal>>(new decimal?[] { 5m, null, -1m }, 24);
        One<bool?, RawNullableFormatter<bool>>(new bool?[] { true, null, false }, 2);
        One<SlotPoint?, RawNullableFormatter<SlotPoint>>(new SlotPoint?[] { new SlotPoint { X = 1, Y = 2 }, null }, 12);
        One<NinoNullable<int>, NinoNullableFormatter<int>>(new NinoNullable<int>[] { 5, default, -1 }, 8);
        One<NinoNullable<decimal>, NinoNullableFormatter<decimal>>(new NinoNullable<decimal>[] { 5m, default }, 24);
        // Others go through their formatter.
        Assert.AreEqual(-1, NinoElements.SlotStride<int, UnmanagedFormatter<int>>());
        Assert.AreEqual(-1, NinoElements.SlotStride<string, StringFormatter>());
        Assert.AreEqual(-1, NinoElements.SlotStride<DateTimeOffset?,
            NullableFormatter<DateTimeOffset, DateTimeOffsetFormatter>>());
    }

    private static void One<T, TFormatter>(T[] items, int stride) where TFormatter : struct, INinoFormatter<T>
    {
        Assert.AreEqual(stride, NinoElements.SlotStride<T, TFormatter>());
        Assert.AreEqual(stride, NinoElements.BlockStride<T, TFormatter>());
        Assert.AreEqual(stride, NinoElements.MinWireSize<T, TFormatter>());
        Assert.AreEqual(1, NinoElements.MinSingleSize<T, TFormatter>());

        var buffer = new NinoArrayBufferWriter();
        var writer = new Writer(buffer);
        NinoElements.Write<T, TFormatter>(items, ref writer);
        writer.Flush();
        byte[] block = buffer.WrittenSpan.ToArray();
        Assert.AreEqual(items.Length * stride, block.Length);

        buffer = new NinoArrayBufferWriter();
        writer = new Writer(buffer);
        foreach (var item in items)
            NinoElements.WriteOne<T, TFormatter>(item, ref writer);
        writer.Flush();
        CollectionAssert.AreEqual(block, buffer.WrittenSpan.ToArray());

        var reader = new Reader(block);
        var read = new T[items.Length];
        for (int i = 0; i < read.Length; i++)
            NinoElements.ReadOne<T, TFormatter>(ref reader, ref read[i]);
        Assert.IsTrue(reader.Eof);
        CollectionAssert.AreEqual(items, read);

        // One element of fewer bytes than its slot is the end of the data.
        Assert.ThrowsExactly<NinoDataException>(() =>
        {
            var truncated = new Reader(block.AsSpan(0, stride - 1));
            T value = default;
            NinoElements.ReadOne<T, TFormatter>(ref truncated, ref value);
        });
    }

    /// <summary>
    /// What every sequence rejects: a presence byte other than 0 or 1, a count the bytes left cannot hold (checked
    /// at the slot's size before anything is allocated), and a last slot cut short.
    /// </summary>
    [TestMethod]
    public void HostileSlotsAreRejected()
    {
        var items = Values(40, Int);
        byte[] bytes = NinoSerializer.Serialize(items);

        Rejected<int?[]>(bytes);
        Rejected<List<int?>>(bytes);
        Rejected<Queue<int?>>(bytes);
        Rejected<Stack<int?>>(bytes);
        Rejected<HashSet<int?>>(NinoSerializer.Serialize(new HashSet<int?>(items)));
        Rejected<SortedSet<int?>>(NinoSerializer.Serialize(new SortedSet<int?>(items)));
        Rejected<LinkedList<int?>>(bytes);
        Rejected<Collection<int?>>(bytes);
        Rejected<IList<int?>>(bytes);
        Rejected<IEnumerable<int?>>(bytes);
        Rejected<ISet<int?>>(NinoSerializer.Serialize(new HashSet<int?>(items)));
        Rejected<Memory<int?>>(bytes);
        Rejected<ReadOnlyMemory<int?>>(bytes);
        Rejected<ArraySegment<int?>>(bytes);
        Rejected<ReadOnlySequence<int?>>(bytes);
        Rejected<ConcurrentQueue<int?>>(bytes);
        Rejected<ConcurrentStack<int?>>(bytes);
        Rejected<ConcurrentBag<int?>>(bytes);
        Rejected<BlockingCollection<int?>>(bytes);
        Rejected<ImmutableArray<int?>>(bytes);
        Rejected<ImmutableList<int?>>(bytes);
        Rejected<ImmutableQueue<int?>>(bytes);
        Rejected<ImmutableStack<int?>>(bytes);
        Rejected<ImmutableHashSet<int?>>(NinoSerializer.Serialize(new HashSet<int?>(items)));
        Rejected<Queue<NinoNullable<int>>>(bytes);
        Rejected<HashSet<NinoNullable<int>>>(NinoSerializer.Serialize(new HashSet<int?>(items)));
        Rejected<LinkedList<NinoNullable<int>>>(bytes);
        Rejected<IList<NinoNullable<int>>>(bytes);
        Rejected<Queue<long?>>(NinoSerializer.Serialize(Values(40, Long)));
        Rejected<Queue<decimal?>>(NinoSerializer.Serialize(Values(40, Decimal)));
        Rejected<Queue<NinoNullable<decimal>>>(NinoSerializer.Serialize(Values(40, Decimal)));
        Rejected<Queue<SlotPoint?>>(NinoSerializer.Serialize(Values(40, Point)));
        Rejected<Queue<bool?>>(NinoSerializer.Serialize(Values(40, Bool)));
    }

    private static void Rejected<TCollection>(byte[] bytes)
    {
        string name = typeof(TCollection).Name;
        int count = BitConverter.ToInt32(bytes, 0);
        int stride = (bytes.Length - 4) / count;
        Assert.IsNotNull(NinoDeserializer.Deserialize<TCollection>(bytes), name);

        // A presence byte of 2, in the first, a middle and the last slot.
        foreach (int slot in new[] { 0, count / 2, count - 1 })
        {
            byte[] invalid = (byte[])bytes.Clone();
            invalid[4 + slot * stride] = 2;
            Assert.ThrowsExactly<NinoDataException>(() => NinoDeserializer.Deserialize<TCollection>(invalid),
                name + " flag at " + slot);
        }

        // The last slot cut short, and cut to the presence byte a single value would take.
        Assert.ThrowsExactly<NinoDataException>(
            () => NinoDeserializer.Deserialize<TCollection>(bytes.AsSpan(0, bytes.Length - 1)), name + " truncated");
        Assert.ThrowsExactly<NinoDataException>(
            () => NinoDeserializer.Deserialize<TCollection>(bytes.AsSpan(0, bytes.Length - stride + 1)), name);

        // A count the data cannot hold: a billion elements claimed over the bytes of 40.
        byte[] claimed = (byte[])bytes.Clone();
        BitConverter.GetBytes(1_000_000_000).CopyTo(claimed, 0);
        long before = GC.GetAllocatedBytesForCurrentThread();
        Assert.ThrowsExactly<NinoDataException>(() => NinoDeserializer.Deserialize<TCollection>(claimed), name);
        Assert.IsTrue(GC.GetAllocatedBytesForCurrentThread() - before < 1 << 20, name + " allocated for the claim");

        // As many elements as a presence byte each would allow (the old bound) are rejected before allocation too.
        BitConverter.GetBytes(bytes.Length - 4).CopyTo(claimed, 0);
        Assert.ThrowsExactly<NinoDataException>(() => NinoDeserializer.Deserialize<TCollection>(claimed), name);
    }

    /// <summary>
    /// The value of an absent element is written as zeros whatever its memory holds (a default T? never assigned
    /// keeps old bytes behind its flag), from every collection.
    /// </summary>
    [TestMethod]
    public void AbsentValuesAreWrittenAsZeros()
    {
        var dirty = new int?[40];
        for (int i = 0; i < dirty.Length; i++)
        {
            dirty[i] = -1;
            if (i % 2 == 0)
                System.Runtime.CompilerServices.Unsafe.As<int?, byte>(ref dirty[i]) = 0;
        }

        byte[] clean = NinoSerializer.Serialize(Enumerable.Range(0, 40).Select(i => i % 2 == 0 ? (int?)null : -1).ToArray());
        CollectionAssert.AreEqual(clean, NinoSerializer.Serialize(dirty));
        CollectionAssert.AreEqual(clean, NinoSerializer.Serialize(new Queue<int?>(dirty)));
        CollectionAssert.AreEqual(clean, NinoSerializer.Serialize(new Stack<int?>(dirty)));
        CollectionAssert.AreEqual(clean, NinoSerializer.Serialize(new LinkedList<int?>(dirty)));
        CollectionAssert.AreEqual(clean, NinoSerializer.Serialize(new Memory<int?>(dirty)));
        CollectionAssert.AreEqual(clean, NinoSerializer.Serialize<IEnumerable<int?>>(new Queue<int?>(dirty)));
        CollectionAssert.AreEqual(clean, NinoSerializer.Serialize(new ConcurrentQueue<int?>(dirty)));
    }
}
