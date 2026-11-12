// Burst-compiled kernels for Nino.Core (NinoKernels). This assembly compiles only when com.unity.burst is installed
// (NINO_BURST, set by the asmdef's versionDefines); without it Nino keeps its scalar paths.
//
// Unity loads the netstandard2.1 build of Nino.Core, which has no hardware intrinsics: on Mono, IL2CPP and HybridCLR it
// moves blocks of tuples and structs a field at a time, narrows or widens Latin-1 text a word at a time and transcodes
// [NinoUtf8] strings with UTF8Encoding. The kernels here do that work with AVX2 or SSSE3 (x86 and x64) or AArch64 Neon
// (for UTF-8, the leading ASCII text; the encoder does the rest).
//
// Calls: Nino.Core keeps each kernel's Burst function pointer (FunctionPointer<T>.Value) in a static field and calls it
// as a C# 9 function pointer (delegate* unmanaged[Cdecl]), which Mono and IL2CPP compile to a direct call of the native
// code: no delegate, no object, no virtual call. Register checks that call form once (InstructionSet through the
// pointer must return what its delegate returns) and registers nothing where it does not hold; then it calls every
// kernel through its pointer against Nino.Core's scalar code and leaves out any that throws or differs
// (NinoBurst.SelfTest.cs), since Burst versions compile the same code differently (NinoBurst.Vectors.cs).
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
// - HybridCLR: hot-update code calls the AOT Nino.Core, which calls these kernels; nothing here is interpreted. Burst
//   compiles only AOT assemblies at build time, so a hot-updated Nino.Core (which needs a hot-updated Nino.Burst: an
//   AOT assembly cannot reference a hot one) has no kernels: Unity runs no [RuntimeInitializeOnLoadMethod] of a hot
//   assembly, and a call to Register there finds no Burst code for its methods and keeps the scalar paths.
// - Armv7 (32-bit Android) and x86 targets without SSSE3: Burst's Neon intrinsics are AArch64 only, so the kernels
//   would take their scalar branch; Register sees that (Isa returns 0) and leaves Nino's own scalar paths in place.
// - WebGL and any player where Burst does not run (BurstCompiler.IsEnabled false): nothing is registered (WebGL players
//   built with SIMD take Nino.WebGL's kernels instead).
#if NINO_BURST
using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using AOT;
using Nino.Core;
using Unity.Burst;
using Unity.Burst.Intrinsics;
using UnityEngine;

[assembly: InternalsVisibleTo("Burst.Tests")]

namespace Nino.Burst
{
    /// <summary>
    /// Registers Burst-compiled <see cref="NinoKernels"/> with Nino.Core at startup (and in the editor after each domain
    /// reload). <see cref="Register"/> and <see cref="Unregister"/> switch them by hand.
    /// </summary>
    [BurstCompile]
    public static unsafe partial class NinoBurst
    {
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

        // Every kernel failed its self-test: Register neither compiles nor tests (nor warns) again.
        private static bool _failed;

        /// <summary>Whether Nino.Core uses these kernels.</summary>
        public static bool Registered => NinoKernels.Current != null &&
                                         (NinoKernels.Current == _kernels || NinoKernels.Current == _kernels16);

        /// <summary>The instruction set name of <see cref="Isa"/>, for logs.</summary>
        public static string IsaName => Isa switch
        {
            Ssse3 => "SSSE3",
            Neon => "AArch64 Neon",
            Avx2 => "AVX2",
            _ => "none",
        };

        // The kernels with every path the CPU has, and with the 16-byte ones only (on AVX2 CPUs, for measuring).
        private static NinoKernels _kernels, _kernels16;

