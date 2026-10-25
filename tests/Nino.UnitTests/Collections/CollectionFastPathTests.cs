using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nino.Core;
using Nino.Core.Internal;

#nullable disable
namespace Nino.UnitTests;

[NinoType]
public struct PaddedItem
{
    public int Id;
    public short Count;
    public bool Bound;
}

[NinoType]
public struct NamedItem
{
    public int Id;
    public string Name;
}

[NinoType]
public struct WideItem
{
    public long A, B, C, D;
    public string Name { get; set; }
    public int E;
}

[NinoType]
public class InlineNote
{
    public int Id;
    public string Text;
}

[NinoType]
public class TaggedNote : InlineNote
{
    public string Tag;
}

[NinoType]
public class NoteBook
{
    public string Title;
    public List<InlineNote> Notes;
}

public class CaseInsensitiveSortedDictionary : SortedDictionary<string, int>
{
    public CaseInsensitiveSortedDictionary() : base(StringComparer.OrdinalIgnoreCase)
    {
    }
}

public class CaseInsensitiveSortedList : SortedList<string, int>
{
    public CaseInsensitiveSortedList() : base(StringComparer.OrdinalIgnoreCase)
    {
    }
}

public class DescendingSortedSet : SortedSet<int>
{
    public DescendingSortedSet() : base(Comparer<int>.Create((a, b) => b.CompareTo(a)))
    {
    }
}

public class DescendingSortedList : SortedList<int, string>
{
    public DescendingSortedList() : base(Comparer<int>.Create((a, b) => b.CompareTo(a)))
    {
    }
}

public class NamedLinkedList : LinkedList<string>
{
}

public class CaseInsensitiveConcurrentDictionary : ConcurrentDictionary<string, int>
{
    public CaseInsensitiveConcurrentDictionary() : base(StringComparer.OrdinalIgnoreCase)
    {
    }
}

/// <summary>
/// Paths that skip the general code for some element and collection types: [NinoType] struct elements through their
/// generated formatter, SortedDictionary, SortedList and ConcurrentDictionary through their own calls, and small
/// blocks copied inline. The bytes and the results must be those of the general path.
/// </summary>
[TestClass]
public class CollectionFastPathTests
{
    [TestMethod]
    public void StructElementsKeepTheirBytesAfterTopLevelUse()
    {
        var items = Enumerable.Range(0, 100).Select(i => new PaddedItem
            { Id = i, Count = (short)(i * 3), Bound = i % 2 == 0 }).ToArray();
        var named = Enumerable.Range(0, 20).Select(i => new NamedItem
            { Id = i, Name = i % 3 == 0 ? null : "n" + i }).ToList();

        byte[] before = NinoSerializer.Serialize(items);
        byte[] namedBefore = NinoSerializer.Serialize(named);
        // Top-level use registers the element type; collections of it must keep their bytes.
        Assert.AreEqual(items[1], NinoDeserializer.Deserialize<PaddedItem>(NinoSerializer.Serialize(items[1])));
        Assert.AreEqual(named[1], NinoDeserializer.Deserialize<NamedItem>(NinoSerializer.Serialize(named[1])));
        CollectionAssert.AreEqual(before, NinoSerializer.Serialize(items));
        CollectionAssert.AreEqual(namedBefore, NinoSerializer.Serialize(named));
        CollectionAssert.AreEqual(before, NinoSerializer.Serialize(items.ToList()));

        CollectionAssert.AreEqual(items, NinoDeserializer.Deserialize<PaddedItem[]>(before));
        CollectionAssert.AreEqual(items, NinoDeserializer.Deserialize<List<PaddedItem>>(before));
        CollectionAssert.AreEqual(named, NinoDeserializer.Deserialize<List<NamedItem>>(namedBefore));
        CollectionAssert.AreEqual(named, NinoDeserializer.Deserialize<NamedItem[]>(namedBefore));
        var byId = items.ToDictionary(i => i.Id);
        CollectionAssert.AreEqual(byId.ToList(),
            NinoDeserializer.Deserialize<Dictionary<int, PaddedItem>>(NinoSerializer.Serialize(byId)).ToList());
    }

