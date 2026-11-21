using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nino.Core;
using Nino.UnitTests.GenericPolymorphism;

namespace Nino.UnitTests
{
    // Bases dispatch the subtypes of their assembly through generated code: the hierarchy's root over all of them, a
    // class below it over its own where they are at most half of the root's (DispatchMid, one of four), else through
    // the root's (ForwardMid, three of five).

    [NinoType(format: NinoFormat.VersionTolerant)]
    public abstract class DispatchRoot
    {
        public int A;
    }

    [NinoType(format: NinoFormat.VersionTolerant)]
    public class DispatchMid : DispatchRoot
    {
        public string B;
    }

    [NinoType(format: NinoFormat.VersionTolerant)]
    public sealed class DispatchLeaf : DispatchMid
    {
        public long C;
    }

    [NinoType(format: NinoFormat.VersionTolerant)]
    public sealed class DispatchSibling : DispatchRoot
    {
        public double D;
    }

    /// <summary>A subtype holding a list of the base: nested bodies, deep enough to take the deferred path.</summary>
    [NinoType(format: NinoFormat.VersionTolerant)]
    public sealed class DispatchHolder : DispatchRoot
    {
        public List<DispatchRoot> Kids;
    }

    [NinoType]
    public abstract class ForwardRoot
    {
        public int A;
    }

    [NinoType]
    public class ForwardMid : ForwardRoot
    {
        public int B;
    }

    [NinoType]
    public sealed class ForwardA : ForwardMid
    {
        public int C;
    }

    [NinoType]
    public sealed class ForwardB : ForwardMid
    {
        public string D;
    }

    [NinoType]
    public sealed class ForwardC : ForwardMid
    {
        public long E;
    }

    [NinoType]
    public sealed class ForwardOther : ForwardRoot
    {
        public double F;
    }

    [TestClass]
    public class DispatchTests
    {
        [TestMethod]
        public void ElementLoopWritesWhatEachElementWritesAlone()
        {
            DispatchRoot[] values =
            {
                new DispatchSibling { A = 1, D = 2.5 }, null, new DispatchLeaf { A = 3, B = "leaf", C = 4 },
                new DispatchMid { A = 5, B = "mid" }, new DispatchHolder { A = 6, Kids = new List<DispatchRoot> { null } },
            };
            var expected = new List<byte>(BitConverter.GetBytes(values.Length));
            foreach (var value in values)
                expected.AddRange(NinoSerializer.Serialize(value));

            CollectionAssert.AreEqual(expected.ToArray(), NinoSerializer.Serialize(values));
            CollectionAssert.AreEqual(expected.ToArray(), NinoSerializer.Serialize(values.ToList()));
        }