        /// <summary>
        /// Compiles the kernels (once) and registers them with Nino.Core: false, with Nino's scalar paths kept, where
        /// Burst is off, the target has no SSSE3 or AArch64 Neon, or the runtime cannot call Burst's function pointers.
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
                if (_failed || !BurstCompiler.IsEnabled)
                    return false;
                try
                {
                    var isa = BurstCompiler.CompileFunctionPointer<IsaFunction>(InstructionSet);
                    _best = isa.Invoke();
                    if (_best == 0)
                        return false;
                    if (!PointersWork(isa))
                    {
                        // Nino.Core calls kernels only through function pointers: a runtime that cannot keeps its own
                        // paths (no Unity the package supports).
                        Debug.LogWarning("Nino: Burst function pointers cannot be called, using the scalar paths.");
                        return false;
                    }

                    var permute = BurstCompiler.CompileFunctionPointer<PermuteFunction>(Permute).Value;
                    var interleave = BurstCompiler.CompileFunctionPointer<Interleave32Function>(Interleave32).Value;
                    var boolSlots = BurstCompiler.CompileFunctionPointer<NormalizeBoolSlotsFunction>(NormalizeBoolSlots).Value;
                    var narrow = BurstCompiler.CompileFunctionPointer<NarrowFunction>(Narrow).Value;
                    var widen = BurstCompiler.CompileFunctionPointer<WidenFunction>(Widen).Value;
                    var narrowAscii = BurstCompiler.CompileFunctionPointer<NarrowFunction>(NarrowAscii).Value;
                    var widenAscii = BurstCompiler.CompileFunctionPointer<WidenAsciiFunction>(WidenAscii).Value;
                    NinoKernels Make(IntPtr permuteKernel, IntPtr interleaveKernel, IntPtr boolSlotsKernel) => new NinoKernels
                    {
                        // The thresholds from a sweep in unity.yml run 40735278928 (IL2CPP players, Linux x64, 2022.3
                        // and 6000.3): blocks of tuples were faster through the kernels from 2 values (1.09-1.30x;
                        // 0.99-1.14x at 1), strings of Latin-1 text from 16 chars (1.24-1.55x); text above U+00FF
                        // costs the same few percent at any length.
                        MinBlocks = 2,
                        MinChars = 16,
                        Permute = (delegate* unmanaged[Cdecl]<byte*, int, byte*, int, byte*, int, void>)permuteKernel,
                        Interleave32 = (delegate* unmanaged[Cdecl]<int*, int*, byte*, int, void>)interleaveKernel,
                        NormalizeBoolSlots = (delegate* unmanaged[Cdecl]<byte*, byte*, int, int>)boolSlotsKernel,
                        Narrow = (delegate* unmanaged[Cdecl]<ushort*, byte*, int, int>)narrow,
                        Widen = (delegate* unmanaged[Cdecl]<byte*, ushort*, int, void>)widen,
                        NarrowAscii = (delegate* unmanaged[Cdecl]<ushort*, byte*, int, int>)narrowAscii,
                        WidenAscii = (delegate* unmanaged[Cdecl]<byte*, ushort*, int, int>)widenAscii,
                    };
                    var kernels = Make(permute, interleave, boolSlots);
                    var kernels16 = _best == Avx2
                        ? Make(BurstCompiler.CompileFunctionPointer<PermuteFunction>(Permute16).Value,
                            BurstCompiler.CompileFunctionPointer<Interleave32Function>(Interleave32Narrow).Value,
                            BurstCompiler.CompileFunctionPointer<NormalizeBoolSlotsFunction>(NormalizeBoolSlots128).Value)
                        : kernels;
                    // Every kernel through its pointer against Nino.Core's scalar code (NinoBurst.SelfTest.cs): one
                    // that throws or differs is left out, the others kept.
                    bool any = SelfTest(kernels, "");
                    if (kernels16 != kernels)
                        any |= SelfTest(kernels16, " (16-byte paths)");
                    if (!any)
                    {
                        _failed = true;
                        return false;
                    }

                    _kernels = kernels;
                    _kernels16 = kernels16;
                }
                catch (Exception e)
                {
                    _kernels = null;
                    Debug.LogWarning($"Nino: Burst kernels not available, using the scalar paths ({e.Message}).");
                    return false;
                }
            }

            bool wide = _best == Avx2 && maxIsa >= Avx2;
            Isa = _best == Avx2 && !wide ? Ssse3 : _best;
            NinoKernels.Register(wide ? _kernels : _kernels16);
            return true;
        }

        // Whether a call through the function pointer returns what the delegate's call returned (_best).
        private static bool PointersWork(FunctionPointer<IsaFunction> isa)
        {
            try
            {
                return isa.Value != IntPtr.Zero && ((delegate* unmanaged[Cdecl]<int>)isa.Value)() == _best;
            }
            catch (Exception)
            {
                return false;
            }
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
    }
}
#endif
