using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
#if NET8_0_OR_GREATER
using System.Runtime.Intrinsics;
#endif

namespace Nino.Core
{
    /// <summary>
    /// Low-level unaligned memory access used by <see cref="Writer"/>, <see cref="Reader"/> and generated code.
    /// All values are stored little-endian, exactly as laid out in memory.
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static class NinoUnsafe
    {
        /// <summary>The most elements an array (of bytes, or a dimension of any array) can have.</summary>
        internal const int MaxArrayLength = 0x7FFFFFC7;

#if NET5_0_OR_GREATER
        /// <summary>
        /// Smallest array (in bytes) allocated uninitialized: the 2 KB below which GC.AllocateUninitializedArray zeroes
        /// anyway. Measured with BenchmarkDotNet (.NET 10, x64, server GC) reading a ReadOnlyCollection of 1,000 ints
        /// (4 KB): 570 to 590 ns with the array zeroed, 450 to 510 uninitialized (MemoryPack, which allocates it
        /// uninitialized: 450 to 570). A loop that allocated and filled arrays had zeroing ahead up to 6 KB, but not
        /// the benchmarks.
        /// </summary>
        private const int MinUninitializedArrayBytes = 2 * 1024;
#endif

        /// <summary>
        /// Whether the process is 64-bit. Constant-folded by the JIT.
        /// </summary>
        public static bool Is64Bit
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => IntPtr.Size == 8;
        }

        /// <summary>Size of <typeparamref name="T"/> in bytes. Constant-folded by the JIT.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int SizeOf<T>() => Unsafe.SizeOf<T>();

