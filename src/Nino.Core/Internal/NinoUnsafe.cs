using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Nino.Core
{
    /// <summary>
    /// Low-level unaligned memory access used by <see cref="Writer"/>, <see cref="Reader"/> and generated code.
    /// All values are stored little-endian, exactly as laid out in memory.
    /// </summary>
    public static class NinoUnsafe
    {

#if NET5_0_OR_GREATER
        /// <summary>
        /// Smallest array (in bytes) worth allocating uninitialized. Every caller fills the array right away, and
        /// filling zeroed memory is cheaper while it is small: zeroing pulls the lines into the cache, which the copy
        /// then hits. Measured (.NET 8, x64) allocating and copying into the array: 2 KB 198 ns zeroed vs 393 ns
        /// uninitialized, 8 KB 806 vs 825, 16 KB 1248 vs 1169, 64 KB 5.6 vs 3.9 µs.
        /// </summary>
        private const int MinUninitializedArrayBytes = 16 * 1024;
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
            Write(ref Unsafe.Add(ref destination, offset), value);
        }

        /// <summary>
        /// Stores <paramref name="value"/> at <paramref name="destination"/> + <paramref name="offset"/> with its
        /// padding bytes cleared (see <see cref="NinoLayout{T}"/>).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteValue<T>(ref byte destination, int offset, T value)
        {
            ref byte dst = ref Unsafe.Add(ref destination, offset);
            Write(ref dst, value);
            NinoLayout<T>.ClearPadding(ref dst);
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
            WriteIn(ref Unsafe.Add(ref destination, offset), in value);
        }

        /// <summary><see cref="WriteValue{T}"/> from a reference (see <see cref="WriteIn{T}(ref byte, in T)"/>).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteValueIn<T>(ref byte destination, int offset, in T value)
        {
            ref byte dst = ref Unsafe.Add(ref destination, offset);
            WriteIn(ref dst, in value);
            NinoLayout<T>.ClearPadding(ref dst);
        }

        /// <summary>
        /// Loads a <typeparamref name="T"/> from <paramref name="source"/> without alignment requirements.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static T Read<T>(ref byte source)
        {
            if (Is64Bit || Unsafe.SizeOf<T>() <= 4)
                return Unsafe.ReadUnaligned<T>(ref source);

            T value = default;
            Unsafe.CopyBlockUnaligned(ref Unsafe.As<T, byte>(ref value), ref source, (uint)Unsafe.SizeOf<T>());
            return value;
        }

        /// <summary>
        /// Loads a <typeparamref name="T"/> from <paramref name="source"/> + <paramref name="offset"/>.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static T Read<T>(ref byte source, int offset)
        {
            return Read<T>(ref Unsafe.Add(ref source, offset));
        }

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
        /// Allocates an array whose contents are not zeroed when the runtime supports it.
        /// Only use when every element is overwritten before being read.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static T[] AllocateUninitializedArray<T>(int length)
        {
#if NET5_0_OR_GREATER
            if (Unsafe.SizeOf<T>() * length >= MinUninitializedArrayBytes)
                return GC.AllocateUninitializedArray<T>(length);
#endif
            return new T[length];
        }

        /// <summary>
        /// A new array of <paramref name="length"/> (not negative) elements, the shared empty array for 0, and
        /// allocated uninitialized where that is cheaper (<see cref="AllocateUninitializedArray{T}"/>) when
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
            // At least MinUninitializedArrayBytes: the size test of AllocateUninitializedArray is already made.
            if (uninitialized)
                return GC.AllocateUninitializedArray<T>(length);
#endif
            return new T[length];
        }

        /// <summary>
        /// The longest array of <typeparamref name="T"/> that <see cref="AllocateUninitializedArray{T}"/> allocates
        /// zeroed (when <paramref name="uninitialized"/>; otherwise any length), as <c>new T[length]</c> does. A
        /// JIT-time constant.
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
