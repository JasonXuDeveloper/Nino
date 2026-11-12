using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nino.Core;
using Nino.Core.Internal;
using Nino.UnitTests.Subset;

namespace Nino.UnitTests
{
    public struct PaddedPlain
    {
        public short A;
        public long B;
    }

    [NinoType(format: NinoFormat.VersionTolerant)]
    public class PaddedHolder
    {
        public SubsetPadded Nino;
        public PaddedPlain Plain;
        public int Tail;
        public SubsetPadded[] Array;
        public List<PaddedPlain> List;
        public Dictionary<int, SubsetPadded> Map;
    }

    [NinoType]
    public class NullableHolder
    {
        public int? Id;
        public long? Score;
        public System.DateTimeOffset? When;
        public long?[] Scores;
        public List<int?> Ids;
    }

    public struct HoldsNullables
    {
        public byte A;
        public long? B;
        public int C;
        public short? D;
    }

    public struct NestsNullables
    {
        public HoldsNullables Inner;
        public System.DateTime? When;
    }

    [StructLayout(LayoutKind.Explicit)]
    public struct ExplicitGap
    {
        [FieldOffset(0)] public int A;
        [FieldOffset(8)] public int B;
    }

    [TestClass]
    public class PaddingTests
    {
        // Builds a value whose padding holds garbage, as uninitialized memory would.
        private static T Dirty<T>(T clean) where T : unmanaged
        {
            T value = default;
            Unsafe.InitBlockUnaligned(ref Unsafe.As<T, byte>(ref value), 0xAB, (uint)Unsafe.SizeOf<T>());
            // Assigning the fields one by one keeps the garbage in the padding.
            ref byte dst = ref Unsafe.As<T, byte>(ref value);
            ref byte src = ref Unsafe.As<T, byte>(ref clean);
            var mask = Mask<T>();
            for (int i = 0; i < mask.Length; i++)
                if (mask[i])
                    Unsafe.Add(ref dst, i) = Unsafe.Add(ref src, i);
            return value;
        }

        private static bool[] Mask<T>()
        {
            if (typeof(T) == typeof(SubsetPadded))
                return new[] { true, false, false, false, true, true, true, true };
            if (typeof(T) == typeof(PaddedPlain))
                return new[] { true, true, false, false, false, false, false, false, true, true, true, true, true, true, true, true };
            return new[] { true, true, true, true, false, false, false, false, true, true, true, true };
        }

        [TestMethod]
        public void PaddingIsCleared()
        {
            var nino = Dirty(new SubsetPadded { A = 1, B = 2 });
            var plain = Dirty(new PaddedPlain { A = 3, B = 4 });
            CollectionAssert.AreEqual(new byte[] { 1, 0, 0, 0, 2, 0, 0, 0 }, NinoSerializer.Serialize(nino));
            CollectionAssert.AreEqual(new byte[] { 3, 0, 0, 0, 0, 0, 0, 0, 4, 0, 0, 0, 0, 0, 0, 0 },
                NinoSerializer.Serialize(plain));
            CollectionAssert.AreEqual(new byte[] { 5, 0, 0, 0, 0, 0, 0, 0, 6, 0, 0, 0 },
                NinoSerializer.Serialize(Dirty(new ExplicitGap { A = 5, B = 6 })));

            var cleanHolder = new PaddedHolder
            {
                Nino = new SubsetPadded { A = 1, B = 2 }, Plain = new PaddedPlain { A = 3, B = 4 }, Tail = 5,
                Array = new[] { new SubsetPadded { A = 7, B = 8 }, new SubsetPadded { A = 9, B = 10 } },
                List = new List<PaddedPlain> { new() { A = 11, B = 12 } },
                Map = new Dictionary<int, SubsetPadded> { { 13, new SubsetPadded { A = 14, B = 15 } } },
            };
            var dirtyHolder = new PaddedHolder
            {
                Nino = nino, Plain = plain, Tail = 5,
                Array = new[] { Dirty(cleanHolder.Array[0]), Dirty(cleanHolder.Array[1]) },
                List = new List<PaddedPlain> { Dirty(cleanHolder.List[0]) },
                Map = new Dictionary<int, SubsetPadded> { { 13, Dirty(cleanHolder.Map[13]) } },
            };
            var bytes = NinoSerializer.Serialize(dirtyHolder);
            CollectionAssert.AreEqual(NinoSerializer.Serialize(cleanHolder), bytes);
            CollectionAssert.DoesNotContain(bytes, (byte)0xAB);

            CollectionAssert.AreEqual(NinoSerializer.Serialize(cleanHolder.Array), NinoSerializer.Serialize(dirtyHolder.Array));
            CollectionAssert.DoesNotContain(NinoSerializer.Serialize(dirtyHolder.Array), (byte)0xAB);
            var output = new ArrayBufferWriter<byte>();
            NinoSerializer.Serialize(dirtyHolder.Array, output);
            CollectionAssert.AreEqual(NinoSerializer.Serialize(cleanHolder.Array), output.WrittenSpan.ToArray());
            output = new ArrayBufferWriter<byte>();
            NinoSerializer.Serialize(dirtyHolder.List, output);
            CollectionAssert.AreEqual(NinoSerializer.Serialize(cleanHolder.List), output.WrittenSpan.ToArray());
            CollectionAssert.DoesNotContain(NinoSerializer.Serialize(dirtyHolder.List), (byte)0xAB);
            CollectionAssert.DoesNotContain(NinoSerializer.Serialize(new SortedList<int, SubsetPadded> { [2] = nino }),
                (byte)0xAB);
            CollectionAssert.DoesNotContain(NinoSerializer.Serialize((SubsetPadded?)nino), (byte)0xAB);
            CollectionAssert.DoesNotContain(NinoSerializer.Serialize(new HashSet<PaddedPlain> { plain }), (byte)0xAB);
        }

