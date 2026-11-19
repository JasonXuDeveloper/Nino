using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nino.Core;
using Nino.Core.CompilerServices;
using Nino.Core.Internal;
using Nino.UnitTests.Subset.Blocks;
using RootFormatter = NinoGen.Nino_UnitTests_DispatchRootFormatter;

namespace Nino.UnitTests
{
    /// <summary>A generic struct: a block where its value is raw or a block, else written member by member.</summary>
    [NinoType(format: NinoFormat.VersionTolerant)]
    public struct RunBox<T>
    {
        public int Id;
        public T Value;
    }

    /// <summary>An unmanaged key without padding: written as its bytes.</summary>
    public struct RunVec2
    {
        public float X, Y;
    }

    /// <summary>An unmanaged key with three bytes of padding after <see cref="A"/>: not its memory on the wire.</summary>
    public struct RunPadded
    {
        public byte A;
        public int B;
    }

    /// <summary>A struct key with a reference (not raw, no fixed size).</summary>
    [NinoType(format: NinoFormat.VersionTolerant)]
    public struct RunTextKey
    {
        public int A;
        public string B;
    }

    /// <summary>
    /// The dispatch root's generated formatter, counting the calls Nino.Core makes to its run loop: it has the loop
    /// as the generated one has it, so Nino.Core routes to <c>WriteRun</c> exactly as for the generated one.
    /// </summary>
    public struct RecordingRootFormatter : INinoFormatter<DispatchRoot>
    {
        [ThreadStatic] public static int RunCalls;

        public void Serialize(DispatchRoot value, ref Writer writer) =>
            default(RootFormatter).Serialize(value, ref writer);

        public void Deserialize(ref Reader reader, ref DispatchRoot value) =>
            default(RootFormatter).Deserialize(ref reader, ref value);

        public int BodyTraits => default(RootFormatter).BodyTraits;

        public bool HasRunLoop => true;

        public void WriteRun(NinoRun run, ref Writer writer)
        {
            RunCalls++;
            RunTests.WriteRun<RootFormatter>(run, ref writer);
        }
    }

    /// <summary>
    /// Every contiguous collection writes its elements through one cursor (<see cref="NinoRun"/>) and one loop per
    /// element kind (<see cref="NinoRuns"/>), a dictionary its entries, whose element is the pair (#355 PR 3).
    /// </summary>
    [TestClass]
    public class RunTests
    {
        /// <summary>The generated run loop of <typeparamref name="TFormatter"/>, reached as Nino.Core reaches it.</summary>
        internal static void WriteRun<TFormatter>(NinoRun run, ref Writer writer)
            where TFormatter : struct, INinoFormatter =>
            default(TFormatter).WriteRun(run, ref writer);

        private static DispatchRoot Root(int i) => (i % 5) switch
        {
            0 => new DispatchSibling { A = i, D = i * 0.5 },
            1 => new DispatchLeaf { A = i, B = "leaf" + i, C = -i },
            2 => null,
            3 => new DispatchMid { A = i, B = "mid" + i },
            _ => new DispatchHolder { A = i, Kids = new List<DispatchRoot> { null, new DispatchSibling { A = -i } } },
        };

        [TestMethod]
        public void EveryCollectionWritesItsElementsAsAnArray()
        {
            Assert.AreEqual(NinoElementKind.Raw, NinoKind.Of<int, UnmanagedFormatter<int>>());
            Assert.AreEqual(NinoElementKind.Slot, NinoKind.Of<int?, UnmanagedNullableFormatter<int>>());
            Assert.AreEqual(NinoElementKind.Block, NinoKind.Of<DateTimeOffset, DateTimeOffsetFormatter>());
            Assert.IsTrue(NinoKind<RootFormatter>.Runs);

            Each(i => i * 7 - 3);                                          // raw
            Each(i => (i, (long)i << 33));                                 // a block (a value tuple)
            Each(i => i % 3 == 0 ? null : (int?)i);                        // slots
            Each(i => new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.FromMinutes(i % 60)).AddTicks(i)); // a block
            Each(i => new GenericBlock<long> { A = i, Item = -i, B = i % 2 == 0 }); // a generic struct block
            Each(i => "s" + i);                                            // strings
            Each(i => new RunBox<string> { Id = i, Value = "b" + i });     // a struct with a reference
            Each(Root);                                                    // a polymorphic base: its run loop
            Each(i => (i, Root(i)));                                       // a composite of one
            Each(i => new RunBox<DispatchRoot> { Id = i, Value = Root(i) });
        }

