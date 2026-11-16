using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Nino.Core
{
    /// <summary>
    /// The registered <see cref="NinoKernels"/>: each kernel's function pointer (null without one) and the shortest
    /// run it is called for (int.MaxValue without it). Each call site tests its threshold inline; past it, a helper
    /// here loads the pointer once, tests it for null and calls it: no object and no virtual call.
    /// </summary>
    internal static unsafe class NinoKernelHook
    {
        internal static NinoKernels Registered;

        internal static delegate* unmanaged[Cdecl]<byte*, int, byte*, int, byte*, int, void> Permute;
        internal static delegate* unmanaged[Cdecl]<int*, int*, byte*, int, void> Interleave32;
        internal static delegate* unmanaged[Cdecl]<byte*, byte*, int, int> NormalizeBoolSlots;
        internal static delegate* unmanaged[Cdecl]<ushort*, byte*, int, int> Narrow;
        internal static delegate* unmanaged[Cdecl]<byte*, ushort*, int, void> Widen;
        internal static delegate* unmanaged[Cdecl]<ushort*, byte*, int, int> NarrowAscii;
        internal static delegate* unmanaged[Cdecl]<byte*, ushort*, int, int> WidenAscii;

        /// <summary>Whether a <see cref="NinoKernels.Permute"/> kernel is registered.</summary>
        internal static bool HasPermute => Permute != null;

        // Tested first on each path: one load and a compare, failing without kernels.
        internal static int MinBlocks = int.MaxValue, MinCopies = int.MaxValue;
        internal static int MinNarrow = int.MaxValue, MinWiden = int.MaxValue;
        internal static int MinNarrowAscii = int.MaxValue, MinWidenAscii = int.MaxValue;

        // The fewest raw structs with padding copied through the kernels: their scalar path copies masked words, so
        // the kernels' call and setup only paid off from 12 to 16 values (Burst, IL2CPP players on Linux x64, Unity
        // 2022.3 and 6000.3: 0.75-0.8x at 1-2 values, 1.06-1.09x at 16), where blocks of tuples gain from 2.
        internal const int CopyFloor = 16;

        internal static int MinInterleave = int.MaxValue;
        internal static int MinBoolSlots = int.MaxValue;
        internal static int MinWriteBoolSlots = int.MaxValue;

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static int TryNormalizeBoolSlots(ref byte source, ref byte destination, int count)
        {
            var normalize = NormalizeBoolSlots;
            if (normalize == null)
                return 0;
            fixed (byte* from = &source, to = &destination)
                return normalize(from, to, count);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static int TryInterleave32(ref int first, ref int second, ref byte target, int count)
        {
            var interleave = Interleave32;
            if (interleave == null)
                return 0;

            int pairs = count & ~3;
            fixed (int* a = &first, b = &second)
            fixed (byte* into = &target)
                interleave(a, b, into, pairs);
            return pairs;
        }

        /// <summary>
        /// <see cref="NinoKernels.Permute"/> of the <paramref name="count"/> values at <paramref name="source"/>; false
        /// (nothing moved) without the kernel. Out of line, the pins with it.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static bool TryPermute(ref byte source, int sourceStride, ref byte destination, int destinationStride,
            IntPtr table, int count)
        {
            var permute = Permute;
            if (permute == null)
                return false;
            fixed (byte* from = &source, to = &destination)
                permute(from, sourceStride, to, destinationStride, (byte*)table, count);
            return true;
        }

        // The text kernels' calls, out of line like TryPermute: a method holding a calli is never inlined by Mono's
        // JIT, so the paths that test the thresholds stay inlinable (NarrowWords into WriteLong, Decode into
        // ReadUtf8At) and only the kernel path pays this call.

        /// <summary><see cref="NinoKernels.Narrow"/>: 1 or 0 as the kernel returns, -1 (nothing done) without it.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static int TryNarrow(ref char source, ref byte destination, int length)
        {
            var narrow = Narrow;
            if (narrow == null)
                return -1;
            fixed (char* from = &source)
            fixed (byte* to = &destination)
                return narrow((ushort*)from, to, length) != 0 ? 1 : 0;
        }

        /// <summary><see cref="NinoKernels.Widen"/>; false (nothing done) without it.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static bool TryWiden(ref byte source, ref ushort destination, int length)
        {
            var widen = Widen;
            if (widen == null)
                return false;
            fixed (byte* from = &source)
            fixed (ushort* to = &destination)
                widen(from, to, length);
            return true;
        }

        /// <summary><see cref="NinoKernels.NarrowAscii"/>: the leading ASCII chars written, 0 without it.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static int NarrowAsciiPrefix(char* source, byte* destination, int length)
        {
            var narrowAscii = NarrowAscii;
            return narrowAscii == null ? 0 : narrowAscii((ushort*)source, destination, length);
        }

        /// <summary><see cref="NinoKernels.WidenAscii"/>: the leading ASCII bytes widened, 0 without it.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static int WidenAsciiPrefix(byte* source, char* destination, int length)
        {
            var widenAscii = WidenAscii;
            return widenAscii == null ? 0 : widenAscii(source, (ushort*)destination, length);
        }

        /// <summary>
        /// A permute table in unmanaged memory, kept for the life of the process (one per type, built once):
        /// <paramref name="length"/> bytes, all 0xFF until set.
        /// </summary>
        internal static IntPtr Table(int length)
        {
            var table = Marshal.AllocHGlobal(length);
            for (int j = 0; j < length; j++)
                ((byte*)table)[j] = 0xFF;
            return table;
        }

        /// <summary>
        /// The tables of a value's block from its (memory offset, block offset, size) runs: for each block byte, the
        /// memory byte it comes from (<paramref name="toWire"/>), or for each memory byte, the block byte (0xFF:
        /// padding). Zero when either size is above 64.
        /// </summary>
        internal static IntPtr Table(int[] runs, int size, int memorySize, bool toWire)
        {
            if (runs == null || size > 64 || memorySize > 64)
                return IntPtr.Zero;
            var table = Table(toWire ? size : memorySize);
            for (int i = 0; i < runs.Length; i += 3)
            for (int b = 0; b < runs[i + 2]; b++)
            {
                int memory = runs[i] + b, wire = runs[i + 1] + b;
                if (toWire)
                    ((byte*)table)[wire] = (byte)memory;
                else
                    ((byte*)table)[memory] = (byte)wire;
            }

            return table;
        }
    }
}
