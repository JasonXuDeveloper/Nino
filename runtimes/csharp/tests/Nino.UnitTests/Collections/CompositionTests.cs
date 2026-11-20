using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nino.Core;
using Nino.Core.Internal;
using RootFormatter = NinoGen.Nino_UnitTests_DispatchRootFormatter;

#nullable disable
namespace Nino.UnitTests
{
    /// <summary>
    /// Composites (a <c>KeyValuePair</c>, a value tuple of two) compose what their items do on a run, one rule for all of
    /// them (NinoItems): a run holding strings through the string run kernels, a polymorphic second item through its
    /// base's run loop, and a dropped composite's items recycled. The bytes are always each item's own write.
    /// </summary>
    [TestClass]
    [DoNotParallelize]
    public class CompositionTests
    {
        private static string Text(int i) => (i % 4) switch
        {
            0 => null,
            1 => "",
            2 => "héllo" + i,
            _ => new string('x', 50 + i % 90),
        };

        /// <summary>The count, then each tuple's items through their own formatters.</summary>
        private static byte[] Expected<T1, T2, TF1, TF2>(IReadOnlyCollection<(T1, T2)> values)
            where TF1 : struct, INinoFormatter<T1>
            where TF2 : struct, INinoFormatter<T2>
        {
            var buffer = new NinoArrayBufferWriter();
            var writer = new Writer(buffer);
            writer.WriteLength(values.Count);
            foreach (var (a, b) in values)
            {
                default(TF1).Serialize(a, ref writer);
                default(TF2).Serialize(b, ref writer);
            }

            writer.Flush();
            return buffer.ToArray();
        }

        private static void Tuples<T1, T2, TF1, TF2>(Func<int, T1> first, Func<int, T2> second, bool cursor)
            where TF1 : struct, INinoFormatter<T1>
            where TF2 : struct, INinoFormatter<T2>
        {
            string name = $"({typeof(T1).Name}, {typeof(T2).Name})";
            Assert.AreEqual(cursor, NinoKind<ValueTupleFormatter<T1, T2, TF1, TF2>>.Cursor, name);
            foreach (int count in new[] { 0, 1, 7, 300 })
            {
                var array = Enumerable.Range(0, count).Select(i => (first(i), second(i))).ToArray();
                var list = array.ToList();
                byte[] expected = Expected<T1, T2, TF1, TF2>(array);
                byte[] bytes = NinoSerializer.Serialize(array);
                CollectionAssert.AreEqual(expected, bytes, name);
                CollectionAssert.AreEqual(expected, NinoSerializer.Serialize(list), name);
                CollectionAssert.AreEqual(array, NinoDeserializer.Deserialize<(T1, T2)[]>(bytes), name);
                CollectionAssert.AreEqual(list, NinoDeserializer.Deserialize<List<(T1, T2)>>(bytes), name);

                // Into an existing array of the length: each item read over the old one.
                var existing = Enumerable.Range(0, count).Select(i => (first(i + 1), second(i + 1))).ToArray();
                NinoDeserializer.Deserialize(bytes, ref existing);
                CollectionAssert.AreEqual(array, existing, name);

                for (int cut = 4; cut < bytes.Length; cut += Math.Max(1, bytes.Length / 31))
                {
                    var prefix = bytes.AsSpan(0, cut).ToArray();
                    Assert.ThrowsExactly<NinoDataException>(() => NinoDeserializer.Deserialize<(T1, T2)[]>(prefix),
                        $"{name}, cut {cut}");
                }
            }
        }

        /// <summary>
        /// Value tuples of two with a string among their items take the string run kernels, as a pair does (a list, an
        /// array, in both encodings' default form), and write each item's bytes.
        /// </summary>
        [TestMethod]
        public void TuplesHoldingStringsTakeTheStringRuns()
        {
            Tuples<int, string, UnmanagedFormatter<int>, StringFormatter>(i => i * 3, Text, true);
            Tuples<string, string, StringFormatter, StringFormatter>(Text, i => Text(i + 1), true);
            Tuples<string, long, StringFormatter, UnmanagedFormatter<long>>(Text, i => -i * 1000L, true);
            Tuples<string, DateTimeOffset, StringFormatter, DateTimeOffsetFormatter>(Text,
                i => new DateTimeOffset(2021, 3, 4, 5, 6, 7, TimeSpan.FromMinutes(i % 300)), true);
            // A tuple of values is a block, not a cursor run.
            Tuples<int, long, UnmanagedFormatter<int>, UnmanagedFormatter<long>>(i => i, i => i * 7L, false);
        }