        /// <summary>
        /// Each collection of <typeparamref name="T"/>, at several sizes, writes what a <c>T[]</c> of its elements in
        /// its order writes, and reads back to the same bytes; a set and a dictionary also with free entries.
        /// </summary>
        private static void Each<T>(Func<int, T> make)
        {
            foreach (int count in new[] { 0, 1, 5, 40 })
            {
                var items = Enumerable.Range(0, count).Select(make).ToArray();
                string name = $"{typeof(T).Name} x{count}";
                byte[] expected = NinoSerializer.Serialize(items);
                Same(expected, items.ToList(), name);
                Same(expected, new ArraySegment<T>(items.Prepend(make(99)).ToArray(), 1, count), name);
                Same(expected, new Memory<T>(items), name);
                Same(expected, new ReadOnlyMemory<T>(items), name);
                Same(expected, ImmutableArray.Create(items), name);

                // A ring wrapped around its end: two runs.
                var queue = new Queue<T>(Enumerable.Range(0, 3).Select(make));
                queue.Clear();
                for (int i = 0; i < 3; i++)
                    queue.Enqueue(make(i));
                for (int i = 0; i < 3; i++)
                    queue.Dequeue();
                foreach (var item in items)
                    queue.Enqueue(item);
                Same(expected, queue, name);
                Same(expected, new Stack<T>(items), name);

                // A set (distinct elements; a null at most once), whole and with a removed entry.
                var set = new HashSet<T>(items);
                Same(NinoSerializer.Serialize(set.ToArray()), set, name);
                if (set.Count > 1)
                {
                    set.Remove(set.First());
                    Same(NinoSerializer.Serialize(set.ToArray()), set, name + " removed");
                    set.Add(make(1000));
                    Same(NinoSerializer.Serialize(set.ToArray()), set, name + " refilled");
                }

                var byIndex = new Dictionary<int, T>();
                for (int i = 0; i < count; i++)
                    byIndex.Add(i * 3, items[i]);
                Pairs(byIndex, name);
                var byString = new Dictionary<string, T>();
                for (int i = 0; i < count; i++)
                    byString.Add("k" + i, items[i]);
                Pairs(byString, name);
            }
        }

        private static void Same<TCollection>(byte[] expected, TCollection value, string name)
        {
            byte[] bytes = NinoSerializer.Serialize(value);
            CollectionAssert.AreEqual(expected, bytes, $"{typeof(TCollection).Name} of {name}");
            CollectionAssert.AreEqual(bytes, NinoSerializer.Serialize(NinoDeserializer.Deserialize<TCollection>(bytes)),
                $"{typeof(TCollection).Name} of {name}, read back");
        }

        /// <summary>
        /// A dictionary writes its count, then each key and each value as they write alone, in its order: whole, with
        /// a removed entry (a free entry the run skips), and with that entry reused.
        /// </summary>
        private static void Pairs<TKey, T>(Dictionary<TKey, T> dictionary, string name)
        {
            CollectionAssert.AreEqual(Expected(dictionary), NinoSerializer.Serialize(dictionary), name);
            if (dictionary.Count < 2)
                return;
            var first = dictionary.Keys.First();
            var item = dictionary[first];
            dictionary.Remove(dictionary.Keys.Skip(1).First());
            CollectionAssert.AreEqual(Expected(dictionary), NinoSerializer.Serialize(dictionary), name + " removed");
            var back = NinoDeserializer.Deserialize<Dictionary<TKey, T>>(NinoSerializer.Serialize(dictionary));
            CollectionAssert.AreEqual(Expected(dictionary), NinoSerializer.Serialize(back), name + " read back");
            dictionary.Remove(first);
            dictionary.Add(first, item);
            CollectionAssert.AreEqual(Expected(dictionary), NinoSerializer.Serialize(dictionary), name + " refilled");
        }

