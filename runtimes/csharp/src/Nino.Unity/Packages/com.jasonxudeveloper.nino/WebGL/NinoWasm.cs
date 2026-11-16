// WebAssembly SIMD kernels for Nino.Core (NinoKernels) in WebGL players: nino_wasm.c, compiled by Unity's Emscripten
// into the player. Registered at startup when the player was built with WebAssembly SIMD (Unity 6: Target WebAssembly
// 2023; Unity 2022.3: --compiler-flags=-msimd128 in Additional IL2CPP Arguments, which Safari before 16.4 cannot load);
// otherwise nothing changes. Only WebGL players compile this assembly (Nino.WebGL.asmdef).
#if UNITY_WEBGL && !UNITY_EDITOR
using System;
using System.Runtime.InteropServices;
using Nino.Core;
using UnityEngine;

namespace Nino.WebGL
{
    /// <summary>Registers the WebAssembly SIMD <see cref="NinoKernels"/> with Nino.Core at startup.</summary>
    public static unsafe class NinoWasm
    {
        // Writes the kernels' function pointers (Permute, Narrow, Widen, NarrowAscii, WidenAscii) to table: 1, or 0
        // (nothing written) in a player built without SIMD. Called once; Nino.Core calls the pointers themselves.
        [DllImport("__Internal")]
        private static extern int nino_wasm_kernels(IntPtr* table);

        private static NinoKernels _kernels;
        private static bool _loaded;

        // The thresholds from unity.yml's WebGL job (Unity 6000.3 and 2022.3, headless Chrome on Linux x64, every
        // size forced through the kernels): Latin-1 strings were faster from 16 chars (1.07-1.45x, up to 10x for long
        // text), raw structs with padding from 32 values (0.93x at 16, 1.07-1.21x at 32, 2.3-5.3x from 1000). Blocks
        // of tuples gained nothing consistent (within 7 % up to 1000 values, 0.77-1.28x at 100,000).
        private static NinoKernels Kernels
        {
            get
            {
                if (_loaded)
                    return _kernels;
                IntPtr* table = stackalloc IntPtr[5];
                if (nino_wasm_kernels(table) != 0)
                    _kernels = new NinoKernels
                    {
                        MinBlocks = 32,
                        MinChars = 16,
                        Permute = (delegate* unmanaged[Cdecl]<byte*, int, byte*, int, byte*, int, void>)table[0],
                        Narrow = (delegate* unmanaged[Cdecl]<ushort*, byte*, int, int>)table[1],
                        Widen = (delegate* unmanaged[Cdecl]<byte*, ushort*, int, void>)table[2],
                        NarrowAscii = (delegate* unmanaged[Cdecl]<ushort*, byte*, int, int>)table[3],
                        WidenAscii = (delegate* unmanaged[Cdecl]<byte*, ushort*, int, int>)table[4],
                    };
                _loaded = true;
                return _kernels;
            }
        }

        /// <summary>Whether the player was built with WebAssembly SIMD (the kernels' only path).</summary>
        public static bool Simd => Kernels != null;

        /// <summary>Whether Nino.Core uses these kernels.</summary>
        public static bool Registered => NinoKernels.Current != null && NinoKernels.Current == _kernels;

        /// <summary>Registers the kernels: false, with Nino's scalar paths kept, in a player built without SIMD.</summary>
        public static bool Register()
        {
            var kernels = Kernels;
            if (kernels == null)
                return false;
            NinoKernels.Register(kernels);
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
    }
}
#endif
