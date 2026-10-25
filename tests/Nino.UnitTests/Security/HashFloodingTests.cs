using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nino.Core;
using Nino.Core.Internal;
using static Nino.UnitTests.Rt;

namespace Nino.UnitTests;

/// <summary>A key whose hash code is one of 7: many keys share one, as keys built to collide do.</summary>
[NinoType]
public sealed class FloodKey : IEquatable<FloodKey>
{
    public int Id;

    public bool Equals(FloodKey other) => other != null && other.Id == Id;

    public override bool Equals(object obj) => Equals(obj as FloodKey);

    public override int GetHashCode() => Id % 7;
}

[NinoType]
public sealed class FloodHolder
{
    public Dictionary<long, int> Map;
    public HashSet<long> Set;
    public Dictionary<long, string> Names;
    public Dictionary<FloodKey, int> Keyed;
    public HashSet<FloodKey> KeySet;
}

/// <summary>
/// Keys built so that they share a hash bucket (hash flooding) would make reading a hash table quadratic: more than
/// NinoSettings.MaxHashCollisions (100) of them in one bucket is invalid data, wherever the table is filled.
/// </summary>
[TestClass]
public class HashFloodingTests
{
    private const int Max = NinoSettings.DefaultMaxHashCollisions;

    /// <summary><paramref name="count"/> longs that all hash to <paramref name="hash"/> (long's hash: low ^ high).</summary>
    private static IEnumerable<long> Colliding(int count, int hash = 0) =>
        Enumerable.Range(1, count).Select(i => (long)i << 32 | (uint)(i ^ hash));

    private static void Rejected<T>(T value)
    {
        var data = NinoSerializer.Serialize(value);
        var e = Assert.ThrowsExactly<NinoDataException>(() => NinoDeserializer.Deserialize<T>(data), typeof(T).Name);
        StringAssert.Contains(e.Message, "MaxHashCollisions");
    }

    [TestMethod]
    public void UpToTheLimitInOneBucketIsRead()
    {
        // The last key finds Max others in its bucket.
        var keys = Colliding(Max + 1).ToArray();
        Assert.AreEqual(Max + 1, RoundTrip(keys.ToDictionary(k => k, k => (int)k)).Count);
        Assert.AreEqual(Max + 1, RoundTrip(keys.ToHashSet()).Count);
        Assert.AreEqual(Max + 1, RoundTrip(keys.ToDictionary(k => k, k => k.ToString())).Count);
        var flood = Enumerable.Range(0, Max + 1).Select(i => new FloodKey { Id = i * 7 }).ToArray();
        var back = RoundTrip(flood.ToDictionary(k => k, k => k.Id));
        Assert.AreEqual(Max + 1, back.Count);
        Assert.AreEqual(14, back[new FloodKey { Id = 14 }]);
        Assert.AreEqual(Max + 1, RoundTrip(flood.ToHashSet()).Count);
        Assert.AreEqual(Max + 1, RoundTrip(keys.ToImmutableHashSet()).Count);
        Assert.AreEqual(Max + 1, RoundTrip(keys.ToImmutableDictionary(k => k, k => 1)).Count);
    }

    [TestMethod]
    public void MoreThanTheLimitInOneBucketIsInvalidData()
    {
        var keys = Colliding(Max + 2).ToArray();
        // Raw pairs and elements (filled directly), pairs read one by one, keys that are objects.
        Rejected(keys.ToDictionary(k => k, k => (int)k));
        Rejected(keys.ToHashSet());
        Rejected(keys.ToDictionary(k => k, k => k.ToString()));
        var flood = Enumerable.Range(0, Max + 2).Select(i => new FloodKey { Id = i * 7 }).ToArray();
        Rejected(flood.ToDictionary(k => k, k => k.Id));
        Rejected(flood.ToHashSet());
        Rejected(new FloodHolder { Keyed = flood.ToDictionary(k => k, k => 0) });
        Rejected(keys.ToLookup(k => k, k => 1));
        Rejected(keys.ToImmutableHashSet());
        Rejected(keys.ToImmutableDictionary(k => k, k => 1));
        Rejected<IDictionary<long, int>>(keys.ToDictionary(k => k, k => 0));
        Rejected<ISet<long>>(keys.ToHashSet());
    }

