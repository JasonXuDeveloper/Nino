using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nino.Core;

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
        var e = Assert.ThrowsExactly<InvalidOperationException>(() =>
            NinoDeserializer.Deserialize<CaseInsensitiveSortedDictionary>(bytes));
        StringAssert.Contains(e.Message, "twice");
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            NinoDeserializer.Deserialize<CaseInsensitiveSortedList>(bytes));
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            NinoDeserializer.Deserialize<CaseInsensitiveConcurrentDictionary>(bytes));

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
}