        /// <summary>
        /// Stores <paramref name="value"/> at <paramref name="destination"/> without alignment requirements.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Write<T>(ref byte destination, T value)
        {
            // 32-bit ARM faults on unaligned 8-byte (LDRD/STRD/VSTR) accesses, which some runtimes emit for
            // unaligned stores of 8+ byte values. A byte-wise block copy is always safe there. It takes the value's
            // address, so it is in another method: an address taken here, even on a branch the JIT folds away,
            // would make every inlined caller pass the value through a stack temporary.
            if (Is64Bit || Unsafe.SizeOf<T>() <= 4)
                Unsafe.WriteUnaligned(ref destination, value);
            else
                WriteBytes(ref destination, value);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void WriteBytes<T>(ref byte destination, T value) => WriteIn(ref destination, in value);

        /// <summary>
        /// Stores <paramref name="value"/> at <paramref name="destination"/> + <paramref name="offset"/>.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Write<T>(ref byte destination, int offset, T value)
        {
            // The offset is added here, in each access, rather than by passing Unsafe.Add(...) on to Write(ref, T):
            // a computed reference handed to another inlined method becomes a temporary when the offset is not an
            // IL constant (offset + NinoUnsafe.SizeOf<S>()), and the store then no longer folds the offset into its
            // address (an extra lea/add per member). Same in WriteIn, WriteValue(In) and Read below.
            if (Is64Bit || Unsafe.SizeOf<T>() <= 4)
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref destination, offset), value);
            else
                WriteBytes(ref Unsafe.Add(ref destination, offset), value);
        }

        /// <summary>
        /// Stores <paramref name="value"/> at <paramref name="destination"/> + <paramref name="offset"/> with its
        /// padding bytes cleared (see <see cref="NinoLayout{T}"/>).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteValue<T>(ref byte destination, int offset, T value)
        {
            Write(ref destination, offset, value);
            NinoLayout<T>.ClearPadding(ref Unsafe.Add(ref destination, offset));
        }

        /// <summary>
        /// <see cref="Write{T}(ref byte, T)"/> from a reference: for values wider than a register that live in the
        /// heap (fields of the object being written), which by value would be copied to the stack and loaded
        /// again (a Guid: a 16-byte store and reload). Values of at most 8 bytes are better passed by value, where
        /// the load folds the field offset.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteIn<T>(ref byte destination, in T value)
        {
            if (Is64Bit || Unsafe.SizeOf<T>() <= 4)
                Unsafe.WriteUnaligned(ref destination, value);
            else
                Unsafe.CopyBlockUnaligned(ref destination, ref Unsafe.As<T, byte>(ref Unsafe.AsRef(in value)),
                    (uint)Unsafe.SizeOf<T>());
        }

        /// <summary><see cref="WriteIn{T}(ref byte, in T)"/> at <paramref name="destination"/> + <paramref name="offset"/>.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteIn<T>(ref byte destination, int offset, in T value)
        {
            if (Is64Bit || Unsafe.SizeOf<T>() <= 4)
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref destination, offset), value);
            else
                Unsafe.CopyBlockUnaligned(ref Unsafe.Add(ref destination, offset),
                    ref Unsafe.As<T, byte>(ref Unsafe.AsRef(in value)), (uint)Unsafe.SizeOf<T>());
        }

        /// <summary><see cref="WriteValue{T}"/> from a reference (see <see cref="WriteIn{T}(ref byte, in T)"/>).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteValueIn<T>(ref byte destination, int offset, in T value)
        {
            WriteIn(ref destination, offset, in value);
            NinoLayout<T>.ClearPadding(ref Unsafe.Add(ref destination, offset));
        }

        /// <summary>
        /// Loads a <typeparamref name="T"/> from <paramref name="source"/> without alignment requirements.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static T Read<T>(ref byte source) =>
            Is64Bit || Unsafe.SizeOf<T>() <= 4 ? Unsafe.ReadUnaligned<T>(ref source) : ReadBytes<T>(ref source);

        private static T ReadBytes<T>(ref byte source)
        {
            T value = default;
            Unsafe.CopyBlockUnaligned(ref Unsafe.As<T, byte>(ref value), ref source, (uint)Unsafe.SizeOf<T>());
            return value;
        }

        /// <summary>
        /// Loads a <typeparamref name="T"/> from <paramref name="source"/> + <paramref name="offset"/>.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static T Read<T>(ref byte source, int offset) =>
            Is64Bit || Unsafe.SizeOf<T>() <= 4
                ? Unsafe.ReadUnaligned<T>(ref Unsafe.Add(ref source, offset))
                : ReadBytes<T>(ref Unsafe.Add(ref source, offset));

        /// <summary><paramref name="source"/> + <paramref name="offset"/> bytes.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ref byte Add(ref byte source, int offset) => ref Unsafe.Add(ref source, offset);

        /// <summary>
        /// Returns a reference to the first element of <paramref name="array"/> without a bounds check.
        /// The array must not be null.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ref T GetArrayDataReference<T>(T[] array)
        {
#if NET5_0_OR_GREATER
            return ref MemoryMarshal.GetArrayDataReference(array);
#else
            return ref MemoryMarshal.GetReference(array.AsSpan());
#endif
        }

        /// <summary>
        /// Copies <paramref name="size"/> bytes. Up to 64 (most strings) inline, as overlapping loads and stores of the
        /// largest width that fits (33 to 64 bytes: two 32-byte vectors where they are accelerated, else four 16-byte
        /// moves), and 65 to 128 as four 32-byte vectors where they are accelerated: a block copy of a size that is not a
        /// constant calls Memmove. Beyond, up to 8 KB with 64-byte vectors where they are accelerated, else from 256
        /// bytes to 4 KB with 32-byte ones (<see cref="CopyVectors{TVector}"/>), and Memmove beyond. Measured on an
        /// AVX-512 Xeon (.NET 10, source and destination of different alignments), with AVX-512 on or off: 33 to 64
        /// bytes in 4.0 to 4.5 ns instead of 4.7 to 5.4 (Memmove), 65 to 128 in about 4.2 ns instead of 5.4 to 6.6. A
        /// loop of 16-byte vectors (Arm64, WebAssembly, SSE only) is slower than Memmove from 2 KB, so those CPUs call
        /// Memmove from 65 bytes.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Copy(ref byte destination, ref byte source, uint size)
        {
            if (Is64Bit && size <= 64)
            {
                if (size > 32)
                {
#if NET8_0_OR_GREATER
                    if (Vector256.IsHardwareAccelerated)
                    {
                        var head = Vector256.LoadUnsafe(ref source);
                        var tail = Vector256.LoadUnsafe(ref Unsafe.Add(ref source, (nint)size - 32));
                        head.StoreUnsafe(ref destination);
                        tail.StoreUnsafe(ref Unsafe.Add(ref destination, (nint)size - 32));
                        return;
                    }
#endif
                    var a = Unsafe.ReadUnaligned<Bytes16>(ref source);
                    var b = Unsafe.ReadUnaligned<Bytes16>(ref Unsafe.Add(ref source, 16));
                    var c = Unsafe.ReadUnaligned<Bytes16>(ref Unsafe.Add(ref source, (nint)size - 32));
                    var d = Unsafe.ReadUnaligned<Bytes16>(ref Unsafe.Add(ref source, (nint)size - 16));
                    Unsafe.WriteUnaligned(ref destination, a);
                    Unsafe.WriteUnaligned(ref Unsafe.Add(ref destination, 16), b);
                    Unsafe.WriteUnaligned(ref Unsafe.Add(ref destination, (nint)size - 32), c);
                    Unsafe.WriteUnaligned(ref Unsafe.Add(ref destination, (nint)size - 16), d);
                }
                else if (size >= 16)
                {
                    var head = Unsafe.ReadUnaligned<Bytes16>(ref source);
                    var tail = Unsafe.ReadUnaligned<Bytes16>(ref Unsafe.Add(ref source, (nint)size - 16));
                    Unsafe.WriteUnaligned(ref destination, head);
                    Unsafe.WriteUnaligned(ref Unsafe.Add(ref destination, (nint)size - 16), tail);
                }
                else if (size >= 8)
                {
                    ulong head = Unsafe.ReadUnaligned<ulong>(ref source);
                    ulong tail = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, (nint)size - 8));
                    Unsafe.WriteUnaligned(ref destination, head);
                    Unsafe.WriteUnaligned(ref Unsafe.Add(ref destination, (nint)size - 8), tail);
                }
                else if (size >= 4)
                {
                    uint head = Unsafe.ReadUnaligned<uint>(ref source);
                    uint tail = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref source, (nint)size - 4));
                    Unsafe.WriteUnaligned(ref destination, head);
                    Unsafe.WriteUnaligned(ref Unsafe.Add(ref destination, (nint)size - 4), tail);
                }
                else if (size != 0)
                {
                    // 1 to 3 bytes: the first, the middle and the last (some the same).
                    byte first = source, middle = Unsafe.Add(ref source, (nint)(size >> 1)),
                        last = Unsafe.Add(ref source, (nint)size - 1);
                    destination = first;
                    Unsafe.Add(ref destination, (nint)(size >> 1)) = middle;
                    Unsafe.Add(ref destination, (nint)size - 1) = last;
                }

                return;
            }

