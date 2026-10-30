using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nino.Core;

namespace Nino.UnitTests;

/// <summary>A struct that holds itself through a list: nested data cannot be deferred (a struct has no slot).</summary>
[NinoType]
public struct UntrustedStructNode
{
    public int V;
    public List<UntrustedStructNode> Kids;
}

/// <summary>
/// 520 bytes in memory, 516 on the wire (the string takes its 4-byte header), not raw: a count of them is checked
/// against the bytes left with 1 byte each.
/// </summary>
[NinoType]
public struct UntrustedBigStruct
{
    public long A0, A1, A2, A3, A4, A5, A6, A7, A8, A9, A10, A11, A12, A13, A14, A15;
    public long B0, B1, B2, B3, B4, B5, B6, B7, B8, B9, B10, B11, B12, B13, B14, B15;
    public long C0, C1, C2, C3, C4, C5, C6, C7, C8, C9, C10, C11, C12, C13, C14, C15;
    public long D0, D1, D2, D3, D4, D5, D6, D7, D8, D9, D10, D11, D12, D13, D14, D15;
    public string S;
}

/// <summary>
/// 512 bytes, unmanaged; not raw in this project (WEAK_VERSION_TOLERANCE gives it a length-prefixed body): a count
/// of them is checked with 1 byte each.
/// </summary>
[NinoType]
public struct UntrustedBigValue : IComparable<UntrustedBigValue>
{
    public long A0, A1, A2, A3, A4, A5, A6, A7, A8, A9, A10, A11, A12, A13, A14, A15;
    public long B0, B1, B2, B3, B4, B5, B6, B7, B8, B9, B10, B11, B12, B13, B14, B15;
    public long C0, C1, C2, C3, C4, C5, C6, C7, C8, C9, C10, C11, C12, C13, C14, C15;
    public long D0, D1, D2, D3, D4, D5, D6, D7, D8, D9, D10, D11, D12, D13, D14, D15;

    public int CompareTo(UntrustedBigValue other) => A0.CompareTo(other.A0);
}

/// <summary>512 bytes, raw (unmanaged, not a [NinoType]): its own size on the wire.</summary>
public struct UntrustedRawBlock : IComparable<UntrustedRawBlock>
{
    public long A0, A1, A2, A3, A4, A5, A6, A7, A8, A9, A10, A11, A12, A13, A14, A15;
    public long B0, B1, B2, B3, B4, B5, B6, B7, B8, B9, B10, B11, B12, B13, B14, B15;
    public long C0, C1, C2, C3, C4, C5, C6, C7, C8, C9, C10, C11, C12, C13, C14, C15;
    public long D0, D1, D2, D3, D4, D5, D6, D7, D8, D9, D10, D11, D12, D13, D14, D15;

    public int CompareTo(UntrustedRawBlock other) => A0.CompareTo(other.A0);
}

/// <summary>Over 500 bytes in memory, 5 on the wire when <see cref="Extra"/> is null.</summary>
[NinoType]
public struct UntrustedSparse : IEquatable<UntrustedSparse>
{
    public int Id;
    public UntrustedBigValue? Extra;

    public bool Equals(UntrustedSparse other) =>
        Id == other.Id && Extra.HasValue == other.Extra.HasValue &&
        (!Extra.HasValue || Extra.Value.A0 == other.Extra.Value.A0 && Extra.Value.D15 == other.Extra.Value.D15);

    public override bool Equals(object obj) => obj is UntrustedSparse other && Equals(other);

    public override int GetHashCode() => Id;
}

/// <summary>A sealed class of 512 bytes: a presence byte on the wire, null when 0.</summary>
[NinoType]
public sealed class UntrustedBigClass
{
    public UntrustedBigValue Value;
}

/// <summary>
/// Data from an untrusted source: every malformed input throws InvalidOperationException with a message starting
/// "Nino:", never another exception, a crash, or a value holding memory the data did not write.
/// </summary>
[TestClass]
public class UntrustedInputTests
{
    private static void Invalid<T>(byte[] data)
    {
        var e = Assert.ThrowsExactly<NinoDataException>(() => NinoDeserializer.Deserialize<T>(data));
        StringAssert.StartsWith(e.Message, "Nino:");
    }

    private static byte[] Ints(params int[] values) => values.SelectMany(BitConverter.GetBytes).ToArray();