        /// <summary>
        /// Arrays, lists and dictionary values of <typeparamref name="T"/> write each element as it writes alone (the
        /// element loops go through the generated WriteRun, the dispatch inline), and so does every other collection
        /// that holds them (#349): a set's entries, a ring, an <c>ImmutableArray</c>, a linked list's nodes, and the
        /// values of a sorted, a concurrent and a sorted-list dictionary and of an array of pairs after their keys.
        /// </summary>
        private static void ElementsMatch<T>(params T[] values) where T : class
        {
            var elements = new List<byte>();
            var pairs = new List<byte>(BitConverter.GetBytes(values.Length));
            for (int i = 0; i < values.Length; i++)
            {
                var alone = NinoSerializer.Serialize(values[i]);
                elements.AddRange(alone);
                pairs.AddRange(BitConverter.GetBytes(i));
                pairs.AddRange(alone);
            }

            var expected = BitConverter.GetBytes(values.Length).Concat(elements).ToArray();
            CollectionAssert.AreEqual(expected, NinoSerializer.Serialize(values));
            CollectionAssert.AreEqual(expected, NinoSerializer.Serialize(values.ToList()));
            var dictionary = new Dictionary<int, T>();
            for (int i = 0; i < values.Length; i++)
                dictionary.Add(i, values[i]);
            CollectionAssert.AreEqual(pairs.ToArray(), NinoSerializer.Serialize(dictionary));

            CollectionAssert.AreEqual(expected, NinoSerializer.Serialize(new HashSet<T>(values, ByReference<T>.Instance)));
            CollectionAssert.AreEqual(expected, NinoSerializer.Serialize(new Queue<T>(values)));
            CollectionAssert.AreEqual(expected, NinoSerializer.Serialize(ImmutableArray.Create(values)));
            CollectionAssert.AreEqual(expected, NinoSerializer.Serialize(new LinkedList<T>(values)));
            CollectionAssert.AreEqual(pairs.ToArray(), NinoSerializer.Serialize(new SortedDictionary<int, T>(dictionary)));
            CollectionAssert.AreEqual(pairs.ToArray(), NinoSerializer.Serialize(new SortedList<int, T>(dictionary)));
            CollectionAssert.AreEqual(pairs.ToArray(), NinoSerializer.Serialize(dictionary.ToArray()));
            var concurrent = new ConcurrentDictionary<int, T>(dictionary);
            CollectionAssert.AreEqual(Pairs(concurrent, concurrent.Count), NinoSerializer.Serialize(concurrent));
            // Gathered a chunk at a time (Nino.Immutable): a tree's nodes, a stack's (bottom to top) and a queue's, a
            // hash tree's buckets in their enumeration order, and a sorted tree's pairs.
            CollectionAssert.AreEqual(expected, NinoSerializer.Serialize(ImmutableList.CreateRange(values)));
            CollectionAssert.AreEqual(expected, NinoSerializer.Serialize(ImmutableStack.CreateRange(values)));
            CollectionAssert.AreEqual(expected, NinoSerializer.Serialize(ImmutableQueue.CreateRange(values)));
            var hashSet = ImmutableHashSet.CreateRange(ByReference<T>.Instance, values.Where(v => v != null));
            CollectionAssert.AreEqual(Elements(hashSet, hashSet.Count), NinoSerializer.Serialize(hashSet));
            var immutable = ImmutableDictionary.CreateRange(dictionary);
            CollectionAssert.AreEqual(Pairs(immutable, immutable.Count), NinoSerializer.Serialize(immutable));
            CollectionAssert.AreEqual(pairs.ToArray(), NinoSerializer.Serialize(ImmutableSortedDictionary.CreateRange(dictionary)));

            // Keys the generated run loop takes (HasRunLoop: their memory is their wire form, or strings).
            PairsMatch(values, i => "key" + i);
            PairsMatch(values, i => (long)i << 40);
            PairsMatch(values, i => (uint)i);
            PairsMatch(values, i => (DispatchKey)i);
            PairsMatch(values, i => (short)i);
        }

        /// <summary>
        /// A dictionary of <paramref name="values"/> writes its count, then each key and each value as they write
        /// alone, in its enumeration order: whole, and with an entry removed (no longer read in place).
        /// </summary>
        private static void PairsMatch<TKey, T>(T[] values, Func<int, TKey> key)
        {
            var dictionary = new Dictionary<TKey, T>();
            for (int i = 0; i < values.Length; i++)
                dictionary.Add(key(i), values[i]);
            CollectionAssert.AreEqual(Pairs(dictionary), NinoSerializer.Serialize(dictionary));

            dictionary.Add(key(values.Length), values[0]);
            dictionary.Remove(key(0));
            CollectionAssert.AreEqual(Pairs(dictionary), NinoSerializer.Serialize(dictionary));
        }

        private static byte[] Pairs<TKey, T>(Dictionary<TKey, T> dictionary) => Pairs(dictionary, dictionary.Count);

        private static byte[] Elements<T>(IEnumerable<T> values, int count)
        {
            var expected = new List<byte>(BitConverter.GetBytes(count));
            foreach (var value in values)
                expected.AddRange(NinoSerializer.Serialize(value));
            return expected.ToArray();
        }

        private static byte[] Pairs<TKey, T>(IEnumerable<KeyValuePair<TKey, T>> dictionary, int count)
        {
            var expected = new List<byte>(BitConverter.GetBytes(count));
            foreach (var pair in dictionary)
            {
                expected.AddRange(NinoSerializer.Serialize(pair.Key));
                expected.AddRange(NinoSerializer.Serialize(pair.Value));
            }

            return expected.ToArray();
        }

        /// <summary>Equal only to itself, as values of a type that overrides Equals are not.</summary>
        private sealed class ByReference<T> : IEqualityComparer<T> where T : class
        {
            public static readonly ByReference<T> Instance = new();

            public bool Equals(T x, T y) => ReferenceEquals(x, y);

            public int GetHashCode(T value) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(value);
        }

        public enum DispatchKey
        {
        }

        [TestMethod]
        public void PolymorphicDictionariesRoundTripAtEverySize()
        {
            foreach (int count in new[] { 1, 4, 12, 32, 100 })
            {
                var byInt = new Dictionary<int, DispatchRoot>();
                var byString = new Dictionary<string, DispatchRoot>();
                for (int i = 0; i < count; i++)
                {
                    DispatchRoot value = (i % 4) switch
                    {
                        0 => new DispatchSibling { A = i, D = i * 0.5 },
                        1 => new DispatchLeaf { A = i, B = "leaf" + i, C = -i },
                        2 => new DispatchMid { A = i, B = "mid" + i },
                        _ => i % 8 == 3 ? null : new DispatchHolder { A = i, Kids = new List<DispatchRoot> { null } },
                    };
                    byInt.Add(i * 7, value);
                    byString.Add("k" + i, value);
                }

                RoundTrips(byInt);
                RoundTrips(byString);
            }
        }