#if NET8_0_OR_GREATER
            if (Vector256.IsHardwareAccelerated)
            {
                if (size <= 128)
                {
                    // 65 to 128 bytes (32-bit processes, which skip the small sizes above, go to Memmove).
                    if (Is64Bit)
                    {
                        var a = Vector256.LoadUnsafe(ref source);
                        var b = Vector256.LoadUnsafe(ref Unsafe.Add(ref source, 32));
                        var c = Vector256.LoadUnsafe(ref Unsafe.Add(ref source, (nint)size - 64));
                        var d = Vector256.LoadUnsafe(ref Unsafe.Add(ref source, (nint)size - 32));
                        a.StoreUnsafe(ref destination);
                        b.StoreUnsafe(ref Unsafe.Add(ref destination, 32));
                        c.StoreUnsafe(ref Unsafe.Add(ref destination, (nint)size - 64));
                        d.StoreUnsafe(ref Unsafe.Add(ref destination, (nint)size - 32));
                        return;
                    }
                }
                else if (Vector512.IsHardwareAccelerated ? size <= MaxVectorCopy : size - 256 <= MaxVector256Copy - 256)
                {
                    if (Vector512.IsHardwareAccelerated)
                        CopyVectors<Vector512<byte>>(ref destination, ref source, size);
                    else
                        CopyVectors<Vector256<byte>>(ref destination, ref source, size);
                    return;
                }
            }