    [TestMethod]
    public void InvalidDataThrowsNinoDataException()
    {
        // One exception type for every rejected input, still an InvalidOperationException for code written before it.
        var e = Assert.ThrowsExactly<NinoDataException>(() => NinoDeserializer.Deserialize<List<int>>(new byte[] { 5, 0, 0, 0, 1 }));
        Assert.IsInstanceOfType(e, typeof(InvalidOperationException));
        StringAssert.StartsWith(e.Message, "Nino: ");
        // The caller's own mistakes are not invalid data.
        Assert.ThrowsExactly<ArgumentNullException>(() => NinoDeserializer.DeserializeBoxed(new byte[] { 0 }, null));
    }

    [TestMethod]
    public void DeepStructsThrowInsteadOfOverflowingTheStack()
    {
        // [V][count = 1] repeated: each struct holds the next, a million deep. Read on a 1 MB thread, which the
        // depth alone overflowed (the process crashed).
        var data = Enumerable.Repeat(Ints(7, 1), 1_000_000).SelectMany(b => b).ToArray();
        Exception thrown = null;
        var thread = new Thread(() =>
        {
            try
            {
                NinoDeserializer.Deserialize<UntrustedStructNode>(data);
            }
            catch (Exception e)
            {
                thrown = e;
            }
        }, 1 << 20);
        thread.Start();
        thread.Join();
        Assert.IsInstanceOfType<InvalidOperationException>(thrown);
        StringAssert.StartsWith(thrown.Message, "Nino:");

        // Nesting the writer accepts still round-trips.
        var node = new UntrustedStructNode { V = 0 };
        for (int i = 1; i < 100; i++)
            node = new UntrustedStructNode { V = i, Kids = new List<UntrustedStructNode> { node } };
        var back = NinoDeserializer.Deserialize<UntrustedStructNode>(NinoSerializer.Serialize(node));
        Assert.AreEqual(99, back.V);
    }

    [TestMethod]
    public void LengthsAreCheckedBeforeAllocating()
    {
        // A StringBuilder of 2^30 chars from 4 bytes.
        Invalid<StringBuilder>(Ints(0x3FFFFFFF));
        Invalid<StringBuilder>(Ints(-0x3FFFFFFF - 2));
        // Dimensions whose product overflows a long (to 0), or that the runtime cannot allocate even empty.
        Invalid<int[,,]>(Ints(1 << 22, 1 << 21, 1 << 21));
        Invalid<int[,,]>(Ints(0x4000, 0x2000, 0x2000));
        Invalid<int[,,]>(Ints(0x10000, 0x10000, 0));
        Assert.AreEqual(0, NinoDeserializer.Deserialize<int[,,]>(Ints(0, 0x10000, 0x10000)).Length);
    }

    [TestMethod]
    public void AFailedReadLeavesNoUninitializedMemory()
    {
        // 2,000 elements of 8 bytes claimed (16 KB, allocated uninitialized), 2,000 bytes given: rejected before
        // the array is made, so the existing value keeps no stale heap memory.
        var data = Ints(2000).Concat(new byte[2000]).ToArray();
        var list = new List<(int, int)>();
        Assert.ThrowsExactly<NinoDataException>(() => NinoDeserializer.Deserialize(data, ref list));
        Assert.IsTrue(list.All(x => x == default));
        var array = Array.Empty<(int, int)>();
        Assert.ThrowsExactly<NinoDataException>(() => NinoDeserializer.Deserialize(data, ref array));
        Assert.IsTrue(array.All(x => x == default));
        Invalid<Memory<long>>(data);
    }