    [TestMethod]
    public void SortedAndConcurrentDictionariesKeepTheBytesOfADictionary()
    {
        var pairs = Enumerable.Range(0, 200).Select(i => new KeyValuePair<string, int>("k" + i, i)).ToList();
        var dictionary = new Dictionary<string, int>(pairs);
        var sorted = new SortedDictionary<string, int>(dictionary, StringComparer.Ordinal);
        byte[] ordered = NinoSerializer.Serialize(new Dictionary<string, int>(sorted));
        CollectionAssert.AreEqual(ordered, NinoSerializer.Serialize(sorted));
        CollectionAssert.AreEqual(ordered, NinoSerializer.Serialize(new SortedList<string, int>(sorted, StringComparer.Ordinal)));

        var bytes = NinoSerializer.Serialize(dictionary);
        var readSorted = NinoDeserializer.Deserialize<SortedDictionary<string, int>>(bytes);
        CollectionAssert.AreEquivalent(pairs, readSorted.ToList());
        var readList = NinoDeserializer.Deserialize<SortedList<string, int>>(bytes);
        CollectionAssert.AreEquivalent(pairs, readList.ToList());
        var readConcurrent = NinoDeserializer.Deserialize<ConcurrentDictionary<string, int>>(bytes);
        CollectionAssert.AreEquivalent(pairs, readConcurrent.ToList());

        // Raw pairs, and values that are collections.
        var numbers = new SortedDictionary<int, List<int>> { [3] = new() { 1 }, [1] = null, [2] = new() };
        var back = NinoDeserializer.Deserialize<SortedDictionary<int, List<int>>>(NinoSerializer.Serialize(numbers));
        CollectionAssert.AreEqual(numbers.Keys.ToList(), back.Keys.ToList());
        Assert.IsNull(back[1]);
        CollectionAssert.AreEqual(new[] { 1 }, back[3]);
        Assert.IsNull(NinoDeserializer.Deserialize<SortedDictionary<int, int>>(
            NinoSerializer.Serialize((SortedDictionary<int, int>)null)));
    }

    [TestMethod]
    public void ExistingSortedAndConcurrentDictionariesAreRefilled()
    {
        var bytes = NinoSerializer.Serialize(new Dictionary<int, int> { [5] = 50, [1] = 10 });

        var sorted = new SortedDictionary<int, int> { [9] = 9, [1] = 1 };
        NinoDeserializer.Deserialize(bytes, ref sorted);
        CollectionAssert.AreEqual(new[] { 1, 5 }, sorted.Keys.ToList());
        Assert.AreEqual(10, sorted[1]);

        var list = new SortedList<int, int> { [9] = 9 };
        NinoDeserializer.Deserialize(bytes, ref list);
        CollectionAssert.AreEqual(new[] { 1, 5 }, list.Keys.ToList());

        var concurrent = new ConcurrentDictionary<int, int>();
        concurrent[7] = 7;
        NinoDeserializer.Deserialize(bytes, ref concurrent);
        CollectionAssert.AreEquivalent(new[] { 1, 5 }, concurrent.Keys.ToList());
        Assert.AreEqual(50, concurrent[5]);
    }

    [TestMethod]
    public void DuplicateKeysAreAClearError()
    {
        // Distinct under the writer's comparer, the same under the reader's.
        var bytes = NinoSerializer.Serialize(new Dictionary<string, int> { ["a"] = 1, ["A"] = 2 });
        var e = Assert.ThrowsExactly<NinoDataException>(() =>
            NinoDeserializer.Deserialize<CaseInsensitiveSortedDictionary>(bytes));
        StringAssert.Contains(e.Message, "twice");
        Assert.ThrowsExactly<NinoDataException>(() =>
            NinoDeserializer.Deserialize<CaseInsensitiveSortedList>(bytes));
        Assert.ThrowsExactly<NinoDataException>(() =>
            NinoDeserializer.Deserialize<CaseInsensitiveConcurrentDictionary>(bytes));

        // A ConcurrentDictionary itself is built by its constructor from the pairs read.
        var pairs = NinoSerializer.Serialize(new[] { new KeyValuePair<int, int>(1, 1), new KeyValuePair<int, int>(1, 2) });
        e = Assert.ThrowsExactly<NinoDataException>(() =>
            NinoDeserializer.Deserialize<ConcurrentDictionary<int, int>>(pairs));
        StringAssert.Contains(e.Message, "twice");
        var nullKey = NinoSerializer.Serialize(new[] { new KeyValuePair<string, int>(null, 1) });
        e = Assert.ThrowsExactly<NinoDataException>(() =>
            NinoDeserializer.Deserialize<ConcurrentDictionary<string, int>>(nullKey));
        StringAssert.Contains(e.Message, "null key");

        // Derived types read the rest normally.
        var fine = NinoSerializer.Serialize(new Dictionary<string, int> { ["a"] = 1, ["b"] = 2 });
        var derived = NinoDeserializer.Deserialize<CaseInsensitiveSortedDictionary>(fine);
        Assert.AreEqual(2, derived["B"]);
        Assert.AreEqual(1, NinoDeserializer.Deserialize<CaseInsensitiveSortedList>(fine)["A"]);
        Assert.AreEqual(2, NinoDeserializer.Deserialize<CaseInsensitiveConcurrentDictionary>(fine)["B"]);
    }

