using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nino.Core;
using Nino.Core.Internal;
using Nino.UnitTests.Subset.Blocks;

#nullable disable
namespace Nino.UnitTests
{
    /// <summary>
    /// Collections that are not one contiguous run (trees, linked lists, enumerators) gathered a chunk at a time
    /// through the run loops (<see cref="NinoGather"/>, #355 step 6): at counts on each side of a chunk's length, every
    /// element kind writes what an array of the elements in the collection's order writes and reads back to the same
    /// bytes; SortedList and PriorityQueue read straight into their storage for any pair (#350), and a read rejected
    /// half way leaves the collection empty.
    /// </summary>
    [TestClass]
    public class GatherTests
    {
        private static DispatchRoot Root(int i) => (i % 5) switch
        {
            0 => new DispatchSibling { A = i, D = i * 0.5 },
            1 => new DispatchLeaf { A = i, B = "leaf" + i, C = -i },
            2 => null,
            3 => new DispatchMid { A = i, B = "mid" + i },
            _ => new DispatchHolder { A = i, Kids = new List<DispatchRoot> { null, new DispatchSibling { A = -i } } },
        };

        [TestMethod]
        public void SequencesWriteTheirElementsAsAnArrayAcrossChunks()
        {
            Each(i => i * 7 - 3, sorted: true);                                       // raw
            Each(i => new PaddedItem { Id = i, Count = (short)i, Bound = i % 2 == 0 }, sorted: false); // raw, padded
            Each(i => (i, (long)i << 33), sorted: true);                              // a block
            Each(i => i % 3 == 0 ? null : (int?)i, sorted: true);                     // slots
            Each(i => new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.FromMinutes(i % 60)).AddTicks(i), sorted: true);
            Each(i => new GenericBlock<long> { A = i, Item = -i, B = i % 2 == 0 }, sorted: false);
            Each(i => i % 7 == 0 ? null : "s" + i, sorted: true);                     // strings
            Each(Root, sorted: false);                                                // a polymorphic base
        }

        [TestMethod]
        public void DictionariesWriteTheirPairsAcrossChunks()
        {
            Pairs(i => i * 3L);
            Pairs(i => "v" + i);
            Pairs(i => i % 3 == 0 ? null : (int?)i);
            Pairs(Root);
        }

        private static int[] Counts<T>()
        {
            int length = NinoGather<T>.Length;
            return new[] { 0, 1, 5, length - 1, length, length + 1, 2 * length + 3 };
        }

        private static IEnumerable<T> Yield<T>(IEnumerable<T> items)
        {
            foreach (var item in items)
                yield return item;
        }

        private static void Each<T>(Func<int, T> make, bool sorted)
        {
            foreach (int count in Counts<T>())
            {
                var items = Enumerable.Range(0, count).Select(make).ToArray();
                string name = $"{typeof(T).Name} x{count}";
                byte[] expected = NinoSerializer.Serialize(items);
                Same(expected, new LinkedList<T>(items), name);
                Same(expected, new ConcurrentQueue<T>(items), name);
                Same(expected, new ReadOnlyCollection<T>(items), name);
                Same(expected, (ICollection<T>)new ObservableCollection<T>(items), name);
                CollectionAssert.AreEqual(expected, NinoSerializer.Serialize(Yield(items)), name + " enumerated");
                Same(expected, new ConcurrentStack<T>(items), name);
                if (sorted)
                {
                    var set = new SortedSet<T>(items);
                    Same(NinoSerializer.Serialize(set.ToArray()), set, name);
                }

                Immutables(items, sorted, name);

#if NET8_0_OR_GREATER && !NINO_CORE_NETSTANDARD
                var queue = new PriorityQueue<T, int>(items.Select((item, i) => (item, i % 17)));
                Same(NinoSerializer.Serialize(queue.UnorderedItems.ToArray()), queue, name);
                if (sorted)
                {
                    var byItem = new PriorityQueue<int, T>(items.Select((item, i) => (i, item)));
                    Same(NinoSerializer.Serialize(byItem.UnorderedItems.ToArray()), byItem, name + " as priorities");
                }
#endif
            }
        }

