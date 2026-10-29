using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace Nino.Core
{
    /// <summary>
    /// Native kernels for the netstandard builds of Nino.Core, which have no hardware intrinsics: Unity (Mono, IL2CPP,
    /// HybridCLR) and .NET Framework take scalar code for the work below unless kernels are registered (.NET Framework
    /// narrows and widens long text with <c>System.Numerics.Vector&lt;T&gt;</c> where its JIT accelerates it). Unity's
    /// package registers Burst-compiled ones (its Nino.Burst assembly, compiled when com.unity.burst is installed) and,
    /// in WebGL players built with SIMD, WebAssembly ones (Nino.WebGL) at startup; without any, nothing changes. The
    /// .NET 8 and later builds use their own vector paths: the type is there too, so code built against any of
    /// Nino.Core's targets runs on all of them, but those builds never call the kernels.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The kernels are native functions (<c>cdecl</c>) given as function pointers: <see cref="Register"/> copies them
    /// into static fields, so Nino calls each one directly, with no object or virtual call in between. A kernel left
    /// null is not used: Nino keeps its own path for that work. Changing the instance after registering it changes
    /// nothing until it is registered again.
    /// </para>
    /// <para>
    /// Each kernel must produce exactly the bytes of its scalar reference (<see cref="PermuteScalar"/>,
    /// <see cref="NarrowScalar"/>, <see cref="WidenScalar"/>, <see cref="NarrowAsciiScalar"/>,
    /// <see cref="WidenAsciiScalar"/>): the wire format does not depend on which path ran. Nino calls a kernel only for
    /// runs of at least <see cref="MinBlocks"/> values or <see cref="MinChars"/> chars, where the call costs less than it
    /// saves. Register kernels before serializing, from one thread.
    /// </para>
    /// </remarks>
    public sealed unsafe class NinoKernels
    {
        /// <summary>The registered kernels, or null.</summary>
        public static NinoKernels Current => NinoKernelHook.Registered;

        /// <summary>
        /// Registers <paramref name="kernels"/> (null: back to the scalar paths): copies its function pointers and
        /// thresholds for Nino's paths to call.
        /// </summary>
        public static void Register(NinoKernels kernels)
        {
            // Thresholds off first and on last: a thread between the two sees no kernels, or the new ones whole (each
            // call site also loads its pointer once and tests it for null).
            NinoKernelHook.MinBlocks = int.MaxValue;
            NinoKernelHook.MinCopies = int.MaxValue;
            NinoKernelHook.MinNarrow = int.MaxValue;
            NinoKernelHook.MinWiden = int.MaxValue;
            NinoKernelHook.MinNarrowAscii = int.MaxValue;
            NinoKernelHook.MinWidenAscii = int.MaxValue;
            Thread.MemoryBarrier();
            NinoKernelHook.Permute = kernels == null ? null : kernels.Permute;
            NinoKernelHook.Narrow = kernels == null ? null : kernels.Narrow;
            NinoKernelHook.Widen = kernels == null ? null : kernels.Widen;
            NinoKernelHook.NarrowAscii = kernels == null ? null : kernels.NarrowAscii;
            NinoKernelHook.WidenAscii = kernels == null ? null : kernels.WidenAscii;
            Volatile.Write(ref NinoKernelHook.Registered, kernels);
            if (kernels == null)
                return;
            Thread.MemoryBarrier();
            int minBlocks = Math.Max(1, kernels.MinBlocks), minChars = Math.Max(16, kernels.MinChars);
            if (kernels.Permute != null)
            {
                NinoKernelHook.MinBlocks = minBlocks;
                NinoKernelHook.MinCopies = Math.Max(NinoKernelHook.CopyFloor, minBlocks);
            }

            if (kernels.Narrow != null)
                NinoKernelHook.MinNarrow = minChars;
            if (kernels.Widen != null)
                NinoKernelHook.MinWiden = minChars;
            if (kernels.NarrowAscii != null)
                NinoKernelHook.MinNarrowAscii = minChars;
            if (kernels.WidenAscii != null)
                NinoKernelHook.MinWidenAscii = minChars;
        }

        /// <summary>
        /// The fewest values of a collection <see cref="Permute"/> is called for (default 16; at least 16 to copy raw
        /// structs with padding, whose own path already copies whole words).
        /// </summary>
        public int MinBlocks { get; set; } = 16;

        /// <summary>The fewest chars of a string the text kernels are called for (default 32; at least 16).</summary>
        public int MinChars { get; set; } = 32;

        /// <summary>
        /// <c>void Permute(byte* source, int sourceStride, byte* destination, int destinationStride, byte* table, int
        /// count)</c>: moves <c>count</c> values from <c>source</c>, <c>sourceStride</c> bytes apart, to
        /// <c>destination</c>, <c>destinationStride</c> bytes apart: byte j of each destination value is byte
        /// <c>table[j]</c> of its source value, or 0 where <c>table[j]</c> is 0xFF. Strides are 1 to 64; the two ranges
        /// do not overlap. Writes a value's struct to its block (no 0xFF), reads it back (0xFF for padding), and copies
        /// raw structs with their padding cleared. Reference: <see cref="PermuteScalar"/>.
        /// </summary>
        public delegate* unmanaged[Cdecl]<byte*, int, byte*, int, byte*, int, void> Permute { get; set; }

        /// <summary>
        /// <c>int Narrow(ushort* source, byte* destination, int length)</c>: narrows the <c>length</c> UTF-16 chars at
        /// <c>source</c> to one byte each at <c>destination</c> (Latin-1, for strings); 1 when all were Latin-1, 0 when
        /// a char is above U+00FF, having written any part of them. Reference: <see cref="NarrowScalar"/>.
        /// </summary>
        public delegate* unmanaged[Cdecl]<ushort*, byte*, int, int> Narrow { get; set; }

        /// <summary>
        /// <c>void Widen(byte* source, ushort* destination, int length)</c>: widens the <c>length</c> Latin-1 bytes at
        /// <c>source</c> to UTF-16 chars at <c>destination</c>. Reference: <see cref="WidenScalar"/>.
        /// </summary>
        public delegate* unmanaged[Cdecl]<byte*, ushort*, int, void> Widen { get; set; }

        /// <summary>
        /// <c>int NarrowAscii(ushort* source, byte* destination, int length)</c>, for <c>[NinoUtf8]</c> strings:
        /// writes the leading ASCII chars (below U+0080) of the <c>length</c> chars at <c>source</c> as a byte each
        /// at <c>destination</c> and returns how many there are (<c>length</c> when all are); bytes past those may be
        /// written too, up to <c>length</c>. Nino encodes the rest as UTF-8 itself. Reference:
        /// <see cref="NarrowAsciiScalar"/>.
        /// </summary>
        public delegate* unmanaged[Cdecl]<ushort*, byte*, int, int> NarrowAscii { get; set; }

        /// <summary>
        /// <c>int WidenAscii(byte* source, ushort* destination, int length)</c>, for <c>[NinoUtf8]</c> strings: widens
        /// the leading ASCII bytes (below 0x80) of the <c>length</c> bytes at <c>source</c> to a char each at
        /// <c>destination</c> and returns how many there are (<c>length</c> when all are); chars past those may be
        /// written too, up to <c>length</c>. Nino decodes and validates the rest itself. Reference:
        /// <see cref="WidenAsciiScalar"/>.
        /// </summary>
        public delegate* unmanaged[Cdecl]<byte*, ushort*, int, int> WidenAscii { get; set; }

        /// <summary>The bytes <see cref="Permute"/> must write, one at a time.</summary>
        public static void PermuteScalar(byte* source, int sourceStride, byte* destination, int destinationStride,
            byte* table, int count)
        {
            for (int i = 0; i < count; i++)
            {
                byte* from = source + (nint)i * sourceStride, to = destination + (nint)i * destinationStride;
                for (int j = 0; j < destinationStride; j++)
                    to[j] = table[j] == 0xFF ? (byte)0 : from[table[j]];
            }
        }

        /// <summary>What <see cref="Narrow"/> must do, one char at a time (true for 1).</summary>
        public static bool NarrowScalar(char* source, byte* destination, int length)
        {
            for (int i = 0; i < length; i++)
            {
                if (source[i] > 0xFF)
                    return false;
                destination[i] = (byte)source[i];
            }

            return true;
        }

        /// <summary>What <see cref="Widen"/> must do, one char at a time.</summary>
        public static void WidenScalar(byte* source, char* destination, int length)
        {
            for (int i = 0; i < length; i++)
                destination[i] = (char)source[i];
        }

        /// <summary>What <see cref="NarrowAscii"/> must do, one char at a time.</summary>
        public static int NarrowAsciiScalar(char* source, byte* destination, int length)
        {
            int i = 0;
            for (; i < length && source[i] < 0x80; i++)
                destination[i] = (byte)source[i];
            return i;
        }

        /// <summary>What <see cref="WidenAscii"/> must do, one byte at a time.</summary>
        public static int WidenAsciiScalar(byte* source, char* destination, int length)
        {
            int i = 0;
            for (; i < length && source[i] < 0x80; i++)
                destination[i] = (char)source[i];
            return i;
        }
    }

    /// <summary>
    /// The registered <see cref="NinoKernels"/>: each kernel's function pointer (null without one) and the shortest
    /// run it is called for (int.MaxValue without it). Each call site tests its threshold inline; past it, a helper
    /// here loads the pointer once, tests it for null and calls it: no object and no virtual call.
    /// </summary>
    internal static unsafe class NinoKernelHook
    {
        internal static NinoKernels Registered;

        internal static delegate* unmanaged[Cdecl]<byte*, int, byte*, int, byte*, int, void> Permute;
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