    [TestMethod]
    public void SmallBlocksOfEverySizeRoundTrip()
    {
        // Raw element blocks of 0 to 40 bytes (copied inline up to 32), and strings.
        for (int n = 0; n <= 40; n++)
        {
            var block = Enumerable.Range(0, n).Select(i => (byte)(i * 7 + n)).ToArray();
            CollectionAssert.AreEqual(block, NinoDeserializer.Deserialize<byte[]>(NinoSerializer.Serialize(block)));
            var shorts = block.Select(b => (short)(b * 300)).Take(n / 2).ToList();
            CollectionAssert.AreEqual(shorts, NinoDeserializer.Deserialize<List<short>>(NinoSerializer.Serialize(shorts)));
        }

        // Strings are allocated by the runtime's FastAllocateString on .NET 8 and later and filled inline up to 32
        // chars: every length around 16 and 32 chars and some past them.
        foreach (int n in Enumerable.Range(0, 70).Concat(new[] { 100, 257, 1000 }))
        {
            var text = new string(Enumerable.Range(0, n).Select(i => (char)('a' + i % 26 + (i % 7 == 0 ? 0x400 : 0))).ToArray());
            Assert.AreEqual(text, NinoDeserializer.Deserialize<string>(NinoSerializer.Serialize(text)));
            var words = new[] { text, null, text + "é" };
            CollectionAssert.AreEqual(words, NinoDeserializer.Deserialize<string[]>(NinoSerializer.Serialize(words)));
        }
    }

    [TestMethod]
    public void BlocksUpToTheVectorLimitRoundTrip()
    {
        // Writes of 64 bytes to 8 KB use 64-byte vectors where the CPU has them, padded structs a masked copy:
        // every size around the vector widths, then sizes past the limit.
        var sizes = Enumerable.Range(0, 300).Concat(Enumerable.Range(0, 60).Select(i => 300 + i * 151))
            .Concat(new[] { 8191, 8192, 8193, 9000 });
        foreach (int n in sizes)
        {
            var block = Enumerable.Range(0, n).Select(i => (byte)(i * 31 + n)).ToArray();
            CollectionAssert.AreEqual(block, NinoDeserializer.Deserialize<byte[]>(NinoSerializer.Serialize(block)));
        }

        foreach (int n in Enumerable.Range(0, 40).Concat(new[] { 63, 64, 65, 150, 999, 1024, 1025, 1200 }))
        {
            var items = Enumerable.Range(0, n).Select(i => new PaddedItem
                { Id = i * 7, Count = (short)i, Bound = i % 3 == 0 }).ToArray();
            byte[] bytes = NinoSerializer.Serialize(items);
            CollectionAssert.AreEqual(items, NinoDeserializer.Deserialize<PaddedItem[]>(bytes));
            CollectionAssert.AreEqual(bytes, NinoSerializer.Serialize(items.ToList()));
        }
    }

    [TestMethod]
    public void SmallClassBodiesInCollectionsKeepTheirSubtypes()
    {
        // A small non-sealed class body is inlined into the collection's loop; its subtypes and nulls still go
        // through the polymorphic path.
        var book = new NoteBook
        {
            Title = "book",
            Notes = Enumerable.Range(0, 50).Select(i => i % 5 == 0 ? null
                : i % 3 == 0 ? new TaggedNote { Id = i, Text = "t" + i, Tag = "#" + i }
                : new InlineNote { Id = i, Text = i % 4 == 0 ? null : "n" + i }).ToList()
        };
        var books = new[] { book, null, new NoteBook { Title = "empty", Notes = new List<InlineNote>() } };
        var result = NinoDeserializer.Deserialize<NoteBook[]>(NinoSerializer.Serialize(books));

        Assert.AreEqual(3, result.Length);
        Assert.IsNull(result[1]);
        Assert.AreEqual(0, result[2].Notes.Count);
        Assert.AreEqual("book", result[0].Title);
        for (int i = 0; i < book.Notes.Count; i++)
        {
            InlineNote expected = book.Notes[i], actual = result[0].Notes[i];
            if (expected == null)
            {
                Assert.IsNull(actual);
                continue;
            }

            Assert.AreEqual(expected.GetType(), actual.GetType());
            Assert.AreEqual(expected.Id, actual.Id);
            Assert.AreEqual(expected.Text, actual.Text);
            if (expected is TaggedNote tagged)
                Assert.AreEqual(tagged.Tag, ((TaggedNote)actual).Tag);
        }

        CollectionAssert.AreEqual(NinoSerializer.Serialize(book.Notes),
            NinoSerializer.Serialize(result[0].Notes));
    }