        /// <summary>
        /// The immutable collections of <paramref name="items"/> walk their trees and nodes through the gather
        /// (<see cref="Nino.Core.CompilerServices.NinoTreeWalk"/>): each writes what an array of its elements in its
        /// enumerator's order writes (a stack's reversed, bottom to top).
        /// </summary>
        private static void Immutables<T>(T[] items, bool sorted, string name)
        {
            // A set of objects hashed by reference reads back in another order.
            bool hashedAlike = typeof(T).IsValueType || typeof(T) == typeof(string);
            Same(NinoSerializer.Serialize(items), ImmutableList.CreateRange(items), name);
            Same(NinoSerializer.Serialize(items), ImmutableQueue.CreateRange(items), name);
            var stack = ImmutableStack.CreateRange(items);
            Same(NinoSerializer.Serialize(stack.Reverse().ToArray()), stack, name);
            var hashed = ImmutableHashSet.CreateRange(items.Where(item => item != null));
            Same(NinoSerializer.Serialize(hashed.ToArray()), hashed, name, reads: hashedAlike);
            var keyed = ImmutableDictionary.CreateRange(items.Where(item => item != null).Distinct()
                .Select((item, i) => new KeyValuePair<T, int>(item, i)));
            Same(NinoSerializer.Serialize(keyed.ToArray()), keyed, name, reads: hashedAlike);
            var byIndex = ImmutableDictionary.CreateRange(items.Select((item, i) => new KeyValuePair<int, T>(i * 3, item)));
            Same(NinoSerializer.Serialize(byIndex.ToArray()), byIndex, name);
            if (!sorted)
                return;
            var ordered = ImmutableSortedSet.CreateRange(items);
            Same(NinoSerializer.Serialize(ordered.ToArray()), ordered, name);
            var sortedPairs = ImmutableSortedDictionary.CreateRange(items.Distinct().Where(item => item != null)
                .Select((item, i) => new KeyValuePair<T, string>(item, "v" + i)));
            Same(NinoSerializer.Serialize(sortedPairs.ToArray()), sortedPairs, name);
        }

        [TestMethod]
        public void ImmutableHashBucketsOfEqualHashesAreWalkedInTheirOrder()
        {
            // A long's hash is its halves XORed: every i | (i << 32) hashes to 0, one bucket of them all.
            foreach (int count in new[] { 3, 40, 2 * NinoGather<long>.Length + 3 })
            {
                var colliding = Enumerable.Range(0, count).Select(i => (long)i | ((long)i << 32)).Concat(Enumerable.Range(1, 50).Select(i => (long)i * 7919));
                var set = ImmutableHashSet.CreateRange(colliding);
                var dictionary = ImmutableDictionary.CreateRange(set.Select(item => new KeyValuePair<long, string>(item, "v" + item)));
                // Not read back for the bytes: a bucket keeps its items in the order they were added, which a read
                // through the builder does not repeat.
                Same(NinoSerializer.Serialize(set.ToArray()), set, $"{count} colliding", reads: false);
                Same(NinoSerializer.Serialize(dictionary.ToArray()), dictionary, $"{count} colliding", reads: false);
            }

            var strings = new ConcurrentDictionary<string, DispatchRoot>(Enumerable.Range(0, 3000)
                .Select(i => new KeyValuePair<string, DispatchRoot>("k" + i, Root(i))));
            Same(NinoSerializer.Serialize(strings.ToArray()), strings, "string keys", reads: false);
        }

        /// <summary>A grouping that only enumerates: its count patched after its elements, which follow its key.</summary>
        private sealed class Enumerated<TKey, T> : IGrouping<TKey, T>
        {
            private readonly T[] _items;

            public Enumerated(TKey key, T[] items)
            {
                Key = key;
                _items = items;
            }

