using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nino.Core;
using Nino.Core.Internal;

namespace Nino.UnitTests
{
    /// <summary>[NinoUtf8] collections: the formatter resolved for the member writes its strings as UTF-8.</summary>
    [NinoType(format: NinoFormat.VersionTolerant)]
    public sealed class CursorUtf8Holder
    {
        [NinoUtf8] public List<string> Tags;
        [NinoUtf8] public string[] Names;
        [NinoUtf8] public HashSet<string> Set;
        [NinoUtf8] public Dictionary<string, int> Counts;
        [NinoUtf8] public Dictionary<int, string> Labels;
    }

    /// <summary>
    /// Strings, raw values and blocks, and pairs of them in any pairing, are written and read by one cursor loop over
    /// values, a pair one value whose formatter composes its items, their data, position and end in registers (<see cref="NinoCursor"/>, #347): the bytes of the formatters'
    /// own writes, every collection that reaches the loops (arrays, lists, sets, dictionaries and the pairs of sorted
    /// and concurrent ones), new and reused, and the checks of hostile data per pair.
    /// </summary>
    [TestClass]
    public class CursorTests
    {
        private static readonly string[] Texts =
        {
            "a", null, "", "héllo", "日本語", new string('x', 70), new string('é', 200), "z\uD83D\uDE00", "end",
        };

        private static string Text(int i) => Texts[i % Texts.Length] is { } s ? s + (i / Texts.Length) : null;

        /// <summary>What the cursor loop must write: the count, then each key and value through its formatter.</summary>
        private static byte[] Expected<TKey, TValue, TKeyFormatter, TValueFormatter>(
            ICollection<KeyValuePair<TKey, TValue>> pairs)
            where TKeyFormatter : struct, INinoFormatter<TKey>
            where TValueFormatter : struct, INinoFormatter<TValue>
        {
            var buffer = new NinoArrayBufferWriter();
            var writer = new Writer(buffer);
            writer.WriteLength(pairs.Count);
            foreach (var pair in pairs)
            {
                default(TKeyFormatter).Serialize(pair.Key, ref writer);
                default(TValueFormatter).Serialize(pair.Value, ref writer);
            }

            writer.Flush();
            return buffer.ToArray();
        }

        private static byte[] Write<T, TFormatter>(T value) where TFormatter : struct, INinoFormatter<T>
        {
            var buffer = new NinoArrayBufferWriter();
            var writer = new Writer(buffer);
            default(TFormatter).Serialize(value, ref writer);
            writer.Flush();
            return buffer.ToArray();
        }

        private static T Read<T, TFormatter>(byte[] bytes, T value = default)
            where TFormatter : struct, INinoFormatter<T>
        {
            var reader = new Reader(bytes);
            default(TFormatter).Deserialize(ref reader, ref value);
            Assert.AreEqual(0, reader.Remaining);
            return value;
        }

