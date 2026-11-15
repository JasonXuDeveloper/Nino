using System;
using System.Collections.Generic;
#if !NET8_0_OR_GREATER
using System.Numerics;
#endif
using System.Runtime.CompilerServices;
using Nino.Core.CompilerServices;
#if NET8_0_OR_GREATER
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;
#endif

namespace Nino.Core.Internal
{
    /// <summary>
    /// A byte mask (0xFF keeps a byte, 0 clears it) applied while copying values to the wire, in one pass:
    /// SIMD where it is fast (<see cref="NinoUnsafe.VectorMasks"/>), 8-byte words otherwise.
    /// </summary>
    internal sealed class NinoMask
    {
        private readonly byte[] _mask;
        private readonly ulong[] _words; // the mask repeated over lcm(length, 8) bytes
        // The mask repeated over the widest selected period plus one vector, so every width shares one table.
        private readonly byte[] _vectorMask;
        private readonly int _vectorPeriod;
#if NET8_0_OR_GREATER
        private readonly int _period256;
        private readonly int _period512;
#endif
        private readonly int[] _gaps; // (offset, length) of each run of cleared bytes in one element
        private readonly int[] _absent; // NinoLayout.Info.Absent: values cleared when their flag is 0
#if NET10_0_OR_GREATER
        // CopyCleared: whether it takes these elements (ClearedRows.Plan), and its tables, built by the first long run.
        private readonly bool _clears;
        private ClearedRows _rows;
#endif
#if !NET8_0_OR_GREATER
        // For the registered kernels (NinoKernels.Permute): each byte from itself, padding cleared (0xFF). Zero for
        // elements above 64 bytes.
        private readonly IntPtr _kernelTable;
#endif

        /// <summary>(offset, length) of each run of cleared bytes in one element.</summary>
        public int[] Gaps => _gaps;

        /// <summary>Whether values whose flag is 0 are cleared too (<see cref="NinoLayout.Info.Absent"/>).</summary>
        public bool ClearsAbsent => _absent != null;

#if NET10_0_OR_GREATER
        /// <summary>
        /// Whether <see cref="Copy"/> clears those values in the copy's one pass, a vector at a time
        /// (<see cref="CopyCleared"/>: AVX-512 VBMI with GFNI); elsewhere it clears them element by element after.
        /// </summary>
        public bool ClearsByVector => _clears;
#endif

        /// <summary>
        /// Returns a mask, or null when <paramref name="mask"/> keeps every byte and there are no
        /// <paramref name="absent"/> values (<see cref="NinoLayout.Info.Absent"/>) to clear.
        /// </summary>
        public static NinoMask Create(byte[] mask, int[] absent = null) =>
            Array.IndexOf(mask, (byte)0) < 0 && absent == null ? null : new NinoMask(mask, absent);

        /// <summary>A mask of <paramref name="size"/> bytes that keeps every byte.</summary>
        public static byte[] Whole(int size)
        {
            var mask = new byte[size];
            mask.AsSpan().Fill(0xFF);
            return mask;
        }

        private NinoMask(byte[] mask, int[] absent)
        {
            _mask = mask;
            _absent = absent;
            var gaps = new List<int>();
            for (int i = 0; i < mask.Length; i++)
            {
                if (mask[i] != 0)
                    continue;
                int start = i;
                while (i + 1 < mask.Length && mask[i + 1] == 0)
                    i++;
                gaps.Add(start);
                gaps.Add(i + 1 - start);
            }

            _gaps = gaps.ToArray();
#if !NET8_0_OR_GREATER
            if (mask.Length <= 64)
            {
                _kernelTable = NinoKernelHook.Table(mask.Length);
                unsafe
                {
                    for (int j = 0; j < mask.Length; j++)
                        if (mask[j] != 0)
                            ((byte*)_kernelTable)[j] = (byte)j;
                }
            }
#endif
#if NET10_0_OR_GREATER
            _clears = ClearedRows.Plan(mask, absent, out _, out _, out _);
#endif
            var words = Repeat(mask, 8, 0, out _);
            _words = new ulong[words.Length / 8];
            for (int i = 0; i < _words.Length; i++)
                _words[i] = Unsafe.ReadUnaligned<ulong>(ref words[i * 8]);
#if NET8_0_OR_GREATER
            // Not on Ice Lake (NinoSimd.Prefer512): GitHub's Xeon Platinum 8370C wrote 1,000 8-byte structs with a
            // padding byte in 116 ns with 64-byte vectors, 83 with 32-byte ones (MemoryPack's copy 88).
            if (Vector128.IsHardwareAccelerated)
            {
                int width = NinoSimd.Prefer512 ? 64 : Vector256.IsHardwareAccelerated ? 32 : 16;
                _vectorMask = Repeat(mask, width, width, out int period);
                _vectorPeriod = width == 16 ? period : Period(mask.Length, 16);
                _period256 = width == 32 ? period : width == 64 ? Period(mask.Length, 32) : 0;
                _period512 = width == 64 ? period : 0;
            }
#else
            if (NinoUnsafe.VectorMasks)
                _vectorMask = Repeat(mask, Vector<byte>.Count, Vector<byte>.Count, out _vectorPeriod);
#endif
        }

