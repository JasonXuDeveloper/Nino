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
#if NET8_0_OR_GREATER
        // CopyCleared: whether it takes these elements (ClearedRows.Plan), and its tables, built by the first long run.
        private readonly bool _clears;
        private ClearedRows _rows;

        // CopyLanes: the distance from each absent value to its flag, which is also the value's size and alignment
        // (1, 2, 4 or 8: a Nullable of a primitive), 0 where the values differ or CopyCleared takes them; the mask and
        // the values' bytes repeated over their period at the lane width (32 bytes with AVX2, 16 else) and a vector more.
        private readonly int _laneDistance;
        private readonly byte[] _laneMask, _laneValues;
        private readonly int _lanePeriod;

        // For a mask of whole bytes, the tables of the shorter kernels: x64 (SSSE3) the pshufb indices that give
        // each value byte its flag from the vector loaded d bytes earlier (a row per phase of the vectors within a
        // 16-byte lane: the run's offset of the vectors modulo 16, over d) and 1 in every other byte kept; Arm64 the
        // flag byte of each value lane (cmtst) and the mask of the bytes outside the values.
        private readonly byte[][] _laneIndex;
        private readonly byte[] _laneOnes, _laneFlags, _laneOthers;
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

#if NET8_0_OR_GREATER
        /// <summary>
        /// Whether <see cref="Copy"/> clears those values in the copy's one pass, a vector at a time
        /// (<see cref="CopyCleared"/>: AVX-512 VBMI, with GFNI on .NET 10); elsewhere it clears them element by element after.
        /// </summary>
        public bool ClearsByVector => _clears;

        /// <summary>
        /// Whether <see cref="Copy"/> clears absent values a vector at a time without CopyCleared (CopyLanes: every
        /// value a Nullable of a primitive, its flag in the lane before it).
        /// </summary>
        public bool ClearsByLanes => _laneDistance != 0;
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
#if NET8_0_OR_GREATER
            _clears = ClearedRows.Plan(mask, absent, out _, out _, out _);
            _laneDistance = _clears || !Vector128.IsHardwareAccelerated ? 0 : LaneDistance(absent, mask.Length);
            if (_laneDistance != 0)
            {
                int laneWidth = Vector256.IsHardwareAccelerated ? 32 : 16;
                var values = new byte[mask.Length];
                for (int i = 0; i < absent.Length; i += 3)
                    values.AsSpan(absent[i + 1], absent[i + 2]).Fill(0xFF);
                _laneMask = Repeat(mask, laneWidth, laneWidth, out _lanePeriod);
                _laneValues = Repeat(values, laneWidth, laneWidth, out _);
                bool whole = Array.TrueForAll(mask, b => b == 0 || b == 0xFF);
                if (whole && Ssse3.IsSupported)
                {
                    int d = _laneDistance;
                    _laneIndex = new byte[16 / d][];
                    for (int row = 0; row < _laneIndex.Length; row++)
                    {
                        var index = _laneIndex[row] = new byte[_laneValues.Length];
                        for (int s = 0; s < index.Length; s++)
                            index[s] = _laneValues[s] != 0 ? (byte)((s - s % d - row * d) & 15) : (byte)0x80;
                    }

                    _laneOnes = new byte[_laneValues.Length];
                    for (int s = 0; s < _laneOnes.Length; s++)
                        _laneOnes[s] = _laneMask[s] != 0 && _laneValues[s] == 0 ? (byte)1 : (byte)0;
                }
                else if (whole && AdvSimd.Arm64.IsSupported)
                {
                    _laneFlags = new byte[_laneValues.Length];
                    _laneOthers = new byte[_laneValues.Length];
                    for (int s = 0; s < _laneFlags.Length; s++)
                    {
                        _laneFlags[s] = _laneValues[s] != 0 && s % _laneDistance == 0 ? (byte)0xFF : (byte)0;
                        _laneOthers[s] = _laneValues[s] != 0 ? (byte)0 : _laneMask[s];
                    }
                }
            }
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
#if NET8_0_OR_GREATER
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

            if (_laneDistance != 0 && count * _mask.Length >= 3 * Vector256<byte>.Count)
            {
                int total = count * _mask.Length;
                switch (_laneDistance)
                {
                    case 8:
                        CopyLanes<Lanes64>(ref destination, ref source, total);
                        break;
                    case 4:
                        CopyLanes<Lanes32>(ref destination, ref source, total);
                        break;
                    case 2:
                        CopyLanes<Lanes16>(ref destination, ref source, total);
                        break;
                    default:
                        CopyLanes<Lanes8>(ref destination, ref source, total);
                        break;
                }
                // The first vector, stored without its flags (they may lie before the run), cleared after.
                int first = Math.Min(count, (Unsafe.SizeOf<Vector256<byte>>() + _mask.Length - 1) / _mask.Length);
                ClearAbsent(ref destination, first);
                return;
            }