    [TestMethod]
    public void WideStructsWrittenByReferenceRoundTrip()
    {
        // Structs with a member that is not raw are passed to their out-of-line body by reference.
        var items = Enumerable.Range(0, 100).Select(i => new WideItem
            { A = i, B = -i, C = i * 3L, D = long.MaxValue - i, Name = i % 7 == 0 ? null : "w" + i, E = i * 5 }).ToList();
        byte[] bytes = NinoSerializer.Serialize(items);
        CollectionAssert.AreEqual(items, NinoDeserializer.Deserialize<List<WideItem>>(bytes));
        CollectionAssert.AreEqual(bytes, NinoSerializer.Serialize(items.ToArray()));
        Assert.AreEqual(items[3], NinoDeserializer.Deserialize<WideItem>(NinoSerializer.Serialize(items[3])));
    }

    [TestMethod]
    public void LargeRawListsRoundTrip()
    {
        // A new list of raw elements gets an uninitialized backing array from 6 KB on; a reused one, a derived list
        // and a list of nullable values take their own paths.
        foreach (int n in new[] { 0, 1, 1535, 1536, 5000 })
        {
            var ints = Enumerable.Range(0, n).Select(i => i * 7 - 3).ToList();
            byte[] bytes = NinoSerializer.Serialize(ints);
            var read = NinoDeserializer.Deserialize<List<int>>(bytes);
            CollectionAssert.AreEqual(ints, read);
            Assert.AreEqual(n, read.Capacity);
            CollectionAssert.AreEqual(bytes, NinoSerializer.Serialize(ints.ToArray()));

            var reused = Enumerable.Repeat(-1, 7000).ToList();
            NinoDeserializer.Deserialize(bytes, ref reused);
            CollectionAssert.AreEqual(ints, reused);

            var derived = NinoDeserializer.Deserialize<DerivedIntList>(bytes);
            CollectionAssert.AreEqual(ints, derived);

            var nullable = Enumerable.Range(0, n).Select(i => i % 3 == 0 ? (long?)null : i * 11L).ToList();
            CollectionAssert.AreEqual(nullable,
                NinoDeserializer.Deserialize<List<long?>>(NinoSerializer.Serialize(nullable)));
        }
    }

    [TestMethod]
    public void ImmutableCollectionsWriteTheBytesOfArrays()
    {
        // ImmutableArray<T> is written and read through its array, ImmutableList<T> built from one: the bytes are
        // those of T[], and a read never reuses the array of the old value.
        foreach (int n in new[] { 0, 1, 3, 2000 })
        {
            var ints = Enumerable.Range(0, n).Select(i => i * 31).ToArray();
            var strings = Enumerable.Range(0, n).Select(i => i % 5 == 0 ? null : "s" + i).ToArray();

            var intArray = ImmutableArray.Create(ints);
            byte[] bytes = NinoSerializer.Serialize(intArray);
            CollectionAssert.AreEqual(NinoSerializer.Serialize(ints), bytes);
            var old = ImmutableArray.Create(Enumerable.Repeat(9, n).ToArray());
            var kept = old.ToArray();
            var read = old;
            NinoDeserializer.Deserialize(bytes, ref read);
            CollectionAssert.AreEqual(ints, read.ToArray());
            CollectionAssert.AreEqual(kept, old.ToArray());

            var stringArray = ImmutableArray.Create(strings);
            bytes = NinoSerializer.Serialize(stringArray);
            CollectionAssert.AreEqual(NinoSerializer.Serialize(strings), bytes);
            CollectionAssert.AreEqual(strings, NinoDeserializer.Deserialize<ImmutableArray<string>>(bytes).ToArray());

            var intList = ImmutableList.Create(ints);
            bytes = NinoSerializer.Serialize(intList);
            CollectionAssert.AreEqual(NinoSerializer.Serialize(ints), bytes);
            CollectionAssert.AreEqual(ints, NinoDeserializer.Deserialize<ImmutableList<int>>(bytes).ToArray());

            var stringList = ImmutableList.Create(strings);
            bytes = NinoSerializer.Serialize(stringList);
            CollectionAssert.AreEqual(NinoSerializer.Serialize(strings), bytes);
            CollectionAssert.AreEqual(strings, NinoDeserializer.Deserialize<ImmutableList<string>>(bytes).ToArray());
        }

        Assert.IsTrue(NinoDeserializer.Deserialize<ImmutableArray<int>>(NinoSerializer.Serialize(default(ImmutableArray<int>))).IsDefault);
        Assert.IsNull(NinoDeserializer.Deserialize<ImmutableList<int>>(NinoSerializer.Serialize((ImmutableList<int>)null)));
    }

