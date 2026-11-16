// NinoKernels.Interleave32: two int columns written as pairs, with AVX2, AArch64 Neon or SSE2 and a scalar tail.
// Registration is in NinoBurst.cs.
#if NINO_BURST
using System.Runtime.InteropServices;
using AOT;
using Unity.Burst;
using Unity.Burst.Intrinsics;

namespace Nino.Burst
{
    public static unsafe partial class NinoBurst
    {
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void Interleave32Function(int* first, int* second, byte* target, int count);

        [BurstCompile(CompileSynchronously = true)]
        [MonoPInvokeCallback(typeof(Interleave32Function))]
        public static void Interleave32(int* first, int* second, byte* target, int count)
            => Interleave32All(first, second, target, count, true);

        [BurstCompile(CompileSynchronously = true)]
        [MonoPInvokeCallback(typeof(Interleave32Function))]
        private static void Interleave32Narrow(int* first, int* second, byte* target, int count)
            => Interleave32All(first, second, target, count, false);

        // One instruction set per test, its block a call and a return (NinoBurst.Vectors.cs, the header). The AVX2
        // path leaves its last four pairs or fewer to the SSE2 one.
        private static void Interleave32All(int* first, int* second, byte* target, int count, bool wide)
        {
            if (wide && X86.Avx2.IsAvx2Supported)
            {
                Interleave32Sse2(first, second, target, count, Interleave32Avx2(first, second, target, count));
                return;
            }

            if (Arm.Neon.IsNeonSupported)
            {
                Interleave32Neon(first, second, target, count);
                return;
            }

            if (X86.Sse2.IsSse2Supported)
            {
                Interleave32Sse2(first, second, target, count, 0);
                return;
            }

            Interleave32Pairs(first, second, target, count, 0);
        }

        // Eight pairs at a time: the pairs written.
        private static int Interleave32Avx2(int* first, int* second, byte* target, int count)
        {
            if (X86.Avx2.IsAvx2Supported)
            {
                int i = 0;
                for (; i <= count - 8; i += 8, target += 64)
                {
                    var a = X86.Avx.mm256_loadu_si256(first + i);
                    var b = X86.Avx.mm256_loadu_si256(second + i);
                    var low = X86.Avx2.mm256_unpacklo_epi32(a, b);
                    var high = X86.Avx2.mm256_unpackhi_epi32(a, b);
                    // Unpack works within each 128-bit lane. Join the matching lanes in pair order.
                    X86.Avx.mm256_storeu_si256(target, X86.Avx2.mm256_permute2x128_si256(low, high, 0x20));
                    X86.Avx.mm256_storeu_si256(target + 32, X86.Avx2.mm256_permute2x128_si256(low, high, 0x31));
                }

                return i;
            }

            return 0;
        }

        // Four pairs at a time from pair i (count is a multiple of four).
        private static void Interleave32Sse2(int* first, int* second, byte* target, int count, int i)
        {
            if (X86.Sse2.IsSse2Supported)
            {
                for (target += 8L * i; i < count; i += 4, target += 32)
                {
                    var a = X86.Sse2.loadu_si128(first + i);
                    var b = X86.Sse2.loadu_si128(second + i);
                    X86.Sse2.storeu_si128(target, X86.Sse2.unpacklo_epi32(a, b));
                    X86.Sse2.storeu_si128(target + 16, X86.Sse2.unpackhi_epi32(a, b));
                }

                return;
            }

            Interleave32Pairs(first, second, target, count, i);
        }

        private static void Interleave32Neon(int* first, int* second, byte* target, int count)
        {
            if (Arm.Neon.IsNeonSupported)
            {
                for (int i = 0; i < count; i += 4, target += 32)
                {
                    var a = Arm.Neon.vld1q_u8((byte*)(first + i));
                    var b = Arm.Neon.vld1q_u8((byte*)(second + i));
                    Arm.Neon.vst1q_u8(target, Arm.Neon.vzip1q_u32(a, b));
                    Arm.Neon.vst1q_u8(target + 16, Arm.Neon.vzip2q_u32(a, b));
                }

                return;
            }

            Interleave32Pairs(first, second, target, count, 0);
        }

        private static void Interleave32Pairs(int* first, int* second, byte* target, int count, int i)
        {
            for (target += 8L * i; i < count; i++, target += 8)
                for (int b = 0; b < 4; b++)
                {
                    target[b] = (byte)((uint)first[i] >> (b * 8));
                    target[4 + b] = (byte)((uint)second[i] >> (b * 8));
                }
        }
    }
}
#endif