#endif
            CopyMasked(ref destination, ref source, count);
            if (_absent != null)
                ClearAbsent(ref destination, count);
        }

#if NET8_0_OR_GREATER
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
                if (absent == null || !Avx512Vbmi.IsSupported)
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
            Multiply(current, Avx512F.TernaryLogic(
                Avx512Vbmi.PermuteVar64x8x2(previous, index, current), follows, kept, FollowedOrKept));

        /// <summary>
        /// <paramref name="value"/>'s bytes times <paramref name="by"/>'s, each 0 or 1 (a flag, or a constant): vgf2p8mulb
        /// with GFNI on .NET 10, else <paramref name="value"/> ANDed with 0 - <paramref name="by"/> (vpsubb, vpandq), as
        /// .NET 8 has no GFNI API. Without it .NET 8 cleared the absent values element by element after the copy: three
        /// times the copy for 1,000 structs of an int, a long? and a double? (#301).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector512<byte> Multiply(Vector512<byte> value, Vector512<byte> by)
        {
#if NET10_0_OR_GREATER
            if (Gfni.V512.IsSupported)
                return Gfni.V512.GaloisFieldMultiply(value, by);
#endif
            return value & (Vector512<byte>.Zero - by);
        }

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
            return Multiply(value, Avx512F.TernaryLogic(
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
                    var value = Multiply(current, Avx512F.TernaryLogic(
                        Avx512Vbmi.PermuteVar64x8(current, index), follows, kept, FollowedOrKept));
                    if (nested)
                        value = Multiply(value, Avx512F.TernaryLogic(
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

#if NET8_0_OR_GREATER
        /// <summary>
        /// The distance from each absent value to its flag where it equals the value's size for every value, a
        /// power of two up to 8 (a <c>Nullable</c> of a primitive: its flag, padding up to the value's alignment, the
        /// value), every value at a multiple of it in an element of <paramref name="size"/> bytes, a multiple too (so
        /// lanes of that width from a run's start are the values' lanes in every element: not a packed struct), and
        /// no value holds another's flag; 0 otherwise.
        /// </summary>
        private static int LaneDistance(int[] absent, int size)
        {
            if (absent == null)
                return 0;
            int distance = absent[2];
            if (distance is not (1 or 2 or 4 or 8) || size % distance != 0)
                return 0;
            for (int i = 0; i < absent.Length; i += 3)
            {
                if (absent[i + 2] != distance || absent[i + 1] - absent[i] != distance || absent[i + 1] % distance != 0)
                    return 0;
                for (int j = 0; j < absent.Length; j += 3)
                    if (absent[j] >= absent[i + 1] && absent[j] < absent[i + 1] + distance)
                        return 0;
            }

            return distance;
        }

        /// <summary>
        /// The masked copy (<see cref="CopyVectors{TVector, TOps}"/>, aligned to the source) with the absent values cleared
        /// in the same pass, for elements whose values are lanes of <see cref="_laneDistance"/> bytes each with its
        /// flag at the start of the lane before: each vector is also loaded that many bytes earlier, where every
        /// value lane finds its flag lane, tested by one compare of lanes of that width. Without it the values were
        /// cleared element by element after the copy: 1,000 structs of an int, a long? and a double? took 3.3 us on
        /// EPYC 7763 (AVX2) and 3.1 on Neoverse-N2, against 0.8 and 0.6 for MemoryPack's plain copy (run
        /// 43714421476). The first vector is stored without the clear (its flags may lie before the run): the
        /// caller clears its elements after.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private unsafe void CopyLanes<TLanes>(ref byte destination, ref byte source, int total)
            where TLanes : struct, ILaneWidth
        {
            if (Vector256.IsHardwareAccelerated)
                CopyLanes<Vector256<byte>, TLanes>(ref destination, ref source, total);
            else
                CopyLanes<Vector128<byte>, TLanes>(ref destination, ref source, total);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private unsafe void CopyLanes<TVector, TLanes>(ref byte destination, ref byte source, int total)
            where TVector : struct
            where TLanes : struct, ILaneWidth
        {
            nuint width = (nuint)Unsafe.SizeOf<TVector>();
            nuint period = (nuint)_lanePeriod;
            nuint d = (nuint)default(TLanes).Distance;
            ref byte mask = ref NinoUnsafe.GetArrayDataReference(_laneMask);
            ref byte values = ref NinoUnsafe.GetArrayDataReference(_laneValues);
            And<TVector>(ref destination, ref source, 0, ref mask, 0);

            nuint end = (nuint)total - width;
            // Every vector at a multiple of d into the run, so its lanes are the values' lanes: aligned to the source
            // (whose values are aligned to d, so aligned loads keep that), not the destination, which follows a
            // 4-byte length and would put the lanes 4 bytes off; rounded down for a source that is not.
            nuint i = width - ((nuint)Unsafe.AsPointer(ref source) & (width - 1));
            i -= i & (d - 1);
            if (i < d)
            {
                // The first vector's flags would lie before the run: one vector from d first.
                Lane<TVector, TLanes>(ref destination, ref source, d, ref mask, ref values, d);
                i += width;
            }

            nuint m = i >= period ? i - period : i; // i % period (i < 2 * width <= 2 * period)
            if (_laneIndex != null)
            {
                // x64 with SSSE3: three operations a vector (pshufb, por, psignb) where the compare took five.
                CopyTabled<TVector, TLanes, Signs>(ref destination, ref source, i, end, period, m,
                    ref NinoUnsafe.GetArrayDataReference(_laneIndex[(int)(i & 15) / (int)d]),
                    ref NinoUnsafe.GetArrayDataReference(_laneIndex[(int)(end & 15) / (int)d]),
                    ref NinoUnsafe.GetArrayDataReference(_laneOnes));
                return;
            }

            if (_laneFlags != null)
            {
                // Arm64: three operations a vector (cmtst, orr, and) where the compare took five.
                ref byte flags = ref NinoUnsafe.GetArrayDataReference(_laneFlags);
                CopyTabled<TVector, TLanes, Tests>(ref destination, ref source, i, end, period, m, ref flags,
                    ref flags, ref NinoUnsafe.GetArrayDataReference(_laneOthers));
                return;
            }

            for (; i <= end; i += width)
            {
                Lane<TVector, TLanes>(ref destination, ref source, i, ref mask, ref values, m);
                m += width;
                if (m >= period)
                    m -= period;
            }

            Lane<TVector, TLanes>(ref destination, ref source, end, ref mask, ref values, TailOffset(m, i, end, period));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void And<TVector>(ref byte destination, ref byte source, nuint offset, ref byte mask, nuint m)
            where TVector : struct
        {
            if (typeof(TVector) == typeof(Vector256<byte>))
                (Vector256.LoadUnsafe(ref source, offset) & Vector256.LoadUnsafe(ref mask, m))
                    .StoreUnsafe(ref destination, offset);
            else
                (Vector128.LoadUnsafe(ref source, offset) & Vector128.LoadUnsafe(ref mask, m))
                    .StoreUnsafe(ref destination, offset);
        }

        /// <summary>One vector of <see cref="CopyLanes{TLanes}"/> at <paramref name="offset"/>, its tables at <paramref name="m"/>.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void Lane<TVector, TLanes>(ref byte destination, ref byte source, nuint offset, ref byte mask,
            ref byte values, nuint m) where TVector : struct where TLanes : struct, ILaneWidth
        {
            var lanes = default(TLanes);
            nuint d = (nuint)lanes.Distance;
            if (typeof(TVector) == typeof(Vector256<byte>))
            {
                var absent = lanes.Absent(Vector256.LoadUnsafe(ref source, offset - d));
                var kept = Vector256.LoadUnsafe(ref source, offset) & Vector256.LoadUnsafe(ref mask, m);
                Vector256.AndNot(kept, absent & Vector256.LoadUnsafe(ref values, m)).StoreUnsafe(ref destination, offset);
            }
            else
            {
                var absent = lanes.Absent(Vector128.LoadUnsafe(ref source, offset - d));
                var kept = Vector128.LoadUnsafe(ref source, offset) & Vector128.LoadUnsafe(ref mask, m);
                Vector128.AndNot(kept, absent & Vector128.LoadUnsafe(ref values, m)).StoreUnsafe(ref destination, offset);
            }
        }

        /// <summary>
        /// The loop of <see cref="CopyLanes{TLanes}"/> through the shorter kernels (<typeparamref name="TOp"/>), each
        /// vector with two tables at its place in the period: from <paramref name="i"/> (<paramref name="m"/> its
        /// place) to the vector at <paramref name="end"/> (<paramref name="first"/> the first table there, for x64's
        /// rows by phase). A period of up to five vectors keeps its tables in registers, whole periods a step: with
        /// the tables loaded per vector, 300 structs of an int, a long? and a double? took 500 ns against 310 (local,
        /// DOTNET_EnableAVX512=0).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void CopyTabled<TVector, TLanes, TOp>(ref byte destination, ref byte source, nuint i, nuint end,
            nuint period, nuint m, ref byte first, ref byte firstAtEnd, ref byte second)
            where TVector : struct where TLanes : struct, ILaneWidth where TOp : struct, ILaneOp
        {
            nuint width = (nuint)Unsafe.SizeOf<TVector>();
            switch (period / width)
            {
                case 1:
                    i = CopyPeriods<TVector, TLanes, TOp, LanePeriod1>(ref destination, ref source, i, end, period, m,
                        ref first, ref second);
                    break;
                case 2:
                    i = CopyPeriods<TVector, TLanes, TOp, LanePeriod2>(ref destination, ref source, i, end, period, m,
                        ref first, ref second);
                    break;
                case 3:
                    i = CopyPeriods<TVector, TLanes, TOp, LanePeriod3>(ref destination, ref source, i, end, period, m,
                        ref first, ref second);
                    break;
                case 4:
                    i = CopyPeriods<TVector, TLanes, TOp, LanePeriod4>(ref destination, ref source, i, end, period, m,
                        ref first, ref second);
                    break;
                case 5:
                    i = CopyPeriods<TVector, TLanes, TOp, LanePeriod5>(ref destination, ref source, i, end, period, m,
                        ref first, ref second);
                    break;
            }

            for (; i <= end; i += width)
            {
                Step<TVector, TLanes, TOp>(ref destination, ref source, i, Load<TVector>(ref first, m),
                    Load<TVector>(ref second, m));
                m += width;
                if (m >= period)
                    m -= period;
            }

            nuint last = TailOffset(m, i, end, period);
            Step<TVector, TLanes, TOp>(ref destination, ref source, end, Load<TVector>(ref firstAtEnd, last),
                Load<TVector>(ref second, last));
        }

        /// <summary>Whole periods of <typeparamref name="TPeriod"/> vectors, their tables in registers; returns where it stopped.</summary>
        // Out of line and fully optimized from the first call: five of them inlined into CopyLanes ran out of its
        // inlining budget and left some as calls anyway.
        [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
        private static nuint CopyPeriods<TVector, TLanes, TOp, TPeriod>(ref byte destination, ref byte source, nuint i,
            nuint end, nuint period, nuint m, ref byte first, ref byte second)
            where TVector : struct where TLanes : struct, ILaneWidth where TOp : struct, ILaneOp
            where TPeriod : struct, ILanePeriod
        {
            nuint width = (nuint)Unsafe.SizeOf<TVector>();
            int vectors = TPeriod.Vectors;
            nuint m1 = Wrap(m + width, period), m2 = Wrap(m1 + width, period), m3 = Wrap(m2 + width, period),
                m4 = Wrap(m3 + width, period);
            TVector a0 = Load<TVector>(ref first, m), b0 = Load<TVector>(ref second, m);
            TVector a1 = vectors > 1 ? Load<TVector>(ref first, m1) : default,
                b1 = vectors > 1 ? Load<TVector>(ref second, m1) : default;
            TVector a2 = vectors > 2 ? Load<TVector>(ref first, m2) : default,
                b2 = vectors > 2 ? Load<TVector>(ref second, m2) : default;
            TVector a3 = vectors > 3 ? Load<TVector>(ref first, m3) : default,
                b3 = vectors > 3 ? Load<TVector>(ref second, m3) : default;
            TVector a4 = vectors > 4 ? Load<TVector>(ref first, m4) : default,
                b4 = vectors > 4 ? Load<TVector>(ref second, m4) : default;
            nuint span = (nuint)vectors * width;
            for (; i + span - width <= end; i += span)
            {
                Step<TVector, TLanes, TOp>(ref destination, ref source, i, a0, b0);
                if (vectors > 1)
                    Step<TVector, TLanes, TOp>(ref destination, ref source, i + width, a1, b1);
                if (vectors > 2)
                    Step<TVector, TLanes, TOp>(ref destination, ref source, i + 2 * width, a2, b2);
                if (vectors > 3)
                    Step<TVector, TLanes, TOp>(ref destination, ref source, i + 3 * width, a3, b3);
                if (vectors > 4)
                    Step<TVector, TLanes, TOp>(ref destination, ref source, i + 4 * width, a4, b4);
            }

            return i;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static nuint Wrap(nuint m, nuint period) => m >= period ? m - period : m;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static TVector Load<TVector>(ref byte table, nuint m) where TVector : struct
        {
            if (typeof(TVector) == typeof(Vector256<byte>))
            {
                var v = Vector256.LoadUnsafe(ref table, m);
                return Unsafe.As<Vector256<byte>, TVector>(ref v);
            }

            var w = Vector128.LoadUnsafe(ref table, m);
            return Unsafe.As<Vector128<byte>, TVector>(ref w);
        }

        /// <summary>One vector at <paramref name="offset"/> through <typeparamref name="TOp"/> with its tables.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void Step<TVector, TLanes, TOp>(ref byte destination, ref byte source, nuint offset, TVector a,
            TVector b) where TVector : struct where TLanes : struct, ILaneWidth where TOp : struct, ILaneOp
        {
            nuint d = (nuint)default(TLanes).Distance;
            if (typeof(TVector) == typeof(Vector256<byte>))
                default(TOp).Apply<TLanes>(Vector256.LoadUnsafe(ref source, offset),
                        Vector256.LoadUnsafe(ref source, offset - d), Unsafe.As<TVector, Vector256<byte>>(ref a),
                        Unsafe.As<TVector, Vector256<byte>>(ref b))
                    .StoreUnsafe(ref destination, offset);
            else
                default(TOp).Apply<TLanes>(Vector128.LoadUnsafe(ref source, offset),
                        Vector128.LoadUnsafe(ref source, offset - d), Unsafe.As<TVector, Vector128<byte>>(ref a),
                        Unsafe.As<TVector, Vector128<byte>>(ref b))
                    .StoreUnsafe(ref destination, offset);
        }

        /// <summary>The vectors of a period whose tables <see cref="CopyPeriods{TVector, TLanes, TOp, TPeriod}"/> keeps in registers.</summary>
        private interface ILanePeriod
        {
            static abstract int Vectors { get; }
        }

        private readonly struct LanePeriod1 : ILanePeriod
        {
            public static int Vectors => 1;
        }

        private readonly struct LanePeriod2 : ILanePeriod
        {
            public static int Vectors => 2;
        }

        private readonly struct LanePeriod3 : ILanePeriod
        {
            public static int Vectors => 3;
        }

        private readonly struct LanePeriod4 : ILanePeriod
        {
            public static int Vectors => 4;
        }

        private readonly struct LanePeriod5 : ILanePeriod
        {
            public static int Vectors => 5;
        }

        /// <summary>
        /// A vector of <paramref name="value"/> as the wire holds it, from <paramref name="flags"/> (the vector one
        /// lane earlier, where each value lane finds its flag) and the two tables at its place.
        /// </summary>
        private interface ILaneOp
        {
            Vector256<byte> Apply<TLanes>(Vector256<byte> value, Vector256<byte> flags, Vector256<byte> a,
                Vector256<byte> b) where TLanes : struct, ILaneWidth;

            Vector128<byte> Apply<TLanes>(Vector128<byte> value, Vector128<byte> flags, Vector128<byte> a,
                Vector128<byte> b) where TLanes : struct, ILaneWidth;
        }

        /// <summary>
        /// x64 with SSSE3: each value byte takes its flag (pshufb with the indices <c>a</c>, 0 elsewhere), every other
        /// kept byte 1 (por <c>b</c>), and the bytes keep their sign by it (psignb: kept where 1, cleared where 0; a
        /// flag is 0 or 1 in every <c>Nullable</c> the runtime makes).
        /// </summary>
        private readonly struct Signs : ILaneOp
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public Vector256<byte> Apply<TLanes>(Vector256<byte> value, Vector256<byte> flags, Vector256<byte> a,
                Vector256<byte> b) where TLanes : struct, ILaneWidth =>
                Avx2.Sign(value.AsSByte(), (Avx2.Shuffle(flags, a) | b).AsSByte()).AsByte();

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public Vector128<byte> Apply<TLanes>(Vector128<byte> value, Vector128<byte> flags, Vector128<byte> a,
                Vector128<byte> b) where TLanes : struct, ILaneWidth =>
                Ssse3.Sign(value.AsSByte(), (Ssse3.Shuffle(flags, a) | b).AsSByte()).AsByte();
        }

        /// <summary>
        /// Arm64: the value lanes whose flag is set (cmtst with <c>a</c>, a flag byte in each value lane), with the
        /// mask of the other bytes (orr <c>b</c>), keep their bytes (and).
        /// </summary>
        private readonly struct Tests : ILaneOp
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public Vector256<byte> Apply<TLanes>(Vector256<byte> value, Vector256<byte> flags, Vector256<byte> a,
                Vector256<byte> b) where TLanes : struct, ILaneWidth =>
                Vector256.Create(Apply<TLanes>(value.GetLower(), flags.GetLower(), a.GetLower(), b.GetLower()),
                    Apply<TLanes>(value.GetUpper(), flags.GetUpper(), a.GetUpper(), b.GetUpper()));

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public Vector128<byte> Apply<TLanes>(Vector128<byte> value, Vector128<byte> flags, Vector128<byte> a,
                Vector128<byte> b) where TLanes : struct, ILaneWidth =>
                value & (default(TLanes).Present(flags, a) | b);
        }

        /// <summary>
        /// The lanes of <see cref="CopyLanes{TLanes}"/>: their width (the distance from a value to its flag) and the
        /// lanes whose first byte is 0 (all ones), from the vector loaded that far before.
        /// </summary>
        private interface ILaneWidth
        {
            int Distance { get; }
            Vector256<byte> Absent(Vector256<byte> flags);
            Vector128<byte> Absent(Vector128<byte> flags);

            /// <summary>All ones in the lanes where <paramref name="flags"/> and <paramref name="test"/> share a bit (cmtst).</summary>
            Vector128<byte> Present(Vector128<byte> flags, Vector128<byte> test);
        }

        private readonly struct Lanes64 : ILaneWidth
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public Vector128<byte> Present(Vector128<byte> flags, Vector128<byte> test) =>
                (AdvSimd.Arm64.IsSupported
                    ? AdvSimd.Arm64.CompareTest(flags.AsUInt64(), test.AsUInt64())
                    : ~Vector128.Equals(flags.AsUInt64() & test.AsUInt64(), Vector128<ulong>.Zero)).AsByte();

            public int Distance => 8;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public Vector256<byte> Absent(Vector256<byte> flags) =>
                Vector256.Equals(flags.AsUInt64() & Vector256.Create(0xFFUL), Vector256<ulong>.Zero).AsByte();

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public Vector128<byte> Absent(Vector128<byte> flags) =>
                Vector128.Equals(flags.AsUInt64() & Vector128.Create(0xFFUL), Vector128<ulong>.Zero).AsByte();
        }

        private readonly struct Lanes32 : ILaneWidth
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public Vector128<byte> Present(Vector128<byte> flags, Vector128<byte> test) =>
                (AdvSimd.Arm64.IsSupported
                    ? AdvSimd.CompareTest(flags.AsUInt32(), test.AsUInt32())
                    : ~Vector128.Equals(flags.AsUInt32() & test.AsUInt32(), Vector128<uint>.Zero)).AsByte();

            public int Distance => 4;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public Vector256<byte> Absent(Vector256<byte> flags) =>
                Vector256.Equals(flags.AsUInt32() & Vector256.Create(0xFFu), Vector256<uint>.Zero).AsByte();

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public Vector128<byte> Absent(Vector128<byte> flags) =>
                Vector128.Equals(flags.AsUInt32() & Vector128.Create(0xFFu), Vector128<uint>.Zero).AsByte();
        }

        private readonly struct Lanes16 : ILaneWidth
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public Vector128<byte> Present(Vector128<byte> flags, Vector128<byte> test) =>
                (AdvSimd.Arm64.IsSupported
                    ? AdvSimd.CompareTest(flags.AsUInt16(), test.AsUInt16())
                    : ~Vector128.Equals(flags.AsUInt16() & test.AsUInt16(), Vector128<ushort>.Zero)).AsByte();

            public int Distance => 2;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public Vector256<byte> Absent(Vector256<byte> flags) =>
                Vector256.Equals(flags.AsUInt16() & Vector256.Create((ushort)0xFF), Vector256<ushort>.Zero).AsByte();

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public Vector128<byte> Absent(Vector128<byte> flags) =>
                Vector128.Equals(flags.AsUInt16() & Vector128.Create((ushort)0xFF), Vector128<ushort>.Zero).AsByte();
        }

        private readonly struct Lanes8 : ILaneWidth
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public Vector128<byte> Present(Vector128<byte> flags, Vector128<byte> test) =>
                (AdvSimd.Arm64.IsSupported
                    ? AdvSimd.CompareTest(flags, test)
                    : ~Vector128.Equals(flags & test, Vector128<byte>.Zero)).AsByte();

            public int Distance => 1;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public Vector256<byte> Absent(Vector256<byte> flags) => Vector256.Equals(flags, Vector256<byte>.Zero);

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public Vector128<byte> Absent(Vector128<byte> flags) => Vector128.Equals(flags, Vector128<byte>.Zero);
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

#if !NET8_0_OR_GREATER
        /// <summary>Whether the <paramref name="total"/> bytes from each of the two do not overlap.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool Apart(ref byte source, ref byte destination, int total)
        {
            long distance = (long)Unsafe.ByteOffset(ref source, ref destination);
            return distance >= total || distance <= -total;
        }

#endif
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void CopyMasked(ref byte destination, ref byte source, int count)
        {
            int total = checked(count * _mask.Length);
#if !NET8_0_OR_GREATER
            // The netstandard2.1 build (Unity, Mono): the registered kernels, for runs long enough whose two ranges are
            // apart, as NinoKernels.Permute requires. A copy over its own bytes (a set's slots cleared where they were
            // written, SlotsWriteInPlace) takes the word loop below, which loads each word before storing it: Burst's
            // 16-byte path stores whole vectors past a value and wrote zeros over the values after it (#378).
            if (count >= NinoKernelHook.MinCopies && _kernelTable != IntPtr.Zero &&
                Apart(ref source, ref destination, total) &&
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
