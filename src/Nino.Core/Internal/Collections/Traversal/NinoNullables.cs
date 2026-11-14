using System;
using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
#if NET8_0_OR_GREATER
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;
#endif
using Nino.Core.CompilerServices;
using Nino.Core.Internal;

namespace Nino.Core
{
    /// <summary>
    /// <c>T?</c> sequences with unmanaged <c>T</c>, in the canonical (64-bit corefx) <c>Nullable&lt;T&gt;</c> layout:
    /// a presence byte, zero padding up to T's alignment, then T in its canonical layout (zero when null).
    /// Where the runtime lays <c>T?</c> out the same way the elements are copied as one block (padding cleared);
    /// elsewhere (old Mono puts the value first, x86 aligns 8-byte values to 4) they are converted one by one.
    /// </summary>
    internal static class NinoNullables<T> where T : unmanaged
    {
        private static readonly int Offset = NinoLayout<T>.Info.Align > 0 ? NinoLayout<T>.Info.Align : 1;

        /// <summary>Wire size of one element.</summary>
        public static readonly int Stride = Offset + NinoLayout<T>.WireSize;

        /// <summary>Whether <c>T?</c> memory equals its wire layout on this runtime.</summary>
        // Not for bool: its values are read one by one, checked (NinoLayout<T>.CopiesAsIs).
        internal static readonly bool Raw = NinoLayout<T>.CopiesAsIs && NullableLayout<T>.HasValueOffset == 0 &&
                                           NullableLayout<T>.ValueOffset == Offset &&
                                           Unsafe.SizeOf<T?>() == Stride;

        /// <summary>
        /// <c>bool?</c> slots laid out as on the wire: little-endian, the presence byte first and the value after it,
        /// two bytes a slot, so slots copy as they are once their bools are checked (the native slot kernels' gate).
        /// </summary>
        private static readonly bool NativeBoolSlots = BitConverter.IsLittleEndian &&
                                                       NullableLayout<T>.HasValueOffset == 0 &&
                                                       NullableLayout<T>.ValueOffset == 1 &&
                                                       Unsafe.SizeOf<T?>() == 2 && Stride == 2 && Offset == 1;

        private static readonly NinoMask Mask = BuildMask();

        private static NinoMask BuildMask()
        {
            var mask = new byte[Stride];
            mask[0] = 0xFF;
            var value = NinoLayout<T>.Info.Mask;
            for (int i = 0; i < NinoLayout<T>.WireSize; i++)
                mask[Offset + i] = value == null ? (byte)0xFF : value[i];
            // A T holding a Nullable: its own absent values, past the element's flag (the element's own after them).
            var inner = NinoLayout<T>.Info.Absent;
            int[] absent = null;
            if (inner != null)
            {
                absent = new int[inner.Length];
                for (int i = 0; i < inner.Length; i += 3)
                {
                    absent[i] = Offset + inner[i];
                    absent[i + 1] = Offset + inner[i + 1];
                    absent[i + 2] = inner[i + 2];
                }
            }

            return NinoMask.Create(mask, absent);
        }

        /// <summary>
        /// Zeroes the value of each element written without one: memory a <c>T?</c> was never assigned in (a rented or
        /// uninitialized array) may hold old bytes behind a cleared flag, and the wire holds zeros there (spec §6.2).
        /// </summary>
        // Out of line: a pass over the flags after the block copy, for the strides CopyLanes does not take.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ClearAbsent(ref byte destination, int count)
        {
            for (int i = 0; i < count; i++)
            {
                ref byte slot = ref Unsafe.Add(ref destination, i * Stride);
                if (slot == 0)
                    NinoMask.Zero(ref Unsafe.Add(ref slot, Offset), NinoLayout<T>.WireSize);
            }
        }

#if NET8_0_OR_GREATER
        /// <summary>
        /// Strides whose elements sit at fixed places in every vector of a period: 2, 4, 8 and 16 bytes (<c>byte?</c>
        /// to <c>long?</c>, <c>double?</c>, <c>DateTime?</c>) tile a vector, so each element is a lane (or two 8-byte
        /// lanes); 24 bytes (<c>decimal?</c>, a 16-byte struct with 8-byte alignment) repeats every three vectors of
        /// any width, each element three 8-byte lanes with its flag in the first.
        /// </summary>
        private static readonly bool Lanes = Vector128.IsHardwareAccelerated && Mask?.ClearsAbsent != true &&
                                             (Stride == 2 || Stride == 4 || Stride == 8 || Stride == 16 ||
                                              Stride == 24);

        /// <summary>The bytes one step of <see cref="CopyLanes"/> covers with 16-byte vectors (one period).</summary>
        private static readonly int LaneStep = Stride == 24 ? 48 : 16;

        /// <summary>
        /// The flag byte of each element over one vector (strides of 2 to 8 bytes); for 16 and 24 the low byte of each
        /// 8-byte lane, applied after each lane holds its element's first 8 bytes.
        /// </summary>
        private static readonly Vector512<byte> Flags =
            Stride >= 16 ? Vector512.Create(0xFFUL).AsByte() : Repeat(Flag(), 0);

        /// <summary>
        /// The bytes a present element keeps: all but the padding (<see cref="Mask"/>). One vector for strides that tile
        /// it; for 24 three, the period of 192 bytes (its first 96 bytes are the period of 32-byte vectors, its first
        /// 48 that of 16-byte ones).
        /// </summary>
        private static readonly Vector512<byte> Keep = Repeat(Keeps(), 0);

        private static readonly Vector512<byte> Keep1 = Repeat(Keeps(), 64);

        private static readonly Vector512<byte> Keep2 = Repeat(Keeps(), 128);

        /// <summary>
        /// For 24 with AVX-512 VBMI, the vpermb / vpermt2b indices that give each byte of the three vectors of a period
        /// its element's flag: from the first vector, then the first and second, then the second and third.
        /// </summary>
        private static readonly Vector512<byte> FlagBytes0 = FlagBytes(0);

        private static readonly Vector512<byte> FlagBytes1 = FlagBytes(1);

        private static readonly Vector512<byte> FlagBytes2 = FlagBytes(2);

        private static Vector512<byte> FlagBytes(int vector)
        {
            var bytes = new byte[64];
            for (int i = 0; i < bytes.Length; i++)
            {
                int at = 64 * vector + i;
                // The element's first byte (strides of 24), from the start of the first source vector.
                bytes[i] = (byte)(at - at % 24 - 64 * Math.Max(vector - 1, 0));
            }

            return Vector512.Create(bytes);
        }

        private static byte[] Flag()
        {
            var flag = new byte[Stride];
            flag[0] = 0xFF;
            return flag;
        }

        private static byte[] Keeps()
        {
            var keep = new byte[Stride];
            keep.AsSpan().Fill(0xFF);
            if (Mask != null)
                for (int g = 0; g < Mask.Gaps.Length; g += 2)
                    keep.AsSpan(Mask.Gaps[g], Mask.Gaps[g + 1]).Clear();
            return keep;
        }

        /// <summary>The 64 bytes at <paramref name="offset"/> of <paramref name="element"/> repeated.</summary>
        private static Vector512<byte> Repeat(byte[] element, int offset)
        {
            var bytes = new byte[64];
            for (int i = 0; i < bytes.Length; i++)
                bytes[i] = element[(offset + i) % element.Length];
            return Vector512.Create(bytes);
        }