        /// <summary>
        /// <paramref name="mask"/> repeated over its <paramref name="period"/>, the least multiple of its length that
        /// is a multiple of <paramref name="multiple"/>, and <paramref name="extra"/> bytes more.
        /// </summary>
        private static byte[] Repeat(byte[] mask, int multiple, int extra, out int period)
        {
            period = Period(mask.Length, multiple);
            var bytes = new byte[period + extra];
            mask.AsSpan().CopyTo(bytes);
            // Double the filled prefix until only a partial copy remains.
            for (int filled = mask.Length; filled < bytes.Length;)
            {
                int count = Math.Min(filled, bytes.Length - filled);
                bytes.AsSpan(0, count).CopyTo(bytes.AsSpan(filled));
                filled += count;
            }
            return bytes;
        }

        private static int Period(int length, int multiple)
        {
            int period = length;
            while (period % multiple != 0)
                period += length;
            return period;
        }

        /// <summary>Copies <paramref name="count"/> masked elements (the mask's length each).</summary>
        // Out of line: one call per collection, around loops for any count. Inlined (the JIT did, on its profile
        // data), its loops took the inlining budget of the generated body writing the collection, which then left
        // helpers such as Writer.SetBuffer as calls.
        [MethodImpl(MethodImplOptions.NoInlining)]
        public void Copy(ref byte destination, ref byte source, int count)
        {
#if NET10_0_OR_GREATER
            if (_clears && count * _mask.Length >= 64)
            {
                // The elements whole vectors hold; the last few of a run under two vectors take the path below.
                int done = CopyCleared(ref destination, ref source, (nuint)(count * _mask.Length));
                count -= done;
                if (count == 0)
                    return;
                destination = ref Unsafe.Add(ref destination, done * _mask.Length);
                source = ref Unsafe.Add(ref source, done * _mask.Length);
            }
#endif
            CopyMasked(ref destination, ref source, count);
            if (_absent != null)
                ClearAbsent(ref destination, count);
        }

#if NET10_0_OR_GREATER
        /// <summary>
        /// The tables of <see cref="CopyCleared"/>: for each place in an element a 64-byte vector can start at (its
        /// phase), a row of 64-byte vectors, aligned: for each byte of the vector where its value's flag is in the
        /// vector before it and itself (a vpermt2b index), whether it follows a flag, and 1 where it is kept whatever
        /// the data; for elements with a <c>Nullable</c> inside another's value, the same for the inner flag.
        /// </summary>
        private sealed unsafe class ClearedRows
        {
            /// <summary>The vectors of a row: index, follows a flag, kept; with nested values the inner index and follows.</summary>
            private const int Plain = 3, WithNested = 5;

            // Pinned, so the rows stay on 64-byte boundaries: a row loaded across two cache lines three times a
            // vector took 2.2 ns a vector against 1.5 (local, AVX-512 Xeon).
            private readonly byte[] _pinned;

            public readonly byte* First;
            public readonly nuint Length, RowSize;

            /// <summary>The bytes from a vector's row to the next one's: the phase moves by 64 bytes, within the element.</summary>
            public readonly nuint Step;
            public readonly bool Nested;

            /// <summary>The vectors after which the phase repeats: the element's length over its common divisor with 64.</summary>
            public readonly int Period;