            public TKey Key { get; }
            public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)_items).GetEnumerator();
            System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
        }

        [TestMethod]
        public void GroupingsGatherTheirElementsAfterTheirKey()
        {
            foreach (int count in Counts<string>())
            {
                var items = Enumerable.Range(0, count).Select(i => i % 7 == 0 ? null : "g" + i).ToArray();
                var listed = items.GroupBy(_ => 5).SingleOrDefault() ?? new Enumerated<int, string>(5, items);
                byte[] expected = NinoSerializer.Serialize(listed);
                CollectionAssert.AreEqual(expected,
                    NinoSerializer.Serialize<IGrouping<int, string>>(new Enumerated<int, string>(5, items)), $"x{count}");
                var ints = Enumerable.Range(0, count).ToArray();
                CollectionAssert.AreEqual(
                    NinoSerializer.Serialize<IGrouping<int, int>>(new Enumerated<int, int>(5, ints)),
                    NinoSerializer.Serialize(ints.GroupBy(_ => 5).SingleOrDefault() ?? new Enumerated<int, int>(5, ints)),
                    $"ints x{count}");
            }
        }

        private static void Pairs<T>(Func<int, T> make)
        {
            foreach (int count in Counts<KeyValuePair<int, T>>())
            {
                var pairs = Enumerable.Range(0, count).Select(i => new KeyValuePair<int, T>(i * 5 - 7, make(i))).ToArray();
                string name = $"{typeof(T).Name} x{count}";
                byte[] expected = NinoSerializer.Serialize(pairs);
                Same(expected, new SortedDictionary<int, T>(pairs.ToDictionary(p => p.Key, p => p.Value)), name);
                Same(expected, new SortedList<int, T>(pairs.ToDictionary(p => p.Key, p => p.Value)), name);
                // Read back into a table of another size, whose enumeration order may differ: the same pairs.
                var concurrent = new ConcurrentDictionary<int, T>(pairs);
                byte[] bytes = NinoSerializer.Serialize(concurrent);
                Same(NinoSerializer.Serialize(concurrent.ToArray()), concurrent, name, reads: false);
                var back = NinoDeserializer.Deserialize<ConcurrentDictionary<int, T>>(bytes);
                CollectionAssert.AreEqual(NinoSerializer.Serialize(pairs),
                    NinoSerializer.Serialize(back.OrderBy(p => p.Key).ToArray()), name + " read back");

                var byString = pairs.Select(p => new KeyValuePair<string, T>("k" + p.Key, p.Value))
                    .OrderBy(p => p.Key, StringComparer.Ordinal).ToArray();
                Same(NinoSerializer.Serialize(byString),
                    new SortedList<string, T>(byString.ToDictionary(p => p.Key, p => p.Value), StringComparer.Ordinal),
                    name + " by string", reads: false);
            }
        }

        /// <summary>
        /// <paramref name="value"/> writes <paramref name="expected"/>, and what is read back writes it too (unless
        /// <paramref name="reads"/> is false: a list whose comparer the read does not have, a table whose order the
        /// read does not keep).
        /// </summary>
        private static void Same<TCollection>(byte[] expected, TCollection value, string name, bool reads = true)
        {
            byte[] bytes = NinoSerializer.Serialize(value);
            CollectionAssert.AreEqual(expected, bytes, $"{typeof(TCollection).Name} of {name}");
            if (!reads)
                return;
            var back = NinoDeserializer.Deserialize<TCollection>(bytes);
            CollectionAssert.AreEqual(bytes, NinoSerializer.Serialize(back), $"{typeof(TCollection).Name} of {name}, read back");
            // Into the instance read: cleared and refilled.
            NinoDeserializer.Deserialize(bytes, ref back);
            CollectionAssert.AreEqual(bytes, NinoSerializer.Serialize(back), $"{typeof(TCollection).Name} of {name}, refilled");
        }

        [TestMethod]
        public void NestedGathersOfTheSameKindTakeChunksOfTheirOwn()
        {
            var trees = new SortedDictionary<int, SortedSet<int>>();
            for (int i = 0; i < 50; i++)
                trees[i] = new SortedSet<int>(Enumerable.Range(i, i * 37));
            var back = RoundTrip(trees);
            Assert.AreEqual(50, back.Count);
            foreach (var pair in trees)
                CollectionAssert.AreEqual(pair.Value.ToArray(), back[pair.Key].ToArray());

            var lists = new LinkedList<LinkedList<string>>();
            for (int i = 0; i < 30; i++)
                lists.AddLast(new LinkedList<string>(Enumerable.Range(0, i * 200).Select(j => "x" + j)));
            var read = RoundTrip(lists);
            CollectionAssert.AreEqual(lists.Select(l => string.Join(",", l)).ToArray(),
                read.Select(l => string.Join(",", l)).ToArray());
        }

        private static T RoundTrip<T>(T value) => NinoDeserializer.Deserialize<T>(NinoSerializer.Serialize(value));

        [TestMethod]
        public void SortedListsReadAnyPairIntoTheirArrays()
        {
            var longs = new SortedList<long, double>(Enumerable.Range(0, 5000).ToDictionary(i => (long)i << 20, i => i * 0.25));
            CollectionAssert.AreEqual(longs.ToArray(), RoundTrip(longs).ToArray());
            var strings = new SortedList<int, string>(Enumerable.Range(0, 3000).ToDictionary(i => i, i => "v" + i));
            CollectionAssert.AreEqual(strings.ToArray(), RoundTrip(strings).ToArray());
            var mixed = new SortedList<short, PaddedItem>(Enumerable.Range(0, 3000)
                .ToDictionary(i => (short)i, i => new PaddedItem { Id = i, Count = (short)-i, Bound = i % 3 == 0 }));
            CollectionAssert.AreEqual(mixed.ToArray(), RoundTrip(mixed).ToArray());

            // Out of order (another writer): sorted as they are added.
            var shuffled = Enumerable.Range(0, 3000).Select(i => new KeyValuePair<int, string>((i * 7919) % 3001, "s" + i))
                .ToArray();
            var sorted = NinoDeserializer.Deserialize<SortedList<int, string>>(NinoSerializer.Serialize(shuffled));
            CollectionAssert.AreEqual(shuffled.OrderBy(p => p.Key).ToArray(), sorted.ToArray());

            // A key twice is invalid data.
            var twice = new[] { new KeyValuePair<long, int>(1, 1), new KeyValuePair<long, int>(2, 2), new KeyValuePair<long, int>(1, 3) };
            Assert.ThrowsExactly<NinoDataException>(() =>
                NinoDeserializer.Deserialize<SortedList<long, int>>(NinoSerializer.Serialize(twice)));
            var nulls = new[] { new KeyValuePair<string, int>("a", 1), new KeyValuePair<string, int>(null, 2) };
            Assert.ThrowsExactly<NinoDataException>(() =>
                NinoDeserializer.Deserialize<SortedList<string, int>>(NinoSerializer.Serialize(nulls)));
        }

