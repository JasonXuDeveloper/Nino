using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nino.Core;
using Nino.Core.CompilerServices;
using Nino.Core.Internal;
using Nino.UnitTests.Subset.Flat;

namespace Nino.UnitTests
{
    /// <summary>
    /// The flat kernels (#355) against the generated body: the bytes of runs, bodies and members written both ways,
    /// read back both ways, reads into existing instances, subtypes in runs, and hostile data.
    /// </summary>
    [TestClass]
    public class FlatTests
    {
        private const string Chars = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789 ";

        private static string Text(Random random, int min, int max, bool wide)
        {
            var chars = new char[random.Next(min, max + 1)];
            for (int i = 0; i < chars.Length; i++)
                chars[i] = wide && i % 7 == 3 ? (char)(0x4E00 + random.Next(500)) : Chars[random.Next(Chars.Length)];
            return new string(chars);
        }

        private static FlatOne[] Ones(int count, bool mixed, int seed = 1)
        {
            var random = new Random(seed);
            var values = new FlatOne[count];
            for (int i = 0; i < count; i++)
            {
                if (mixed && i % 11 == 5)
                    continue;
                var value = mixed && i % 13 == 7 ? new FlatOneDerived { Extra = i } : new FlatOne();
                value.Stamp = random.Next() * 1000L;
                value.Score = random.NextDouble();
                value.Id = random.Next();
                value.Kind = (FlatKind)(i % 3);
                value.Name = mixed && i % 17 == 3 ? null : mixed && i % 19 == 4 ? "" : Text(random, 8, 24, mixed);
                values[i] = value;
            }

            return values;
        }

        private static FlatMany[] Manys(int count, bool mixed, int seed = 2)
        {
            var random = new Random(seed);
            var values = new FlatMany[count];
            for (int i = 0; i < count; i++)
            {
                if (mixed && i % 11 == 5)
                    continue;
                values[i] = new FlatMany
                {
                    Id = random.Next(),
                    Name = mixed && i % 17 == 3 ? null : Text(random, 6, 16, mixed),
                    Tag = mixed && i % 19 == 4 ? "" : Text(random, 4, 10, mixed),
                    At = new DateTime(2020, 1, 1, 0, 0, 0, (DateTimeKind)(i % 3)).AddSeconds(random.Next()),
                    Flag = random.Next(2) == 0,
                    Text = Text(random, 20, 60, mixed),
                    Price = random.Next() / 100m,
                };
            }

            return values;
        }

        private static FlatManyProps Props(FlatMany v) => v == null ? null : new FlatManyProps
            { Id = v.Id, Name = v.Name, Tag = v.Tag, At = v.At, Flag = v.Flag, Text = v.Text, Price = v.Price };

        private static FlatOneProps Props(FlatOne v) => v == null ? null : new FlatOneProps
            { Stamp = v.Stamp, Score = v.Score, Id = v.Id, Kind = v.Kind, Name = v.Name };

        private static bool Same(FlatOne a, FlatOne b) =>
            a == null ? b == null :
            b != null && a.GetType() == b.GetType() && a.Stamp == b.Stamp && a.Score.Equals(b.Score) && a.Id == b.Id &&
            a.Kind == b.Kind && a.Name == b.Name && (a is not FlatOneDerived d || ((FlatOneDerived)b).Extra == d.Extra);

        private static bool Same(FlatMany a, FlatMany b) =>
            a == null ? b == null :
            b != null && a.Id == b.Id && a.Name == b.Name && a.Tag == b.Tag && a.At == b.At &&
            a.At.Kind == b.At.Kind && a.Flag == b.Flag && a.Text == b.Text && a.Price == b.Price;

        private static readonly int[] Counts = { 1, 2, 7, 100, 1000 };

        /// <summary>The generated formatter of <typeparamref name="T"/> (internal to the Subset assembly), or its twin.</summary>
        private static Type Formatter<T>(string twin = null)
        {
            var type = typeof(T).Assembly.GetType($"NinoGen.Nino_UnitTests_Subset_Flat_{typeof(T).Name}Formatter", true);
            return twin == null ? type : type.GetNestedType(twin, BindingFlags.NonPublic | BindingFlags.Public);
        }

        private static bool HasFlat(Type formatter) => ((INinoFormatter)Activator.CreateInstance(formatter)).HasFlat;