        internal static byte[] Expected<TKey, T>(Dictionary<TKey, T> dictionary)
        {
            var expected = new List<byte>(BitConverter.GetBytes(dictionary.Count));
            foreach (var pair in dictionary)
            {
                expected.AddRange(NinoSerializer.Serialize(pair.Key));
                expected.AddRange(NinoSerializer.Serialize(pair.Value));
            }

            return expected.ToArray();
        }

        /// <summary>
        /// The free entries of a set and a dictionary are told apart on this runtime (a run with free entries skips
        /// them rather than going through the enumerator), and a run says so.
        /// </summary>
        [TestMethod]
        public void RunsSkipFreeEntries()
        {
            Assert.AreNotEqual(CollectionViews.FreeEntries.Unknown, CollectionViews.SetFree);
            Assert.AreNotEqual(CollectionViews.FreeEntries.Unknown, CollectionViews.MapFree);
            var dictionary = Enumerable.Range(0, 10).ToDictionary(i => i, i => "v" + i);
            Assert.IsTrue(CollectionFields.Run(dictionary, dictionary.Count, out var run));
            Assert.AreEqual(NinoRun.AllInUse, run.InUse);
            dictionary.Remove(4);
            Assert.IsTrue(CollectionFields.Run(dictionary, dictionary.Count, out run));
            Assert.AreNotEqual(NinoRun.AllInUse, run.InUse);
            Assert.AreEqual(10, run.Count);
            var set = new HashSet<int>(Enumerable.Range(0, 10));
            set.Remove(4);
            Assert.IsTrue(CollectionFields.Run(set, set.Count, out run));
            Assert.AreNotEqual(NinoRun.AllInUse, run.InUse);
        }

        /// <summary>
        /// The generated run loop writes the bytes of <see cref="NinoSerializer"/>: over an array's run, shallow and
        /// with a writer already deep (each value then through Serialize, Nino.Core's deep path), and over a
        /// dictionary's entries with their keys, whole and with a free entry.
        /// </summary>
        [TestMethod]
        public void RunLoopWritesTheSerializerBytes()
        {
            DispatchRoot[] values = Enumerable.Range(0, 12).Select(Root).ToArray();
            CollectionAssert.AreEqual(NinoSerializer.Serialize(values), Write(values, 0, false));
            CollectionAssert.AreEqual(Write(values, NinoDepth.Inline, true), Write(values, NinoDepth.Inline, false));

            var dictionary = Enumerable.Range(0, 12).ToDictionary(i => i * 5, Root);
            CollectionAssert.AreEqual(Expected(dictionary), Write(dictionary));
            dictionary.Remove(10);
            CollectionAssert.AreEqual(Expected(dictionary), Write(dictionary));
            var named = Enumerable.Range(0, 12).ToDictionary(i => "n" + i, Root);
            named.Remove("n3");
            CollectionAssert.AreEqual(Expected(named), Write(named));
        }

        private static byte[] Write(DispatchRoot[] values, int depth, bool perValue)
        {
            using var buffer = new NinoArrayBufferWriter();
            var writer = new Writer(buffer);
            writer.Depth = depth;
            writer.WriteLength(values.Length);
            if (perValue)
            {
                foreach (var value in values)
                    default(RootFormatter).Serialize(value, ref writer);
            }
            else
            {
                WriteRun<RootFormatter>(NinoRun.Of<DispatchRoot>(values), ref writer);
            }

            writer.Flush();
            return buffer.WrittenSpan.ToArray();
        }