    [TestMethod]
    public void ARejectedReadKeepsNewArraysOutOfTheSlot()
    {
        // 8,000 elements (allocated uninitialized from 6 KB), the last one invalid: the read rejects the data after it
        // allocated the new array, which must not reach the existing value (stale heap memory once the read stopped).
        const int n = 8000;
        byte[] Elements(Func<int, byte[]> element) =>
            Ints(n).Concat(Enumerable.Range(0, n).SelectMany(element)).ToArray();

        // Has-value bytes other than 0 or 1.
        var nullables = Elements(i => new byte[] { (byte)(i == n - 1 ? 2 : 0), 0, 0, 0, 0, 0, 0, 0 });
        var nullableArray = new int?[1];
        var old = nullableArray;
        Assert.ThrowsExactly<NinoDataException>(() => NinoDeserializer.Deserialize(nullables, ref nullableArray));
        Assert.AreSame(old, nullableArray);
        List<int?> nullableList = null;
        Assert.ThrowsExactly<NinoDataException>(() => NinoDeserializer.Deserialize(nullables, ref nullableList));
        Assert.IsNull(nullableList);

        // Bools other than 0 or 1, raw and in blocks.
        var bools = Elements(i => new[] { (byte)(i == n - 1 ? 2 : 1) });
        List<bool> boolList = null;
        Assert.ThrowsExactly<NinoDataException>(() => NinoDeserializer.Deserialize(bools, ref boolList));
        Assert.IsNull(boolList);
        var held = (new bool[1], 5);
        var heldOld = held.Item1;
        var heldData = bools.Concat(Ints(5)).ToArray();
        Assert.ThrowsExactly<NinoDataException>(() => NinoDeserializer.Deserialize(heldData, ref held));
        Assert.AreSame(heldOld, held.Item1);

        // Blocks with a bool are read into a zeroed array (kept off the recursive read path): what the read did not
        // reach is default, never the 0xCD of freed memory. 12,000 of 8 bytes: an array on the large object heap,
        // whose memory is reused uninitialized after a collection.
        const int m = 12000;
        var pairs = Ints(m).Concat(Enumerable.Range(0, m)
            .SelectMany(i => Ints(i).Append((byte)(i == m - 1 ? 2 : 1)))).ToArray();
        void Garbage()
        {
            for (int i = 0; i < 32; i++)
                new byte[200_000].AsSpan().Fill(0xCD);
            GC.Collect();
        }

        bool Clean(ReadOnlySpan<(int, bool)> values)
        {
            for (int i = 0; i < values.Length; i++)
                if (values[i] != default && values[i] != (i, true))
                    return false;
            return true;
        }

        Garbage();
        var pairArray = new (int, bool)[1];
        Assert.ThrowsExactly<NinoDataException>(() => NinoDeserializer.Deserialize(pairs, ref pairArray));
        Assert.IsTrue(Clean(pairArray));
        Garbage();
        List<(int, bool)> pairList = null;
        Assert.ThrowsExactly<NinoDataException>(() => NinoDeserializer.Deserialize(pairs, ref pairList));
        Assert.IsTrue(pairList == null || Clean(pairList.ToArray()));
        Garbage();
        var pairMemory = new Memory<(int, bool)>(new (int, bool)[1]);
        Assert.ThrowsExactly<NinoDataException>(() => NinoDeserializer.Deserialize(pairs, ref pairMemory));
        Assert.IsTrue(Clean(pairMemory.Span));
    }

    [TestMethod]
    public void InvalidValuesThrowAsInvalidData()
    {
        // A null key: [count 1][key: null string][value].
        var nullKey = Ints(1, -1, 5);
        Invalid<Dictionary<string, int>>(nullKey);
        Invalid<SortedDictionary<string, int>>(nullKey);
        Invalid<SortedList<string, int>>(nullKey);
        Invalid<System.Collections.Concurrent.ConcurrentDictionary<string, int>>(nullKey);
        Invalid<System.Collections.Immutable.ImmutableDictionary<string, int>>(nullKey);
        Invalid<IDictionary<string, int>>(nullKey);

        // Values the types' constructors reject.
        Invalid<Version>(Ints(1, -5, -1, -1));
        Invalid<System.Globalization.CultureInfo>(NinoSerializer.Serialize("a\u0000b!"));
        Invalid<TimeZoneInfo>(NinoSerializer.Serialize("No/Such_Zone")
            .Concat(NinoSerializer.Serialize((string)null)).ToArray());
        Invalid<TimeZoneInfo>(NinoSerializer.Serialize("Custom")
            .Concat(NinoSerializer.Serialize("not;serialized;rules")).ToArray());
        Invalid<Uri>(NinoSerializer.Serialize("http://[::1"));
        // A UTC time or offset out of range.
        Invalid<DateTimeOffset>(BitConverter.GetBytes(long.MaxValue).Concat(BitConverter.GetBytes((short)0)).ToArray());
    }

    /// <summary>
    /// The most a read of data claiming more elements than it holds may allocate per byte: up to 16 bytes per byte
    /// left ahead of the elements (NinoElements.Amplification), then at most 4 times the memory of the elements
    /// actually read (here 520 bytes for 516 on the wire), plus the exception.
    /// </summary>
    private const int MaxBytesPerByte = 24;