        /// <summary>The bytes of a run element by element through the formatter (the loop every other class takes).</summary>
        private static byte[] Each<T>(T[] values) =>
            (byte[])typeof(FlatTests).GetMethod(nameof(EachWith), BindingFlags.NonPublic | BindingFlags.Static)!
                .MakeGenericMethod(typeof(T), Formatter<T>()).Invoke(null, new object[] { values });

        private static byte[] EachWith<T, TFormatter>(T[] values) where TFormatter : struct, INinoFormatter<T>
        {
            var buffer = new NinoArrayBufferWriter();
            var writer = new Writer(buffer);
            writer = NinoRuns.WriteEach<T, TFormatter>(NinoRun.Of<T>(values), values.Length, writer);
            writer.Flush();
            return buffer.WrittenSpan.ToArray();
        }

        [TestMethod]
        public void FlatTypesTakeTheKernels()
        {
            Assert.IsTrue(HasFlat(Formatter<FlatOne>()));
            Assert.IsTrue(HasFlat(Formatter<FlatMany>()));
            Assert.IsTrue(HasFlat(Formatter<FlatMany>("__Top")));
            Assert.IsFalse(HasFlat(Formatter<FlatOneProps>()));
            Assert.IsFalse(HasFlat(Formatter<FlatManyProps>()));
            Assert.IsTrue(HasFlat(Formatter<FlatOneDerived>()));
            // The generator predicted FlatOne's block as CoreCLR lays it out (64-bit; others take the chain of fields).
            if (Environment.Is64BitProcess && Type.GetType("Mono.Runtime") == null)
                Assert.IsTrue(Checked<FlatOne>("Write"));
            Assert.AreEqual(Checked<FlatMany>("Write") && !NinoSettings.Untrusted, Checked<FlatMany>("Read"));
        }

