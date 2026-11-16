// NinoKernels.Permute: blocks of raw values moved between their memory layout and the wire layout by a byte table,
// with a 32-byte AVX2 path, a 16-byte SSSE3 / AArch64 Neon path and a scalar tail. Registration is in NinoBurst.cs.
#if NINO_BURST
using System;
using System.Runtime.InteropServices;
using AOT;
using Nino.Core;
using Unity.Burst;
using Unity.Burst.Intrinsics;

namespace Nino.Burst
{
    public static unsafe partial class NinoBurst
    {
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void PermuteFunction(byte* source, int sourceStride, byte* destination, int destinationStride,
            byte* table, int count);

        /// <summary>
        /// <see cref="NinoKernels.Permute"/>: each value as up to four 16-byte loads, each 16 bytes written ORed from
        /// one byte shuffle (pshufb, or tbl on AArch64) per load it takes bytes from. The last values, whose loads or
        /// stores would reach past either range, one byte at a time.
        /// </summary>
        [BurstCompile(CompileSynchronously = true)]
        [MonoPInvokeCallback(typeof(PermuteFunction))]
        public static void Permute(byte* source, int sourceStride, byte* destination, int destinationStride,
            byte* table, int count) =>
            PermuteAll(source, sourceStride, destination, destinationStride, table, count, true);

        /// <summary><see cref="Permute"/> without the AVX2 paths (registered by <c>Register(Ssse3)</c>).</summary>
        [BurstCompile(CompileSynchronously = true)]
        [MonoPInvokeCallback(typeof(PermuteFunction))]
        public static void Permute16(byte* source, int sourceStride, byte* destination, int destinationStride,
            byte* table, int count) =>
            PermuteAll(source, sourceStride, destination, destinationStride, table, count, false);

        // The fewest values the 32-byte path takes: below it the 16-byte path was as fast or faster on the CI players'
        // CPUs (32 values: about 5% slower with AVX2; 1000 and more: up to 3 times faster to read, 1.2 times to write).
        private const int Avx2MinValues = 256;

        // One instruction set per test, its block a call and a return (NinoBurst.Vectors.cs, the header). The AVX2
        // path runs inside the SSSE3 test: an AVX2 CPU has SSSE3, and PermuteAvx2 tests AVX2 itself.
        private static void PermuteAll(byte* source, int sourceStride, byte* destination, int destinationStride,
            byte* table, int count, bool wide)
        {
            if (Arm.Neon.IsNeonSupported)
            {
                PermuteRest(source, sourceStride, destination, destinationStride, table, count, 0, true);
                return;
            }

            if (X86.Ssse3.IsSsse3Supported)
            {
                int done = wide && count >= Avx2MinValues && sourceStride <= 16 && destinationStride <= 16
                    ? PermuteAvx2(source, sourceStride, destination, destinationStride, table, count)
                    : 0;
                PermuteRest(source, sourceStride, destination, destinationStride, table, count, done, true);
                return;
            }

            PermuteRest(source, sourceStride, destination, destinationStride, table, count, 0, false);
        }

        // The values from i: 16 bytes at a time where vectors are on and the strides allow, then one byte at a time.
        private static void PermuteRest(byte* source, int sourceStride, byte* destination, int destinationStride,
            byte* table, int count, int i, bool vectors)
        {
            if (vectors && sourceStride <= 64 && destinationStride <= 64 && i < count)
                i += PermuteVectors(source + (long)i * sourceStride, sourceStride,
                    destination + (long)i * destinationStride, destinationStride, table, count - i);
            for (; i < count; i++)
            {
                byte* from = source + (long)i * sourceStride, to = destination + (long)i * destinationStride;
                for (int j = 0; j < destinationStride; j++)
                    to[j] = table[j] == 0xFF ? (byte)0 : from[table[j]];
            }
        }