        /// <summary>
        /// Copies <paramref name="total"/> bytes of elements (<see cref="LaneStep"/> or more) as the wire holds them:
        /// padding cleared, and the value of an element without one cleared with it, in one pass. Each element's
        /// flag, compared with zero across its lanes, selects the bytes it keeps (on AVX-512 a mask register, so a
        /// vector costs a test and a masked AND more than the masked copy). Steps start at whole elements; the last one
        /// overlaps the one before it.
        /// </summary>
        // Out of line, as NinoMask.Copy: one call per collection. The widths as NinoMask's: 64-byte vectors where
        // NinoSimd.Prefer512, 32-byte ones, else 16-byte ones (Arm64, Wasm, SSE).
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void CopyLanes(ref byte destination, ref byte source, int total)
        {
            nuint length = (nuint)total;
#if NET9_0_OR_GREATER
            if (AdvSimd.Arm64.IsSupported)
            {
                if (Stride <= 16)
                    CopySpread(ref destination, ref source, length);
                else
                    CopyWords(ref destination, ref source, length);
                return;
            }

            if (AdvSimd.Arm64.IsSupported && length >= (nuint)PartStep)
            {
                CopyParts(ref destination, ref source, length);
                return;
            }
#endif
            if (NinoSimd.Prefer512 && length >= 4 * (nuint)LaneStep)
                CopyLanes<Vector512<byte>, Lanes512>(ref destination, ref source, length);
            else if (Vector256.IsHardwareAccelerated && length >= 2 * (nuint)LaneStep)
                CopyLanes<Vector256<byte>, Lanes256>(ref destination, ref source, length);
            else
                CopyLanes<Vector128<byte>, Lanes128>(ref destination, ref source, length);
        }

#if NET9_0_OR_GREATER
        /// <summary>
        /// The width of the parts an Arm64 structure load (ld2, ld3) splits each element into: half the stride, or
        /// 8 bytes for 24 (three parts). The flag is the low byte of the first part.
        /// </summary>
        private static readonly int PartWidth = Stride == 24 ? 8 : Stride / 2;

        /// <summary>The bytes of one structure load: two 16-byte parts, three for 24.</summary>
        private static readonly int PartStep = Stride == 24 ? 48 : 32;

        /// <summary>The low byte of each lane of <see cref="PartWidth"/>: the flag in the first part.</summary>
        private static readonly Vector128<byte> PartFlag = Part(Flag(), 0);

        /// <summary>The bytes each part keeps of a present element (<see cref="Keeps"/>), lane by lane.</summary>
        private static readonly Vector128<byte> PartKeep0 = Part(Keeps(), 0);

        private static readonly Vector128<byte> PartKeep1 = Part(Keeps(), 1);

        private static readonly Vector128<byte> PartKeep2 = Stride == 24 ? Part(Keeps(), 2) : default;

        /// <summary>
        /// Whether the first part keeps only the flag (its padding clears the rest), which is zero in an absent element,
        /// and whether the other parts keep every byte: the ANDs these make redundant are left out.
        /// </summary>
        private static readonly bool FlagPartOnly = PartKeep0 == PartFlag;

        private static readonly bool WholePart1 = PartKeep1 == Vector128<byte>.AllBitsSet;

        private static readonly bool WholePart2 = PartKeep2 == Vector128<byte>.AllBitsSet;

        private static Vector128<byte> Part(byte[] element, int part)
        {
            var bytes = new byte[16];
            for (int i = 0; i < bytes.Length; i++)
                bytes[i] = element[part * PartWidth + i % PartWidth];
            return Vector128.Create(bytes);
        }

        /// <summary>
        /// For strides of 2 to 16 bytes, the table lookup (tbl) that gives each byte of a 16-byte vector its element's
        /// flag where a present element keeps it, and zero (an index out of the table) in the padding.
        /// </summary>
        private static readonly Vector128<byte> SpreadFlags = Spread();

        private static Vector128<byte> Spread()
        {
            var bytes = new byte[16];
            if (Stride <= 16)
            {
                var keep = Keeps();
                for (int i = 0; i < bytes.Length; i++)
                    bytes[i] = keep[i % Stride] != 0 ? (byte)(i - i % Stride) : (byte)0xFF;
            }

            return Vector128.Create(bytes);
        }