        private static byte[] Write<TKey>(Dictionary<TKey, DispatchRoot> dictionary)
        {
            using var buffer = new NinoArrayBufferWriter();
            var writer = new Writer(buffer);
            writer.WriteLength(dictionary.Count);
            // The run of the pairs (the entries, which hold references), written by the entry's formatter: its value's
            // generated loop over the values with their keys.
            Assert.IsTrue(CollectionFields.Run(dictionary, dictionary.Count, out var run));
            if (typeof(TKey) == typeof(string))
                WriteRun<CollectionFields.MapEntryFormatter<string, DispatchRoot, StringFormatter, RootFormatter>>(run,
                    ref writer);
            else
                WriteRun<CollectionFields.MapEntryFormatter<int, DispatchRoot, UnmanagedFormatter<int>,
                    RootFormatter>>(run, ref writer);
            writer.Flush();
            return buffer.WrittenSpan.ToArray();
        }

        /// <summary>
        /// Every key, with every key formatter: a key whose memory is its wire form (raw without padding, a block that
        /// is its memory: tuples and generic structs too) or a string takes the base's run loop, any other the
        /// value-by-value loop; each writes what the keys and values write alone, whole and with an entry removed.
        /// </summary>
        [TestMethod]
        public void RunLoopServesEveryKeyFormatter()
        {
            DispatchRoot[] values = Enumerable.Range(0, 7).Select(Root).ToArray();
            Keys<int, UnmanagedFormatter<int>>(values, i => i, true);
            Keys<long, UnmanagedFormatter<long>>(values, i => (long)i << 40, true);
            Keys<short, UnmanagedFormatter<short>>(values, i => (short)i, true);
            Keys<uint, UnmanagedFormatter<uint>>(values, i => (uint)i, true);
            Keys<Guid, UnmanagedFormatter<Guid>>(values, i => new Guid(i, 0, 0, new byte[8]), true);
            Keys<RunVec2, UnmanagedFormatter<RunVec2>>(values, i => new RunVec2 { X = i, Y = -i }, true);
            Keys<string, StringFormatter>(values, i => "k" + i, true);
            Keys<(int, int), ValueTupleFormatter<int, int, UnmanagedFormatter<int>, UnmanagedFormatter<int>>>(values,
                i => (i, -i), NinoBlockPlan<(int, int),
                    ValueTupleFormatter<int, int, UnmanagedFormatter<int>, UnmanagedFormatter<int>>>.IsMemory);
            Keys<KeyValuePair<int, long>, KeyValuePairFormatter<int, long, UnmanagedFormatter<int>,
                UnmanagedFormatter<long>>>(values, i => new KeyValuePair<int, long>(i, -i),
                NinoBlockPlan<KeyValuePair<int, long>, KeyValuePairFormatter<int, long, UnmanagedFormatter<int>,
                    UnmanagedFormatter<long>>>.IsMemory);
            // Not their memory, or of no fixed size: value by value.
            Keys<RunPadded, UnmanagedFormatter<RunPadded>>(values, i => new RunPadded { A = (byte)i, B = -i }, false);
            Keys<string, Utf8StringFormatter>(values, i => "ü" + i, false);
            Keys<int?, UnmanagedNullableFormatter<int>>(values, i => i, false);
            Keys<(int, string), ValueTupleFormatter<int, string, UnmanagedFormatter<int>, StringFormatter>>(values,
                i => (i, "t" + i), false);
            Keys<RunTextKey, NinoGen.Nino_UnitTests_RunTextKeyFormatter>(values,
                i => new RunTextKey { A = i, B = "s" + i }, false);
            Keys<DispatchSibling, NinoGen.Nino_UnitTests_DispatchSiblingFormatter>(values,
                i => new DispatchSibling { A = i }, false);

            // Generic structs as keys (a block, and one of no fixed size), and tuples, pairs and generic structs holding
            // the base as values: the bytes of each key and value alone.
            var byBlock = new Dictionary<GenericBlock<int>, DispatchRoot>();
            var byBox = new Dictionary<RunBox<string>, DispatchRoot>();
            var tuples = new Dictionary<(int, short), (int, DispatchRoot)>();
            var pairs = new Dictionary<int, KeyValuePair<long, DispatchRoot>>();
            var boxes = new Dictionary<string, RunBox<DispatchRoot>>();
            for (int i = 0; i < values.Length; i++)
            {
                byBlock.Add(new GenericBlock<int> { A = i, Item = -i, B = i % 2 == 0 }, values[i]);
                byBox.Add(new RunBox<string> { Id = i, Value = "x" + i }, values[i]);
                tuples.Add((i, (short)-i), (i, values[i]));
                pairs.Add(i, new KeyValuePair<long, DispatchRoot>(i, values[i]));
                boxes.Add("b" + i, new RunBox<DispatchRoot> { Id = i, Value = values[i] });
            }

            Pairs(byBlock, "GenericBlock keys");
            Pairs(byBox, "RunBox keys");
            Pairs(tuples, "tuples");
            Pairs(pairs, "pairs");
            Pairs(boxes, "RunBox values");
        }