            /// <summary>
            /// Whether <see cref="CopyCleared"/> takes elements of <paramref name="mask"/> and <paramref name="absent"/>,
            /// and for each byte of an element the flags it follows: <paramref name="outer"/> (-1 for none) and, for a
            /// value inside another <c>Nullable</c>'s, <paramref name="inner"/>. Needs a permutation across two
            /// vectors (AVX-512 VBMI) and GFNI, a mask of whole bytes, every flag kept and at most 64 bytes before
            /// the last byte of its value, and values nested no deeper than two.
            /// </summary>
            public static bool Plan(byte[] mask, int[] absent, out int[] outer, out int[] inner, out bool nested)
            {
                outer = inner = null;
                nested = false;
                if (absent == null || !Avx512Vbmi.IsSupported || !Gfni.V512.IsSupported)
                    return false;
                outer = new int[mask.Length];
                inner = new int[mask.Length];
                outer.AsSpan().Fill(-1);
                inner.AsSpan().Fill(-1);
                // Outer values come before the ones inside them (NinoLayout.Info.Absent).
                for (int i = 0; i < absent.Length; i += 3)
                {
                    int flag = absent[i];
                    if (mask[flag] != 0xFF)
                        return false;
                    for (int at = absent[i + 1]; at < absent[i + 1] + absent[i + 2]; at++)
                    {
                        if (mask[at] == 0)
                            continue;
                        if (mask[at] != 0xFF || at <= flag || at - flag > 64 || inner[at] >= 0)
                            return false;
                        if (outer[at] < 0)
                        {
                            outer[at] = flag;
                        }
                        else
                        {
                            inner[at] = flag;
                            nested = true;
                        }
                    }
                }

                foreach (byte keep in mask)
                    if (keep != 0 && keep != 0xFF)
                        return false;
                return true;
            }

            public ClearedRows(byte[] mask, int[] absent)
            {
                Plan(mask, absent, out int[] outer, out int[] inner, out Nested);
                int length = mask.Length, row = 64 * (Nested ? WithNested : Plain);
                Length = (nuint)length;
                RowSize = (nuint)row;
                Step = (nuint)(64 % length * row);
                // The vectors of a period: the least n with 64 n a multiple of the length.
                Period = 1;
                while (Period * 64 % length != 0)
                    Period++;
                _pinned = GC.AllocateArray<byte>(length * row + 63, pinned: true);
                First = (byte*)(((nuint)Unsafe.AsPointer(ref _pinned[0]) + 63) & ~(nuint)63);
                for (int phase = 0; phase < length; phase++)
                {
                    byte* at = First + phase * row;
                    for (int i = 0; i < 64; i++)
                    {
                        int e = (phase + i) % length;
                        // The byte itself where it follows no flag: its index is not used.
                        at[i] = (byte)(64 + i - (outer[e] < 0 ? 0 : e - outer[e]));
                        at[64 + i] = outer[e] < 0 ? (byte)0 : (byte)0xFF;
                        at[128 + i] = mask[e] != 0 && outer[e] < 0 ? (byte)1 : (byte)0;
                        if (!Nested)
                            continue;
                        at[192 + i] = (byte)(64 + i - (inner[e] < 0 ? 0 : e - inner[e]));
                        at[256 + i] = inner[e] < 0 ? (byte)0 : (byte)0xFF;
                    }
                }
            }
        }

        /// <summary>
        /// One vector of elements as the wire holds it: each byte multiplied in GF(2^8) by its value's flag (1 keeps
        /// it, 0 clears it; a flag is 0 or 1 in every <c>Nullable</c> the runtime makes), by 1 where it is kept
        /// whatever the data and by 0 where it is padding. The flags come from <paramref name="previous"/> (the 64
        /// bytes before) and <paramref name="current"/> by one permutation (vpermt2b), from the source and never
        /// from what was stored: a load right after a store that only partly covers it waits for the store.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector512<byte> Cleared(Vector512<byte> previous, Vector512<byte> current, Vector512<byte> index,
            Vector512<byte> follows, Vector512<byte> kept) =>
            Gfni.V512.GaloisFieldMultiply(current, Avx512F.TernaryLogic(
                Avx512Vbmi.PermuteVar64x8x2(previous, index, current), follows, kept, FollowedOrKept));

        /// <summary>vpternlog's table for <c>(a &amp; b) | c</c>.</summary>
        private const byte FollowedOrKept = 0xEA;

        /// <summary>vpternlog's table for <c>(a &amp; b) | (~b &amp; c)</c>: a where b, else c.</summary>
        private const byte FollowedOrOne = 0xE2;