        /// <summary>
        /// A dictionary of <paramref name="count"/> pairs, a few removed (free entries the write skips) when
        /// <paramref name="holes"/>: written as the formatters write each pair, read back new and into an existing
        /// dictionary (the merge), and its data cut anywhere rejected.
        /// </summary>
        private static void Pairs<TKey, TValue, TKeyFormatter, TValueFormatter>(Func<int, TKey> key,
            Func<int, TValue> value, int count, bool holes)
            where TKeyFormatter : struct, INinoFormatter<TKey>
            where TValueFormatter : struct, INinoFormatter<TValue>
        {
            Assert.IsTrue(NinoCursor.Pairs<TKey, TValue, TKeyFormatter, TValueFormatter>());
            var dictionary = new Dictionary<TKey, TValue>();
            for (int i = 0; i < count + (holes ? 3 : 0); i++)
                dictionary[key(i)] = value(i);
            if (holes)
                for (int i = 0; i < 3; i++)
                    dictionary.Remove(key(i * 2));
            string name = $"{typeof(TKey).Name}/{typeof(TValue).Name}/{typeof(TKeyFormatter).Name}/" +
                          $"{typeof(TValueFormatter).Name}, {count}";

            byte[] bytes = Write<Dictionary<TKey, TValue>,
                DictionaryFormatter<TKey, TValue, TKeyFormatter, TValueFormatter>>(dictionary);
            CollectionAssert.AreEqual(Expected<TKey, TValue, TKeyFormatter, TValueFormatter>(dictionary), bytes, name);

            var read = Read<Dictionary<TKey, TValue>,
                DictionaryFormatter<TKey, TValue, TKeyFormatter, TValueFormatter>>(bytes);
            CollectionAssert.AreEquivalent(dictionary.ToList(), read.ToList(), name);

            // Into the dictionary just read (each value read over its own), and into one with other pairs.
            var again = Read<Dictionary<TKey, TValue>,
                DictionaryFormatter<TKey, TValue, TKeyFormatter, TValueFormatter>>(bytes, read);
            Assert.AreSame(read, again);
            CollectionAssert.AreEquivalent(dictionary.ToList(), again.ToList(), name);
            var other = new Dictionary<TKey, TValue> { [key(count + 10)] = value(1) };
            foreach (var pair in dictionary.Take(2))
                other[pair.Key] = value(5);
            CollectionAssert.AreEquivalent(dictionary.ToList(), Read<Dictionary<TKey, TValue>,
                DictionaryFormatter<TKey, TValue, TKeyFormatter, TValueFormatter>>(bytes, other).ToList(), name);

            for (int cut = 0; cut < bytes.Length; cut += Math.Max(1, bytes.Length / 97))
            {
                var prefix = bytes.AsSpan(0, cut).ToArray();
                Assert.ThrowsExactly<NinoDataException>(() => Read<Dictionary<TKey, TValue>,
                    DictionaryFormatter<TKey, TValue, TKeyFormatter, TValueFormatter>>(prefix), $"{name}, cut {cut}");
                var existing = new Dictionary<TKey, TValue>(dictionary);
                Assert.ThrowsExactly<NinoDataException>(() => Read<Dictionary<TKey, TValue>,
                    DictionaryFormatter<TKey, TValue, TKeyFormatter, TValueFormatter>>(prefix, existing),
                    $"{name}, cut {cut}, into an existing one");
            }
        }

        /// <summary>
        /// Every pairing of strings (both encodings), raw values and blocks: the four string and raw pairings
        /// (<c>Dictionary&lt;string, int&gt;</c> among them, which the string loops left to the general one), UTF-8,
        /// a block (<see cref="DateTimeOffset"/>), sizes that take the read's fill (from HashTables.FillFrom pairs) and
        /// its adds, and strings long enough to grow the buffer while the loop writes.
        /// </summary>
        [TestMethod]
        public void EveryPairingTakesThePairLoop()
        {
            foreach (int count in new[] { 1, 3, 4, 40, 700 })
            foreach (bool holes in new[] { false, true })
            {
                Pairs<string, int, StringFormatter, UnmanagedFormatter<int>>(i => "k" + i + Text(i), i => i, count,
                    holes);
                Pairs<int, string, UnmanagedFormatter<int>, StringFormatter>(i => i, Text, count, holes);
                Pairs<string, string, StringFormatter, StringFormatter>(i => "k" + i, Text, count, holes);
                Pairs<string, string, Utf8StringFormatter, Utf8StringFormatter>(i => "ü" + i, Text, count, holes);
                Pairs<string, int, Utf8StringFormatter, UnmanagedFormatter<int>>(i => Text(i) + i, i => i, count,
                    holes);
                Pairs<string, DateTimeOffset, StringFormatter, DateTimeOffsetFormatter>(i => "d" + i,
                    i => new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.FromMinutes(i % 600)), count, holes);
                Pairs<bool, string, UnmanagedFormatter<bool>, StringFormatter>(i => i % 2 == 0, Text, Math.Min(count, 1),
                    false);
            }
        }

        /// <summary>
        /// Strings in a row (arrays, lists, sets with free entries) in both encodings: the element loop writes the
        /// bytes of each string's write, reads them back, and keeps an element's string read again over the same chars.
        /// </summary>
        [TestMethod]
        public void StringsInARowTakeTheElementLoop()
        {
            var values = Enumerable.Range(0, 500).Select(Text).ToArray();
            Elements<StringFormatter>(values);
            Elements<Utf8StringFormatter>(values);

            var set = new HashSet<string>(values.Where(v => v != null).Take(100));
            set.Remove(values[2]);
            set.Remove(values[11]);
            byte[] bytes = Write<HashSet<string>, HashSetFormatter<HashSet<string>, string, StringFormatter>>(set);
            CollectionAssert.AreEqual(Expected<StringFormatter>(set.ToArray()), bytes);
            CollectionAssert.AreEquivalent(set.ToList(),
                Read<HashSet<string>, HashSetFormatter<HashSet<string>, string, StringFormatter>>(bytes).ToList());
        }

