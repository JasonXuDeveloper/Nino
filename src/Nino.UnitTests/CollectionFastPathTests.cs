using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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
        // Raw element blocks of 0 to 40 bytes (copied inline up to 32), and strings of 0 to 20 chars.
        for (int n = 0; n <= 40; n++)
        {
            var block = Enumerable.Range(0, n).Select(i => (byte)(i * 7 + n)).ToArray();
            CollectionAssert.AreEqual(block, NinoDeserializer.Deserialize<byte[]>(NinoSerializer.Serialize(block)));
            var shorts = block.Select(b => (short)(b * 300)).Take(n / 2).ToList();
            CollectionAssert.AreEqual(shorts, NinoDeserializer.Deserialize<List<short>>(NinoSerializer.Serialize(shorts)));
        }

        for (int n = 0; n <= 20; n++)
        {
            var text = new string(Enumerable.Range(0, n).Select(i => (char)('a' + i)).ToArray());
            Assert.AreEqual(text, NinoDeserializer.Deserialize<string>(NinoSerializer.Serialize(text)));
            var words = new[] { text, null, text + "é" };
            CollectionAssert.AreEqual(words, NinoDeserializer.Deserialize<string[]>(NinoSerializer.Serialize(words)));
        }
    }
}