        /// <summary>
        /// A value tuple of two whose second item is a polymorphic base with a run loop composes onto that loop, its
        /// first item written by it, as a pair does: the bytes of each item's own write.
        /// </summary>
        [TestMethod]
        public void TuplesOfAPolymorphicItemTakeItsRunLoop()
        {
            Assert.IsTrue(NinoKind<ValueTupleFormatter<int, DispatchRoot, UnmanagedFormatter<int>, RootFormatter>>.Runs);
            Assert.IsTrue(NinoKind<ValueTupleFormatter<string, DispatchRoot, StringFormatter, RootFormatter>>.Runs);
            DispatchRoot Root(int i) => (i % 3) switch
            {
                0 => null,
                1 => new DispatchLeaf { A = i, B = "b" + i, C = -i },
                _ => new DispatchSibling { A = i * 2, D = i * 0.5 },
            };

            foreach (int count in new[] { 0, 1, 40 })
            {
                var ints = Enumerable.Range(0, count).Select(i => (i, Root(i))).ToList();
                byte[] bytes = NinoSerializer.Serialize(ints);
                CollectionAssert.AreEqual(
                    Expected<int, DispatchRoot, UnmanagedFormatter<int>, RootFormatter>(ints), bytes);
                var back = NinoDeserializer.Deserialize<List<(int, DispatchRoot)>>(bytes);
                Assert.AreEqual(count, back.Count);
                CollectionAssert.AreEqual(bytes, NinoSerializer.Serialize(back));

                var strings = Enumerable.Range(0, count).Select(i => (Text(i), Root(i))).ToArray();
                CollectionAssert.AreEqual(
                    Expected<string, DispatchRoot, StringFormatter, RootFormatter>(strings),
                    NinoSerializer.Serialize(strings));
            }
        }

        private readonly List<object> _recycled = new();

        /// <summary>
        /// A collection that drops composites recycles their items, each once as its own type, whatever holds them: a
        /// list's tail of pairs, an array's dropped tuples, a dictionary's pairs, a refilled set of tuples.
        /// </summary>
        [TestMethod]
        public void DroppedCompositesRecycleTheirItems()
        {
            NinoSettings.SetRecycler<RcItem>(item => _recycled.Add(item));
            try
            {
                var items = Enumerable.Range(0, 4).Select(i => new RcItem { Id = i }).ToArray();

                // A list of pairs that shrinks: the dropped pairs' values.
                var pairs = items.Select(i => new KeyValuePair<string, RcItem>("k" + i.Id, i)).ToList();
                NinoDeserializer.Deserialize(NinoSerializer.Serialize(pairs.Take(1).ToList()), ref pairs);
                CollectionAssert.AreEquivalent(items.Skip(1).ToArray(), _recycled);
                _recycled.Clear();

                // An array of tuples read into a shorter array: the dropped tuples' items.
                var tuples = items.Select(i => (i, i.Id)).ToArray();
                NinoDeserializer.Deserialize(NinoSerializer.Serialize(tuples.Take(2).ToArray()), ref tuples);
                CollectionAssert.AreEquivalent(items.Skip(2).ToArray(), _recycled);
                _recycled.Clear();

                // A set of tuples refilled: every old tuple's item.
                var set = new HashSet<(int, RcItem)>(items.Select(i => (i.Id, i)));
                NinoDeserializer.Deserialize(NinoSerializer.Serialize(new HashSet<(int, RcItem)>()), ref set);
                CollectionAssert.AreEquivalent(items, _recycled);
                _recycled.Clear();

                // A dictionary refilled with other keys: the stale pairs' values.
                var map = items.ToDictionary(i => i.Id, i => i);
                NinoDeserializer.Deserialize(NinoSerializer.Serialize(new Dictionary<int, RcItem>()), ref map);
                CollectionAssert.AreEquivalent(items, _recycled);
            }
            finally
            {
                NinoSettings.SetRecycler<RcItem>(null);
                _recycled.Clear();
            }
        }
    }
}
