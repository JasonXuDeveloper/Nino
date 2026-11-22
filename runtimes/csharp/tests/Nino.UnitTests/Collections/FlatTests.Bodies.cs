using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nino.Core;
using Nino.Core.Internal;
using Nino.UnitTests.Subset.Flat;

namespace Nino.UnitTests
{
    /// <summary>
    /// Step C of #355: keyed, version-tolerant and nested bodies on the one item chain, as leaves (NinoKeyItem,
    /// NinoEntriesItem, NinoTolerantItem, NinoRegionItem, a nested type's chain as one item). The bytes against twins
    /// written by generated bodies, reads across versions, reads into existing elements, the runs through the kernels
    /// (their drivers on the stack), runs past the inline depth, and hostile data read the same way both ways.
    /// </summary>
    public partial class FlatTests
    {
        private static readonly int[] RunCounts = { 1, 15, 16, 17, 100, 600 };

        private static FlatKeyed[] Keyeds(int count, int seed = 11)
        {
            var random = new Random(seed);
            return Enumerable.Range(0, count).Select(i => i % 19 == 7 ? null : new FlatKeyed
            {
                Id = random.Next(), Name = i % 5 == 2 ? null : Text(random, 0, 30, i % 3 == 1),
                Big = i % 4 == 0 ? null : random.Next() * 3L, Flag = i % 2 == 1,
                Tag = i % 7 == 3 ? "" : Text(random, 1, 12, true), At = new DateTime(2022, 3, 4).AddMinutes(i),
            }).ToArray();
        }

        private static FlatKeyedTwin Twin(FlatKeyed v) => v == null ? null : new FlatKeyedTwin
            { Id = v.Id, Name = v.Name, Big = v.Big, Flag = v.Flag, Tag = v.Tag, At = v.At };

        private static FlatTolerant[] Tolerants(int count, int seed = 12)
        {
            var random = new Random(seed);
            return Enumerable.Range(0, count).Select(i => i % 17 == 4 ? null : new FlatTolerant
            {
                Id = random.Next(), Name = i % 6 == 1 ? null : Text(random, 0, 25, i % 4 == 2),
                Count = i % 3 == 0 ? null : -i, Score = random.NextDouble(), Note = i % 5 == 0 ? null : "n" + i,
            }).ToArray();
        }

        private static FlatTolerantTwin Twin(FlatTolerant v) => v == null ? null : new FlatTolerantTwin
            { Id = v.Id, Name = v.Name, Count = v.Count, Score = v.Score, Note = v.Note };

        private static FlatOuter[] Outers(int count, int seed = 13)
        {
            var random = new Random(seed);
            var ones = Ones(count, true, seed);
            return Enumerable.Range(0, count).Select(i => i % 23 == 11 ? null : new FlatOuter
            {
                Id = i,
                Leaf = i % 7 == 2 ? null : new FlatLeaf { Id = -i, Name = Text(random, 0, 10, i % 2 == 0) },
                Name = i % 9 == 4 ? null : Text(random, 1, 20, false),
                Point = new FlatPoint
                {
                    Id = i, Name = i % 4 == 1 ? null : "p" + i, X = i * 0.5, Flag = i % 3 == 0,
                    Count = i % 5 == 0 ? null : i, At = new DateTime(2023, 1, 1).AddHours(i), Tag = "t",
                },
                One = ones[i],
                Tolerant = new FlatTolerantPoint { Id = i, Name = i % 8 == 0 ? null : "tp", Big = i % 2 == 0 ? null : i },
            }).ToArray();
        }

        private static FlatOuterTwin Twin(FlatOuter v) => v == null ? null : new FlatOuterTwin
        {
            Id = v.Id, Leaf = v.Leaf, Name = v.Name, One = v.One,
            Point = new FlatPointTwin
            {
                Id = v.Point.Id, Name = v.Point.Name, X = v.Point.X, Flag = v.Point.Flag, Count = v.Point.Count,
                At = v.Point.At, Tag = v.Point.Tag,
            },
            Tolerant = new FlatTolerantPointTwin { Id = v.Tolerant.Id, Name = v.Tolerant.Name, Big = v.Tolerant.Big },
        };

