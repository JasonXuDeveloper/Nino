#if NET8_0_OR_GREATER
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;

namespace Nino.Core.Internal
{
    // Groups moved as 16-byte lanes (AVX2, SSSE3, Arm64, WebAssembly), as LanePlan describes.
    internal static partial class NinoBlockCollections
    {
        /// <summary>
        /// Moves values a group at a time as <see cref="NinoBlockPlan{T, TFormatter}.LanePlan{TDirection}"/> describes,
        /// from their memory to their blocks (<see cref="NinoWrites"/>) or back: returns the values moved. Not inlined,
        /// as <see cref="WriteGroups{T, TFormatter}"/>.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static unsafe int Lanes<T, TFormatter, TDirection>(ref byte source, ref byte destination, int count)
            where TFormatter : struct, INinoFormatter<T>
            where TDirection : struct, INinoDirection
        {
            int size = NinoBlockPlan<T, TFormatter>.Size;
            bool writes = typeof(TDirection) == typeof(NinoWrites);
            int from = writes ? Unsafe.SizeOf<T>() : size, to = writes ? size : Unsafe.SizeOf<T>();
            int group = NinoBlockPlan<T, TFormatter>.LanePlan<TDirection>.Group;
            int width = NinoBlockPlan<T, TFormatter>.LanePlan<TDirection>.Width;
            int last = NinoBlockPlan<T, TFormatter>.LanePlan<TDirection>.Last(count, from, to);
            // The first value whose loads start inside the source (a window may start before the group's values),
            // then the first from there whose vectors start on a boundary of their width, so that no store straddles
            // two cache lines (when one does). The values before the first one at a time; those from it to the
            // aligned one, and those after the last group, by one more group each, unaligned (as WriteGroups; the
            // stores of a group are its values, and at most part of the next value's, which is moved after it).
            int start = NinoBlockPlan<T, TFormatter>.LanePlan<TDirection>.Start;
            int skip = FirstAligned((nuint)Unsafe.AsPointer(ref destination), to, start, group, (nuint)(width - 1));
            int i = 0;
            for (; i < start; i++)
                Move<T, TFormatter, TDirection>(ref source, ref destination, i, size);

            // The indices from an array, so they stay in registers (see ReadGroups).
            if (width == 32)
            {
                var masks = NinoBlockPlan<T, TFormatter>.LanePlan<TDirection>.Masks32;
                Vector256<byte> a0 = masks[0], b0 = masks[1], a1 = masks[2], b1 = masks[3], a2 = masks[4],
                    b2 = masks[5], a3 = masks[6], b3 = masks[7];
                if (skip > start && start <= last)
                {
                    Lanes32<T, TFormatter, TDirection>(ref Unsafe.Add(ref source, (nint)start * from),
                        ref Unsafe.Add(ref destination, (nint)start * to), a0, b0, a1, b1, a2, b2, a3, b3);
                    i = skip;
                }

                if (i <= last)
                {
                    // Cursors bumped a group at a time and a count down, not a multiply and an add per group.
                    int n = (last - i) / group + 1;
                    ref byte at = ref Unsafe.Add(ref source, (nint)i * from);
                    ref byte into = ref Unsafe.Add(ref destination, (nint)i * to);
                    i += n * group;
                    do
                    {
                        Lanes32<T, TFormatter, TDirection>(ref at, ref into, a0, b0, a1, b1, a2, b2, a3, b3);
                        at = ref Unsafe.Add(ref at, (nint)group * from);
                        into = ref Unsafe.Add(ref into, (nint)group * to);
                    } while (--n != 0);
                }

                if (i < last + group && last >= start)
                {
                    Lanes32<T, TFormatter, TDirection>(ref Unsafe.Add(ref source, (nint)last * from),
                        ref Unsafe.Add(ref destination, (nint)last * to), a0, b0, a1, b1, a2, b2, a3, b3);
                    i = last + group;
                }
            }
            else
            {
                var masks = NinoBlockPlan<T, TFormatter>.LanePlan<TDirection>.Masks16;
                Vector128<byte> a0 = masks[0], b0 = masks[1], a1 = masks[2], b1 = masks[3], a2 = masks[4],
                    b2 = masks[5], a3 = masks[6], b3 = masks[7];
                if (skip > start && start <= last)
                {
                    Lanes16<T, TFormatter, TDirection>(ref Unsafe.Add(ref source, (nint)start * from),
                        ref Unsafe.Add(ref destination, (nint)start * to), a0, b0, a1, b1, a2, b2, a3, b3);
                    i = skip;
                }

                if (i <= last)
                {
                    // Cursors bumped a group at a time and a count down, not a multiply and an add per group.
                    int n = (last - i) / group + 1;
                    ref byte at = ref Unsafe.Add(ref source, (nint)i * from);
                    ref byte into = ref Unsafe.Add(ref destination, (nint)i * to);
                    i += n * group;
                    do
                    {
                        Lanes16<T, TFormatter, TDirection>(ref at, ref into, a0, b0, a1, b1, a2, b2, a3, b3);
                        at = ref Unsafe.Add(ref at, (nint)group * from);
                        into = ref Unsafe.Add(ref into, (nint)group * to);
                    } while (--n != 0);
                }

                if (i < last + group && last >= start)
                {
                    Lanes16<T, TFormatter, TDirection>(ref Unsafe.Add(ref source, (nint)last * from),
                        ref Unsafe.Add(ref destination, (nint)last * to), a0, b0, a1, b1, a2, b2, a3, b3);
                    i = last + group;
                }
            }

            return i;
        }