        [TestMethod]
        public void PolymorphicDictionariesOfStructKeysRoundTrip()
        {
            // Pairs read into a new dictionary's entries, its buckets built after them (HashTables.Link).
            var byGuid = new Dictionary<Guid, DispatchRoot>();
            for (int i = 0; i < 40; i++)
                byGuid.Add(new Guid(i, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10),
                    i % 3 == 0 ? new DispatchSibling { A = i } : i % 3 == 1 ? null : new DispatchLeaf { A = i, C = i });
            RoundTrips(byGuid);
            var back = NinoDeserializer.Deserialize<Dictionary<Guid, DispatchRoot>>(NinoSerializer.Serialize(byGuid));
            back.Add(Guid.Empty, new DispatchMid { B = "added" });
            Assert.IsTrue(back.Remove(new Guid(3, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10)));
            Assert.AreEqual(40, back.Count);
            Assert.AreEqual("added", ((DispatchMid)back[Guid.Empty]).B);
        }

        [TestMethod]
        public void PolymorphicDictionaryWithAKeyTwiceIsInvalidData()
        {
            // A dictionary's bytes are its count and its pairs, as a list of pairs writes them. Five pairs: from four on
            // the pairs are read into the entries and linked after (NinoEntries, HashTables.Link), so a rejected read
            // leaves the dictionary empty; below four each pair is inserted as it is read.
            var pairs = new List<KeyValuePair<int, DispatchRoot>>
            {
                new(1, new DispatchSibling { A = 1 }), new(2, null), new(3, new DispatchMid { B = "3" }),
                new(4, new DispatchLeaf { A = 4 }), new(1, new DispatchLeaf { A = 2 }),
            };
            var bytes = NinoSerializer.Serialize(pairs);
            var e = Assert.ThrowsExactly<NinoDataException>(() =>
                NinoDeserializer.Deserialize<Dictionary<int, DispatchRoot>>(bytes));
            StringAssert.Contains(e.Message, "twice");

            // Into an existing empty dictionary: rejected, and left empty and usable.
            var target = new Dictionary<int, DispatchRoot>();
            Assert.ThrowsExactly<NinoDataException>(() => NinoDeserializer.Deserialize(bytes, ref target));
            Assert.AreEqual(0, target.Count);
            Assert.IsFalse(target.ContainsKey(1));
            target.Add(5, new DispatchMid { B = "five" });
            Assert.AreEqual("five", ((DispatchMid)target[5]).B);
        }

        [TestMethod]
        public void PolymorphicDictionaryCutShortLeavesNothingBehind()
        {
            var source = new Dictionary<int, DispatchRoot>();
            for (int i = 0; i < 12; i++)
                source.Add(i, new DispatchMid { A = i, B = "v" + i });
            var bytes = NinoSerializer.Serialize(source);
            foreach (int cut in new[] { 6, bytes.Length / 2, bytes.Length - 1 })
            {
                var data = bytes.AsSpan(0, cut).ToArray();
                Assert.ThrowsExactly<NinoDataException>(() =>
                    NinoDeserializer.Deserialize<Dictionary<int, DispatchRoot>>(data));
                var target = new Dictionary<int, DispatchRoot>();
                Assert.ThrowsExactly<NinoDataException>(() => NinoDeserializer.Deserialize(data, ref target));
                Assert.AreEqual(0, target.Count);
                // Its entries hold none of the values read before the data ran out.
                foreach (var pair in target)
                    Assert.Fail("an entry was left: " + pair.Key);
                NinoDeserializer.Deserialize(bytes, ref target);
                Assert.AreEqual(12, target.Count);
                Assert.AreEqual("v7", ((DispatchMid)target[7]).B);
            }
        }