        /// <summary><see cref="Cleared(Vector512{byte}, Vector512{byte}, Vector512{byte}, Vector512{byte}, Vector512{byte})"/> from a row of the tables.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static unsafe Vector512<byte> Cleared(Vector512<byte> previous, Vector512<byte> current, byte* row,
            bool nested)
        {
            var value = Cleared(previous, current, Vector512.LoadAligned(row), Vector512.LoadAligned(row + 64),
                Vector512.LoadAligned(row + 128));
            if (!nested)
                return value;
            // A value inside another Nullable's value: multiplied by the inner flag as well, 1 for every other byte.
            return Gfni.V512.GaloisFieldMultiply(value, Avx512F.TernaryLogic(
                Avx512Vbmi.PermuteVar64x8x2(previous, Vector512.LoadAligned(row + 192), current),
                Vector512.LoadAligned(row + 256), Vector512.Create((byte)1), FollowedOrOne));
        }

        /// <summary>
        /// The element length below which <see cref="CopyCleared"/> writes a vector's whole elements a step, its
        /// stores overlapping (as it does elements that divide 64 bytes, whose stores do not overlap): three
        /// operations a store that covers 52 bytes or more, where the stores on the destination's boundaries are
        /// five a vector (vpermt2b is three on Intel cores). 1,000 elements of 6 bytes took 110 to 118 ns so and
        /// 145 to 150 aligned, of 12 bytes 230 and 245 to 255, a 16-byte struct 258 and 300; of 20 bytes 355 to 380
        /// and 340 to 348, of 40 bytes 2,200 and 1,190 (local, AVX-512 Xeon, serialize).
        /// </summary>
        private const int WholeBelow = 16;

        /// <summary>The vectors from which a run of elements that tile the vector aligns its stores (as NinoNullables' lanes).</summary>
        private const int AlignFrom = 8;

        /// <summary>
        /// Copies elements that hold <c>Nullable</c> values (<paramref name="total"/> bytes, 64 or more) with their
        /// padding and their absent values cleared, a 64-byte vector at a time, and returns how many it wrote whole:
        /// all of them from two vectors on, else the ones the first vector holds. Element by element after the
        /// masked copy, the absent values of 1,000 structs of an int, a long? and a double? took three times the
        /// copy (3,344 ns against 1,126 for the same struct without them, local).
        /// </summary>
        private unsafe int CopyCleared(ref byte destination, ref byte source, nuint total)
        {
            var rows = _rows ??= new ClearedRows(_mask, _absent);
            if (total < 128 || _mask.Length < WholeBelow || 64 % _mask.Length == 0)
            {
                // The whole elements a vector holds, a step: every flag of its bytes is in it (elements start at
                // its first byte), so one permutation of the vector itself gathers them (vpermb takes the low six
                // bits of the first row's indices). The store's last bytes are the next step's, except where the
                // elements tile the vector: those go on from the destination's first 64-byte boundary where whole
                // elements lead to it, as NinoNullables' lanes do.
                nuint length = (nuint)_mask.Length, step = 64u / (uint)length * length, i = 0, next = step;
                if (step == 64 && total >= AlignFrom * 64)
                {
                    nuint head = NinoUnsafe.BytesToAligned(ref destination, 64);
                    // A length that divides 64 is a power of two.
                    if ((head & (length - 1)) == 0)
                        next = head;
                }

                nuint last = total - 64;
                byte* row = rows.First;
                bool nested = rows.Nested;
                Vector512<byte> index = Vector512.LoadAligned(row), follows = Vector512.LoadAligned(row + 64),
                    kept = Vector512.LoadAligned(row + 128);
                do
                {
                    var current = Vector512.LoadUnsafe(ref source, i);
                    var value = Gfni.V512.GaloisFieldMultiply(current, Avx512F.TernaryLogic(
                        Avx512Vbmi.PermuteVar64x8(current, index), follows, kept, FollowedOrKept));
                    if (nested)
                        value = Gfni.V512.GaloisFieldMultiply(value, Avx512F.TernaryLogic(
                            Avx512Vbmi.PermuteVar64x8(current, Vector512.LoadAligned(row + 192)),
                            Vector512.LoadAligned(row + 256), Vector512.Create((byte)1), FollowedOrOne));
                    value.StoreUnsafe(ref destination, i);
                    i += next;
                    next = step;
                    // A run under two vectors: its first one only (an element over 64 bytes makes no step).
                } while (total >= 128 && i <= last);

                if (total < 128)
                    return (int)((uint)i / (uint)length);
                // The elements after the last whole vector: one more that ends the run, wherever in an element
                // it starts (element by element, five 12-byte elements took 30 ns of a run of 1,000's 245).
                Cleared(Vector512.LoadUnsafe(ref source, last - 64), Vector512.LoadUnsafe(ref source, last),
                    row + (uint)last % (uint)length * rows.RowSize, nested).StoreUnsafe(ref destination, last);
                return (int)((uint)total / (uint)length);
            }

            if (rows.Nested)
                CopyCleared<AnyPeriod>(ref destination, ref source, total, rows);
            else if (rows.Period == 3)
                CopyCleared<Period3>(ref destination, ref source, total, rows);
            else if (rows.Period == 5)
                CopyCleared<Period5>(ref destination, ref source, total, rows);
            else
                CopyCleared<AnyPeriod>(ref destination, ref source, total, rows);
            return (int)total / _mask.Length;
        }

        /// <summary>
        /// The vectors of a period whose rows <see cref="CopyCleared{TPeriod}"/> keeps in registers (3 or 5: an
        /// element of 3 or 5 times a power of two), 0 for rows loaded per vector.
        /// </summary>
        private interface IClearedPeriod
        {
            static abstract int Vectors { get; }
        }

        private readonly struct AnyPeriod : IClearedPeriod
        {
            public static int Vectors => 0;
        }

        private readonly struct Period3 : IClearedPeriod
        {
            public static int Vectors => 3;
        }

        private readonly struct Period5 : IClearedPeriod
        {
            public static int Vectors => 5;
        }

        /// <summary>
        /// <see cref="CopyCleared"/> for two vectors or more. Whole vectors, each written once and in address order,
        /// whatever the element's length: a vector starts anywhere in an element (its phase picks its row), so the
        /// first two are stored as they lie and the rest on the destination's 64-byte boundaries (a store across
        /// two cache lines costs about as much as four of these operations), the last one ending the run. No store
        /// runs past its elements into bytes the next one writes: one 40-byte element a 64-byte store took 2,560 ns
        /// for 1,000, this 1,160, a copy 1,075 (local, AVX-512 Xeon). Where the phase repeats within five vectors
        /// the rows stay in registers: loaded per vector, 20-byte elements took 500 ns for 1,000 against 330 (a
        /// kernel harness).
        /// </summary>
        // Fully optimized from the first call, as CopyVectors.
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static unsafe void CopyCleared<TPeriod>(ref byte destination, ref byte source, nuint total,
            ClearedRows plan)
            where TPeriod : struct, IClearedPeriod
        {
            byte* rows = plan.First;
            nuint size = plan.RowSize, length = plan.Length;
            bool nested = plan.Nested;
            nuint step = plan.Step;
            byte* wrap = rows + length * size;
            var a = Vector512.LoadUnsafe(ref source);
            Cleared(a, a, rows, nested).StoreUnsafe(ref destination);
            var b = Vector512.LoadUnsafe(ref source, 64);
            nuint end = total - 64;
            nuint i = 64 + NinoUnsafe.BytesToAligned(ref destination, 64);
            byte* row = rows + (uint)i % (uint)length * size;
            byte* last = rows + (uint)end % (uint)length * size;
            Cleared(a, b, rows + step, nested).StoreUnsafe(ref destination, 64);
            var previous = Vector512.LoadUnsafe(ref source, i - 64);
            nuint span = (nuint)TPeriod.Vectors * 64;
            if (TPeriod.Vectors != 0 && i + span <= end)
            {
                byte* row1 = Next(row, step, wrap, size * length), row2 = Next(row1, step, wrap, size * length);
                byte* row3 = Next(row2, step, wrap, size * length), row4 = Next(row3, step, wrap, size * length);
                Vector512<byte> x0 = Vector512.LoadAligned(row), f0 = Vector512.LoadAligned(row + 64),
                    k0 = Vector512.LoadAligned(row + 128);
                Vector512<byte> x1 = Vector512.LoadAligned(row1), f1 = Vector512.LoadAligned(row1 + 64),
                    k1 = Vector512.LoadAligned(row1 + 128);
                Vector512<byte> x2 = Vector512.LoadAligned(row2), f2 = Vector512.LoadAligned(row2 + 64),
                    k2 = Vector512.LoadAligned(row2 + 128);
                Vector512<byte> x3 = Vector512.LoadAligned(row3), f3 = Vector512.LoadAligned(row3 + 64),
                    k3 = Vector512.LoadAligned(row3 + 128);
                Vector512<byte> x4 = Vector512.LoadAligned(row4), f4 = Vector512.LoadAligned(row4 + 64),
                    k4 = Vector512.LoadAligned(row4 + 128);
                do
                {
                    var c0 = Vector512.LoadUnsafe(ref source, i);
                    Cleared(previous, c0, x0, f0, k0).StoreUnsafe(ref destination, i);
                    previous = c0;
                    {
                        var c1 = Vector512.LoadUnsafe(ref source, i + 64);
                        var c2 = Vector512.LoadUnsafe(ref source, i + 128);
                        Cleared(c0, c1, x1, f1, k1).StoreUnsafe(ref destination, i + 64);
                        Cleared(c1, c2, x2, f2, k2).StoreUnsafe(ref destination, i + 128);
                        previous = c2;
                    }

                    if (TPeriod.Vectors >= 5)
                    {
                        var c3 = Vector512.LoadUnsafe(ref source, i + 192);
                        var c4 = Vector512.LoadUnsafe(ref source, i + 256);
                        Cleared(previous, c3, x3, f3, k3).StoreUnsafe(ref destination, i + 192);
                        Cleared(c3, c4, x4, f4, k4).StoreUnsafe(ref destination, i + 256);
                        previous = c4;
                    }

                    i += span;
                } while (i + span <= end);
            }

            // The vectors after the last whole period, at the phase the periods started with; every vector of a
            // longer period.
            for (; i < end; i += 64)
            {
                var current = Vector512.LoadUnsafe(ref source, i);
                Cleared(previous, current, row, nested).StoreUnsafe(ref destination, i);
                previous = current;
                row = Next(row, step, wrap, size * length);
            }

            Cleared(Vector512.LoadUnsafe(ref source, end - 64), Vector512.LoadUnsafe(ref source, end), last, nested)
                .StoreUnsafe(ref destination, end);
        }

        /// <summary>The row of the vector after <paramref name="row"/>'s.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static unsafe byte* Next(byte* row, nuint step, byte* wrap, nuint all)
        {
            row += step;
            return row >= wrap ? row - all : row;
        }