    [TestMethod]
    public void ExistingTablesAreCheckedToo()
    {
        var data = NinoSerializer.Serialize(Colliding(Max + 2).ToDictionary(k => k, k => 1));
        // Refilled through its own comparer (by the dictionary itself): its buckets are walked afterwards.
        var existing = new Dictionary<long, int>(new LongComparer()) { [5] = 5 };
        Assert.ThrowsExactly<NinoDataException>(() => NinoDeserializer.Deserialize(data, ref existing));
        var set = new HashSet<long>(new LongComparer()) { 5 };
        Assert.ThrowsExactly<NinoDataException>(() =>
            NinoDeserializer.Deserialize(NinoSerializer.Serialize(Colliding(Max + 2).ToHashSet()), ref set));
        var holder = new FloodHolder { Map = new Dictionary<long, int> { [1] = 1 } };
        var flooded = NinoSerializer.Serialize(new FloodHolder { Map = Colliding(Max + 2).ToDictionary(k => k, k => 1) });
        Assert.ThrowsExactly<NinoDataException>(() => NinoDeserializer.Deserialize(flooded, ref holder));
    }

    [TestMethod]
    public void ExistingDictionaryIsRefilledThroughItsComparer()
    {
        // Raw pairs read into a dictionary with its own comparer are hashed by that comparer, not filled as a new one.
        var dictionary = new Dictionary<int, int>(new ModuloComparer()) { [1] = 1 };
        NinoDeserializer.Deserialize(NinoSerializer.Serialize(new Dictionary<int, int> { [3] = 30, [4] = 40 }),
            ref dictionary);
        Assert.AreEqual(2, dictionary.Count);
        Assert.AreEqual(30, dictionary[13]);
        Assert.AreEqual(40, dictionary[24]);
    }

    [TestMethod]
    public void ManyKeysInFullBucketsReadInLinearTime()
    {
        // 50,000 keys, Max + 1 per hash code: every bucket as full as the limit allows. Each key walks at most Max
        // others, so the read takes about Max times as long as with spread keys, not quadratic time (with 50,000 in
        // one bucket: about 25 times longer again). Generous bounds, for slow CI machines.
        const int perHash = Max + 1, hashes = 495;
        // Hash codes 0 to 494: each its own bucket in a table of 50,000.
        var keys = Enumerable.Range(0, hashes).SelectMany(h => Colliding(perHash, h)).ToArray();
        Assert.AreEqual(hashes * perHash, keys.Distinct().Count());
        var map = NinoSerializer.Serialize(keys.ToDictionary(k => k, k => 1));
        var set = NinoSerializer.Serialize(keys.ToHashSet());
        NinoDeserializer.Deserialize<Dictionary<long, int>>(map);
        var watch = Stopwatch.StartNew();
        Assert.AreEqual(keys.Length, NinoDeserializer.Deserialize<Dictionary<long, int>>(map).Count);
        Assert.AreEqual(keys.Length, NinoDeserializer.Deserialize<HashSet<long>>(set).Count);
        Assert.IsTrue(watch.Elapsed < TimeSpan.FromSeconds(10), $"{watch.Elapsed}");

        // All in one bucket: rejected after Max + 2 keys, at once.
        var flood = NinoSerializer.Serialize(Colliding(50_000).ToDictionary(k => k, k => 1));
        watch.Restart();
        Assert.ThrowsExactly<NinoDataException>(() => NinoDeserializer.Deserialize<Dictionary<long, int>>(flood));
        Assert.IsTrue(watch.Elapsed < TimeSpan.FromSeconds(2), $"{watch.Elapsed}");
    }