        [TestMethod]
        public void ElementLoopReadsIntoListsOfOtherSubtypes()
        {
            // Null slots and slots of the same subtype read in place; a slot of another type is replaced, a body that
            // nests is read deeper, and an id the dispatch does not know is invalid data.
            DispatchRoot[] values =
            {
                new DispatchSibling { A = 1, D = 2 }, new DispatchLeaf { A = 3, C = 4 }, null,
                new DispatchHolder { A = 5, Kids = new List<DispatchRoot> { new DispatchMid { B = "kid" } } },
                new DispatchMid { A = 6, B = "m" },
            };
            var bytes = NinoSerializer.Serialize(values.ToList());
            var sameSibling = new DispatchSibling { D = 9 };
            var target = new List<DispatchRoot>
            {
                sameSibling, new DispatchSibling(), new DispatchMid(), new DispatchLeaf(), new DispatchHolder(),
            };
            NinoDeserializer.Deserialize(bytes, ref target);
            Assert.AreSame(sameSibling, target[0]);
            Assert.AreEqual(2d, ((DispatchSibling)target[0]).D);
            Assert.AreEqual(4, ((DispatchLeaf)target[1]).C);
            Assert.IsNull(target[2]);
            Assert.AreEqual("kid", ((DispatchMid)((DispatchHolder)target[3]).Kids[0]).B);
            Assert.AreEqual("m", ((DispatchMid)target[4]).B);
            CollectionAssert.AreEqual(bytes, NinoSerializer.Serialize(target));

            // An id no subtype has.
            var unknown = bytes.ToArray();
            BitConverter.GetBytes(0x5EED1234).CopyTo(unknown, 4);
            Assert.ThrowsExactly<NinoDataException>(() => NinoDeserializer.Deserialize<List<DispatchRoot>>(unknown));
        }

        private static void RoundTrips<TKey>(Dictionary<TKey, DispatchRoot> dictionary)
        {
            var bytes = NinoSerializer.Serialize(dictionary);
            var back = NinoDeserializer.Deserialize<Dictionary<TKey, DispatchRoot>>(bytes);
            Assert.AreEqual(dictionary.Count, back.Count);
            foreach (var pair in dictionary)
            {
                var read = back[pair.Key];
                Assert.AreEqual(pair.Value?.GetType(), read?.GetType());
                Assert.AreEqual(pair.Value?.A, read?.A);
            }

            CollectionAssert.AreEqual(bytes, NinoSerializer.Serialize(back));
        }

        [TestMethod]
        public void EveryDispatchingBaseWritesElementsAsAlone()
        {
            // An instantiable base (its own id first), a middle class, a generic root and a generic subtype's base.
            ElementsMatch<DelegLevel1>(new DelegLevel1 { A = 1, Name = "one" }, null,
                new DelegLevel2 { A = 2, Items = new List<int> { 3 } }, new DelegLevel3 { A = 4, D = 5, E = 6 });
            ElementsMatch<DelegLevel2>(new DelegLevel2 { A = 7 }, new DelegLevel3 { A = 8, E = 9 }, null);
            ElementsMatch<PureBox<int>>(new PureIntBox { Value = 1, X = 2 }, null, new PureIntBox { Value = 3 });
            ElementsMatch<GenericPolyBase>(new GenericPolyIntBox { Id = 1, Value = 2 }, null);
            ElementsMatch<NonGenericPolyBase>(new NonGenericPolyLeaf { Id = 5, Value = 6 }, null);
            ElementsMatch<TrackedBox<int>>(new TrackedIntBox { Value = 1 }, null);
        }

        [TestMethod]
        public void DeepElementLoopsRoundTrip()
        {
            // Past NinoDepth.Inline open bodies, the loop writes each element through the dispatch's deep path.
            DispatchRoot node = new DispatchSibling { A = -1, D = 1 };
            for (int i = 0; i < 300; i++)
                node = new DispatchHolder { A = i, Kids = new List<DispatchRoot> { node, null, new DispatchMid { B = "m" + i } } };

            var bytes = NinoSerializer.Serialize(node);
            var back = NinoDeserializer.Deserialize<DispatchRoot>(bytes);
            CollectionAssert.AreEqual(bytes, NinoSerializer.Serialize(back));
            int depth = 0;
            while (back is DispatchHolder holder)
            {
                Assert.IsNull(holder.Kids[1]);
                Assert.AreEqual("m" + holder.A, ((DispatchMid)holder.Kids[2]).B);
                back = holder.Kids[0];
                depth++;
            }

            Assert.AreEqual(300, depth);
            Assert.AreEqual(1d, ((DispatchSibling)back).D);
        }