#endif

        /// <summary>
        /// Zeroes the <c>Nullable</c> values without one in <paramref name="count"/> elements, after their bytes are in
        /// place: data-dependent, so not part of the mask. Only types that hold a <c>Nullable</c> pay for it.
        /// </summary>
        private void ClearAbsent(ref byte destination, int count)
        {
            for (int e = 0; e < count; e++)
                NinoLayout.ClearAbsent(_absent, ref Unsafe.Add(ref destination, e * _mask.Length));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void CopyMasked(ref byte destination, ref byte source, int count)
        {
            int total = checked(count * _mask.Length);
#if !NET8_0_OR_GREATER
            // The netstandard2.1 build (Unity, Mono): the registered kernels, for runs long enough.
            if (count >= NinoKernelHook.MinCopies && _kernelTable != IntPtr.Zero &&
                NinoKernelHook.TryPermute(ref source, _mask.Length, ref destination, _mask.Length, _kernelTable, count))
                return;
#endif
#if NET8_0_OR_GREATER
            if (Vector512.IsHardwareAccelerated && _period512 != 0 && total >= Vector512<byte>.Count)
            {
                CopyVectors<Vector512<byte>, NinoVectors512>(ref destination, ref source, total, _vectorMask, _period512);
                return;
            }
            if (Vector256.IsHardwareAccelerated && _period256 != 0 && total >= Vector256<byte>.Count)
            {
                CopyVectors<Vector256<byte>, NinoVectors256>(ref destination, ref source, total, _vectorMask, _period256);
                return;
            }
            if (_vectorMask != null && total >= Vector128<byte>.Count)
            {
                CopyVectors<Vector128<byte>, NinoVectors128>(ref destination, ref source, total, _vectorMask, _vectorPeriod);
                return;
            }
#else
            if (_vectorMask != null && total >= Vector<byte>.Count)
            {
                CopyVectors<Vector<byte>, NinoVectors>(ref destination, ref source, total, _vectorMask, _vectorPeriod);
                return;
            }
#endif

            int i = 0;
            int words = _words.Length;
            int maskWord = 0;
            ref ulong word = ref NinoUnsafe.GetArrayDataReference(_words);
            // The platform fast path of hosts without vector masks. In the netstandard2.1 build, runs of
            // NinoKernelHook.MinCopies elements or more (CopyFloor, 16, at least) went to the registered Permute kernel
            // first (Burst on Unity, a native or wasm kernel elsewhere), so on Unity with Burst these unrolls copy only
            // the shorter runs; they copy every run on hosts without a registered kernel (Mono, Armv7, Unity
            // with Burst off). IL2CPP does not accelerate Vector<T>, so no vector path above runs there either. On
            // .NET 8+ they serve hosts without SIMD. Where vectors run, a total of one vector or more took a vector
            // path above (16 bytes on .NET 8+, Vector<byte>.Count in the netstandard2.1 build), so only shorter totals
            // get here. Periods of one to three words, unrolled with every mask word in a register, all loads before
            // the stores (Arm64 pairs them as ldp / stp); the shared word loop below wraps the mask index per word.
            // Measured with SIMD disabled on an Apple M4 (.NET 10, not on Mono or IL2CPP): 12-byte elements 38-56%
            // faster, 24-byte 44-55%, 16-byte 37-67% around 64 KiB, one-word periods 39-48% (7a3cf2c6, f3d6bd81,
            // 4c8a1e11; vectors-copies.md, "Masked copy"). Keep them until a Unity or Mono measurement says otherwise.
            if (words == 3 && total >= 24)
            {
                ulong mask0 = word, mask1 = Unsafe.Add(ref word, 1), mask2 = Unsafe.Add(ref word, 2);
                for (; i <= total - 24; i += 24)
                {
                    ref byte src = ref Unsafe.Add(ref source, i);
                    ref byte dst = ref Unsafe.Add(ref destination, i);
                    ulong a = Unsafe.ReadUnaligned<ulong>(ref src);
                    ulong b = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref src, 8));
                    ulong c = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref src, 16));
                    Unsafe.WriteUnaligned(ref dst, a & mask0);
                    Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, 8), b & mask1);
                    Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, 16), c & mask2);
                }
            }
            else if (words <= 2 && total >= 16)
            {
                ulong mask0 = word, mask1 = words == 1 ? mask0 : Unsafe.Add(ref word, 1);
                for (; i <= total - 16; i += 16)
                {
                    ref byte src = ref Unsafe.Add(ref source, i);
                    ref byte dst = ref Unsafe.Add(ref destination, i);
                    ulong a = Unsafe.ReadUnaligned<ulong>(ref src);
                    ulong b = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref src, 8));
                    Unsafe.WriteUnaligned(ref dst, a & mask0);
                    Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, 8), b & mask1);
                }
            }

            for (; i <= total - 8; i += 8)
            {
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref destination, i),
                    Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, i)) & Unsafe.Add(ref word, maskWord));
                if (++maskWord == words)
                    maskWord = 0;
            }

            ref byte mask = ref Unsafe.As<ulong, byte>(ref word);
            for (int k = maskWord * 8; i < total; i++, k++)
                Unsafe.Add(ref destination, i) = (byte)(Unsafe.Add(ref source, i) & Unsafe.Add(ref mask, k));
        }

        /// <summary>
        /// Vector copy with aligned stores (aligned loads on Arm64, <see cref="AlignedSide"/>): the destination usually
        /// follows a 4-byte length, and unaligned vector stores straddle cache lines every other store. The first and
        /// last vectors are unaligned and overlap the aligned body; the mask of each vector is loaded at its offset
        /// in <paramref name="masks"/>, the mask repeated over <paramref name="repeat"/> bytes and one vector more.
        /// Offsets are native-sized (nuint): int offsets took a sign extension and an address add per vector, measured
        /// on an AVX-512 Xeon as 8,000 bytes in 74 ns instead of 66 (64-byte vectors). <typeparamref name="TVector"/>
        /// is <see cref="Vector{T}"/> of bytes, or a 64-byte vector where those are accelerated (not on CPUs that slow
        /// down for them): measured on an AVX-512 Xeon, structs of 8 bytes with a padding byte, source and destination
        /// of different alignments, 8,000 bytes in 66 ns (97 ns with 32-byte vectors, 57 ns for a plain copy, 75 ns
        /// Memmove), 1,200 bytes in 14 ns (19.5 ns, and 18.6 ns).
        /// </summary>
        // Fully optimized from the first call: a profile taken on short copies (lists of a few elements) would lay
        // out the unrolled loop as cold code for the long ones.