        private static void Keys<TKey, TKeyFormatter>(DispatchRoot[] values, Func<int, TKey> key, bool loop)
            where TKeyFormatter : struct, INinoFormatter<TKey>
        {
            Assert.AreEqual(loop, NinoRuns.KeyOf<TKey, TKeyFormatter>() != NinoRun.NoKey, typeof(TKey).Name);
            var dictionary = new Dictionary<TKey, DispatchRoot>();
            for (int i = 0; i < values.Length; i++)
                dictionary.Add(key(i), values[i]);
            for (int round = 0; round < 2; round++)
            {
                var expected = new NinoArrayBufferWriter();
                var writer = new Writer(expected);
                writer.WriteLength(dictionary.Count);
                foreach (var pair in dictionary)
                {
                    default(TKeyFormatter).Serialize(pair.Key, ref writer);
                    default(RootFormatter).Serialize(pair.Value, ref writer);
                }

                writer.Flush();
                var actual = new NinoArrayBufferWriter();
                writer = new Writer(actual);
                int calls = RecordingRootFormatter.RunCalls;
                NinoDictionary<TKey, DispatchRoot, TKeyFormatter, RecordingRootFormatter>.Write(dictionary,
                    ref writer);
                writer.Flush();
                CollectionAssert.AreEqual(expected.WrittenSpan.ToArray(), actual.WrittenSpan.ToArray(),
                    typeof(TKey).Name);
                Assert.AreEqual(calls + (loop ? 1 : 0), RecordingRootFormatter.RunCalls, typeof(TKey).Name);

                dictionary.Add(key(values.Length + round), values[0]);
                dictionary.Remove(key(round));
            }
        }

