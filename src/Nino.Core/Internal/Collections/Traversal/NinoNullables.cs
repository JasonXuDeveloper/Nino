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

#if NET10_0_OR_GREATER
        /// <summary>
        /// For a stride of 16 bytes, the shuffle (pshufb, within each 16-byte lane, so within each element) that gives
        /// every byte a present element keeps its element's flag, and zero (an index with the top bit) to the padding:
        /// what <see cref="Kept(Vector512{byte})"/> multiplies the element by.
        /// </summary>
        private static readonly Vector512<byte> FlagOfByte =
            Stride is 2 or 4 or 8 or 16 ? FlagOfBytes() : default;

        private static Vector512<byte> FlagOfBytes()
        {
            // The index of the element's flag within its 16-byte lane (strides of 2 to 8 have several elements a lane).
            var keep = Keeps();
            var bytes = new byte[64];
            for (int i = 0; i < bytes.Length; i++)
                bytes[i] = keep[i % Stride] != 0 ? (byte)(i % 16 - i % Stride) : (byte)0x80;
            return Vector512.Create(bytes);
        }

        /// <summary>
        /// Whether a 16-byte element is cleared by a multiplication (GFNI): its bytes are kept whole or cleared whole
        /// (no byte keeps only some of its bits).
        /// </summary>
        // Strides of 2 to 8 bytes are one lane of a mask register an element: a test and a masked AND, two
        // operations already. Without AVX-512 (VL) they are a compare, an AND NOT and an AND, and take this too.
        private static readonly bool Multiplies = Gfni.IsSupported && KeepsWholeBytes() &&
                                                  (Stride == 16 ||
                                                   (Stride is 2 or 4 or 8 && !Avx512F.VL.IsSupported));

        /// <summary>The whole elements a 64-byte vector holds (0 for a stride over 64 bytes).</summary>
        private static readonly int PerVector = Stride <= 64 ? 64 / Stride : 0;

        /// <summary>
        /// Whether a run of any other stride up to 64 bytes (12, 20, 24, 32, ...: a <c>decimal?</c>, a <c>Guid?</c>, a
        /// struct of two or three words) is cleared the same way, a vector's whole elements at a time: their flags
        /// lie anywhere in the vector, so a permutation across it (vpermb, AVX-512 VBMI) gives each byte its
        /// element's flag.
        /// </summary>
        private static readonly bool Permutes = PerVector > 0 && Stride is not (2 or 4 or 8 or 16) &&
                                                Mask?.ClearsAbsent != true && Avx512Vbmi.IsSupported &&
                                                Gfni.V512.IsSupported && KeepsWholeBytes();

        /// <summary>For <see cref="Permutes"/>: where in the vector the flag of each byte's element is.</summary>
        private static readonly Vector512<byte> FlagIndex = Permutes ? FlagIndexes(false) : default;

        /// <summary>For <see cref="Permutes"/>: the bytes of the vector's whole elements that the wire keeps.</summary>
        private static readonly Vector512<byte> KeptBytes = Permutes ? FlagIndexes(true) : default;

        private static Vector512<byte> FlagIndexes(bool kept)
        {
            var keep = Keeps();
            var bytes = new byte[64];
            for (int i = 0; i < PerVector * Stride; i++)
                bytes[i] = kept ? keep[i % Stride] : (byte)(i - i % Stride);
            return Vector512.Create(bytes);
        }

        /// <summary>
        /// Copies the elements of <paramref name="total"/> bytes that fill whole 64-byte loads, as the wire holds
        /// them: each vector's whole elements cleared by <see cref="Kept(Vector512{byte})"/>'s multiplication, the
        /// flags gathered by a permutation. A store writes the whole vector; what lies past its whole elements is
        /// written again by the next one, or by the caller, which copies the elements from the returned offset on.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static nuint CopyPermuted(ref byte destination, ref byte source, nuint total)
        {
            nuint step = (nuint)(PerVector * Stride);
            var index = FlagIndex;
            var kept = KeptBytes;
            nuint i = 0;
            for (; i + 64 <= total; i += step)
            {
                var elements = Vector512.LoadUnsafe(ref source, i);
                var flags = Avx512Vbmi.PermuteVar64x8(elements, index) & kept;
                Gfni.V512.GaloisFieldMultiply(elements, flags).StoreUnsafe(ref destination, i);
            }

            return i;
        }

        /// <summary>
        /// The elements of a 16-byte stride as the wire holds them, in two operations: every byte multiplied in GF(2^8)
        /// by its element's flag (gf2p8mulb: times 1 it is kept, times 0 cleared), the padding by 0. A test into a
        /// mask register and a masked AND after the shuffle are three, two of them on port 5 of an Intel core. A flag
        /// is 0 or 1 in every <c>Nullable</c> the runtime makes; any other value is invalid data to every reader.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector512<byte> Kept(Vector512<byte> value) =>
            Gfni.V512.GaloisFieldMultiply(value, Avx512BW.Shuffle(value, FlagOfByte));

        /// <summary><see cref="Kept(Vector512{byte})"/> for 32-byte vectors.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector256<byte> Kept(Vector256<byte> value) =>
            Gfni.V256.GaloisFieldMultiply(value, Avx2.Shuffle(value, FlagOfByte.GetLower()));
#endif

        /// <summary>Whether the wire keeps or clears each byte of an element whole (no byte keeps only some bits).</summary>
        private static bool KeepsWholeBytes()
        {
            foreach (byte keep in Keeps())
                if (keep != 0 && keep != 0xFF)
                    return false;
            return true;
        }

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
#if NET10_0_OR_GREATER
                if (Multiplies && Gfni.V512.IsSupported)
                {
                    Kept(a).StoreUnsafe(ref destination, offset);
                    return;
                }
