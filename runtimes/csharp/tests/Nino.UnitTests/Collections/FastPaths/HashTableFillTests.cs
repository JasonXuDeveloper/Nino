using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nino.Core;

#nullable disable
namespace Nino.UnitTests;

/// <summary>
/// New sets and dictionaries of raw elements are filled through their entries and buckets (HashTables): the same
/// contents, order and behavior as the public API gives, and a repeated element still throws.
/// </summary>
[TestClass]
public class HashTableFillTests
{
    internal static Guid[] CollidingGuids(int n) =>
        Enumerable.Range(0, n).Select(i => new Guid(i, (short)i, 7, 1, 2, 3, 4, 5, 6, 7, 8)).ToArray();

    internal static void CheckSet<T>(T[] items, T missing)
    {
        var source = new HashSet<T>(items);
        var back = NinoDeserializer.Deserialize<HashSet<T>>(NinoSerializer.Serialize(source));
        Assert.AreEqual(source.Count, back.Count);
        CollectionAssert.AreEqual(source.ToArray(), back.ToArray());
        foreach (var item in source)
            Assert.IsTrue(back.Contains(item));
        Assert.IsFalse(back.Contains(missing));
        Assert.IsTrue(back.Add(missing));
        Assert.IsTrue(back.Remove(items[0]));
        Assert.IsFalse(back.Contains(items[0]));
        Assert.AreEqual(source.Count, back.Count);

        var repeated = items.Concat(new[] { items[items.Length / 2] }).ToArray();
        Assert.ThrowsExactly<NinoDataException>(() =>
            NinoDeserializer.Deserialize<HashSet<T>>(NinoSerializer.Serialize(repeated)));
    }

    private static void CheckDictionary<TKey, TValue>(TKey[] keys, Func<TKey, TValue> value, TKey missing)
    {
        var source = keys.ToDictionary(k => k, value);
        var back = NinoDeserializer.Deserialize<Dictionary<TKey, TValue>>(NinoSerializer.Serialize(source));
        CollectionAssert.AreEqual(source.ToArray(), back.ToArray());
        foreach (var pair in source)
            Assert.AreEqual(pair.Value, back[pair.Key]);
        Assert.IsFalse(back.ContainsKey(missing));
        back[missing] = value(missing);
        Assert.IsTrue(back.Remove(keys[0]));
        Assert.AreEqual(source.Count, back.Count);

        var repeated = source.Concat(new[] { source.First() }).ToArray();
        Assert.ThrowsExactly<NinoDataException>(() =>
            NinoDeserializer.Deserialize<Dictionary<TKey, TValue>>(NinoSerializer.Serialize(repeated)));
    }

    [TestMethod]
    public void RawSetsRoundTrip()
    {
        CheckSet(Enumerable.Range(0, 1000).Select(i => i * 3 - 1500).ToArray(), 1);
        CheckSet(Enumerable.Range(0, 300).Select(i => (long)i << 33 ^ -i).Concat(new[] { long.MinValue, long.MaxValue })
            .ToArray(), 42L);
        // Guids of one hash code (the halves of their first eight bytes cancel out): up to
        // NinoSettings.MaxHashCollisions here, and 200 rejected; read in full under a raised limit by
        // SettingsTests.CollidingRawSetUnderARaisedLimit.
        CheckSet(CollidingGuids(NinoSettings.DefaultMaxHashCollisions), Guid.Empty);
        Assert.ThrowsExactly<NinoDataException>(() =>
            NinoDeserializer.Deserialize<HashSet<Guid>>(NinoSerializer.Serialize(new HashSet<Guid>(CollidingGuids(200)))));
        CheckSet(new[] { 0.5, -0.0, double.MaxValue, double.Epsilon }, 1.5);
        CheckSet(Enumerable.Range(0, 100).Select(i => new PaddedItem { Id = i % 10, Count = (short)i }).ToArray(),
            new PaddedItem { Id = -1 });
    }