#if NET8_0_OR_GREATER && !NINO_CORE_NETSTANDARD
        [TestMethod]
        public void PriorityQueuesReadAnyPairIntoTheirHeap()
        {
            var random = new Random(355);
            var floats = new PriorityQueue<long, float>(Enumerable.Range(0, 5000).Select(i => ((long)i, (float)random.NextDouble())));
            Assert.AreEqual(Dequeued(floats), Dequeued(RoundTrip(floats)));
            var strings = new PriorityQueue<string, long>(Enumerable.Range(0, 3000).Select(i => ("e" + i, (long)random.Next(100))));
            CollectionAssert.AreEqual(strings.UnorderedItems.ToArray(), RoundTrip(strings).UnorderedItems.ToArray());

            // Published as a heap: used through the public API afterwards.
            var built = NinoDeserializer.Deserialize<PriorityQueue<string, long>>(NinoSerializer.Serialize(
                new PriorityQueue<string, long>(Enumerable.Range(0, 3000).Select(i => ("e" + i, (long)(i * 7919 % 3001))))));
            built.Enqueue("first", -1);
            Assert.AreEqual("first", built.Peek());
            Assert.AreEqual(3001, built.Count);
            long previous = long.MinValue;
            while (built.TryDequeue(out _, out long priority))
            {
                Assert.IsTrue(priority >= previous, $"{priority} after {previous}");
                previous = priority;
            }

            // Not a heap (another writer): enqueued in turn, each still dequeued by its priority.
            var descending = Enumerable.Range(0, 3000).Select(i => ("d" + i, (long)(3000 - i))).ToArray();
            var queue = NinoDeserializer.Deserialize<PriorityQueue<string, long>>(NinoSerializer.Serialize(descending));
            Assert.AreEqual(3000, queue.Count);
            long last = long.MinValue;
            while (queue.TryDequeue(out _, out long priority))
            {
                Assert.IsTrue(priority >= last);
                last = priority;
            }
        }

        private static string Dequeued<TElement, TPriority>(PriorityQueue<TElement, TPriority> queue)
        {
            var copy = new PriorityQueue<TElement, TPriority>(queue.UnorderedItems);
            var order = new List<TPriority>();
            while (copy.TryDequeue(out _, out var priority))
                order.Add(priority);
            return string.Join(",", order);
        }
