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

        /// <summary>
        /// Whether a run may be written over itself (<see cref="WriteSlots(ref byte, ReadOnlySpan{T?})"/> with the
        /// destination its own source): the slots are the values' memory and tile every vector (2, 4, 8 or 16
        /// bytes), so each step reads exactly the bytes it writes and writing a step twice changes nothing. The
        /// kernels of other strides store whole vectors past their last whole element and read those bytes again
        /// from the source in the next step.
        /// </summary>
        internal static readonly bool InPlace = Raw && Stride is 2 or 4 or 8 or 16;

#if NET8_0_OR_GREATER
        /// <summary>Whether raw runs are read by a loop that checks their flags while it copies (ReadPrechecked).</summary>
        private static readonly bool FusesCheck = Vector128.IsHardwareAccelerated &&
                                                  (NinoSimd.Amd || AdvSimd.Arm64.IsSupported);
#endif

        /// <summary>Whether T holds a <c>Nullable</c> itself: only the masked copy clears a value inside a value.</summary>
        private static readonly bool HoldsNullables = NinoLayout<T>.Info.Absent != null;

        /// <summary>
        /// One element as a masked copy takes it: the flag and T's bytes kept, the padding between them and T's own
        /// cleared, and T (with any <c>Nullable</c> inside it) cleared where its flag is 0
        /// (<see cref="NinoMask.Copy"/>): what writes the runs of strides the kernels below do not take where a
        /// vector clears across elements, and every run of a T that holds a <c>Nullable</c> itself.
        /// </summary>
        private static readonly NinoMask Mask = BuildMask(true);

        private static NinoMask BuildMask(bool withAbsent)
        {
            var mask = new byte[Stride];
            mask[0] = 0xFF;
            var value = NinoLayout<T>.Info.Mask;
            for (int i = 0; i < NinoLayout<T>.WireSize; i++)
                mask[Offset + i] = value == null ? (byte)0xFF : value[i];
            // The element's own value first, then those of a T holding a Nullable, past the element's flag: outer
            // before inner, as NinoLayout.Info.Absent lists them.
            var inner = NinoLayout<T>.Info.Absent ?? Array.Empty<int>();
            var absent = new int[3 + inner.Length];
            absent[1] = Offset;
            absent[2] = NinoLayout<T>.WireSize;
            for (int i = 0; i < inner.Length; i += 3)
            {
                absent[3 + i] = Offset + inner[i];
                absent[4 + i] = Offset + inner[i + 1];
                absent[5 + i] = inner[i + 2];
            }

            return NinoMask.Create(mask, withAbsent ? absent : null);
        }

        /// <summary>
        /// The element's padding alone (null without any), for runs no vector clears: copied with it, then cleared
        /// by <see cref="ClearAbsent"/>, whose sizes are constants here. Through <see cref="Mask"/>'s list of
        /// values instead, every run of a host without vectors (and of the netstandard2.1 build) took 1.3 to 2
        /// times as long (1,000 <c>int?</c> 1,820 ns against 1,110, <c>Guid?</c> 5,200 against 3,600, local,
        /// <c>DOTNET_EnableHWIntrinsic=0</c>).
        /// </summary>
        private static readonly NinoMask Padding = HoldsNullables ? null : BuildMask(false);

        /// <summary>
        /// Zeroes the value of each element written without one: memory a <c>T?</c> was never assigned in (a rented or
        /// uninitialized array) may hold old bytes behind a cleared flag, and the wire holds zeros there (spec §6.2).
        /// </summary>
        // Out of line: a pass over the flags after the block copy, for the runs no kernel takes.
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

        /// <summary>
        /// Whether the masked copy clears absent values in its one pass, a 64-byte vector at a time, whatever the
        /// stride (<see cref="NinoMask.ClearsByVector"/>: AVX-512 VBMI, with GFNI on .NET 10).
        /// </summary>
#if NET8_0_OR_GREATER
        private static readonly bool ClearedByVector = Mask.ClearsByVector;
#else
        private const bool ClearedByVector = false;
#endif