#endif
            Unsafe.CopyBlockUnaligned(ref destination, ref source, size);
        }

        /// <summary>
        /// <see cref="Copy"/> into memory that may have just been allocated (a read into a new array): Memmove from
        /// <see cref="MinUninitializedArrayBytes"/>, where a new array is uninitialized and vector stores read each of
        /// its lines from memory first (Memmove's rep movsb writes whole lines without reading them). Below, where a new
        /// array was zeroed into the cache, as <see cref="Copy"/>. Measured on an AVX-512 Xeon (.NET 10): 1,000 ints
        /// read into a new array in 470 to 710 ns with Memmove, 760 to 830 with vector stores; into an existing
        /// array of 250, 14 ns instead of 23 with vector stores. Out of line: a size that is not a constant takes every
        /// branch of Copy, and inlined into each raw list and array read that code slowed the reads around it (a list
        /// of 8 classes holding a List&lt;int&gt; each, read in place: 680 to 710 ns inline, 570 to 610 out of line).
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void CopyToNew(ref byte destination, ref byte source, uint size)
        {
#if NET5_0_OR_GREATER
            if (size >= MinUninitializedArrayBytes)
            {
                Unsafe.CopyBlockUnaligned(ref destination, ref source, size);
                return;
            }
#endif
            Copy(ref destination, ref source, size);
        }

#if NET8_0_OR_GREATER
        /// <summary>
        /// Largest copy done with 64-byte vectors by <see cref="CopyVectors{TVector}"/>. Memmove calls the C library's
        /// from 2 KB, which handles a source and destination of different alignments better: from about 8 KB it is as
        /// fast or faster.
        /// </summary>
        private const uint MaxVectorCopy = 8 * 1024;

        /// <summary>
        /// The range copied with 32-byte vectors where 64-byte ones are not accelerated (AVX2 without AVX-512, most
        /// desktop CPUs). Measured on a Xeon with AVX-512 turned off (.NET 10, source and destination of different
        /// alignments) against Memmove: 256 bytes 3.9 ns instead of 6.3, 1,200 bytes 12.4 instead of 15.7, 4,000 bytes
        /// 38.9 instead of 44.8; on par from 129 to 255 bytes, and slower at 8 KB.
        /// </summary>
        private const uint MaxVector256Copy = 4 * 1024;

        /// <summary>
        /// Copies at least one vector (<typeparamref name="TVector"/>, a <c>Vector256</c> or <c>Vector512</c> of bytes)
        /// with vector loads and stores: the first one unaligned, then four at a time with aligned stores (an unaligned
        /// store straddles two cache lines), and the last one unaligned, overlapping. Measured on an AVX-512 Xeon
        /// against Memmove (.NET 10), source and destination of different alignments, with 64-byte vectors: 256 bytes
        /// 5.9 ns instead of 7.9 ns, 1,200 bytes 11.9 ns instead of 18.6 ns, 4,000 bytes 34 ns instead of 48 ns.
        /// </summary>
        // Fully optimized from the first call (see NinoMask.CopyVectors512).
        [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
        private static unsafe void CopyVectors<TVector>(ref byte destination, ref byte source, uint size)
            where TVector : struct
        {
            nuint width = (nuint)Unsafe.SizeOf<TVector>();
            Unsafe.WriteUnaligned(ref destination, Unsafe.ReadUnaligned<TVector>(ref source));
            nuint i = width - ((nuint)Unsafe.AsPointer(ref destination) & (width - 1));
            nuint end = size - width;
            for (; i + 3 * width <= end; i += 4 * width)
            {
                var a = Unsafe.ReadUnaligned<TVector>(ref Unsafe.Add(ref source, i));
                var b = Unsafe.ReadUnaligned<TVector>(ref Unsafe.Add(ref source, i + width));
                var c = Unsafe.ReadUnaligned<TVector>(ref Unsafe.Add(ref source, i + 2 * width));
                var d = Unsafe.ReadUnaligned<TVector>(ref Unsafe.Add(ref source, i + 3 * width));
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref destination, i), a);
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref destination, i + width), b);
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref destination, i + 2 * width), c);
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref destination, i + 3 * width), d);
            }

            for (; i <= end; i += width)
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref destination, i),
                    Unsafe.ReadUnaligned<TVector>(ref Unsafe.Add(ref source, i)));
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref destination, end),
                Unsafe.ReadUnaligned<TVector>(ref Unsafe.Add(ref source, end)));
        }