#endif

        [TestMethod]
        public void ARejectedReadLeavesTheCollectionEmpty()
        {
            Rejected(new LinkedList<int>(Enumerable.Range(0, 9000)), new LinkedList<int>(new[] { 1, 2 }), l => l.Count);
            Rejected(new LinkedList<string>(Enumerable.Range(0, 5000).Select(i => "s" + i)),
                new LinkedList<string>(new[] { "a" }), l => l.Count);
            Rejected(new SortedList<int, long>(Enumerable.Range(0, 5000).ToDictionary(i => i, i => (long)i)),
                new SortedList<int, long> { [1] = 1 }, l => l.Count);
            Rejected(new SortedList<int, string>(Enumerable.Range(0, 5000).ToDictionary(i => i, i => "v" + i)),
                new SortedList<int, string> { [1] = "a" }, l => l.Count);
            Rejected(new ConcurrentDictionary<int, string>(Enumerable.Range(0, 5000)
                .Select(i => new KeyValuePair<int, string>(i, "v" + i))), null, d => d?.Count ?? 0, expectEmpty: false);
            // Read into its heap (.NET 10), published only once whole.
#if NET10_0_OR_GREATER && !NINO_CORE_NETSTANDARD
            Rejected(new PriorityQueue<string, long>(Enumerable.Range(0, 5000).Select(i => ("e" + i, (long)i))),
                new PriorityQueue<string, long>(new[] { ("a", 1L) }), q => q.Count);
            Rejected(new PriorityQueue<long, (int, long)>(Enumerable.Range(0, 5000).Select(i => ((long)i, (i, (long)i)))),
                new PriorityQueue<long, (int, long)>(new[] { (1L, (1, 1L)) }), q => q.Count);
#endif
        }

        /// <summary>
        /// Every proper prefix of <paramref name="value"/>'s bytes is invalid data, and read into
        /// <paramref name="target"/> leaves it as it was (a count the data cannot hold is rejected first) or empty,
        /// never half filled.
        /// </summary>
        private static void Rejected<T>(T value, T target, Func<T, int> count, bool expectEmpty = true)
        {
            byte[] bytes = NinoSerializer.Serialize(value);
            int before = target == null ? 0 : count(target);
            foreach (int cut in new[] { 4, 5, bytes.Length / 3, bytes.Length / 2, bytes.Length - 1 })
            {
                var into = target;
                Assert.ThrowsExactly<NinoDataException>(() => NinoDeserializer.Deserialize(bytes.AsSpan(0, cut), ref into),
                    $"{typeof(T).Name} cut at {cut}");
                if (expectEmpty)
                    Assert.IsTrue(count(into) == 0 || count(into) == before,
                        $"{typeof(T).Name} cut at {cut}: {count(into)}");
                Assert.ThrowsExactly<NinoDataException>(() => NinoDeserializer.Deserialize<T>(bytes.AsSpan(0, cut)));
            }
        }
    }
}