    private static void BlocksRoundTrip<T>(Func<int, T> make)
    {
        // Every length around the vector widths (16 and 64 bytes): the bytes are each value's own, after the count.
        foreach (int n in Enumerable.Range(0, 70).Concat(new[] { 100, 1000 }))
        {
            var values = Enumerable.Range(0, n).Select(make).ToArray();
            var expected = BitConverter.GetBytes(n).Concat(values.SelectMany(v => NinoSerializer.Serialize(v))).ToArray();
            CollectionAssert.AreEqual(expected, NinoSerializer.Serialize(values));
            CollectionAssert.AreEqual(expected, NinoSerializer.Serialize(values.ToList()));
            CollectionAssert.AreEqual(values, NinoDeserializer.Deserialize<T[]>(expected));
            CollectionAssert.AreEqual(values, NinoDeserializer.Deserialize<List<T>>(expected));
            var reused = Enumerable.Repeat(make(7), n + 3).ToList();
            NinoDeserializer.Deserialize(expected, ref reused);
            CollectionAssert.AreEqual(values, reused);
        }
    }

    [TestMethod]
    public void BlocksReadAtEveryAlignment()
    {
        // Arrays are 8-byte aligned: 16-byte values start on a 64-byte boundary in some and 8 bytes past one in the
        // others, read with different plans (AVX-512). Both, into memory full of garbage first.
        ReadsAtEveryAlignment(i => (i, -7L * i));
        ReadsAtEveryAlignment(i => new KeyValuePair<int, long>(i, i * 1000L));
        ReadsAtEveryAlignment(i => (i * 3L, (byte)i));
        ReadsAtEveryAlignment(i => ((short)i, (double)i / 3));
        // 20 bytes in memory: no group of whole vectors fits in four, so groups that end inside a vector.
        ReadsAtEveryAlignment(i => ((byte)i, i, i * 3, i * 5, i * 7));
    }

    private static void ReadsAtEveryAlignment<T>(Func<int, T> make) where T : unmanaged
    {
        var seen = new HashSet<long>();
        var keep = new List<object>();
        // Every count up to a few groups: a group from the first value and one ending at the last, over the aligned ones.
        foreach (int n in Enumerable.Range(30, 100).Concat(new[] { 1000, 1001 }))
        {
            var values = Enumerable.Range(0, n).Select(make).ToArray();
            var bytes = NinoSerializer.Serialize(values);
            for (int shift = 0; shift < 8; shift++)
            {
                keep.Add(new byte[shift * 8 + 1]);
                var read = new T[n];
                System.Runtime.InteropServices.MemoryMarshal.AsBytes(read.AsSpan()).Fill(0xFF);
                var pin = System.Runtime.InteropServices.GCHandle.Alloc(read, System.Runtime.InteropServices.GCHandleType.Pinned);
                seen.Add((long)pin.AddrOfPinnedObject() % 16);
                pin.Free();
                NinoDeserializer.Deserialize(bytes, ref read);
                CollectionAssert.AreEqual(values, read, $"{typeof(T)} n={n} shift={shift}");
            }
        }

        GC.KeepAlive(keep);
        if (System.Runtime.CompilerServices.Unsafe.SizeOf<T>() % 16 == 0)
            Assert.IsTrue(seen.Contains(0) && seen.Contains(8), $"{typeof(T)}: both alignments read ({string.Join(",", seen)})");
    }

    [TestMethod]
    public void BlocksWriteAsEachValue()
    {
        // Groups of values move as vectors (AVX-512, AVX2, SSSE3, Arm64 and WebAssembly each their own plan), with
        // loads before and after a group's values: every count, every destination alignment, every layout below.
        WritesAsEachValue(i => (i, -7L * i));
        WritesAsEachValue(i => new KeyValuePair<int, long>(i, i * 1000L));
        WritesAsEachValue(i => (i * 3L, (byte)i));
        WritesAsEachValue(i => ((byte)i, i * 3L));
        WritesAsEachValue(i => ((short)i, (double)i / 3));
        WritesAsEachValue(i => ((short)i, i * 5));
        WritesAsEachValue(i => ((byte)i, i * 5));
        WritesAsEachValue(i => (i, (short)i, i * 9L));
        WritesAsEachValue(i => ((double)i, i, (byte)i));
        WritesAsEachValue(i => ((byte)i, i, i * 3, i * 5, i * 7));
    }

