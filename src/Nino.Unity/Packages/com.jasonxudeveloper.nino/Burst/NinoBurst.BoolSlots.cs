// Nullable bool slots share one native traversal across AVX2, SSE2, NEON and scalar tails.
// Presence bytes are prechecked by Core. A failed value group stays unwritten for scalar completion.
#if NINO_BURST
using System.Runtime.InteropServices;
using AOT;
using Nino.Core;
using Unity.Burst;
using Unity.Burst.Intrinsics;

namespace Nino.Burst
{
    public static unsafe partial class NinoBurst
    {
        private const ulong InvalidBoolSlotBits = 0xFE00FE00FE00FE00UL;

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate int NormalizeBoolSlotsFunction(byte* source, byte* target, int count);

        [BurstCompile(CompileSynchronously = true)]
        [MonoPInvokeCallback(typeof(NormalizeBoolSlotsFunction))]
        public static int NormalizeBoolSlots(byte* source, byte* target, int count) =>
            NormalizeBoolSlotsAll(source, target, count, true);

        [BurstCompile(CompileSynchronously = true)]
        [MonoPInvokeCallback(typeof(NormalizeBoolSlotsFunction))]
        private static int NormalizeBoolSlots128(byte* source, byte* target, int count) =>
            NormalizeBoolSlotsAll(source, target, count, false);

        // One instruction set per test, its block a call and a return (NinoBurst.Vectors.cs, the header). Each path
        // returns the slots it completed, stopping at a group with an invalid value; the 16-byte paths and the scalar
        // one finish what the wider one left.
        private static int NormalizeBoolSlotsAll(byte* source, byte* target, int count, bool wide)
        {
            if (wide && X86.Avx2.IsAvx2Supported)
                return NormalizeBoolSlotsAvx2(source, target, count);
            if (Arm.Neon.IsNeonSupported)
                return NormalizeBoolSlotsNeon(source, target, count);
            if (X86.Sse2.IsSse2Supported)
                return NormalizeBoolSlotsSse2(source, target, count, 0);
            return NinoKernels.NormalizeBoolSlotsScalar(source, target, count);
        }

        private static int NormalizeBoolSlotsAvx2(byte* source, byte* target, int count)
        {
            if (X86.Avx2.IsAvx2Supported)
            {
                int i = 0;
                for (; i <= count - 16; i += 16)
                {
                    var pairs = X86.Avx.mm256_loadu_si256(source + i * 2);
                    var active = X86.Avx2.mm256_srai_epi16(X86.Avx2.mm256_slli_epi16(pairs, 15), 15);
                    var normalized = X86.Avx2.mm256_and_si256(pairs, active);
                    var invalid = X86.Avx2.mm256_and_si256(normalized,
                        new v256(InvalidBoolSlotBits, InvalidBoolSlotBits, InvalidBoolSlotBits, InvalidBoolSlotBits));
                    if (X86.Avx2.mm256_movemask_epi8(X86.Avx2.mm256_cmpeq_epi16(invalid, default)) != -1)
                        return i;
                    X86.Avx.mm256_storeu_si256(target + i * 2, normalized);
                }

                return NormalizeBoolSlotsSse2(source, target, count, i);
            }

            return 0;
        }

        private static int NormalizeBoolSlotsNeon(byte* source, byte* target, int count)
        {
            if (Arm.Neon.IsNeonSupported)
            {
                int i = 0;
                for (; i <= count - 8; i += 8)
                {
                    var pairs = Arm.Neon.vld1q_u8(source + i * 2);
                    var active = Arm.Neon.vshrq_n_s16(Arm.Neon.vshlq_n_u16(pairs, 15), 15);
                    var normalized = Arm.Neon.vandq_u8(pairs, active);
                    var invalid = Arm.Neon.vandq_u8(normalized, new v128(InvalidBoolSlotBits, InvalidBoolSlotBits));
                    if (Arm.Neon.vmaxvq_u16(invalid) != 0)
                        return i;
                    Arm.Neon.vst1q_u8(target + i * 2, normalized);
                }

                return NormalizeBoolSlotsFrom(source, target, count, i);
            }

            return 0;
        }

        // From slot i.
        private static int NormalizeBoolSlotsSse2(byte* source, byte* target, int count, int i)
        {
            if (X86.Sse2.IsSse2Supported)
            {
                for (; i <= count - 8; i += 8)
                {
                    var pairs = X86.Sse2.loadu_si128(source + i * 2);
                    var active = X86.Sse2.srai_epi16(X86.Sse2.slli_epi16(pairs, 15), 15);
                    var normalized = X86.Sse2.and_si128(pairs, active);
                    var invalid = X86.Sse2.and_si128(normalized, new v128(InvalidBoolSlotBits, InvalidBoolSlotBits));
                    if (X86.Sse2.movemask_epi8(X86.Sse2.cmpeq_epi16(invalid, default)) != 0xFFFF)
                        return i;
                    X86.Sse2.storeu_si128(target + i * 2, normalized);
                }

                return NormalizeBoolSlotsFrom(source, target, count, i);
            }

            return i;
        }

        private static int NormalizeBoolSlotsFrom(byte* source, byte* target, int count, int i) =>
            i + NinoKernels.NormalizeBoolSlotsScalar(source + i * 2, target + i * 2, count - i);
    }
}
#endif