#endif

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
#if NET10_0_OR_GREATER
                if (Multiplies && Gfni.V512.IsSupported)
                {
                    Kept(a).StoreUnsafe(ref destination, offset);
                    Kept(b).StoreUnsafe(ref destination, offset + 64);
                    Kept(c).StoreUnsafe(ref destination, offset + 128);
                    Kept(d).StoreUnsafe(ref destination, offset + 192);
                    return;
                }
#endif
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
#if NET10_0_OR_GREATER
                if (Multiplies && Gfni.V256.IsSupported)
                {
                    Kept(a).StoreUnsafe(ref destination, offset);
                    return;
                }
#endif

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
#if NET10_0_OR_GREATER
                if (Multiplies && Gfni.V256.IsSupported)
                {
                    Kept(a).StoreUnsafe(ref destination, offset);
                    Kept(b).StoreUnsafe(ref destination, offset + 32);
                    Kept(c).StoreUnsafe(ref destination, offset + 64);
                    Kept(d).StoreUnsafe(ref destination, offset + 96);
                    return;
                }
#endif
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

        /// <summary>
        /// Whether a run of a stride the lanes do not take (3, 6, 12, 20, 32, 40, ...: a struct of two floats, a
        /// <c>Guid?</c>, a struct of three or four words) is cleared in one pass by <see cref="CopyPieces"/>, with
        /// 16-byte vectors (SSSE3, Arm64) or 32-byte ones (AVX2). AVX-512 VBMI with GFNI takes strides up to 64 bytes
        /// in <see cref="CopyPermuted"/> first; no vectors, and the netstandard build, copy and then clear.
        /// </summary>
        private static readonly bool Pieces = (Ssse3.IsSupported || AdvSimd.Arm64.IsSupported) && !Lanes &&
                                              Stride is not (2 or 4 or 8 or 16 or 24) &&
                                              Mask?.ClearsAbsent != true && KeepsWholeBytes();

        /// <summary>Whether 32-byte and 16-byte vectors clear by the multiplication (GFNI), as <see cref="Kept(Vector256{byte})"/>.</summary>
#if NET10_0_OR_GREATER
        private static readonly bool Multiplies256 = Gfni.V256.IsSupported;

        private static readonly bool Multiplies128 = Gfni.IsSupported;
#else
        private const bool Multiplies256 = false;

        private const bool Multiplies128 = false;
