using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nino.Core;

namespace Nino.UnitTests
{
    /// <summary>
    /// <see cref="NinoNullable{T}"/>: its memory everywhere (single values, arrays, lists, members, the published
    /// layout), a presence and a value of a T each. Where T is as large as its alignment those are the bytes of a
    /// <c>T?</c> run, either reading the other's data; a wider T (Guid, decimal, a struct) takes two T a value.
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

        // Two words with padding after the first field: three words a slot on the wire, four in memory.
        public struct Pair
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

        /// <summary>The bytes an array is expected to be: its length, then each value's presence and value, a T each.</summary>
        private static byte[] Memory<T>(NinoNullable<T>[] values) where T : unmanaged
        {
            var bytes = new byte[4 + values.Length * 2 * Unsafe.SizeOf<T>()];
            BitConverter.TryWriteBytes(bytes, values.Length);
            MemoryMarshal.AsBytes(values.AsSpan()).CopyTo(bytes.AsSpan(4));
            return bytes;
        }

        /// <summary>
        /// <typeparamref name="T"/> without padding. <paramref name="asNullable"/>: T as large as its alignment, so
        /// a run is the bytes of the <c>T?</c> run.
        /// </summary>
        private static void WritesItsMemory<T>(Func<int, T> make, bool asNullable) where T : unmanaged
        {
            foreach (int length in Lengths)
            {
                var nullables = Enumerable.Range(0, length).Select(i => Nth(i, make)).ToArray();
                var ninos = nullables.Select(v => (NinoNullable<T>)v).ToArray();
                string what = $"{typeof(T).Name}[{length}]";

                byte[] bytes = NinoSerializer.Serialize(ninos);
                CollectionAssert.AreEqual(Memory(ninos), bytes, what);
                CollectionAssert.AreEqual(bytes, NinoSerializer.Serialize(new List<NinoNullable<T>>(ninos)), "List " + what);
                if (asNullable)
                {
                    CollectionAssert.AreEqual(NinoSerializer.Serialize(nullables), bytes, "as T? " + what);
                    CollectionAssert.AreEqual(nullables, NinoDeserializer.Deserialize<T?[]>(bytes), "as T? " + what);
                }

                CollectionAssert.AreEqual(ninos, NinoDeserializer.Deserialize<NinoNullable<T>[]>(bytes), what);
                CollectionAssert.AreEqual(ninos, NinoDeserializer.Deserialize<List<NinoNullable<T>>>(bytes), "List " + what);
                CollectionAssert.AreEqual(ninos, NinoDeserializer.Deserialize<Queue<NinoNullable<T>>>(
                    NinoSerializer.Serialize(new Queue<NinoNullable<T>>(ninos))).ToArray(), "Queue " + what);

                // Into existing arrays and lists, longer, shorter or of the same length.
                var into = Enumerable.Repeat((NinoNullable<T>)make(77), length + (length & 1)).ToArray();
                NinoDeserializer.Deserialize(bytes, ref into);
                CollectionAssert.AreEqual(ninos, into, "into " + what);
                var intoList = new List<NinoNullable<T>> { make(5), default };
                NinoDeserializer.Deserialize(bytes, ref intoList);
                CollectionAssert.AreEqual(ninos, intoList, "into List " + what);
            }

            foreach (NinoNullable<T> single in new[] { (T?)null, make(3) })
            {
                byte[] bytes = NinoSerializer.Serialize(single);
                CollectionAssert.AreEqual(Memory(new[] { single }).AsSpan(4).ToArray(), bytes);
                Assert.AreEqual(single, NinoDeserializer.Deserialize<NinoNullable<T>>(bytes));
            }
        }

        [TestMethod]
        public void WritesItsMemoryAndReadsIt()
        {
            // As large as their alignment: the bytes of T? runs.
            WritesItsMemory(i => i * 10_000_000_000L - 3, true);
            WritesItsMemory(i => i * 7 + 1, true);
            WritesItsMemory(i => i + 0.5, true);
            WritesItsMemory(i => new DateTime(2024, 1, 1).AddMinutes(i), true);
            WritesItsMemory(i => i % 2 == 0 ? Tone.Low : Tone.High, true);
            WritesItsMemory(i => (byte)(i + 1), true);
            WritesItsMemory(i => i % 3 == 0, true);
            // Wider than their alignment: two T a value, where a T? takes T's alignment and a T.
            WritesItsMemory(i => new Guid(i + 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11), false);
            WritesItsMemory(i => i + 0.25m, false);
            WritesItsMemory(i => new Cell { Row = i, Mark = -i }, false);
            Assert.AreEqual(4 + 3 * 32, NinoSerializer.Serialize(new NinoNullable<decimal>[3]).Length);
            Assert.AreEqual(4 + 3 * 24, NinoSerializer.Serialize(new decimal?[3]).Length);
        }