#endif

        /// <summary>16 bytes, moved with one load and one store.</summary>
        private struct Bytes16
        {
#pragma warning disable CS0649
            public ulong A, B;
#pragma warning restore CS0649
        }

        /// <summary>
        /// Allocates an array whose contents are not zeroed when the runtime supports it and it is large enough for
        /// that to be cheaper (<see cref="NewArray{T}"/>). Only use when every element is overwritten before being read.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static T[] AllocateUninitializedArray<T>(int length) => NewArray<T>(length, uninitialized: true);

        /// <summary>
        /// A new array of <paramref name="length"/> (not negative) elements, the shared empty array for 0, and
        /// allocated uninitialized from 2 KB (MinUninitializedArrayBytes) on .NET 5 and later when
        /// <paramref name="uninitialized"/> (the caller overwrites every element). One unsigned compare picks the
        /// common case, a zeroed array of 1 element or more. The others are inline too: a call in between would cost
        /// a frame on the large arrays (where an uninitialized one is called for).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static T[] NewArray<T>(int length, bool uninitialized)
        {
            if ((uint)(length - 1) < (uint)MaxZeroedLength<T>(uninitialized))
                return new T[length];
            if (length == 0)
                return Array.Empty<T>();
#if NET5_0_OR_GREATER
            // At least MinUninitializedArrayBytes (MaxZeroedLength): no size test of its own.
            if (uninitialized)
                return GC.AllocateUninitializedArray<T>(length);
#endif
            return new T[length];
        }

        /// <summary>
        /// The longest array of <typeparamref name="T"/> that <see cref="NewArray{T}"/> allocates zeroed (when
        /// <paramref name="uninitialized"/>; otherwise any length), as <c>new T[length]</c> does. A JIT-time constant.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int MaxZeroedLength<T>(bool uninitialized)
        {
#if NET5_0_OR_GREATER
            // length * size < MinUninitializedArrayBytes.
            if (uninitialized)
                return (MinUninitializedArrayBytes - 1) / Unsafe.SizeOf<T>();
#endif
            return int.MaxValue;
        }

        /// <summary>
        /// Whether <typeparamref name="T"/> is a reference type or contains references (i.e. is not unmanaged).
        /// Constant-folded by the JIT on .NET Core and .NET Standard 2.1.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsReferenceOrContainsReferences<T>()
        {
#if NETSTANDARD2_0
            return TypeInfo<T>.IsReferenceOrContainsReferences;
#else
            return RuntimeHelpers.IsReferenceOrContainsReferences<T>();
#endif
        }

#if NETSTANDARD2_0
        private static class TypeInfo<T>
        {
            public static readonly bool IsReferenceOrContainsReferences = !IsUnmanaged(typeof(T));
        }
#endif

        /// <summary>Whether <paramref name="type"/> is an unmanaged type (no references anywhere in its layout).</summary>
        // Reached from the runtime resolver (dynamic code only) and the netstandard2.0 build.
        [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2070",
            Justification = NinoLayout.FieldsJustification)]
        public static bool IsUnmanaged(Type type)
        {
            if (type.IsPrimitive || type.IsEnum || type.IsPointer)
                return true;
            if (!type.IsValueType || type.IsGenericTypeDefinition)
                return false;

            foreach (var field in type.GetFields(System.Reflection.BindingFlags.Instance |
                                                 System.Reflection.BindingFlags.Public |
                                                 System.Reflection.BindingFlags.NonPublic))
            {
                if (!IsUnmanaged(field.FieldType))
                    return false;
            }

            return true;
        }
    }
}
