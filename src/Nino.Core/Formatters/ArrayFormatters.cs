using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Nino.Core
{
    /// <summary>Shared element loops for array-like formatters.</summary>
    public static class NinoElements
    {
        /// <summary>Writes <paramref name="count"/> elements starting at <paramref name="first"/>.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Write<T, TFormatter>(ref T first, int count, ref Writer writer)
            where TFormatter : struct, INinoFormatter<T>
        {
            for (int i = 0; i < count; i++)
                default(TFormatter).Serialize(Unsafe.Add(ref first, i), ref writer);
        }

        /// <summary>Reads <paramref name="count"/> elements into the slots starting at <paramref name="first"/>.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Read<T, TFormatter>(ref T first, int count, ref Reader reader)
            where TFormatter : struct, INinoFormatter<T>
        {
            for (int i = 0; i < count; i++)
                default(TFormatter).Deserialize(ref Unsafe.Add(ref first, i), ref reader);
        }

        /// <summary>Writes the elements of a span.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Write<T, TFormatter>(ReadOnlySpan<T> values, ref Writer writer)
            where TFormatter : struct, INinoFormatter<T>
        {
            foreach (var item in values)
                default(TFormatter).Serialize(item, ref writer);
        }

        /// <summary>Reads into the elements of a span, reusing the existing values.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Read<T, TFormatter>(Span<T> values, ref Reader reader)
            where TFormatter : struct, INinoFormatter<T>
        {
            for (int i = 0; i < values.Length; i++)
                default(TFormatter).Deserialize(ref values[i], ref reader);
        }

        /// <summary>
        /// Replaces <paramref name="array"/> with an array of <paramref name="length"/> elements, moving over the
        /// elements that fit and recycling the rest (and the old array).
        /// </summary>
        public static T[] Resize<T>(ref T[] array, int length, bool uninitialized = false)
        {
            var old = array;
            var result = length == 0
                ? Array.Empty<T>()
                : uninitialized
                    ? NinoUnsafe.AllocateUninitializedArray<T>(length)
                    : new T[length];
            if (old != null)
            {
                int keep = Math.Min(old.Length, length);
                if (!uninitialized)
                    Array.Copy(old, result, keep);
                if (old.Length > length)
                    NinoRecycler.RecycleRange(old.AsSpan(length));
                if (old.Length != 0)
                {
                    if (NinoUnsafe.IsReferenceOrContainsReferences<T>())
                        Array.Clear(old, 0, old.Length);
                    NinoRecycler<T[]>.Recycle(old);
                }
            }

            array = result;
            return result;
        }
    }

    /// <summary><c>T[]</c>: length (-1 = null), then the elements. Reuses an existing array of the same length.</summary>
    public struct ArrayFormatter<T, TFormatter> : INinoFormatter<T[]> where TFormatter : struct, INinoFormatter<T>
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Serialize(T[] value, ref Writer writer)
        {
            if (value == null)
            {
                writer.WriteNullLength();
                return;
            }

            writer.WriteLength(value.Length);
            NinoElements.Write<T, TFormatter>(value, ref writer);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Deserialize(ref T[] value, ref Reader reader)
        {
            int length = reader.ReadLength();
            if (length < 0)
            {
                NinoRecycler.Discard(ref value);
                return;
            }

            var array = value;
            if (array == null || array.Length != length)
                array = NinoElements.Resize(ref value, length);
            NinoElements.Read<T, TFormatter>(array, ref reader);
        }
    }

    /// <summary>Unmanaged <c>T[]</c>: length (-1 = null), then one block copy.</summary>
    public struct UnmanagedArrayFormatter<T> : INinoFormatter<T[]>, INinoRawArrayFormatter where T : unmanaged
    {
        // Elements with a non-canonical layout take the formatter path, which converts them.
        int INinoRawArrayFormatter.ElementSize => NinoLayout<T>.IsCanonical ? Unsafe.SizeOf<T>() : 0;

        INinoRawArrayFormatter INinoRawArrayFormatter.Padded => NinoLayout<T>.HasPadding ? this : null;

        void INinoRawArrayFormatter.CopyToWire(ref byte destination, ref byte source, int count) =>
            NinoLayout<T>.CopyToWire(ref destination, ref source, count);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Serialize(T[] value, ref Writer writer) => writer.Write(value);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Deserialize(ref T[] value, ref Reader reader)
        {
            int length = reader.ReadLength(NinoLayout<T>.Size);
            if (length < 0)
            {
                NinoRecycler.Discard(ref value);
                return;
            }

            var array = value;
            if (array == null || array.Length != length)
                array = NinoElements.Resize(ref value, length, uninitialized: true);
            reader.ReadUnmanagedRaw(array.AsSpan());
        }
    }

    /// <summary>
    /// <c>T?[]</c> of raw <typeparamref name="T"/>: the same bytes as <see cref="ArrayFormatter{T,TFormatter}"/> with
    /// <see cref="NullableFormatter{T,TFormatter}"/> (length, then a has-value byte and the value per element),
    /// written into one reservation and read without per-element calls.
    /// </summary>
    public struct NullableArrayFormatter<T> : INinoFormatter<T?[]> where T : unmanaged
    {
        public void Serialize(T?[] value, ref Writer writer)
        {
            if (value == null)
            {
                writer.WriteNullLength();
                return;
            }

            NinoNullables<T>.Write(value, ref writer);
        }

        public void Deserialize(ref T?[] value, ref Reader reader)
        {
            int length = NinoNullables<T>.ReadCount(ref reader);
            if (length < 0)
            {
                NinoRecycler.Discard(ref value);
                return;
            }

            var array = value;
            if (array == null || array.Length != length)
                array = NinoElements.Resize(ref value, length, uninitialized: true);
            NinoNullables<T>.Read(array, ref reader);
        }
    }

    /// <summary>
    /// <c>T?</c> sequences with unmanaged <c>T</c>, in the canonical (64-bit corefx) <c>Nullable&lt;T&gt;</c> layout:
    /// a has-value byte, zero padding up to T's alignment, then T in its canonical layout (zero when null).
    /// Where the runtime lays <c>T?</c> out the same way the elements are copied as one block (padding cleared);
    /// elsewhere (old Mono puts the value first, x86 aligns 8-byte values to 4) they are converted one by one.
    /// </summary>
    internal static class NinoNullables<T> where T : unmanaged
    {
        private static readonly int Offset = NinoLayout<T>.Info.Align > 0 ? NinoLayout<T>.Info.Align : 1;

        /// <summary>Wire size of one element.</summary>
        public static readonly int Stride = Offset + NinoLayout<T>.WireSize;

        /// <summary>Whether <c>T?</c> memory equals its wire layout on this runtime.</summary>
        private static readonly bool Raw = NinoLayout<T>.IsCanonical && NullableLayout<T>.HasValueOffset == 0 &&
                                           NullableLayout<T>.ValueOffset == Offset &&
                                           Unsafe.SizeOf<T?>() == Stride;

        private static readonly NinoMask? Mask = BuildMask();

        private static NinoMask? BuildMask()
        {
            var mask = new byte[Stride];
            mask[0] = 0xFF;
            var value = NinoLayout<T>.Info.Mask;
            for (int i = 0; i < NinoLayout<T>.WireSize; i++)
                mask[Offset + i] = value == null ? (byte)0xFF : value[i];
            return NinoMask.Create(mask);
        }

        /// <summary>Writes the count and the elements.</summary>
        public static void Write(ReadOnlySpan<T?> values, ref Writer writer)
        {
            ref byte dst = ref writer.Reserve(checked(4 + values.Length * Stride));
            NinoUnsafe.Write(ref dst, values.Length);
            dst = ref Unsafe.Add(ref dst, 4);
            if (values.IsEmpty)
                return;
            ref T? item = ref MemoryMarshal.GetReference(values);
            if (Raw)
            {
                ref byte src = ref Unsafe.As<T?, byte>(ref item);
                if (Mask != null)
                    Mask.Copy(ref dst, ref src, values.Length);
                else
                    Unsafe.CopyBlockUnaligned(ref dst, ref src, (uint)(values.Length * Stride));
                return;
            }

            Unsafe.InitBlockUnaligned(ref dst, 0, (uint)(values.Length * Stride));
            for (int i = 0; i < values.Length; i++)
            {
                ref T? element = ref Unsafe.Add(ref item, i);
                if (!element.HasValue)
                    continue;
                ref byte slot = ref Unsafe.Add(ref dst, i * Stride);
                slot = 1;
                NinoLayout<T>.Write(ref Unsafe.Add(ref slot, Offset), element.GetValueOrDefault());
            }
        }

        /// <summary>Reads the element count, validated against the remaining bytes (-1 for null).</summary>
        public static int ReadCount(ref Reader reader) => reader.ReadLength(Stride);

        /// <summary>Reads <c>destination.Length</c> elements (the count has been read).</summary>
        public static void Read(Span<T?> destination, ref Reader reader)
        {
            ref byte src = ref reader.ReadBlock(checked(destination.Length * Stride));
            if (destination.IsEmpty)
                return;
            ref T? item = ref MemoryMarshal.GetReference(destination);
            if (Raw)
            {
                Unsafe.CopyBlockUnaligned(ref Unsafe.As<T?, byte>(ref item), ref src,
                    (uint)(destination.Length * Stride));
                return;
            }

            for (int i = 0; i < destination.Length; i++)
            {
                ref byte slot = ref Unsafe.Add(ref src, i * Stride);
                Unsafe.Add(ref item, i) = slot != 0 ? NinoLayout<T>.Read(ref Unsafe.Add(ref slot, Offset)) : default(T?);
            }
        }
    }

    /// <summary>
    /// Field offsets of <c>Nullable&lt;T&gt;</c> in memory on the running runtime. Old Mono stores the value first
    /// while corefx stores the flag first, so the layout is probed rather than assumed.
    /// </summary>
    internal static class NullableLayout<T> where T : unmanaged
    {
        public static readonly int HasValueOffset = ProbeHasValue();

        public static readonly int ValueOffset =
            HasValueOffset == 0 ? Unsafe.SizeOf<T?>() - Unsafe.SizeOf<T>() : 0;

        private static int ProbeHasValue()
        {
            T? probe = default(T); // has value, zero value and padding
            ref byte b = ref Unsafe.As<T?, byte>(ref probe);
            for (int i = 0; i < Unsafe.SizeOf<T?>(); i++)
                if (Unsafe.Add(ref b, i) != 0)
                    return i;
            return 0;
        }
    }

    /// <summary><c>ArraySegment&lt;T&gt;</c>: written like an array; read into a new array.</summary>
    public struct ArraySegmentFormatter<T, TFormatter> : INinoFormatter<ArraySegment<T>>
        where TFormatter : struct, INinoFormatter<T>
    {
        public void Serialize(ArraySegment<T> value, ref Writer writer)
        {
            if (value.Array == null)
            {
                writer.WriteNullLength();
                return;
            }

            writer.WriteLength(value.Count);
            NinoElements.Write<T, TFormatter>(value.AsSpan(), ref writer);
        }

        public void Deserialize(ref ArraySegment<T> value, ref Reader reader)
        {
            T[] array = null;
            default(ArrayFormatter<T, TFormatter>).Deserialize(ref array, ref reader);
            value = array == null ? default : new ArraySegment<T>(array);
        }
    }

    /// <summary>
    /// <c>T[,]</c>: both lengths (first is -1 for null), then the elements in row-major order.
    /// </summary>
    public struct Array2DFormatter<T, TFormatter> : INinoFormatter<T[,]> where TFormatter : struct, INinoFormatter<T>
    {
        public void Serialize(T[,] value, ref Writer writer)
        {
            if (value == null)
            {
                writer.WriteNullLength();
                return;
            }

            int a = value.GetLength(0), b = value.GetLength(1);
            ref byte p = ref writer.Reserve(8);
            NinoUnsafe.Write(ref p, 0, a);
            NinoUnsafe.Write(ref p, 4, b);
            if (value.Length != 0)
                MultiDim.Write<T, TFormatter>(ref value[0, 0], value.Length, ref writer);
        }

        public void Deserialize(ref T[,] value, ref Reader reader)
        {
            int a = reader.ReadLength();
            if (a < 0)
            {
                NinoRecycler.Discard(ref value);
                return;
            }

            int b = reader.ReadLength();
            int total = MultiDim.CheckedTotal(a, b, 1, 1, ref reader);
            var array = value;
            if (array == null || array.GetLength(0) != a || array.GetLength(1) != b)
            {
                MultiDim.Recycle(value);
                array = new T[a, b];
                value = array;
            }

            if (total != 0)
                MultiDim.Read<T, TFormatter>(ref array[0, 0], total, ref reader);
        }
    }

    /// <summary><c>T[,,]</c>: the three lengths (first is -1 for null), then the elements in row-major order.</summary>
    public struct Array3DFormatter<T, TFormatter> : INinoFormatter<T[,,]> where TFormatter : struct, INinoFormatter<T>
    {
        public void Serialize(T[,,] value, ref Writer writer)
        {
            if (value == null)
            {
                writer.WriteNullLength();
                return;
            }

            ref byte p = ref writer.Reserve(12);
            NinoUnsafe.Write(ref p, 0, value.GetLength(0));
            NinoUnsafe.Write(ref p, 4, value.GetLength(1));
            NinoUnsafe.Write(ref p, 8, value.GetLength(2));
            if (value.Length != 0)
                MultiDim.Write<T, TFormatter>(ref value[0, 0, 0], value.Length, ref writer);
        }

        public void Deserialize(ref T[,,] value, ref Reader reader)
        {
            int a = reader.ReadLength();
            if (a < 0)
            {
                NinoRecycler.Discard(ref value);
                return;
            }

            int b = reader.ReadLength();
            int c = reader.ReadLength();
            int total = MultiDim.CheckedTotal(a, b, c, 1, ref reader);
            var array = value;
            if (array == null || array.GetLength(0) != a || array.GetLength(1) != b || array.GetLength(2) != c)
            {
                MultiDim.Recycle(value);
                array = new T[a, b, c];
                value = array;
            }

            if (total != 0)
                MultiDim.Read<T, TFormatter>(ref array[0, 0, 0], total, ref reader);
        }
    }

    /// <summary><c>T[,,,]</c>: the four lengths (first is -1 for null), then the elements in row-major order.</summary>
    public struct Array4DFormatter<T, TFormatter> : INinoFormatter<T[,,,]> where TFormatter : struct, INinoFormatter<T>
    {
        public void Serialize(T[,,,] value, ref Writer writer)
        {
            if (value == null)
            {
                writer.WriteNullLength();
                return;
            }

            ref byte p = ref writer.Reserve(16);
            NinoUnsafe.Write(ref p, 0, value.GetLength(0));
            NinoUnsafe.Write(ref p, 4, value.GetLength(1));
            NinoUnsafe.Write(ref p, 8, value.GetLength(2));
            NinoUnsafe.Write(ref p, 12, value.GetLength(3));
            if (value.Length != 0)
                MultiDim.Write<T, TFormatter>(ref value[0, 0, 0, 0], value.Length, ref writer);
        }

        public void Deserialize(ref T[,,,] value, ref Reader reader)
        {
            int a = reader.ReadLength();
            if (a < 0)
            {
                NinoRecycler.Discard(ref value);
                return;
            }

            int b = reader.ReadLength();
            int c = reader.ReadLength();
            int d = reader.ReadLength();
            int total = MultiDim.CheckedTotal(a, b, c, d, ref reader);
            var array = value;
            if (array == null || array.GetLength(0) != a || array.GetLength(1) != b || array.GetLength(2) != c ||
                array.GetLength(3) != d)
            {
                MultiDim.Recycle(value);
                array = new T[a, b, c, d];
                value = array;
            }

            if (total != 0)
                MultiDim.Read<T, TFormatter>(ref array[0, 0, 0, 0], total, ref reader);
        }
    }

    internal static class MultiDim
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Write<T, TFormatter>(ref T first, int count, ref Writer writer)
            where TFormatter : struct, INinoFormatter<T>
        {
            // Raw elements (unmanaged values and raw [NinoType] structs) are copied as one block.
            if (RawFormatter<TFormatter>.Is)
            {
                int size = checked(count * Unsafe.SizeOf<T>());
                NinoLayout<T>.CopyToWire(ref writer.Reserve(size), ref Unsafe.As<T, byte>(ref first), count);
                return;
            }

            NinoElements.Write<T, TFormatter>(ref first, count, ref writer);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Read<T, TFormatter>(ref T first, int count, ref Reader reader)
            where TFormatter : struct, INinoFormatter<T>
        {
            // Raw elements (unmanaged values and raw [NinoType] structs) are copied as one block.
            if (RawFormatter<TFormatter>.Is)
            {
                int size = checked(count * Unsafe.SizeOf<T>());
                Unsafe.CopyBlockUnaligned(ref Unsafe.As<T, byte>(ref first), ref reader.ReadBlock(size), (uint)size);
                return;
            }

            NinoElements.Read<T, TFormatter>(ref first, count, ref reader);
        }

        public static int CheckedTotal(int a, int b, int c, int d, ref Reader reader)
        {
            if (b < 0 || c < 0 || d < 0)
                NinoThrow.InvalidLength(-1);
            long total = (long)a * b * c * d;
            if (total > reader.Remaining)
                NinoThrow.InvalidLength((int)Math.Min(total, int.MaxValue));
            return (int)total;
        }

        public static void Recycle<TArray>(TArray array) where TArray : class
        {
            if (array != null)
                NinoRecycler<TArray>.Recycle(array);
        }
    }
}