        /// <summary>
        /// <see cref="Lanes{T, TFormatter, TDirection}"/> of reads that check their bools
        /// (<see cref="NinoBlockPlan{T, TFormatter}.LanePlan{TDirection}.Checks"/>, 32-byte groups): each group's
        /// vectors, read back once stored and ORed into one accumulator per vector, have their bools tested once after
        /// the last group, in place of the strided pass before the groups (<see cref="CheckBools{T, TFormatter}"/>,
        /// which then names the bad one). A method of its own, not a branch of Lanes: that code in Lanes, even where it
        /// is dead, put Lanes16 past the JIT's inlining budget on Arm64 (a call per group, every lane path 1.4 to 5
        /// times as slow on Neoverse-N2, .NET A/B run 41035440735).
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static unsafe int LanesChecked<T, TFormatter>(ref byte source, ref byte destination, int count)
            where TFormatter : struct, INinoFormatter<T>
        {
            int size = NinoBlockPlan<T, TFormatter>.Size, to = Unsafe.SizeOf<T>();
            int group = NinoBlockPlan<T, TFormatter>.LanePlan<NinoReads>.Group;
            int last = NinoBlockPlan<T, TFormatter>.LanePlan<NinoReads>.Last(count, size, to);
            // As in Lanes.
            int start = NinoBlockPlan<T, TFormatter>.LanePlan<NinoReads>.Start;
            int skip = FirstAligned((nuint)Unsafe.AsPointer(ref destination), to, start, group, 31);
            int i = 0;
            for (; i < start; i++)
                Move<T, TFormatter, NinoReads>(ref source, ref destination, i, size);

            var masks = NinoBlockPlan<T, TFormatter>.LanePlan<NinoReads>.Masks32;
            Vector256<byte> a0 = masks[0], b0 = masks[1], a1 = masks[2], b1 = masks[3], a2 = masks[4], b2 = masks[5],
                a3 = masks[6], b3 = masks[7];
            Vector256<byte> c0 = default, c1 = default, c2 = default, c3 = default;
            if (skip > start && start <= last)
            {
                ref byte into = ref Unsafe.Add(ref destination, (nint)start * to);
                Lanes32<T, TFormatter, NinoReads>(ref Unsafe.Add(ref source, (nint)start * size), ref into, a0, b0,
                    a1, b1, a2, b2, a3, b3);
                Stored<T, TFormatter>(ref into, ref c0, ref c1, ref c2, ref c3);
                i = skip;
            }

            if (i <= last)
            {
                int n = (last - i) / group + 1;
                ref byte at = ref Unsafe.Add(ref source, (nint)i * size);
                ref byte into = ref Unsafe.Add(ref destination, (nint)i * to);
                i += n * group;
                do
                {
                    Lanes32<T, TFormatter, NinoReads>(ref at, ref into, a0, b0, a1, b1, a2, b2, a3, b3);
                    Stored<T, TFormatter>(ref into, ref c0, ref c1, ref c2, ref c3);
                    at = ref Unsafe.Add(ref at, (nint)group * size);
                    into = ref Unsafe.Add(ref into, (nint)group * to);
                } while (--n != 0);
            }

            if (i < last + group && last >= start)
            {
                ref byte into = ref Unsafe.Add(ref destination, (nint)last * to);
                Lanes32<T, TFormatter, NinoReads>(ref Unsafe.Add(ref source, (nint)last * size), ref into, a0, b0, a1,
                    b1, a2, b2, a3, b3);
                Stored<T, TFormatter>(ref into, ref c0, ref c1, ref c2, ref c3);
                i = last + group;
            }

            if (((c0 & NinoBlockPlan<T, TFormatter>.LanePlan<NinoReads>.BoolsA) |
                 (c1 & NinoBlockPlan<T, TFormatter>.LanePlan<NinoReads>.BoolsB) |
                 (c2 & NinoBlockPlan<T, TFormatter>.LanePlan<NinoReads>.BoolsC) |
                 (c3 & NinoBlockPlan<T, TFormatter>.LanePlan<NinoReads>.BoolsD)) != Vector256<byte>.Zero)
                CheckBools<T, TFormatter>(ref source, count);
            return i;
        }

