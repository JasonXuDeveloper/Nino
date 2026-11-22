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
    /// Strings in either encoding, and pairs of a string with a string, a raw value or a block, are written and read by
    /// the string run kernels (<see cref="NinoStringRuns"/>, #355), each item at its offset in the element, their data,
    /// position and end in registers: the bytes of the formatters' own writes, every collection that reaches them
    /// (arrays, lists, sets, dictionaries and the pairs of sorted and concurrent ones), new and reused, and hostile
    /// data: every length of a run checked before a string is allocated, truncated data, invalid UTF-8, repeated keys
    /// and counts the data cannot hold.
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
            Assert.IsTrue(NinoItems.Pair<TKey, TValue, TKeyFormatter, TValueFormatter>());
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
                // Slots' values (a raw T?), present and absent, beside a string either way round.
                Pairs<string, int?, StringFormatter, UnmanagedNullableFormatter<int>>(i => "n" + i,
                    i => i % 3 == 0 ? null : i, count, holes);
                Pairs<string, long?, Utf8StringFormatter, UnmanagedNullableFormatter<long>>(i => "ü" + i,
                    i => i % 4 == 1 ? null : -i, count, holes);
                Pairs<int?, string, UnmanagedNullableFormatter<int>, StringFormatter>(i => i, Text, count, holes);
            }
        }

        /// <summary>
        /// Value tuples of three items or more whose items are strings, raw values, blocks and slots' values take the
        /// string run kernels (<see cref="NinoItems"/>) in arrays and lists: the bytes of each item's own write, read
        /// back new and over existing values, and every cut of the data rejected.
        /// </summary>
        [TestMethod]
        public void LongTuplesTakeTheItemChain()
        {
            Assert.IsTrue(NinoKind<ValueTupleFormatter<string, int, string, StringFormatter, UnmanagedFormatter<int>,
                StringFormatter>>.Cursor);
            Assert.IsTrue(NinoKind<ValueTupleFormatter<int?, string, DateTimeOffset, long,
                UnmanagedNullableFormatter<int>, Utf8StringFormatter, DateTimeOffsetFormatter,
                UnmanagedFormatter<long>>>.Cursor);
            // An item that is neither a string nor moves beside one: value by value.
            Assert.IsFalse(NinoKind<ValueTupleFormatter<string, int, int[], StringFormatter, UnmanagedFormatter<int>,
                UnmanagedArrayFormatter<int>>>.Cursor);

            foreach (int count in new[] { 1, 5, 300 })
            {
                var triples = Enumerable.Range(0, count).Select(i => (Text(i), i, Text(i + 3))).ToArray();
                Tuples<(string, int, string), ValueTupleFormatter<string, int, string, StringFormatter,
                    UnmanagedFormatter<int>, StringFormatter>>(triples, (ref Writer w, (string, int, string) t) =>
                {
                    default(StringFormatter).Serialize(t.Item1, ref w);
                    default(UnmanagedFormatter<int>).Serialize(t.Item2, ref w);
                    default(StringFormatter).Serialize(t.Item3, ref w);
                });

                var mixed = Enumerable.Range(0, count).Select(i => (i % 3 == 0 ? (int?)null : i, Text(i),
                    new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.FromMinutes(i % 600)), (long)-i)).ToArray();
                Tuples<(int?, string, DateTimeOffset, long), ValueTupleFormatter<int?, string, DateTimeOffset, long,
                    UnmanagedNullableFormatter<int>, Utf8StringFormatter, DateTimeOffsetFormatter,
                    UnmanagedFormatter<long>>>(mixed, (ref Writer w, (int?, string, DateTimeOffset, long) t) =>
                {
                    default(UnmanagedNullableFormatter<int>).Serialize(t.Item1, ref w);
                    default(Utf8StringFormatter).Serialize(t.Item2, ref w);
                    default(DateTimeOffsetFormatter).Serialize(t.Item3, ref w);
                    default(UnmanagedFormatter<long>).Serialize(t.Item4, ref w);
                });

                // Seven items, the longest plan: every bottom item kind, strings first and last.
                var seven = Enumerable.Range(0, count).Select(i => (Text(i), i, i % 2 == 0 ? (long?)null : -i,
                    Text(i + 1), (byte)i, Guid.Empty, Text(i + 2))).ToArray();
                Tuples<(string, int, long?, string, byte, Guid, string), ValueTupleFormatter<string, int, long?, string,
                    byte, Guid, string, StringFormatter, UnmanagedFormatter<int>, UnmanagedNullableFormatter<long>,
                    Utf8StringFormatter, UnmanagedFormatter<byte>, UnmanagedFormatter<Guid>, StringFormatter>>(seven,
                    (ref Writer w, (string, int, long?, string, byte, Guid, string) t) =>
                    {
                        default(StringFormatter).Serialize(t.Item1, ref w);
                        default(UnmanagedFormatter<int>).Serialize(t.Item2, ref w);
                        default(UnmanagedNullableFormatter<long>).Serialize(t.Item3, ref w);
                        default(Utf8StringFormatter).Serialize(t.Item4, ref w);
                        default(UnmanagedFormatter<byte>).Serialize(t.Item5, ref w);
                        default(UnmanagedFormatter<Guid>).Serialize(t.Item6, ref w);
                        default(StringFormatter).Serialize(t.Item7, ref w);
                    });
            }

            Assert.IsTrue(NinoKind<ValueTupleFormatter<string, int, long?, string, byte, Guid, string, StringFormatter,
                UnmanagedFormatter<int>, UnmanagedNullableFormatter<long>, Utf8StringFormatter, UnmanagedFormatter<byte>,
                UnmanagedFormatter<Guid>, StringFormatter>>.Cursor);
        }

        private delegate void ItemsWrite<T>(ref Writer writer, T value);

        private static void Tuples<T, TFormatter>(T[] values, ItemsWrite<T> items)
            where TFormatter : struct, INinoFormatter<T>
        {
            var buffer = new NinoArrayBufferWriter();
            var writer = new Writer(buffer);
            writer.WriteLength(values.Length);
            foreach (var value in values)
                items(ref writer, value);
            writer.Flush();
            byte[] expected = buffer.ToArray();
            byte[] array = Write<T[], ArrayFormatter<T, TFormatter>>(values);
            CollectionAssert.AreEqual(expected, array, typeof(T).Name);
            CollectionAssert.AreEqual(expected, Write<List<T>, ListFormatter<List<T>, T, TFormatter>>(values.ToList()));
            CollectionAssert.AreEqual(values, Read<T[], ArrayFormatter<T, TFormatter>>(array));
            CollectionAssert.AreEqual(values, Read<List<T>, ListFormatter<List<T>, T, TFormatter>>(array));
            var existing = (T[])values.Clone();
            CollectionAssert.AreEqual(values, Read<T[], ArrayFormatter<T, TFormatter>>(array, existing));
            for (int cut = 0; cut < array.Length; cut += Math.Max(1, array.Length / 61))
            {
                var prefix = array.AsSpan(0, cut).ToArray();
                Assert.ThrowsExactly<NinoDataException>(() => Read<T[], ArrayFormatter<T, TFormatter>>(prefix),
                    $"{typeof(T).Name}, cut {cut}");
            }
        }

        /// <summary>
        /// A slot's value beside a string (<see cref="NinoSlotItem{T,TFormatter,TSize}"/>) rejects a presence byte other than
        /// 0 or 1 and a present value cut short, as its single read does, in a new dictionary and in a merge.
        /// </summary>
        [TestMethod]
        public void SlotValuesBesideStringsRejectBadPresence()
        {
            var pairs = new Dictionary<string, int?> { ["a"] = 1, ["b"] = null, ["c"] = 3 };
            var bytes = NinoSerializer.Serialize(pairs);
            CollectionAssert.AreEquivalent(pairs.ToList(),
                NinoDeserializer.Deserialize<Dictionary<string, int?>>(bytes).ToList());
            // Count (4), "a" (header and one char: 2 bytes), then the presence byte of 1.
            const int presence = 6;
            Assert.AreEqual(1, bytes[presence]);
            var bad = (byte[])bytes.Clone();
            bad[presence] = 2;
            Assert.ThrowsExactly<NinoDataException>(() => NinoDeserializer.Deserialize<Dictionary<string, int?>>(bad));
            var existing = new Dictionary<string, int?>(pairs);
            Assert.ThrowsExactly<NinoDataException>(() => NinoDeserializer.Deserialize(bad, ref existing));
            Assert.ThrowsExactly<NinoDataException>(() =>
                NinoDeserializer.Deserialize<Dictionary<string, int?>>(bytes.AsSpan(0, presence + 3).ToArray()));
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

        /// <summary>The bytes of <paramref name="values"/> as <typeparamref name="TFormatter"/> writes a run of
        /// them (the count, then each string).</summary>
        private static byte[] Run<TFormatter>(string[] values) where TFormatter : struct, INinoFormatter<string> =>
            Write<string[], ArrayFormatter<string, TFormatter>>(values);

        /// <summary>
        /// Whether <paramref name="data"/>, a run of strings, is rejected as an array, a list and a set, and read into
        /// an existing array of its count without changing the elements from <paramref name="bad"/> on: each string's
        /// lengths are checked before it is read, and the read stops at the first that is out of range.
        /// </summary>
        private static void Rejected<TFormatter>(byte[] data, int count, int bad, string what)
            where TFormatter : struct, INinoFormatter<string>
        {
            Assert.ThrowsExactly<NinoDataException>(() => Read<string[], ArrayFormatter<string, TFormatter>>(data),
                what);
            Assert.ThrowsExactly<NinoDataException>(() =>
                Read<List<string>, ListFormatter<List<string>, string, TFormatter>>(data), what);
            Assert.ThrowsExactly<NinoDataException>(() =>
                Read<HashSet<string>, HashSetFormatter<HashSet<string>, string, TFormatter>>(data), what);
            var existing = Enumerable.Range(0, count).Select(i => "old" + i).ToArray();
            var before = (string[])existing.Clone();
            Assert.ThrowsExactly<NinoDataException>(() =>
                Read<string[], ArrayFormatter<string, TFormatter>>(data, existing), what);
            for (int i = bad; i < count; i++)
                Assert.AreSame(before[i], existing[i], $"{what}: [{i}] read past the rejected string");
        }

        /// <summary>
        /// A length out of range at the first, a middle and the last string of a run (one past the data, a header
        /// byte no write makes, a header that a shorter one holds) is rejected before that string is allocated, and
        /// nothing after it is read, in both encodings; so is a count the data cannot hold (before any string is
        /// read), and a UTF-8 string of more chars than bytes.
        /// </summary>
        [TestMethod]
        public void BadLengthsAnywhereInARunAreRejected()
        {
            var values = Enumerable.Range(0, 5).Select(i => "abc" + i).ToArray();
            byte[] data = Run<StringFormatter>(values);
            foreach (int index in new[] { 0, 2, 4 })
            {
                // Each string is a header (9: Latin-1, 4 chars) then 4 bytes, after the 4-byte count.
                int at = 4 + 5 * index;
                Rejected<StringFormatter>(With(data, at, 0x7F), 5, index, $"past the data at [{index}]");
                Rejected<StringFormatter>(With(data, at, 0x82), 5, index, $"header out of range at [{index}]");
                Rejected<StringFormatter>(With(data, at, 0x80, 0x10, 0x00), 5, index,
                    $"header not minimal at [{index}]");
                Rejected<StringFormatter>(data.AsSpan(0, at + 3).ToArray(), 5, index, $"cut in [{index}]");
            }

            // UTF-8: the char count plus one (5), the byte count (4), the bytes.
            byte[] utf8 = Run<Utf8StringFormatter>(values);
            foreach (int index in new[] { 0, 2, 4 })
            {
                int at = 4 + 6 * index;
                Rejected<Utf8StringFormatter>(With(utf8, at + 1, 0x7F), 5, index, $"UTF-8 past the data [{index}]");
                Rejected<Utf8StringFormatter>(With(utf8, at, 0x7F), 5, index, $"UTF-8 more chars [{index}]");
                Rejected<Utf8StringFormatter>(With(utf8, at + 1, 0x80, 0x10, 0x00), 5, index,
                    $"UTF-8 count not minimal [{index}]");
                Rejected<Utf8StringFormatter>(utf8.AsSpan(0, at + 4).ToArray(), 5, index, $"UTF-8 cut [{index}]");
            }

            // A count of 2^28 strings with 25 bytes after it.
            Rejected<StringFormatter>(With(data, 0, 0x00, 0x00, 0x00, 0x10), 5, 0, "huge count");
            Rejected<Utf8StringFormatter>(With(utf8, 0, 0x00, 0x00, 0x00, 0x10), 5, 0, "UTF-8 huge count");
        }

        /// <summary>
        /// Bytes that are not UTF-8, in the first, a middle or the last string of a run, are invalid data in every
        /// collection of UTF-8 strings: arrays, sets, and either side of a dictionary.
        /// </summary>
        [TestMethod]
        public void InvalidUtf8InARunIsRejected()
        {
            var values = Enumerable.Range(0, 5).Select(i => "abc" + i).ToArray();
            byte[] data = Run<Utf8StringFormatter>(values);
            var keyed = values.ToDictionary(v => v, v => v.Length);
            byte[] keys = Write<Dictionary<string, int>,
                DictionaryFormatter<string, int, Utf8StringFormatter, UnmanagedFormatter<int>>>(keyed);
            foreach (int index in new[] { 0, 2, 4 })
            {
                // A lone continuation byte, then an unfinished sequence.
                var bad = With(data, 4 + 6 * index + 2, 0x80, 0xE2);
                Assert.ThrowsExactly<NinoDataException>(() =>
                    Read<string[], ArrayFormatter<string, Utf8StringFormatter>>(bad), $"[{index}]");
                Assert.ThrowsExactly<NinoDataException>(() =>
                    Read<HashSet<string>, HashSetFormatter<HashSet<string>, string, Utf8StringFormatter>>(bad));
                var badKey = With(keys, 4 + 10 * index + 2, 0xFF);
                Assert.ThrowsExactly<NinoDataException>(() => Read<Dictionary<string, int>,
                    DictionaryFormatter<string, int, Utf8StringFormatter, UnmanagedFormatter<int>>>(badKey));
            }

            var labels = Enumerable.Range(0, 5).ToDictionary(i => i, i => values[i]);
            byte[] valued = Write<Dictionary<int, string>,
                DictionaryFormatter<int, string, UnmanagedFormatter<int>, Utf8StringFormatter>>(labels);
            Assert.ThrowsExactly<NinoDataException>(() => Read<Dictionary<int, string>,
                DictionaryFormatter<int, string, UnmanagedFormatter<int>, Utf8StringFormatter>>(
                With(valued, 4 + 10 * 4 + 6, 0xC0)));
        }

        /// <summary>
        /// A key repeated in a set of strings or in a dictionary with strings among its pairs (its fill and its adds,
        /// both encodings, either side) is invalid data; and a pair's length out of range at the first, a middle or
        /// the last pair is rejected, the pairs after it left as they were.
        /// </summary>
        [TestMethod]
        public void RepeatedKeysAndBadPairsAreRejected()
        {
            foreach (int count in new[] { 3, 9 })
            {
                var values = Enumerable.Range(0, count).Select(i => "s" + i).ToArray();
                values[count - 1] = values[count / 2];
                Assert.ThrowsExactly<NinoDataException>(() =>
                    NinoDeserializer.Deserialize<HashSet<string>>(NinoSerializer.Serialize(values)));
                Assert.ThrowsExactly<NinoDataException>(() =>
                    Read<HashSet<string>, HashSetFormatter<HashSet<string>, string, Utf8StringFormatter>>(
                        Run<Utf8StringFormatter>(values)));

                var pairs = values.Select((v, i) => new KeyValuePair<string, string>(v, "v" + i)).ToArray();
                Assert.ThrowsExactly<NinoDataException>(() =>
                    NinoDeserializer.Deserialize<Dictionary<string, string>>(NinoSerializer.Serialize(pairs)));
                var numbered = values.Select((v, i) => new KeyValuePair<string, int>(v, i)).ToArray();
                Assert.ThrowsExactly<NinoDataException>(() =>
                    NinoDeserializer.Deserialize<Dictionary<string, int>>(NinoSerializer.Serialize(numbered)));
                var labels = Enumerable.Range(0, count)
                    .Select(i => new KeyValuePair<int, string>(i == count - 1 ? 0 : i, "v" + i)).ToArray();
                Assert.ThrowsExactly<NinoDataException>(() =>
                    NinoDeserializer.Deserialize<Dictionary<int, string>>(NinoSerializer.Serialize(labels)));
            }

            // Pairs of a key "kN" (header 5, two chars) and an int: 7 bytes each after the count.
            var map = Enumerable.Range(0, 5).ToDictionary(i => "k" + i, i => i);
            byte[] data = NinoSerializer.Serialize(map);
            foreach (int index in new[] { 0, 2, 4 })
            {
                var bad = With(data, 4 + 7 * index, 0x7F);
                Assert.ThrowsExactly<NinoDataException>(() => NinoDeserializer.Deserialize<Dictionary<string, int>>(bad));
                Assert.ThrowsExactly<NinoDataException>(() =>
                    NinoDeserializer.Deserialize<KeyValuePair<string, int>[]>(bad));
                var existing = Enumerable.Range(0, 5).Select(i => new KeyValuePair<string, int>("old" + i, -i))
                    .ToArray();
                var before = (KeyValuePair<string, int>[])existing.Clone();
                Assert.ThrowsExactly<NinoDataException>(() => NinoDeserializer.Deserialize(bad, ref existing));
                CollectionAssert.AreEqual(before.Skip(index).ToArray(), existing.Skip(index).ToArray(),
                    $"pairs read past the rejected one, [{index}]");
            }
        }

        private static byte[] With(byte[] data, int at, params byte[] bytes)
        {
            var copy = (byte[])data.Clone();
            bytes.CopyTo(copy, at);
            return copy;
        }
    }
}