        /// <summary>
        /// The runs of <typeparamref name="T"/> against those of its twin <typeparamref name="TTwin"/> (written by
        /// generated bodies): the same bytes as an array and a list, read back to the same bytes, read into existing
        /// elements (other values), and every cut rejected.
        /// </summary>
        private static void SameRuns<T, TTwin>(Func<int, int, T[]> values, Func<T, TTwin> twin, string name)
        {
            foreach (int count in RunCounts)
            {
                var array = values(count, 1);
                byte[] bytes = NinoSerializer.Serialize(array);
                CollectionAssert.AreEqual(NinoSerializer.Serialize(array.Select(twin).ToArray()), bytes, $"{name} {count}");
                CollectionAssert.AreEqual(bytes, NinoSerializer.Serialize(array.ToList()), $"{name} list {count}");
                CollectionAssert.AreEqual(bytes, NinoSerializer.Serialize(NinoDeserializer.Deserialize<T[]>(bytes)),
                    $"{name} read {count}");
                CollectionAssert.AreEqual(bytes, NinoSerializer.Serialize(NinoDeserializer.Deserialize<List<T>>(bytes)),
                    $"{name} list read {count}");
                var existing = values(count, 2);
                NinoDeserializer.Deserialize(bytes, ref existing);
                CollectionAssert.AreEqual(bytes, NinoSerializer.Serialize(existing), $"{name} into existing {count}");
                for (int cut = 0; cut < bytes.Length; cut += Math.Max(1, bytes.Length / 29))
                {
                    var prefix = bytes.AsSpan(0, cut).ToArray();
                    Assert.ThrowsExactly<NinoDataException>(() => NinoDeserializer.Deserialize<T[]>(prefix),
                        $"{name} cut {cut} of {count}");
                }
            }
        }

        /// <summary>
        /// <paramref name="bytes"/> read as <typeparamref name="T"/>[] (the flat kernels) and as
        /// <typeparamref name="TTwin"/>[] (generated bodies): both rejected (<see cref="NinoDataException"/>), or both
        /// read to values that write the same bytes. Hostile data reads the same way either way.
        /// </summary>
        private static void ReadsAlike<T, TTwin>(byte[] bytes, string what)
        {
            byte[] flat = null, twin = null;
            Exception flatError = null, twinError = null;
            try { flat = NinoSerializer.Serialize(NinoDeserializer.Deserialize<T[]>(bytes)); }
            catch (Exception e) { flatError = e; }
            try { twin = NinoSerializer.Serialize(NinoDeserializer.Deserialize<TTwin[]>(bytes)); }
            catch (Exception e) { twinError = e; }
            if (flatError != null || twinError != null)
            {
                Assert.IsInstanceOfType<NinoDataException>(flatError, $"{what}: flat {flatError}, twin {twinError}");
                Assert.IsInstanceOfType<NinoDataException>(twinError, $"{what}: flat {flatError}, twin {twinError}");
                return;
            }

            CollectionAssert.AreEqual(twin, flat, what);
        }

        /// <summary>
        /// Every byte of <paramref name="bytes"/> (a run long enough for the kernels) changed in turn, and every int32
        /// position set to values a reader checks (0, -1, a sign bit, too long): read alike both ways.
        /// </summary>
        private static void MutationsReadAlike<T, TTwin>(byte[] bytes, string name)
        {
            var random = new Random(bytes.Length);
            int step = Math.Max(1, bytes.Length / 400);
            for (int i = 4; i < bytes.Length; i += step)
            {
                var bad = (byte[])bytes.Clone();
                bad[i] = (byte)(bad[i] + 1 + random.Next(255));
                ReadsAlike<T, TTwin>(bad, $"{name} byte {i}");
            }

            foreach (int value in new[] { 0, -1, int.MinValue | 8, 1 << 20, 3 })
            for (int i = 4; i + 4 <= bytes.Length; i += step * 3)
            {
                var bad = (byte[])bytes.Clone();
                BitConverter.GetBytes(value).CopyTo(bad, i);
                ReadsAlike<T, TTwin>(bad, $"{name} int {value} at {i}");
            }
        }