        /// <summary>
        /// The vectors a group of <see cref="LanesChecked{T, TFormatter}"/> just stored at <paramref name="into"/>,
        /// read back (from the store buffer) and ORed into one accumulator each: one load and one OR a vector.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void Stored<T, TFormatter>(ref byte into, ref Vector256<byte> c0, ref Vector256<byte> c1,
            ref Vector256<byte> c2, ref Vector256<byte> c3)
            where TFormatter : struct, INinoFormatter<T>
        {
            int vectors = NinoBlockPlan<T, TFormatter>.LanePlan<NinoReads>.Vectors;
            c0 |= Vector256.LoadUnsafe(ref into);
            if (vectors > 1)
                c1 |= Vector256.LoadUnsafe(ref into, 32);
            if (vectors > 2)
                c2 |= Vector256.LoadUnsafe(ref into, 64);
            if (vectors > 3)
                c3 |= Vector256.LoadUnsafe(ref into, 96);
        }

        /// <summary>Value <paramref name="i"/> moved alone, in the direction of
        /// <typeparamref name="TDirection"/>.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void Move<T, TFormatter, TDirection>(ref byte source, ref byte destination, int i, int size)
            where TFormatter : struct, INinoFormatter<T>
            where TDirection : struct, INinoDirection
        {
            if (typeof(TDirection) == typeof(NinoWrites))
                WriteValue<T, TFormatter>(ref Unsafe.Add(ref destination, (nint)i * size),
                    in Unsafe.Add(ref Unsafe.As<byte, T>(ref source), i));
            else
                ReadOne<T, TFormatter>(ref Unsafe.Add(ref source, (nint)i * size),
                    ref Unsafe.Add(ref Unsafe.As<byte, T>(ref destination), i));
        }