        /// <summary>
        /// Pairs are elements (#355): a list's and an array's pairs take the paths a dictionary's take. A pair of a
        /// polymorphic value composes onto the value's run loop (one call per collection, keys written by it), pairs of
        /// strings take the cursor loop, pairs of raw values are a block, in a row and in a dictionary's entries alike;
        /// each writes the bytes of its pairs written one by one, and reads them back.
        /// </summary>
        [TestMethod]
        public void PairsInRowsTakeTheDictionaryPaths()
        {
            DispatchRoot[] values = Enumerable.Range(0, 9).Select(Root).ToArray();
            var pairs = values.Select((v, i) => new KeyValuePair<int, DispatchRoot>(i * 3, v)).ToList();
            var named = values.Select((v, i) => new KeyValuePair<string, DispatchRoot>("p" + i, v)).ToArray();
            Assert.IsTrue(NinoKind<KeyValuePairFormatter<int, DispatchRoot, UnmanagedFormatter<int>,
                RecordingRootFormatter>>.Runs);
            Assert.IsTrue(NinoKind<KeyValuePairFormatter<string, DispatchRoot, StringFormatter,
                RecordingRootFormatter>>.Runs);
            // A key the loop cannot write (UTF-8): value by value.
            Assert.IsFalse(NinoKind<KeyValuePairFormatter<string, DispatchRoot, Utf8StringFormatter,
                RecordingRootFormatter>>.Runs);

            int calls = RecordingRootFormatter.RunCalls;
            CollectionAssert.AreEqual(ExpectedPairs(pairs), Write<List<KeyValuePair<int, DispatchRoot>>,
                ListFormatter<List<KeyValuePair<int, DispatchRoot>>, KeyValuePair<int, DispatchRoot>,
                    KeyValuePairFormatter<int, DispatchRoot, UnmanagedFormatter<int>, RecordingRootFormatter>>>(pairs));
            CollectionAssert.AreEqual(ExpectedPairs(named), Write<KeyValuePair<string, DispatchRoot>[],
                ArrayFormatter<KeyValuePair<string, DispatchRoot>,
                    KeyValuePairFormatter<string, DispatchRoot, StringFormatter, RecordingRootFormatter>>>(named));
            var dictionary = pairs.ToDictionary(p => p.Key, p => p.Value);
            dictionary.Remove(6);
            CollectionAssert.AreEqual(ExpectedPairs(dictionary), Write<Dictionary<int, DispatchRoot>,
                DictionaryFormatter<int, DispatchRoot, UnmanagedFormatter<int>, RecordingRootFormatter>>(dictionary));
            Assert.AreEqual(calls + 3, RecordingRootFormatter.RunCalls);
            CollectionAssert.AreEqual(ExpectedPairs(pairs), NinoSerializer.Serialize(
                NinoDeserializer.Deserialize<List<KeyValuePair<int, DispatchRoot>>>(NinoSerializer.Serialize(pairs))));

            // Strings: the cursor loop, in a row and in a dictionary's entries (each the entry, composed).
            Assert.IsTrue(NinoCursor.Own<KeyValuePair<string, string>,
                KeyValuePairFormatter<string, string, StringFormatter, StringFormatter>>());
            Assert.IsTrue(NinoCursor.Own<CollectionFields.MapEntry<string, string>,
                CollectionFields.MapEntryFormatter<string, string, StringFormatter, StringFormatter>>());
            var texts = Enumerable.Range(0, 40).Select(i => new KeyValuePair<string, string>("k" + i, "v" + i)).ToArray();
            CollectionAssert.AreEqual(NinoSerializer.Serialize(texts.ToDictionary(p => p.Key, p => p.Value)),
                NinoSerializer.Serialize(texts));

            // Raw pairs: a block, the same element in a list and in a dictionary's entries where this runtime lays
            // the entry out as a set's entry of the pair.
            Assert.AreEqual(NinoElementKind.Block, NinoKind.Of<KeyValuePair<int, int>,
                KeyValuePairFormatter<int, int, UnmanagedFormatter<int>, UnmanagedFormatter<int>>>());
            Assert.IsTrue(CollectionFields.PairLayout.Entries);
            Assert.IsTrue(CollectionFields.PairLayout<int, int>.InEntries);
            var ints = Enumerable.Range(0, 100).Select(i => new KeyValuePair<int, int>(i * 7, -i)).ToList();
            var map = ints.ToDictionary(p => p.Key, p => p.Value);
            CollectionAssert.AreEqual(NinoSerializer.Serialize(ints), NinoSerializer.Serialize(map));
            CollectionAssert.AreEqual(ints, NinoDeserializer.Deserialize<Dictionary<int, int>>(
                NinoSerializer.Serialize(ints)).ToList());
            // Pairs with references: the entry is the element (CoreCLR moves references first in it).
            Assert.IsFalse(CollectionFields.PairLayout<string, int>.InEntries);
        }

        private static byte[] Write<T, TFormatter>(T value) where TFormatter : struct, INinoFormatter<T>
        {
            using var buffer = new NinoArrayBufferWriter();
            var writer = new Writer(buffer);
            default(TFormatter).Serialize(value, ref writer);
            writer.Flush();
            return buffer.WrittenSpan.ToArray();
        }

