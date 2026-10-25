// Burst-compiled kernels for Nino.Core (NinoKernels). This assembly compiles only when com.unity.burst is installed
// (NINO_BURST, set by the asmdef's versionDefines); without it Nino keeps its scalar paths.
//
// Unity loads the netstandard2.1 build of Nino.Core, which has no hardware intrinsics: on Mono, IL2CPP and HybridCLR it
// moves blocks of tuples and structs a field at a time and narrows or widens Latin-1 text a word at a time. The kernels
// here do that work with SSSE3 (x86 and x64) or AArch64 Neon, called through Burst function pointers.
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

        /// <summary>
        /// The instruction set the kernels were compiled for: 1 SSSE3, 2 AArch64 Neon, 0 none (the scalar branches, or
        /// the managed code when Burst did not compile them). Set by <see cref="Register"/>.
        /// </summary>
        public static int Isa { get; private set; }

        /// <summary>Whether Nino.Core uses these kernels.</summary>
        public static bool Registered => NinoKernels.Current is Kernels;

        private static Kernels _kernels;

        /// <summary>
        /// Compiles the kernels (once) and registers them with Nino.Core: false, with Nino's scalar paths kept, where
        /// Burst is off or the target has no SSSE3 or AArch64 Neon.
        /// </summary>
        public static bool Register()
        {
            if (_kernels == null)
            {
                if (!BurstCompiler.IsEnabled)
                    return false;
                try
                {
                    var isa = BurstCompiler.CompileFunctionPointer<IsaFunction>(InstructionSet).Invoke;
                    Isa = isa();
                    if (Isa == 0)
                        return false;
                    _kernels = new Kernels(BurstCompiler.CompileFunctionPointer<PermuteFunction>(Permute).Invoke,
                        BurstCompiler.CompileFunctionPointer<NarrowFunction>(Narrow).Invoke,
                        BurstCompiler.CompileFunctionPointer<WidenFunction>(Widen).Invoke);
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"Nino: Burst kernels not available, using the scalar paths ({e.Message}).");
                    return false;
                }
            }

            NinoKernels.Register(_kernels);
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
                return 2;
            if (X86.Ssse3.IsSsse3Supported)
                return 1;
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
            byte* table, int count)
        {
            int i = 0;
            if ((Arm.Neon.IsNeonSupported || X86.Ssse3.IsSsse3Supported) && sourceStride <= 64 &&
                destinationStride <= 64)
                i = PermuteVectors(source, sourceStride, destination, destinationStride, table, count);
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
        /// <see cref="NinoKernels.Narrow"/>, 16 chars at a time (the last 16 again, overlapping): 1 when all are Latin-1,
        /// 0 at the first 16 with a char above U+00FF.
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