        [TestMethod]
        public void MaskedCopyMatchesTheMaskAtEveryLengthAndAlignment()
        {
            var random = new System.Random(7);
            foreach (int size in new[] { 2, 3, 12, 16, 24, 40, 64, 72 })
            {
                var pattern = new byte[size];
                for (int i = 0; i < size; i++)
                    pattern[i] = (byte)(i % 3 == 1 ? 0 : 0xFF);
                var mask = NinoMask.Create(pattern);
                Assert.IsNotNull(mask);
                foreach (int count in new[] { 0, 1, 2, 3, 5, 8, 13, 33, 100 })
                {
                    for (int offset = 0; offset < 8; offset++)
                    {
                        var src = new byte[count * size];
                        random.NextBytes(src);
                        var dst = new byte[offset + src.Length + 8];
                        random.NextBytes(dst);
                        var expected = (byte[])dst.Clone();
                        for (int i = 0; i < src.Length; i++)
                            expected[offset + i] = (byte)(src[i] & pattern[i % size]);
                        if (src.Length != 0)
                            mask.Copy(ref dst[offset], ref src[0], count);
                        CollectionAssert.AreEqual(expected, dst, $"size {size}, count {count}, offset {offset}");

                        // In place: only the padding changes.
                        var inPlace = (byte[])src.Clone();
                        if (inPlace.Length != 0)
                            mask.Clear(ref inPlace[0], count);
                        for (int i = 0; i < src.Length; i++)
                            Assert.AreEqual((byte)(src[i] & pattern[i % size]), inPlace[i]);
                    }
                }
            }
        }