#if NET8_0_OR_GREATER
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
#endif
        private static unsafe void CopyVectors<TVector, TOps>(ref byte destination, ref byte source, int total,
            byte[] masks, int repeat)
            where TVector : struct
            where TOps : struct, INinoVectors<TVector>
        {
            var ops = default(TOps);
            nuint width = (nuint)Unsafe.SizeOf<TVector>();
            nuint period = (nuint)repeat;
            ref byte mask = ref NinoUnsafe.GetArrayDataReference(masks);
            ops.Store(ops.And(ops.Load(ref source, 0), ops.Load(ref mask, 0)), ref destination, 0);

            nuint end = (nuint)total - width;
            if (end <= width)
            {
                // The final vector's offset is still within the first mask period.
                if (end != 0)
                    ops.Store(ops.And(ops.Load(ref source, end), ops.Load(ref mask, end)), ref destination, end);
                return;
            }

            nuint i = width - ((nuint)Unsafe.AsPointer(ref AlignedSide(ref destination, ref source)) & (width - 1));
            nuint m = i; // i % period (i <= width <= period)
            if (period == width)
            {
                var v = ops.Load(ref mask, m);
#if NET8_0_OR_GREATER
                if (AdvSimd.Arm64.IsSupported && Unsafe.SizeOf<TVector>() == 16)
                    i = CopyPairs(ref destination, ref source, i, end - 1, ops.Lower(v));
#endif
                // The final store handles the vector at end, even when it is aligned.
                for (; i + 3 * width < end; i += 4 * width)
                {
                    var a = ops.And(ops.Load(ref source, i), v);
                    var b = ops.And(ops.Load(ref source, i + width), v);
                    var c = ops.And(ops.Load(ref source, i + 2 * width), v);
                    var e = ops.And(ops.Load(ref source, i + 3 * width), v);
                    ops.Store(a, ref destination, i);
                    ops.Store(b, ref destination, i + width);
                    ops.Store(c, ref destination, i + 2 * width);
                    ops.Store(e, ref destination, i + 3 * width);
                }

                for (; i < end; i += width)
                    ops.Store(ops.And(ops.Load(ref source, i), v), ref destination, i);
            }
            else
            {
                if (m >= period)
                    m -= period;
                for (; i <= end; i += width)
                {
                    ops.Store(ops.And(ops.Load(ref source, i), ops.Load(ref mask, m)), ref destination, i);
                    m += width;
                    if (m >= period)
                        m -= period;
                }
            }

            ops.Store(ops.And(ops.Load(ref source, end), ops.Load(ref mask, TailOffset(m, i, end, period))),
                ref destination, end);
        }

        /// <summary>
        /// The side <see cref="CopyVectors{TVector, TOps}"/> aligns: the destination (unaligned vector stores straddle cache lines),
        /// but the source on Arm64, where aligned loads beat aligned stores: on GitHub's Arm64 runners (Neoverse N2, .NET
        /// 10) a 16-byte vector copy took 0.76 to 1.00 of memcpy's time for 8,000 bytes with its loads aligned, and up to
        /// 1.31 times with its stores aligned.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ref byte AlignedSide(ref byte destination, ref byte source)
        {
#if NET8_0_OR_GREATER
            if (AdvSimd.Arm64.IsSupported)
                return ref source;
#endif
            return ref destination;
        }

