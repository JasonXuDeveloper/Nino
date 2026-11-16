using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nino.Core;

#nullable disable
namespace Nino.UnitTests;

/// <summary>
/// Dictionary and HashSet: small dictionaries refilled through their own comparer, and entries written in enumeration
/// order whether or not some are free.
/// </summary>
[TestClass]
public class DictionaryFastPathTests
{
    [TestMethod]
    public void StringDictionaryRefillKeepsTheSingleKeyUsingItsComparer()
    {
        var target = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["old"] = "stale",
            ["KEEP"] = "previous",
            ["other"] = "stale",
        };
        var original = target;
        var input = new Dictionary<string, string> { ["keep"] = "updated" };

        NinoDeserializer.Deserialize(NinoSerializer.Serialize(input), ref target);

        Assert.AreSame(original, target);
        Assert.AreEqual("KEEP", target.Keys.Single());
        Assert.AreEqual("updated", target["keep"]);
    }

    [TestMethod]
    public void SmallDictionaryRefillKeepsDistinctKeysWhenInputRepeatsAKey()
    {
        var target = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["ONE"] = "old",
            ["two"] = "old",
            ["stale"] = "old",
        };
        var input = new List<KeyValuePair<string, string>>
        {
            new("one", "first"),
            new("One", "last"),
        };
        NinoDeserializer.Deserialize(NinoSerializer.Serialize(input), ref target);
        Assert.AreEqual(1, target.Count);
        Assert.AreEqual("last", target["one"]);
        Assert.IsFalse(target.ContainsKey("stale"));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void SmallDictionaryRefillKeepsThreeKeysInAnyOrder(bool repeated)
    {
        var target = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["ONE"] = "old",
            ["TWO"] = "old",
            ["THREE"] = "old",
            ["stale"] = "old",
        };
        var input = new List<KeyValuePair<string, string>>
        {
            new("three", "third"),
            new("one", "first"),
            new("two", "second"),
        };
        if (repeated)
            input.Add(new("One", "last"));

        NinoDeserializer.Deserialize(NinoSerializer.Serialize(input), ref target);
        Assert.AreEqual(3, target.Count);
        Assert.AreEqual(repeated ? "last" : "first", target["one"]);
        Assert.AreEqual("second", target["two"]);
        Assert.AreEqual("third", target["three"]);
        Assert.IsFalse(target.ContainsKey("stale"));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void SmallDictionaryRefillKeepsFourReorderedKeys(bool collisions)
    {
        IEqualityComparer<string> comparer = collisions
            ? new CollidingStringComparer()
            : StringComparer.OrdinalIgnoreCase;
        var target = new Dictionary<string, string>(comparer)
        {
            ["ONE"] = "old",
            ["TWO"] = "old",
            ["THREE"] = "old",
            ["FOUR"] = "old",
        };
        var input = new Dictionary<string, string>
        {
            ["four"] = "fourth",
            ["three"] = "third",
            ["two"] = "second",
            ["one"] = "first",
        };
        NinoDeserializer.Deserialize(NinoSerializer.Serialize(input), ref target);
        Assert.AreEqual(4, target.Count);
        foreach (var pair in input)
            Assert.AreEqual(pair.Value, target[pair.Key]);
    }

    private sealed class CollidingStringComparer : IEqualityComparer<string>
    {
        public bool Equals(string x, string y) => StringComparer.OrdinalIgnoreCase.Equals(x, y);
        public int GetHashCode(string value) => 0;
    }

    [TestMethod]
    public void SmallDictionaryRefillUsesItsValueTypeKeyComparer()
    {
        var target = new Dictionary<int, string>(new Modulo16Comparer())
        {
            [0] = "old",
            [1] = "old",
            [2] = "old",
            [3] = "stale",
        };
        var input = new Dictionary<int, string>
        {
            [18] = "third",
            [17] = "second",
            [16] = "first",
        };
        NinoDeserializer.Deserialize(NinoSerializer.Serialize(input), ref target);
        Assert.AreEqual(3, target.Count);
        Assert.AreEqual("first", target[0]);
        Assert.AreEqual("second", target[1]);
        Assert.AreEqual("third", target[2]);
        Assert.IsFalse(target.ContainsKey(3));
    }

    private sealed class Modulo16Comparer : IEqualityComparer<int>
    {
        public bool Equals(int x, int y) => (x & 15) == (y & 15);
        public int GetHashCode(int value) => value & 15;
    }

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