        // A T? whose padding, and whose value when it has none, hold garbage: MemoryPack copies both as they are
        // (its 1.22.0 adds SafeNullable<T> for it); Nino writes a presence byte and the value, or the fixed stride
        // with its padding and an absent value zeroed (spec §6.2, §6.5), so it needs no such type.
        private static T? DirtyNullable<T>(T? clean) where T : unmanaged
        {
            T? value = default;
            ref byte bytes = ref Unsafe.As<T?, byte>(ref value);
            Unsafe.InitBlockUnaligned(ref bytes, 0xAB, (uint)Unsafe.SizeOf<T?>());
            bytes = clean.HasValue ? (byte)1 : (byte)0;
            if (clean.HasValue)
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref bytes, Unsafe.SizeOf<T?>() - Unsafe.SizeOf<T>()), clean.Value);
            return value;
        }

        [TestMethod]
        public void NullablesFromDirtyMemoryWriteTheBytesOfCleanOnes()
        {
            var when = new System.DateTimeOffset(2024, 1, 2, 3, 4, 5, System.TimeSpan.FromMinutes(90));
            var clean = new NullableHolder
            {
                Id = 1, Score = null, When = when, Scores = new long?[] { 2, null, 3 }, Ids = new List<int?> { null, 4 },
            };
            var dirty = new NullableHolder
            {
                Id = DirtyNullable<int>(1), Score = DirtyNullable<long>(null), When = DirtyNullable<System.DateTimeOffset>(when),
                Scores = new[] { DirtyNullable<long>(2), DirtyNullable<long>(null), DirtyNullable<long>(3) },
                Ids = new List<int?> { DirtyNullable<int>(null), DirtyNullable<int>(4) },
            };
            Assert.AreEqual((byte)0xAB, Unsafe.Add(ref Unsafe.As<long?, byte>(ref dirty.Scores[1]), 8), "the setup keeps garbage");

            var bytes = NinoSerializer.Serialize(dirty);
            CollectionAssert.AreEqual(NinoSerializer.Serialize(clean), bytes);
            CollectionAssert.DoesNotContain(bytes, (byte)0xAB);
            CollectionAssert.AreEqual(NinoSerializer.Serialize(clean.Scores), NinoSerializer.Serialize(dirty.Scores));
            CollectionAssert.AreEqual(NinoSerializer.Serialize(clean.Ids), NinoSerializer.Serialize(dirty.Ids));
            CollectionAssert.AreEqual(NinoSerializer.Serialize(clean.Id), NinoSerializer.Serialize(dirty.Id));
            CollectionAssert.AreEqual(NinoSerializer.Serialize(clean.Score), NinoSerializer.Serialize(dirty.Score));
            CollectionAssert.AreEqual(NinoSerializer.Serialize(clean.When), NinoSerializer.Serialize(dirty.When));
        }
        // Every stride (2, 4, 8, 16, 20, 24) and run lengths on both sides of each vector width, of the four-vector
        // steps of 64-byte vectors (130 byte? elements: 260 bytes) and of the three-vector periods of decimal?.
        private static readonly int[] RunLengths =
            Enumerable.Range(0, 41).Concat(new[] { 63, 64, 65, 127, 128, 129, 130, 131, 257 }).ToArray();

        [TestMethod]
        public void RawNullableRunsFromDirtyMemoryWriteZerosForAbsentValues()
        {
            AbsentValuesAreZero<byte>(i => (byte)(i + 1));
            AbsentValuesAreZero<short>(i => (short)(i * 1000 + 1));
            AbsentValuesAreZero<int>(i => i * 100_000 + 1);
            AbsentValuesAreZero<long>(i => i * 10_000_000_000L + 1);
            AbsentValuesAreZero<double>(i => i + 0.5);
            AbsentValuesAreZero<System.DateTime>(i => new System.DateTime(2024, 1, 1).AddDays(i));
            AbsentValuesAreZero<System.Guid>(i => new System.Guid(i + 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11));
            AbsentValuesAreZero<decimal>(i => i + 0.25m);
        }

        private static void AbsentValuesAreZero<T>(System.Func<int, T> make) where T : unmanaged
        {
            foreach (int count in RunLengths)
            for (int pattern = 0; pattern < 3; pattern++)
            {
                var clean = new T?[count];
                var dirty = new T?[count];
                for (int i = 0; i < count; i++)
                {
                    bool has = pattern == 0 ? i % 2 == 0 : pattern == 1 ? i % 3 == 2 : i % 7 != 3;
                    clean[i] = has ? make(i) : null;
                    dirty[i] = DirtyNullable(clean[i]);
                }

                CollectionAssert.AreEqual(NinoSerializer.Serialize(clean), NinoSerializer.Serialize(dirty),
                    $"{typeof(T).Name}?[{count}], pattern {pattern}");
                CollectionAssert.AreEqual(NinoSerializer.Serialize(new List<T?>(clean)),
                    NinoSerializer.Serialize(new List<T?>(dirty)), $"List<{typeof(T).Name}?>({count}), pattern {pattern}");
            }
        }

        // A Nullable field's value when it has none, in a struct written as its bytes: zeros (spec 6.2), whatever
        // the memory held. One value, a run of them, a run of T? of them and a dictionary's raw pairs.
        private static HoldsNullables DirtyHolds(int i, bool hasB, bool hasD)
        {
            HoldsNullables value = default;
            Unsafe.InitBlockUnaligned(ref Unsafe.As<HoldsNullables, byte>(ref value), 0xAB,
                (uint)Unsafe.SizeOf<HoldsNullables>());
            value.A = (byte)i;
            value.C = i * 3;
            if (hasB)
                value.B = i * 1000L;
            else
                Unsafe.As<long?, byte>(ref value.B) = 0;
            if (hasD)
                value.D = (short)-i;
            else
                Unsafe.As<short?, byte>(ref value.D) = 0;
            return value;
        }

        private static HoldsNullables CleanHolds(int i, bool hasB, bool hasD) => new HoldsNullables
        {
            A = (byte)i, C = i * 3, B = hasB ? i * 1000L : null, D = hasD ? (short)-i : null,
        };

        [TestMethod]
        public void StructsHoldingNullablesFromDirtyMemoryWriteZerosForAbsentValues()
        {
            for (int count = 1; count <= 20; count++)
            {
                var clean = new HoldsNullables[count];
                var dirty = new HoldsNullables[count];
                var cleanNests = new NestsNullables[count];
                var dirtyNests = new NestsNullables[count];
                var cleanOptional = new HoldsNullables?[count];
                var dirtyOptional = new HoldsNullables?[count];
                var cleanMap = new Dictionary<int, HoldsNullables>();
                var dirtyMap = new Dictionary<int, HoldsNullables>();
                for (int i = 0; i < count; i++)
                {
                    bool hasB = i % 3 == 1, hasD = i % 2 == 0;
                    clean[i] = CleanHolds(i, hasB, hasD);
                    dirty[i] = DirtyHolds(i, hasB, hasD);
                    cleanNests[i] = new NestsNullables { Inner = clean[i], When = hasD ? null : new System.DateTime(2024, 1, 1) };
                    Unsafe.InitBlockUnaligned(ref Unsafe.As<NestsNullables, byte>(ref dirtyNests[i]), 0xAB,
                        (uint)Unsafe.SizeOf<NestsNullables>());
                    dirtyNests[i].Inner = dirty[i];
                    if (hasD)
                        Unsafe.As<System.DateTime?, byte>(ref dirtyNests[i].When) = 0;
                    else
                        dirtyNests[i].When = cleanNests[i].When;

                    cleanOptional[i] = i % 4 == 3 ? null : clean[i];
                    dirtyOptional[i] = DirtyNullable(cleanOptional[i]);
                    if (cleanOptional[i].HasValue)
                        Unsafe.WriteUnaligned(ref Unsafe.Add(ref Unsafe.As<HoldsNullables?, byte>(ref dirtyOptional[i]),
                            Unsafe.SizeOf<HoldsNullables?>() - Unsafe.SizeOf<HoldsNullables>()), dirty[i]);
                    cleanMap[i] = clean[i];
                    dirtyMap[i] = dirty[i];
                }

                Assert.AreEqual((byte)0xAB, Unsafe.Add(ref Unsafe.As<long?, byte>(ref dirty[0].B), 8), "the setup keeps garbage");
                CollectionAssert.AreEqual(NinoSerializer.Serialize(clean[0]), NinoSerializer.Serialize(dirty[0]), $"one, {count}");
                CollectionAssert.AreEqual(NinoSerializer.Serialize(clean), NinoSerializer.Serialize(dirty), $"array of {count}");
                CollectionAssert.AreEqual(NinoSerializer.Serialize(new List<HoldsNullables>(clean)),
                    NinoSerializer.Serialize(new List<HoldsNullables>(dirty)), $"list of {count}");
                CollectionAssert.AreEqual(NinoSerializer.Serialize(cleanNests), NinoSerializer.Serialize(dirtyNests),
                    $"nested, {count}");
                CollectionAssert.AreEqual(NinoSerializer.Serialize(cleanOptional), NinoSerializer.Serialize(dirtyOptional),
                    $"optional, {count}");
                CollectionAssert.AreEqual(NinoSerializer.Serialize(cleanMap), NinoSerializer.Serialize(dirtyMap),
                    $"dictionary of {count}");
                var back = NinoDeserializer.Deserialize<HoldsNullables[]>(NinoSerializer.Serialize(dirty));
                CollectionAssert.AreEqual(clean, back);
            }
        }
}
}
