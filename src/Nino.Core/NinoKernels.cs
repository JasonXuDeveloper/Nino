using System;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Nino.Core
{
    /// <summary>
    /// Native kernels for the netstandard2.1 build of Nino.Core, which has no hardware intrinsics: Unity (Mono, IL2CPP,
    /// HybridCLR) and Mono take scalar code for the work below unless kernels are registered. Unity's
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
    /// <see cref="Interleave32Scalar"/>, <see cref="NarrowScalar"/>, <see cref="WidenScalar"/>, <see cref="NarrowAsciiScalar"/>,
    /// <see cref="WidenAsciiScalar"/>, <see cref="NormalizeBoolSlotsScalar"/>): the wire format does not depend on which
    /// path ran. Nino calls a kernel only for runs of at least <see cref="MinBlocks"/> values or
    /// <see cref="MinChars"/> chars, where the call costs less than it saves. Register kernels before serializing,
    /// from one thread.
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
            NinoKernelHook.MinInterleave = int.MaxValue;
            NinoKernelHook.MinBoolSlots = int.MaxValue;
            NinoKernelHook.MinWriteBoolSlots = int.MaxValue;
            NinoKernelHook.MinCopies = int.MaxValue;
            NinoKernelHook.MinNarrow = int.MaxValue;
            NinoKernelHook.MinWiden = int.MaxValue;
            NinoKernelHook.MinNarrowAscii = int.MaxValue;
            NinoKernelHook.MinWidenAscii = int.MaxValue;
            Thread.MemoryBarrier();
            NinoKernelHook.Permute = kernels == null ? null : kernels.Permute;
            NinoKernelHook.Interleave32 = kernels == null ? null : kernels.Interleave32;
            NinoKernelHook.NormalizeBoolSlots = kernels == null ? null : kernels.NormalizeBoolSlots;
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

            if (kernels.Interleave32 != null)
                NinoKernelHook.MinInterleave = Math.Max(16, minBlocks);
            if (kernels.NormalizeBoolSlots != null)
            {
                NinoKernelHook.MinBoolSlots = Math.Max(16, minBlocks);
                NinoKernelHook.MinWriteBoolSlots = Math.Max(32, minBlocks);
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
        /// structs with padding, whose own path already copies whole words; <see cref="Interleave32"/> uses at least
        /// 16 pairs, <see cref="NormalizeBoolSlots"/> at least 16 slots when reading and 32 when writing).
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
        /// <c>void Interleave32(int* first, int* second, byte* target, int count)</c>: writes the <c>count</c> pairs of
        /// four-byte values <c>first[i]</c>, <c>second[i]</c> as 8 bytes each at <c>target + 8 * i</c> (a raw
        /// <c>SortedList</c>'s keys and values as wire pairs). <c>count</c> is a multiple of four; the destination
        /// overlaps neither source and may be unaligned. Reference: <see cref="Interleave32Scalar"/>.
        /// </summary>
        public delegate* unmanaged[Cdecl]<int*, int*, byte*, int, void> Interleave32 { get; set; }

        /// <summary>
        /// Normalizes <c>count</c> nullable-bool slots from <c>source</c> into the nonoverlapping
        /// <c>destination</c>. Each slot is two little-endian bytes: a presence flag (0 or 1), then the bool value.
        /// Null slots become two zero bytes. Returns the number of completed leading slots (0 to count).
        /// If an active value exceeds 1, returns the start of its processing group before writing that group.
        /// Bytes after the completed prefix remain untouched. Reference: <see cref="NormalizeBoolSlotsScalar"/>.
        /// </summary>
        public delegate* unmanaged[Cdecl]<byte*, byte*, int, int> NormalizeBoolSlots { get; set; }

        /// <summary>The bytes <see cref="Interleave32"/> must write, one pair at a time.</summary>
        public static void Interleave32Scalar(int* first, int* second, byte* target, int count)
        {
            for (int i = 0; i < count; i++)
            {
                Unsafe.WriteUnaligned(target + 8 * (nint)i, first[i]);
                Unsafe.WriteUnaligned(target + 8 * (nint)i + 4, second[i]);
            }
        }

        /// <summary>What <see cref="NormalizeBoolSlots"/> does, with one slot per processing group.</summary>
        public static int NormalizeBoolSlotsScalar(byte* source, byte* destination, int count)
        {
            for (int i = 0; i < count; i++)
            {
                byte flag = source[2 * i], value = source[2 * i + 1];
                if (flag != 0 && value > 1)
                    return i;
                destination[2 * i] = flag;
                destination[2 * i + 1] = flag == 0 ? (byte)0 : value;
            }

            return count;
        }

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
}