        /// <summary>A flag of the startup check (__Check) of <typeparamref name="T"/>'s formatter.</summary>
        private static bool Checked<T>(string flag) =>
            (bool)Formatter<T>("__Check").GetField(flag, BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;

        [TestMethod]
        public void SealedRunsWriteTheGeneratedBodysBytes()
        {
            foreach (int count in Counts)
            foreach (bool mixed in new[] { false, true })
            {
                var values = Manys(count, mixed);
                var props = values.Select(Props).ToArray();
                byte[] expected = NinoSerializer.Serialize(props);
                CollectionAssert.AreEqual(expected, NinoSerializer.Serialize(values), $"array {count} {mixed}");
                CollectionAssert.AreEqual(expected, NinoSerializer.Serialize(values.ToList()), $"list {count} {mixed}");
                CollectionAssert.AreEqual(expected, Each(values), $"each {count} {mixed}");
                CollectionAssert.AreEqual(NinoSerializer.Serialize(props.ToList()),
                    NinoSerializer.Serialize(values.ToList()));

                var array = NinoDeserializer.Deserialize<FlatMany[]>(expected);
                var list = NinoDeserializer.Deserialize<List<FlatMany>>(expected);
                for (int i = 0; i < count; i++)
                {
                    Assert.IsTrue(Same(values[i], array[i]), $"array element {i}");
                    Assert.IsTrue(Same(values[i], list[i]), $"list element {i}");
                }
            }
        }

        [TestMethod]
        public void TypedRunsWriteTheGeneratedBodysBytes()
        {
            foreach (int count in Counts)
            foreach (bool mixed in new[] { false, true })
            {
                var values = Ones(count, mixed);
                byte[] each = Each(values);
                CollectionAssert.AreEqual(each, NinoSerializer.Serialize(values), $"array {count} {mixed}");
                CollectionAssert.AreEqual(each, NinoSerializer.Serialize(values.ToList()), $"list {count} {mixed}");
                var array = NinoDeserializer.Deserialize<FlatOne[]>(each);
                var list = NinoDeserializer.Deserialize<List<FlatOne>>(each);
                for (int i = 0; i < count; i++)
                {
                    Assert.IsTrue(Same(values[i], array[i]), $"array element {i}");
                    Assert.IsTrue(Same(values[i], list[i]), $"list element {i}");
                }

                // Each body against the generated body of the same members (the type ids differ).
                foreach (var value in values.Where(v => v != null && v.GetType() == typeof(FlatOne)))
                    CollectionAssert.AreEqual(NinoSerializer.Serialize(Props(value)).Skip(4).ToArray(),
                        NinoSerializer.Serialize(value).Skip(4).ToArray());
            }
        }

        [TestMethod]
        public void BodiesAndMembersWriteTheGeneratedBodysBytes()
        {
            foreach (var value in Manys(200, true))
            {
                CollectionAssert.AreEqual(NinoSerializer.Serialize(Props(value)), NinoSerializer.Serialize(value));
                var read = NinoDeserializer.Deserialize<FlatMany>(NinoSerializer.Serialize(value));
                Assert.IsTrue(Same(value, read));
            }

            var holder = new FlatHolder { One = Manys(1, false)[0], Many = Manys(9, true).ToList(), Ones = Ones(9, true) };
            var back = NinoDeserializer.Deserialize<FlatHolder>(NinoSerializer.Serialize(holder));
            Assert.IsTrue(Same(holder.One, back.One));
            Assert.AreEqual(holder.Many.Count, back.Many.Count);
            for (int i = 0; i < holder.Many.Count; i++)
                Assert.IsTrue(Same(holder.Many[i], back.Many[i]));
            for (int i = 0; i < holder.Ones.Length; i++)
                Assert.IsTrue(Same(holder.Ones[i], back.Ones[i]));
        }

        [TestMethod]
        public void SetsAndDictionariesOfFlatClasses()
        {
            // Free entries (a removed element): the same slots in both sets, so the same order.
            var values = Manys(20, false);
            var set = new HashSet<FlatMany>(values);
            var props = new HashSet<FlatManyProps>(values.Select(Props));
            set.Remove(values[3]);
            props.Remove(props.First(p => p.Id == values[3].Id));
            CollectionAssert.AreEqual(NinoSerializer.Serialize(props), NinoSerializer.Serialize(set));
            Assert.AreEqual(19, NinoDeserializer.Deserialize<HashSet<FlatMany>>(NinoSerializer.Serialize(set)).Count);

            var map = values.Select((v, i) => (v, i)).ToDictionary(x => x.i, x => x.v);
            var mapProps = values.Select((v, i) => (v, i)).ToDictionary(x => x.i, x => Props(x.v));
            CollectionAssert.AreEqual(NinoSerializer.Serialize(mapProps), NinoSerializer.Serialize(map));
            var mapBack = NinoDeserializer.Deserialize<Dictionary<int, FlatMany>>(NinoSerializer.Serialize(map));
            for (int i = 0; i < values.Length; i++)
                Assert.IsTrue(Same(values[i], mapBack[i]));
        }

        [TestMethod]
        public void ReadsIntoExistingElementsKeepTheirInstances()
        {
            var values = Ones(100, true);
            byte[] data = NinoSerializer.Serialize(values);
            var existing = NinoDeserializer.Deserialize<FlatOne[]>(data);
            var kept = (FlatOne[])existing.Clone();
            NinoDeserializer.Deserialize(data, ref existing);
            for (int i = 0; i < values.Length; i++)
            {
                Assert.IsTrue(Same(values[i], existing[i]), $"element {i}");
                if (kept[i] != null)
                    Assert.AreSame(kept[i], existing[i], $"element {i} kept");
            }

            // Existing elements that differ from the data: nulls, subtypes and other values in other places. An element
            // of the data's exact type keeps its instance; the rest are what the data says.
            var others = Ones(100, true, 7).Select(v => v ?? new FlatOneDerived()).ToArray();
            others[3] = null;
            var before = (FlatOne[])others.Clone();
            NinoDeserializer.Deserialize(data, ref others);
            for (int i = 0; i < values.Length; i++)
            {
                Assert.IsTrue(Same(values[i], others[i]), $"other element {i}");
                if (values[i] != null && before[i] != null && values[i].GetType() == typeof(FlatOne) &&
                    before[i].GetType() == typeof(FlatOne))
                    Assert.AreSame(before[i], others[i], $"other element {i} kept");
            }

            var manys = Manys(40, true);
            var intoManys = Manys(40, false, 9).ToList();
            var keptManys = intoManys.ToArray();
            NinoDeserializer.Deserialize(NinoSerializer.Serialize(manys), ref intoManys);
            for (int i = 0; i < manys.Length; i++)
            {
                Assert.IsTrue(Same(manys[i], intoManys[i]), $"many {i}");
                if (manys[i] != null)
                    Assert.AreSame(keptManys[i], intoManys[i], $"many {i} kept");
            }

            // An existing object whose strings equal the data's keeps them (no store).
            var many = Manys(1, false)[0];
            var target = NinoDeserializer.Deserialize<FlatMany>(NinoSerializer.Serialize(many));
            string name = target.Name;
            NinoDeserializer.Deserialize(NinoSerializer.Serialize(many), ref target);
            Assert.AreSame(name, target.Name);
        }

        [TestMethod]
        public void TruncatedDataIsRejected()
        {
            byte[] ones = NinoSerializer.Serialize(Ones(50, true));
            byte[] manys = NinoSerializer.Serialize(Manys(50, true));
            for (int cut = 1; cut < 200; cut += 3)
            {
                Assert.ThrowsExactly<NinoDataException>(() =>
                    NinoDeserializer.Deserialize<FlatOne[]>(ones.AsSpan(0, ones.Length - cut)));
                Assert.ThrowsExactly<NinoDataException>(() =>
                    NinoDeserializer.Deserialize<List<FlatMany>>(manys.AsSpan(0, manys.Length - cut)));
            }

            byte[] one = NinoSerializer.Serialize(Manys(1, false)[0]);
            for (int length = 1; length < one.Length; length++)
                Assert.ThrowsExactly<NinoDataException>(() => NinoDeserializer.Deserialize<FlatMany>(one.AsSpan(0, length)));
        }

        [TestMethod]
        public void InvalidBoolAndPresenceAreRejected()
        {
            // Only the bool is 1 after the presence byte: zeros and null strings around it.
            var value = new FlatMany { Flag = true };
            byte[] one = NinoSerializer.Serialize(value);
            int flag = Array.IndexOf(one, (byte)1, 1);
            Assert.AreEqual(-1, Array.IndexOf(one, (byte)1, flag + 1));
            byte[] bad = (byte[])one.Clone();
            bad[flag] = 2;
            Assert.ThrowsExactly<NinoDataException>(() => NinoDeserializer.Deserialize<FlatMany>(bad));
            // A run long enough for the flat kernels (NinoFlatRuns.MinRun), the bad element last.
            int count = NinoFlatRuns.MinRun + 4;
            byte[] run = BitConverter.GetBytes(count).Concat(Enumerable.Repeat(one, count - 1).SelectMany(x => x))
                .Concat(bad).ToArray();
            Assert.ThrowsExactly<NinoDataException>(() => NinoDeserializer.Deserialize<FlatMany[]>(run));
            Assert.ThrowsExactly<NinoDataException>(() => NinoDeserializer.Deserialize<List<FlatMany>>(run));

            byte[] presence = BitConverter.GetBytes(count).Concat(Enumerable.Repeat(one, count - 1).SelectMany(x => x))
                .Concat(new byte[] { 7 }).Concat(one.Skip(1)).ToArray();
            Assert.ThrowsExactly<NinoDataException>(() => NinoDeserializer.Deserialize<FlatMany[]>(presence));
        }

        [TestMethod]
        public void RunsPastTheInlineDepthWriteTheGeneratedBodysBytes()
        {
            // Nodes past NinoDepth.Inline are deferred: their runs go element by element, the ones above through the
            // kernels; both as the generated bodies write them.
            const int depth = 200;
            FlatNode node = null;
            FlatNodeProps twin = null;
            for (int i = 0; i < depth; i++)
            {
                var items = Manys(NinoFlatRuns.MinRun + 2, true, i);
                node = new FlatNode { Items = items, Next = node };
                twin = new FlatNodeProps { Items = items.Select(Props).ToArray(), Next = twin };
            }

            byte[] bytes = NinoSerializer.Serialize(node);
            CollectionAssert.AreEqual(NinoSerializer.Serialize(twin), bytes);
            var back = NinoDeserializer.Deserialize<FlatNode>(bytes);
            for (FlatNode a = node, b = back; a != null; a = a.Next, b = b.Next)
            {
                Assert.AreEqual(a.Items.Length, b.Items.Length);
                for (int i = 0; i < a.Items.Length; i++)
                    Assert.IsTrue(Same(a.Items[i], b.Items[i]));
            }

            Assert.IsTrue(Same(node.Items[3], NinoDeserializer.Deserialize<FlatNodeProps>(bytes).Items[3] is { } p
                ? new FlatMany { Id = p.Id, Name = p.Name, Tag = p.Tag, At = p.At, Flag = p.Flag, Text = p.Text, Price = p.Price }
                : null));
        }
    }
}