    private const int PayloadSize = 1 << 18;

    /// <summary>
    /// Reads <paramref name="data"/> as <typeparamref name="T"/> twice (the first initializes the formatters), and
    /// asserts that it throws Nino's exception after allocating at most <see cref="MaxBytesPerByte"/> times the data
    /// (sized for the claimed count, it allocated up to 540 times the data).
    /// </summary>
    private static void Bounded<T>(byte[] data)
    {
        Invalid<T>(data);
#if NETCOREAPP
        long before = GC.GetAllocatedBytesForCurrentThread();
        Invalid<T>(data);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.IsTrue(allocated <= (long)MaxBytesPerByte * data.Length + (64 << 10),
            $"{typeof(T)}: {allocated:N0} bytes allocated reading {data.Length:N0}");
#endif
    }

    /// <summary>
    /// The lengths, then 0xFF bytes up to <see cref="PayloadSize"/>: the first element fails (the region length of a
    /// version-tolerant struct, -1), so what the read allocated is what it allocated ahead of the elements.
    /// </summary>
    private static byte[] Claim(params int[] lengths)
    {
        var data = new byte[PayloadSize];
        data.AsSpan().Fill(0xFF);
        Buffer.BlockCopy(Ints(lengths), 0, data, 0, lengths.Length * sizeof(int));
        return data;
    }

