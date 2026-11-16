// The 16-byte vector operations the kernels in NinoBurst.Text.cs and NinoBurst.Permute.cs share.
#if NINO_BURST
using Unity.Burst.Intrinsics;

namespace Nino.Burst
{
    public static unsafe partial class NinoBurst
    {
        // The vector operations, one instruction each on both instruction sets. Only called where one of them is
        // supported. Burst requires each instruction to sit under a test of its own IsXxxSupported in the same
        // method: a player is compiled ahead of time for a baseline (SSE2 on x64, none on Armv7 or WebGL) and
        // rejects an SSSE3 or even SSE2 instruction outside such a test, although the editor, compiling for the
        // machine it runs on, accepts it. The final `default` is never reached.
        //
        // Every kernel tests one instruction set per `if` (never `Arm.Neon.IsNeonSupported || X86.Sse2...`), and the
        // block under each test only calls and returns: no code after it is reached from another test's block. Burst's
        // check of intrinsics (CheckIntrinsicUsageTransform, the same in Burst 1.8 and 2.0) gives each block the
        // instruction set of the first test whose edge reaches it and ignores the others (two known sets compare
        // equal in its visited set), then replaces each block marked with a set the target lacks by a throw. A block
        // reached by both edges of `Neon || Sse2` kept Neon on x64 under Burst 2.0 (Unity 6000.6): every narrow from
        // 16 chars threw ("burst_abort" in Narrow), while Burst 1.8.29 passed the same code: which edge comes first
        // depends on the IL graph each Burst builds. The kernels' vector loops (NarrowVectors, PermuteVectors, ...)
        // test nothing themselves.

        private static v128 Load(byte* at)
        {
            if (Arm.Neon.IsNeonSupported)
                return Arm.Neon.vld1q_u8(at);
            if (X86.Sse2.IsSse2Supported)
                return X86.Sse2.loadu_si128(at);
            return default;
        }

        private static void Store(byte* at, v128 value)
        {
            if (Arm.Neon.IsNeonSupported)
                Arm.Neon.vst1q_u8(at, value);
            else if (X86.Sse2.IsSse2Supported)
                X86.Sse2.storeu_si128(at, value);
        }

        // A byte shuffle: each index's byte of value, zero for an index from 0x80 (pshufb) or from 16 (tbl).
        private static v128 Shuffle(v128 value, v128 indices)
        {
            if (Arm.Neon.IsNeonSupported)
                return Arm.Neon.vqtbl1q_u8(value, indices);
            if (X86.Ssse3.IsSsse3Supported)
                return X86.Ssse3.shuffle_epi8(value, indices);
            return default;
        }

        private static v128 Or(v128 a, v128 b)
        {
            if (Arm.Neon.IsNeonSupported)
                return Arm.Neon.vorrq_u8(a, b);
            if (X86.Sse2.IsSse2Supported)
                return X86.Sse2.or_si128(a, b);
            return default;
        }

        // Whether a char of a or b (eight UTF-16 chars each) is above U+00FF.
        private static bool AboveLatin1(v128 a, v128 b)
        {
            if (Arm.Neon.IsNeonSupported)
                return Arm.Neon.vmaxvq_u16(Arm.Neon.vorrq_u8(a, b)) > 0xFF;
            if (X86.Sse2.IsSse2Supported)
            {
                v128 high = X86.Sse2.and_si128(X86.Sse2.or_si128(a, b), X86.Sse2.set1_epi16(unchecked((short)0xFF00)));
                return X86.Sse2.movemask_epi8(X86.Sse2.cmpeq_epi8(high, X86.Sse2.setzero_si128())) != 0xFFFF;
            }

            return true;
        }

        // Whether a char of a or b (eight UTF-16 chars each) is above U+007F.
        private static bool AboveAscii(v128 a, v128 b)
        {
            if (Arm.Neon.IsNeonSupported)
                return Arm.Neon.vmaxvq_u16(Arm.Neon.vorrq_u8(a, b)) > 0x7F;
            if (X86.Sse2.IsSse2Supported)
            {
                v128 high = X86.Sse2.and_si128(X86.Sse2.or_si128(a, b), X86.Sse2.set1_epi16(unchecked((short)0xFF80)));
                return X86.Sse2.movemask_epi8(X86.Sse2.cmpeq_epi8(high, X86.Sse2.setzero_si128())) != 0xFFFF;
            }

            return true;
        }

        // Whether a byte of value is above 0x7F.
        private static bool AnyAboveAscii(v128 value)
        {
            if (Arm.Neon.IsNeonSupported)
                return Arm.Neon.vmaxvq_u8(value) > 0x7F;
            if (X86.Sse2.IsSse2Supported)
                return X86.Sse2.movemask_epi8(value) != 0;
            return true;
        }

        // The low bytes of the sixteen chars of a and b, in order (all at most U+00FF).
        private static v128 Pack(v128 a, v128 b)
        {
            if (Arm.Neon.IsNeonSupported)
                return Arm.Neon.vuzp1q_u8(a, b);
            if (X86.Sse2.IsSse2Supported)
                return X86.Sse2.packus_epi16(a, b);
            return default;
        }

        // The first and last eight bytes of value as chars.
        private static v128 Low(v128 value)
        {
            if (Arm.Neon.IsNeonSupported)
                return Arm.Neon.vzip1q_u8(value, new v128((byte)0));
            if (X86.Sse2.IsSse2Supported)
                return X86.Sse2.unpacklo_epi8(value, X86.Sse2.setzero_si128());
            return default;
        }

        private static v128 High(v128 value)
        {
            if (Arm.Neon.IsNeonSupported)
                return Arm.Neon.vzip2q_u8(value, new v128((byte)0));
            if (X86.Sse2.IsSse2Supported)
                return X86.Sse2.unpackhi_epi8(value, X86.Sse2.setzero_si128());
            return default;
        }
    }
}
#endif
