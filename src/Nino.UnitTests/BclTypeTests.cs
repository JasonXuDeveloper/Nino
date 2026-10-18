using System;
using System.Buffers;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nino.Core;

#nullable disable
namespace Nino.UnitTests;

[NinoType]
public class BclTypeHolder
{
    public BigInteger Big;
    public Uri Link;
    public Version Version;
    public StringBuilder Text;
    public BitArray Bits;
    public CultureInfo Culture;
    public TimeZoneInfo Zone;
    public Lazy<string> Lazy;
    public Memory<int> Memory;
    public ReadOnlyMemory<string> Names;
    public ReadOnlySequence<byte> Sequence;
    public ReadOnlyCollection<int> ReadOnly;
    public ReadOnlyObservableCollection<string> Observed;
    public ILookup<string, int> Lookup;
    public IGrouping<int, string> Group;
    public BlockingCollection<int> Blocking;
}

/// <summary>The BCL types with formatters of their own: round trips, null and the bytes they share with other types.</summary>
[TestClass]
public class BclTypeTests
{
    private static T RoundTrip<T>(T value) => NinoDeserializer.Deserialize<T>(NinoSerializer.Serialize(value));

    [TestMethod]
    public void BigIntegers()
    {
        foreach (var value in new[]
                 {
                     BigInteger.Zero, BigInteger.One, BigInteger.MinusOne, new BigInteger(long.MaxValue) * 1000 + 7,
                     -BigInteger.Pow(3, 200), BigInteger.Pow(2, 64), new BigInteger(128), new BigInteger(-129),
                 })
        {
            var bytes = NinoSerializer.Serialize(value);
            CollectionAssert.AreEqual(BitConverter.GetBytes(value.ToByteArray().Length).Concat(value.ToByteArray())
                .ToArray(), bytes);
            Assert.AreEqual(value, NinoDeserializer.Deserialize<BigInteger>(bytes));
        }
    }

    [TestMethod]
    public void UrisVersionsCultures()
    {
        foreach (var uri in new[] { new Uri("https://example.com/a?b=c#d"), new Uri("../relative/path", UriKind.Relative), null })
        {
            var back = RoundTrip(uri);
            Assert.AreEqual(uri?.OriginalString, back?.OriginalString);
            Assert.AreEqual(uri?.IsAbsoluteUri, back?.IsAbsoluteUri);
        }

        CollectionAssert.AreEqual(NinoSerializer.Serialize("https://example.com/"),
            NinoSerializer.Serialize(new Uri("https://example.com/")));

        foreach (var version in new[] { new Version(1, 2), new Version(1, 2, 3), new Version(1, 2, 3, 4), new Version(), null })
        {
            var back = RoundTrip(version);
            Assert.AreEqual(version, back);
            Assert.AreEqual(version?.ToString(), back?.ToString());
        }

        Assert.AreEqual(4, NinoSerializer.Serialize((Version)null).Length);

        foreach (var culture in new[] { CultureInfo.InvariantCulture, CultureInfo.GetCultureInfo("en-US"), null })
            Assert.AreEqual(culture?.Name, RoundTrip(culture)?.Name);
    }

    [TestMethod]
    public void StringBuilders()
    {
        var text = new StringBuilder("héllo, ");
        for (int i = 0; i < 2000; i++)
            text.Append(i); // several chunks
        var bytes = NinoSerializer.Serialize(text);
        CollectionAssert.AreEqual(NinoSerializer.Serialize(text.ToString()), bytes);
        Assert.AreEqual(text.ToString(), NinoDeserializer.Deserialize<StringBuilder>(bytes).ToString());

        // An existing builder is refilled.
        var existing = new StringBuilder("old text that is longer");
        NinoDeserializer.Deserialize(NinoSerializer.Serialize(new StringBuilder("new")), ref existing);
        Assert.AreEqual("new", existing.ToString());

        Assert.IsNull(RoundTrip((StringBuilder)null));
        Assert.AreEqual("", RoundTrip(new StringBuilder()).ToString());
    }

    [TestMethod]
    public void BitArrays()
    {
        foreach (int count in new[] { 0, 1, 7, 8, 9, 64, 1001 })
        {
            var bits = new BitArray(count);
            for (int i = 0; i < count; i++)
                bits[i] = i % 3 == 0;
            var bytes = NinoSerializer.Serialize(bits);
            Assert.AreEqual(4 + (count + 7) / 8, bytes.Length);
            var back = NinoDeserializer.Deserialize<BitArray>(bytes);
            Assert.AreEqual(count, back.Length);
            for (int i = 0; i < count; i++)
                Assert.AreEqual(bits[i], back[i]);
        }

        Assert.IsNull(RoundTrip((BitArray)null));
    }

