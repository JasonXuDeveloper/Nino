#if NET8_0_OR_GREATER
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Nino.Core.Internal
{
    // The AVX-512 paths: whole groups by two-source permutes, aligned loads, pairs.
    internal static partial class NinoBlockCollections
    {
        /// <summary>
        /// The groups of <see cref="WriteAll{T, TFormatter}"/>, two or more (one is cheaper as single vectors than as a
        /// call): returns the values written. Not inlined, so its dynamic PGO profile is that of long runs only:
        /// compiled inside WriteAll after short ones, the loop took 3.5 times as long (1,000 12-byte blocks: 577 ns
        /// instead of 160).
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static unsafe int WriteGroups<T, TFormatter>(ref byte destination, ref T first, int count)
            where TFormatter : struct, INinoFormatter<T>
        {
            int size = NinoBlockPlan<T, TFormatter>.Size;
            int i = 0;
            int group = NinoBlockPlan<T, TFormatter>.Group;
            int last = NinoBlockPlan<T, TFormatter>.LastGroup(count);
            ref byte source = ref Unsafe.As<T, byte>(ref first);
            // The groups from the first value whose blocks start on a vector boundary: the blocks usually follow a
            // 4-byte length, and a store across two cache lines costs two (measured on an AVX-512 Xeon, 1,000
            // 12-byte blocks: 230 ns unaligned, 150 to 170 aligned). The values before it, and those after the last
            // group, by one more group each, unaligned, over values the aligned groups also write (the same bytes):
            // one at a time, up to a group less one at each end took up to a fifth of the time (1,000 (int, long):
            // 123 to 149 ns by the alignment of the blocks, 100 to 125 with the two groups).
            int skip = FirstAligned((nuint)Unsafe.AsPointer(ref destination), size, 0, group, 63);
            if (skip > 0)
            {
                WriteGroup<T, TFormatter>(ref source, ref destination, 0, size);
                i = skip;
            }

            // Ice Lake: the groups whose memory lies inside the values loaded from 64-byte boundaries.
            if (NinoSimd.IceLake && NinoBlockPlan<T, TFormatter>.GroupWords)
                i = WriteAligned<T, TFormatter>(ref source, ref destination, i, count);

            if (i <= last)
            {
                // Cursors bumped a group at a time and a count down, not two multiplications per group.
                int n = (last - i) / group + 1;
                ref byte from = ref Unsafe.Add(ref source, (nint)i * Unsafe.SizeOf<T>());
                ref byte to = ref Unsafe.Add(ref destination, (nint)i * size);
                i += n * group;
                do
                {
                    Permute64x2<T, TFormatter>(ref from, 0, ref to, 0);
                    from = ref Unsafe.Add(ref from, (nint)group * Unsafe.SizeOf<T>());
                    to = ref Unsafe.Add(ref to, (nint)group * size);
                } while (--n != 0);
            }

            if (i < last + group)
            {
                WriteGroup<T, TFormatter>(ref source, ref destination, last, size);
                i = last + group;
            }

            return i;
        }

        /// <summary>
        /// The values from value <paramref name="i"/> of <paramref name="count"/>,
        /// <see cref="NinoBlockPlan{T, TFormatter}.PerPair"/> at a time, each step one two-source byte permute of 128
        /// bytes of their memory stored as 64 bytes of blocks (the bytes past the step's blocks rewritten by the next);
        /// the values after the last step by one more, over values already written (the same bytes). Returns the values
        /// written.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int WritePairs<T, TFormatter>(ref byte source, ref byte destination, int i, int count)
            where TFormatter : struct, INinoFormatter<T>
        {
            int size = NinoBlockPlan<T, TFormatter>.Size;
            int step = NinoBlockPlan<T, TFormatter>.PerPair;
            int last = NinoBlockPlan<T, TFormatter>.LastPair(count);
            if (i > last)
                return i;
            var toWire = NinoBlockPlan<T, TFormatter>.ToWire128;
            int n = (last - i) / step + 1;
            ref byte from = ref Unsafe.Add(ref source, (nint)i * Unsafe.SizeOf<T>());
            ref byte to = ref Unsafe.Add(ref destination, (nint)i * size);
            i += n * step;
            do
            {
                Avx512Vbmi.PermuteVar64x8x2(Vector512.LoadUnsafe(ref from), toWire, Vector512.LoadUnsafe(ref from, 64))
                    .StoreUnsafe(ref to);
                from = ref Unsafe.Add(ref from, (nint)step * Unsafe.SizeOf<T>());
                to = ref Unsafe.Add(ref to, (nint)step * size);
            } while (--n != 0);

            if (i < last + step)
            {
                from = ref Unsafe.Add(ref source, (nint)last * Unsafe.SizeOf<T>());
                Avx512Vbmi.PermuteVar64x8x2(Vector512.LoadUnsafe(ref from), toWire, Vector512.LoadUnsafe(ref from, 64))
                    .StoreUnsafe(ref destination, (nuint)last * (nuint)size);
                i = last + step;
            }

            return i;
        }

        /// <summary>
        /// Groups from value <paramref name="i"/> (a group start) with every memory vector loaded from a 64-byte
        /// boundary (<see cref="NinoBlockPlan{T, TFormatter}.Aligned"/>): on GitHub's Xeon Platinum 8370C (Ice Lake) a
        /// 64-byte load across two cache lines costs about twice an aligned one (16,000 bytes copied from 8 bytes past
        /// a line in 377 ns, from a line 190), and the groups' loads start wherever the values do. Groups that would
        /// load before the first value or past the last are left to the caller. Returns the next group start.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static unsafe int WriteAligned<T, TFormatter>(ref byte source, ref byte destination, int i, int count)
            where TFormatter : struct, INinoFormatter<T>
        {
            var tables = NinoBlockPlan<T, TFormatter>.Aligned.Tables;
            int[] spans = NinoBlockPlan<T, TFormatter>.Aligned.Spans;
            int shift = (int)(((nuint)Unsafe.AsPointer(ref source) + (nuint)i * (nuint)Unsafe.SizeOf<T>()) & 63);
            if (tables == null || (shift & 3) != 0)
                return i;
            int size = NinoBlockPlan<T, TFormatter>.Size;
            int group = NinoBlockPlan<T, TFormatter>.Group;
            int vectors = NinoBlockPlan<T, TFormatter>.GroupVectors;
            // The first group whose aligned start is not before the values (those before it unaligned, as the
            // caller's), the last whose loads end inside them.
            int first = i;
            while ((long)first * Unsafe.SizeOf<T>() < shift)
                first += group;
            long end = (long)count * Unsafe.SizeOf<T>() + shift - NinoBlockPlan<T, TFormatter>.Aligned.Reach;
            if (end < (long)first * Unsafe.SizeOf<T>())
                return i;
            for (; i < first; i += group)
                WriteGroup<T, TFormatter>(ref source, ref destination, i, size);
            int n = (int)((end / Unsafe.SizeOf<T>() - i) / group) + 1;

            int slot = shift / 4 * 4;
            // Everything per vector of blocks out of the loop: its tables, its first memory vector, two or three.
            var inner0 = tables[slot * 2];
            var outer0 = tables[slot * 2 + 1];
            int span0 = spans[slot];
            Vector512<byte> inner1 = default, outer1 = default, inner2 = default, outer2 = default, inner3 = default,
                outer3 = default;
            int span1 = 0, span2 = 0, span3 = 0;
            if (vectors > 1)
            {
                inner1 = tables[slot * 2 + 2];
                outer1 = tables[slot * 2 + 3];
                span1 = spans[slot + 1];
            }

            if (vectors > 2)
            {
                inner2 = tables[slot * 2 + 4];
                outer2 = tables[slot * 2 + 5];
                span2 = spans[slot + 2];
            }

            if (vectors > 3)
            {
                inner3 = tables[slot * 2 + 6];
                outer3 = tables[slot * 2 + 7];
                span3 = spans[slot + 3];
            }

            nuint low0 = (nuint)(span0 & ~1), low1 = (nuint)(span1 & ~1), low2 = (nuint)(span2 & ~1),
                low3 = (nuint)(span3 & ~1);
            bool three0 = (span0 & 1) != 0, three1 = (span1 & 1) != 0, three2 = (span2 & 1) != 0,
                three3 = (span3 & 1) != 0;
            ref byte from = ref Unsafe.Add(ref source, (nint)i * Unsafe.SizeOf<T>() - shift);
            ref byte to = ref Unsafe.Add(ref destination, (nint)i * size);
            i += n * group;
            do
            {
                AlignedVector(ref from, low0, three0, inner0, outer0).StoreUnsafe(ref to);
                if (vectors > 1)
                    AlignedVector(ref from, low1, three1, inner1, outer1).StoreUnsafe(ref to, 64);
                if (vectors > 2)
                    AlignedVector(ref from, low2, three2, inner2, outer2).StoreUnsafe(ref to, 128);
                if (vectors > 3)
                    AlignedVector(ref from, low3, three3, inner3, outer3).StoreUnsafe(ref to, 192);
                from = ref Unsafe.Add(ref from, (nint)group * Unsafe.SizeOf<T>());
                to = ref Unsafe.Add(ref to, (nint)group * size);
            } while (--n != 0);

            return i;
        }

        /// <summary>
        /// One vector of blocks of <see cref="WriteAligned{T, TFormatter}"/>, from the aligned memory vector
        /// <paramref name="low"/> bytes past the group's aligned start, and the next two when <paramref name="three"/>.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector512<byte> AlignedVector(ref byte at, nuint low, bool three, Vector512<byte> inner,
            Vector512<byte> outer)
        {
            var vector = Avx512F.PermuteVar16x32x2(Vector512.LoadUnsafe(ref at, low).AsInt32(), inner.AsInt32(),
                Vector512.LoadUnsafe(ref at, low + 64).AsInt32());
            if (three)
                vector = Avx512F.PermuteVar16x32x2(vector, outer.AsInt32(),
                    Vector512.LoadUnsafe(ref at, low + 128).AsInt32());
            return vector.AsByte();
        }

        /// <summary>The group of values from value <paramref name="i"/>
        /// (<see cref="Permute64x2{T, TFormatter}"/>).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void WriteGroup<T, TFormatter>(ref byte source, ref byte destination, int i, int size)
            where TFormatter : struct, INinoFormatter<T> =>
            Permute64x2<T, TFormatter>(ref source, (nuint)i * (nuint)Unsafe.SizeOf<T>(), ref destination,
                (nuint)i * (nuint)size);

        /// <summary>
        /// The read groups of <see cref="ReadAll{T, TFormatter}"/>, two or more: returns the values read. Not inlined,
        /// as <see cref="WriteGroups{T, TFormatter}"/>. Stores are aligned: the plan whose vectors start on a 64-byte
        /// boundary at a value, or 8 bytes into one.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static unsafe int ReadGroups<T, TFormatter>(ref byte source, ref T first, int count)
            where TFormatter : struct, INinoFormatter<T>
        {
            int size = NinoBlockPlan<T, TFormatter>.Size;
            nuint at = (nuint)Unsafe.AsPointer(ref first);
            if (NinoBlockPlan<T, TFormatter>.ReadPlan<NinoPhase8>.Enabled && at % 16 == 8)
                return ReadGroups<T, TFormatter, NinoPhase8>(ref source, ref first, count, size, at);
            return ReadGroups<T, TFormatter, NinoPhase0>(ref source, ref first, count, size, at);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int ReadGroups<T, TFormatter, TPhase>(ref byte source, ref T first, int count, int size,
            nuint at)
            where TFormatter : struct, INinoFormatter<T>
            where TPhase : struct, INinoPhase
        {
            int i = 0, phase = default(TPhase).Bytes;
            int group = NinoBlockPlan<T, TFormatter>.ReadPlan<TPhase>.Group;
            int last = NinoBlockPlan<T, TFormatter>.ReadPlan<TPhase>.Last(count);
            ref byte destination = ref Unsafe.As<T, byte>(ref first);
            // The groups from the first value whose vectors start on a 64-byte boundary (from the first value,
            // unaligned, when none does); the values before it, and those after the last group, by one more group
            // each, unaligned (as WriteGroups). With a phase, the first value is read whole too: a group's vectors
            // start past its first bytes, and each group's last vector writes those of the next.
            int skip = FirstAligned(at + (nuint)phase, Unsafe.SizeOf<T>(), 0, group, 63);
            if (phase > 0)
                ReadOne<T, TFormatter>(ref source, ref first);

            // The indices from an array, not constants, so they stay in registers: the JIT reloads a vector
            // constant at every use (1,000 (int, long) into an array: 135 to 150 ns, against 115 to 130 with them in
            // registers).
            var masks = NinoBlockPlan<T, TFormatter>.ReadPlan<TPhase>.Masks;
            Vector512<byte> m0 = masks[0], m1 = masks[1], m2 = masks[2], m3 = masks[3];
            var keeps = NinoBlockPlan<T, TFormatter>.ReadPlan<TPhase>.Keeps;
            Vector512<byte> k0 = keeps[0], k1 = keeps[1], k2 = keeps[2], k3 = keeps[3];
            var bools = NinoBlockPlan<T, TFormatter>.ReadPlan<TPhase>.BoolMasks;
            Vector512<byte> b0 = bools[0], b1 = bools[1], b2 = bools[2], b3 = bools[3], b4 = bools[4];
            var bad = Vector512<byte>.Zero;
            if (skip > 0)
            {
                ReadGroup<T, TFormatter, TPhase>(ref source, 0, ref destination, (nuint)phase, m0, m1, m2, m3, k0, k1,
                    k2, k3, b0, b1, b2, b3, b4, ref bad);
                i = skip;
            }

            if (i <= last)
            {
                // As in WriteGroups.
                int n = (last - i) / group + 1;
                ref byte from = ref Unsafe.Add(ref source, (nint)i * size);
                ref byte to = ref Unsafe.Add(ref destination, (nint)i * Unsafe.SizeOf<T>() + phase);
                i += n * group;
                do
                {
                    ReadGroup<T, TFormatter, TPhase>(ref from, 0, ref to, 0, m0, m1, m2, m3, k0, k1, k2, k3, b0, b1, b2,
                        b3, b4, ref bad);
                    from = ref Unsafe.Add(ref from, (nint)group * size);
                    to = ref Unsafe.Add(ref to, (nint)group * Unsafe.SizeOf<T>());
                } while (--n != 0);
            }

            if (i < last + group)
            {
                ReadGroup<T, TFormatter, TPhase>(ref source, (nuint)last * (nuint)size, ref destination,
                    (nuint)last * (nuint)Unsafe.SizeOf<T>() + (nuint)phase, m0, m1, m2, m3, k0, k1, k2, k3, b0, b1, b2,
                    b3, b4, ref bad);
                i = last + group;
            }

            // One test for all the groups' bools (ReadPlan.Checks): the strided pass names the bad one.
            if (NinoBlockPlan<T, TFormatter>.ReadPlan<TPhase>.Checks && bad != Vector512<byte>.Zero)
                CheckBools<T, TFormatter>(ref source, count);
            return i;
        }

        /// <summary>
        /// One read group: the vectors of blocks loaded once, each memory vector one permute of two, its padding
        /// cleared; with <see cref="NinoBlockPlan{T, TFormatter}.ReadPlan{TPhase}.Checks"/>, the bools' high bits ORed
        /// into <paramref name="bad"/>.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void ReadGroup<T, TFormatter, TPhase>(ref byte source, nuint from, ref byte destination,
            nuint to, Vector512<byte> m0, Vector512<byte> m1, Vector512<byte> m2, Vector512<byte> m3,
            Vector512<byte> k0, Vector512<byte> k1, Vector512<byte> k2, Vector512<byte> k3, Vector512<byte> b0,
            Vector512<byte> b1, Vector512<byte> b2, Vector512<byte> b3, Vector512<byte> b4, ref Vector512<byte> bad)
            where TFormatter : struct, INinoFormatter<T>
            where TPhase : struct, INinoPhase
        {
            int loads = NinoBlockPlan<T, TFormatter>.ReadPlan<TPhase>.Loads;
            int vectors = NinoBlockPlan<T, TFormatter>.ReadPlan<TPhase>.Vectors;
            var w0 = Vector512.LoadUnsafe(ref source, from);
            var w1 = loads > 1 ? Vector512.LoadUnsafe(ref source, from + 64) : default;
            var w2 = loads > 2 ? Vector512.LoadUnsafe(ref source, from + 128) : default;
            var w3 = loads > 3 ? Vector512.LoadUnsafe(ref source, from + 192) : default;
            var w4 = loads > 4 ? Vector512.LoadUnsafe(ref source, from + 256) : default;
            if (NinoBlockPlan<T, TFormatter>.ReadPlan<TPhase>.Checks)
            {
                bad |= w0 & b0;
                if (loads > 1)
                    bad |= w1 & b1;
                if (loads > 2)
                    bad |= w2 & b2;
                if (loads > 3)
                    bad |= w3 & b3;
                if (loads > 4)
                    bad |= w4 & b4;
            }
            ReadVector<T, TFormatter, TPhase>(NinoBlockPlan<T, TFormatter>.ReadPlan<TPhase>.Base0, w0, w1, w2, w3, w4, m0,
                k0)
                .StoreUnsafe(ref destination, to);
            if (vectors < 2)
                return;
            ReadVector<T, TFormatter, TPhase>(NinoBlockPlan<T, TFormatter>.ReadPlan<TPhase>.Base1, w0, w1, w2, w3, w4, m1,
                k1)
                .StoreUnsafe(ref destination, to + 64);
            if (vectors < 3)
                return;
            ReadVector<T, TFormatter, TPhase>(NinoBlockPlan<T, TFormatter>.ReadPlan<TPhase>.Base2, w0, w1, w2, w3, w4, m2,
                k2)
                .StoreUnsafe(ref destination, to + 128);
            if (vectors < 4)
                return;
            ReadVector<T, TFormatter, TPhase>(NinoBlockPlan<T, TFormatter>.ReadPlan<TPhase>.Base3, w0, w1, w2, w3, w4, m3,
                k3)
                .StoreUnsafe(ref destination, to + 192);
        }

        /// <summary>
        /// A memory vector from vectors of blocks <paramref name="pair"/> and the next (a constant); the next is not
        /// loaded (default) when the vector draws on <paramref name="pair"/> alone.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector512<byte> ReadVector<T, TFormatter, TPhase>(int pair, Vector512<byte> w0,
            Vector512<byte> w1, Vector512<byte> w2, Vector512<byte> w3, Vector512<byte> w4, Vector512<byte> mask,
            Vector512<byte> keep)
            where TFormatter : struct, INinoFormatter<T>
            where TPhase : struct, INinoPhase
        {
            var low = pair == 0 ? w0 : pair == 1 ? w1 : pair == 2 ? w2 : w3;
            var high = pair == 0 ? w1 : pair == 1 ? w2 : pair == 2 ? w3 : w4;
            var vector = NinoBlockPlan<T, TFormatter>.ReadPlan<TPhase>.Words
                ? Avx512F.PermuteVar16x32x2(low.AsInt32(), mask.AsInt32(), high.AsInt32()).AsByte()
                : Avx512Vbmi.PermuteVar64x8x2(low, mask, high);
            // Padding takes whatever its index points at; cleared, so that every byte of the values is written.
            return NinoBlockPlan<T, TFormatter>.ReadPlan<TPhase>.Pads ? vector & keep : vector;
        }

        /// <summary>
        /// One group of values (<see cref="NinoBlockPlan{T, TFormatter}.Groups"/>) with AVX-512: vector k of blocks is
        /// one two-source permute (<see cref="Pair{T, TFormatter}"/>) of memory vectors k and k + 1, each memory vector
        /// loaded once.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void Permute64x2<T, TFormatter>(ref byte source, nuint from, ref byte destination, nuint to)
            where TFormatter : struct, INinoFormatter<T>
        {
            int vectors = NinoBlockPlan<T, TFormatter>.GroupVectors;
            var a = Vector512.LoadUnsafe(ref source, from);
            var b = Vector512.LoadUnsafe(ref source, from + 64);
            Pair<T, TFormatter>(a, NinoBlockPlan<T, TFormatter>.Group0, b).StoreUnsafe(ref destination, to);
            if (vectors < 2)
                return;
            var c = Vector512.LoadUnsafe(ref source, from + 128);
            Pair<T, TFormatter>(b, NinoBlockPlan<T, TFormatter>.Group1, c).StoreUnsafe(ref destination, to + 64);
            if (vectors < 3)
                return;
            var d = Vector512.LoadUnsafe(ref source, from + 192);
            Pair<T, TFormatter>(c, NinoBlockPlan<T, TFormatter>.Group2, d).StoreUnsafe(ref destination, to + 128);
            if (vectors < 4)
                return;
            var e = Vector512.LoadUnsafe(ref source, from + 256);
            Pair<T, TFormatter>(d, NinoBlockPlan<T, TFormatter>.Group3, e).StoreUnsafe(ref destination, to + 192);
        }

        /// <summary>
        /// The two-source permute of a write group: 4-byte words (vpermt2d, AVX-512F) where every block moves whole
        /// words (<see cref="NinoBlockPlan{T, TFormatter}.GroupWords"/>), else bytes (vpermt2b, AVX-512 VBMI).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector512<byte> Pair<T, TFormatter>(Vector512<byte> low, Vector512<byte> mask,
            Vector512<byte> high)
            where TFormatter : struct, INinoFormatter<T> =>
            Avx512F.IsSupported && NinoBlockPlan<T, TFormatter>.GroupWords
                ? Avx512F.PermuteVar16x32x2(low.AsInt32(), mask.AsInt32(), high.AsInt32()).AsByte()
                : Avx512Vbmi.PermuteVar64x8x2(low, mask, high);
    }
}
#endif
