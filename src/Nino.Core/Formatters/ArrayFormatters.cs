using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Nino.Core
{
    /// <summary>Shared element loops for array-like formatters.</summary>
    internal static class NinoElements
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
            // Elements of a fixed size (BlockSize): one capacity check for the block, none per element. Raw elements
            // too, as the unmanaged formatters write them: an interface, Stack or Queue of them, or any collection
            // whose formatter is not specialized for them, would otherwise check the capacity per element.
            int block = BlockSize<T, TFormatter>();
            if (block > 0)
            {
                if (!values.IsEmpty)
                    WriteBlock<T, TFormatter>(ref writer.Reserve(checked(values.Length * block)), values);
                return;
            }

            foreach (var item in values)
                default(TFormatter).Serialize(item, ref writer);
        }

        /// <summary>
        /// The wire size of each element when a span of them is written as one block, else -1: raw elements
        /// (unmanaged values and raw [NinoType] structs), DateTimeOffsets and tuples of raw items. A JIT-time constant.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int BlockSize<T, TFormatter>() where TFormatter : struct, INinoFormatter<T> =>
            RawFormatter.Is<T, TFormatter>() ? Unsafe.SizeOf<T>()
            : typeof(TFormatter) == typeof(DateTimeOffsetFormatter) ? NinoWire.DateTimeOffsetSize
            : NinoBlock.Size<T, TFormatter>();

        /// <summary>Writes <paramref name="values"/> (not empty) as one block of <see cref="BlockSize"/> bytes each.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteBlock<T, TFormatter>(ref byte destination, ReadOnlySpan<T> values)
            where TFormatter : struct, INinoFormatter<T>
        {
            if (RawFormatter.Is<T, TFormatter>())
                NinoLayout<T>.CopyToWire(ref destination,
                    ref Unsafe.As<T, byte>(ref MemoryMarshal.GetReference(values)), values.Length);
            else if (typeof(TFormatter) == typeof(DateTimeOffsetFormatter))
                DateTimeOffsetFormatter.WriteAll(ref destination,
                    ref Unsafe.As<T, DateTimeOffset>(ref MemoryMarshal.GetReference(values)), values.Length);
            else
                NinoBlock.WriteAll<T, TFormatter>(ref destination, ref MemoryMarshal.GetReference(values),
                    values.Length, NinoBlock.Size<T, TFormatter>());
        }

        /// <summary>
        /// The bytes of a collection of <paramref name="count"/> elements (-1: null) written as its length and one
        /// block (<see cref="INinoSizedFormatter{T}"/>), or -1 when its elements are not, or it is too large for an
        /// array (the regular path reports it).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int SizedBytes<T, TFormatter>(int count) where TFormatter : struct, INinoFormatter<T>
        {
            int block = BlockSize<T, TFormatter>();
            if (block <= 0)
                return -1;
            long size = NinoWire.LengthSize + (long)Math.Max(count, 0) * block;
            return size <= int.MaxValue ? (int)size : -1;
        }

        /// <summary>
        /// Writes a collection's length (the null length when <paramref name="isNull"/>) and its elements as one block
        /// into the <see cref="SizedBytes"/> at <paramref name="destination"/>.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteSized<T, TFormatter>(ref byte destination, ReadOnlySpan<T> values, bool isNull)
            where TFormatter : struct, INinoFormatter<T>
        {
            NinoUnsafe.Write(ref destination, isNull ? NinoWire.NullLength : values.Length);
            if (!values.IsEmpty)
                WriteBlock<T, TFormatter>(ref Unsafe.Add(ref destination, NinoWire.LengthSize), values);
        }

        /// <summary>Reads into the elements of a span, reusing the existing values.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Read<T, TFormatter>(Span<T> values, ref Reader reader)
            where TFormatter : struct, INinoFormatter<T>
        {
            if (typeof(TFormatter) == typeof(StringFormatter))
            {
                // One loop with the position in a register (see Reader.ReadStrings).
                reader.ReadStrings(ref Unsafe.As<T, string>(ref MemoryMarshal.GetReference(values)), values.Length);
                return;
            }

            // Raw elements: one bounds check and one block copy (see Write).
            if (RawFormatter.Is<T, TFormatter>())
            {
                int size = checked(values.Length * Unsafe.SizeOf<T>());
                Unsafe.CopyBlockUnaligned(ref Unsafe.As<T, byte>(ref MemoryMarshal.GetReference(values)),
                    ref reader.ReadBlock(size), (uint)size);
                return;
            }

            if (typeof(TFormatter) == typeof(DateTimeOffsetFormatter))
            {
                DateTimeOffsetFormatter.ReadAll(
                    ref reader.ReadBlock(checked(values.Length * NinoWire.DateTimeOffsetSize)),
                    ref Unsafe.As<T, DateTimeOffset>(ref MemoryMarshal.GetReference(values)), values.Length);
                return;
            }

            int block = NinoBlock.Size<T, TFormatter>();
            if (block > 0)
            {
                NinoBlock.ReadAll<T, TFormatter>(ref reader.ReadBlock(checked(values.Length * block)),
                    ref MemoryMarshal.GetReference(values), values.Length, block);
                return;
            }

            // The reader as a local for the loop: its cursor stays in registers, where through the ref parameter every
            // element would store it and load it back (List<(int, long)>: 2x faster). Copied a field at a time
            // (Reader.CopyTo).
            Reader local = default;
            reader.CopyTo(ref local);
            for (int i = 0; i < values.Length; i++)
                default(TFormatter).Deserialize(ref values[i], ref local);
            local.CopyTo(ref reader);
        }

        /// <summary>
        /// Returns <paramref name="array"/> when it has <paramref name="length"/> elements; otherwise replaces it with
        /// an array of that length, moving over the elements that fit (unless <paramref name="uninitialized"/>: the
        /// caller overwrites every element) and recycling the rest (and the old array).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static T[] EnsureLength<T>(ref T[] array, int length, bool uninitialized = false)
        {
            var current = array;
            if (current == null)
            {
                // No array yet (a new object): allocated here, the JIT's allocation helper called directly rather
                // than through Resize (a call and a frame).
                var result = NinoUnsafe.NewArray<T>(length, uninitialized);
                array = result;
                return result;
            }

            if (current.Length == length)
                return current;
            // Resize takes the old array by value and the slot is stored here: the slot's address reaching a call
            // would keep it in memory when it is a local (the value of a property, read into a local and stored back
            // after), with a store and a reload around the call, and zeroed in the prolog.
            var resized = Resize(current, length, uninitialized);
            array = resized;
            // As NinoRecycler.Replace: the old array is recycled once the new one took the slot (the shared empty
            // array never is).
            if (current.Length != 0)
                NinoRecycler<T[]>.Recycle(current);
            return resized;
        }

        /// <summary>
        /// A new array of <paramref name="length"/> elements for <paramref name="old"/> (null, or of another length):
        /// moves over the elements that fit (unless <paramref name="uninitialized"/>), recycles the rest and clears
        /// the old array. The caller stores the result and recycles the old array.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static T[] Resize<T>(T[] old, int length, bool uninitialized)
        {
            var result = NinoUnsafe.NewArray<T>(length, uninitialized);
            // Nothing to move or drop (the shared empty array is never recycled).
            if (old == null || old.Length == 0)
                return result;

            // The elements that fit move to the new array (which now owns them; an uninitialized one is about to be
            // overwritten, so they are dropped too), the rest are dropped, and the emptied old array is replaced.
            int keep = uninitialized ? 0 : Math.Min(old.Length, length);
            Array.Copy(old, result, keep);
            NinoRecycler.DiscardAll(old.AsSpan(keep));
            if (NinoUnsafe.IsReferenceOrContainsReferences<T>())
                Array.Clear(old, 0, old.Length);
            return result;
        }
    }

    /// <summary><c>T[]</c>: length (-1 = null), then the elements. Reuses an existing array of the same length.</summary>
    public struct ArrayFormatter<T, TFormatter> : INinoFormatter<T[]>, INinoSizedFormatter<T[]>
        where TFormatter : struct, INinoFormatter<T>
    {
        int INinoSizedFormatter<T[]>.Size(T[] value) =>
            NinoElements.SizedBytes<T, TFormatter>(value == null ? -1 : value.Length);

        void INinoSizedFormatter<T[]>.WriteSized(T[] value, ref byte destination) =>
            NinoElements.WriteSized<T, TFormatter>(ref destination, value, value == null);

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
            if (!reader.TryReadLength(1, out int length))
            {
                NinoRecycler.Discard(ref value);
                return;
            }

            // Raw elements are all overwritten by one block copy, DateTimeOffsets and blocks each in full: a new array need not
            // be zeroed first.
            NinoElements.Read<T, TFormatter>(
                NinoElements.EnsureLength(ref value, length, uninitialized: RawFormatter.Is<T, TFormatter>() ||
                                               typeof(TFormatter) == typeof(DateTimeOffsetFormatter) ||
                                               NinoBlock.Size<T, TFormatter>() > 0),
                ref reader);
        }
    }

    /// <summary>Unmanaged <c>T[]</c>: length (-1 = null), then one block copy.</summary>
    public struct UnmanagedArrayFormatter<[NinoRaw] T> : INinoFormatter<T[]>, INinoRawArrayFormatter where T : unmanaged
    {
        // Elements with a non-canonical layout take the formatter path, which converts them.
        int INinoRawArrayFormatter.ElementSize => NinoLayout<T>.IsCanonical ? Unsafe.SizeOf<T>() : 0;

        // The top-level calls reach these through a static readonly field of their exact type, so they are inlined
        // there with Unsafe.SizeOf<T>() a constant: no multiplication by a loaded size, no overflow branches.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void INinoRawArrayFormatter.Write(NinoArrayBufferWriter buffer, Array array)
        {
            var values = Unsafe.As<T[]>(array);
            int length = values.Length;
            NinoLayout<T>.WriteLengthPrefixed(ref buffer.Reserve(WireSize(length)),
                ref Unsafe.As<T, byte>(ref NinoUnsafe.GetArrayDataReference(values)), length);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        byte[] INinoRawArrayFormatter.Serialize(Array array)
        {
            var values = Unsafe.As<T[]>(array);
            int length = values.Length;
            // Every byte is written below.
            var bytes = NinoUnsafe.AllocateUninitializedArray<byte>(WireSize(length));
            NinoLayout<T>.WriteLengthPrefixed(ref NinoUnsafe.GetArrayDataReference(bytes),
                ref Unsafe.As<T, byte>(ref NinoUnsafe.GetArrayDataReference(values)), length);
            return bytes;
        }

        /// <summary>The length prefix and <paramref name="length"/> elements, in bytes.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int WireSize(int length)
        {
            // In 64 bits: cannot overflow; one compare rejects what does not fit an array.
            ulong size = NinoWire.LengthSize + (ulong)(uint)length * (uint)Unsafe.SizeOf<T>();
            if (size > int.MaxValue)
                NinoThrow.TooLarge();
            return (int)size;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        Array INinoRawArrayFormatter.Read(ReadOnlySpan<byte> data, Array existing)
        {
            // A new array (NinoDeserializer.Deserialize<T[]>(data) passes null, a constant once inlined): no slot
            // to reuse, so no local whose address reaches the recycler and stays in memory.
            if (existing == null && NinoLayout<T>.IsCanonical)
                return ReadNew(data);
            var reader = new Reader(data);
            var value = Unsafe.As<T[]>(existing);
            Deserialize(ref value, ref reader);
            return value;
        }

        /// <summary>The bytes of <see cref="Deserialize"/>, into a new array (null for null).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static T[] ReadNew(ReadOnlySpan<byte> data)
        {
            if (data.Length < NinoWire.LengthSize)
                NinoThrow.EndOfData();
            ref byte start = ref MemoryMarshal.GetReference(data);
            int length = NinoUnsafe.Read<int>(ref start);
            uint size = (uint)Unsafe.SizeOf<T>();
            if ((ulong)(uint)length * size > (uint)(data.Length - NinoWire.LengthSize))
            {
                if (length != NinoWire.NullLength)
                    NinoThrow.InvalidLength(length);
                return null;
            }

            var array = NinoUnsafe.NewArray<T>(length, uninitialized: true);
            Unsafe.CopyBlockUnaligned(ref Unsafe.As<T, byte>(ref NinoUnsafe.GetArrayDataReference(array)),
                ref Unsafe.Add(ref start, NinoWire.LengthSize), (uint)length * size);
            return array;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Serialize(T[] value, ref Writer writer) => writer.Write(value);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Deserialize(ref T[] value, ref Reader reader)
        {
            if (!NinoLayout<T>.IsCanonical)
            {
                DeserializeConverted(ref value, ref reader);
                return;
            }

            // One bounds check for the count and the elements: a negative count (null, or invalid) is a huge
            // unsigned size and takes the cold path.
            int length = reader.UnsafeRead<int>();
            uint size = (uint)Unsafe.SizeOf<T>();
            if ((ulong)(uint)length * size > (uint)reader.Remaining)
            {
                if (length != NinoWire.NullLength)
                    NinoThrow.InvalidLength(length);
                NinoRecycler.Discard(ref value);
                return;
            }

            var array = NinoElements.EnsureLength(ref value, length, uninitialized: true);
            uint bytes = (uint)length * size;
            Unsafe.CopyBlockUnaligned(ref Unsafe.As<T, byte>(ref NinoUnsafe.GetArrayDataReference(array)),
                ref reader.UnsafeAdvance((int)bytes), bytes);
        }

        private static void DeserializeConverted(ref T[] value, ref Reader reader)
        {
            int length = reader.ReadLength(NinoLayout<T>.Size);
            if (length < 0)
            {
                NinoRecycler.Discard(ref value);
                return;
            }

            reader.ReadUnmanagedRaw(NinoElements.EnsureLength(ref value, length, uninitialized: true).AsSpan());
        }
    }

    /// <summary>
    /// <c>T?[]</c> of raw <typeparamref name="T"/>: length, then every element at a fixed stride in the canonical
    /// <c>Nullable&lt;T&gt;</c> layout (see <see cref="NinoNullables{T}"/>), written into one reservation and read
    /// without per-element calls. Not the bytes of <see cref="ArrayFormatter{T,TFormatter}"/> with
    /// <see cref="NullableFormatter{T,TFormatter}"/> (a has-value byte, then the value only when present), which
    /// sequences declared as interfaces (<c>IList&lt;T?&gt;</c>, <c>IEnumerable&lt;T?&gt;</c>) write.
    /// </summary>
    public struct NullableArrayFormatter<[NinoRaw] T> : INinoFormatter<T?[]> where T : unmanaged
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

            NinoNullables<T>.Read(NinoElements.EnsureLength(ref value, length, uninitialized: true), ref reader);
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

        private static readonly NinoMask Mask = BuildMask();

        private static NinoMask BuildMask()
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
            ref byte dst = ref writer.Reserve(checked(NinoWire.LengthSize + values.Length * Stride));
            NinoUnsafe.Write(ref dst, values.Length);
            dst = ref Unsafe.Add(ref dst, NinoWire.LengthSize);
            if (values.IsEmpty)
                return;
            ref T? item = ref MemoryMarshal.GetReference(values);
            if (Raw)
            {
                ref byte src = ref Unsafe.As<T?, byte>(ref item);
                if (Mask != null)
                    Mask.Copy(ref dst, ref src, values.Length);
                else
                    NinoUnsafe.Copy(ref dst, ref src, (uint)(values.Length * Stride));
                return;
            }

            Unsafe.InitBlockUnaligned(ref dst, 0, (uint)(values.Length * Stride));
            for (int i = 0; i < values.Length; i++)
            {
                ref T? element = ref Unsafe.Add(ref item, i);
                if (!element.HasValue)
                    continue;
                ref byte slot = ref Unsafe.Add(ref dst, i * Stride);
                slot = NinoWire.NotNull;
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
                Unsafe.Add(ref item, i) = slot != NinoWire.Null
                    ? NinoLayout<T>.Read(ref Unsafe.Add(ref slot, Offset))
                    : default(T?);
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
            ref byte p = ref writer.Reserve(2 * NinoWire.LengthSize);
            NinoUnsafe.Write(ref p, 0, a);
            NinoUnsafe.Write(ref p, NinoWire.LengthSize, b);
            if (value.Length != 0)
                MultiDim.Write<T, TFormatter>(ref value[0, 0], value.Length, ref writer);
        }

        public void Deserialize(ref T[,] value, ref Reader reader)
        {
            int a = MultiDim.ReadLength(ref reader);
            if (a < 0)
            {
                NinoRecycler.Discard(ref value);
                return;
            }

            int b = reader.UnsafeRead<int>();
            int total = MultiDim.CheckedTotal(a, b, 1, 1, ref reader);
            var array = value;
            if (array == null || array.GetLength(0) != a || array.GetLength(1) != b)
            {
                array = new T[a, b];
                NinoRecycler.Replace(ref value, array);
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

            ref byte p = ref writer.Reserve(3 * NinoWire.LengthSize);
            NinoUnsafe.Write(ref p, 0, value.GetLength(0));
            NinoUnsafe.Write(ref p, NinoWire.LengthSize, value.GetLength(1));
            NinoUnsafe.Write(ref p, 2 * NinoWire.LengthSize, value.GetLength(2));
            if (value.Length != 0)
                MultiDim.Write<T, TFormatter>(ref value[0, 0, 0], value.Length, ref writer);
        }

        public void Deserialize(ref T[,,] value, ref Reader reader)
        {
            int a = MultiDim.ReadLength(ref reader);
            if (a < 0)
            {
                NinoRecycler.Discard(ref value);
                return;
            }

            int b = reader.UnsafeRead<int>();
            int c = reader.UnsafeRead<int>();
            int total = MultiDim.CheckedTotal(a, b, c, 1, ref reader);
            var array = value;
            if (array == null || array.GetLength(0) != a || array.GetLength(1) != b || array.GetLength(2) != c)
            {
                array = new T[a, b, c];
                NinoRecycler.Replace(ref value, array);
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

            ref byte p = ref writer.Reserve(4 * NinoWire.LengthSize);
            NinoUnsafe.Write(ref p, 0, value.GetLength(0));
            NinoUnsafe.Write(ref p, NinoWire.LengthSize, value.GetLength(1));
            NinoUnsafe.Write(ref p, 2 * NinoWire.LengthSize, value.GetLength(2));
            NinoUnsafe.Write(ref p, 3 * NinoWire.LengthSize, value.GetLength(3));
            if (value.Length != 0)
                MultiDim.Write<T, TFormatter>(ref value[0, 0, 0, 0], value.Length, ref writer);
        }

        public void Deserialize(ref T[,,,] value, ref Reader reader)
        {
            int a = MultiDim.ReadLength(ref reader);
            if (a < 0)
            {
                NinoRecycler.Discard(ref value);
                return;
            }

            int b = reader.UnsafeRead<int>();
            int c = reader.UnsafeRead<int>();
            int d = reader.UnsafeRead<int>();
            int total = MultiDim.CheckedTotal(a, b, c, d, ref reader);
            var array = value;
            if (array == null || array.GetLength(0) != a || array.GetLength(1) != b || array.GetLength(2) != c ||
                array.GetLength(3) != d)
            {
                array = new T[a, b, c, d];
                NinoRecycler.Replace(ref value, array);
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
            if (RawFormatter.Is<T, TFormatter>())
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
            if (RawFormatter.Is<T, TFormatter>())
            {
                int size = checked(count * Unsafe.SizeOf<T>());
                Unsafe.CopyBlockUnaligned(ref Unsafe.As<T, byte>(ref first), ref reader.ReadBlock(size), (uint)size);
                return;
            }

            NinoElements.Read<T, TFormatter>(ref first, count, ref reader);
        }

        /// <summary>
        /// The first length (-1 for null). Not checked against the remaining bytes: another dimension may be 0, so
        /// a single length says nothing about the payload; <see cref="CheckedTotal"/> checks the product.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int ReadLength(ref Reader reader) => reader.ReadLength(minElementSize: 0);

        /// <summary>Longest dimension the runtime can allocate, also when the array is empty.</summary>
        private const int MaxLength = 0x7FFFFFC7;

        /// <summary>
        /// The element count of the other lengths read raw: rejects negative lengths, lengths no array can have and
        /// more elements than bytes left (every element takes at least one byte).
        /// </summary>
        public static int CheckedTotal(int a, int b, int c, int d, ref Reader reader)
        {
            if (b < 0 || c < 0 || d < 0)
                NinoThrow.InvalidLength(Math.Min(b, Math.Min(c, d)));
            if (a > MaxLength || b > MaxLength || c > MaxLength || d > MaxLength)
                NinoThrow.InvalidLength(Math.Max(Math.Max(a, b), Math.Max(c, d)));
            long total = (long)a * b * c * d;
            if (total > reader.Remaining)
                NinoThrow.InvalidLength((int)Math.Min(total, int.MaxValue));
            return (int)total;
        }
    }
}