        private static byte[] ExpectedPairs<TKey>(ICollection<KeyValuePair<TKey, DispatchRoot>> pairs)
        {
            using var buffer = new NinoArrayBufferWriter();
            var writer = new Writer(buffer);
            writer.WriteLength(pairs.Count);
            foreach (var pair in pairs)
            {
                if (typeof(TKey) == typeof(string))
                    default(StringFormatter).Serialize((string)(object)pair.Key, ref writer);
                else
                    default(UnmanagedFormatter<int>).Serialize((int)(object)pair.Key, ref writer);
                default(RootFormatter).Serialize(pair.Value, ref writer);
            }

            writer.Flush();
            return buffer.WrittenSpan.ToArray();
        }

        /// <summary>
        /// Hostile input on every read of a dictionary's pairs: the block fill (raw pairs), the cursor fill and adds
        /// (strings), the per-pair fill and adds, into new and existing dictionaries: every cut, a repeated key and a
        /// null key are <see cref="NinoDataException"/>, and a rejected read leaves a dictionary it filled empty (one
        /// the dictionary adds to itself holds at most the pairs before the rejected one).
        /// </summary>
        [TestMethod]
        public void HostilePairsAreRejected()
        {
            Hostile(Enumerable.Range(0, 20).ToDictionary(i => i, i => -i), 3, k => k);
            Hostile(Enumerable.Range(0, 20).ToDictionary(i => "k" + i, i => i), 3, k => "k" + k);
            Hostile(Enumerable.Range(0, 20).ToDictionary(i => i, i => "v" + i), 3, k => k);
            Hostile(Enumerable.Range(0, 20).ToDictionary(i => "k" + i, Root), 3, k => "k" + k);
            Hostile(Enumerable.Range(0, 20).ToDictionary(i => i, i => (int?)i), 3, k => k);
            // Two pairs: below HashTables.FillFrom, added one at a time.
            Hostile(Enumerable.Range(0, 2).ToDictionary(i => "k" + i, i => "v" + i), 1, k => "k" + k);
        }

        private static void Hostile<TKey, TValue>(Dictionary<TKey, TValue> dictionary, int at, Func<int, TKey> key)
        {
            byte[] bytes = NinoSerializer.Serialize(dictionary);
            Truncated(bytes, d => NinoDeserializer.Deserialize<Dictionary<TKey, TValue>>(d));
            for (int length = 0; length < bytes.Length; length++)
            {
                var existing = new Dictionary<TKey, TValue>(dictionary);
                var prefix = bytes.AsSpan(0, length).ToArray();
                Assert.ThrowsExactly<NinoDataException>(() => NinoDeserializer.Deserialize(prefix, ref existing),
                    $"{typeof(TKey).Name}/{typeof(TValue).Name} into an existing one, {length} bytes");
            }

            // The pair at `at` given the key of the first: a repeated key.
            var repeated = dictionary.ToList();
            repeated[at] = new KeyValuePair<TKey, TValue>(key(0), repeated[at].Value);
            byte[] twice = NinoSerializer.Serialize(repeated);
            Assert.ThrowsExactly<NinoDataException>(() => NinoDeserializer.Deserialize<Dictionary<TKey, TValue>>(twice));
            var into = new Dictionary<TKey, TValue>();
            Assert.ThrowsExactly<NinoDataException>(() => NinoDeserializer.Deserialize(twice, ref into));
            // Filled then linked here: left empty; added by the dictionary itself (string keys on .NET, which guards
            // them): the pairs before the rejected one, never it.
            if (HashTables.Plan<TKey, TValue>.Inserts && dictionary.Count >= HashTables.FillFrom)
                Assert.AreEqual(0, into.Count, typeof(TKey).Name + "/" + typeof(TValue).Name);
            else
                Assert.IsTrue(into.Count <= at, typeof(TKey).Name + "/" + typeof(TValue).Name);

            // A null key, where a key can be null.
            if (default(TKey) == null)
            {
                repeated[at] = new KeyValuePair<TKey, TValue>(default, repeated[at].Value);
                byte[] nulls = NinoSerializer.Serialize(repeated);
                Assert.ThrowsExactly<NinoDataException>(() =>
                    NinoDeserializer.Deserialize<Dictionary<TKey, TValue>>(nulls));
            }
        }