    [TestMethod]
    public void RawDictionariesRoundTrip()
    {
        CheckDictionary(Enumerable.Range(0, 2000).Select(i => i * 11 - 300).ToArray(), i => i * 2L, 7);
        CheckDictionary(Enumerable.Range(0, 64).Select(i => (long)i << 32 | (uint)i).ToArray(), k => (byte)k, 3L);
        CheckDictionary(Enumerable.Range(0, 100).Select(i => new Guid(i, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10)).ToArray(),
            g => (double)g.GetHashCode(), Guid.Empty);
    }

    [TestMethod]
    public void ExistingSetIsRefilledThroughItsComparer()
    {
        // Not new: read element by element, keeping its comparer.
        var set = new HashSet<int>(new ModuloComparer()) { 1 };
        NinoDeserializer.Deserialize(NinoSerializer.Serialize(new HashSet<int> { 3, 4 }), ref set);
        Assert.IsTrue(set.Contains(13));
        Assert.AreEqual(2, set.Count);
    }

    private sealed class ModuloComparer : IEqualityComparer<int>
    {
        public bool Equals(int x, int y) => x % 10 == y % 10;
        public int GetHashCode(int value) => value % 10;
    }

    /// <summary>
    /// Pairs of a key or value that is not raw are read into a new dictionary's entries and linked after the read from
    /// four pairs on (NinoDictionary.ReadEntries, HashTables.Link), one at a time below: the same contents either way,
    /// a repeated or null key rejected, and a read that fails part way leaves the dictionary it filled empty.
    /// </summary>
    [TestMethod]
    public void EntriesFillOfDictionariesWithObjects()
    {
        for (int n = 1; n <= 9; n++)
        {
            var ints = Enumerable.Range(0, n).ToDictionary(i => i * 7 - 20, i => Enumerable.Range(0, i).ToList());
            var intsBack = NinoDeserializer.Deserialize<Dictionary<int, List<int>>>(NinoSerializer.Serialize(ints));
            CollectionAssert.AreEqual(ints.Keys.ToArray(), intsBack.Keys.ToArray(), $"n = {n}");
            foreach (var pair in ints)
                CollectionAssert.AreEqual(pair.Value, intsBack[pair.Key]);
            Assert.IsFalse(intsBack.ContainsKey(1000));
            intsBack[1000] = null;
            Assert.IsTrue(intsBack.Remove(-20));
            Assert.AreEqual(n, intsBack.Count);

            var strings = Enumerable.Range(0, n).ToDictionary(i => "k" + i, i => new[] { i, -i });
            var stringsBack = NinoDeserializer.Deserialize<Dictionary<string, int[]>>(NinoSerializer.Serialize(strings));
            CollectionAssert.AreEqual(strings.Keys.ToArray(), stringsBack.Keys.ToArray(), $"n = {n}");
            Assert.AreEqual(n - 1, stringsBack["k" + (n - 1)][0]);

            var repeated = ints.Concat(new[] { ints.Last() }).ToList();
            StringAssert.Contains(Assert.ThrowsExactly<NinoDataException>(() =>
                NinoDeserializer.Deserialize<Dictionary<int, List<int>>>(NinoSerializer.Serialize(repeated))).Message,
                "twice");
            var nullKey = strings.Select(p => new KeyValuePair<string, int[]>(p.Key, p.Value))
                .Append(new KeyValuePair<string, int[]>(null, new int[0])).ToList();
            StringAssert.Contains(Assert.ThrowsExactly<NinoDataException>(() =>
                NinoDeserializer.Deserialize<Dictionary<string, int[]>>(NinoSerializer.Serialize(nullKey))).Message,
                "null key");
        }

        // Cut inside its last value: the existing empty dictionary read into is empty again afterwards.
        var bytes = NinoSerializer.Serialize(Enumerable.Range(0, 8).ToDictionary(i => i, i => new List<int> { i, i }));
        var target = new Dictionary<int, List<int>>();
        Assert.ThrowsExactly<NinoDataException>(() =>
            NinoDeserializer.Deserialize(bytes.AsSpan(0, bytes.Length - 2).ToArray(), ref target));
        Assert.AreEqual(0, target.Count);
        Assert.IsFalse(target.ContainsKey(0));
        NinoDeserializer.Deserialize(bytes, ref target);
        Assert.AreEqual(8, target.Count);
        CollectionAssert.AreEqual(new List<int> { 7, 7 }, target[7]);
    }
}
