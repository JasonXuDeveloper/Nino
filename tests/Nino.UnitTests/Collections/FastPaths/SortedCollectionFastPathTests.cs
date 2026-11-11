using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nino.Core;
using static Nino.UnitTests.Rt;

#nullable disable
namespace Nino.UnitTests;

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

public class CaseInsensitiveConcurrentDictionary : ConcurrentDictionary<string, int>
{
    public CaseInsensitiveConcurrentDictionary() : base(StringComparer.OrdinalIgnoreCase)
    {
    }
}

/// <summary>
/// SortedDictionary, SortedList, SortedSet and ConcurrentDictionary through their own calls: the bytes of a dictionary,
/// trees built in one pass, refills of existing instances, pairs out of the reader's order and duplicate keys.
/// </summary>
[TestClass]
public class SortedCollectionFastPathTests
{
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
    public void SortedSetWritesPackedItemsAtAnUnalignedOffset()
    {
        var set = new SortedSet<int>(Enumerable.Range(0, 257), Comparer<int>.Create((a, b) => b.CompareTo(a)));
        using var buffer = OffsetWriter();
        NinoSerializer.Serialize(set, buffer);
        CollectionAssert.AreEqual(NinoSerializer.Serialize(set.ToArray()), buffer.WrittenSpan.Slice(1).ToArray());
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(257)]
    public void SortedDictionaryWritesPackedPairsAtAnUnalignedOffset(int count)
    {
        var values = Enumerable.Range(0, count).ToDictionary(i => i, i => -i);
        var dictionary = new SortedDictionary<int, int>(values, Comparer<int>.Create((a, b) => b.CompareTo(a)));
        using var buffer = OffsetWriter();
        NinoSerializer.Serialize(dictionary, buffer);
        CollectionAssert.AreEqual(NinoSerializer.Serialize(dictionary.ToArray()), buffer.WrittenSpan.Slice(1).ToArray());
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

        // Strings for both keys and values take a loop of their own: the same checks.
        var stringNullKey =
            NinoSerializer.Serialize(new List<KeyValuePair<string, string>> { new("a", "1"), new(null, "2") });
        StringAssert.Contains(Assert.ThrowsExactly<NinoDataException>(() =>
            NinoDeserializer.Deserialize<Dictionary<string, string>>(stringNullKey)).Message, "null key");
        var twice = NinoSerializer.Serialize(new List<KeyValuePair<string, string>> { new("a", "1"), new("a", "2") });
        StringAssert.Contains(Assert.ThrowsExactly<NinoDataException>(() =>
            NinoDeserializer.Deserialize<Dictionary<string, string>>(twice)).Message, "twice");
        var caselessStrings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var differentCase = NinoSerializer.Serialize(new Dictionary<string, string> { ["a"] = "1", ["A"] = "2" });
        Assert.ThrowsExactly<NinoDataException>(() => NinoDeserializer.Deserialize(differentCase, ref caselessStrings));
        var strings = new Dictionary<string, string> { ["k"] = "v", ["\u00e9"] = "\u4e2d", [""] = null };
        var back = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        NinoDeserializer.Deserialize(NinoSerializer.Serialize(strings), ref back);
        Assert.AreEqual(3, back.Count);
        Assert.AreEqual("v", back["K"]);
        Assert.AreEqual("\u4e2d", back["\u00e9"]);
        Assert.IsNull(back[""]);
    }
}