        /// <summary>
        /// Every prefix of what a run wrote is rejected on read (the reads are as before: hostile input still throws
        /// <see cref="NinoDataException"/> for each collection the runs write).
        /// </summary>
        [TestMethod]
        public void TruncatedRunsAreRejected()
        {
            var values = Enumerable.Range(0, 9).Select(Root).ToArray();
            var map = Enumerable.Range(0, 9).ToDictionary(i => "k" + i, Root);
            map.Remove("k2");
            var set = new HashSet<int>(Enumerable.Range(0, 20));
            set.Remove(5);
            Truncated(NinoSerializer.Serialize(values), d => NinoDeserializer.Deserialize<DispatchRoot[]>(d));
            Truncated(NinoSerializer.Serialize(map), d => NinoDeserializer.Deserialize<Dictionary<string, DispatchRoot>>(d));
            Truncated(NinoSerializer.Serialize(set), d => NinoDeserializer.Deserialize<HashSet<int>>(d));
        }

        private static void Truncated<T>(byte[] bytes, Func<byte[], T> read)
        {
            for (int length = 0; length < bytes.Length; length++)
            {
                var prefix = bytes.AsSpan(0, length).ToArray();
                Assert.ThrowsExactly<NinoDataException>(() => read(prefix), $"{typeof(T).Name}, {length} bytes");
            }
        }

        /// <summary>
        /// Composites carry the dispatch bit of their items (<see cref="NinoBodyTraits.Dispatches"/>): a pair, a tuple
        /// and a generic struct holding a polymorphic base, and not one holding none.
        /// </summary>
        [TestMethod]
        public void CompositesCarryTheirItemsDispatch()
        {
            Assert.AreNotEqual(0, NinoBlocks.Dispatches<RootFormatter>());
            Assert.AreNotEqual(0, NinoBlocks.Dispatches<KeyValuePairFormatter<int, DispatchRoot,
                UnmanagedFormatter<int>, RootFormatter>>());
            Assert.AreNotEqual(0, NinoBlocks.Dispatches<ValueTupleFormatter<int, DispatchRoot,
                UnmanagedFormatter<int>, RootFormatter>>());
            Assert.AreNotEqual(0, NinoBlocks.Dispatches<TupleFormatter<DispatchRoot, int, RootFormatter,
                UnmanagedFormatter<int>>>());
            Assert.AreEqual(0, NinoBlocks.Dispatches<KeyValuePairFormatter<int, string, UnmanagedFormatter<int>,
                StringFormatter>>());
            Assert.AreEqual(0, NinoBlocks.Dispatches<ValueTupleFormatter<int, long, UnmanagedFormatter<int>,
                UnmanagedFormatter<long>>>());
            Assert.AreNotEqual(0, Traits(typeof(RunBox<DispatchRoot>)) & NinoBodyTraits.Dispatches);
            Assert.AreEqual(0, Traits(typeof(RunBox<string>)) & NinoBodyTraits.Dispatches);
        }

        // The BodyTraits of RunBox<T>'s generated formatter, closed over T and T's formatter.
        private static int Traits(Type type)
        {
            var item = type.GetGenericArguments()[0];
            var itemFormatter = item == typeof(string) ? typeof(StringFormatter) : typeof(RootFormatter);
            var open = typeof(RunTests).Assembly.GetTypes().Single(t =>
                t.Namespace == "NinoGen" && t.Name.StartsWith("Nino_UnitTests_RunBox_1Formatter", StringComparison.Ordinal));
            var formatter = (INinoFormatter)Activator.CreateInstance(open.MakeGenericType(item, itemFormatter))!;
            return formatter.BodyTraits;
        }
    }
}
