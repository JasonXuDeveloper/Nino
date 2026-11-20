using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nino.Core;
using Nino.Core.Internal;

#nullable disable
namespace Nino.UnitTests;

/// <summary>
/// The entries of sets and dictionaries are the elements of their runs (storage elements: the value, or the key and
/// value, at their offsets in the entry, the hash code and link skipped), and blocks where their items are: the block
/// kernels pack a run of entries into the wire's bytes and scatter the data into new entries, which are then linked.
/// The bytes are the pairs' and values' as a list writes them, for every count the vector paths split differently;
/// hostile data is rejected, and the collection read into is left empty.
/// </summary>
[TestClass]
public class EntryBlockTests
{
    private static readonly int[] Counts =
        Enumerable.Range(0, 70).Concat(new[] { 127, 128, 129, 255, 256, 1000, 1031 }).ToArray();

    [TestMethod]
    public void EntriesOfValuesAreBlocks()
    {
        Assert.IsTrue(CollectionFields.EntryBlocks.Mapped);
        Assert.AreEqual(NinoElementKind.Block, NinoKind.Of<CollectionFields.SetEntry<int>,
            NinoSetEntryFormatter<int, UnmanagedFormatter<int>>>());
        // A block item (a tuple) inside a dictionary's entry: still one block of the entry.
        Assert.AreEqual(NinoElementKind.Block, NinoKind.Of<CollectionFields.MapEntry<int, (int, long)>,
            NinoMapEntryFormatter<int, (int, long), UnmanagedFormatter<int>,
                ValueTupleFormatter<int, long, UnmanagedFormatter<int>, UnmanagedFormatter<long>>>>());
    }

    [TestMethod]
    public void EntryBlocksWriteTheBytesOfTheirItems()
    {
        foreach (int n in Counts)
        {
            Pairs(n, i => i * 7 - 3, i => -i);
            Pairs(n, i => (long)i << 33, i => (short)i);
            Pairs(n, i => (short)i, i => (long)i * 99);
            if (n <= 256)
                Pairs(n, i => (byte)i, i => (long)i);
            Pairs(n, i => i, i => new Guid(i, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10));
            Pairs(n, i => i, i => (i, (long)-i));
            Pairs(n, i => (long)i, i => i % 3 == 0);
            Values(n, i => i * 13);
            Values(n, i => (long)i << 20);
            Values(n, i => new Guid(i, 9, 8, 7, 6, 5, 4, 3, 2, 1, 0));
            Values(n, i => (i, (short)-i));
        }
    }

    // A dictionary writes what a list of its pairs writes, whole and with entries freed, and reads back the same
    // pairs in the same order.
    private static void Pairs<TKey, TValue>(int n, Func<int, TKey> key, Func<int, TValue> value)
    {
        var pairs = Enumerable.Range(0, n).Select(i => new KeyValuePair<TKey, TValue>(key(i), value(i))).ToList();
        var dictionary = pairs.ToDictionary(p => p.Key, p => p.Value);
        var bytes = NinoSerializer.Serialize(pairs);
        CollectionAssert.AreEqual(bytes, NinoSerializer.Serialize(dictionary), $"{typeof(TKey)}, {typeof(TValue)} x{n}");
        CollectionAssert.AreEqual(pairs, NinoDeserializer.Deserialize<Dictionary<TKey, TValue>>(bytes).ToList());
        if (n < 3)
            return;
        dictionary.Remove(pairs[1].Key);
        dictionary.Remove(pairs[n - 1].Key);
        var left = dictionary.ToList();
        CollectionAssert.AreEqual(NinoSerializer.Serialize(left), NinoSerializer.Serialize(dictionary));
    }

    private static void Values<T>(int n, Func<int, T> value)
    {
        var values = Enumerable.Range(0, n).Select(value).ToList();
        var set = new HashSet<T>(values);
        var bytes = NinoSerializer.Serialize(values);
        CollectionAssert.AreEqual(bytes, NinoSerializer.Serialize(set), $"{typeof(T)} x{n}");
        CollectionAssert.AreEqual(values, NinoDeserializer.Deserialize<HashSet<T>>(bytes).ToList());
        if (n < 3)
            return;
        set.Remove(values[0]);
        set.Remove(values[n / 2]);
        CollectionAssert.AreEqual(NinoSerializer.Serialize(set.ToList()), NinoSerializer.Serialize(set));
    }