#if NET8_0_OR_GREATER
        /// <summary>
        /// The body of <see cref="CopyVectors{TVector, TOps}"/> on Arm64, 64 bytes at a time as two load pairs and two store pairs
        /// (ldp/stp of q registers, as the C library's memcpy copies): from <paramref name="i"/> while a whole 64
        /// bytes fits before <paramref name="end"/> + 16; returns where it stopped.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static unsafe nuint CopyPairs(ref byte destination, ref byte source, nuint i, nuint end,
            Vector128<byte> v)
        {
            fixed (byte* d = &destination, s = &source)
            {
                for (; i + 48 <= end; i += 64)
                {
                    var (a, b) = AdvSimd.Arm64.LoadPairVector128(s + i);
                    var (c, e) = AdvSimd.Arm64.LoadPairVector128(s + i + 32);
                    AdvSimd.Arm64.StorePair(d + i, a & v, b & v);
                    AdvSimd.Arm64.StorePair(d + i + 32, c & v, e & v);
                }
            }

            return i;
        }
#endif

        /// <summary>
        /// <c>end % period</c> from the loop's state, without a division (an <c>idiv</c> of about 12 cycles, 5% of
        /// copying 1,000 8-byte structs): <paramref name="m"/> is <paramref name="i"/> % <paramref name="period"/>,
        /// and the last vector at <paramref name="end"/> starts less than a vector before <paramref name="i"/>.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static nuint TailOffset(nuint m, nuint i, nuint end, nuint period)
        {
            nuint back = i - end;
            return m >= back ? m - back : m + period - back;
        }

        /// <summary>
        /// Zeroes the padding of <paramref name="count"/> elements in place. Only stores: reading back bytes just
        /// written by narrower field stores would stall on store forwarding (the absent values of types holding a
        /// <c>Nullable</c>, cleared after, read their flags).
        /// </summary>
        public void Clear(ref byte destination, int count)
        {
            int[] gaps = _gaps;
            for (int e = 0; e < count; e++)
            {
                ref byte element = ref Unsafe.Add(ref destination, e * _mask.Length);
                for (int g = 0; g < gaps.Length; g += 2)
                    Zero(ref Unsafe.Add(ref element, gaps[g]), gaps[g + 1]);
            }

            if (_absent != null)
                ClearAbsent(ref destination, count);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static void Zero(ref byte destination, int length)
        {
            switch (length)
            {
                case 1:
                    destination = 0;
                    return;
                case 2:
                    Unsafe.WriteUnaligned(ref destination, (ushort)0);
                    return;
                case 3:
                    Unsafe.WriteUnaligned(ref destination, (ushort)0);
                    Unsafe.Add(ref destination, 2) = 0;
                    return;
                case 4:
                    Unsafe.WriteUnaligned(ref destination, 0u);
                    return;
                case 5:
                case 6:
                case 7:
                    // Two overlapping stores, both inside the gap.
                    Unsafe.WriteUnaligned(ref destination, 0u);
                    Unsafe.WriteUnaligned(ref Unsafe.Add(ref destination, length - 4), 0u);
                    return;
                case 8:
                    Unsafe.WriteUnaligned(ref destination, 0ul);
                    return;
                default:
                    Unsafe.InitBlockUnaligned(ref destination, 0, (uint)length);
                    return;
            }
        }
    }
}
