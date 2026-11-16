#if NET8_0_OR_GREATER
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;

namespace Nino.Core.Internal
{
    // Values moved by one 64-byte permute a step (AVX-512 VBMI, Arm64 table lookups).
    internal static partial class NinoBlockCollections
    {
        /// <summary>
        /// <see cref="Permute64(ref byte, nint, ref byte, nint, Vector512{byte}, int, Vector512{byte}, bool)"/> of the
        /// values from value <paramref name="i"/> to value <paramref name="last"/>, <paramref name="step"/> at a time
        /// (none when <paramref name="i"/> is past <paramref name="last"/>), each <paramref name="from"/> bytes apart
        /// in <paramref name="source"/> and <paramref name="to"/> in <paramref name="destination"/>; returns the value
        /// after them. Cursors bumped a vector at a time and a count down, and on Arm64 the quarters of the indices
        /// taken out once: there a 64-byte vector is a struct of four, and a group at a time it was copied through the
        /// stack and its quarters loaded from it again.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int Permute64(ref byte source, nint from, ref byte destination, nint to, int i, int last,
            int step, Vector512<byte> indices, int tables, Vector512<byte> keep, bool pads)
        {
            if (i > last)
                return i;
            var (q0, q1, q2, q3) = Quarters(indices);
            int n = (last - i) / step + 1;
            ref byte at = ref Unsafe.Add(ref source, i * from);
            ref byte into = ref Unsafe.Add(ref destination, i * to);
            i += n * step;
            do
            {
                Permute64(ref at, ref into, indices, tables, q0, q1, q2, q3, keep, pads);
                at = ref Unsafe.Add(ref at, step * from);
                into = ref Unsafe.Add(ref into, step * to);
            } while (--n != 0);

            return i;
        }

        /// <summary>
        /// One 64-byte byte permute from <paramref name="source"/> + <paramref name="from"/> to
        /// <paramref name="destination"/> + <paramref name="to"/>: each byte written is the source byte its index
        /// names. AVX-512 VBMI's permute, which reads only an index's low six bits (so a padding byte, index 0x80,
        /// takes some byte, cleared by <paramref name="keep"/> where <paramref name="pads"/>), or on Arm64 four
        /// 4-register table lookups, one per 16 bytes written (0 at index 0x80).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void Permute64(ref byte source, nint from, ref byte destination, nint to,
            Vector512<byte> indices, int tables, Vector512<byte> keep, bool pads)
        {
            var (q0, q1, q2, q3) = Quarters(indices);
            Permute64(ref Unsafe.Add(ref source, from), ref Unsafe.Add(ref destination, to), indices, tables, q0, q1,
                q2, q3, keep, pads);
        }

        /// <summary>The 16-byte quarters of <paramref name="indices"/> for the Arm64 table lookups (none with AVX-512
        /// VBMI).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static (Vector128<byte>, Vector128<byte>, Vector128<byte>, Vector128<byte>) Quarters(
            Vector512<byte> indices) =>
            Avx512Vbmi.IsSupported
                ? default
                : (indices.GetLower().GetLower(), indices.GetLower().GetUpper(), indices.GetUpper().GetLower(),
                    indices.GetUpper().GetUpper());

        /// <summary>
        /// One permute of
        /// <see cref="Permute64(ref byte, nint, ref byte, nint, Vector512{byte}, int, Vector512{byte}, bool)"/>; on
        /// Arm64 each 16 bytes written by one lookup in the quarters <paramref name="tables"/> names
        /// (<see cref="NinoBlockPlan{T, TFormatter}.WireTables64"/>), each quarter loaded once.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void Permute64(ref byte source, ref byte destination, Vector512<byte> indices, int tables,
            Vector128<byte> q0, Vector128<byte> q1, Vector128<byte> q2, Vector128<byte> q3, Vector512<byte> keep,
            bool pads)
        {
            if (Avx512Vbmi.IsSupported)
            {
                var vector = Avx512Vbmi.PermuteVar64x8(Vector512.LoadUnsafe(ref source), indices);
                (pads ? vector & keep : vector).StoreUnsafe(ref destination);
                return;
            }

            Vector128<byte> t0 = default, t1 = default, t2 = default, t3 = default;
            if ((tables & 1 << Loaded) != 0)
                t0 = Vector128.LoadUnsafe(ref source);
            if ((tables & 1 << Loaded + 1) != 0)
                t1 = Vector128.LoadUnsafe(ref source, 16);
            if ((tables & 1 << Loaded + 2) != 0)
                t2 = Vector128.LoadUnsafe(ref source, 32);
            if ((tables & 1 << Loaded + 3) != 0)
                t3 = Vector128.LoadUnsafe(ref source, 48);
            // All lookups before the stores, so the JIT pairs them (stp).
            var r0 = Table64(t0, t1, t2, t3, tables, q0);
            var r1 = Table64(t0, t1, t2, t3, tables >> TableBits, q1);
            var r2 = Table64(t0, t1, t2, t3, tables >> 2 * TableBits, q2);
            var r3 = Table64(t0, t1, t2, t3, tables >> 3 * TableBits, q3);
            if ((tables & 1 << Written) != 0)
                r0.StoreUnsafe(ref destination);
            if ((tables & 1 << Written + 1) != 0)
                r1.StoreUnsafe(ref destination, 16);
            if ((tables & 1 << Written + 2) != 0)
                r2.StoreUnsafe(ref destination, 32);
            if ((tables & 1 << Written + 3) != 0)
                r3.StoreUnsafe(ref destination, 48);
        }

        /// <summary>
        /// A lookup of <paramref name="index"/> in the consecutive quarters <paramref name="table"/> names (its low two
        /// bits the first, the next two their count less one), whose indices count from the first.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector128<byte> Table64(Vector128<byte> t0, Vector128<byte> t1, Vector128<byte> t2,
            Vector128<byte> t3, int table, Vector128<byte> index)
        {
            int first = table & 3;
            switch (table >> 2 & 3)
            {
                case 0:
                    return AdvSimd.Arm64.VectorTableLookup(first == 0 ? t0 : first == 1 ? t1 : first == 2 ? t2 : t3,
                        index);
                case 1:
                    return first == 0 ? AdvSimd.Arm64.VectorTableLookup((t0, t1), index)
                        : first == 1 ? AdvSimd.Arm64.VectorTableLookup((t1, t2), index)
                        : AdvSimd.Arm64.VectorTableLookup((t2, t3), index);
                case 2:
                    return first == 0 ? AdvSimd.Arm64.VectorTableLookup((t0, t1, t2), index)
                        : AdvSimd.Arm64.VectorTableLookup((t1, t2, t3), index);
                default:
                    return AdvSimd.Arm64.VectorTableLookup((t0, t1, t2, t3), index);
            }
        }
    }
}
#endif