#endif

        // Read by the tables below: static fields are initialized in the order they are written.
        /// <summary>
        /// For <see cref="Pieces128"/>: the place in a 16-byte vector of the flag of each byte's element, for the bytes
        /// the wire keeps of the whole elements it holds (several of a stride below 16, else the first 16 bytes of
        /// one), and an index out of the table (pshufb and tbl give zero) for the others.
        /// </summary>
        private static readonly Vector128<byte> PieceFlags = Pieces ? PieceFlagIndexes() : default;

        private static Vector128<byte> PieceFlagIndexes()
        {
            var keep = Keeps();
            var bytes = new byte[16];
            int whole = Stride >= 16 ? 16 : 16 / Stride * Stride;
            for (int i = 0; i < bytes.Length; i++)
                bytes[i] = i < whole && keep[i % Stride] != 0 ? (byte)(i - i % Stride) : (byte)0x80;
            return Vector128.Create(bytes);
        }

        /// <summary>
        /// For <see cref="Pieces256"/>: what the first 32 bytes of an element are tested against or ANDed with. With
        /// GFNI the bytes the wire keeps (the flag in each of them is the byte's multiplier); without, 1 in those bytes
        /// and 0xFF in the others, which no flag equals, so one compare gives the bytes of a present element to keep.
        /// </summary>
        private static readonly Vector256<byte> PieceKept = Pieces ? PieceKeptBytes() : default;

        private static Vector256<byte> PieceKeptBytes()
        {
            var keep = Keeps();
            var bytes = new byte[32];
            for (int i = 0; i < bytes.Length; i++)
            {
                bool kept = i < Stride && keep[i] != 0;
                bytes[i] = Multiplies256 ? (kept ? (byte)0xFF : (byte)0) : (kept ? (byte)1 : (byte)0xFF);
            }

            return Vector256.Create(bytes);
        }

        /// <summary>The bytes the wire keeps of one element, for the pieces after an element's first vector.</summary>
        private static readonly byte[] PieceKeeps = Pieces ? Keeps() : null;

        /// <summary>Whether those pieces keep every byte (the value has no padding of its own): no AND for them.</summary>
        private static readonly bool WholePieces = Array.TrueForAll(Keeps()[Math.Min(8, Stride)..], keep => keep == 0xFF);

        /// <summary>
        /// Copies the elements of <paramref name="total"/> bytes that fill whole vectors as the wire holds them, and
        /// returns how many bytes it wrote (whole elements); the caller copies the few after them. AVX2 takes an
        /// element of up to 32 bytes in one load (its last element would end past the run: 16-byte vectors take it)
        /// and longer ones in 32-byte pieces; 16-byte vectors take the rest.
        /// </summary>
        // Out of line: one call per collection, as CopyLanes.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static nuint CopyPieces(ref byte destination, ref byte source, nuint total)
        {
            nuint done = 0;
            if (Avx2.IsSupported && Stride > 16)
            {
                done = CopyPieces<Vector256<byte>, Pieces256>(ref destination, ref source, total);
                if (done == total)
                    return done;
            }

            return done + CopyPieces<Vector128<byte>, Pieces128>(ref Unsafe.Add(ref destination, done),
                ref Unsafe.Add(ref source, done), total - done);
        }

        /// <summary>
        /// One vector width of <see cref="CopyPieces"/>. A stride below the vector: the whole elements a vector holds a
        /// step, each byte cleared by its own element's flag; what the store writes past them the next step writes
        /// again. A stride of a vector or more: one element a step, its first vector, then pieces of a vector each
        /// cleared by the element's flag, the last ending where the element ends (it overlaps the one before).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static nuint CopyPieces<TVector, TPieces>(ref byte destination, ref byte source, nuint total)
            where TVector : struct
            where TPieces : struct, IPieces<TVector>
        {
            nuint width = (nuint)Unsafe.SizeOf<TVector>(), stride = (nuint)Stride;
            nuint step = stride >= width ? stride : width / stride * stride;
            nuint reach = stride >= width ? stride : width;
            var table = TPieces.Table;
            ref byte keep = ref MemoryMarshal.GetArrayDataReference(PieceKeeps);
            nuint i = 0;
            for (; i + reach <= total; i += step)
            {
                var first = Unsafe.ReadUnaligned<TVector>(ref Unsafe.Add(ref source, i));
                if (stride > width)
                {
                    var present = TPieces.Present(first);
                    for (nuint at = width; at + width < stride; at += width)
                        Unsafe.WriteUnaligned(ref Unsafe.Add(ref destination, i + at), TPieces.Rest(
                            Unsafe.ReadUnaligned<TVector>(ref Unsafe.Add(ref source, i + at)), present,
                            ref Unsafe.Add(ref keep, at)));
                    nuint last = i + stride - width;
                    Unsafe.WriteUnaligned(ref Unsafe.Add(ref destination, last), TPieces.Rest(
                        Unsafe.ReadUnaligned<TVector>(ref Unsafe.Add(ref source, last)), present,
                        ref Unsafe.Add(ref keep, stride - width)));
                }

                Unsafe.WriteUnaligned(ref Unsafe.Add(ref destination, i), TPieces.First(first, table));
            }

            return i;
        }

        /// <summary>The operations of one vector width of <see cref="CopyPieces{TVector,TPieces}"/>.</summary>
        private interface IPieces<TVector> where TVector : struct
        {
            /// <summary>The constant <see cref="First"/> takes, read once before the loop.</summary>
            static abstract TVector Table { get; }

            /// <summary>The first vector of a step as the wire holds it.</summary>
            static abstract TVector First(TVector first, TVector table);

            /// <summary>Whether the element whose first vector is <paramref name="first"/> has a value, as <see cref="Rest"/> takes it.</summary>
            static abstract TVector Present(TVector first);

            /// <summary>A later piece of that element as the wire holds it; <paramref name="keep"/> is its bytes to keep.</summary>
            static abstract TVector Rest(TVector piece, TVector present, ref byte keep);
        }

        /// <summary>
        /// 16-byte vectors: a shuffle (pshufb, tbl) gives each byte its element's flag, zero where the wire keeps
        /// nothing. x64 multiplies by it with GFNI, else compares it with zero and clears those bytes (pandn); Arm64
        /// tests it (cmtst) and ANDs.
        /// </summary>
        private readonly struct Pieces128 : IPieces<Vector128<byte>>
        {
            public static Vector128<byte> Table
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get => PieceFlags;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static Vector128<byte> First(Vector128<byte> first, Vector128<byte> table)
            {
                if (AdvSimd.Arm64.IsSupported)
                {
                    var flags = AdvSimd.Arm64.VectorTableLookup(first, table);
                    return first & AdvSimd.CompareTest(flags, flags);
                }

                if (Ssse3.IsSupported)
                {
                    var flags = Ssse3.Shuffle(first, table);
#if NET10_0_OR_GREATER
                    if (Gfni.IsSupported)
                        return Gfni.GaloisFieldMultiply(first, flags);
#endif
                    return Sse2.AndNot(Sse2.CompareEqual(flags, Vector128<byte>.Zero), first);
                }

                return first;
            }

            /// <summary>The flag in every byte: with GFNI the multiplier itself, else the bytes of an absent element (x64), of a present one (Arm64).</summary>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static Vector128<byte> Present(Vector128<byte> first)
            {
                if (AdvSimd.Arm64.IsSupported)
                {
                    var flags = AdvSimd.DuplicateSelectedScalarToVector128(first, 0);
                    return AdvSimd.CompareTest(flags, flags);
                }

                if (Ssse3.IsSupported)
                {
                    var flags = Ssse3.Shuffle(first, Vector128<byte>.Zero);
                    return Multiplies128 ? flags : Sse2.CompareEqual(flags, Vector128<byte>.Zero);
                }

                return first;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static Vector128<byte> Rest(Vector128<byte> piece, Vector128<byte> present, ref byte keep)
            {
                if (AdvSimd.Arm64.IsSupported)
                    return WholePieces ? piece & present : piece & present & Vector128.LoadUnsafe(ref keep);
#if NET10_0_OR_GREATER
                if (Gfni.IsSupported)
                    return Gfni.GaloisFieldMultiply(piece, WholePieces ? present : present & Vector128.LoadUnsafe(ref keep));
#endif
                return Sse2.IsSupported
                    ? Sse2.AndNot(present, WholePieces ? piece : piece & Vector128.LoadUnsafe(ref keep))
                    : piece;
            }
        }

        /// <summary>
        /// 32-byte vectors (AVX2), for strides above 16 bytes: the element's flag broadcast to every byte (a shuffle
        /// stays within 16 bytes); with GFNI the bytes multiplied by it where the wire keeps them, without it compared
        /// with 1 there and ANDed.
        /// </summary>
        private readonly struct Pieces256 : IPieces<Vector256<byte>>
        {
            public static Vector256<byte> Table
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get => PieceKept;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static Vector256<byte> First(Vector256<byte> first, Vector256<byte> table)
            {
                if (!Avx2.IsSupported)
                    return first;
                var flags = Avx2.BroadcastScalarToVector256(first.GetLower());
#if NET10_0_OR_GREATER
                if (Gfni.V256.IsSupported)
                    return Gfni.V256.GaloisFieldMultiply(first, flags & table);
#endif
                return first & Avx2.CompareEqual(flags, table);
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static Vector256<byte> Present(Vector256<byte> first)
            {
                if (!Avx2.IsSupported)
                    return first;
                var flags = Avx2.BroadcastScalarToVector256(first.GetLower());
                return Multiplies256 ? flags : Avx2.CompareEqual(flags, Vector256<byte>.Zero);
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static Vector256<byte> Rest(Vector256<byte> piece, Vector256<byte> present, ref byte keep)
            {
#if NET10_0_OR_GREATER
                if (Gfni.V256.IsSupported)
                    return Gfni.V256.GaloisFieldMultiply(piece, WholePieces ? present : present & Vector256.LoadUnsafe(ref keep));
#endif
                return Avx2.IsSupported
                    ? Avx2.AndNot(present, WholePieces ? piece : piece & Vector256.LoadUnsafe(ref keep))
                    : piece;
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
                int count = values.Length;
#if NET10_0_OR_GREATER
                if (Permutes && count * Stride >= 64)
                {
                    // The elements that fill whole vectors; the last few take the paths below.
                    int done = (int)CopyPermuted(ref dst, ref src, (nuint)(count * Stride));
                    count -= done / Stride;
                    if (count == 0)
                        return;
                    src = ref Unsafe.Add(ref src, done);
                    dst = ref Unsafe.Add(ref dst, done);
                }
#endif
#if NET8_0_OR_GREATER
                if (Lanes && count * Stride >= LaneStep)
                {
                    CopyLanes(ref dst, ref src, count * Stride);
                    return;
                }

                if (Pieces)
                {
                    // The elements that fill whole vectors; the few after them are copied and cleared below.
                    int done = (int)CopyPieces(ref dst, ref src, (nuint)(count * Stride));
                    count -= done / Stride;
                    if (count == 0)
                        return;
                    src = ref Unsafe.Add(ref src, done);
                    dst = ref Unsafe.Add(ref dst, done);
                }
#endif
                if (Mask != null)
                    Mask.Copy(ref dst, ref src, count);
                else
                    NinoUnsafe.Copy(ref dst, ref src, (uint)(count * Stride));
                ClearAbsent(ref dst, count);
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
        /// Whether a <see cref="NinoNullable{T}"/> of a 16-byte <typeparamref name="T"/> with 8-byte alignment
        /// (<c>decimal</c>, a struct of two words) is four words in memory and three on the wire (the flag's word,
        /// then the value's two): a run is then written and read a word at a time, without converting each value.
        /// </summary>
#if NET8_0_OR_GREATER
        internal static readonly bool Wide = Raw && Stride == 24 && Offset == 8 && Unsafe.SizeOf<T>() == 16 &&
                                            Unsafe.SizeOf<NinoNullable<T>>() == 32;

        /// <summary>The bytes the wire keeps of the two words of a <see cref="Wide"/> value.</summary>
        private static readonly ulong WideKeep0 = Wide ? BitConverter.ToUInt64(Keeps(), 8) : 0;

        private static readonly ulong WideKeep1 = Wide ? BitConverter.ToUInt64(Keeps(), 16) : 0;
#else
        // The netstandard build converts these slot by slot (Unity's Burst kernels take its block copies).
        internal static bool Wide => false;

        private static ulong WideKeep0 => 0;

        private static ulong WideKeep1 => 0;
#endif

        /// <summary>
        /// Writes <paramref name="count"/> <see cref="Wide"/> values: of each four-word slot the first (its presence,
        /// a flag and zeros), the third and the fourth (the value, without the bytes the wire clears), two slots a
        /// vector with AVX-512 (one permutation and one AND), one with AVX2. The type keeps an absent value zero.
        /// </summary>
        private static void WriteWide(ref byte destination, ref byte source, int count)
        {
            int i = 0;
#if NET8_0_OR_GREATER
            // A vector store writes past the words it keeps (16 bytes with AVX-512, 8 with AVX2): the last element
            // is left to the words below, whose 24 bytes cover them.
            if (Vector512.IsHardwareAccelerated && Avx512F.IsSupported)
            {
                var order = Vector512.Create(0UL, 2, 3, 4, 6, 7, 7, 7);
                var keep = Vector512.Create(0xFFUL, WideKeep0, WideKeep1, 0xFF, WideKeep0, WideKeep1, 0, 0);
                for (; i + 3 <= count; i += 2)
                {
                    var slots = Vector512.LoadUnsafe(ref Unsafe.As<byte, ulong>(ref source), (nuint)i * 4);
                    (Avx512F.PermuteVar8x64(slots, order) & keep).AsByte()
                        .StoreUnsafe(ref destination, (nuint)i * 24);
                }
            }
            else if (Avx2.IsSupported)
            {
                var keep = Vector256.Create(0xFFUL, WideKeep0, WideKeep1, 0);
                for (; i + 2 <= count; i++)
                {
                    var slot = Vector256.LoadUnsafe(ref Unsafe.As<byte, ulong>(ref source), (nuint)i * 4);
                    (Avx2.Permute4x64(slot, 0b11_11_10_00) & keep).AsByte().StoreUnsafe(ref destination, (nuint)i * 24);
                }
            }
#endif
            for (; i < count; i++)
            {
                ref byte slot = ref Unsafe.Add(ref source, i * 32);
                ref byte target = ref Unsafe.Add(ref destination, i * 24);
                ulong value0 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref slot, 16));
                ulong value1 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref slot, 24));
                Unsafe.WriteUnaligned(ref target, (ulong)slot);
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref target, 8), value0 & WideKeep0);
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref target, 16), value1 & WideKeep1);
            }
        }

        /// <summary>
        /// Reads <paramref name="count"/> <see cref="Wide"/> values whose presence bytes were checked (0 or 1): each
        /// slot's four words stored (presence, zero, the value or zeros where the data has none).
        /// </summary>
        private static void ReadWide(ref byte destination, ref byte source, int count)
        {
            for (int i = 0; i < count; i++)
            {
                ref byte slot = ref Unsafe.Add(ref source, i * 24);
                ref byte target = ref Unsafe.Add(ref destination, i * 32);
                ulong flag = slot;
                ulong present = 0UL - flag;
                ulong value0 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref slot, 8));
                ulong value1 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref slot, 16));
                Unsafe.WriteUnaligned(ref target, flag);
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref target, 8), 0UL);
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref target, 16), value0 & present);
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref target, 24), value1 & present);
            }
        }