#if NET8_0_OR_GREATER
        /// <summary>
        /// Strides whose elements sit at fixed places in every vector of a period: 2, 4, 8 and 16 bytes (<c>byte?</c>
        /// to <c>long?</c>, <c>double?</c>, <c>DateTime?</c>) tile a vector, so each element is a lane (or two 8-byte
        /// lanes); 24 bytes (<c>decimal?</c>, a 16-byte struct with 8-byte alignment) repeats every three vectors of
        /// any width, each element three 8-byte lanes with its flag in the first.
        /// </summary>
        private static readonly bool Lanes = Vector128.IsHardwareAccelerated && !HoldsNullables &&
                                             (Stride == 2 || Stride == 4 || Stride == 8 || Stride == 16 ||
                                              Stride == 24);

        /// <summary>
        /// Whether every byte of an element is kept whole or cleared whole (no byte keeps only some of its bits):
        /// what a kernel that clears by a multiplication or by its element's flag alone needs.
        /// </summary>
        private static readonly bool WholeBytes = KeepsWholeBytes();

        private static bool KeepsWholeBytes()
        {
            foreach (byte keep in Keeps())
                if (keep != 0 && keep != 0xFF)
                    return false;
            return true;
        }

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
        // Strides of 2 to 8 bytes too, with AVX-512 (VL) as well: their lane test (the flags ANDed, vptestm, a masked
        // AND) is three operations to this two, 1,000 int? 99-115 ns against 117-125 (local AVX-512 Xeon, in process).
        private static readonly bool Multiplies = Gfni.IsSupported && WholeBytes &&
                                                  (Stride is 2 or 4 or 8 or 16);

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
        /// <summary>
        /// For a 24-byte stride, by where a 64-byte line of the destination starts in an element (0, 4, ... 20 bytes
        /// in, a sixth each): the dword permutation (vpermt2d) of the line before and the line that gives each dword
        /// its element's first dword (the flag's), and the bytes the wire keeps there.
        /// </summary>
        private static readonly Vector512<uint>[] GatherIndex = Stride == 24 ? GatherIndexes() : null;

        private static readonly Vector512<byte>[] GatherKeep = Stride == 24 ? GatherKeeps() : null;

        private static Vector512<uint>[] GatherIndexes()
        {
            var rows = new Vector512<uint>[Stride / 4];
            var dwords = new uint[16];
            for (int p = 0; p < Stride; p += 4)
            {
                for (int d = 0; d < 16; d++)
                    dwords[d] = (uint)(16 + d - (p + 4 * d) % Stride / 4);
                rows[p / 4] = Vector512.Create(dwords);
            }

            return rows;
        }

        private static Vector512<byte>[] GatherKeeps()
        {
            var rows = new Vector512<byte>[Stride / 4];
            var keep = Keeps();
            var bytes = new byte[64];
            for (int p = 0; p < Stride; p += 4)
            {
                for (int b = 0; b < 64; b++)
                    bytes[b] = keep[(p + b) % Stride];
                rows[p / 4] = Vector512.Create(bytes);
            }

            return rows;
        }

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
            if (AdvSimd.Arm64.IsSupported)
            {
#if NET10_0_OR_GREATER
                if (Halves && length >= 32)
                {
                    CopyHalves(ref destination, ref source, length);
                    return;
                }
#endif
                if (Stride <= 16)
                    CopySpread(ref destination, ref source, length);
                else
                    CopyWords(ref destination, ref source, length);
                return;
            }

            if (NinoSimd.Prefer512 && length >= 4 * (nuint)LaneStep)
                CopyLanes<Vector512<byte>, Lanes512>(ref destination, ref source, length);
            else if (Vector256.IsHardwareAccelerated && length >= 2 * (nuint)LaneStep)
                CopyLanes<Vector256<byte>, Lanes256>(ref destination, ref source, length);
            else
                CopyLanes<Vector128<byte>, Lanes128>(ref destination, ref source, length);
        }

        /// <summary>
        /// The bytes of a 16-byte vector's whole elements, for strides up to 16 bytes: the vector where they tile
        /// it (2, 4, 8, 16), else as many elements as it holds (15 bytes of 3 or 5, 12 of 6 or 12, ...).
        /// </summary>
        private static readonly int SpreadStep = Stride <= 16 ? 16 / Stride * Stride : 0;

        /// <summary>
        /// For strides up to 16 bytes, the shuffle (tbl, pshufb) that gives each byte of a 16-byte vector its
        /// element's flag where a present element keeps it, and zero (an index out of the table) in the padding
        /// and past the vector's whole elements.
        /// </summary>
        private static readonly Vector128<byte> SpreadFlags = Spread();

        private static Vector128<byte> Spread()
        {
            var bytes = new byte[16];
            bytes.AsSpan().Fill(0xFF);
            if (Stride <= 16)
            {
                var keep = Keeps();
                for (int i = 0; i < SpreadStep; i++)
                    if (keep[i % Stride] != 0)
                        bytes[i] = (byte)(i - i % Stride);
            }

            return Vector128.Create(bytes);
        }

        /// <summary>
        /// Whether a run of a stride under 16 bytes that does not tile a vector (3, 5, 6, 7, 9 to 15: a struct of
        /// two bytes, of an int and a byte packed, of three ints packed to 2) is cleared by <see cref="CopySpread"/>,
        /// a 16-byte vector's whole elements a step: where no wider vector clears it across elements
        /// (<see cref="ClearedByVector"/>) and a shuffle exists (SSSE3, Arm64).
        /// </summary>
        private static readonly bool Spreads = Stride < 16 && SpreadStep != 16 && !HoldsNullables && WholeBytes &&
                                               !ClearedByVector &&
                                               (Ssse3.IsSupported || AdvSimd.Arm64.IsSupported);

        /// <summary>
        /// Copies the elements of <paramref name="total"/> bytes (16 or more) that 16-byte vectors hold whole, for
        /// strides up to 16 bytes, and returns the bytes it wrote: each byte takes its element's flag (tbl, pshufb)
        /// and is multiplied by it (<see cref="Spread(Vector128{byte}, Vector128{byte})"/>). Strides that tile the
        /// vector (Arm64's <see cref="CopyLanes"/>): every element, vectors loaded and stored in pairs (ldp, stp),
        /// the last one overlapping the one before it. Other strides: a store writes zeros past its whole elements,
        /// which the next step writes again, and the caller copies the elements after the last whole vector.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static nuint CopySpread(ref byte destination, ref byte source, nuint total)
        {
            var spread = SpreadFlags;
            if (SpreadStep != 16)
            {
                nuint step = (nuint)SpreadStep, i = 0;
                for (; i + 16 <= total; i += step)
                    Spread(Vector128.LoadUnsafe(ref source, i), spread).StoreUnsafe(ref destination, i);
                return i;
            }

            // Cursors bumped, so the two vectors of a step are at constant offsets (ldp, stp).
            nuint left = total;
            for (; left >= 32; left -= 32)
            {
                var a = Spread(Vector128.LoadUnsafe(ref source), spread);
                var b = Spread(Vector128.LoadUnsafe(ref source, 16), spread);
                a.StoreUnsafe(ref destination);
                b.StoreUnsafe(ref destination, 16);
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
            return total;
        }

        /// <summary><see cref="CopySpread"/> out of line, for <see cref="WriteSlots(ref byte, ReadOnlySpan{T?})"/>.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int CopySpreads(ref byte destination, ref byte source, int total) =>
            (int)CopySpread(ref destination, ref source, (nuint)total);

        /// <summary>
        /// A vector of elements as the wire holds them, in two operations: each byte multiplied by its element's
        /// flag (times 1 it is kept, times 0 cleared; a flag is 0 or 1 in every <c>Nullable</c> the runtime makes),
        /// the padding by 0. Arm64: tbl and a byte multiplication, where testing the flags (cmtst) and an AND were
        /// three operations for the two vector pipes of a Neoverse core, which the store's data takes too (llvm-mca,
        /// neoverse-n2, 32 bytes a step: 3.0 cycles against 4.0, a copy 1.0; neoverse-n1 3.0 against 4.0, neoverse-v2
        /// 2.0 against 2.3, apple-m1 2.0 against 2.3; not timed on hardware). x64: pshufb, then the multiplication
        /// in GF(2^8) with GFNI, else a compare and an AND NOT.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector128<byte> Spread(Vector128<byte> value, Vector128<byte> spread)
        {
            if (AdvSimd.Arm64.IsSupported)
                return AdvSimd.Multiply(value, AdvSimd.Arm64.VectorTableLookup(value, spread));
            if (Ssse3.IsSupported)
                return Times(value, Ssse3.Shuffle(value, spread));
            return value;
        }

        /// <summary><paramref name="value"/>'s bytes times <paramref name="by"/>'s, each 0 or 1: gf2p8mulb, else psignb.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector128<byte> Times(Vector128<byte> value, Vector128<byte> by)
        {
#if NET10_0_OR_GREATER
            if (Gfni.IsSupported)
                return Gfni.GaloisFieldMultiply(value, by);
#endif
            return Ssse3.Sign(value.AsSByte(), by.AsSByte()).AsByte();
        }

        /// <summary><see cref="Times(Vector128{byte}, Vector128{byte})"/> for 32-byte vectors: vgf2p8mulb, else vpsignb.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector256<byte> Times(Vector256<byte> value, Vector256<byte> by)
        {
#if NET10_0_OR_GREATER
            if (Gfni.V256.IsSupported)
                return Gfni.V256.GaloisFieldMultiply(value, by);
#endif
            return Avx2.Sign(value.AsSByte(), by.AsSByte()).AsByte();
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
        /// registers: each element's flag negated is the mask of its words (0 or all ones: a flag is 0 or 1 in
        /// every <c>Nullable</c> the runtime makes), loaded and stored in pairs (ldp, stp), where three vectors of
        /// tbl and a multiplication wait for the vector pipes (llvm-mca, neoverse-n2, two elements: 2.8 cycles
        /// against 4.5; the mask as a sign shifted down, three operations instead of one, 3.9).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void CopyWords(ref byte destination, ref byte source, nuint total)
        {
            // Cursors bumped by the stride, so each element's words are at constant offsets, read before any is written
            // (ldp, stp); a ternary on the flag compiled to a branch per element.
            ref byte end = ref Unsafe.Add(ref source, total);
            while (Unsafe.IsAddressLessThan(ref source, ref end))
            {
                ulong w0 = Unsafe.ReadUnaligned<ulong>(ref source);
                ulong w1 = Stride >= 16 ? Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, 8)) : 0;
                ulong w2 = Stride == 24 ? Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, 16)) : 0;
                ulong flag = w0 & 0xFF;
                ulong present = 0UL - flag;
                Unsafe.WriteUnaligned(ref destination, FlagWordOnly ? flag : w0 & KeepWord0 & present);
                if (Stride >= 16)
                    Unsafe.WriteUnaligned(ref Unsafe.Add(ref destination, 8), w1 & KeepWord1 & present);
                if (Stride == 24)
                    Unsafe.WriteUnaligned(ref Unsafe.Add(ref destination, 16), w2 & KeepWord2 & present);
                source = ref Unsafe.Add(ref source, Stride);
                destination = ref Unsafe.Add(ref destination, Stride);
            }
        }