        /// <summary>
        /// One group of 32-byte vectors (AVX2): each the bytes of two loads shuffled within their 16-byte lanes
        /// (vpshufb, which zeroes where an index has its high bit set) and ORed, or of one load alone. All are worked
        /// out before any is stored, so that a load repeated across them is one (no store in between).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void Lanes32<T, TFormatter, TDirection>(ref byte source, ref byte destination,
            Vector256<byte> a0, Vector256<byte> b0, Vector256<byte> a1, Vector256<byte> b1, Vector256<byte> a2,
            Vector256<byte> b2, Vector256<byte> a3, Vector256<byte> b3)
            where TFormatter : struct, INinoFormatter<T>
            where TDirection : struct, INinoDirection
        {
            int vectors = NinoBlockPlan<T, TFormatter>.LanePlan<TDirection>.Vectors;
            var v0 = Lane32<T, TFormatter, TDirection>(ref source, 0, a0, b0);
            var v1 = vectors > 1 ? Lane32<T, TFormatter, TDirection>(ref source, 1, a1, b1) : default;
            var v2 = vectors > 2 ? Lane32<T, TFormatter, TDirection>(ref source, 2, a2, b2) : default;
            var v3 = vectors > 3 ? Lane32<T, TFormatter, TDirection>(ref source, 3, a3, b3) : default;
            v0.StoreUnsafe(ref destination);
            if (vectors > 1)
                v1.StoreUnsafe(ref destination, 32);
            if (vectors > 2)
                v2.StoreUnsafe(ref destination, 64);
            if (vectors > 3)
                v3.StoreUnsafe(ref destination, 96);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector256<byte> Lane32<T, TFormatter, TDirection>(ref byte source, int vector, Vector256<byte> a,
            Vector256<byte> b)
            where TFormatter : struct, INinoFormatter<T>
            where TDirection : struct, INinoDirection
        {
            var result = Avx2.Shuffle(Load32(ref source, NinoBlockPlan<T, TFormatter>.LanePlan<TDirection>.Window(vector, 0, 0),
                NinoBlockPlan<T, TFormatter>.LanePlan<TDirection>.Window(vector, 0, 1)), a);
            if (NinoBlockPlan<T, TFormatter>.LanePlan<TDirection>.Window(vector, 1, 0) == NinoLanes.None)
                return result;
            return result | Avx2.Shuffle(Load32(ref source, NinoBlockPlan<T, TFormatter>.LanePlan<TDirection>.Window(vector, 1, 0),
                NinoBlockPlan<T, TFormatter>.LanePlan<TDirection>.Window(vector, 1, 1)), b);
        }

        /// <summary>
        /// The 16 bytes at <paramref name="low"/> and the 16 at <paramref name="high"/>: one load when they follow;
        /// on the 16-byte grid, a lane of each of two loads on the 32-byte grid (vperm2i128), loads the group's other
        /// vectors share; else the low lane of one load and the high lane of another, blended (vpblendd issues three
        /// or four a cycle, where vinserti128 issues once a cycle on Intel and Zen 3).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector256<byte> Load32(ref byte source, int low, int high)
        {
            if (high == low + 16)
                return Vector256.LoadUnsafe(ref Unsafe.Add(ref source, low));
            if (NinoLanes.Paired(low, high))
            {
                // The lane of each that holds the window: its low (0) or high (1) half.
                var x = Vector256.LoadUnsafe(ref Unsafe.Add(ref source, low & ~31));
                var y = Vector256.LoadUnsafe(ref Unsafe.Add(ref source, high & ~31));
                return ((low >> 4) & 1, (high >> 4) & 1) switch
                {
                    (0, 0) => Avx2.Permute2x128(x, y, 0x20),
                    (0, _) => Avx2.Permute2x128(x, y, 0x30),
                    (_, 0) => Avx2.Permute2x128(x, y, 0x21),
                    _ => Avx2.Permute2x128(x, y, 0x31)
                };
            }

            return Avx2.Blend(Vector256.LoadUnsafe(ref Unsafe.Add(ref source, low)).AsInt32(),
                Vector256.LoadUnsafe(ref Unsafe.Add(ref source, high - 16)).AsInt32(), 0xF0).AsByte();
        }

        /// <summary>
        /// One group of 16-byte vectors: each one or two 16-byte loads, as one table lookup of one or two registers on
        /// Arm64 (the second's indices from 16), or each load shuffled (an index from 0x80 giving a zero) and ORed. As
        /// <see cref="Lanes32{T, TFormatter, TDirection}"/>, all before any is stored.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void Lanes16<T, TFormatter, TDirection>(ref byte source, ref byte destination,
            Vector128<byte> a0, Vector128<byte> b0, Vector128<byte> a1, Vector128<byte> b1, Vector128<byte> a2,
            Vector128<byte> b2, Vector128<byte> a3, Vector128<byte> b3)
            where TFormatter : struct, INinoFormatter<T>
            where TDirection : struct, INinoDirection
        {
            int vectors = NinoBlockPlan<T, TFormatter>.LanePlan<TDirection>.Vectors;
            Vector128<byte> v0, v1, v2, v3;
            if (AdvSimd.Arm64.IsSupported && NinoBlockPlan<T, TFormatter>.LanePlan<TDirection>.Gridded)
            {
                // The group's windows once each, before any lookup: the JIT then gives them consecutive registers,
                // and each lookup takes two of them as they are (loaded for each lookup, the loads it repeats take
                // register copies).
                var w0 = NinoBlockPlan<T, TFormatter>.LanePlan<TDirection>.Loads(0) ? Vector128.LoadUnsafe(ref source) : default;
                var w1 = NinoBlockPlan<T, TFormatter>.LanePlan<TDirection>.Loads(1)
                    ? Vector128.LoadUnsafe(ref source, 16)
                    : default;
                var w2 = NinoBlockPlan<T, TFormatter>.LanePlan<TDirection>.Loads(2)
                    ? Vector128.LoadUnsafe(ref source, 32)
                    : default;
                var w3 = NinoBlockPlan<T, TFormatter>.LanePlan<TDirection>.Loads(3)
                    ? Vector128.LoadUnsafe(ref source, 48)
                    : default;
                v0 = Table16<T, TFormatter, TDirection>(0, w0, w1, w2, w3, a0);
                v1 = vectors > 1 ? Table16<T, TFormatter, TDirection>(1, w0, w1, w2, w3, a1) : default;
                v2 = vectors > 2 ? Table16<T, TFormatter, TDirection>(2, w0, w1, w2, w3, a2) : default;
                v3 = vectors > 3 ? Table16<T, TFormatter, TDirection>(3, w0, w1, w2, w3, a3) : default;
            }
            else
            {
                v0 = Lane16<T, TFormatter, TDirection>(ref source, 0, a0, b0);
                v1 = vectors > 1 ? Lane16<T, TFormatter, TDirection>(ref source, 1, a1, b1) : default;
                v2 = vectors > 2 ? Lane16<T, TFormatter, TDirection>(ref source, 2, a2, b2) : default;
                v3 = vectors > 3 ? Lane16<T, TFormatter, TDirection>(ref source, 3, a3, b3) : default;
            }

            v0.StoreUnsafe(ref destination);
            if (vectors > 1)
                v1.StoreUnsafe(ref destination, 16);
            if (vectors > 2)
                v2.StoreUnsafe(ref destination, 32);
            if (vectors > 3)
                v3.StoreUnsafe(ref destination, 48);
        }

        /// <summary>Vector <paramref name="vector"/> of a <see cref="NinoLanes.Gridded"/> group: a lookup in its
        /// window, or its two consecutive ones (Arm64).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector128<byte> Table16<T, TFormatter, TDirection>(int vector, Vector128<byte> w0,
            Vector128<byte> w1, Vector128<byte> w2, Vector128<byte> w3, Vector128<byte> a)
            where TFormatter : struct, INinoFormatter<T>
            where TDirection : struct, INinoDirection
        {
            int k = NinoBlockPlan<T, TFormatter>.LanePlan<TDirection>.Window(vector, 0, 0) / 16;
            if (NinoBlockPlan<T, TFormatter>.LanePlan<TDirection>.Window(vector, 1, 0) == NinoLanes.None)
                return AdvSimd.Arm64.VectorTableLookup(k == 0 ? w0 : k == 1 ? w1 : k == 2 ? w2 : w3, a);
            return k == 0 ? AdvSimd.Arm64.VectorTableLookup((w0, w1), a)
                : k == 1 ? AdvSimd.Arm64.VectorTableLookup((w1, w2), a)
                : AdvSimd.Arm64.VectorTableLookup((w2, w3), a);
        }

        /// <summary>
        /// For Arm64, <paramref name="a"/> holds the indices into both loads (a two-register lookup); elsewhere
        /// <paramref name="a"/> and <paramref name="b"/> those into each.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector128<byte> Lane16<T, TFormatter, TDirection>(ref byte source, int vector, Vector128<byte> a,
            Vector128<byte> b)
            where TFormatter : struct, INinoFormatter<T>
            where TDirection : struct, INinoDirection
        {
            var low = Vector128.LoadUnsafe(
                ref Unsafe.Add(ref source, NinoBlockPlan<T, TFormatter>.LanePlan<TDirection>.Window(vector, 0, 0)));
            int second = NinoBlockPlan<T, TFormatter>.LanePlan<TDirection>.Window(vector, 1, 0);
            if (second == NinoLanes.None)
                return NinoBlockPlan<T, TFormatter>.Shuffle(low, a);
            var high = Vector128.LoadUnsafe(ref Unsafe.Add(ref source, second));
            if (AdvSimd.Arm64.IsSupported)
                return AdvSimd.Arm64.VectorTableLookup((low, high), a);
            return NinoBlockPlan<T, TFormatter>.Shuffle(low, a) | NinoBlockPlan<T, TFormatter>.Shuffle(high, b);
        }
    }
}
#endif