#if NET8_0_OR_GREATER
        /// <summary>The bytes of a <see cref="NinoNullable{T}"/> in memory: its presence and its value, a T each.</summary>
        private static readonly int SlotSize = Unsafe.SizeOf<NinoNullable<T>>();

        /// <summary>
        /// Whether a run of <see cref="NinoNullable{T}"/> that is not its wire as it lies (<see cref="Words"/>) is
        /// moved by vectors: T larger than its alignment and written as its bytes (<c>decimal</c>, <c>Guid</c>, a
        /// struct of floats or of words). Of each slot the wire holds the presence byte, zeros up to T's alignment,
        /// then the value without the bytes the wire clears; reading is the same the other way.
        /// </summary>
        private static readonly bool Moves = Raw && !Words && Mask?.ClearsAbsent != true && KeepsWholeBytes() &&
                                             Unsafe.SizeOf<T>() == NinoLayout<T>.WireSize;

        /// <summary>
        /// The vector whose whole slots one shuffle packs into their wire form (<see cref="Shuffled{TVector,TShuffle}"/>),
        /// 0 for none: 64 bytes with AVX-512 VBMI (vpermb, any slot up to 64 bytes), 32 with AVX2 where the slot is
        /// made of 4-byte words (vpermd: T aligned to 4 or 8 bytes, up to 16 bytes), 16 where a slot fits (pshufb with
        /// SSSE3, tbl on Arm64: T up to 8 bytes).
        /// </summary>
        private static readonly int PackWidth =
            !Moves ? 0
            : Avx512Vbmi.IsSupported && Vector512.IsHardwareAccelerated && SlotSize <= 64 ? 64
            : Avx2.IsSupported && SlotSize <= 32 && Offset % 4 == 0 ? 32
            : (Ssse3.IsSupported || AdvSimd.Arm64.IsSupported) && SlotSize <= 16 ? 16
            : 0;

        /// <summary>The slots a vector of <see cref="PackWidth"/> bytes holds whole.</summary>
        private static readonly int SlotsPerVector = PackWidth / SlotSize;

        private static readonly Vector512<byte> PackIndex = PackWidth != 0 ? Packing(true, false) : default;

        private static readonly Vector512<byte> PackKeep = PackWidth != 0 ? Packing(true, true) : default;

        private static readonly Vector512<byte> UnpackIndex = PackWidth != 0 ? Packing(false, false) : default;

        private static readonly Vector512<byte> UnpackKeep = PackWidth != 0 ? Packing(false, true) : default;

        /// <summary>
        /// For each byte of the vector written (the wire when <paramref name="pack"/>, else the slots in memory): the
        /// byte of the vector read that it takes, or with <paramref name="keep"/> whether it keeps it (the rest zero).
        /// For 32-byte vectors the index is of 4-byte words (vpermd); for 16-byte ones a byte the wire does not keep
        /// has an index out of the table, which pshufb and tbl turn into zero, so they need no AND.
        /// </summary>
        private static Vector512<byte> Packing(bool pack, bool keep)
        {
            int size = Unsafe.SizeOf<T>();
            var kept = Keeps();
            var bytes = new byte[64];
            if (PackWidth == 16 && !keep)
                bytes.AsSpan().Fill(0x80);
            int to = pack ? Stride : SlotSize, from = pack ? SlotSize : Stride;
            for (int i = 0; i < SlotsPerVector * to; i++)
            {
                int element = i / to, at = i % to;
                // On the wire: the flag, zeros to Offset, the value. In memory: the flag, zeros to size, the value.
                int head = pack ? Offset : size;
                bool value = at >= head;
                byte keeps = at == 0 ? (byte)0xFF : value ? (pack ? kept[at] : (byte)0xFF) : (byte)0;
                int takes = element * from + (value ? (pack ? size : Offset) + at - head : 0);
                if (keep)
                    bytes[i] = keeps;
                else if (PackWidth == 64)
                    bytes[i] = (byte)takes;
                else if (PackWidth == 16)
                    bytes[i] = keeps != 0 ? (byte)takes : (byte)0x80;
                else if (i % 4 == 0)
                    bytes[i] = (byte)(takes / 4);
            }

            return Vector512.Create(bytes);
        }

        /// <summary>
        /// Packs the slots of <paramref name="count"/> values that fill whole loads and stores of a vector, and
        /// returns how many it wrote: a store writes the whole vector, and what lies past its elements is written
        /// again by the next one or by the caller, which writes the rest.
        /// </summary>
        private static int WritePacked(ref byte destination, ref byte source, int count) =>
            PackWidth == 64
                ? Shuffled<Vector512<byte>, Shuffle512>(ref destination, ref source, count, SlotSize, Stride,
                    PackIndex, PackKeep)
                : PackWidth == 32
                    ? Shuffled<Vector256<byte>, Shuffle256>(ref destination, ref source, count, SlotSize, Stride,
                        PackIndex.GetLower(), PackKeep.GetLower())
                    : Shuffled<Vector128<byte>, Shuffle128>(ref destination, ref source, count, SlotSize, Stride,
                        PackIndex.GetLower().GetLower(), default);

        /// <summary><see cref="WritePacked"/> the other way, of values whose presence bytes were checked.</summary>
        private static int ReadPacked(ref byte destination, ref byte source, int count) =>
            PackWidth == 64
                ? Shuffled<Vector512<byte>, Shuffle512>(ref destination, ref source, count, Stride, SlotSize,
                    UnpackIndex, UnpackKeep)
                : PackWidth == 32
                    ? Shuffled<Vector256<byte>, Shuffle256>(ref destination, ref source, count, Stride, SlotSize,
                        UnpackIndex.GetLower(), UnpackKeep.GetLower())
                    : Shuffled<Vector128<byte>, Shuffle128>(ref destination, ref source, count, Stride, SlotSize,
                        UnpackIndex.GetLower().GetLower(), default);

        /// <summary>
        /// The loop of <see cref="WritePacked"/> and <see cref="ReadPacked"/>: a vector of whole elements of
        /// <paramref name="from"/> bytes loaded, shuffled into elements of <paramref name="to"/> bytes and stored,
        /// while both the load and the store stay inside the run.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int Shuffled<TVector, TShuffle>(ref byte destination, ref byte source, int count, int from,
            int to, TVector index, TVector keep)
            where TVector : struct
            where TShuffle : struct, IShuffle<TVector>
        {
            int i = 0;
            for (; (count - i) * Stride >= Unsafe.SizeOf<TVector>(); i += SlotsPerVector)
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref destination, (nuint)(i * to)), TShuffle.Shuffle(
                    Unsafe.ReadUnaligned<TVector>(ref Unsafe.Add(ref source, (nuint)(i * from))), index, keep));
            return i;
        }

        /// <summary>One vector width of <see cref="Shuffled{TVector,TShuffle}"/>.</summary>
        private interface IShuffle<TVector> where TVector : struct
        {
            static abstract TVector Shuffle(TVector value, TVector index, TVector keep);
        }

        private readonly struct Shuffle512 : IShuffle<Vector512<byte>>
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static Vector512<byte> Shuffle(Vector512<byte> value, Vector512<byte> index, Vector512<byte> keep) =>
                Avx512Vbmi.IsSupported ? Avx512Vbmi.PermuteVar64x8(value, index) & keep : value;
        }

        private readonly struct Shuffle256 : IShuffle<Vector256<byte>>
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static Vector256<byte> Shuffle(Vector256<byte> value, Vector256<byte> index, Vector256<byte> keep) =>
                Avx2.IsSupported ? Avx2.PermuteVar8x32(value.AsInt32(), index.AsInt32()).AsByte() & keep : value;
        }

        private readonly struct Shuffle128 : IShuffle<Vector128<byte>>
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static Vector128<byte> Shuffle(Vector128<byte> value, Vector128<byte> index, Vector128<byte> keep) =>
                AdvSimd.Arm64.IsSupported ? AdvSimd.Arm64.VectorTableLookup(value, index)
                : Ssse3.IsSupported ? Ssse3.Shuffle(value, index)
                : value;
        }

        /// <summary>
        /// The vector the slots no shuffle packs are moved with (<see cref="WriteWindows"/>, <see cref="ReadWindows"/>),
        /// 0 for none: the wire's bytes of a slot but its presence byte lie together in memory (the end of the
        /// presence, which is zeros, then the value), so they are copied in pieces of a vector, the last ending where
        /// the slot ends, and the presence is stored after them. 32 bytes with AVX2 for a wire slot of 32 bytes or
        /// more, else 16 for one of 16 or more.
        /// </summary>
        private static readonly int WindowWidth =
            !Moves || Offset > 8 ? 0
            : Avx2.IsSupported && Stride >= 32 ? 32
            : Vector128.IsHardwareAccelerated && Stride >= 16 ? 16
            : 0;

        /// <summary>Whether the wire keeps every byte of the value (T has no padding of its own).</summary>
        private static readonly bool WholeValue = Array.TrueForAll(Keeps()[Offset..], keep => keep == 0xFF);

        /// <summary>The bytes the wire keeps of one slot, for the pieces of a value with padding.</summary>
        private static readonly byte[] WindowKeeps = WindowWidth != 0 ? Keeps() : null;

        /// <summary>Writes <paramref name="count"/> values a vector piece at a time (<see cref="WindowWidth"/>).</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void WriteWindows(ref byte destination, ref byte source, int count)
        {
            if (WindowWidth == 32)
                WriteWindows<Vector256<byte>>(ref destination, ref source, count);
            else
                WriteWindows<Vector128<byte>>(ref destination, ref source, count);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void WriteWindows<TVector>(ref byte destination, ref byte source, int count)
            where TVector : struct
        {
            nuint width = (nuint)Unsafe.SizeOf<TVector>(), stride = (nuint)Stride;
            ref byte keep = ref MemoryMarshal.GetArrayDataReference(WindowKeeps);
            for (int i = 0; i < count; i++)
            {
                // The slot's last Stride bytes: Offset bytes of the presence's end, then the value.
                ref byte window = ref Unsafe.Add(ref source, SlotSize - Stride);
                for (nuint at = 0; at + width < stride; at += width)
                    Unsafe.WriteUnaligned(ref Unsafe.Add(ref destination, at),
                        KeptPiece(Unsafe.ReadUnaligned<TVector>(ref Unsafe.Add(ref window, at)), ref Unsafe.Add(ref keep, at)));
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref destination, stride - width), KeptPiece(
                    Unsafe.ReadUnaligned<TVector>(ref Unsafe.Add(ref window, stride - width)),
                    ref Unsafe.Add(ref keep, stride - width)));
                // Over the presence's end the pieces copied: the presence byte and zeros up to the value.
                WritePresence(ref destination, source);
                source = ref Unsafe.Add(ref source, SlotSize);
                destination = ref Unsafe.Add(ref destination, stride);
            }
        }

        /// <summary>A piece of a value as the wire holds it: as it is, or without the bytes of T's own padding.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static TVector KeptPiece<TVector>(TVector piece, ref byte keep) where TVector : struct
        {
            if (WholeValue)
                return piece;
            if (typeof(TVector) == typeof(Vector256<byte>))
            {
                var kept = Unsafe.As<TVector, Vector256<byte>>(ref piece) & Vector256.LoadUnsafe(ref keep);
                return Unsafe.As<Vector256<byte>, TVector>(ref kept);
            }
            else
            {
                var kept = Unsafe.As<TVector, Vector128<byte>>(ref piece) & Vector128.LoadUnsafe(ref keep);
                return Unsafe.As<Vector128<byte>, TVector>(ref kept);
            }
        }

        /// <summary><see cref="WriteWindows"/> the other way, of values whose presence bytes were checked.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ReadWindows(ref byte destination, ref byte source, int count)
        {
            if (WindowWidth == 32)
                ReadWindows<Vector256<byte>>(ref destination, ref source, count);
            else
                ReadWindows<Vector128<byte>>(ref destination, ref source, count);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void ReadWindows<TVector>(ref byte destination, ref byte source, int count)
            where TVector : struct
        {
            nuint width = (nuint)Unsafe.SizeOf<TVector>(), stride = (nuint)Stride;
            for (int i = 0; i < count; i++)
            {
                ref byte window = ref Unsafe.Add(ref destination, SlotSize - Stride);
                byte presence = source;
                for (nuint at = 0; at + width < stride; at += width)
                    Unsafe.WriteUnaligned(ref Unsafe.Add(ref window, at),
                        Unsafe.ReadUnaligned<TVector>(ref Unsafe.Add(ref source, at)));
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref window, stride - width),
                    Unsafe.ReadUnaligned<TVector>(ref Unsafe.Add(ref source, stride - width)));
                // Over the wire's flag and padding the pieces copied: the presence, a whole T (stores only).
                Unsafe.As<byte, T>(ref destination) = default;
                destination = presence;
                source = ref Unsafe.Add(ref source, stride);
                destination = ref Unsafe.Add(ref destination, SlotSize);
            }
        }