        [TestMethod]
        public void KeyedBodiesTakeTheChain()
        {
            Assert.IsTrue(HasFlat(Formatter<FlatKeyed>()));
            Assert.IsTrue(HasFlat(Formatter<FlatKeyedPoint>()));
            Assert.IsFalse(HasFlat(Formatter<FlatKeyedTwin>()));
            SameRuns((count, seed) => Keyeds(count, 11 + seed), Twin, "keyed");
            SameRuns((count, seed) => Enumerable.Range(0, count)
                .Select(i => new FlatKeyedPoint { Name = i % 3 == seed ? null : "k" + i * seed, Id = i * seed }).ToArray(),
                static p => new FlatKeyedPointTwin { Name = p.Name, Id = p.Id }, "keyed struct");

            // Across versions: entries the reader does not know, in another order, under a former name, or missing
            // leave the element to the formatter, which reads them in any order. A former name is read, never written:
            // FlatKeyedOld writes Enabled under its own name, which FlatKeyed does not know.
            var keyeds = Keyeds(40).Where(static v => v != null).ToArray();
            var olds = NinoDeserializer.Deserialize<FlatKeyedOld[]>(NinoSerializer.Serialize(keyeds));
            for (int i = 0; i < keyeds.Length; i++)
                Assert.IsTrue(olds[i].Id == keyeds[i].Id && olds[i].Name == keyeds[i].Name &&
                              olds[i].Enabled == keyeds[i].Flag, $"old {i}");
            var news = NinoDeserializer.Deserialize<FlatKeyed[]>(NinoSerializer.Serialize(olds));
            for (int i = 0; i < keyeds.Length; i++)
                Assert.IsTrue(news[i].Id == keyeds[i].Id && news[i].Name == keyeds[i].Name && !news[i].Flag &&
                              news[i].Big == null && news[i].Tag == null, $"new {i}");

            var (writes, reads) = Stacks(keyeds);
            Assert.IsTrue(writes[0].Contains("NinoFlat`2.WriteSealed"), "FlatKeyed[] write");
            Assert.IsTrue(reads[0].Contains("NinoFlat`2.ReadSealed"), "FlatKeyed[] read");
            MutationsReadAlike<FlatKeyed, FlatKeyedTwin>(NinoSerializer.Serialize(Keyeds(20)), "keyed");
            MutationsReadAlike<FlatKeyedPoint, FlatKeyedPointTwin>(NinoSerializer.Serialize(Enumerable.Range(0, 20)
                .Select(static i => new FlatKeyedPoint { Name = "k" + i, Id = i }).ToArray()), "keyed struct");
        }

        [TestMethod]
        public void VersionTolerantBodiesTakeTheChain()
        {
            Assert.IsTrue(HasFlat(Formatter<FlatTolerant>()));
            Assert.IsTrue(HasFlat(Formatter<FlatTolerantPoint>()));
            Assert.IsFalse(HasFlat(Formatter<FlatTolerantTwin>()));
            SameRuns((count, seed) => Tolerants(count, 12 + seed), Twin, "tolerant");
            SameRuns((count, seed) => Enumerable.Range(0, count)
                .Select(i => new FlatTolerantPoint { Id = i * seed, Name = i % 4 == seed ? null : "v" + i, Big = i % 3 == 0 ? null : i })
                .ToArray(), static p => new FlatTolerantPointTwin { Id = p.Id, Name = p.Name, Big = p.Big }, "tolerant struct");

            // An older version's bytes: the members it lacks keep their values (a property's initializer, not
            // overwritten); a newer version's: the members after the last known one skipped.
            var tolerants = Tolerants(40).Where(static v => v != null).ToArray();
            var olds = tolerants.Select(static v => new FlatTolerantOld { Id = v.Id, Name = v.Name }).ToArray();
            var news = NinoDeserializer.Deserialize<FlatTolerant[]>(NinoSerializer.Serialize(olds));
            for (int i = 0; i < olds.Length; i++)
                Assert.IsTrue(news[i].Id == olds[i].Id && news[i].Name == olds[i].Name && news[i].Count == null &&
                              news[i].Note == "unset", $"new {i}");
            var existing = Tolerants(olds.Length, 3).Select(static v => v ?? new FlatTolerant()).ToArray();
            var kept = existing.Select(static v => (v.Count, v.Score, v.Note)).ToArray();
            NinoDeserializer.Deserialize(NinoSerializer.Serialize(olds), ref existing);
            for (int i = 0; i < olds.Length; i++)
                Assert.IsTrue(existing[i].Id == olds[i].Id && (existing[i].Count, existing[i].Score, existing[i].Note) == kept[i],
                    $"existing {i}");
            var back = NinoDeserializer.Deserialize<FlatTolerantOld[]>(NinoSerializer.Serialize(tolerants));
            for (int i = 0; i < olds.Length; i++)
                Assert.IsTrue(back[i].Id == olds[i].Id && back[i].Name == olds[i].Name, $"old {i}");

            var (writes, reads) = Stacks(tolerants);
            Assert.IsTrue(writes[0].Contains("NinoFlat`2.WriteSealed"), "FlatTolerant[] write");
            Assert.IsTrue(reads[0].Contains("NinoFlat`2.ReadSealed"), "FlatTolerant[] read");
            MutationsReadAlike<FlatTolerant, FlatTolerantTwin>(NinoSerializer.Serialize(Tolerants(20)), "tolerant");
        }