    private static void WritesAsEachValue<T>(Func<int, T> make) where T : unmanaged
    {
        foreach (int n in Enumerable.Range(0, 80).Concat(new[] { 255, 1000, 1001 }))
        {
            var values = Enumerable.Range(0, n).Select(make).ToArray();
            var expected = new List<byte>(BitConverter.GetBytes(n));
            foreach (var value in values)
                expected.AddRange(NinoSerializer.Serialize(value));
            CollectionAssert.AreEqual(expected, NinoSerializer.Serialize(values), $"{typeof(T)}[] n={n}");
            CollectionAssert.AreEqual(expected, NinoSerializer.Serialize(values.ToList()), $"List<{typeof(T)}> n={n}");
            // Every alignment of the blocks to a 64-byte boundary from a few groups on (below, every 8 bytes).
            for (int shift = 1; shift < (n >= 32 ? 64 : 8); shift++)
            {
                var buffer = new NinoArrayBufferWriter();
                buffer.GetSpan(shift);
                buffer.Advance(shift);
                NinoSerializer.Serialize(values.ToList(), buffer);
                CollectionAssert.AreEqual(expected, buffer.WrittenSpan.Slice(shift).ToArray(), $"{typeof(T)} n={n} shift={shift}");
            }

            CollectionAssert.AreEqual(values, NinoDeserializer.Deserialize<List<T>>(NinoSerializer.Serialize(values)));
        }

        Assert.AreEqual(4, NinoSerializer.Serialize((List<T>)null).Length);
        Assert.IsNull(NinoDeserializer.Deserialize<List<T>>(NinoSerializer.Serialize((T[])null)));
    }

    [TestMethod]
    public void SizedCollectionsWriteTheWritersBytes()
    {
        // Collections whose size is known up front are written straight into an array of that size: the same bytes
        // as through a writer, for null, empty and full ones of every kind of block.
        var at = new DateTimeOffset(2026, 10, 19, 1, 2, 3, TimeSpan.FromHours(8));
        SameBytes(new[] { 1, 2, 3 });
        SameBytes(new List<int> { 1, 2, 3 });
        SameBytes(new List<int>());
        SameBytes((List<int>)null);
        SameBytes(Array.Empty<(int, long)>());
        SameBytes(((int, long)[])null);
        SameBytes(Enumerable.Range(0, 100).Select(i => (i, i * 3L)).ToList());
        SameBytes(Enumerable.Range(0, 100).Select(i => at.AddMinutes(i)).ToArray());
        SameBytes(Enumerable.Range(0, 100).Select(i => at.AddMinutes(i)).ToList());
        SameBytes(Enumerable.Range(0, 100).Select(i => new PaddedItem { Id = i, Count = (short)i, Bound = i % 2 == 0 })
            .ToList());
    }

    private static void SameBytes<T>(T value)
    {
        var buffer = new NinoArrayBufferWriter();
        NinoSerializer.Serialize(value, buffer);
        CollectionAssert.AreEqual(buffer.WrittenSpan.ToArray(), NinoSerializer.Serialize(value), typeof(T).ToString());
    }

    [TestMethod]
    public void SortedCollectionsAreBuiltInOnePass()
    {
        foreach (int n in Enumerable.Range(0, 70).Concat(new[] { 100, 1000 }))
        {
            var keys = Enumerable.Range(0, n).Select(i => i * 7 - 300).ToList();
            var set = new SortedSet<int>(keys);
            var readSet = NinoDeserializer.Deserialize<SortedSet<int>>(NinoSerializer.Serialize(set));
            CollectionAssert.AreEqual(keys, readSet.ToList());
            Assert.AreEqual(n, readSet.Count);
            if (n > 0)
            {
                Assert.AreEqual(keys[0], readSet.Min);
                Assert.AreEqual(keys[n - 1], readSet.Max);
                Assert.IsTrue(readSet.Contains(keys[n / 2]));
                Assert.IsFalse(readSet.Contains(keys[n / 2] + 1));
            }

            // The tree it holds is its own: it takes inserts and removals.
            readSet.Add(-1000);
            readSet.Remove(keys.FirstOrDefault());
            Assert.AreEqual(n == 0 ? 1 : n, readSet.Count);

            var strings = new SortedSet<string>(keys.Select(k => "s" + k), StringComparer.Ordinal);
            var readStrings = NinoDeserializer.Deserialize<SortedSet<string>>(NinoSerializer.Serialize(strings));
            CollectionAssert.AreEqual(strings.OrderBy(x => x, StringComparer.Ordinal).ToList(), readStrings.ToList());

            var dictionary = new SortedDictionary<int, string>(keys.ToDictionary(k => k, k => k % 3 == 0 ? null : "v" + k));
            var readDictionary =
                NinoDeserializer.Deserialize<SortedDictionary<int, string>>(NinoSerializer.Serialize(dictionary));
            CollectionAssert.AreEqual(dictionary.ToList(), readDictionary.ToList());
            readDictionary[-1000] = "x";
            Assert.AreEqual("x", readDictionary.First().Value);

            var pairs = new SortedDictionary<int, long>(keys.ToDictionary(k => k, k => (long)k << 33));
            CollectionAssert.AreEqual(pairs.ToList(),
                NinoDeserializer.Deserialize<SortedDictionary<int, long>>(NinoSerializer.Serialize(pairs)).ToList());

            var list = new SortedList<int, string>(dictionary);
            var listBytes = NinoSerializer.Serialize(list);
            CollectionAssert.AreEqual(NinoSerializer.Serialize(dictionary), listBytes);
            var readList = NinoDeserializer.Deserialize<SortedList<int, string>>(listBytes);
            CollectionAssert.AreEqual(list.ToList(), readList.ToList());
            readList.Add(-1000, "x");
            Assert.AreEqual(0, readList.IndexOfKey(-1000));
        }
    }