        [TestMethod]
        public void DeepReadsIntoExistingInstancesGoThroughTheRegistry()
        {
            // The switch reads only shallow bodies into null or the same subtype; past NinoDepth.Inline open bodies, and
            // into a slot holding another type, the registry reads, reusing an instance of the same subtype there too.
            DispatchRoot Chain(int depth, Func<int, DispatchRoot> leaf)
            {
                DispatchRoot node = leaf(-1);
                for (int i = 0; i < depth; i++)
                    node = new DispatchHolder { A = i, Kids = new List<DispatchRoot> { node, leaf(i) } };
                return node;
            }

            var bytes = NinoSerializer.Serialize(Chain(200, i => new DispatchSibling { A = i, D = i * 2 }));
            // The target: the same holders, its leaves of another type every other level and of the same type else.
            DispatchRoot target = Chain(200, i => i % 2 == 0 ? new DispatchMid { B = "old" } : new DispatchSibling());
            var kept = new List<DispatchRoot>();
            for (var node = target; node is DispatchHolder h; node = h.Kids[0])
                kept.Add(h.Kids[1]);

            NinoDeserializer.Deserialize(bytes, ref target);
            CollectionAssert.AreEqual(bytes, NinoSerializer.Serialize(target));
            int level = 0;
            for (var node = target; node is DispatchHolder h; node = h.Kids[0], level++)
            {
                var leaf = (DispatchSibling)h.Kids[1];
                Assert.AreEqual(h.A * 2d, leaf.D);
                if (kept[level] is DispatchSibling)
                    Assert.AreSame(kept[level], leaf);
            }

            Assert.AreEqual(200, level);
        }

        [TestMethod]
        public void EveryLevelRoundTrips()
        {
            DispatchRoot[] values =
            {
                new DispatchMid { A = 1, B = "m" }, new DispatchLeaf { A = 2, B = "l", C = 3 },
                new DispatchSibling { A = 4, D = 5.5 }, null,
            };
            var back = NinoDeserializer.Deserialize<DispatchRoot[]>(NinoSerializer.Serialize(values));
            Assert.AreEqual("m", ((DispatchMid)back[0]).B);
            Assert.AreEqual(3, ((DispatchLeaf)back[1]).C);
            Assert.AreEqual(5.5, ((DispatchSibling)back[2]).D);
            Assert.IsNull(back[3]);

            // Through the middle class: its own type and its subtype.
            DispatchMid mid = new DispatchLeaf { A = 7, B = "x", C = 8 };
            var midBack = NinoDeserializer.Deserialize<DispatchMid>(NinoSerializer.Serialize(mid));
            Assert.AreEqual(8, ((DispatchLeaf)midBack).C);
            midBack = NinoDeserializer.Deserialize<DispatchMid>(NinoSerializer.Serialize(new DispatchMid { B = "own" }));
            Assert.AreEqual(typeof(DispatchMid), midBack.GetType());
        }

        [TestMethod]
        public void SiblingTypeInAMiddleSlotIsRejected()
        {
            var bytes = NinoSerializer.Serialize<DispatchRoot>(new DispatchSibling { D = 1 });
            Assert.ThrowsExactly<NinoDataException>(() => NinoDeserializer.Deserialize<DispatchMid>(bytes));
        }

        [TestMethod]
        public void MiddleClassThroughTheRootsDispatchRoundTripsAndRejectsASibling()
        {
            ForwardMid[] values =
            {
                new ForwardA { A = 1, B = 2, C = 3 }, null, new ForwardB { D = "b" }, new ForwardC { E = 4 },
                new ForwardMid { B = 5 },
            };
            ElementsMatch(values);
            var back = NinoDeserializer.Deserialize<ForwardMid[]>(NinoSerializer.Serialize(values));
            Assert.AreEqual(3, ((ForwardA)back[0]).C);
            Assert.IsNull(back[1]);
            Assert.AreEqual("b", ((ForwardB)back[2]).D);
            Assert.AreEqual(4, ((ForwardC)back[3]).E);
            Assert.AreEqual(typeof(ForwardMid), back[4].GetType());

            var sibling = NinoSerializer.Serialize<ForwardRoot>(new ForwardOther { F = 1 });
            Assert.ThrowsExactly<NinoDataException>(() => NinoDeserializer.Deserialize<ForwardMid>(sibling));
        }

        [TestMethod]
        public void InstanceOfAnotherSubtypeIsReplaced()
        {
            DispatchRoot target = new DispatchSibling { D = 9 };
            NinoDeserializer.Deserialize(NinoSerializer.Serialize<DispatchRoot>(new DispatchLeaf { C = 6 }), ref target);
            Assert.AreEqual(6, ((DispatchLeaf)target).C);

            // The same subtype is reused in place.
            var same = target;
            NinoDeserializer.Deserialize(NinoSerializer.Serialize<DispatchRoot>(new DispatchLeaf { C = 10 }), ref target);
            Assert.AreSame(same, target);
            Assert.AreEqual(10, ((DispatchLeaf)target).C);
        }
    }
}