    [TestMethod]
    public void ConcurrentDictionariesAreCheckedToo()
    {
        // Its buckets are walked through its private table, where this runtime's layout was found (.NET 8+ here).
        Assert.IsTrue(ConcurrentTables.Plan<long, int>.Walks);
        Assert.IsTrue(ConcurrentTables.Plan<FloodKey, int>.Walks);
        // String keys are left to the runtime, which switches to a randomized comparer itself.
        Assert.IsFalse(ConcurrentTables.Plan<string, int>.Walks);
        // A new one is built here, its buckets counted as they fill.
        Assert.IsTrue(ConcurrentTables.Plan<long, int>.Builds);
        Assert.IsTrue(ConcurrentTables.Plan<long, string>.Builds);
        Assert.IsTrue(ConcurrentTables.Plan<FloodKey, int>.Builds);
        Assert.IsFalse(ConcurrentTables.Plan<string, int>.Builds);

        var keys = Colliding(Max + 1).ToArray();
        var back = RoundTrip(new ConcurrentDictionary<long, int>(keys.ToDictionary(k => k, k => (int)k)));
        Assert.AreEqual(Max + 1, back.Count);
        foreach (long key in keys)
            Assert.AreEqual((int)key, back[key]);
        var flood = Enumerable.Range(0, Max + 1).Select(i => new FloodKey { Id = i * 7 }).ToArray();
        var keyed = RoundTrip(new ConcurrentDictionary<FloodKey, int>(flood.ToDictionary(k => k, k => k.Id)));
        Assert.AreEqual(Max + 1, keyed.Count);
        Assert.AreEqual(14, keyed[new FloodKey { Id = 14 }]);
        // Still a dictionary that grows and takes concurrent writers as one built by its constructor does.
        for (int i = 0; i < 1000; i++)
            back.TryAdd(-i - 1, i);
        Assert.AreEqual(Max + 1001, back.Count);

        keys = Colliding(Max + 2).ToArray();
        Rejected(new ConcurrentDictionary<long, int>(keys.ToDictionary(k => k, k => 0)));
        Rejected(new ConcurrentDictionary<long, string>(keys.ToDictionary(k => k, k => k.ToString())));
        flood = Enumerable.Range(0, Max + 2).Select(i => new FloodKey { Id = i * 7 }).ToArray();
        Rejected(new ConcurrentDictionary<FloodKey, int>(flood.ToDictionary(k => k, k => 0)));

        // Read into an existing one (through its TryAdd), also with its own comparer.
        var data = NinoSerializer.Serialize(new ConcurrentDictionary<long, int>(keys.ToDictionary(k => k, k => 1)));
        var existing = new ConcurrentDictionary<long, int>(new LongComparer()) { [5] = 5 };
        Assert.ThrowsExactly<NinoDataException>(() => NinoDeserializer.Deserialize(data, ref existing));
        var plain = new ConcurrentDictionary<long, int> { [5] = 5 };
        Assert.ThrowsExactly<NinoDataException>(() => NinoDeserializer.Deserialize(data, ref plain));
        // As an interface the existing instance is read through.
        IDictionary<long, int> asInterface = new ConcurrentDictionary<long, int>();
        Assert.ThrowsExactly<NinoDataException>(() => NinoDeserializer.Deserialize(data, ref asInterface));
    }

    [TestMethod]
    public void ConcurrentDictionariesCountBucketsNotOnlyEqualHashCodes()
    {
        // Distinct hash codes, one bucket: multiples of the bucket count the dictionary is made with for that many.
        int buckets = ConcurrentTables.BucketsOf(ConcurrentTables.Create<int, int>(Max + 1)).Length;
        var keys = Enumerable.Range(0, Max + 1).Select(i => i * buckets).ToArray();
        Assert.AreEqual(Max + 1, RoundTrip(new ConcurrentDictionary<int, int>(keys.ToDictionary(k => k, k => k))).Count);
        buckets = ConcurrentTables.BucketsOf(ConcurrentTables.Create<int, int>(Max + 2)).Length;
        keys = Enumerable.Range(0, Max + 2).Select(i => i * buckets).ToArray();
        Rejected(new ConcurrentDictionary<int, int>(keys.ToDictionary(k => k, k => k)));
    }

