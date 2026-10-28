// Burst-compiled kernels for Nino.Core (NinoKernels). This assembly compiles only when com.unity.burst is installed
// (NINO_BURST, set by the asmdef's versionDefines); without it Nino keeps its scalar paths.
//
// Unity loads the netstandard2.1 build of Nino.Core, which has no hardware intrinsics: on Mono, IL2CPP and HybridCLR it
// moves blocks of tuples and structs a field at a time and narrows or widens Latin-1 text a word at a time. The kernels
// here do that work with AVX2 or SSSE3 (x86 and x64) or AArch64 Neon, called through Burst function pointers.
//
// x64 players: Burst AOT compiles each function pointer once per CPU target of the Burst AOT Settings (Burst 1.8.29's
// default for x64 desktop players: SSE2 and AVX2; SSE4 can be added) and picks the variant the CPU runs at load time.
// The AVX2 variant takes the 32-byte permute below for blocks of 256 values or more, an SSE4 variant the 16-byte (SSSE3)
// paths; the SSE2 variant has no byte shuffle, so on a CPU without AVX2 and without an SSE4 target the kernels are not
// registered.
//
// Platforms (each kernel checks its instruction set at Burst compile time, IsXxxSupported, and has a scalar branch):
// - Editor (Mono) and Mono players: Burst JIT in the editor (compiled synchronously), Burst AOT in players.
// - IL2CPP players (Windows, macOS, Linux, Android arm64, iOS): Burst AOT-compiles the function pointers into the
//   player's Burst library; [MonoPInvokeCallback] keeps the managed fallback callable.
// - HybridCLR: hot-update code calls the AOT Nino.Core, which calls these kernels; nothing here is interpreted.
// - Armv7 (32-bit Android) and x86 targets without SSSE3: Burst's Neon intrinsics are AArch64 only, so the kernels
//   would take their scalar branch; Register sees that (Isa returns 0) and leaves Nino's own scalar paths in place.
// - WebGL and any player where Burst does not run (BurstCompiler.IsEnabled false): nothing is registered.
#if NINO_BURST
using System;
using System.Runtime.InteropServices;
using AOT;
using Nino.Core;
using Unity.Burst;
using Unity.Burst.Intrinsics;
using UnityEngine;

namespace Nino.Burst
{
    /// <summary>
    /// Registers Burst-compiled <see cref="NinoKernels"/> with Nino.Core at startup (and in the editor after each domain
    /// reload). <see cref="Register"/> and <see cref="Unregister"/> switch them by hand.
    /// </summary>
    [BurstCompile]
    public static unsafe class NinoBurst
    {
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void PermuteFunction(byte* source, int sourceStride, byte* destination, int destinationStride,
            byte* table, int count);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate int NarrowFunction(ushort* source, byte* destination, int length);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void WidenFunction(byte* source, ushort* destination, int length);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate int IsaFunction();

        /// <summary>Values of <see cref="Isa"/>: the kernels' 16-byte x86 paths (SSSE3), AArch64 Neon, the 32-byte x86
        /// paths (AVX2, with the 16-byte ones for what they do not cover).</summary>
        public const int Ssse3 = 1, Neon = 2, Avx2 = 3;

        /// <summary>
        /// The instruction set of the registered kernels: <see cref="Ssse3"/>, <see cref="Neon"/>, <see cref="Avx2"/>, 0
        /// none (the scalar branches, or the managed code when Burst did not compile them). Set by
        /// <see cref="Register()"/>.
        /// </summary>
        public static int Isa { get; private set; }

        // The best instruction set Burst compiled the kernels for on this CPU.
        private static int _best;

        /// <summary>Whether Nino.Core uses these kernels.</summary>
        public static bool Registered => NinoKernels.Current is Kernels;

        /// <summary>The instruction set name of <see cref="Isa"/>, for logs.</summary>
        public static string IsaName => Isa switch
        {
            Ssse3 => "SSSE3",
            Neon => "AArch64 Neon",
            Avx2 => "AVX2",
            _ => "none",
        };

        // The kernels with every path the CPU has, and with the 16-byte ones only (on AVX2 CPUs, for measuring).
        private static Kernels _kernels, _kernels16;

        /// <summary>
        /// Compiles the kernels (once) and registers them with Nino.Core: false, with Nino's scalar paths kept, where
        /// Burst is off or the target has no SSSE3 or AArch64 Neon.
        /// </summary>
        public static bool Register() => Register(Avx2);

        /// <summary>
        /// <see cref="Register()"/>, with the x86 kernels limited to <paramref name="maxIsa"/>: <see cref="Ssse3"/> keeps
        /// an AVX2 CPU on the 16-byte paths (to compare the two; they write the same bytes).
        /// </summary>
        public static bool Register(int maxIsa)
        {
            if (_kernels == null)
            {
                if (!BurstCompiler.IsEnabled)
                    return false;
                try
                {
                    var isa = BurstCompiler.CompileFunctionPointer<IsaFunction>(InstructionSet).Invoke;
                    _best = isa();
                    if (_best == 0)
                        return false;
                    _kernels = new Kernels(BurstCompiler.CompileFunctionPointer<PermuteFunction>(Permute).Invoke,
                        BurstCompiler.CompileFunctionPointer<NarrowFunction>(Narrow).Invoke,
                        BurstCompiler.CompileFunctionPointer<WidenFunction>(Widen).Invoke);
                    _kernels16 = _best == Avx2
                        ? new Kernels(BurstCompiler.CompileFunctionPointer<PermuteFunction>(Permute16).Invoke,
                            _kernels.NarrowPointer, _kernels.WidenPointer)
                        : _kernels;
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"Nino: Burst kernels not available, using the scalar paths ({e.Message}).");
                    return false;
                }
            }

