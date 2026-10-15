using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nino.Core;

#nullable disable
namespace Nino.UnitTests;

[NinoType]
public class ReuseHolder
{
    public Dictionary<int, string> Names;
    public Dictionary<string, ReuseItem> Items;
}

[NinoType]
public class ReuseItem
{
    public int Value;
    public List<int> Data;
}

/// <summary>Ref deserialization reuses dictionaries in place, merging by key.</summary>
[TestClass]
public class RefReuseTests
{
    [TestMethod]
    public void DictionariesMergeByKey()
    {
        var keptItem = new ReuseItem { Value = 1, Data = new List<int> { 1 } };
        var keptList = keptItem.Data;
        var dropped = new ReuseItem { Value = 2 };
        var target = new ReuseHolder
        {
            Names = new Dictionary<int, string> { [1] = "one", [2] = "two", [3] = "three" },
            Items = new Dictionary<string, ReuseItem> { ["a"] = keptItem, ["b"] = dropped },
        };
        var names = target.Names;

        NinoDeserializer.Deserialize(NinoSerializer.Serialize(new ReuseHolder
        {
            Names = new Dictionary<int, string> { [1] = "one", [4] = "four" },
            Items = new Dictionary<string, ReuseItem>
                { ["a"] = new() { Value = 10, Data = new List<int> { 7, 8 } }, ["c"] = new() { Value = 3 } },
        }), ref target);

        Assert.AreSame(names, target.Names);
        CollectionAssert.AreEquivalent(new[] { 1, 4 }, new List<int>(target.Names.Keys));
        Assert.AreEqual("one", target.Names[1]);
        Assert.AreEqual("four", target.Names[4]);

        Assert.AreEqual(2, target.Items.Count);
        Assert.AreSame(keptItem, target.Items["a"]);
        Assert.AreSame(keptList, keptItem.Data);
        Assert.AreEqual(10, keptItem.Value);
        CollectionAssert.AreEqual(new[] { 7, 8 }, keptItem.Data);
        Assert.AreEqual(3, target.Items["c"].Value);
        Assert.IsFalse(target.Items.ContainsKey("b"));
    }

    [TestMethod]
    public void RemovedDictionaryValuesAreRecycled()
    {
        var recycled = new List<ReuseItem>();
        NinoSettings.SetRecycler<ReuseItem>(recycled.Add);
        try
        {
            var dropped = new ReuseItem();
            var target = new ReuseHolder { Items = new Dictionary<string, ReuseItem> { ["a"] = new(), ["b"] = dropped } };
            NinoDeserializer.Deserialize(NinoSerializer.Serialize(new ReuseHolder
                { Items = new Dictionary<string, ReuseItem> { ["a"] = new() } }), ref target);
            CollectionAssert.AreEqual(new[] { dropped }, recycled);
        }
        finally
        {
            NinoSettings.SetRecycler<ReuseItem>(null);
        }
    }

    [TestMethod]
    public void EmptyDataClearsTheDictionary()
    {
        var target = new ReuseHolder { Names = new Dictionary<int, string> { [1] = "a" } };
        NinoDeserializer.Deserialize(NinoSerializer.Serialize(new ReuseHolder { Names = new Dictionary<int, string>() }), ref target);
        Assert.AreEqual(0, target.Names.Count);
    }

    [TestMethod]
    public void RepeatedKeyDoesNotKeepStaleEntries()
    {
        // The data repeats key 1 (as a dictionary written under another comparer can): the pair count matches the
        // dictionary's, but key 2 is gone, and the last value of key 1 wins.
        var bytes = NinoSerializer.Serialize(new List<KeyValuePair<int, string>>
            { new(1, "x"), new(1, "y") });
        var target = new Dictionary<int, string> { [1] = "a", [2] = "b" };
        NinoDeserializer.Deserialize(bytes, ref target);
        Assert.AreEqual(1, target.Count);
        Assert.AreEqual("y", target[1]);

        // The same keys in another order are merged in place without dropping anything.
        target = new Dictionary<int, string> { [2] = "b", [1] = "a" };
        var same = target;
        NinoDeserializer.Deserialize(NinoSerializer.Serialize(new Dictionary<int, string> { [1] = "c", [2] = "d" }),
            ref target);
        Assert.AreSame(same, target);
        Assert.AreEqual(2, target.Count);
        Assert.AreEqual("c", target[1]);
        Assert.AreEqual("d", target[2]);
    }

    [TestMethod]
    public void GrownListGetsNewElements()
    {
        // Slots past a list's count are never stale: grown elements are new instances, not ones dropped earlier.
        var list = new List<ReuseItem> { new() { Value = 1 }, new() { Value = 2 }, new() { Value = 3 } };
        var dropped = list[2];
        list.RemoveAt(2);
        NinoDeserializer.Deserialize(NinoSerializer.Serialize(new List<ReuseItem>
            { new() { Value = 4 }, new() { Value = 5 }, new() { Value = 6 } }), ref list);
        Assert.AreEqual(3, list.Count);
        Assert.AreNotSame(dropped, list[2]);
        Assert.AreEqual(6, list[2].Value);
        Assert.AreEqual(3, dropped.Value);
    }
}