        private static int PermuteVectors(byte* source, int sourceStride, byte* destination, int destinationStride,
            byte* table, int count)
        {
            int loads = (sourceStride + 15) >> 4, stores = (destinationStride + 15) >> 4;
            // The last value whose loads stay inside the source and whose stores stay inside the destination. A store
            // past a value's own bytes writes over the next value's, which is written after it.
            int last = Math.Min(count - (loads * 16 + sourceStride - 1) / sourceStride,
                count - (stores * 16 + destinationStride - 1) / destinationStride);
            if (last < 0)
                return 0;

            byte* at0 = source, to0 = destination;
            int n = last + 1;
            if (loads == 1 && stores == 1)
            {
                // Values and blocks of up to 16 bytes (most tuples and small structs): one shuffle each, by the table
                // itself (an index of 0xFF, like any with the top bit set, gives 0 for both pshufb and tbl). Building
                // only this mask moved the padded copy's break-even from about 24 values to 12.
                v128 m = default;
                byte* mask = (byte*)&m;
                for (int j = 0; j < 16; j++)
                    mask[j] = j < destinationStride ? table[j] : (byte)0x80;
                for (int k = 0; k < n; k++)
                {
                    Store(to0, Shuffle(Load(at0), m));
                    at0 += sourceStride;
                    to0 += destinationStride;
                }

                return n;
            }

            // The shuffle of load s for store o (masks[o * 4 + s]): its bytes from that load at their index, every other
            // byte 0x80 (zero for both pshufb and tbl). Bit o * 4 + s of used: the store takes bytes from the load.
            v128* masks = stackalloc v128[16];
            int used = 0;
            for (int o = 0; o < stores; o++)
            for (int s = 0; s < loads; s++)
            {
                byte* mask = (byte*)(masks + o * 4 + s);
                for (int j = 0; j < 16; j++)
                {
                    int at = o * 16 + j;
                    int from = at < destinationStride ? table[at] : 0xFF;
                    bool here = from != 0xFF && from >> 4 == s;
                    mask[j] = here ? (byte)(from & 15) : (byte)0x80;
                    if (here)
                        used |= 1 << (o * 4 + s);
                }
            }

            for (int k = 0; k < n; k++)
            {
                v128 x0 = Load(at0);
                v128 x1 = loads > 1 ? Load(at0 + 16) : default;
                v128 x2 = loads > 2 ? Load(at0 + 32) : default;
                v128 x3 = loads > 3 ? Load(at0 + 48) : default;
                for (int o = 0; o < stores; o++)
                {
                    v128 r = default;
                    if ((used & 1 << (o * 4)) != 0)
                        r = Shuffle(x0, masks[o * 4]);
                    if ((used & 1 << (o * 4 + 1)) != 0)
                        r = Or(r, Shuffle(x1, masks[o * 4 + 1]));
                    if ((used & 1 << (o * 4 + 2)) != 0)
                        r = Or(r, Shuffle(x2, masks[o * 4 + 2]));
                    if ((used & 1 << (o * 4 + 3)) != 0)
                        r = Or(r, Shuffle(x3, masks[o * 4 + 3]));
                    Store(to0 + o * 16, r);
                }

                at0 += sourceStride;
                to0 += destinationStride;
            }

            return n;
        }

        /// <summary>
        /// Values of up to 16 bytes (both strides), two or more a 32-byte vector: each 16-byte lane holds
        /// v = min(16 / sourceStride, 16 / destinationStride) values, shuffled (vpshufb) by one mask for both lanes; the
        /// lanes' bytes are stored as one 32-byte store when they fill it, joined by a dword permute (vpermd) when they
        /// are whole dwords, else as two 16-byte stores. Returns the values done (from the first), leaving those whose
        /// loads or stores would reach past either range to the 16-byte path. AVX2 only (0 elsewhere).
        /// </summary>
        private static int PermuteAvx2(byte* source, int sourceStride, byte* destination, int destinationStride,
            byte* table, int count)
        {
            if (X86.Avx2.IsAvx2Supported)
            {
                int v = Math.Min(16 / sourceStride, 16 / destinationStride);
                int laneIn = v * sourceStride, laneOut = v * destinationStride, step = 2 * v;
                int kind = laneOut == 16 ? 0 : (laneOut & 3) == 0 ? 1 : 2;
                int readExtent = laneIn + 16, writeExtent = kind == 2 ? laneOut + 16 : 32;
                int n = count / step;
                long inTotal = (long)count * sourceStride, outTotal = (long)count * destinationStride;
                while (n > 0 && ((long)(n - 1) * step * sourceStride + readExtent > inTotal ||
                                 (long)(n - 1) * step * destinationStride + writeExtent > outTotal))
                    n--;
                if (n == 0)
                    return 0;

                // The shuffle of a lane (the same in both): byte j of the lane's output is byte b of value q.
                byte* m = stackalloc byte[32];
                for (int j = 0; j < 16; j++)
                {
                    int from = 0xFF;
                    if (j < laneOut)
                    {
                        int q = j / destinationStride, b = j - q * destinationStride;
                        from = table[b] == 0xFF ? 0xFF : q * sourceStride + table[b];
                    }

                    m[j] = m[16 + j] = from == 0xFF ? (byte)0x80 : (byte)from;
                }

                // The dwords of both lanes' output, in order (kind 1).
                int* d = stackalloc int[8];
                for (int j = 0; j < 8; j++)
                    d[j] = j < laneOut / 4 ? j : j < 2 * (laneOut / 4) ? j - laneOut / 4 + 4 : 0;
                v256 mask = X86.Avx.mm256_loadu_si256(m), dwords = X86.Avx.mm256_loadu_si256(d);
                bool contiguous = laneIn == 16;
                int inStep = step * sourceStride, outStep = step * destinationStride;
                byte* at = source, to = destination;
                for (int k = 0; k < n; k++)
                {
                    v256 x = contiguous
                        ? X86.Avx.mm256_loadu_si256(at)
                        : X86.Avx.mm256_loadu2_m128i(at + laneIn, at);
                    v256 r = X86.Avx2.mm256_shuffle_epi8(x, mask);
                    if (kind == 0)
                        X86.Avx.mm256_storeu_si256(to, r);
                    else if (kind == 1)
                        X86.Avx.mm256_storeu_si256(to, X86.Avx2.mm256_permutevar8x32_epi32(r, dwords));
                    else
                    {
                        X86.Sse2.storeu_si128(to, X86.Avx.mm256_castsi256_si128(r));
                        X86.Sse2.storeu_si128(to + laneOut, X86.Avx2.mm256_extracti128_si256(r, 1));
                    }

                    at += inStep;
                    to += outStep;
                }

                return n * step;
            }

            return 0;
        }
    }
}
#endif