        /// <summary>What the element loop must write: the count, then each string through its formatter.</summary>
        private static byte[] Expected<TFormatter>(string[] values) where TFormatter : struct, INinoFormatter<string>
        {
            var buffer = new NinoArrayBufferWriter();
            var writer = new Writer(buffer);
            writer.WriteLength(values.Length);
            foreach (var value in values)
                default(TFormatter).Serialize(value, ref writer);
            writer.Flush();
            return buffer.ToArray();
        }

        private static void Elements<TFormatter>(string[] values) where TFormatter : struct, INinoFormatter<string>
        {
            byte[] expected = Expected<TFormatter>(values);

            CollectionAssert.AreEqual(expected, Write<string[], ArrayFormatter<string, TFormatter>>(values));
            CollectionAssert.AreEqual(expected,
                Write<List<string>, ListFormatter<List<string>, string, TFormatter>>(values.ToList()));
            var array = Read<string[], ArrayFormatter<string, TFormatter>>(expected);
            CollectionAssert.AreEqual(values, array);

            // Read again into the same array: short ASCII strings kept, not allocated anew (both encodings compare
            // them with the data).
            var kept = (string[])array.Clone();
            var again = Read<string[], ArrayFormatter<string, TFormatter>>(expected, array);
            CollectionAssert.AreEqual(values, again);
            for (int i = 0; i < values.Length; i++)
                if (kept[i] is { Length: > 0 and < 63 } s && s.All(c => c < 0x80))
                    Assert.AreSame(kept[i], again[i], $"{typeof(TFormatter).Name} [{i}]");

            for (int cut = 0; cut < expected.Length; cut += 7)
            {
                var prefix = expected.AsSpan(0, cut).ToArray();
                Assert.ThrowsExactly<NinoDataException>(() => Read<string[], ArrayFormatter<string, TFormatter>>(prefix));
            }
        }

        /// <summary>
        /// [NinoUtf8] collection members: their strings are UTF-8 on the wire (the formatter resolved for the member)
        /// and round trip through the loops.
        /// </summary>
        [TestMethod]
        public void Utf8MembersTakeTheLoops()
        {
            var value = new CursorUtf8Holder
            {
                Tags = Enumerable.Range(0, 50).Select(Text).ToList(),
                Names = Enumerable.Range(0, 9).Select(Text).ToArray(),
                Set = new HashSet<string>(Enumerable.Range(1, 30).Select(i => "s" + i + Text(i))),
                Counts = Enumerable.Range(0, 20).ToDictionary(i => "c" + i + Text(i), i => i),
                Labels = Enumerable.Range(0, 20).ToDictionary(i => i, Text),
            };
            var copy = NinoDeserializer.Deserialize<CursorUtf8Holder>(NinoSerializer.Serialize(value));
            CollectionAssert.AreEqual(value.Tags, copy.Tags);
            CollectionAssert.AreEqual(value.Names, copy.Names);
            CollectionAssert.AreEquivalent(value.Set.ToList(), copy.Set.ToList());
            CollectionAssert.AreEquivalent(value.Counts.ToList(), copy.Counts.ToList());
            CollectionAssert.AreEquivalent(value.Labels.ToList(), copy.Labels.ToList());

            // "é" is two bytes in UTF-8 (one Latin-1 byte in the default encoding): its char count plus one, its byte
            // count, then the bytes.
            CollectionAssert.AreEqual(new byte[] { 2, 2, 0xC3, 0xA9 },
                Write<string[], ArrayFormatter<string, Utf8StringFormatter>>(new[] { "é" }).Skip(4).ToArray());
        }