#if NET10_0_OR_GREATER
        /// <summary>
        /// Whether Arm64 writes a run as its elements' halves (<see cref="CopyHalves"/>): strides of 8 and 16 bytes whose
        /// first half is the flag and padding, the second the value (<c>int?</c>, <c>float?</c>, <c>long?</c>,
        /// <c>double?</c>, <c>DateTime?</c>, ...).
        /// </summary>
        private static readonly bool Halves = (Stride == 8 || Stride == 16) && Offset == Stride / 2 && WholeBytes &&
                                              !HoldsNullables;

        /// <summary>The bytes the value half of an element keeps (<see cref="Halves"/>), as a lane of its width.</summary>
        private static readonly ulong KeepHalf = Stride == 16 ? KeepWord1 : Stride == 8 ? KeepWord0 >> 32 : 0;

        /// <summary>
        /// <see cref="CopyLanes"/> on Arm64 for <see cref="Halves"/>, 32 bytes (two or four elements) a step: a structure
        /// load (ld2) parts the flag halves from the value halves, a test of each flag against its byte (cmtst) is the
        /// mask of its value, one AND each, and a structure store (st2) writes them back interleaved; three vector
        /// operations a step, where tbl and a byte multiplication took four, the multiplication on one pipe of a
        /// Neoverse core. At least 32 bytes; the last step overlaps the one before it (whole elements: 32 is a multiple
        /// of the stride).
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static unsafe void CopyHalves(ref byte destination, ref byte source, nuint total)
        {
            fixed (byte* d = &destination)
            fixed (byte* s = &source)
            {
                nuint end = total - 32;
                nuint i = 0;
                for (; i + 32 < end; i += 64)
                {
                    Half(d + i, s + i);
                    Half(d + i + 32, s + i + 32);
                }

                for (; i < end; i += 32)
                    Half(d + i, s + i);
                Half(d + end, s + end);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static unsafe void Half(byte* destination, byte* source)
        {
            if (Stride == 16)
            {
                var (flags, values) = AdvSimd.Arm64.Load2xVector128AndUnzip((ulong*)source);
                var flag = flags & Vector128.Create(0xFFUL);
                var present = AdvSimd.Arm64.CompareTest(flags, Vector128.Create(0xFFUL));
                if (KeepHalf != ulong.MaxValue)
                    present &= Vector128.Create(KeepHalf);
                AdvSimd.Arm64.StoreVectorAndZip((ulong*)destination, (flag, values & present));
            }
            else
            {
                var (flags, values) = AdvSimd.Arm64.Load2xVector128AndUnzip((uint*)source);
                var flag = flags & Vector128.Create(0xFFu);
                var present = AdvSimd.CompareTest(flags, Vector128.Create(0xFFu));
                if ((uint)KeepHalf != uint.MaxValue)
                    present &= Vector128.Create((uint)KeepHalf);
                AdvSimd.Arm64.StoreVectorAndZip((uint*)destination, (flag, values & present));
            }
        }
#endif

        /// <summary>
        /// For a 24-byte stride with a shuffle within 16-byte lanes (pshufb), the indices that give each byte an element
        /// keeps its element's flag, for the three vectors of a period (<paramref name="k"/>) of
        /// <paramref name="width"/> bytes: from the vector itself (the flag in the byte's own 16-byte lane) or, with
        /// <paramref name="previous"/>, from the vector loaded 16 bytes earlier (the flag in the lane before); zero
        /// (an index with the top bit) elsewhere.
        /// </summary>
        private static Vector256<byte> Spread24(int width, int k, bool previous)
        {
            var bytes = new byte[32];
            bytes.AsSpan().Fill(0x80);
            if (Stride != 24)
                return Vector256.Create(bytes);
            var keep = Keeps();
            for (int p = 0; p < width; p++)
            {
                int at = k * width + p, flag = at - at % 24, lane = k * width + (p & ~15);
                if (keep[at % 24] == 0)
                    continue;
                if (flag >= lane)
                {
                    if (!previous)
                        bytes[p] = (byte)(flag - lane);
                }
                else if (previous)
                {
                    bytes[p] = (byte)(flag - (lane - 16));
                }
            }

            return Vector256.Create(bytes);
        }

        private static readonly Vector256<byte> Own0 = Spread24(32, 0, false), Own1 = Spread24(32, 1, false),
            Own2 = Spread24(32, 2, false), Before0 = Spread24(32, 0, true), Before1 = Spread24(32, 1, true),
            Before2 = Spread24(32, 2, true);

        private static readonly Vector128<byte> Own16_0 = Spread24(16, 0, false).GetLower(),
            Own16_1 = Spread24(16, 1, false).GetLower(), Own16_2 = Spread24(16, 2, false).GetLower(),
            Before16_0 = Spread24(16, 0, true).GetLower(), Before16_1 = Spread24(16, 1, true).GetLower(),
            Before16_2 = Spread24(16, 2, true).GetLower();

        /// <summary>
        /// The vectors <see cref="CopyPieces"/> moves an element in: 64 bytes where <see cref="NinoSimd.Prefer512"/>
        /// and the element holds one, 32 where 32-byte vectors copy (AVX2), 16 (SSE2, Arm64); 0 for a stride of 16
        /// bytes or less, or no vectors.
        /// </summary>
        private static readonly int PieceWidth = !Vector128.IsHardwareAccelerated || Stride <= 16 ? 0
            : Stride >= 64 && NinoSimd.Prefer512 ? 64
            : Vector256.IsHardwareAccelerated ? 32 : 16;

        /// <summary>
        /// Whether a run of any other stride from 17 bytes (20, 28, 40, 72, ...: a <c>Guid?</c>, a struct of five
        /// or nine words) is written by <see cref="CopyPieces"/>, an element at a time: where no vector clears it
        /// across elements (<see cref="ClearedByVector"/>: it takes every flag that is at most 64 bytes before its
        /// value's end).
        /// </summary>
        private static readonly bool Pieces = PieceWidth != 0 && !Lanes && !HoldsNullables && WholeBytes &&
                                              !ClearedByVector;

        /// <summary>The whole vectors of <see cref="PieceWidth"/> bytes in one element, and the bytes after them.</summary>
        private static readonly int PieceVectors = PieceWidth == 0 ? 0 : Stride / PieceWidth;

        private static readonly int PieceTail = PieceWidth == 0 ? 0 : Stride % PieceWidth;

        /// <summary>
        /// Whether the bytes after an element's whole vectors are one more vector that runs at most 12 bytes into the
        /// next element (which writes them again), not narrower pieces that end with it: one store for up to four.
        /// Further ahead it lost (local, AVX-512 Xeon with <c>DOTNET_EnableAVX512=0</c>, 1,000 elements): 40 bytes as
        /// two 32-byte vectors, 24 past the element, 2,430 ns against 1,810 for a vector and a word; 48 bytes as two,
        /// 16 past, 2,520 against 2,050; 72 bytes as three, 24 past, 3,620 against 2,500.
        /// </summary>
        private static readonly bool PieceRunsAhead = PieceTail != 0 && PieceWidth - PieceTail <= 12;

        /// <summary>
        /// The bytes an element keeps over its whole vectors and the one after them (0xFF past the element: the
        /// next one's bytes, which it writes again).
        /// </summary>
        // Built only where Pieces holds: a type that never takes the pieces allocates nothing for them.
        private static readonly byte[] PieceKeeps = Pieces ? PieceKeepTable() : null;

        private static byte[] PieceKeepTable()
        {
            var bytes = new byte[Math.Max((PieceVectors + 1) * PieceWidth, Stride)];
            bytes.AsSpan().Fill(0xFF);
            Keeps().CopyTo(bytes, 0);
            return bytes;
        }

        /// <summary>The bytes an element keeps from 0, 1, 2 and 3 piece widths on (64 bytes each).</summary>
        private static readonly Vector512<byte> PieceKeep0 = PieceKeepRow(0), PieceKeep1 = PieceKeepRow(1),
            PieceKeep2 = PieceKeepRow(2), PieceKeep3 = PieceKeepRow(3);

        private static Vector512<byte> PieceKeepRow(int k)
        {
            if (!Pieces)
                return Vector512<byte>.AllBitsSet;
            var bytes = new byte[64];
            bytes.AsSpan().Fill(0xFF);
            for (int i = 0; i < 64 && k * PieceWidth + i < Stride; i++)
                bytes[i] = PieceKeeps[k * PieceWidth + i];
            return Vector512.Create(bytes);
        }

        /// <summary>Where the last byte an element clears ends: pieces from there on keep every byte.</summary>
        private static readonly int PieceKeepEnd = Pieces ? Array.LastIndexOf(Keeps(), (byte)0) + 1 : 0;

        /// <summary>
        /// Copies <paramref name="count"/> elements (one or more) as the wire holds them, each in vectors of
        /// <see cref="PieceWidth"/> bytes, then either one more that runs into the next element
        /// (<see cref="PieceRunsAhead"/>; never the last one, so nothing is written past the run) or narrower
        /// pieces that end with it (a vector of 32 or 16 bytes, then a word of 8, 4, 2 and 1 bytes as its length
        /// needs). A vector is multiplied by the element's flag spread from its first byte (vpbroadcastb, pshufb,
        /// dup; a flag is 0 or 1 in every <c>Nullable</c> the runtime makes), ANDed with the bytes it keeps up to
        /// the last one it clears: two operations, one past it; a word is ANDed with 0 - flag. Stores in address
        /// order, each element read from the source, never from the destination (the store before may have run into
        /// it).
        /// </summary>
        // Fully optimized from the first call (the class is initialized by then, so its fields are constants). The
        // loop before it (wip/simd-coverage's pieces, 453b953c) spread the flag from a general register, looped over
        // an element's vectors and tested at every piece whether it kept every byte, and AVX2 left strides of 17 to
        // 31 bytes to the masked copy and a pass over the flags. Local, 1,000 elements, ns, before / this
        // (DOTNET_EnableAVX512=0): 20 bytes 1,170-1,330 / 830-990, 28 bytes 1,470 / 1,100, 32 bytes 1,380 / 1,230,
        // 48 bytes 2,030-2,190 / 1,680, 72 bytes 2,520-2,630 / 2,230-2,250; DOTNET_EnableAVX2=0: 20 bytes 1,610-1,740
        // / 740-940, 28 bytes 2,930 / 1,020, 48 bytes 3,100 / 1,730, 72 bytes 4,240 / 3,240.
        [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
        private static void CopyPieces(ref byte destination, ref byte source, int count)
        {
            ref byte keep = ref MemoryMarshal.GetArrayDataReference(PieceKeeps);
            ref byte last = ref Unsafe.Add(ref source, (nuint)(uint)(count - 1) * (nuint)Stride);
            while (Unsafe.IsAddressLessThan(ref source, ref last))
            {
                Piece(ref destination, ref source, ref keep, PieceRunsAhead);
                source = ref Unsafe.Add(ref source, Stride);
                destination = ref Unsafe.Add(ref destination, Stride);
            }

            Piece(ref destination, ref source, ref keep, false);
        }

        /// <summary>
        /// One element of <see cref="CopyPieces"/>, its last vector running ahead when <paramref name="ahead"/>.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void Piece(ref byte destination, ref byte source, ref byte keep, bool ahead)
        {
            var head = PieceVectors == 0 ? Vector128.LoadUnsafe(ref source) : default;
            Vector256<byte> present32 = default;
            Vector128<byte> present16;
            if (PieceWidth == 64)
            {
                var present64 = Whole512(ref destination, ref source, ref keep, head, ahead);
                if (ahead)
                    return;
                present32 = present64.GetLower();
                present16 = present32.GetLower();
            }
            else if (PieceWidth == 32)
            {
                present32 = Whole256(ref destination, ref source, ref keep, head, ahead);
                if (ahead)
                    return;
                present16 = present32.GetLower();
            }
            else
            {
                present16 = Whole128(ref destination, ref source, ref keep, head, ahead);
                if (ahead)
                    return;
            }

            nuint at = (nuint)(PieceWidth * PieceVectors);
            if (PieceWidth == 64 && (PieceTail & 32) != 0)
            {
                Kept256(Vector256.LoadUnsafe(ref source, at), present32 & Vector256.LoadUnsafe(ref keep, at))
                    .StoreUnsafe(ref destination, at);
                at += 32;
            }

            if (PieceWidth >= 32 && (PieceTail & 16) != 0)
            {
                Kept128(Vector128.LoadUnsafe(ref source, at), present16 & Vector128.LoadUnsafe(ref keep, at))
                    .StoreUnsafe(ref destination, at);
                at += 16;
            }

            ulong present = 0UL - source;
            if ((Stride & 8) != 0)
            {
                ulong word = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, at)) & present;
                word &= Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref keep, at));
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref destination, at), word);
                at += 8;
            }

            if ((Stride & 4) != 0)
            {
                uint word = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref source, at)) & (uint)present;
                word &= Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref keep, at));
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref destination, at), word);
                at += 4;
            }

            if ((Stride & 2) != 0)
            {
                uint word = Unsafe.ReadUnaligned<ushort>(ref Unsafe.Add(ref source, at)) & (uint)present;
                word &= Unsafe.ReadUnaligned<ushort>(ref Unsafe.Add(ref keep, at));
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref destination, at), (ushort)word);
                at += 2;
            }

            if ((Stride & 1) != 0)
                Unsafe.Add(ref destination, at) =
                    (byte)(Unsafe.Add(ref source, at) & (uint)present & Unsafe.Add(ref keep, at));
        }

        /// <summary>
        /// The element's whole vectors of 64 bytes, and the one after them when <paramref name="ahead"/>, up to four
        /// loaded before any is stored, each multiplied by the element's flag spread over it (from the first one
        /// when there is one, else from <paramref name="head"/>), ANDed up to the last byte the element clears with
        /// the bytes it keeps; returns the flag spread.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector512<byte> Whole512(ref byte destination, ref byte source, ref byte keep,
            Vector128<byte> head, bool ahead)
        {
            int n = PieceVectors + (ahead ? 1 : 0);
            Vector512<byte> a = default, b = default, c = default, d = default;
            if (n > 0)
            {
                a = Vector512.LoadUnsafe(ref source);
                head = a.GetLower().GetLower();
            }

            var present = Spread512(head);
            if (n > 1)
                b = Vector512.LoadUnsafe(ref source, 64);
            if (n > 2)
                c = Vector512.LoadUnsafe(ref source, 128);
            if (n > 3)
                d = Vector512.LoadUnsafe(ref source, 192);
            if (n > 0)
                (PieceKeepEnd > 0 ? Kept512(a, present & PieceKeep0) : Kept512(a, present))
                    .StoreUnsafe(ref destination);
            if (n > 1)
                (PieceKeepEnd > 64 ? Kept512(b, present & PieceKeep1) : Kept512(b, present))
                    .StoreUnsafe(ref destination, 64);
            if (n > 2)
                (PieceKeepEnd > 128 ? Kept512(c, present & PieceKeep2) : Kept512(c, present))
                    .StoreUnsafe(ref destination, 128);
            if (n > 3)
                (PieceKeepEnd > 192 ? Kept512(d, present & PieceKeep3) : Kept512(d, present))
                    .StoreUnsafe(ref destination, 192);
            for (nuint at = 256; at < (nuint)(64 * n); at += 64)
                Kept512(Vector512.LoadUnsafe(ref source, at), present & Vector512.LoadUnsafe(ref keep, at))
                    .StoreUnsafe(ref destination, at);
            return present;
        }

        /// <summary>
        /// The element's whole vectors of 32 bytes, and the one after them when <paramref name="ahead"/>, up to four
        /// loaded before any is stored, each multiplied by the element's flag spread over it (from the first one
        /// when there is one, else from <paramref name="head"/>), ANDed up to the last byte the element clears with
        /// the bytes it keeps; returns the flag spread.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector256<byte> Whole256(ref byte destination, ref byte source, ref byte keep,
            Vector128<byte> head, bool ahead)
        {
            int n = PieceVectors + (ahead ? 1 : 0);
            Vector256<byte> a = default, b = default, c = default, d = default;
            if (n > 0)
            {
                a = Vector256.LoadUnsafe(ref source);
                head = a.GetLower();
            }

            var present = Spread256(head);
            if (n > 1)
                b = Vector256.LoadUnsafe(ref source, 32);
            if (n > 2)
                c = Vector256.LoadUnsafe(ref source, 64);
            if (n > 3)
                d = Vector256.LoadUnsafe(ref source, 96);
            if (n > 0)
                (PieceKeepEnd > 0 ? Kept256(a, present & PieceKeep0.GetLower()) : Kept256(a, present))
                    .StoreUnsafe(ref destination);
            if (n > 1)
                (PieceKeepEnd > 32 ? Kept256(b, present & PieceKeep1.GetLower()) : Kept256(b, present))
                    .StoreUnsafe(ref destination, 32);
            if (n > 2)
                (PieceKeepEnd > 64 ? Kept256(c, present & PieceKeep2.GetLower()) : Kept256(c, present))
                    .StoreUnsafe(ref destination, 64);
            if (n > 3)
                (PieceKeepEnd > 96 ? Kept256(d, present & PieceKeep3.GetLower()) : Kept256(d, present))
                    .StoreUnsafe(ref destination, 96);
            for (nuint at = 128; at < (nuint)(32 * n); at += 32)
                Kept256(Vector256.LoadUnsafe(ref source, at), present & Vector256.LoadUnsafe(ref keep, at))
                    .StoreUnsafe(ref destination, at);
            return present;
        }

        /// <summary>
        /// The element's whole vectors of 16 bytes, and the one after them when <paramref name="ahead"/>, up to four
        /// loaded before any is stored, each multiplied by the element's flag spread over it (from the first one
        /// when there is one, else from <paramref name="head"/>), ANDed up to the last byte the element clears with
        /// the bytes it keeps; returns the flag spread.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector128<byte> Whole128(ref byte destination, ref byte source, ref byte keep,
            Vector128<byte> head, bool ahead)
        {
            int n = PieceVectors + (ahead ? 1 : 0);
            Vector128<byte> a = default, b = default, c = default, d = default;
            if (n > 0)
            {
                a = Vector128.LoadUnsafe(ref source);
                head = a;
            }

            var present = Spread128(head);
            if (n > 1)
                b = Vector128.LoadUnsafe(ref source, 16);
            if (n > 2)
                c = Vector128.LoadUnsafe(ref source, 32);
            if (n > 3)
                d = Vector128.LoadUnsafe(ref source, 48);
            if (n > 0)
                (PieceKeepEnd > 0 ? Kept128(a, present & PieceKeep0.GetLower().GetLower()) : Kept128(a, present))
                    .StoreUnsafe(ref destination);
            if (n > 1)
                (PieceKeepEnd > 16 ? Kept128(b, present & PieceKeep1.GetLower().GetLower()) : Kept128(b, present))
                    .StoreUnsafe(ref destination, 16);
            if (n > 2)
                (PieceKeepEnd > 32 ? Kept128(c, present & PieceKeep2.GetLower().GetLower()) : Kept128(c, present))
                    .StoreUnsafe(ref destination, 32);
            if (n > 3)
                (PieceKeepEnd > 48 ? Kept128(d, present & PieceKeep3.GetLower().GetLower()) : Kept128(d, present))
                    .StoreUnsafe(ref destination, 48);
            for (nuint at = 64; at < (nuint)(16 * n); at += 16)
                Kept128(Vector128.LoadUnsafe(ref source, at), present & Vector128.LoadUnsafe(ref keep, at))
                    .StoreUnsafe(ref destination, at);
            return present;
        }

        /// <summary>
        /// <paramref name="value"/>'s bytes kept where <paramref name="by"/>'s are 1, zero where they are 0: a
        /// multiplication (GFNI), else ANDed with 0 - by.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector512<byte> Kept512(Vector512<byte> value, Vector512<byte> by)
        {
#if NET10_0_OR_GREATER
            if (Gfni.V512.IsSupported)
                return Gfni.V512.GaloisFieldMultiply(value, by);
#endif
            return value & (Vector512<byte>.Zero - by);
        }

        /// <summary><see cref="Kept512"/> for 32 bytes: vgf2p8mulb, else vpsignb.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector256<byte> Kept256(Vector256<byte> value, Vector256<byte> by) => Times(value, by);

        /// <summary><see cref="Kept512"/> for 16 bytes: mul (Arm64), gf2p8mulb or psignb, else an AND.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector128<byte> Kept128(Vector128<byte> value, Vector128<byte> by)
        {
            if (AdvSimd.Arm64.IsSupported)
                return AdvSimd.Multiply(value, by);
            if (Ssse3.IsSupported)
                return Times(value, by);
            return value & (Vector128<byte>.Zero - by);
        }

        /// <summary>The first byte of <paramref name="head"/> in each of 64 bytes: vpbroadcastb.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector512<byte> Spread512(Vector128<byte> head) =>
            Avx512BW.IsSupported ? Avx512BW.BroadcastScalarToVector512(head) : default;

        /// <summary>The first byte of <paramref name="head"/> in each of 32 bytes: vpbroadcastb.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector256<byte> Spread256(Vector128<byte> head) =>
            Avx2.IsSupported ? Avx2.BroadcastScalarToVector256(head) : default;

        /// <summary>The first byte of <paramref name="head"/> in each of 16 bytes: dup, vpbroadcastb, pshufb.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector128<byte> Spread128(Vector128<byte> head)
        {
            if (AdvSimd.Arm64.IsSupported)
                return AdvSimd.DuplicateSelectedScalarToVector128(head, 0);
            if (Avx2.IsSupported)
                return Avx2.BroadcastScalarToVector128(head);
            if (Ssse3.IsSupported)
                return Ssse3.Shuffle(head, Vector128<byte>.Zero);
            return Vector128.Create(head.ToScalar());
        }

        /// <summary>
        /// The steps from which <see cref="CopyLanes{TVector, TLanes}"/> aligns its stores: below, the extra step costs
        /// more than the split stores.
        /// </summary>
        private const int AlignFrom = 8;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void CopyLanes<TVector, TLanes>(ref byte destination, ref byte source, nuint total)
            where TVector : struct
            where TLanes : struct, ILanes
        {
            var lanes = default(TLanes);
            nuint step = (nuint)(LaneStep * Unsafe.SizeOf<TVector>() / 16);
            nuint end = total - step;
            nuint i = 0;
            // Stores at whole vectors of the destination where whole elements lead there (for 24 bytes a step is
            // three vectors, and one or two elements may lead to a line's start): the first step as it lies, then
            // steps from the first aligned element on, which write its overlap again. A store that is not
            // aligned to its width straddles two cache lines, and the stores bound this loop.
            if (total >= AlignFrom * step)
            {
                nuint head = NinoUnsafe.BytesToAligned(ref destination, (nuint)Unsafe.SizeOf<TVector>());
                if (head % (nuint)Stride == 0)
                {
                    lanes.Copy(ref destination, ref source, 0);
                    i = head;
                }
                else if (typeof(TVector) == typeof(Vector512<byte>) && Stride != 24 && head % 4 == 0)
                {
                    // No element starts a line (a top-level run, 4 bytes of length into an 8-byte aligned array):
                    // the results moved onto the lines instead.
                    Lanes512.CopyRealigned(ref destination, ref source, total, head);
                    return;
                }
                else if (typeof(TVector) == typeof(Vector512<byte>) && Stride == 24 && head % 4 == 0)
                {
                    // The first and the last step as they lie, the lines between them gathered at the
                    // destination's phase.
                    lanes.Copy(ref destination, ref source, 0);
                    Lanes512.CopyGathered24(ref destination, ref source, total, head);
                    lanes.Copy(ref destination, ref source, end);
                    return;
                }
            }

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
            /// <summary>One vector of a stride that tiles it (2 to 16 bytes) as the wire holds it.</summary>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private static Vector512<byte> Cleaned(Vector512<byte> a)
            {
#if NET10_0_OR_GREATER
                if (Multiplies && Gfni.V512.IsSupported)
                    return Kept(a);
#endif
                return Present(a, Tile(a), Keep);
            }

            /// <summary>
            /// The elements of <paramref name="total"/> bytes (a stride that tiles the vector, 8 vectors or more) with
            /// every store but the first and the last two on the destination's 64-byte boundaries, where no element
            /// starts one (<paramref name="head"/>, the bytes to the first, a multiple of 4): each vector cleared
            /// where it lies, as the other loops do, then the bytes of two consecutive results that fall on one line
            /// of the destination joined by a dword permutation across both (vpermt2d, one uop on Ice Lake, Sapphire
            /// Rapids and Zen 4), with an index built once. Four operations a vector for strides up to 8 bytes
            /// (vpshufb, vgf2p8mulb, vpermt2d, the store) where the split store cost more: 1,000 <c>long?</c> 191
            /// ns against 250 unaligned and 164 where elements start a line, <c>Vec3?</c> 208 against 272,
            /// <c>List&lt;long?&gt;</c> 197 against 249, <c>int?</c> 117 against 130 (local, AVX-512 Xeon). Not 24
            /// bytes, whose kernel already spends four or five operations a vector and gained nothing (513 against
            /// 510 to 560). AVX2 has no one-uop dword shift across a 32-byte vector.
            /// </summary>
            [MethodImpl(MethodImplOptions.NoInlining)]
            public static void CopyRealigned(ref byte destination, ref byte source, nuint total, nuint head)
            {
                var index = Vector512.Create(0u, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15) +
                            Vector512.Create((uint)head >> 2);
                var previous = Cleaned(Vector512.LoadUnsafe(ref source));
                previous.StoreUnsafe(ref destination);
                nuint i = 0;
                // previous is the vector at i; the store at i + head takes its last bytes and the next one's first.
                for (; i + 256 + 64 <= total; i += 256)
                {
                    var b = Cleaned(Vector512.LoadUnsafe(ref source, i + 64));
                    var c = Cleaned(Vector512.LoadUnsafe(ref source, i + 128));
                    var d = Cleaned(Vector512.LoadUnsafe(ref source, i + 192));
                    var e = Cleaned(Vector512.LoadUnsafe(ref source, i + 256));
                    Avx512F.PermuteVar16x32x2(previous.AsUInt32(), index, b.AsUInt32()).AsByte()
                        .StoreUnsafe(ref destination, i + head);
                    Avx512F.PermuteVar16x32x2(b.AsUInt32(), index, c.AsUInt32()).AsByte()
                        .StoreUnsafe(ref destination, i + 64 + head);
                    Avx512F.PermuteVar16x32x2(c.AsUInt32(), index, d.AsUInt32()).AsByte()
                        .StoreUnsafe(ref destination, i + 128 + head);
                    Avx512F.PermuteVar16x32x2(d.AsUInt32(), index, e.AsUInt32()).AsByte()
                        .StoreUnsafe(ref destination, i + 192 + head);
                    previous = e;
                }

                for (; i + 128 <= total; i += 64)
                {
                    var next = Cleaned(Vector512.LoadUnsafe(ref source, i + 64));
                    Avx512F.PermuteVar16x32x2(previous.AsUInt32(), index, next.AsUInt32()).AsByte()
                        .StoreUnsafe(ref destination, i + head);
                    previous = next;
                }

                // Written up to i + head: the vector at i, and the last one, which ends the run (a whole element
                // from its start, as the stride divides 64), cover the rest.
                previous.StoreUnsafe(ref destination, i);
                Cleaned(Vector512.LoadUnsafe(ref source, total - 64)).StoreUnsafe(ref destination, total - 64);
            }

            /// <summary>
            /// The 64-byte lines of the destination from <c>head + 64</c> to the last whole one, for a 24-byte stride
            /// whose elements never start a line (<paramref name="head"/>, the bytes to the first, a multiple of 4;
            /// the caller writes the first and the last 192 bytes): each line loaded where it lies in the source, each
            /// dword given its element's first dword from the line before and the line (vpermt2d, one uop), the
            /// presence its bit 0 spread over the dword (vpslld 31, vpsrad 31: a flag is 0 or 1 in every
            /// <c>Nullable</c> the runtime makes) and ANDed with the line and the bytes kept (vpternlogd). One port-5
            /// operation a vector where the permutation of the flags (vpermb and two vpermt2b, three uops each on
            /// Intel), the AND, the multiplication and the realignment (vpermt2d) took four or more, and every store
            /// aligned. 1,000 <c>decimal?</c> in the kernel harness 381 ns against 475 for that form (a copy 248);
            /// it needs AVX-512F alone.
            /// </summary>
            [MethodImpl(MethodImplOptions.NoInlining)]
            public static void CopyGathered24(ref byte destination, ref byte source, nuint total, nuint head)
            {
                nuint at = head + 64;
                var previous = Vector512.LoadUnsafe(ref source, head);
                int p0 = (int)(at % 24), p1 = (int)((at + 64) % 24), p2 = (int)((at + 128) % 24);
                Vector512<uint> i0 = GatherIndex[p0 / 4], i1 = GatherIndex[p1 / 4], i2 = GatherIndex[p2 / 4];
                Vector512<byte> k0 = GatherKeep[p0 / 4], k1 = GatherKeep[p1 / 4], k2 = GatherKeep[p2 / 4];
                for (; at + 192 <= total; at += 192)
                {
                    var a = Vector512.LoadUnsafe(ref source, at);
                    var b = Vector512.LoadUnsafe(ref source, at + 64);
                    var c = Vector512.LoadUnsafe(ref source, at + 128);
                    Gathered(previous, a, i0, k0).StoreUnsafe(ref destination, at);
                    Gathered(a, b, i1, k1).StoreUnsafe(ref destination, at + 64);
                    Gathered(b, c, i2, k2).StoreUnsafe(ref destination, at + 128);
                    previous = c;
                }

                for (; at + 64 <= total; at += 64)
                {
                    var a = Vector512.LoadUnsafe(ref source, at);
                    Gathered(previous, a, i0, k0).StoreUnsafe(ref destination, at);
                    previous = a;
                    (i0, i1, i2) = (i1, i2, i0);
                    (k0, k1, k2) = (k1, k2, k0);
                }
            }

            /// <summary>One line of <see cref="CopyGathered24"/>.</summary>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private static Vector512<byte> Gathered(Vector512<byte> previous, Vector512<byte> line,
                Vector512<uint> index, Vector512<byte> keep)
            {
                var first = Avx512F.PermuteVar16x32x2(previous.AsUInt32(), index, line.AsUInt32());
                var present = Avx512F.ShiftRightArithmetic(Avx512F.ShiftLeftLogical(first, 31).AsInt32(), 31);
                return Avx512F.TernaryLogic(line, present.AsByte(), keep, 0x80);
            }


            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void Copy(ref byte destination, ref byte source, nuint offset)
            {
                var a = Vector512.LoadUnsafe(ref source, offset);
                if (Stride != 24)
                {
                    Cleaned(a).StoreUnsafe(ref destination, offset);
                    return;
                }

                // Each 8-byte lane takes the first 8 bytes of its element: elements start every third lane, the one a
                // vector starts in up to two lanes before it (in the vector before, within the period).
                var b = Vector512.LoadUnsafe(ref source, offset + 64);
                var c = Vector512.LoadUnsafe(ref source, offset + 128);
#if NET10_0_OR_GREATER
                if (Avx512Vbmi.IsSupported && Gfni.V512.IsSupported && WholeBytes)
                {
                    // Every byte multiplied by its element's flag (vpermb, vpermt2b), the padding by 0 (the flags
                    // ANDed with the bytes kept): the permutation alone on port 5 where the test took it twice.
                    Gfni.V512.GaloisFieldMultiply(a, Avx512Vbmi.PermuteVar64x8(a, FlagBytes0) & Keep)
                        .StoreUnsafe(ref destination, offset);
                    Gfni.V512.GaloisFieldMultiply(b, Avx512Vbmi.PermuteVar64x8x2(a, FlagBytes1, b) & Keep1)
                        .StoreUnsafe(ref destination, offset + 64);
                    Gfni.V512.GaloisFieldMultiply(c, Avx512Vbmi.PermuteVar64x8x2(b, FlagBytes2, c) & Keep2)
                        .StoreUnsafe(ref destination, offset + 128);
                    return;
                }
#endif
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
                Cleaned(a).StoreUnsafe(ref destination, offset);
                Cleaned(b).StoreUnsafe(ref destination, offset + 64);
                Cleaned(c).StoreUnsafe(ref destination, offset + 128);
                Cleaned(d).StoreUnsafe(ref destination, offset + 192);
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
                    Cleaned(a).StoreUnsafe(ref destination, offset);
                    return;
                }

                var b = Vector256.LoadUnsafe(ref source, offset + 32);
                var c = Vector256.LoadUnsafe(ref source, offset + 64);
                if (Avx2.IsSupported && WholeBytes)
                {
                    // Each byte its element's flag from its own 16-byte lane or, through the vector loaded 16 bytes
                    // earlier, the lane before (two pshufb, por), then the multiplication: four operations a vector
                    // within lanes, where moving the lanes took six with a cross-lane permutation in most.
                    var w = offset >= 16
                        ? Vector256.LoadUnsafe(ref source, offset - 16)
                        : Vector256.Create(a.GetLower(), a.GetLower());
                    Times(a, Avx2.Shuffle(a, Own0) | Avx2.Shuffle(w, Before0)).StoreUnsafe(ref destination, offset);
                    w = Vector256.LoadUnsafe(ref source, offset + 16);
                    Times(b, Avx2.Shuffle(b, Own1) | Avx2.Shuffle(w, Before1))
                        .StoreUnsafe(ref destination, offset + 32);
                    w = Vector256.LoadUnsafe(ref source, offset + 48);
                    Times(c, Avx2.Shuffle(c, Own2) | Avx2.Shuffle(w, Before2))
                        .StoreUnsafe(ref destination, offset + 64);
                    return;
                }

                // As Lanes512's: elements start at lanes 0, 3, 6 and 9 of the three vectors (lanes 4 and 5 take lane
                // 3's, lane 8 lane 6's).
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
                var a = Vector256.LoadUnsafe(ref source, offset);
                var b = Vector256.LoadUnsafe(ref source, offset + 32);
                var c = Vector256.LoadUnsafe(ref source, offset + 64);
                var d = Vector256.LoadUnsafe(ref source, offset + 96);
                Cleaned(a).StoreUnsafe(ref destination, offset);
                Cleaned(b).StoreUnsafe(ref destination, offset + 32);
                Cleaned(c).StoreUnsafe(ref destination, offset + 64);
                Cleaned(d).StoreUnsafe(ref destination, offset + 96);
            }

            /// <summary>
            /// One vector of a stride that tiles it (2 to 16 bytes) as the wire holds it: multiplied by its flags with
            /// GFNI (<see cref="Kept(Vector256{byte})"/>), else with AVX2 each
            /// byte takes its element's flag (vpshufb, zero in the padding) and keeps its value by its sign (vpsignb:
            /// kept where 1, cleared where 0; a flag is 0 or 1 in every <c>Nullable</c> the runtime makes), two
            /// operations where the lane test took four, as fast as GFNI's multiplication: 16,000 bytes of a 16-byte
            /// stride 297-309 ns against 332-342 with the destination 4 bytes past a line (186 against 277 on the
            /// line), 1,000 <c>int?</c> serialized 174-179 against 193-196 (local Xeon, DOTNET_EnableAVX512=0
            /// DOTNET_EnableGFNI=0; not timed on AMD).
            /// </summary>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private static Vector256<byte> Cleaned(Vector256<byte> a)
            {
#if NET10_0_OR_GREATER
                if (Multiplies && Gfni.V256.IsSupported)
                    return Kept(a);
#endif
                if (Avx2.IsSupported)
                {
                    var spread = SpreadFlags;
                    return Avx2.Sign(a.AsSByte(), Avx2.Shuffle(a, Vector256.Create(spread, spread)).AsSByte()).AsByte();
                }

                return Present(a, Tile(a), Keep.GetLower());
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
                    Cleaned(a).StoreUnsafe(ref destination, offset);
                    return;
                }

                var b = Vector128.LoadUnsafe(ref source, offset + 16);
                var c = Vector128.LoadUnsafe(ref source, offset + 32);
                if (Ssse3.IsSupported && WholeBytes)
                {
                    // As Lanes256's: each byte its element's flag from its own vector or the one before.
                    Times(a, Ssse3.Shuffle(a, Own16_0)).StoreUnsafe(ref destination, offset);
                    Times(b, Ssse3.Shuffle(b, Own16_1) | Ssse3.Shuffle(a, Before16_1))
                        .StoreUnsafe(ref destination, offset + 16);
                    Times(c, Ssse3.Shuffle(c, Own16_2) | Ssse3.Shuffle(b, Before16_2))
                        .StoreUnsafe(ref destination, offset + 32);
                    return;
                }

                // Two elements: the first in lanes 0 to 2, the second in lanes 3 to 5 of the three vectors.
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
                var a = Vector128.LoadUnsafe(ref source, offset);
                var b = Vector128.LoadUnsafe(ref source, offset + 16);
                var c = Vector128.LoadUnsafe(ref source, offset + 32);
                var d = Vector128.LoadUnsafe(ref source, offset + 48);
                Cleaned(a).StoreUnsafe(ref destination, offset);
                Cleaned(b).StoreUnsafe(ref destination, offset + 16);
                Cleaned(c).StoreUnsafe(ref destination, offset + 32);
                Cleaned(d).StoreUnsafe(ref destination, offset + 48);
            }

            /// <summary>
            /// One vector of a stride that tiles it as the wire holds it: with SSSE3 each byte its element's flag
            /// (pshufb), then the multiplication (GFNI) or a compare and an AND NOT
            /// (<see cref="Spread(Vector128{byte}, Vector128{byte})"/>): two or three operations where the lane test
            /// took five.
            /// </summary>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private static Vector128<byte> Cleaned(Vector128<byte> a) => Ssse3.IsSupported
                ? Spread(a, SpreadFlags)
                : a & Present(Tile(a)) & Keep.GetLower().GetLower();

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private static Vector128<byte> Tile(Vector128<byte> value) => Stride != 16
                ? value
                : Vector128.Shuffle(value.AsUInt64(), Vector128.Create(0UL, 0)).AsByte();

            /// <summary>
            /// All ones in the lanes (of the stride's width; 8 bytes for 16 and 24) whose <see cref="Flags"/> bytes are
            /// not zero.
            /// </summary>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private static Vector128<byte> Present(Vector128<byte> value)
            {
                value &= Flags.GetLower().GetLower();
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
            WriteSlots(ref dst, values);
        }

        /// <summary>
        /// Writes the elements (at least one) into the <see cref="Stride"/> bytes each at <paramref name="dst"/>,
        /// which the caller reserved: what any collection of them writes (NinoElements.WriteBlock).
        /// </summary>
        internal static void WriteSlots(ref byte dst, ReadOnlySpan<T?> values)
        {
            ref T? item = ref MemoryMarshal.GetReference(values);
            if (Raw)
            {
                ref byte src = ref Unsafe.As<T?, byte>(ref item);
                int count = values.Length;
#if NET8_0_OR_GREATER
                if (Lanes && count * Stride >= LaneStep)
                {
                    CopyLanes(ref dst, ref src, count * Stride);
                    return;
                }

                if (Pieces)
                {
                    CopyPieces(ref dst, ref src, count);
                    return;
                }

                if (Spreads && count * Stride >= 16)
                {
                    // The elements that fill whole vectors; the last one or few take the masked copy.
                    int done = CopySpreads(ref dst, ref src, count * Stride);
                    count -= done / Stride;
                    if (count == 0)
                        return;
                    src = ref Unsafe.Add(ref src, done);
                    dst = ref Unsafe.Add(ref dst, done);
                }
#endif
                // With a vector that clears across elements, every stride the kernels above left (64 bytes or
                // more of them); else the masked copy, and then the absent values element by element.
                if (HoldsNullables || (ClearedByVector && count * Stride >= 64))
                {
                    Mask.Copy(ref dst, ref src, count);
                    return;
                }

                if (Padding != null)
                    Padding.Copy(ref dst, ref src, count);
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

        /// <summary>Reads the elements after the count check has bounded the full fixed-stride payload.</summary>
        /// <param name="reused">Whether <paramref name="destination"/> is storage that already held elements (an
        /// existing array or list), not one just allocated.</param>
        public static void ReadPrechecked(Span<T?> destination, ref Reader reader, bool reused = false) =>
            ReadPrechecked(ref MemoryMarshal.GetReference(destination), destination.Length, ref reader, reused);

        /// <summary>
        /// <see cref="ReadPrechecked(Span{T?}, ref Reader)"/> of <paramref name="count"/> elements from
        /// <paramref name="item"/>.
        /// </summary>
        internal static void ReadPrechecked(ref T? item, int count, ref Reader reader, bool reused = false)
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
            // and the value unequal to any other). Raw runs are checked first, then copied by Memmove (CopyToNew), which writes a new array's lines without reading
            // them: a vector loop that checks while it copies read 1,000 int? into a new array 5 to 15 % slower. Data
            // they reject clears the destination before it throws, so a new array allocated uninitialized never shows
            // stale memory (NinoElements.CopiedWhole).
            if (Raw)
            {
                ref byte destination = ref Unsafe.As<T?, byte>(ref item);
#if NET8_0_OR_GREATER
                // On AMD and Arm64 a vector loop checks while it copies: Memmove (rep movsb on AMD) gains nothing there
                // over Nino's own copy, and a separate check pass cost long? and Vec3? runs 10-20 % on EPYC 7763 and
                // Neoverse-N2 (1,000 elements, run 43714421476 against the fused kernel of f8d9e34d).
                // Into an array that already holds elements (a reused destination), x64 checks while it copies with
                // its stores on the destination's lines (NinoChecks.CopyFlagsLined): 1,000 int? 197 -> 126 ns, long?
                // 380 -> 258 (local AVX-512 Xeon, in process). Not into a new array, whose lines Memmove writes
                // without reading them: there the same loop took 1,222 ns against 961.
                if ((FusesCheck || (reused && Vector128.IsHardwareAccelerated)) && count >= 128 &&
                    NinoChecks.TryFlagMask(Stride, out var mask))
                {
                    if (reused && !AdvSimd.Arm64.IsSupported
                            ? NinoChecks.CopyFlagsLinedBest(ref destination, ref src, bytes, mask)
                            : NinoChecks.CopyFlagsBest(ref destination, ref src, bytes, mask))
                        return;
                    Unsafe.InitBlockUnaligned(ref destination, 0, (uint)bytes);
                    NinoChecks.Flags(ref src, count, Stride);
                }
#endif
                if (NinoChecks.ValidFlags(ref src, count, Stride))
                {
                    NinoUnsafe.CopyToNew(ref destination, ref src, (uint)bytes);
                    return;
                }

                Unsafe.InitBlockUnaligned(ref destination, 0, (uint)bytes);
                NinoChecks.Flags(ref src, count, Stride);
            }

            NinoChecks.Flags(ref src, count, Stride);

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