    [TestMethod]
    public void LargeElementsAllocateInProportionToTheData()
    {
        // As many elements as the bytes left allow at the fewest bytes each (1 for the struct; 5 for the pairs,
        // whose keys take 4).
        int many = PayloadSize - sizeof(int);
        var data = Claim(many);
        var pairs = Claim(many / 5);
        Bounded<UntrustedBigStruct[]>(data);
        Bounded<List<UntrustedBigStruct>>(data);
        Bounded<IList<UntrustedBigStruct>>(data);
        Bounded<IEnumerable<UntrustedBigStruct>>(data);
        Bounded<IReadOnlyList<UntrustedBigStruct>>(data);
        Bounded<ReadOnlyCollection<UntrustedBigStruct>>(data);
        Bounded<ObservableCollection<UntrustedBigStruct>>(data);
        Bounded<Memory<UntrustedBigStruct>>(data);
        Bounded<ReadOnlyMemory<UntrustedBigStruct>>(data);
        Bounded<ArraySegment<UntrustedBigStruct>>(data);
        Bounded<Queue<UntrustedBigStruct>>(data);
        Bounded<Stack<UntrustedBigStruct>>(data);
        Bounded<HashSet<UntrustedBigStruct>>(data);
        Bounded<ISet<UntrustedBigStruct>>(data);
        Bounded<LinkedList<UntrustedBigStruct>>(data);
        Bounded<ConcurrentQueue<UntrustedBigStruct>>(data);
        Bounded<ConcurrentStack<UntrustedBigStruct>>(data);
        Bounded<ConcurrentBag<UntrustedBigStruct>>(data);
        Bounded<BlockingCollection<UntrustedBigStruct>>(data);
        Bounded<ImmutableArray<UntrustedBigStruct>>(data);
        Bounded<ImmutableList<UntrustedBigStruct>>(data);
        Bounded<ImmutableQueue<UntrustedBigStruct>>(data);
        Bounded<ImmutableStack<UntrustedBigStruct>>(data);
        Bounded<ImmutableHashSet<UntrustedBigStruct>>(data);
        Bounded<SortedSet<UntrustedBigValue>>(data);
        Bounded<ImmutableSortedSet<UntrustedBigValue>>(data);

        // Unmanaged elements that are not raw: their fewest bytes from a static field (NinoElements.MinWireSize).
        Bounded<UntrustedBigValue[]>(data);
        Bounded<List<UntrustedBigValue>>(data);
        Bounded<Queue<UntrustedBigValue>>(data);
        Bounded<Stack<UntrustedBigValue>>(data);
        Bounded<HashSet<UntrustedBigValue>>(data);
        Bounded<ConcurrentQueue<UntrustedBigValue>>(data);
        Bounded<ConcurrentStack<UntrustedBigValue>>(data);
        Bounded<LinkedList<UntrustedBigValue>>(data);

        // Raw elements, checked with their size (Queue, Stack and the general collections checked 1 byte each).
        Bounded<Queue<UntrustedRawBlock>>(data);
        Bounded<Stack<UntrustedRawBlock>>(data);
        Bounded<ConcurrentQueue<UntrustedRawBlock>>(data);
        Bounded<ConcurrentStack<UntrustedRawBlock>>(data);
        Bounded<SortedSet<UntrustedRawBlock>>(data);
        Bounded<LinkedList<UntrustedRawBlock>>(data);
        Bounded<IGrouping<int, UntrustedRawBlock>>(data);
        Bounded<UntrustedRawBlock[,]>(Claim(many / 4, 2));

        // References: a slot each (8 bytes), the objects made only as they are read (none here: all null).
        Bounded<UntrustedBigClass[]>(data);
        Bounded<List<UntrustedBigClass>>(data);
        Bounded<Queue<UntrustedBigClass>>(data);
        Bounded<HashSet<UntrustedBigClass>>(data);

        // Pairs: the dictionary's entries for the count (key, value, hash code, next, bucket).
        Bounded<Dictionary<int, UntrustedBigStruct>>(pairs);
        Bounded<Dictionary<UntrustedBigStruct, int>>(pairs);
        Bounded<IDictionary<int, UntrustedBigStruct>>(pairs);
        Bounded<IReadOnlyDictionary<int, UntrustedBigStruct>>(pairs);
        Bounded<SortedDictionary<int, UntrustedBigStruct>>(pairs);
        Bounded<SortedList<int, UntrustedBigStruct>>(pairs);
        Bounded<ConcurrentDictionary<int, UntrustedBigStruct>>(pairs);
        Bounded<ImmutableDictionary<int, UntrustedBigStruct>>(pairs);
        Bounded<ImmutableSortedDictionary<int, UntrustedBigStruct>>(pairs);
        Bounded<Dictionary<int, UntrustedBigValue>>(pairs);
        Bounded<SortedDictionary<int, UntrustedBigValue>>(pairs);
        Bounded<SortedDictionary<int, UntrustedRawBlock>>(pairs);
#if NET6_0_OR_GREATER && !NINO_CORE_NETSTANDARD // PriorityQueue: .NET 6+ builds of Nino.Core only
        Bounded<PriorityQueue<UntrustedBigStruct, int>>(pairs);
        Bounded<PriorityQueue<UntrustedBigValue, int>>(pairs);
        Bounded<PriorityQueue<UntrustedRawBlock, int>>(pairs);
#endif

        // Groupings: their count, the key, then the elements; a lookup: the grouping count first.
        Bounded<IGrouping<int, UntrustedBigStruct>>(data);
        Bounded<ILookup<int, UntrustedBigStruct>>(Claim(1, many - 8));
        Bounded<ILookup<UntrustedBigStruct, int>>(Claim(many / 4));

        // Multidimensional arrays: the product of the lengths.
        Bounded<UntrustedBigStruct[,]>(Claim(many / 4, 2));
        Bounded<UntrustedBigStruct[,,]>(Claim(many / 8, 2, 2));
        Bounded<UntrustedBigStruct[,,,]>(Claim(many / 16, 2, 2, 2));
        Bounded<UntrustedBigValue[,]>(Claim(many / 4, 2));

        // An existing dictionary refilled (it reads the keys into an array of the count first).
#if NETCOREAPP
        var existing = new Dictionary<UntrustedBigStruct, int> { [default] = 1 };
        long before = GC.GetAllocatedBytesForCurrentThread();
        Assert.ThrowsExactly<NinoDataException>(() => NinoDeserializer.Deserialize(pairs, ref existing));
        Assert.IsTrue(GC.GetAllocatedBytesForCurrentThread() - before <= (long)MaxBytesPerByte * data.Length);
#endif
    }

