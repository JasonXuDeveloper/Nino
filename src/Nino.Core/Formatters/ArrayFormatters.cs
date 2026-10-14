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
        int INinoRawArrayFormatter.ElementSize => Unsafe.SizeOf<T>();

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Serialize(T[] value, ref Writer writer) => writer.Write(value);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Deserialize(ref T[] value, ref Reader reader)
        {
            int length = reader.ReadLength(Unsafe.SizeOf<T>());
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
                Unsafe.CopyBlockUnaligned(ref writer.Reserve(size), ref Unsafe.As<T, byte>(ref first), (uint)size);
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
