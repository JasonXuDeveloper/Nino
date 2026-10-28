// WebAssembly SIMD kernels for Nino.Core (NinoKernels) in WebGL players: nino_wasm.c, compiled by Unity's Emscripten
// into the player. Registered at startup when the player was built with WebAssembly SIMD (Unity 6: Target WebAssembly
// 2023; Unity 2022.3: --compiler-flags=-msimd128 in Additional IL2CPP Arguments, which Safari before 16.4 cannot load);
// otherwise nothing changes. Only WebGL players compile this assembly (Nino.WebGL.asmdef).
#if UNITY_WEBGL && !UNITY_EDITOR
using System.Runtime.InteropServices;
using Nino.Core;
using UnityEngine;

namespace Nino.WebGL
{
    /// <summary>Registers the WebAssembly SIMD <see cref="NinoKernels"/> with Nino.Core at startup.</summary>
    public static unsafe class NinoWasm
    {
        [DllImport("__Internal")]
        private static extern int nino_wasm_simd();

        [DllImport("__Internal")]
        private static extern void nino_wasm_permute(byte* source, int sourceStride, byte* destination,
            int destinationStride, byte* table, int count);

        [DllImport("__Internal")]
        private static extern int nino_wasm_narrow(char* source, byte* destination, int length);

        [DllImport("__Internal")]
        private static extern void nino_wasm_widen(byte* source, char* destination, int length);

        /// <summary>Whether the player was built with WebAssembly SIMD (the kernels' only path).</summary>
        public static bool Simd => nino_wasm_simd() != 0;

        /// <summary>Whether Nino.Core uses these kernels.</summary>
        public static bool Registered => NinoKernels.Current is Kernels;

        /// <summary>Registers the kernels: false, with Nino's scalar paths kept, in a player built without SIMD.</summary>
        public static bool Register()
        {
            if (!Simd)
                return false;
            NinoKernels.Register(new Kernels());
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

        // The thresholds from unity.yml's WebGL job (Unity 6000.3 and 2022.3, headless Chrome on Linux x64, every
        // size forced through the kernels): Latin-1 strings were faster from 16 chars (1.07-1.45x, up to 10x for long
        // text), raw structs with padding from 32 values (0.93x at 16, 1.07-1.21x at 32, 2.3-5.3x from 1000). Blocks
        // of tuples gained nothing consistent (within 7 % up to 1000 values, 0.77-1.28x at 100,000).
        private sealed class Kernels : NinoKernels
        {
            public override int MinBlocks => 32;
            public override int MinChars => 16;

            public override void Permute(byte* source, int sourceStride, byte* destination, int destinationStride,
                byte* table, int count) =>
                nino_wasm_permute(source, sourceStride, destination, destinationStride, table, count);

            public override bool Narrow(char* source, byte* destination, int length) =>
                nino_wasm_narrow(source, destination, length) != 0;

            public override void Widen(byte* source, char* destination, int length) =>
                nino_wasm_widen(source, destination, length);
        }
    }
}
#endif
