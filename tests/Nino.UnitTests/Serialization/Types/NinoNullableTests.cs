using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nino.Core;

namespace Nino.UnitTests
{
    /// <summary>
    /// <see cref="NinoNullable{T}"/>: the bytes of <c>T?</c> everywhere (single values, arrays, lists, members, the
    /// published layout), either reading the other's data, for the types copied as one block (T as large as its
    /// alignment) and the others (bool, Guid, decimal, a raw struct), and its checks of the data.
    /// </summary>
    [TestClass]
    public class NinoNullableTests
    {
        public enum Tone : short { None, Low = 3, High = 7 }

        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        public struct Cell
        {
            public int Row;
            public long Mark;
        }

        [NinoType(format: NinoFormat.VersionTolerant)]
        public sealed class WithNullable
        {
            public long? Total;
            public Guid? Owner;
            public long?[] Totals;
            public List<int?> Counts;
            public decimal?[] Prices;
        }

        [NinoType(format: NinoFormat.VersionTolerant)]
        public sealed class WithNinoNullable
        {
            public NinoNullable<long> Total;
            public NinoNullable<Guid> Owner;
            public NinoNullable<long>[] Totals;
            public List<NinoNullable<int>> Counts;
            public NinoNullable<decimal>[] Prices;
        }

        // Every fifth null, as the benchmarks; lengths around the vector widths and steps of the block paths.
        private static readonly int[] Lengths = { 0, 1, 2, 3, 7, 8, 9, 16, 31, 33, 64, 129, 1000 };

        private static T? Nth<T>(int i, Func<int, T> make) where T : unmanaged => i % 5 == 0 ? null : make(i);

        private static void SameBytesAsNullable<T>(Func<int, T> make) where T : unmanaged
        {
            foreach (int length in Lengths)
            {
                var nullables = Enumerable.Range(0, length).Select(i => Nth(i, make)).ToArray();
                var ninos = nullables.Select(v => (NinoNullable<T>)v).ToArray();
                string what = $"{typeof(T).Name}[{length}]";

                byte[] bytes = NinoSerializer.Serialize(nullables);
                CollectionAssert.AreEqual(bytes, NinoSerializer.Serialize(ninos), what);
                CollectionAssert.AreEqual(NinoSerializer.Serialize(new List<T?>(nullables)),
                    NinoSerializer.Serialize(new List<NinoNullable<T>>(ninos)), "List " + what);

                // Either reads the other's data.
                CollectionAssert.AreEqual(ninos, NinoDeserializer.Deserialize<NinoNullable<T>[]>(bytes), what);
                CollectionAssert.AreEqual(nullables, NinoDeserializer.Deserialize<T?[]>(NinoSerializer.Serialize(ninos)),
                    what);
                CollectionAssert.AreEqual(ninos, NinoDeserializer.Deserialize<List<NinoNullable<T>>>(bytes), "List " + what);

                // Into existing arrays and lists, longer, shorter or of the same length.
                var into = Enumerable.Repeat((NinoNullable<T>)make(77), length + (length & 1)).ToArray();
                NinoDeserializer.Deserialize(bytes, ref into);
                CollectionAssert.AreEqual(ninos, into, "into " + what);
                var intoList = new List<NinoNullable<T>> { make(5), default };
                NinoDeserializer.Deserialize(bytes, ref intoList);
                CollectionAssert.AreEqual(ninos, intoList, "into List " + what);
            }

            foreach (var single in new[] { (T?)null, make(3) })
            {
                byte[] bytes = NinoSerializer.Serialize(single);
                CollectionAssert.AreEqual(bytes, NinoSerializer.Serialize((NinoNullable<T>)single));
                Assert.AreEqual((NinoNullable<T>)single, NinoDeserializer.Deserialize<NinoNullable<T>>(bytes));
            }
        }

        [TestMethod]
        public void WritesTheBytesOfNullableAndReadsThem()
        {
            // As large as their alignment: block copies.
            SameBytesAsNullable(i => i * 10_000_000_000L - 3);
            SameBytesAsNullable(i => i * 7 + 1);
            SameBytesAsNullable(i => i + 0.5);
            SameBytesAsNullable(i => new DateTime(2024, 1, 1).AddMinutes(i));
            SameBytesAsNullable(i => i % 2 == 0 ? Tone.Low : Tone.High);
            SameBytesAsNullable(i => (byte)(i + 1));
            // Converted slot by slot.
            SameBytesAsNullable(i => i % 3 == 0);
            SameBytesAsNullable(i => new Guid(i + 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11));
            SameBytesAsNullable(i => i + 0.25m);
            SameBytesAsNullable(i => new Cell { Row = i, Mark = -i });
        }

