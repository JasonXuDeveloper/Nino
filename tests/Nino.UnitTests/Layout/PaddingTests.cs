using System;
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

        // Structs whose T? has every stride the vector paths split by: below a 16-byte vector (3, 6, 10, 12), one
        // vector (16), between 16 and 32 bytes (18, 20, 24, 28), 32 and above in pieces (32, 40, 48, 64, 72), with
        // padding inside the value (the last three) and a flag padded to 16 bytes (Int128).
        public struct Stride3 { public byte A, B; }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct Stride6 { public int A; public byte B; }

        [StructLayout(LayoutKind.Sequential, Pack = 2)]
        public struct Stride10 { public long A; }

        public struct Stride12 { public float X, Y; }

        public struct Stride16 { public int A, B, C; }

        [StructLayout(LayoutKind.Sequential, Pack = 2)]
        public struct Stride18 { public long A, B; }

        public struct Stride20 { public int A, B, C, D; }

        public struct Stride24 { public long A, B; }

        public struct Stride28 { public int A, B, C, D, E, F; }

        public struct Stride32 { public long A, B, C; }

        public struct Stride40 { public long A, B, C, D; }

        public struct Stride48 { public long A, B, C, D, E; }

        public struct Stride64 { public long A, B, C, D, E, F, G; }

        public struct Stride72 { public long A, B, C, D, E, F, G, H; }

        public struct Flag16 { public Int128 A, B; }

        public struct Gap24 { public int A; public long B; }

        public struct Gap32 { public int A; public long B, C; }

        public struct Gap40 { public long A, B, C; public int D; }

        // A value whose padding holds garbage too (its fields assigned over 0xAB bytes).
        private static T DirtyValue<T>(T clean, System.Action<T, System.IntPtr> assign) where T : unmanaged
        {
            T value = default;
            unsafe
            {
                Unsafe.InitBlockUnaligned(&value, 0xAB, (uint)sizeof(T));
                assign(clean, (System.IntPtr)(&value));
            }

            return value;
        }

        // The bytes of a T?[] written slot by slot: the count, then per element the flag, zeros up to T's alignment and
        // T's bytes, or zeros when it has none. T? in memory is that slot on 64-bit little-endian CoreCLR.
        private static byte[] SlotBySlot<T>(T?[] clean) where T : unmanaged
        {
            int size = Unsafe.SizeOf<T>(), stride = Unsafe.SizeOf<T?>();
            var bytes = new byte[4 + clean.Length * stride];
            System.BitConverter.TryWriteBytes(bytes, clean.Length);
            for (int i = 0; i < clean.Length; i++)
            {
                if (!clean[i].HasValue)
                    continue;
                bytes[4 + i * stride] = 1;
                T value = clean[i].Value;
                MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref value, 1))
                    .CopyTo(bytes.AsSpan(4 + i * stride + stride - size));
            }

            return bytes;
        }

        private static void EveryRunIsSlotBySlot<T>(Func<int, T> make, Func<T, T> dirtyValue = null)
            where T : unmanaged
        {
            foreach (int count in RunLengths)
            for (int pattern = 0; pattern < 4; pattern++)
            {
                var clean = new T?[count];
                var dirty = new T?[count];
                for (int i = 0; i < count; i++)
                {
                    bool has = pattern == 0 ? i % 2 == 0 : pattern == 1 ? i % 3 == 2 : pattern == 2 ? i % 7 != 3 : i % 5 == 4;
                    clean[i] = has ? make(i) : null;
                    dirty[i] = DirtyNullable(has && dirtyValue != null ? dirtyValue(make(i)) : clean[i]);
                }

                string what = $"{typeof(T).Name}?[{count}], pattern {pattern}";
                byte[] bytes = NinoSerializer.Serialize(dirty);
                CollectionAssert.AreEqual(NinoSerializer.Serialize(clean), bytes, what);
                CollectionAssert.AreEqual(NinoSerializer.Serialize(new List<T?>(clean)),
                    NinoSerializer.Serialize(new List<T?>(dirty)), "List of " + what);
                if (System.Environment.Is64BitProcess && System.BitConverter.IsLittleEndian && dirtyValue == null)
                    CollectionAssert.AreEqual(SlotBySlot(clean), bytes, "slot by slot, " + what);
                CollectionAssert.AreEqual(clean, NinoDeserializer.Deserialize<T?[]>(bytes), "read back, " + what);
            }
        }

        [TestMethod]
        public void RawNullableRunsOfEveryStrideFromDirtyMemoryWriteTheSlotBySlotBytes()
        {
            EveryRunIsSlotBySlot(i => new Stride3 { A = (byte)(i + 1), B = (byte)(i * 3) });
            EveryRunIsSlotBySlot(i => new Stride6 { A = i * 100_000 + 1, B = (byte)i });
            EveryRunIsSlotBySlot(i => new Stride10 { A = i * 10_000_000_000L + 1 });
            EveryRunIsSlotBySlot(i => new Stride12 { X = i + 0.5f, Y = -i });
            EveryRunIsSlotBySlot(i => new Stride16 { A = i + 1, B = -i, C = i * 77 });
            EveryRunIsSlotBySlot(i => new Stride18 { A = i + 1, B = -i * 10_000_000_000L });
            EveryRunIsSlotBySlot(i => new Stride20 { A = i + 1, B = -i, C = i * 77, D = int.MaxValue - i });
            EveryRunIsSlotBySlot(i => new Stride24 { A = i + 1, B = -i * 10_000_000_000L });
            EveryRunIsSlotBySlot(i => new Stride28 { A = i + 1, B = -i, C = i * 77, D = int.MaxValue - i, E = i, F = ~i });
            EveryRunIsSlotBySlot(i => new Stride32 { A = i + 1, B = -i, C = long.MaxValue - i });
            EveryRunIsSlotBySlot(i => new Stride40 { A = i + 1, B = -i, C = long.MaxValue - i, D = i * 3 });
            EveryRunIsSlotBySlot(i => new Stride48 { A = i + 1, B = -i, C = long.MaxValue - i, D = i * 3, E = ~i });
            EveryRunIsSlotBySlot(i => new Stride64 { A = i + 1, B = -i, C = i * 3, D = ~i, E = i * 5, F = -7, G = i });
            EveryRunIsSlotBySlot(i => new Stride72 { A = i + 1, B = -i, C = i * 3, D = ~i, E = i * 5, F = -7, G = i, H = 9 });
            EveryRunIsSlotBySlot(i => (System.Int128)i * long.MaxValue + 1);
            EveryRunIsSlotBySlot(i => new Flag16 { A = i + 1, B = -i });
            EveryRunIsSlotBySlot(i => new System.Guid(i + 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11));
            EveryRunIsSlotBySlot(i => i + 0.25m);

            // Padding inside the value, holding garbage as well.
            unsafe
            {
                EveryRunIsSlotBySlot(i => new Gap24 { A = i + 1, B = -i }, clean => DirtyValue(clean, (c, p) =>
                {
                    ((Gap24*)p)->A = c.A;
                    ((Gap24*)p)->B = c.B;
                }));
                EveryRunIsSlotBySlot(i => new Gap32 { A = i + 1, B = -i, C = i * 3 }, clean => DirtyValue(clean, (c, p) =>
                {
                    ((Gap32*)p)->A = c.A;
                    ((Gap32*)p)->B = c.B;
                    ((Gap32*)p)->C = c.C;
                }));
                EveryRunIsSlotBySlot(i => new Gap40 { A = i + 1, B = -i, C = i * 3, D = ~i }, clean => DirtyValue(clean, (c, p) =>
                {
                    ((Gap40*)p)->A = c.A;
                    ((Gap40*)p)->B = c.B;
                    ((Gap40*)p)->C = c.C;
                    ((Gap40*)p)->D = c.D;
                }));
            }
        }

        // NinoNullable<T> runs of every shape the vector paths split by (T larger than its alignment: slots of 4, 8
        // and 16 bytes a shuffle packs, 24 and 32 bytes, and longer ones moved in pieces): the bytes of the T? run,
        // from values whose own padding holds garbage too, and read back into clean memory.
        public struct Slot4 { public byte A, B; }

        public struct Slot8 { public short A, B; }

        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        public struct Slot24 { public int A; public long B; }

        private static void EveryNinoNullableRunIsTheNullableRun<T>(Func<int, T> make, Func<T, T> dirtyValue = null)
            where T : unmanaged
        {
            foreach (int count in RunLengths)
            for (int pattern = 0; pattern < 3; pattern++)
            {
                var clean = new T?[count];
                var ninos = new NinoNullable<T>[count];
                var expected = new NinoNullable<T>[count];
                for (int i = 0; i < count; i++)
                {
                    bool has = pattern == 0 ? i % 2 == 0 : pattern == 1 ? i % 3 == 2 : i % 5 != 0;
                    clean[i] = has ? make(i) : null;
                    expected[i] = clean[i];
                    ninos[i] = has ? new NinoNullable<T>(dirtyValue != null ? dirtyValue(make(i)) : make(i)) : default;
                }

                string what = $"NinoNullable<{typeof(T).Name}>[{count}], pattern {pattern}";
                byte[] bytes = NinoSerializer.Serialize(clean);
                CollectionAssert.AreEqual(bytes, NinoSerializer.Serialize(ninos), what);
                CollectionAssert.AreEqual(bytes, NinoSerializer.Serialize(new List<NinoNullable<T>>(ninos)), "List of " + what);

                // Read into new memory and into memory that held other values: the bytes a clean value has.
                var read = NinoDeserializer.Deserialize<NinoNullable<T>[]>(bytes);
                CollectionAssert.AreEqual(MemoryMarshal.AsBytes(expected.AsSpan()).ToArray(),
                    MemoryMarshal.AsBytes(read.AsSpan()).ToArray(), "read, " + what);
                var into = new NinoNullable<T>[count];
                MemoryMarshal.AsBytes(into.AsSpan()).Fill(0xAB);
                NinoDeserializer.Deserialize(bytes, ref into);
                CollectionAssert.AreEqual(MemoryMarshal.AsBytes(expected.AsSpan()).ToArray(),
                    MemoryMarshal.AsBytes(into.AsSpan()).ToArray(), "read into, " + what);
                CollectionAssert.AreEqual(clean, NinoDeserializer.Deserialize<T?[]>(NinoSerializer.Serialize(ninos)), what);
            }
        }

        [TestMethod]
        public void NinoNullableRunsOfEveryShapeWriteTheBytesOfNullableRuns()
        {
            EveryNinoNullableRunIsTheNullableRun(i => new Slot4 { A = (byte)(i + 1), B = (byte)(i * 3) });
            EveryNinoNullableRunIsTheNullableRun(i => new Slot8 { A = (short)(i + 1), B = (short)-i });
            EveryNinoNullableRunIsTheNullableRun(i => new Stride6 { A = i * 100_000 + 1, B = (byte)i });
            EveryNinoNullableRunIsTheNullableRun(i => new Stride12 { X = i + 0.5f, Y = -i });
            EveryNinoNullableRunIsTheNullableRun(i => new Stride16 { A = i + 1, B = -i, C = i * 77 });
            EveryNinoNullableRunIsTheNullableRun(i => new Slot24 { A = i + 1, B = -i * 10_000_000_000L });
            EveryNinoNullableRunIsTheNullableRun(i => new Stride18 { A = i + 1, B = -i * 10_000_000_000L });
            EveryNinoNullableRunIsTheNullableRun(i => new Stride20 { A = i + 1, B = -i, C = i * 77, D = int.MaxValue - i });
            EveryNinoNullableRunIsTheNullableRun(i => new Stride24 { A = i + 1, B = -i * 10_000_000_000L });
            EveryNinoNullableRunIsTheNullableRun(i => new Stride28 { A = i + 1, B = -i, C = i * 77, D = int.MaxValue - i, E = i, F = ~i });
            EveryNinoNullableRunIsTheNullableRun(i => new Stride32 { A = i + 1, B = -i, C = long.MaxValue - i });
            EveryNinoNullableRunIsTheNullableRun(i => new Stride40 { A = i + 1, B = -i, C = long.MaxValue - i, D = i * 3 });
            EveryNinoNullableRunIsTheNullableRun(i => new Stride72 { A = i + 1, B = -i, C = i * 3, D = ~i, E = i * 5, F = -7, G = i, H = 9 });
            EveryNinoNullableRunIsTheNullableRun(i => new Flag16 { A = i + 1, B = -i });
            EveryNinoNullableRunIsTheNullableRun(i => new Guid(i + 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11));
            EveryNinoNullableRunIsTheNullableRun(i => i + 0.25m);
            unsafe
            {
                EveryNinoNullableRunIsTheNullableRun(i => new Gap24 { A = i + 1, B = -i }, clean => DirtyValue(clean, (c, p) =>
                {
                    ((Gap24*)p)->A = c.A;
                    ((Gap24*)p)->B = c.B;
                }));
                EveryNinoNullableRunIsTheNullableRun(i => new Gap32 { A = i + 1, B = -i, C = i * 3 }, clean => DirtyValue(clean, (c, p) =>
                {
                    ((Gap32*)p)->A = c.A;
                    ((Gap32*)p)->B = c.B;
                    ((Gap32*)p)->C = c.C;
                }));
                EveryNinoNullableRunIsTheNullableRun(i => new Gap40 { A = i + 1, B = -i, C = i * 3, D = ~i }, clean => DirtyValue(clean, (c, p) =>
                {
                    ((Gap40*)p)->A = c.A;
                    ((Gap40*)p)->B = c.B;
                    ((Gap40*)p)->C = c.C;
                    ((Gap40*)p)->D = c.D;
                }));
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