    [TestMethod]
    public void LargeElementsGrowAsTheyAreRead()
    {
        // 5 bytes each on the wire, over 500 in memory: more than the bytes left allow ahead, so each collection
        // grows as they are read, into the same values.
        var items = Enumerable.Range(0, 5000).Select(i => new UntrustedSparse
        {
            Id = i,
            Extra = i % 1000 == 0 ? new UntrustedBigValue { A0 = i, D15 = -i } : null
        }).ToArray();
        CollectionAssert.AreEqual(items, NinoDeserializer.Deserialize<UntrustedSparse[]>(NinoSerializer.Serialize(items)));
        CollectionAssert.AreEqual(items,
            NinoDeserializer.Deserialize<List<UntrustedSparse>>(NinoSerializer.Serialize(items.ToList())));
        CollectionAssert.AreEqual(items, NinoDeserializer.Deserialize<Queue<UntrustedSparse>>(
            NinoSerializer.Serialize(new Queue<UntrustedSparse>(items))).ToArray());
        CollectionAssert.AreEqual(items.Reverse().ToArray(), NinoDeserializer.Deserialize<Stack<UntrustedSparse>>(
            NinoSerializer.Serialize(new Stack<UntrustedSparse>(items))).ToArray());
        CollectionAssert.AreEqual(items, NinoDeserializer.Deserialize<ConcurrentQueue<UntrustedSparse>>(
            NinoSerializer.Serialize(new ConcurrentQueue<UntrustedSparse>(items))).ToArray());
        CollectionAssert.AreEqual(items, NinoDeserializer.Deserialize<ImmutableList<UntrustedSparse>>(
            NinoSerializer.Serialize(items.ToImmutableList())).ToArray());
        CollectionAssert.AreEqual(items, NinoDeserializer.Deserialize<ReadOnlyMemory<UntrustedSparse>>(
            NinoSerializer.Serialize(new ReadOnlyMemory<UntrustedSparse>(items))).ToArray());
        CollectionAssert.AreEquivalent(items, NinoDeserializer.Deserialize<HashSet<UntrustedSparse>>(
            NinoSerializer.Serialize(new HashSet<UntrustedSparse>(items))).ToArray());
        var dictionary = items.ToDictionary(x => x.Id);
        var back = NinoDeserializer.Deserialize<Dictionary<int, UntrustedSparse>>(NinoSerializer.Serialize(dictionary));
        CollectionAssert.AreEquivalent(dictionary, back);
        var sorted = NinoDeserializer.Deserialize<SortedDictionary<int, UntrustedSparse>>(
            NinoSerializer.Serialize(new SortedDictionary<int, UntrustedSparse>(dictionary)));
        CollectionAssert.AreEqual(items, sorted.Values.ToArray());
        var lookup = items.ToLookup(x => x.Id % 7);
        var lookupBack = NinoDeserializer.Deserialize<ILookup<int, UntrustedSparse>>(NinoSerializer.Serialize(lookup));
        Assert.IsTrue(lookup.All(g => g.SequenceEqual(lookupBack[g.Key])));
        var square = new UntrustedSparse[100, 50];
        for (int i = 0; i < 100; i++)
        for (int j = 0; j < 50; j++)
            square[i, j] = items[i * 50 + j];
        var squareBack = NinoDeserializer.Deserialize<UntrustedSparse[,]>(NinoSerializer.Serialize(square));
        CollectionAssert.AreEqual(items, squareBack.Cast<UntrustedSparse>().ToArray());
#if NET6_0_OR_GREATER && !NINO_CORE_NETSTANDARD
        var queue = new PriorityQueue<UntrustedSparse, int>(items.Select(x => (x, x.Id)));
        var queueBack = NinoDeserializer.Deserialize<PriorityQueue<UntrustedSparse, int>>(NinoSerializer.Serialize(queue));
        Assert.AreEqual(items.Length, queueBack.Count);
        Assert.AreEqual(0, queueBack.Dequeue().Id);
#endif

        // Into existing values of another length: the elements that fit are reused.
        var array = new UntrustedSparse[3];
        NinoDeserializer.Deserialize(NinoSerializer.Serialize(items), ref array);
        CollectionAssert.AreEqual(items, array);
        var list = items.Take(4000).Reverse().ToList();
        NinoDeserializer.Deserialize(NinoSerializer.Serialize(items), ref list);
        CollectionAssert.AreEqual(items, list);
        list = items.Concat(items).ToList();
        NinoDeserializer.Deserialize(NinoSerializer.Serialize(items), ref list);
        CollectionAssert.AreEqual(items, list);
        var memory = new Memory<UntrustedSparse>(new UntrustedSparse[2]);
        NinoDeserializer.Deserialize(NinoSerializer.Serialize(new Memory<UntrustedSparse>(items)), ref memory);
        CollectionAssert.AreEqual(items, memory.ToArray());
        var existing = items.Take(10).ToDictionary(x => x.Id + 1);
        NinoDeserializer.Deserialize(NinoSerializer.Serialize(dictionary), ref existing);
        CollectionAssert.AreEquivalent(dictionary, existing);
    }
}