        [TestMethod]
        public void NestedBodiesTakeTheChain()
        {
            Assert.IsTrue(HasFlat(Formatter<FlatOuter>()));
            Assert.IsTrue(HasFlat(Formatter<FlatWrap>()));
            Assert.IsFalse(HasFlat(Formatter<FlatOuterTwin>()));
            // FlatOne's subtypes among the nested members leave their elements to the formatter.
            SameRuns((count, seed) => Outers(count, 13 + seed), Twin, "outer");
            SameRuns((count, seed) => Enumerable.Range(0, count).Select(i => new FlatWrap
            {
                Many = i % 5 == seed ? null : Manys(1, false, i + seed)[0], X = i * seed,
            }).ToArray(), static w => new FlatWrapTwin { Many = Props(w.Many), X = w.X }, "wrap");

            var outers = Outers(60).Where(static v => v != null).ToArray();
            var (writes, reads) = Stacks(outers);
            Assert.IsTrue(writes[0].Contains("NinoFlat`2.WriteSealed"), "FlatOuter[] write");
            Assert.IsTrue(reads[0].Contains("NinoFlat`2.ReadSealed"), "FlatOuter[] read");

            // Into existing elements: nested objects kept where the data has them, a held one dropped for null data.
            byte[] bytes = NinoSerializer.Serialize(outers);
            var into = NinoDeserializer.Deserialize<FlatOuter[]>(bytes);
            var leaves = into.Select(static o => o.Leaf).ToArray();
            NinoDeserializer.Deserialize(bytes, ref into);
            for (int i = 0; i < into.Length; i++)
                Assert.AreSame(leaves[i], into[i].Leaf, $"leaf {i} kept");
            var cleared = outers.Select(static o => new FlatOuter { Id = o.Id, Name = o.Name, Point = o.Point, One = o.One }).ToArray();
            NinoDeserializer.Deserialize(NinoSerializer.Serialize(cleared), ref into);
            Assert.IsTrue(into.All(static o => o.Leaf == null));
            MutationsReadAlike<FlatOuter, FlatOuterTwin>(NinoSerializer.Serialize(Outers(20)), "outer");
        }

        [TestMethod]
        public void NestedRunsPastTheInlineDepthWriteTheGeneratedBodysBytes()
        {
            // A run whose nested class bodies would open at NinoDepth.Inline goes element by element (deferred), the
            // ones above through the kernels; both as the generated bodies write them.
            const int depth = 200;
            FlatOuterNode node = null;
            FlatOuterNodeTwin twin = null;
            for (int i = 0; i < depth; i++)
            {
                var items = Outers(NinoFlatRuns.MinRun + 1, i).Select(static o => o ?? new FlatOuter()).ToArray();
                node = new FlatOuterNode { Items = items, Next = node };
                twin = new FlatOuterNodeTwin { Items = items.Select(Twin).ToArray(), Next = twin };
            }

            byte[] bytes = NinoSerializer.Serialize(node);
            CollectionAssert.AreEqual(NinoSerializer.Serialize(twin), bytes);
            CollectionAssert.AreEqual(bytes, NinoSerializer.Serialize(NinoDeserializer.Deserialize<FlatOuterNode>(bytes)));
        }

        [TestMethod]
        public void DictionariesOfFlatStructsSkipFreeEntries()
        {
            var map = Enumerable.Range(0, 40).ToDictionary(static i => i, static i => new FlatTolerantPoint { Id = i, Name = "d" + i });
            var twins = map.ToDictionary(static p => p.Key, static p => new FlatTolerantPointTwin { Id = p.Value.Id, Name = p.Value.Name });
            map.Remove(7);
            twins.Remove(7);
            byte[] bytes = NinoSerializer.Serialize(map);
            CollectionAssert.AreEqual(NinoSerializer.Serialize(twins), bytes);
            var back = NinoDeserializer.Deserialize<Dictionary<int, FlatTolerantPoint>>(bytes);
            Assert.AreEqual(39, back.Count);
            Assert.AreEqual("d8", back[8].Name);
        }
    }
}
