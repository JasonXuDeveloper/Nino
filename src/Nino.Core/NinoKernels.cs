using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace Nino.Core
{
    /// <summary>
    /// Native kernels for the netstandard builds of Nino.Core, which have no hardware intrinsics: Unity (Mono, IL2CPP,
    /// HybridCLR) and .NET Framework take scalar code for the work below unless kernels are registered. Unity's package
    /// registers Burst-compiled ones (its Nino.Burst assembly, compiled when com.unity.burst is installed) at startup;
    /// without any, nothing changes. The .NET 8 and later builds use their own vector paths: the type is there too, so
    /// code built against any of Nino.Core's targets runs on all of them, but those builds never call the kernels.
    /// </summary>
    /// <remarks>
    /// Each kernel must produce exactly the bytes of its scalar reference (<see cref="PermuteScalar"/>,
    /// <see cref="NarrowScalar"/>, <see cref="WidenScalar"/>): the wire format does not depend on which path ran.
    /// Nino calls a kernel only for runs of at least <see cref="MinBlocks"/> values or <see cref="MinChars"/> chars, where
    /// the call costs less than it saves. Register kernels before serializing, from one thread.
    /// </remarks>
    public abstract unsafe class NinoKernels
    {
        /// <summary>The registered kernels, or null.</summary>
        public static NinoKernels Current => NinoKernelHook.Kernels;

        /// <summary>Registers <paramref name="kernels"/> (null: back to the scalar paths).</summary>
        public static void Register(NinoKernels kernels)
        {
            // Thresholds off first and on last: a thread between the two sees no kernels, or the new ones whole.
            NinoKernelHook.MinBlocks = int.MaxValue;
            NinoKernelHook.MinChars = int.MaxValue;
            Volatile.Write(ref NinoKernelHook.Kernels, kernels);
            if (kernels == null)
                return;
            NinoKernelHook.MinBlocks = Math.Max(1, kernels.MinBlocks);
            NinoKernelHook.MinChars = Math.Max(16, kernels.MinChars);
        }

        /// <summary>The fewest values of a collection <see cref="Permute"/> is called for.</summary>
        public virtual int MinBlocks => 16;

        /// <summary>The fewest chars of a string <see cref="Narrow"/> and <see cref="Widen"/> are called for (at least 16).</summary>
        public virtual int MinChars => 32;

        /// <summary>
        /// Moves <paramref name="count"/> values from <paramref name="source"/>, <paramref name="sourceStride"/> bytes apart,
        /// to <paramref name="destination"/>, <paramref name="destinationStride"/> bytes apart: byte j of each destination
        /// value is byte <c>table[j]</c> of its source value, or 0 where <c>table[j]</c> is 0xFF. Strides are 1 to 64; the
        /// two ranges do not overlap. Writes a value's struct to its block (no 0xFF), reads it back (0xFF for padding),
        /// and copies raw structs with their padding cleared.
        /// </summary>
        public abstract void Permute(byte* source, int sourceStride, byte* destination, int destinationStride,
            byte* table, int count);

        /// <summary>
        /// Narrows the <paramref name="length"/> UTF-16 chars at <paramref name="source"/> to one byte each at
        /// <paramref name="destination"/> (Latin-1); false when a char is above U+00FF, having written any part of them.
        /// </summary>
        public abstract bool Narrow(char* source, byte* destination, int length);

        /// <summary>Widens the <paramref name="length"/> Latin-1 bytes at <paramref name="source"/> to UTF-16 chars at <paramref name="destination"/>.</summary>
        public abstract void Widen(byte* source, char* destination, int length);

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

        /// <summary>What <see cref="Narrow"/> must do, one char at a time.</summary>
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
    }

    /// <summary>The registered <see cref="NinoKernels"/> and their thresholds (int.MaxValue without kernels).</summary>
    internal static unsafe class NinoKernelHook
    {
        internal static NinoKernels Kernels;

        // Tested first on each path: one load and a compare, failing without kernels.
        internal static int MinBlocks = int.MaxValue, MinChars = int.MaxValue;

        /// <summary>
        /// <see cref="NinoKernels.Permute"/> of the <paramref name="count"/> values at <paramref name="source"/>; false
        /// (nothing moved) without kernels. Out of line, the pins with it.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static bool Permute(ref byte source, int sourceStride, ref byte destination, int destinationStride,
            IntPtr table, int count)
        {
            var kernels = Kernels;
            if (kernels == null)
                return false;
            fixed (byte* from = &source, to = &destination)
                kernels.Permute(from, sourceStride, to, destinationStride, (byte*)table, count);
            return true;
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