            bool wide = _best == Avx2 && maxIsa >= Avx2;
            Isa = _best == Avx2 && !wide ? Ssse3 : _best;
            NinoKernels.Register(wide ? _kernels : _kernels16);
            return true;
        }

        /// <summary>Unregisters the kernels: Nino.Core takes its scalar paths again.</summary>
        public static void Unregister()
        {
            if (Registered)
                NinoKernels.Register(null);
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void RegisterAtStartup() => Register();

#if UNITY_EDITOR
        // After the domain reload, once Burst's own loader has run.
        [UnityEditor.InitializeOnLoadMethod]
        private static void RegisterInEditor() => UnityEditor.EditorApplication.delayCall += () => Register();
#endif

        /// <summary>The kernels as Nino.Core calls them: each a call through the Burst function pointer.</summary>
        private sealed class Kernels : NinoKernels
        {
            private readonly PermuteFunction _permute;
            private readonly NarrowFunction _narrow;
            private readonly WidenFunction _widen;

            public NarrowFunction NarrowPointer => _narrow;
            public WidenFunction WidenPointer => _widen;

            public Kernels(PermuteFunction permute, NarrowFunction narrow, WidenFunction widen)
            {
                _permute = permute;
                _narrow = narrow;
                _widen = widen;
            }

            public override void Permute(byte* source, int sourceStride, byte* destination, int destinationStride,
                byte* table, int count) => _permute(source, sourceStride, destination, destinationStride, table, count);

            public override bool Narrow(char* source, byte* destination, int length) =>
                _narrow((ushort*)source, destination, length) != 0;

            public override void Widen(byte* source, char* destination, int length) =>
                _widen(source, (ushort*)destination, length);
        }

        [BurstCompile(CompileSynchronously = true)]
        [MonoPInvokeCallback(typeof(IsaFunction))]
        public static int InstructionSet()
        {
            if (Arm.Neon.IsNeonSupported)
                return Neon;
            if (X86.Avx2.IsAvx2Supported)
                return Avx2;
            if (X86.Ssse3.IsSsse3Supported)
                return Ssse3;
            return 0;
        }

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

        private static void PermuteAll(byte* source, int sourceStride, byte* destination, int destinationStride,
            byte* table, int count, bool wide)
        {
            int i = 0;
            if (wide && count >= Avx2MinValues && sourceStride <= 16 && destinationStride <= 16)
                i = PermuteAvx2(source, sourceStride, destination, destinationStride, table, count);
            if ((Arm.Neon.IsNeonSupported || X86.Ssse3.IsSsse3Supported) && sourceStride <= 64 &&
                destinationStride <= 64 && i < count)
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

            byte* at0 = source, to0 = destination;
            int n = last + 1;
            if (loads == 1 && stores == 1)
            {
                // Values and blocks of up to 16 bytes (most tuples and small structs): one shuffle each.
                v128 m = masks[0];
                for (int k = 0; k < n; k++)
                {
                    Store(to0, Shuffle(Load(at0), m));
                    at0 += sourceStride;
                    to0 += destinationStride;
                }

                return n;
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

        /// <summary>
        /// <see cref="NinoKernels.Narrow"/>, 16 chars at a time (the last 16 again, overlapping): 1 when all are Latin-1,
        /// 0 at the first 16 with a char above U+00FF. No 32-byte path: on the CI players' CPUs it was no faster
        /// (strings of 1000 and 100000 chars, within a few percent either way).
        /// </summary>
        [BurstCompile(CompileSynchronously = true)]
        [MonoPInvokeCallback(typeof(NarrowFunction))]
        public static int Narrow(ushort* source, byte* destination, int length)
        {
            if ((Arm.Neon.IsNeonSupported || X86.Sse2.IsSse2Supported) && length >= 16)
            {
                int i = 0;
                while (true)
                {
                    v128 a = Load((byte*)(source + i)), b = Load((byte*)(source + i + 8));
                    if (AboveLatin1(a, b))
                        return 0;
                    Store(destination + i, Pack(a, b));
                    if (i + 16 == length)
                        return 1;
                    i = i + 32 <= length ? i + 16 : length - 16;
                }
            }

            for (int i = 0; i < length; i++)
            {
                if (source[i] > 0xFF)
                    return 0;
                destination[i] = (byte)source[i];
            }

            return 1;
        }

        /// <summary><see cref="NinoKernels.Widen"/>, 16 bytes at a time (the last 16 again, overlapping).</summary>
        [BurstCompile(CompileSynchronously = true)]
        [MonoPInvokeCallback(typeof(WidenFunction))]
        public static void Widen(byte* source, ushort* destination, int length)
        {
            if ((Arm.Neon.IsNeonSupported || X86.Sse2.IsSse2Supported) && length >= 16)
            {
                int i = 0;
                while (true)
                {
                    v128 v = Load(source + i);
                    Store((byte*)(destination + i), Low(v));
                    Store((byte*)(destination + i + 8), High(v));
                    if (i + 16 == length)
                        return;
                    i = i + 32 <= length ? i + 16 : length - 16;
                }
            }

            for (int i = 0; i < length; i++)
                destination[i] = source[i];
        }

        // The vector operations, one instruction each on both instruction sets. Only called where one of them is
        // supported. Burst requires each instruction to sit under a test of its own IsXxxSupported in the same
        // method: a player is compiled ahead of time for a baseline (SSE2 on x64, none on Armv7 or WebGL) and
        // rejects an SSSE3 or even SSE2 instruction outside such a test, although the editor, compiling for the
        // machine it runs on, accepts it. The final `default` is never reached.

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
