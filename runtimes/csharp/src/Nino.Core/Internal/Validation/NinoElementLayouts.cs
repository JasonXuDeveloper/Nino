using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nino.Core.CompilerServices;

namespace Nino.Core.Internal
{
    /// <summary>
    /// Arrays of <typeparamref name="T"/> that compiled code can fill on this runtime. IL2CPP lays out some structs
    /// with <c>[StructLayout(Pack)]</c> in them one way at run time and another in the C++ it compiles (libil2cpp's
    /// <c>GenericClass::CreateClass</c> never copies a generic definition's packing; a packed struct's run-time
    /// alignment applies <c>Pack</c> where its C++ struct does not: #378). It allocates an array with the run-time
    /// element size and compiled code indexes it with the C++ <c>sizeof</c>, so where that is larger, filling an array
    /// writes past it (Unity 2022.3.62f2 and 6000.6.9f1: a <c>HoldsPacked?</c> array has 36-byte elements that compiled
    /// code writes 40 bytes at a time), in Nino and in the user's code alike. The arrays Nino creates of such a type
    /// are allocated with enough elements for compiled code and then given the length asked for (IL2CPP's
    /// <c>Il2CppArray::max_length</c>, the word after the header's bounds), so every element compiled code writes lies
    /// inside them; compiled code, Nino's and the user's, reads them right. The sizes are measured, not assumed: once
    /// IL2CPP lays them out alike, nothing changes. Only the netstandard2.1 build, the one IL2CPP runs, reads
    /// <see cref="Oversized"/>, and only on IL2CPP.
    /// </summary>
    internal static class NinoElementLayouts<T>
    {
        /// <summary>
        /// On IL2CPP, the run-time element size where it is smaller than <c>Unsafe.SizeOf&lt;T&gt;()</c> and the
        /// array's length can be set; 0 otherwise.
        /// </summary>
        private static readonly int RuntimeSize = Measure();

        /// <summary>Whether arrays of <typeparamref name="T"/> are allocated with spare elements (<see cref="New"/>).</summary>
        public static readonly bool Oversized = RuntimeSize != 0;

        private static int Measure()
        {
            if (!NinoReflection.IsIl2Cpp || RuntimeHelpers.IsReferenceOrContainsReferences<T>() ||
                NinoReflection.SplitLayoutIn(typeof(T), null) == null)
                return 0;
            try
            {
                var array = new T[0x5A5];
                long runtime = ElementDistance(array);
                // The length must be where IL2CPP keeps it, after the bounds (null for a one-dimensional array).
                if (runtime <= 0 || runtime >= Unsafe.SizeOf<T>() ||
                    ObjectFields.At<IntPtr>(array, 0) != IntPtr.Zero ||
                    ObjectFields.At<UIntPtr>(array, IntPtr.Size) != (UIntPtr)0x5A5)
                    return 0;
                return (int)runtime;
            }
            catch
            {
                return 0;
            }
        }

        /// <summary>The distance between two elements as the runtime lays the array out (IL2CPP: <c>il2cpp_array_element_size</c>).</summary>
        private static long ElementDistance(T[] array)
        {
            var handle = GCHandle.Alloc(array, GCHandleType.Pinned);
            try
            {
                return (long)Marshal.UnsafeAddrOfPinnedArrayElement(array, 1) -
                       (long)Marshal.UnsafeAddrOfPinnedArrayElement(array, 0);
            }
            finally
            {
                handle.Free();
            }
        }

        /// <summary>
        /// A zeroed array of <paramref name="length"/> (above 0) elements whose memory holds as many compiled
        /// elements: allocated with more run-time elements, then its length set back. Out of line, as it is rare.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static T[] New(int length)
        {
            long bytes = (long)length * Unsafe.SizeOf<T>();
            var array = new T[checked((int)((bytes + RuntimeSize - 1) / RuntimeSize))];
            ObjectFields.At<UIntPtr>(array, IntPtr.Size) = (UIntPtr)length;
            return array;
        }
    }
}