#endif

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

            int count = values.Length;
#if NET8_0_OR_GREATER
            if (PackWidth != 0)
            {
                int done = WritePacked(ref dst, ref Unsafe.As<NinoNullable<T>, byte>(ref item), count);
                count -= done;
                item = ref Unsafe.Add(ref item, done);
                dst = ref Unsafe.Add(ref dst, done * Stride);
            }

            if (WindowWidth != 0 && !Wide)
            {
                WriteWindows(ref dst, ref Unsafe.As<NinoNullable<T>, byte>(ref item), count);
                return;
            }
#endif

            if (Wide)
            {
                WriteWide(ref dst, ref Unsafe.As<NinoNullable<T>, byte>(ref item), count);
                return;
            }

            // Slot by slot without a branch: the presence byte with the slot's padding (Offset bytes), then the value,
            // which the type keeps zero when it has none.
            for (int i = 0; i < count; i++)
            {
                ref T presence = ref Unsafe.As<NinoNullable<T>, T>(ref Unsafe.Add(ref item, i));
                ref byte slot = ref Unsafe.Add(ref dst, i * Stride);
                WritePresence(ref slot, Unsafe.As<T, byte>(ref presence));
                NinoLayout<T>.Write(ref Unsafe.Add(ref slot, Offset), in Unsafe.Add(ref presence, 1));
            }
        }

        /// <summary>The first <see cref="Offset"/> bytes of a slot: the presence byte, then zeros.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void WritePresence(ref byte slot, byte presence)
        {
            if (Offset == 8)
                Unsafe.WriteUnaligned(ref slot, (ulong)presence);
            else if (Offset == 4)
                Unsafe.WriteUnaligned(ref slot, (uint)presence);
            else if (Offset == 2)
                Unsafe.WriteUnaligned(ref slot, (ushort)presence);
            else
                slot = presence;
        }

        /// <summary>
        /// Writes one <see cref="NinoNullable{T}"/> as a <c>T?</c> (a presence byte, then the value when present) from
        /// its memory.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteOne(ref NinoNullable<T> value, ref Writer writer)
        {
            ref T presence = ref Unsafe.As<NinoNullable<T>, T>(ref value);
            if (Unsafe.As<T, byte>(ref presence) == NinoWire.Null)
            {
                writer.UnsafeWrite(NinoWire.Null);
                return;
            }

            ref byte dst = ref writer.Reserve(1 + NinoLayout<T>.Size);
            dst = NinoWire.NotNull;
            if (Wide)
            {
                // The value's two words, loaded before either is stored.
                ref byte memory = ref Unsafe.As<NinoNullable<T>, byte>(ref value);
                ulong value0 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref memory, 16));
                ulong value1 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref memory, 24));
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, 1), value0 & WideKeep0);
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, 9), value1 & WideKeep1);
                return;
            }

            NinoLayout<T>.Write(ref Unsafe.Add(ref dst, 1), in Unsafe.Add(ref presence, 1));
        }

        /// <summary>
        /// Stores a <see cref="Wide"/> value that has one, read from <paramref name="source"/> (its 16 bytes on the
        /// wire), into <paramref name="value"/>: four words stored, none read back.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static void StoreWide(ref NinoNullable<T> value, ref byte source)
        {
            ref byte memory = ref Unsafe.As<NinoNullable<T>, byte>(ref value);
            ulong value0 = Unsafe.ReadUnaligned<ulong>(ref source);
            ulong value1 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, 8));
            Unsafe.WriteUnaligned(ref memory, 1UL);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref memory, 8), 0UL);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref memory, 16), value0);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref memory, 24), value1);
        }

        /// <summary>
        /// Reads a <see cref="NinoNullable{T}"/> run whose count was checked: as a <c>T?</c> run (presence bytes
        /// checked, then one block copy; in one pass into a <paramref name="fresh"/> array) where <see cref="Words"/>,
        /// else slot by slot into values this type makes, which are clean whatever the data's padding held (also in
        /// <see cref="NinoSettings.Untrusted"/> mode, which then never keeps the data's padding). Every byte of
        /// <paramref name="destination"/> is written, so it may be uninitialized memory.
        /// </summary>
        public static void Read(Span<NinoNullable<T>> destination, ref Reader reader, bool fresh = false)
        {
            // The same memory as T?, so the T? read checks the presence bytes and copies the slots.
            if (Words && !NinoLimits.Untrusted)
            {
                ref T? item = ref Unsafe.As<NinoNullable<T>, T?>(ref MemoryMarshal.GetReference(destination));
#if NET8_0_OR_GREATER
                if (fresh && destination.Length >= 128 && TryReadFresh(ref item, destination.Length, ref reader))
                    return;
#endif
                ReadPrechecked(ref item, destination.Length, ref reader);
                return;
            }

            ref byte src = ref reader.UnsafeAdvance(destination.Length * Stride);
            if (destination.IsEmpty)
                return;
            NinoChecks.Flags(ref src, destination.Length, Stride);
            ref NinoNullable<T> first = ref MemoryMarshal.GetReference(destination);
            int count = destination.Length;
#if NET8_0_OR_GREATER
            if (PackWidth != 0 && !NinoLimits.Untrusted)
            {
                int done = ReadPacked(ref Unsafe.As<NinoNullable<T>, byte>(ref first), ref src, count);
                count -= done;
                first = ref Unsafe.Add(ref first, done);
                src = ref Unsafe.Add(ref src, done * Stride);
            }

            if (WindowWidth != 0 && !Wide && !NinoLimits.Untrusted)
            {
                ReadWindows(ref Unsafe.As<NinoNullable<T>, byte>(ref first), ref src, count);
                return;
            }
#endif

            if (Wide && !NinoLimits.Untrusted)
            {
                ReadWide(ref Unsafe.As<NinoNullable<T>, byte>(ref first), ref src, count);
                return;
            }

            for (int i = 0; i < count; i++)
            {
                ref byte slot = ref Unsafe.Add(ref src, i * Stride);
                // Stores only: a value built in a local and copied here is read back over the stores that made it.
                ref T presence = ref Unsafe.As<NinoNullable<T>, T>(ref Unsafe.Add(ref first, i));
                presence = default;
                Unsafe.As<T, byte>(ref presence) = slot;
                Unsafe.Add(ref presence, 1) = slot != NinoWire.Null ? NinoLayout<T>.Read(ref Unsafe.Add(ref slot, Offset)) : default;
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
        internal static bool TryReadFresh(T?[] destination, int length, ref Reader reader) =>
            TryReadFresh(ref NinoUnsafe.GetArrayDataReference(destination), length, ref reader);

        /// <summary>
        /// <see cref="TryReadFresh(T?[], int, ref Reader)"/> from <paramref name="item"/>: also a new
        /// <see cref="NinoNullable{T}"/> array, its memory taken as <c>T?</c> (<see cref="Words"/>).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool TryReadFresh(ref T? item, int length, ref Reader reader)
        {
            if (!Vector128.IsHardwareAccelerated)
                return false;

            int stride = Unsafe.SizeOf<T?>();
            int bytes = length * stride;
            ref byte target = ref Unsafe.As<T?, byte>(ref item);
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
        /// <c>T?</c> (<see cref="Words"/>).
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