    [TestMethod]
    public void SortedCollectionsReadPairsOutOfOrder()
    {
        // A SortedList, SortedSet or SortedDictionary with another comparer than the writer's: the pairs come out of
        // order, and are sorted by the reader's.
        var keys = Enumerable.Range(0, 50).ToList();
        var ascending = NinoSerializer.Serialize(new SortedSet<int>(keys));
        var descendingSet = NinoDeserializer.Deserialize<DescendingSortedSet>(ascending);
        CollectionAssert.AreEqual(Enumerable.Reverse(keys).ToList(), descendingSet.ToList());
        Assert.AreEqual(49, descendingSet.Min);

        var pairs = NinoSerializer.Serialize(new SortedList<int, string>(keys.ToDictionary(k => k, k => "v" + k)));
        var descendingList = NinoDeserializer.Deserialize<DescendingSortedList>(pairs);
        CollectionAssert.AreEqual(Enumerable.Reverse(keys).ToList(), descendingList.Keys.ToList());
        Assert.AreEqual("v49", descendingList.Values[0]);

        // An existing list with larger arrays, read into from unsorted pairs.
        var existing = new SortedList<int, string>(100) { [1] = "old" };
        NinoDeserializer.Deserialize(NinoSerializer.Serialize(new Dictionary<int, string> { [9] = "a", [3] = "b" }),
            ref existing);
        CollectionAssert.AreEqual(new[] { 3, 9 }, existing.Keys.ToList());
        CollectionAssert.AreEqual(new[] { "b", "a" }, existing.Values.ToList());

        // Duplicates in a set are dropped, as Add drops them.
        var sets = NinoSerializer.Serialize(new List<string> { "a", "B", "A", "b" });
        var caseless = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        NinoDeserializer.Deserialize(sets, ref caseless);
        Assert.AreEqual(2, caseless.Count);

        // A null key is invalid data, as for every dictionary that takes none.
        var nullKey = NinoSerializer.Serialize(new List<KeyValuePair<string, int>> { new("a", 1), new(null, 2) });
        StringAssert.Contains(Assert.ThrowsExactly<NinoDataException>(() =>
            NinoDeserializer.Deserialize<SortedList<string, int>>(nullKey)).Message, "null key");
        StringAssert.Contains(Assert.ThrowsExactly<NinoDataException>(() =>
            NinoDeserializer.Deserialize<SortedDictionary<string, int>>(nullKey)).Message, "null key");
        StringAssert.Contains(Assert.ThrowsExactly<NinoDataException>(() =>
            NinoDeserializer.Deserialize<Dictionary<string, int>>(nullKey)).Message, "null key");
    }

    [TestMethod]
    public void LinkedListsAreWalkedNodeByNode()
    {
        foreach (int n in new[] { 0, 1, 2, 17, 1000 })
        {
            var ints = new LinkedList<int>(Enumerable.Range(0, n).Select(i => i * 31));
            var intBytes = NinoSerializer.Serialize(ints);
            CollectionAssert.AreEqual(NinoSerializer.Serialize(ints.ToList()), intBytes);
            CollectionAssert.AreEqual(ints.ToList(), NinoDeserializer.Deserialize<LinkedList<int>>(intBytes).ToList());

            var padded = new LinkedList<PaddedItem>(Enumerable.Range(0, n)
                .Select(i => new PaddedItem { Id = i, Count = (short)i, Bound = i % 2 == 0 }));
            CollectionAssert.AreEqual(NinoSerializer.Serialize(padded.ToArray()), NinoSerializer.Serialize(padded));

            var names = new NamedLinkedList();
            foreach (var i in Enumerable.Range(0, n))
                names.AddLast(i % 4 == 0 ? null : "n" + i);
            var read = NinoDeserializer.Deserialize<NamedLinkedList>(NinoSerializer.Serialize(names));
            CollectionAssert.AreEqual(names.ToList(), read.ToList());
            NinoDeserializer.Deserialize(intBytes, ref ints);
            Assert.AreEqual(n, ints.Count);
        }
    }