        [TestMethod]
        public void MembersWriteTheBytesOfNullableMembers()
        {
            var nullable = new WithNullable
            {
                Total = -5, Owner = null, Totals = new long?[] { 1, null, 3 }, Counts = new List<int?> { null, 2 },
                Prices = new decimal?[] { 1.5m, null },
            };
            var nino = new WithNinoNullable
            {
                Total = -5, Owner = default, Totals = new NinoNullable<long>[] { 1, default, 3 },
                Counts = new List<NinoNullable<int>> { default, 2 }, Prices = new NinoNullable<decimal>[] { 1.5m, default },
            };
            byte[] bytes = NinoSerializer.Serialize(nullable);
            CollectionAssert.AreEqual(bytes, NinoSerializer.Serialize(nino));
            var read = NinoDeserializer.Deserialize<WithNinoNullable>(bytes);
            Assert.AreEqual(nino.Total, read.Total);
            Assert.IsFalse(read.Owner.HasValue);
            CollectionAssert.AreEqual(nino.Totals, read.Totals);
            CollectionAssert.AreEqual(nino.Counts, read.Counts);
            CollectionAssert.AreEqual(nino.Prices, read.Prices);
        }

        [TestMethod]
        public void PublishedLayoutsSayNullable()
        {
            string Encodings(Type type) => string.Join(";", typeof(NinoNullableTests).Assembly
                .GetCustomAttributes<NinoTypeLayoutAttribute>().Single(a => a.Type == type).Members.Split(';')
                .Select(m => m.Substring(m.IndexOf(':', m.IndexOf(':') + 1) + 1)));

            Assert.AreEqual(Encodings(typeof(WithNullable)), Encodings(typeof(WithNinoNullable)));
        }

        [TestMethod]
        public void BehavesAsNullable()
        {
            NinoNullable<int> none = default;
            NinoNullable<int> seven = 7;
            Assert.IsFalse(none.HasValue);
            Assert.IsTrue(seven.HasValue);
            Assert.AreEqual(7, seven.Value);
            Assert.AreEqual(0, none.GetValueOrDefault());
            Assert.AreEqual(9, none.GetValueOrDefault(9));
            Assert.ThrowsExactly<InvalidOperationException>(() => _ = none.Value);
            Assert.IsNull(none.ToNullable());
            Assert.AreEqual(7, (int?)seven);
            Assert.AreEqual(seven, (NinoNullable<int>)(int?)7);
            Assert.AreEqual(none, (NinoNullable<int>)(int?)null);
            Assert.IsTrue(seven == new NinoNullable<int>(7) && none != seven);
            Assert.IsTrue(seven.Equals((object)7) && none.Equals(null));
            Assert.AreEqual(7.GetHashCode(), seven.GetHashCode());
            Assert.AreEqual("7", seven.ToString());
            Assert.AreEqual("", none.ToString());
            // No padding: its presence is a whole T.
            Assert.AreEqual(16, System.Runtime.CompilerServices.Unsafe.SizeOf<NinoNullable<long>>());
            Assert.AreEqual(8, System.Runtime.CompilerServices.Unsafe.SizeOf<NinoNullable<int>>());
        }

        [TestMethod]
        public void InvalidPresenceBytesAndTruncatedDataThrow()
        {
            var values = new NinoNullable<long>[] { 1, default, 3 };
            byte[] bytes = NinoSerializer.Serialize(values);
            Assert.ThrowsExactly<NinoDataException>(() =>
                NinoDeserializer.Deserialize<NinoNullable<long>[]>(bytes.AsSpan(0, bytes.Length - 1)));
            bytes[4 + 16] = 2; // the second slot's presence byte
            Assert.ThrowsExactly<NinoDataException>(() => NinoDeserializer.Deserialize<NinoNullable<long>[]>(bytes));
            Assert.ThrowsExactly<NinoDataException>(() =>
                NinoDeserializer.Deserialize<List<NinoNullable<long>>>(bytes));

            var guids = NinoSerializer.Serialize(new NinoNullable<Guid>[] { Guid.NewGuid(), default });
            guids[4 + 20] = 5;
            Assert.ThrowsExactly<NinoDataException>(() => NinoDeserializer.Deserialize<NinoNullable<Guid>[]>(guids));
            var bools = NinoSerializer.Serialize(new NinoNullable<bool>[] { true, default });
            bools[4 + 1] = 2; // a bool other than 0 or 1
            Assert.ThrowsExactly<NinoDataException>(() => NinoDeserializer.Deserialize<NinoNullable<bool>[]>(bools));
            Assert.ThrowsExactly<NinoDataException>(() => NinoDeserializer.Deserialize<NinoNullable<long>>(new byte[] { 2 }));
            Assert.ThrowsExactly<NinoDataException>(() => NinoDeserializer.Deserialize<NinoNullable<long>>(new byte[] { 1, 0 }));
        }
    }
}
