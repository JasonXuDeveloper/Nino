#if NET8_0_OR_GREATER
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Nino.Core.Internal
{
    /// <summary>Hardware capabilities and CPU policy shared by managed SIMD kernels.</summary>
    internal static partial class NinoSimd
    {
        internal static bool Has512
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => Vector512.IsHardwareAccelerated;
        }

        internal static bool Has256
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => Vector256.IsHardwareAccelerated;
        }

        internal static bool Has128
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => Vector128.IsHardwareAccelerated;
        }

        /// <summary>
        /// An AMD x64 CPU, whose rep movsb (Memmove's copy of a large block) is slower than vector stores also into a
        /// new array, where Intel's writes whole lines without reading them first. Measured on GitHub's EPYC 7763
        /// (.NET 10), a new uninitialized array filled from offset 4 of a buffer, vector stores against Memmove: 8,000
        /// bytes in 279 to 288 ns instead of 341 to 350, 16,000 in 556 to 567 instead of 658 to 677, 64,000 in 1,989 to
        /// 2,018 instead of 2,680 to 2,750; on an AVX-512 Xeon, Memmove 1.3 to 2 times as fast; on Arm64 (Neoverse
        /// N2) Memmove as fast or faster. One CPUID at startup.
        /// </summary>
        internal static readonly bool Amd = X86Base.IsSupported && IsAmd();

        private static bool IsAmd()
        {
            // "AuthenticAMD", in EBX, EDX and ECX.
            var (_, ebx, ecx, edx) = X86Base.CpuId(0, 0);
            return ebx == 0x68747541 && edx == 0x69746E65 && ecx == 0x444D4163;
        }

        /// <summary>
        /// An Intel CPU of the Sunny Cove generation (<see cref="IsIceLake"/>): on GitHub's Xeon Platinum 8370C a
        /// 64-byte load across two cache lines costs about twice an aligned one, so its block writes load memory
        /// vectors from 64-byte boundaries (<see cref="NinoBlockCollections"/>.WriteAligned).
        /// </summary>
        internal static readonly bool IceLake = X86Base.IsSupported && !Amd && IsIceLake();

        /// <summary>
        /// An x64 CPU whose 64-byte vector work beats 32-byte vectors where the two disagree: AMD's (Zen 4 and later)
        /// and Intel's other than the Sunny Cove generation (<see cref="IsIceLake"/>), where both kernels below lost.
        /// Not by AVX512-FP16 (Sapphire Rapids and later): Azure's Xeon Platinum 8573C (Emerald Rapids) does not report
        /// it, and with 32-byte masks there it wrote 1,000 8-byte structs with a padding byte in 92 ns instead of 60
        /// (.NET A/B run 41020623184). Uses the cached CPU profile.
        /// </summary>
        internal static bool Prefer512
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => Has512 && X86Base.IsSupported && !IceLake;
        }

        /// <summary>
        /// An Intel CPU of the Sunny Cove generation and its client relatives (Ice Lake server and client, Tiger Lake,
        /// Rocket Lake), by family 6 and model: the first cores with AVX-512 VBMI, whose 64-byte permutes and masked
        /// copies ran slower than 32-byte ones on GitHub's Xeon Platinum 8370C.
        /// </summary>
        private static bool IsIceLake()
        {
            int eax = X86Base.CpuId(1, 0).Eax;
            int family = (eax >> 8) & 0xF, model = ((eax >> 4) & 0xF) | ((eax >> 12) & 0xF0);
            return family == 6 && model is 0x6A or 0x6C or 0x7D or 0x7E or 0x9D or 0x8C or 0x8D or 0xA7;
        }

        /// <summary>
        /// An x64 CPU with AVX-512 VBMI whose two-source 64-byte byte permute (vpermt2b) writes values of more than 16
        /// bytes faster than lane groups of 32-byte vectors (<see cref="NinoBlockCollections"/>.WritePairs),
        /// <see cref="Prefer512"/>. Measured on GitHub's runners, 1,000 values of 24 bytes as 15-byte blocks (.NET 10):
        /// Xeon Platinum 8573C 0.65 and EPYC 9V45 0.76 times the time of lane groups, Xeon Platinum 8370C (Ice Lake)
        /// 1.44 times.
        /// </summary>
        internal static readonly bool PairPermutes = Avx512Vbmi.IsSupported && !IceLake;
    }
}
#endif