        [TestMethod]
        public void PaddingInsideTheValueIsCleared()
        {
            // Pair has four bytes of padding after Row: the wire clears them, in the presence and in the value.
            var values = Enumerable.Range(0, 9).Select(i => (NinoNullable<Pair>)Nth(i, n => new Pair { Row = n, Mark = -n }))
                .ToArray();
            MemoryMarshal.AsBytes(values.AsSpan()).Slice(16 + 4, 4).Fill(0xAB); // the first value's padding
            byte[] bytes = NinoSerializer.Serialize(values);
            Assert.AreEqual(4 + 9 * 32, bytes.Length);
            CollectionAssert.AreEqual(new byte[4], bytes.AsSpan(4 + 16 + 4, 4).ToArray());
            CollectionAssert.AreEqual(values, NinoDeserializer.Deserialize<NinoNullable<Pair>[]>(bytes));
        }

        [TestMethod]
        public void MembersWriteTheirMemory()
        {
            var nino = new WithNinoNullable
            {
                Total = -5, Owner = default, Totals = new NinoNullable<long>[] { 1, default, 3 },
                Counts = new List<NinoNullable<int>> { default, 2 }, Prices = new NinoNullable<decimal>[] { 1.5m, default },
            };
            var nullable = new WithNullable
            {
                Total = -5, Owner = null, Totals = new long?[] { 1, null, 3 }, Counts = new List<int?> { null, 2 },
                Prices = new decimal?[] { 1.5m, null },
            };
            byte[] bytes = NinoSerializer.Serialize(nino);
            // Total 16 bytes instead of 9, Owner 32 instead of 1, each price 32 instead of 24; the runs of long and
            // int are the same bytes.
            Assert.AreEqual(NinoSerializer.Serialize(nullable).Length + 7 + 31 + 2 * 8, bytes.Length);
            var read = NinoDeserializer.Deserialize<WithNinoNullable>(bytes);
            Assert.AreEqual(nino.Total, read.Total);
            Assert.IsFalse(read.Owner.HasValue);
            CollectionAssert.AreEqual(nino.Totals, read.Totals);
            CollectionAssert.AreEqual(nino.Counts, read.Counts);
            CollectionAssert.AreEqual(nino.Prices, read.Prices);
        }

        [TestMethod]
        public void PublishedLayoutsSayPresenceAndValue()
        {
            string[] Encodings(Type type) => typeof(NinoNullableTests).Assembly
                .GetCustomAttributes<NinoTypeLayoutAttribute>().Single(a => a.Type == type).Members.Split(';')
                .Select(m => m.Substring(m.IndexOf(':', m.IndexOf(':') + 1) + 1)).ToArray();

            var nino = Encodings(typeof(WithNinoNullable));
            var nullable = Encodings(typeof(WithNullable));
            Assert.AreEqual("raw(16){hasValue:bool@0:raw(1),value:long@8:raw(8)}", nino[0]);
            Assert.AreEqual("raw(32){hasValue:bool@0:raw(1),value:System.Guid@16:raw(16)}", nino[1]);
            // Runs of a T as large as its alignment are published as the T? runs they are.
            Assert.AreEqual(nullable[2], nino[2]);
            Assert.AreEqual(nullable[3], nino[3]);
            Assert.AreEqual("seq(raw(32){hasValue:bool@0:raw(1),value:decimal@16:raw(16)})", nino[4]);
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
            Assert.AreEqual(16, Unsafe.SizeOf<NinoNullable<long>>());
            Assert.AreEqual(8, Unsafe.SizeOf<NinoNullable<int>>());
        }

        [TestMethod]
        public void TruncatedDataThrows()
        {
            // Its values are checked in NinoSettings.Untrusted mode, as every raw struct's (UntrustedModeTests).
            byte[] bytes = NinoSerializer.Serialize(new NinoNullable<long>[] { 1, default, 3 });
            Assert.ThrowsExactly<NinoDataException>(() =>
                NinoDeserializer.Deserialize<NinoNullable<long>[]>(bytes.AsSpan(0, bytes.Length - 1)));
            Assert.ThrowsExactly<NinoDataException>(() =>
                NinoDeserializer.Deserialize<List<NinoNullable<long>>>(bytes.AsSpan(0, bytes.Length - 1)));
            Assert.ThrowsExactly<NinoDataException>(() => NinoDeserializer.Deserialize<NinoNullable<long>>(new byte[15]));
        }
    }
}