    /// <summary>
    /// Counts the data cannot hold, repeated keys and values, and bools other than 0 or 1 in entry blocks are invalid
    /// data, whatever count of entries the kernels split; a dictionary or set read into is left empty and usable.
    /// </summary>
    [TestMethod]
    public void HostileEntryBlocksAreRejected()
    {
        foreach (int n in new[] { 1, 2, 3, 4, 5, 7, 8, 9, 15, 16, 17, 31, 32, 33, 64, 100, 257 })
        {
            var map = Enumerable.Range(0, n).ToDictionary(i => (long)i * 3, i => i % 2 == 0);
            var data = NinoSerializer.Serialize(map);
            // A count one more than the data holds, and the data one byte short.
            Rejected<Dictionary<long, bool>>(With(data, 0, BitConverter.GetBytes(n + 1)), "count");
            Rejected<Dictionary<long, bool>>(data.AsSpan(0, data.Length - 1).ToArray(), "short");
            // A bool of 2 in the first, middle and last entry: 4 + 9 * i + 8.
            foreach (int i in new[] { 0, n / 2, n - 1 })
                Rejected<Dictionary<long, bool>>(With(data, 4 + 9 * i + 8, 2), "bool " + i);
            if (n > 1)
            {
                // The last key the same as the first.
                Rejected<Dictionary<long, bool>>(With(data, 4 + 9 * (n - 1), BitConverter.GetBytes(0L)), "key");
                var ints = NinoSerializer.Serialize(new HashSet<int>(Enumerable.Range(0, n)));
                Rejected<HashSet<int>>(With(ints, 4 + 4 * (n - 1), BitConverter.GetBytes(0)), "value");
            }

            var counted = NinoSerializer.Serialize(new HashSet<int>(Enumerable.Range(0, n)));
            Rejected<HashSet<int>>(With(counted, 0, BitConverter.GetBytes(int.MaxValue)), "set count");
        }
    }

    // Rejected as a new collection, and read into an existing one, which is then empty and usable.
    private static void Rejected<TCollection>(byte[] data, string what) where TCollection : class, new()
    {
        Assert.ThrowsExactly<NinoDataException>(() => NinoDeserializer.Deserialize<TCollection>(data), what);
        var existing = new TCollection();
        Assert.ThrowsExactly<NinoDataException>(() => NinoDeserializer.Deserialize(data, ref existing), what);
        Assert.AreEqual(0, Count(existing), what);
    }

    private static int Count(object collection) => ((System.Collections.IEnumerable)collection).Cast<object>().Count();

    private static byte[] With(byte[] data, int at, params byte[] bytes)
    {
        var copy = (byte[])data.Clone();
        bytes.CopyTo(copy, at);
        return copy;
    }

    /// <summary>
    /// More keys than <see cref="NinoSettings.MaxHashCollisions"/> in one bucket, filled through the entry block and
    /// linked, are invalid data: longs whose halves cancel all hash to 0.
    /// </summary>
    [TestMethod]
    public void FloodedEntryBlocksAreRejected()
    {
        var keys = Enumerable.Range(1, NinoSettings.DefaultMaxHashCollisions + 2)
            .Select(i => (long)i << 32 | (uint)i).ToArray();
        Rejected<Dictionary<long, bool>>(NinoSerializer.Serialize(keys.ToDictionary(k => k, _ => true)), "map");
        Rejected<HashSet<long>>(NinoSerializer.Serialize(new HashSet<long>(keys)), "set");
    }

#if NET8_0_OR_GREATER
    /// <summary>
    /// Entry blocks that end the data right before an inaccessible page: the kernels' vectors stay inside the data for
    /// every count, and an entry cut short is invalid data.
    /// </summary>
    [TestMethod]
    public void EntryBlocksAreReadWithinTheData()
    {
        foreach (int n in Counts)
        {
            AtEnd(Enumerable.Range(0, n).ToDictionary(i => i, i => -i));
            AtEnd(Enumerable.Range(0, n).ToDictionary(i => (long)i, i => (short)i));
            if (n <= 256)
                AtEnd(Enumerable.Range(0, n).ToDictionary(i => (byte)i, i => (long)i));
            AtEnd(Enumerable.Range(0, n).ToDictionary(i => i, i => (i, (long)i)));
            AtEnd(new HashSet<int>(Enumerable.Range(0, n)));
            AtEnd(new HashSet<long>(Enumerable.Range(0, n).Select(i => (long)i)));
        }

        static void AtEnd<TCollection>(TCollection value) where TCollection : System.Collections.IEnumerable
        {
            var data = NinoSerializer.Serialize(value);
            var at = GuardedMemory.AtEnd(data);
            if (at.IsEmpty)
                return;
            Assert.AreEqual(Count(value), Count(NinoDeserializer.Deserialize<TCollection>(at)));
            if (Count(value) == 0)
                return;
            var cut = GuardedMemory.AtEnd(data.AsSpan(0, data.Length - 1));
            try
            {
                NinoDeserializer.Deserialize<TCollection>(cut);
                Assert.Fail("read past the data");
            }
            catch (NinoDataException)
            {
            }
        }
    }
#endif
}