        /// <summary>
        /// <see cref="CopyLanes"/> on Arm64 for strides of 2 to 16 bytes, which tile a 16-byte vector: each byte takes
        /// its element's flag (tbl), the flags tested against themselves (cmtst) give the bytes to keep, and one AND
        /// keeps them; vectors loaded and stored in pairs (ldp, stp). Three vector operations a vector, where the
        /// structure loads and stores of <see cref="CopyParts"/> (ld2, st2) took the vector pipes too. The last vector
        /// overlaps the one before it.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void CopySpread(ref byte destination, ref byte source, nuint total)
        {
            // Cursors bumped, so the two vectors of a step are at constant offsets (ldp, stp).
            var spread = SpreadFlags;
            nuint left = total;
            for (; left >= 32; left -= 32)
            {
                var a = Vector128.LoadUnsafe(ref source);
                var b = Vector128.LoadUnsafe(ref source, 16);
                Spread(a, spread).StoreUnsafe(ref destination);
                Spread(b, spread).StoreUnsafe(ref destination, 16);
                source = ref Unsafe.Add(ref source, 32);
                destination = ref Unsafe.Add(ref destination, 32);
            }

            if (left >= 16)
            {
                Spread(Vector128.LoadUnsafe(ref source), spread).StoreUnsafe(ref destination);
                source = ref Unsafe.Add(ref source, 16);
                destination = ref Unsafe.Add(ref destination, 16);
                left -= 16;
            }

            if (left != 0)
                Spread(Vector128.LoadUnsafe(ref source, left - 16), spread).StoreUnsafe(ref destination, left - 16);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector128<byte> Spread(Vector128<byte> value, Vector128<byte> spread)
        {
            var flags = AdvSimd.Arm64.VectorTableLookup(value, spread);
            return value & AdvSimd.CompareTest(flags, flags);
        }

        /// <summary>The bytes the first, second and third 8-byte words of a present element keep.</summary>
        private static readonly ulong KeepWord0 = Stride >= 8 ? Word(Keeps(), 0) : 0;

        private static readonly ulong KeepWord1 = Stride >= 16 ? Word(Keeps(), 8) : 0;

        private static readonly ulong KeepWord2 = Stride == 24 ? Word(Keeps(), 16) : 0;

        /// <summary>Whether an element's first word keeps only its flag, which is zero where it has no value.</summary>
        private static readonly bool FlagWordOnly = KeepWord0 == 0xFF;

        private static ulong Word(byte[] keep, int offset) => BitConverter.ToUInt64(keep, offset);

        /// <summary>
        /// <see cref="CopyLanes"/> on Arm64 for a stride of 24 bytes (or 8 and 16), a word at a time in general-purpose
        /// registers: each element's flag tested once, each word kept (ANDed with its bytes to keep) or zeroed by a
        /// conditional select, loaded and stored in pairs (ldp, stp), where the structure loads and stores of
        /// <see cref="CopyParts"/> (ld2, st2) take the vector pipes.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void CopyWords(ref byte destination, ref byte source, nuint total)
        {
            // Cursors bumped by the stride, so each element's words are at constant offsets, read before any is written
            // (ldp, stp); the presence a mask, all ones for a flag other than 0 (its negation shifted right
            // arithmetically), ANDed into each word: a ternary compiled to a branch per element.
            ref byte end = ref Unsafe.Add(ref source, total);
            while (Unsafe.IsAddressLessThan(ref source, ref end))
            {
                ulong w0 = Unsafe.ReadUnaligned<ulong>(ref source);
                ulong w1 = Stride >= 16 ? Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, 8)) : 0;
                ulong w2 = Stride == 24 ? Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, 16)) : 0;
                ulong present = (ulong)(-(long)(byte)w0 >> 63);
                Unsafe.WriteUnaligned(ref destination, FlagWordOnly ? w0 & 0xFF : w0 & KeepWord0 & present);
                if (Stride >= 16)
                    Unsafe.WriteUnaligned(ref Unsafe.Add(ref destination, 8), w1 & KeepWord1 & present);
                if (Stride == 24)
                    Unsafe.WriteUnaligned(ref Unsafe.Add(ref destination, 16), w2 & KeepWord2 & present);
                source = ref Unsafe.Add(ref source, Stride);
                destination = ref Unsafe.Add(ref destination, Stride);
            }
        }

        /// <summary>
        /// <see cref="CopyLanes"/> on Arm64: 32 bytes a step (48 for 24) loaded as structures, so each register holds
        /// one part of every element (the flags in the first) and the flag test (cmtst) is one per step with no
        /// shuffle; stored zipped back (st2, st3). <paramref name="total"/> is at least one step.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static unsafe void CopyParts(ref byte destination, ref byte source, nuint total)
        {
            nuint step = (nuint)PartStep;
            nuint end = total - step;
            var flag = PartFlag;
            var keep0 = PartKeep0;
            var keep1 = PartKeep1;
            var keep2 = PartKeep2;
            fixed (byte* d = &destination, s = &source)
            {
                for (nuint i = 0; i < end; i += step)
                    CopyPart(d + i, s + i, flag, keep0, keep1, keep2);
                CopyPart(d + end, s + end, flag, keep0, keep1, keep2);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static unsafe void CopyPart(byte* destination, byte* source, Vector128<byte> flag,
            Vector128<byte> keep0, Vector128<byte> keep1, Vector128<byte> keep2)
        {
            if (Stride == 2)
            {
                var (a, b) = AdvSimd.Arm64.Load2xVector128AndUnzip(source);
                var present = AdvSimd.CompareTest(a, flag);
                AdvSimd.Arm64.StoreVectorAndZip(destination,
                    (First(a, keep0, present), Rest(b, keep1, present, WholePart1)));
            }
            else if (Stride == 4)
            {
                var (a, b) = AdvSimd.Arm64.Load2xVector128AndUnzip((ushort*)source);
                var present = AdvSimd.CompareTest(a, flag.AsUInt16()).AsByte();
                AdvSimd.Arm64.StoreVectorAndZip((ushort*)destination, (First(a.AsByte(), keep0, present).AsUInt16(),
                    Rest(b.AsByte(), keep1, present, WholePart1).AsUInt16()));
            }
            else if (Stride == 8)
            {
                var (a, b) = AdvSimd.Arm64.Load2xVector128AndUnzip((uint*)source);
                var present = AdvSimd.CompareTest(a, flag.AsUInt32()).AsByte();
                AdvSimd.Arm64.StoreVectorAndZip((uint*)destination, (First(a.AsByte(), keep0, present).AsUInt32(),
                    Rest(b.AsByte(), keep1, present, WholePart1).AsUInt32()));
            }
            else if (Stride == 16)
            {
                var (a, b) = AdvSimd.Arm64.Load2xVector128AndUnzip((ulong*)source);
                var present = AdvSimd.Arm64.CompareTest(a, flag.AsUInt64()).AsByte();
                AdvSimd.Arm64.StoreVectorAndZip((ulong*)destination, (First(a.AsByte(), keep0, present).AsUInt64(),
                    Rest(b.AsByte(), keep1, present, WholePart1).AsUInt64()));
            }
            else
            {
                var (a, b, c) = AdvSimd.Arm64.Load3xVector128AndUnzip((ulong*)source);
                var present = AdvSimd.Arm64.CompareTest(a, flag.AsUInt64()).AsByte();
                AdvSimd.Arm64.StoreVectorAndZip((ulong*)destination, (First(a.AsByte(), keep0, present).AsUInt64(),
                    Rest(b.AsByte(), keep1, present, WholePart1).AsUInt64(),
                    Rest(c.AsByte(), keep2, present, WholePart2).AsUInt64()));
            }
        }

        /// <summary>The first part of the elements: their flags, and what else it holds where present.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector128<byte> First(Vector128<byte> part, Vector128<byte> keep, Vector128<byte> present) =>
            FlagPartOnly ? part & keep : part & keep & present;

        /// <summary>Another part of the elements: kept where present.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector128<byte> Rest(Vector128<byte> part, Vector128<byte> keep, Vector128<byte> present,
            bool whole) => whole ? part & present : part & keep & present;
#endif

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void CopyLanes<TVector, TLanes>(ref byte destination, ref byte source, nuint total)
            where TVector : struct
            where TLanes : struct, ILanes
        {
            var lanes = default(TLanes);
            nuint step = (nuint)(LaneStep * Unsafe.SizeOf<TVector>() / 16);
            nuint end = total - step;
            nuint i = 0;
            if (Stride != 24)
                for (; i + 3 * step < end; i += 4 * step)
                    lanes.CopyFour(ref destination, ref source, i);
            for (; i < end; i += step)
                lanes.Copy(ref destination, ref source, i);
            lanes.Copy(ref destination, ref source, end);
        }

        /// <summary>One width of <see cref="CopyLanes"/>: a step (a vector, or the three of a 24-byte period) at a time.</summary>
        private interface ILanes
        {
            void Copy(ref byte destination, ref byte source, nuint offset);

            /// <summary>Four vectors of a stride that tiles one, all loads before the stores.</summary>
            void CopyFour(ref byte destination, ref byte source, nuint offset);
        }

        private readonly struct Lanes512 : ILanes
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void Copy(ref byte destination, ref byte source, nuint offset)
            {
                var a = Vector512.LoadUnsafe(ref source, offset);
                if (Stride != 24)
                {
                    Present(a, Tile(a), Keep).StoreUnsafe(ref destination, offset);
                    return;
                }

                // Each 8-byte lane takes the first 8 bytes of its element: elements start every third lane, the one a
                // vector starts in up to two lanes before it (in the vector before, within the period).
                var b = Vector512.LoadUnsafe(ref source, offset + 64);
                var c = Vector512.LoadUnsafe(ref source, offset + 128);
                if (Avx512Vbmi.IsSupported)
                {
                    // Every byte of each 8-byte lane takes its element's flag (vpermb, vpermt2b), so the lanes are
                    // tested as they are: one op fewer than moving the lanes and isolating the flag.
                    Present(a, Avx512Vbmi.PermuteVar64x8(a, FlagBytes0), Keep, true)
                        .StoreUnsafe(ref destination, offset);
                    Present(b, Avx512Vbmi.PermuteVar64x8x2(a, FlagBytes1, b), Keep1, true)
                        .StoreUnsafe(ref destination, offset + 64);
                    Present(c, Avx512Vbmi.PermuteVar64x8x2(b, FlagBytes2, c), Keep2, true)
                        .StoreUnsafe(ref destination, offset + 128);
                    return;
                }

                var a8 = a.AsUInt64();
                var b8 = b.AsUInt64();
                Present(a, Avx512F.PermuteVar8x64(a8, Vector512.Create(0UL, 0, 0, 3, 3, 3, 6, 6)).AsByte(), Keep)
                    .StoreUnsafe(ref destination, offset);
                Present(b, Avx512F.PermuteVar8x64x2(a8, Vector512.Create(6UL, 9, 9, 9, 12, 12, 12, 15), b8).AsByte(),
                    Keep1).StoreUnsafe(ref destination, offset + 64);
                Present(c, Avx512F.PermuteVar8x64x2(b8, Vector512.Create(7UL, 7, 10, 10, 10, 13, 13, 13),
                    c.AsUInt64()).AsByte(), Keep2).StoreUnsafe(ref destination, offset + 128);
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void CopyFour(ref byte destination, ref byte source, nuint offset)
            {
                var a = Vector512.LoadUnsafe(ref source, offset);
                var b = Vector512.LoadUnsafe(ref source, offset + 64);
                var c = Vector512.LoadUnsafe(ref source, offset + 128);
                var d = Vector512.LoadUnsafe(ref source, offset + 192);
                Present(a, Tile(a), Keep).StoreUnsafe(ref destination, offset);
                Present(b, Tile(b), Keep).StoreUnsafe(ref destination, offset + 64);
                Present(c, Tile(c), Keep).StoreUnsafe(ref destination, offset + 128);
                Present(d, Tile(d), Keep).StoreUnsafe(ref destination, offset + 192);
            }

            /// <summary>
            /// For a 16-byte stride, every byte of each element its flag (vpshufb with index 0 in each 16-byte lane),
            /// tested as it is.
            /// </summary>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private static Vector512<byte> Tile(Vector512<byte> value) =>
                Stride != 16 ? value : Avx512BW.Shuffle(value, Vector512<byte>.Zero);

            /// <summary>
            /// <paramref name="value"/> & <paramref name="keep"/> in the lanes whose <paramref name="flags"/> (the
            /// element's flag at <see cref="Flags"/>, or in every byte when <paramref name="spread"/>; for 16 always)
            /// are not zero, zero in the others: a test into a mask register and a masked AND (vptestm, vpand {k}{z}).
            /// </summary>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private static Vector512<byte> Present(Vector512<byte> value, Vector512<byte> flags, Vector512<byte> keep,
                bool spread = false)
            {
                var kept = value & keep;
                if (!spread && Stride != 16)
                    flags &= Flags;
                return Stride == 2
                    ? Vector512.ConditionalSelect(~Vector512.Equals(flags.AsUInt16(), Vector512<ushort>.Zero),
                        kept.AsUInt16(), Vector512<ushort>.Zero).AsByte()
                    : Stride == 4
                        ? Vector512.ConditionalSelect(~Vector512.Equals(flags.AsUInt32(), Vector512<uint>.Zero),
                            kept.AsUInt32(), Vector512<uint>.Zero).AsByte()
                        : Vector512.ConditionalSelect(~Vector512.Equals(flags.AsUInt64(), Vector512<ulong>.Zero),
                            kept.AsUInt64(), Vector512<ulong>.Zero).AsByte();
            }
        }

        private readonly struct Lanes256 : ILanes
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void Copy(ref byte destination, ref byte source, nuint offset)
            {
                var a = Vector256.LoadUnsafe(ref source, offset);
                if (Stride != 24)
                {
                    Present(a, Tile(a), Keep.GetLower()).StoreUnsafe(ref destination, offset);
                    return;
                }

                // As Lanes512's: elements start at lanes 0, 3, 6 and 9 of the three vectors (lanes 4 and 5 take lane
                // 3's, lane 8 lane 6's).
                var b = Vector256.LoadUnsafe(ref source, offset + 32);
                var c = Vector256.LoadUnsafe(ref source, offset + 64);
                var a8 = a.AsUInt64();
                var b8 = b.AsUInt64();
                var c8 = c.AsUInt64();
                Vector256<ulong> fb, fc;
                if (Avx512F.VL.IsSupported)
                {
                    fb = Avx512F.VL.PermuteVar4x64x2(a8, Vector256.Create(3UL, 3, 6, 6), b8);
                    fc = Avx512F.VL.PermuteVar4x64x2(b8, Vector256.Create(2UL, 5, 5, 5), c8);
                }
                else
                {
                    var b2 = Vector256.Shuffle(b8, Vector256.Create(2UL, 2, 2, 2));
                    fb = Vector256.ConditionalSelect(Vector256.Create(ulong.MaxValue, ulong.MaxValue, 0, 0),
                        Vector256.Shuffle(a8, Vector256.Create(3UL, 3, 3, 3)), b2);
                    fc = Vector256.ConditionalSelect(Vector256.Create(ulong.MaxValue, 0, 0, 0), b2,
                        Vector256.Shuffle(c8, Vector256.Create(1UL, 1, 1, 1)));
                }

                Present(a, Vector256.Shuffle(a8, Vector256.Create(0UL, 0, 0, 3)).AsByte(), Keep.GetLower())
                    .StoreUnsafe(ref destination, offset);
                Present(b, fb.AsByte(), Keep.GetUpper()).StoreUnsafe(ref destination, offset + 32);
                Present(c, fc.AsByte(), Keep1.GetLower()).StoreUnsafe(ref destination, offset + 64);
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void CopyFour(ref byte destination, ref byte source, nuint offset)
            {
                var keep = Keep.GetLower();
                var a = Vector256.LoadUnsafe(ref source, offset);
                var b = Vector256.LoadUnsafe(ref source, offset + 32);
                var c = Vector256.LoadUnsafe(ref source, offset + 64);
                var d = Vector256.LoadUnsafe(ref source, offset + 96);
                Present(a, Tile(a), keep).StoreUnsafe(ref destination, offset);
                Present(b, Tile(b), keep).StoreUnsafe(ref destination, offset + 32);
                Present(c, Tile(c), keep).StoreUnsafe(ref destination, offset + 64);
                Present(d, Tile(d), keep).StoreUnsafe(ref destination, offset + 96);
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private static Vector256<byte> Tile(Vector256<byte> value) =>
                Stride != 16 ? value : Avx2.Shuffle(value, Vector256<byte>.Zero);

            /// <summary>As Lanes512's: a mask register with AVX-512 (VL), else a compare and an AND NOT.</summary>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private static Vector256<byte> Present(Vector256<byte> value, Vector256<byte> flags, Vector256<byte> keep)
            {
                var kept = value & keep;
                if (Stride != 16)
                    flags &= Flags.GetLower();
                if (Avx512F.VL.IsSupported)
                    return Stride == 2
                        ? Vector256.ConditionalSelect(~Vector256.Equals(flags.AsUInt16(), Vector256<ushort>.Zero),
                            kept.AsUInt16(), Vector256<ushort>.Zero).AsByte()
                        : Stride == 4
                            ? Vector256.ConditionalSelect(~Vector256.Equals(flags.AsUInt32(), Vector256<uint>.Zero),
                                kept.AsUInt32(), Vector256<uint>.Zero).AsByte()
                            : Vector256.ConditionalSelect(~Vector256.Equals(flags.AsUInt64(), Vector256<ulong>.Zero),
                                kept.AsUInt64(), Vector256<ulong>.Zero).AsByte();
                var absent = Stride == 2 ? Vector256.Equals(flags.AsUInt16(), Vector256<ushort>.Zero).AsByte()
                    : Stride == 4 ? Vector256.Equals(flags.AsUInt32(), Vector256<uint>.Zero).AsByte()
                    : Vector256.Equals(flags.AsUInt64(), Vector256<ulong>.Zero).AsByte();
                return Vector256.AndNot(kept, absent);
            }
        }

        private readonly struct Lanes128 : ILanes
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void Copy(ref byte destination, ref byte source, nuint offset)
            {
                var keep = Keep.GetLower().GetLower();
                var a = Vector128.LoadUnsafe(ref source, offset);
                if (Stride != 24)
                {
                    (a & Present(Tile(a)) & keep).StoreUnsafe(ref destination, offset);
                    return;
                }

                // Two elements: the first in lanes 0 to 2, the second in lanes 3 to 5 of the three vectors.
                var b = Vector128.LoadUnsafe(ref source, offset + 16);
                var c = Vector128.LoadUnsafe(ref source, offset + 32);
                var first = Present(a).AsUInt64();
                var second = Present(b).AsUInt64();
                (a & Vector128.Shuffle(first, Vector128.Create(0UL, 0)).AsByte() & keep)
                    .StoreUnsafe(ref destination, offset);
                (b & Vector128.ConditionalSelect(Vector128.Create(0, ulong.MaxValue), second, first).AsByte() &
                 Keep.GetLower().GetUpper()).StoreUnsafe(ref destination, offset + 16);
                (c & Vector128.Shuffle(second, Vector128.Create(1UL, 1)).AsByte() & Keep.GetUpper().GetLower())
                    .StoreUnsafe(ref destination, offset + 32);
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void CopyFour(ref byte destination, ref byte source, nuint offset)
            {
                var keep = Keep.GetLower().GetLower();
                var a = Vector128.LoadUnsafe(ref source, offset);
                var b = Vector128.LoadUnsafe(ref source, offset + 16);
                var c = Vector128.LoadUnsafe(ref source, offset + 32);
                var d = Vector128.LoadUnsafe(ref source, offset + 48);
                (a & Present(Tile(a)) & keep).StoreUnsafe(ref destination, offset);
                (b & Present(Tile(b)) & keep).StoreUnsafe(ref destination, offset + 16);
                (c & Present(Tile(c)) & keep).StoreUnsafe(ref destination, offset + 32);
                (d & Present(Tile(d)) & keep).StoreUnsafe(ref destination, offset + 48);
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private static Vector128<byte> Tile(Vector128<byte> value) => Stride != 16
                ? value
                : Vector128.Shuffle(value.AsUInt64(), Vector128.Create(0UL, 0)).AsByte();

            /// <summary>
            /// All ones in the lanes (of the stride's width; 8 bytes for 16 and 24) whose <see cref="Flags"/> bytes are
            /// not zero: one cmtst on Arm64.
            /// </summary>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private static Vector128<byte> Present(Vector128<byte> value)
            {
                var flags = Flags.GetLower().GetLower();
                if (AdvSimd.Arm64.IsSupported)
                    return Stride == 2 ? AdvSimd.CompareTest(value.AsUInt16(), flags.AsUInt16()).AsByte()
                        : Stride == 4 ? AdvSimd.CompareTest(value.AsUInt32(), flags.AsUInt32()).AsByte()
                        : AdvSimd.Arm64.CompareTest(value.AsUInt64(), flags.AsUInt64()).AsByte();
                value &= flags;
                return Stride == 2 ? ~Vector128.Equals(value.AsUInt16(), Vector128<ushort>.Zero).AsByte()
                    : Stride == 4 ? ~Vector128.Equals(value.AsUInt32(), Vector128<uint>.Zero).AsByte()
                    : ~Vector128.Equals(value.AsUInt64(), Vector128<ulong>.Zero).AsByte();
            }
        }
#endif

        /// <summary>Writes the count and the elements.</summary>
        public static void Write(ReadOnlySpan<T?> values, ref Writer writer)
        {
            ref byte dst = ref writer.ReserveCounted(values.Length, Stride);
            if (values.IsEmpty)
                return;
            ref T? item = ref MemoryMarshal.GetReference(values);
            if (Raw)
            {
                ref byte src = ref Unsafe.As<T?, byte>(ref item);
#if NET8_0_OR_GREATER
                if (Lanes && values.Length * Stride >= LaneStep)
                {
                    CopyLanes(ref dst, ref src, values.Length * Stride);
                    return;
                }
#endif
                if (Mask != null)
                    Mask.Copy(ref dst, ref src, values.Length);
                else
                    NinoUnsafe.Copy(ref dst, ref src, (uint)(values.Length * Stride));
                ClearAbsent(ref dst, values.Length);
                return;
            }

            if (typeof(T) == typeof(bool) && values.Length < 4 && BitConverter.IsLittleEndian && Stride == 2 && Offset == 1)
            {
                WriteBoolValues(ref item, ref dst, values.Length);
                return;
            }

#if NET8_0_OR_GREATER
            if (typeof(T) == typeof(bool) && values.Length >= 4 && NativeBoolSlots)
            {
                ref ushort source = ref Unsafe.As<T?, ushort>(ref item);
                ref ushort target = ref Unsafe.As<byte, ushort>(ref dst);
                if (NinoSimd.Prefer512 && values.Length >= Vector512<ushort>.Count)
                    WriteBoolSlots<Vector512<ushort>, BoolSlots512>(ref source, ref target, values.Length);
                else if (Vector256.IsHardwareAccelerated && values.Length >= Vector256<ushort>.Count)
                    WriteBoolSlots<Vector256<ushort>, BoolSlots256>(ref source, ref target, values.Length);
                else if (Vector128.IsHardwareAccelerated && values.Length >= Vector128<ushort>.Count)
                    WriteBoolSlots<Vector128<ushort>, BoolSlots128>(ref source, ref target, values.Length);
                else if (NinoSimd.HasArm64)
                    WriteBoolSlots<Vector64<ushort>, BoolSlots64>(ref source, ref target, values.Length);
                else
                    WriteBoolSlots<ulong, BoolSlotsWord>(ref source, ref target, values.Length);

                return;
            }

#endif

#if !NET8_0_OR_GREATER
            if (typeof(T) == typeof(bool) && values.Length >= NinoKernelHook.MinWriteBoolSlots &&
                TryWriteNativeBoolSlots(ref item, ref dst, values.Length))
                return;
#endif
            Unsafe.InitBlockUnaligned(ref dst, 0, (uint)(values.Length * Stride));
            for (int i = 0; i < values.Length; i++)
            {
                ref T? element = ref Unsafe.Add(ref item, i);
                if (!element.HasValue)
                    continue;
                ref byte slot = ref Unsafe.Add(ref dst, i * Stride);
                slot = NinoWire.NotNull;
                NinoLayout<T>.Write(ref Unsafe.Add(ref slot, Offset), element.GetValueOrDefault());
            }
        }

        /// <summary>
        /// Whether a run of <see cref="NinoNullable{T}"/> is its wire as it lies in memory: T as large as its alignment
        /// (its presence a whole T, so the slot's padding is the presence's upper bytes) and the slots copied as they
        /// are (<see cref="Raw"/>). Its memory is then the memory a <c>T?</c> run read as raw holds, byte for byte.
        /// </summary>
        internal static readonly bool Words = Raw && Unsafe.SizeOf<NinoNullable<T>>() == Stride &&
                                             Unsafe.SizeOf<T?>() == Stride;

        /// <summary>
        /// Writes the count and the elements of a <see cref="NinoNullable{T}"/> run, the bytes of a <c>T?</c> run: as
        /// one block copy where <see cref="Words"/> (the type keeps its memory clean), else slot by slot.
        /// </summary>
        public static void Write(ReadOnlySpan<NinoNullable<T>> values, ref Writer writer)
        {
            ref byte dst = ref writer.ReserveCounted(values.Length, Stride);
            if (values.IsEmpty)
                return;
            ref NinoNullable<T> item = ref MemoryMarshal.GetReference(values);
            if (Words)
            {
                NinoUnsafe.Copy(ref dst, ref Unsafe.As<NinoNullable<T>, byte>(ref item), (uint)(values.Length * Stride));
                return;
            }

            Unsafe.InitBlockUnaligned(ref dst, 0, (uint)(values.Length * Stride));
            for (int i = 0; i < values.Length; i++)
            {
                ref NinoNullable<T> element = ref Unsafe.Add(ref item, i);
                if (!element.HasValue)
                    continue;
                ref byte slot = ref Unsafe.Add(ref dst, i * Stride);
                slot = NinoWire.NotNull;
                NinoLayout<T>.Write(ref Unsafe.Add(ref slot, Offset), element.GetValueOrDefault());
            }
        }

        /// <summary>
        /// Reads a <see cref="NinoNullable{T}"/> run whose count was checked: as a <c>T?</c> run (presence bytes
        /// checked, then one block copy) where <see cref="Words"/>, else slot by slot into values this type makes, which
        /// are clean whatever the data's padding held (also in <see cref="NinoSettings.Untrusted"/> mode, which then
        /// never keeps the data's padding).
        /// </summary>
        public static void Read(Span<NinoNullable<T>> destination, ref Reader reader)
        {
            // The same memory as T?, so the T? read checks the presence bytes and copies the slots.
            if (Words && !NinoLimits.Untrusted)
            {
                ReadPrechecked(ref Unsafe.As<NinoNullable<T>, T?>(ref MemoryMarshal.GetReference(destination)),
                    destination.Length, ref reader);
                return;
            }

            ref byte src = ref reader.UnsafeAdvance(destination.Length * Stride);
            if (destination.IsEmpty)
                return;
            NinoChecks.Flags(ref src, destination.Length, Stride);
            ref NinoNullable<T> item = ref MemoryMarshal.GetReference(destination);
            for (int i = 0; i < destination.Length; i++)
            {
                ref byte slot = ref Unsafe.Add(ref src, i * Stride);
                Unsafe.Add(ref item, i) = slot != NinoWire.Null
                    ? new NinoNullable<T>(NinoLayout<T>.Read(ref Unsafe.Add(ref slot, Offset)))
                    : default;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void WriteBoolValues(ref T? item, ref byte destination, int count)
        {
            for (int i = 0; i < count; i++)
            {
                bool? element = Unsafe.As<T?, bool?>(ref Unsafe.Add(ref item, i));
                bool value = element.GetValueOrDefault();
                ushort slot = element.HasValue ? (ushort)(1 | Unsafe.As<bool, byte>(ref value) << 8) : (ushort)0;
                NinoUnsafe.Write(ref Unsafe.Add(ref destination, i * 2), slot);
            }
        }

#if !NET8_0_OR_GREATER
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool TryWriteNativeBoolSlots(ref T? item, ref byte destination, int count)
        {
            if (!NativeBoolSlots)
                return false;

            ref byte source = ref Unsafe.As<T?, byte>(ref item);
            int bytes = count * Stride;
            nint distance = Unsafe.ByteOffset(ref source, ref destination);
            if (distance > -bytes && distance < bytes)
                return false;

            int completed = NinoKernelHook.TryNormalizeBoolSlots(ref source, ref destination, count);
            WriteBoolValues(ref Unsafe.Add(ref item, completed),
                ref Unsafe.Add(ref destination, completed * Stride), count - completed);
            return true;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool TryReadNativeBoolSlots(ref byte source, ref bool? destination, int count, int bytes)
        {
            if (!NativeBoolSlots)
                return false;

            ref byte target = ref Unsafe.As<bool?, byte>(ref destination);
            nint distance = Unsafe.ByteOffset(ref source, ref target);
            if (distance > -bytes && distance < bytes)
                return false;

            int completed = NinoKernelHook.TryNormalizeBoolSlots(ref source, ref target, count);
            if (completed != count)
                ReadBoolValues(ref Unsafe.Add(ref source, completed * Stride),
                    ref Unsafe.Add(ref destination, completed), count - completed);
            return true;
        }
#endif

#if NET8_0_OR_GREATER
        [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
        internal static void WriteBoolSlots<TVector, TSlots>(ref ushort source, ref ushort target, int count)
            where TVector : struct where TSlots : struct, IBoolSlots<TVector>
        {
            nuint lanes = (uint)(Unsafe.SizeOf<TVector>() / sizeof(ushort));
            nuint last = (uint)count - lanes;
            for (nuint i = 0; i < last; i += lanes)
                WriteBoolGroup<TVector, TSlots>(ref source, ref target, i);
            // End the final vector at the last slot, overlapping only bytes already written.
            WriteBoolGroup<TVector, TSlots>(ref source, ref target, last);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void WriteBoolGroup<TVector, TSlots>(ref ushort source, ref ushort target, nuint offset)
            where TVector : struct where TSlots : struct, IBoolSlots<TVector>
        {
            TVector pairs = Unsafe.ReadUnaligned<TVector>(ref Unsafe.As<ushort, byte>(ref Unsafe.Add(ref source, offset)));
            Unsafe.WriteUnaligned(ref Unsafe.As<ushort, byte>(ref Unsafe.Add(ref target, offset)), TSlots.Normalize(pairs));
        }

        private const ushort InvalidBoolSlotBits = 0xFE00;

        // Extend the low-byte 0/1 flag to mask each slot. Readers check presence bytes separately.
        internal interface IBoolSlots<TVector> where TVector : struct
        {
            static abstract TVector Normalize(TVector pairs);
            static abstract bool HasInvalidValues(TVector pairs);
        }

        private readonly struct BoolSlots64 : IBoolSlots<Vector64<ushort>>
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static bool HasInvalidValues(Vector64<ushort> pairs) =>
                (pairs & Vector64.Create(InvalidBoolSlotBits)) != Vector64<ushort>.Zero;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static Vector64<ushort> Normalize(Vector64<ushort> pairs)
            {
                var active = (pairs << 15).AsInt16() >> 15;
                return pairs & active.AsUInt16();
            }
        }

        /// <summary>
        /// Four slots in a 64-bit word, where no vector of four slots is (x64 and x86 below one 128-bit vector, hosts
        /// without vectors): the presence bit spread over its 16 bits masks the slot, as <see cref="BoolSlots64"/>'s shifts
        /// do.
        /// </summary>
        private readonly struct BoolSlotsWord : IBoolSlots<ulong>
        {
            private const ulong Presence = 0x0001_0001_0001_0001UL;
            private const ulong Invalid = InvalidBoolSlotBits * Presence;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static bool HasInvalidValues(ulong pairs) => (pairs & Invalid) != 0;

            // (bits << 16) - bits is bits * 0xFFFF without a 64-bit multiplication, which 32-bit x86 calls a helper for.
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static ulong Normalize(ulong pairs)
            {
                ulong bits = pairs & Presence;
                return pairs & ((bits << 16) - bits);
            }
        }

        private readonly struct BoolSlots128 : IBoolSlots<Vector128<ushort>>
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static bool HasInvalidValues(Vector128<ushort> pairs) =>
                (pairs & Vector128.Create(InvalidBoolSlotBits)) != Vector128<ushort>.Zero;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static Vector128<ushort> Normalize(Vector128<ushort> pairs)
            {
                var active = (pairs << 15).AsInt16() >> 15;
                return pairs & active.AsUInt16();
            }
        }

        private readonly struct BoolSlots256 : IBoolSlots<Vector256<ushort>>
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static bool HasInvalidValues(Vector256<ushort> pairs) =>
                (pairs & Vector256.Create(InvalidBoolSlotBits)) != Vector256<ushort>.Zero;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static Vector256<ushort> Normalize(Vector256<ushort> pairs)
            {
                var active = (pairs << 15).AsInt16() >> 15;
                return pairs & active.AsUInt16();
            }
        }

        internal readonly struct BoolSlots512 : IBoolSlots<Vector512<ushort>>
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static bool HasInvalidValues(Vector512<ushort> pairs) =>
                (pairs & Vector512.Create(InvalidBoolSlotBits)) != Vector512<ushort>.Zero;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static Vector512<ushort> Normalize(Vector512<ushort> pairs)
            {
                var active = (pairs << 15).AsInt16() >> 15;
                return pairs & active.AsUInt16();
            }
        }
#endif

#if NET8_0_OR_GREATER
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static bool TryReadFresh(T?[] destination, int length, ref Reader reader)
        {
            if (!Vector128.IsHardwareAccelerated)
                return false;

            int stride = Unsafe.SizeOf<T?>();
            int bytes = length * stride;
            ref byte target = ref Unsafe.As<T?, byte>(ref NinoUnsafe.GetArrayDataReference(destination));
            if (!NinoChecks.TryCopyFlags(ref target, ref reader.Peek(), bytes, stride))
                return false;
            reader.UnsafeAdvance(bytes);
            return true;
        }
#endif

        /// <summary>Reads the elements after the count check has bounded the full fixed-stride payload.</summary>
        public static void ReadPrechecked(Span<T?> destination, ref Reader reader) =>
            ReadPrechecked(ref MemoryMarshal.GetReference(destination), destination.Length, ref reader);

        /// <summary>
        /// <see cref="ReadPrechecked(Span{T?}, ref Reader)"/> of <paramref name="count"/> elements from
        /// <paramref name="item"/>: also what reads a <see cref="NinoNullable{T}"/> run in place, its memory taken as
        /// <c>T?</c> (<see cref="Words"/>), on every build (netstandard2.0 has no span over a reference).
        /// </summary>
        private static void ReadPrechecked(ref T? item, int count, ref Reader reader)
        {
            int bytes = count * Stride;
            ref byte src = ref reader.UnsafeAdvance(bytes);
            if (count == 0)
                return;
            if (typeof(T) == typeof(bool) && count == 1)
            {
                ReadSingleBool(ref src, ref Unsafe.As<T?, bool?>(ref item));
                return;
            }

            // Presence bytes other than 0 or 1 are invalid data (copied as they are, they would make HasValue true
            // and the value unequal to any other).
            NinoChecks.Flags(ref src, count, Stride);
            if (Raw)
            {
                NinoUnsafe.CopyToNew(ref Unsafe.As<T?, byte>(ref item), ref src, (uint)bytes);
                return;
            }

            if (typeof(T) == typeof(bool))
            {
                                ref bool? booleans = ref Unsafe.As<T?, bool?>(ref item);
#if NET8_0_OR_GREATER
                if (count < Vector128<ushort>.Count)
                {
                    if (count >= Vector64<ushort>.Count && TryReadShortBoolSlots(ref src, ref booleans, count, bytes))
                        return;
                    ReadBoolValues(ref src, ref booleans, count);
                    return;
                }

                if (NativeBoolSlots)
                {
                    ref byte target = ref Unsafe.As<bool?, byte>(ref booleans);
                    nint distance = Unsafe.ByteOffset(ref src, ref target);
                    if (distance <= -bytes || distance >= bytes)
                    {
                        ref ushort source = ref Unsafe.As<byte, ushort>(ref src);
                        ref ushort destinationSlots = ref Unsafe.As<byte, ushort>(ref target);
                        if (NinoSimd.Prefer512 && count >= Vector512<ushort>.Count)
                            ReadBoolSlots<Vector512<ushort>, BoolSlots512>(ref source, ref destinationSlots, count);
                        else if (Vector256.IsHardwareAccelerated && count >= Vector256<ushort>.Count)
                            ReadBoolSlots<Vector256<ushort>, BoolSlots256>(ref source, ref destinationSlots, count);
                        else if (Vector128.IsHardwareAccelerated)
                            ReadBoolSlots<Vector128<ushort>, BoolSlots128>(ref source, ref destinationSlots, count);
                        else
                            ReadBoolSlots<ulong, BoolSlotsWord>(ref source, ref destinationSlots, count);
                        return;
                    }
                }
#else
                if (count >= NinoKernelHook.MinBoolSlots &&
                    TryReadNativeBoolSlots(ref src, ref booleans, count, bytes))
                    return;
#endif
                // Keep the original conversion loop for long reads outside the vector path.
                if (count <= 1024)
                {
                    ReadBoolValues(ref src, ref booleans, count);
                    return;
                }
            }

            for (int i = 0; i < count; i++)
            {
                ref byte slot = ref Unsafe.Add(ref src, i * Stride);
                Unsafe.Add(ref item, i) = slot != NinoWire.Null
                    ? NinoLayout<T>.Read(ref Unsafe.Add(ref slot, Offset))
                    : default(T?);
            }
        }

#if NET8_0_OR_GREATER
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool TryReadShortBoolSlots(ref byte source, ref bool? destination, int count, int bytes)
        {
            if (!NativeBoolSlots)
                return false;

            ref byte target = ref Unsafe.As<bool?, byte>(ref destination);
            nint distance = Unsafe.ByteOffset(ref source, ref target);
            if (distance > -bytes && distance < bytes)
                return false;

            ref ushort from = ref Unsafe.As<byte, ushort>(ref source);
            ref ushort to = ref Unsafe.As<byte, ushort>(ref target);
            if (NinoSimd.HasArm64)
                ReadShortBoolSlots<Vector64<ushort>, BoolSlots64>(ref from, ref to, count);
            else
                ReadShortBoolSlots<ulong, BoolSlotsWord>(ref from, ref to, count);
            return true;
        }

        [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
        private static void ReadShortBoolSlots<TVector, TSlots>(ref ushort source, ref ushort target, int count)
            where TVector : struct
            where TSlots : struct, IBoolSlots<TVector>
        {
            // Four slots, then the last four (overlapping): 4 to 7 slots.
            ReadBoolGroup<TVector, TSlots>(ref source, ref target, 0);
            if (count > 4)
                ReadBoolGroup<TVector, TSlots>(ref source, ref target, (uint)(count - 4));
        }

        [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
        internal static void ReadBoolSlots<TVector, TSlots>(ref ushort source, ref ushort target, int count)
            where TVector : struct
            where TSlots : struct, IBoolSlots<TVector>
        {
            nuint lanes = (uint)(Unsafe.SizeOf<TVector>() / sizeof(ushort));
            nuint last = (uint)count - lanes;
            for (nuint i = 0; i < last; i += lanes)
                ReadBoolGroup<TVector, TSlots>(ref source, ref target, i);
            ReadBoolGroup<TVector, TSlots>(ref source, ref target, last);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void ReadBoolGroup<TVector, TSlots>(ref ushort source, ref ushort target, nuint offset)
            where TVector : struct
            where TSlots : struct, IBoolSlots<TVector>
        {
            var pairs = TSlots.Normalize(Unsafe.ReadUnaligned<TVector>(
                ref Unsafe.As<ushort, byte>(ref Unsafe.Add(ref source, offset))));
            if (TSlots.HasInvalidValues(pairs))
            {
                // Use the scalar order for the first invalid value and any preceding stores.
                ReadBoolValues(ref Unsafe.As<ushort, byte>(ref Unsafe.Add(ref source, offset)),
                    ref Unsafe.As<ushort, bool?>(ref Unsafe.Add(ref target, offset)), Unsafe.SizeOf<TVector>() / sizeof(ushort));
                return;
            }

            Unsafe.WriteUnaligned(ref Unsafe.As<ushort, byte>(ref Unsafe.Add(ref target, offset)), pairs);
        }
#endif

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ReadSingleBool(ref byte source, ref bool? destination)
        {
            byte flag = source;
            if (flag > 1)
                NinoThrow.InvalidPresence(flag);

            destination = flag != NinoWire.Null
                ? NinoUnsafe.CheckedBool(Unsafe.Add(ref source, Offset))
                : default(bool?);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ReadBoolValues(ref byte source, ref bool? item, int count)
        {
            for (int i = 0; i < count; i++)
            {
                ref byte slot = ref Unsafe.Add(ref source, i * Stride);
                Unsafe.Add(ref item, i) = slot != NinoWire.Null
                    ? NinoUnsafe.CheckedBool(Unsafe.Add(ref slot, Offset))
                    : default(bool?);
            }
        }
    }

    /// <summary>
    /// Field offsets of <c>Nullable&lt;T&gt;</c> in memory on the running runtime. Old Mono stores the value first
    /// while corefx stores the flag first, so the layout is probed rather than assumed.
    /// </summary>
    internal static class NullableLayout<T> where T : struct
    {
        public static readonly int HasValueOffset = ProbeHasValue();

        public static readonly int ValueOffset =
            HasValueOffset == 0 ? Unsafe.SizeOf<T?>() - Unsafe.SizeOf<T>() : 0;

        private static int ProbeHasValue()
        {
            T? probe = default(T); // has value, zero value and padding
            ref byte b = ref Unsafe.As<T?, byte>(ref probe);
            for (int i = 0; i < Unsafe.SizeOf<T?>(); i++)
                if (Unsafe.Add(ref b, i) != 0)
                    return i;
            return 0;
        }
    }
#if NET8_0_OR_GREATER
    /// <summary>
    /// A single <c>T?</c> of 16 bytes read from its wire form (the presence byte, then T) as two 8-byte words built in
    /// registers: memory holds the flag, zeros up to T's alignment, then T. Through a local the JIT stored T (12 bytes
    /// of <c>SimpleStruct</c>: 8 and 4) and loaded the 16-byte nullable over those stores, two store-forwarding stalls
    /// a read (NullableTest.NinoDeserializeNullableStruct, 15 ns against MemoryPack's 1 ns in v4.0.0-rc.10).
    /// </summary>
    internal static class NullableWords<T> where T : struct
    {
        /// <summary>Where T starts in <c>T?</c>: its alignment, the flag being first.</summary>
        private static readonly int Offset = NullableLayout<T>.ValueOffset;

        /// <summary>
        /// Whether <c>T?</c> is two words in the wire's order: a little-endian runtime with the flag first, and
        /// <c>T?</c> 16 bytes, T filling it from its alignment on (so the wire's 17 - Offset bytes cover both words).
        /// </summary>
        private static readonly bool Fits = BitConverter.IsLittleEndian && Unsafe.SizeOf<T?>() == 16 &&
                                            NullableLayout<T>.HasValueOffset == 0 && Offset is > 0 and <= 8 &&
                                            Unsafe.SizeOf<T>() == 16 - Offset;

        /// <summary>Whether <see cref="Read"/> applies: <see cref="Fits"/>, T read as its bytes.</summary>
        public static readonly bool Applies = Fits && NinoLayout<T>.CopiesAsIs;

        /// <summary>
        /// Stores the value whose present flag <paramref name="flag"/> is (checked by the caller), and T after it, as two
        /// words over <paramref name="value"/>: a <c>T?</c> local the JIT split into its fields joins them again through
        /// the stack to be returned.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Read(ref byte flag, ref T? value)
        {
            // The flag in byte 0 (it is 1), T's first bytes from Offset on; Offset 8 leaves T wholly in the high word.
            ulong low = Offset == 8 ? 1UL : NinoUnsafe.Read<ulong>(ref flag) >> 8 << (8 * Offset) | 1UL;
            ulong high = NinoUnsafe.Read<ulong>(ref Unsafe.Add(ref flag, 9 - Offset));
            Unsafe.As<T?, Words>(ref value) = new Words(low, high);
        }

        /// <summary>
        /// Whether <see cref="Write"/> applies: <see cref="Fits"/>, T written as it lies in memory (canonical, no
        /// padding to clear).
        /// </summary>
        public static readonly bool Writes = Fits && NinoLayout<T>.IsCanonical && !NinoLayout<T>.HasPadding;

        /// <summary>
        /// Writes the present <paramref name="value"/> as the wire holds it (the presence byte, then T) from its two
        /// words: the low one shifted down to T's first bytes behind the flag, the high one over its end. Why: the
        /// value copied T's 8 and 4 bytes from offset 4 of a 16-byte store, which Intel cores do not forward (a stall
        /// per write: NullableTest.NinoSerializeNullableStruct 6.9 ns against MemoryPack's 3.3 on a Xeon).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Write(ref byte destination, in T? value)
        {
            ref Words words = ref Unsafe.As<T?, Words>(ref Unsafe.AsRef(in value));
            // Bytes 0 to 7 first (for Offset 8 only the flag counts), then bytes 9 - Offset to 16 - Offset over them.
            NinoUnsafe.Write(ref destination, Offset == 8 ? 1UL : words.Low >> (8 * Offset) << 8 | 1UL);
            NinoUnsafe.Write(ref destination, 9 - Offset, words.High);
        }

        private readonly struct Words
        {
            public readonly ulong Low;
            public readonly ulong High;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public Words(ulong low, ulong high)
            {
                Low = low;
                High = high;
            }
        }
    }
#endif
}