    [TestMethod]
    public void TimeZones()
    {
        var custom = TimeZoneInfo.CreateCustomTimeZone("Nino/Test", TimeSpan.FromHours(5.5), "Test", "Test Standard");
        var back = RoundTrip(custom);
        Assert.AreEqual(custom.Id, back.Id);
        Assert.AreEqual(custom.BaseUtcOffset, back.BaseUtcOffset);
        Assert.AreEqual(TimeZoneInfo.Utc.BaseUtcOffset, RoundTrip(TimeZoneInfo.Utc).BaseUtcOffset);
        Assert.IsNull(RoundTrip((TimeZoneInfo)null));
    }

    [TestMethod]
    public void Lazies()
    {
        int created = 0;
        var lazy = new Lazy<List<int>>(() =>
        {
            created++;
            return new List<int> { 1, 2, 3 };
        });
        var back = RoundTrip(lazy);
        Assert.AreEqual(1, created);
        Assert.IsTrue(back.IsValueCreated);
        CollectionAssert.AreEqual(lazy.Value, back.Value);
        Assert.IsNull(RoundTrip((Lazy<int>)null));
        Assert.AreEqual(5, RoundTrip(new Lazy<int>(() => 5)).Value);
    }

    [TestMethod]
    public void MemoriesAndSequencesAreArrays()
    {
        var ints = Enumerable.Range(0, 100).ToArray();
        var slice = new Memory<int>(ints, 10, 50);
        CollectionAssert.AreEqual(NinoSerializer.Serialize(ints.Skip(10).Take(50).ToArray()), NinoSerializer.Serialize(slice));
        CollectionAssert.AreEqual(slice.ToArray(), RoundTrip(slice).ToArray());

        ReadOnlyMemory<string> names = new[] { "a", null, "c" };
        CollectionAssert.AreEqual(names.ToArray(), RoundTrip(names).ToArray());
        Assert.AreEqual(0, RoundTrip(Memory<int>.Empty).Length);

        // Several segments, written as one array.
        var first = new Segment<byte>(new byte[] { 1, 2, 3 });
        var last = first.Append(new byte[] { 4, 5 });
        var sequence = new ReadOnlySequence<byte>(first, 0, last, 2);
        Assert.IsFalse(sequence.IsSingleSegment);
        var bytes = NinoSerializer.Serialize(sequence);
        CollectionAssert.AreEqual(NinoSerializer.Serialize(new byte[] { 1, 2, 3, 4, 5 }), bytes);
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3, 4, 5 }, NinoDeserializer.Deserialize<ReadOnlySequence<byte>>(bytes).ToArray());
    }

    private sealed class Segment<T> : ReadOnlySequenceSegment<T>
    {
        public Segment(T[] items) => Memory = items;

        public Segment<T> Append(T[] items)
        {
            var next = new Segment<T>(items) { RunningIndex = RunningIndex + Memory.Length };
            Next = next;
            return next;
        }
    }

    [TestMethod]
    public void ReadOnlyCollectionsAreLists()
    {
        var list = new List<int> { 1, 2, 3 };
        var readOnly = new ReadOnlyCollection<int>(list);
        CollectionAssert.AreEqual(NinoSerializer.Serialize(list), NinoSerializer.Serialize(readOnly));
        var back = RoundTrip(readOnly);
        Assert.IsInstanceOfType(back, typeof(ReadOnlyCollection<int>));
        CollectionAssert.AreEqual(list, back);

        var observed = new ReadOnlyObservableCollection<string>(new ObservableCollection<string> { "x", "y" });
        CollectionAssert.AreEqual(observed, RoundTrip(observed));
        Assert.IsNull(RoundTrip((ReadOnlyCollection<int>)null));
        Assert.IsNull(RoundTrip((ReadOnlyObservableCollection<int>)null));
    }

    [TestMethod]
    public void LookupsAndGroupings()
    {
        var words = new[] { "apple", "avocado", "banana", "blueberry", "cherry", null };
        var lookup = words.ToLookup(w => w?.Substring(0, 1), w => w?.Length ?? 0);
        var back = RoundTrip(lookup);
        Assert.AreEqual(lookup.Count, back.Count);
        foreach (var group in lookup)
            CollectionAssert.AreEqual(group.ToArray(), back[group.Key].ToArray());
        CollectionAssert.AreEqual(lookup.Select(g => g.Key).ToArray(), back.Select(g => g.Key).ToArray());

        var grouping = Enumerable.Range(0, 10).GroupBy(i => i % 3).First(g => g.Key == 1);
        var groupBack = RoundTrip(grouping);
        Assert.AreEqual(1, groupBack.Key);
        CollectionAssert.AreEqual(grouping.ToArray(), groupBack.ToArray());

        Assert.IsNull(RoundTrip((ILookup<int, int>)null));
        Assert.IsNull(RoundTrip((IGrouping<int, int>)null));
        Assert.AreEqual(0, RoundTrip(Array.Empty<int>().ToLookup(i => i)).Count);
    }

    [TestMethod]
    public void BlockingCollections()
    {
        var blocking = new BlockingCollection<int>();
        for (int i = 0; i < 100; i++)
            blocking.Add(i);
        var back = RoundTrip(blocking);
        CollectionAssert.AreEqual(blocking.ToArray(), back.ToArray());
        back.Add(100);
        Assert.AreEqual(0, back.Take());
        Assert.IsNull(RoundTrip((BlockingCollection<int>)null));
    }

    [TestMethod]
    public void AsMembers()
    {
        var holder = new BclTypeHolder
        {
            Big = BigInteger.Pow(10, 40),
            Link = new Uri("https://nino.example/"),
            Version = new Version(4, 0, 0),
            Text = new StringBuilder("text"),
            Bits = new BitArray(new[] { true, false, true }),
            Culture = CultureInfo.GetCultureInfo("fr-FR"),
            Zone = TimeZoneInfo.Utc,
            Lazy = new Lazy<string>(() => "lazy"),
            Memory = new[] { 1, 2 },
            Names = new[] { "n" },
            Sequence = new ReadOnlySequence<byte>(new byte[] { 9 }),
            ReadOnly = new ReadOnlyCollection<int>(new[] { 7 }),
            Observed = new ReadOnlyObservableCollection<string>(new ObservableCollection<string> { "o" }),
            Lookup = new[] { 1, 2, 3 }.ToLookup(i => i % 2 == 0 ? "even" : "odd"),
            Group = new[] { "g" }.GroupBy(s => s.Length).Single(),
            Blocking = new BlockingCollection<int> { 5 },
        };
        var back = RoundTrip(holder);
        Assert.AreEqual(holder.Big, back.Big);
        Assert.AreEqual(holder.Link, back.Link);
        Assert.AreEqual(holder.Version, back.Version);
        Assert.AreEqual("text", back.Text.ToString());
        Assert.IsTrue(back.Bits[0] && !back.Bits[1] && back.Bits[2]);
        Assert.AreEqual("fr-FR", back.Culture.Name);
        Assert.AreEqual(TimeZoneInfo.Utc.Id, back.Zone.Id);
        Assert.AreEqual("lazy", back.Lazy.Value);
        CollectionAssert.AreEqual(new[] { 1, 2 }, back.Memory.ToArray());
        CollectionAssert.AreEqual(new[] { "n" }, back.Names.ToArray());
        CollectionAssert.AreEqual(new byte[] { 9 }, back.Sequence.ToArray());
        CollectionAssert.AreEqual(new[] { 7 }, back.ReadOnly);
        CollectionAssert.AreEqual(new[] { "o" }, back.Observed);
        CollectionAssert.AreEqual(new[] { 1, 3 }, back.Lookup["odd"].ToArray());
        Assert.AreEqual(1, back.Group.Key);
        CollectionAssert.AreEqual(new[] { 5 }, back.Blocking.ToArray());

        var empty = RoundTrip(new BclTypeHolder());
        Assert.IsNull(empty.Link);
        Assert.IsNull(empty.Lookup);
        Assert.IsNull(empty.Lazy);
        Assert.AreEqual(0, empty.Memory.Length);
        Assert.IsTrue(empty.Sequence.IsEmpty);
    }

    [TestMethod]
    public void TruncatedDataThrows()
    {
        var bytes = NinoSerializer.Serialize(BigInteger.Pow(2, 100));
        Assert.ThrowsExactly<InvalidOperationException>(() => NinoDeserializer.Deserialize<BigInteger>(bytes.AsSpan(0, bytes.Length - 1)));
        var bits = NinoSerializer.Serialize(new BitArray(100));
        Assert.ThrowsExactly<InvalidOperationException>(() => NinoDeserializer.Deserialize<BitArray>(bits.AsSpan(0, bits.Length - 1)));
        var text = NinoSerializer.Serialize(new StringBuilder("abc"));
        Assert.ThrowsExactly<InvalidOperationException>(() => NinoDeserializer.Deserialize<StringBuilder>(text.AsSpan(0, text.Length - 1)));
    }

    [TestMethod]
    public void ImmutableCollectionsShareTheBytesOfTheirMutableCounterparts()
    {
        var ints = new[] { 5, 3, 9, 1 };
        var set = ImmutableHashSet.CreateRange(ints);
        CollectionAssert.AreEqual(NinoSerializer.Serialize(new HashSet<int>(set)), NinoSerializer.Serialize(set));
        CollectionAssert.AreEquivalent(ints, RoundTrip(set).ToArray());

        var sorted = ImmutableSortedSet.CreateRange(new[] { "b", "a", "c" });
        CollectionAssert.AreEqual(NinoSerializer.Serialize(new SortedSet<string>(sorted)), NinoSerializer.Serialize(sorted));
        CollectionAssert.AreEqual(sorted.ToArray(), RoundTrip(sorted).ToArray());

        var queue = ImmutableQueue.CreateRange(ints);
        CollectionAssert.AreEqual(NinoSerializer.Serialize(new Queue<int>(ints)), NinoSerializer.Serialize(queue));
        CollectionAssert.AreEqual(ints, RoundTrip(queue).ToArray());

        var stack = ImmutableStack.CreateRange(new[] { "bottom", "middle", "top" });
        CollectionAssert.AreEqual(NinoSerializer.Serialize(new Stack<string>(new[] { "bottom", "middle", "top" })),
            NinoSerializer.Serialize(stack));
        var stackBack = RoundTrip(stack);
        Assert.AreEqual("top", stackBack.Peek());
        CollectionAssert.AreEqual(stack.ToArray(), stackBack.ToArray());

        var dictionary = ImmutableDictionary.CreateRange(ints.Select(i => new KeyValuePair<int, string>(i, i.ToString())));
        CollectionAssert.AreEqual(NinoSerializer.Serialize(new Dictionary<int, string>(dictionary)),
            NinoSerializer.Serialize(dictionary));
        CollectionAssert.AreEquivalent(dictionary.ToArray(), RoundTrip(dictionary).ToArray());

        var sortedDictionary = ImmutableSortedDictionary.CreateRange(dictionary);
        CollectionAssert.AreEqual(NinoSerializer.Serialize(new SortedDictionary<int, string>(sortedDictionary)),
            NinoSerializer.Serialize(sortedDictionary));
        CollectionAssert.AreEqual(sortedDictionary.ToArray(), RoundTrip(sortedDictionary).ToArray());

        Assert.IsNull(RoundTrip((ImmutableHashSet<int>)null));
        Assert.IsNull(RoundTrip((ImmutableSortedSet<int>)null));
        Assert.IsNull(RoundTrip((ImmutableQueue<int>)null));
        Assert.IsNull(RoundTrip((ImmutableStack<int>)null));
        Assert.IsNull(RoundTrip((ImmutableDictionary<int, int>)null));
        Assert.IsNull(RoundTrip((ImmutableSortedDictionary<int, int>)null));
        Assert.IsTrue(RoundTrip(ImmutableQueue<int>.Empty).IsEmpty);
        Assert.IsTrue(RoundTrip(ImmutableStack<int>.Empty).IsEmpty);
    }

    [TestMethod]
    public void ImmutableDuplicatesAreAClearError()
    {
        var twice = NinoSerializer.Serialize(new List<int> { 1, 1 });
        Assert.ThrowsExactly<InvalidOperationException>(() => NinoDeserializer.Deserialize<ImmutableHashSet<int>>(twice));
        Assert.ThrowsExactly<InvalidOperationException>(() => NinoDeserializer.Deserialize<ImmutableSortedSet<int>>(twice));

        foreach (var pairs in new[]
                 {
                     new List<KeyValuePair<int, int>> { new(1, 2), new(1, 2) },
                     new List<KeyValuePair<int, int>> { new(1, 2), new(1, 3) },
                 })
        {
            var bytes = NinoSerializer.Serialize(pairs);
            Assert.ThrowsExactly<InvalidOperationException>(() =>
                NinoDeserializer.Deserialize<ImmutableDictionary<int, int>>(bytes));
            Assert.ThrowsExactly<InvalidOperationException>(() =>
                NinoDeserializer.Deserialize<ImmutableSortedDictionary<int, int>>(bytes));
        }
    }
}