    [TestMethod]
    public void TuplesAndPairsOfRawItemsAsBlocks()
    {
        // Copied as memory when it is the block ((int, int)), moved by byte shuffles when the runtime reorders or pads
        // the items ((int, long) is a long, then an int and 4 padding bytes), or one at a time (over 16 bytes).
        BlocksRoundTrip(i => (i, i * 3));
        BlocksRoundTrip(i => (i, -7L * i));
        BlocksRoundTrip(i => ((byte)i, i * 11, (short)-i));
        BlocksRoundTrip(i => (long.MaxValue - i, i * 5L, i));
        BlocksRoundTrip(i => (i % 2 == 0, (double)i / 3));
        BlocksRoundTrip(i => new KeyValuePair<int, int>(i, -i));
        BlocksRoundTrip(i => new KeyValuePair<int, long>(i, i * 1000L));
        BlocksRoundTrip(i => new KeyValuePair<long, byte>(i * 3L, (byte)i));
        BlocksRoundTrip(i => (i, i % 3 == 0 ? null : "t" + i));
    }

    [TestMethod]
    public void CollectionLayoutsAreRecognized()
    {
        // Each collection Nino reads and writes through its fields is checked against the runtime's layout at
        // startup, and its public API is used when the check fails. On .NET every check must pass, or the fast
        // paths are silently off.
        Assert.IsTrue(Nino.Core.Internal.CollectionViews.List, "List<T>");
        Assert.IsTrue(Nino.Core.Internal.CollectionViews.Stack, "Stack<T>");
        Assert.IsTrue(Nino.Core.Internal.CollectionViews.Queue, "Queue<T>");
        Assert.IsTrue(Nino.Core.Internal.CollectionViews.SortedList, "SortedList<TKey, TValue>");
        Assert.IsTrue(Nino.Core.Internal.CollectionViews.SortedSet, "SortedSet<T>");
        Assert.IsTrue(Nino.Core.Internal.CollectionViews.SortedDictionary, "SortedDictionary<TKey, TValue>");
        Assert.IsTrue(Nino.Core.Internal.CollectionViews.HashSet, "HashSet<T>");
        Assert.IsTrue(Nino.Core.Internal.CollectionViews.Dictionary, "Dictionary<TKey, TValue>");
    }

#if NET9_0_OR_GREATER && !NINO_CORE_NETSTANDARD
    [TestMethod]
    public void LayoutsByNameMatchTheMirrors()
    {
        // The net10.0 build finds List<T>, Stack<T>, Queue<T>, SortedList<TKey, TValue> and LinkedList<T> by their
        // fields' names (UnsafeAccessor) first; every other build by the mirror classes. Both must give the same
        // offsets, so both read and write the same bytes (the netstandard test runs take the mirrors alone).
        Assert.IsTrue(Nino.Core.Internal.CollectionViews.ByNameMatchesMirrors());
    }
#endif

    [TestMethod]
    public void SetsAndDictionariesWriteTheirEntriesInOrder()
    {
        // Raw sets and dictionaries are written from their entries when none is free, else through the enumerator:
        // either way the elements in enumeration order, as an array of them is written.
        SetStates(i => (byte)i);
        SetStates(i => (short)i);
        SetStates(i => i * 7L);
        SetStates(i => (i, (byte)i));
        DictionaryStates(i => (byte)i);
        DictionaryStates(i => i * 7L);
        DictionaryStates(i => ((short)i, i));
    }

    private static void SetStates<T>(Func<int, T> make)
    {
        var set = new HashSet<T>();
        Check();
        for (int i = 0; i < 100; i++)
            set.Add(make(i));
        Check();
        for (int i = 0; i < 100; i += 3)
            set.Remove(make(i));
        Check();
        for (int i = 0; i < 100; i += 3)
            set.Add(make(i + 1000));
        Check();
        set.Clear();
        Check();
        set.Add(make(5));
        Check();

        void Check()
        {
            CollectionAssert.AreEqual(NinoSerializer.Serialize(set.ToArray()), NinoSerializer.Serialize(set));
            CollectionAssert.AreEquivalent(set.ToArray(),
                NinoDeserializer.Deserialize<HashSet<T>>(NinoSerializer.Serialize(set)).ToArray());
        }
    }

    private static void DictionaryStates<T>(Func<int, T> make)
    {
        var dictionary = new Dictionary<int, T>();
        Check();
        for (int i = 0; i < 100; i++)
            dictionary.Add(i, make(i));
        Check();
        for (int i = 0; i < 100; i += 3)
            dictionary.Remove(i);
        Check();
        for (int i = 0; i < 100; i += 3)
            dictionary.Add(i + 1000, make(i));
        Check();
        dictionary.Clear();
        Check();
        dictionary.Add(5, make(5));
        Check();

        void Check()
        {
            CollectionAssert.AreEqual(NinoSerializer.Serialize(dictionary.ToArray()),
                NinoSerializer.Serialize(dictionary));
            CollectionAssert.AreEquivalent(dictionary.ToArray(),
                NinoDeserializer.Deserialize<Dictionary<int, T>>(NinoSerializer.Serialize(dictionary)).ToArray());
        }
    }
}