        /// <summary>
        /// Sorted and concurrent dictionaries move their pairs through a rented array of pairs, whose element is the
        /// pair: with strings in it, the element loop over the pair's cursor form. Their bytes are a dictionary's.
        /// </summary>
        [TestMethod]
        public void SortedAndConcurrentPairsTakeTheElementLoop()
        {
            var pairs = Enumerable.Range(0, 300).ToDictionary(i => "k" + i.ToString("D4"), Text);
            var sorted = new SortedDictionary<string, string>(pairs, StringComparer.Ordinal);
            var bytes = NinoSerializer.Serialize(sorted);
            CollectionAssert.AreEqual(Expected<string, string, StringFormatter, StringFormatter>(sorted), bytes);
            CollectionAssert.AreEqual(sorted.ToList(),
                NinoDeserializer.Deserialize<SortedDictionary<string, string>>(bytes).ToList());

            var sortedList = new SortedList<int, string>(Enumerable.Range(0, 50).ToDictionary(i => i, Text));
            CollectionAssert.AreEqual(sortedList.ToList(), NinoDeserializer.Deserialize<SortedList<int, string>>(
                NinoSerializer.Serialize(sortedList)).ToList());

            var concurrent = new ConcurrentDictionary<string, int>(
                Enumerable.Range(0, 200).Select(i => new KeyValuePair<string, int>("c" + i + Text(i), i)));
            var read = NinoDeserializer.Deserialize<ConcurrentDictionary<string, int>>(
                NinoSerializer.Serialize(concurrent));
            CollectionAssert.AreEquivalent(concurrent.ToList(), read.ToList());

            Assert.IsTrue(NinoKind<KeyValuePairFormatter<string, string, StringFormatter, StringFormatter>>.Cursor);
            Assert.IsTrue(NinoKind<KeyValuePairFormatter<int, string, UnmanagedFormatter<int>, StringFormatter>>.Cursor);
            Assert.IsFalse(NinoKind<KeyValuePairFormatter<string, RunTextKey, StringFormatter,
                NinoGen.Nino_UnitTests_RunTextKeyFormatter>>.Cursor);
        }

        /// <summary>
        /// Hostile pairs are rejected per pair as before: a null key (in the fill, the adds and the merge), a key twice,
        /// and a string whose length runs past the data, before anything is allocated for it.
        /// </summary>
        [TestMethod]
        public void HostilePairsAreRejected()
        {
            foreach (int count in new[] { 2, 8 })
            {
                var pairs = Enumerable.Range(0, count).ToDictionary(i => "k" + i, i => i);
                var bytes = NinoSerializer.Serialize(pairs);

                // The first key null: its header (one byte) becomes 0, the bytes of "k0" (header 2 chars) are dropped.
                var nullKey = new byte[bytes.Length - 2];
                Array.Copy(bytes, nullKey, 4);
                nullKey[4] = 0;
                Array.Copy(bytes, 7, nullKey, 5, bytes.Length - 7);
                StringAssert.Contains(Assert.ThrowsExactly<NinoDataException>(() =>
                    NinoDeserializer.Deserialize<Dictionary<string, int>>(nullKey)).Message, "null key");
                var existing = new Dictionary<string, int> { ["x"] = 1 };
                StringAssert.Contains(Assert.ThrowsExactly<NinoDataException>(() =>
                    NinoDeserializer.Deserialize(nullKey, ref existing)).Message, "null key");

                // The second key the same as the first.
                var twice = (byte[])bytes.Clone();
                Array.Copy(bytes, 4, twice, 4 + 7, 3);
                var e = Assert.ThrowsExactly<NinoDataException>(() =>
                    NinoDeserializer.Deserialize<Dictionary<string, int>>(twice));
                StringAssert.Contains(e.Message, "twice");

                // A string of 2^29 chars with 4 bytes left.
                var huge = (byte[])bytes.Clone();
                huge[4] = 0xFF;
                Assert.ThrowsExactly<NinoDataException>(() => NinoDeserializer.Deserialize<Dictionary<string, int>>(huge));

                var stringKeys = Enumerable.Range(0, count).ToDictionary(i => i, i => "v" + i);
                var raw = NinoSerializer.Serialize(stringKeys);
                Array.Copy(raw, 4, raw, 4 + 7, 4);
                Assert.ThrowsExactly<NinoDataException>(() => NinoDeserializer.Deserialize<Dictionary<int, string>>(raw));
            }
        }
    }
}