    [TestMethod]
    public void ConcurrentDictionariesBuiltHereWorkAsBuiltByTheirConstructor()
    {
        // A key repeated far into the data, with references in the nodes.
        var pairs = Enumerable.Range(0, 60).Select(i => new KeyValuePair<long, string>(i, "v" + i)).ToList();
        pairs.Add(new KeyValuePair<long, string>(37, "again"));
        var e = Assert.ThrowsExactly<NinoDataException>(() =>
            NinoDeserializer.Deserialize<ConcurrentDictionary<long, string>>(NinoSerializer.Serialize(pairs.ToArray())));
        StringAssert.Contains(e.Message, "twice");

        const int count = 5000;
        var source = new ConcurrentDictionary<long, string>(
            Enumerable.Range(0, count).ToDictionary(i => (long)i * 31, i => "v" + i));
        var data = NinoSerializer.Serialize(source);
        var built = new List<ConcurrentDictionary<long, string>>();
        for (int i = 0; i < 20; i++)
        {
            built.Add(NinoDeserializer.Deserialize<ConcurrentDictionary<long, string>>(data));
            GC.Collect(2, GCCollectionMode.Forced, true, true);
        }

        foreach (var dictionary in built)
        {
            Assert.AreEqual(count, dictionary.Count);
            CollectionAssert.AreEquivalent(source.ToList(), dictionary.ToList());
        }

        // Removes, updates and concurrent adds that grow the table.
        var read = built[0];
        for (long i = 0; i < count; i += 2)
            Assert.IsTrue(read.TryRemove(i * 31, out var removed) && removed == "v" + i);
        read.AddOrUpdate(31, "new", (_, _) => "updated");
        Assert.AreEqual("updated", read[31]);
        System.Threading.Tasks.Parallel.For(0, 40_000, i => Assert.IsTrue(read.TryAdd(-i - 1, "n" + i)));
        GC.Collect(2, GCCollectionMode.Forced, true, true);
        Assert.AreEqual(count / 2 + 40_000, read.Count);
        for (long i = 0; i < count; i++)
            Assert.AreEqual(i % 2 == 1, read.ContainsKey(i * 31));
        for (int i = 0; i < 40_000; i++)
            Assert.AreEqual("n" + i, read[-i - 1]);
    }

    [TestMethod]
    public void ConcurrentDictionaryInFullBucketsReadsInLinearTime()
    {
        const int perHash = Max + 1, hashes = 495;
        var keys = Enumerable.Range(0, hashes).SelectMany(h => Colliding(perHash, h)).ToArray();
        var map = NinoSerializer.Serialize(new ConcurrentDictionary<long, int>(keys.ToDictionary(k => k, k => 1)));
        NinoDeserializer.Deserialize<ConcurrentDictionary<long, int>>(map);
        var watch = Stopwatch.StartNew();
        Assert.AreEqual(keys.Length, NinoDeserializer.Deserialize<ConcurrentDictionary<long, int>>(map).Count);
        Assert.IsTrue(watch.Elapsed < TimeSpan.FromSeconds(10), $"{watch.Elapsed}");

        // All in one bucket: rejected after Max + 2 keys, at once (not after 50,000 keys walked 25,000 each). A
        // dictionary's bytes are a concurrent one's.
        var flood = NinoSerializer.Serialize(Colliding(50_000).ToDictionary(k => k, k => 1));
        watch.Restart();
        Assert.ThrowsExactly<NinoDataException>(() =>
            NinoDeserializer.Deserialize<ConcurrentDictionary<long, int>>(flood));
        Assert.IsTrue(watch.Elapsed < TimeSpan.FromSeconds(2), $"{watch.Elapsed}");
    }

    private sealed class LongComparer : IEqualityComparer<long>
    {
        public bool Equals(long x, long y) => x == y;
        public int GetHashCode(long value) => value.GetHashCode();
    }

    private sealed class ModuloComparer : IEqualityComparer<int>
    {
        public bool Equals(int x, int y) => x % 10 == y % 10;
        public int GetHashCode(int value) => value % 10;
    }
}
