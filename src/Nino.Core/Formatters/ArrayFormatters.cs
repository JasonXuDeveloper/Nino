using System;
using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nino.Core.CompilerServices;
using Nino.Core.Internal;

namespace Nino.Core
{
    /// <summary><c>T[]</c>: length (-1 = null), then the elements. Reuses an existing array of the same length.</summary>
    public struct ArrayFormatter<T, TFormatter> : INinoFormatter<T[]>, INinoSizedFormatter<T[]>
        where TFormatter : struct, INinoFormatter<T>
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        int INinoSizedFormatter<T[]>.Size(T[] value) =>
            NinoElements.SizedBytes<T, TFormatter>(value == null ? -1 : value.Length);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void INinoSizedFormatter<T[]>.WriteSized(T[] value, ref byte destination) =>
            NinoElements.WriteSized<T, TFormatter>(ref destination, value, value == null);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Serialize(T[] value, ref Writer writer)
        {
            if (!writer.TryWriteNullLength(value))
                NinoCollections.WriteSpan<T, TFormatter>(value, ref writer);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Deserialize(ref Reader reader, ref T[] value)
        {
            if (!reader.TryReadLengthOrDiscard(ref value, NinoElements.MinWireSize<T, TFormatter>(), out int length))
                return;

            // Elements far larger in memory than on the wire: out of line, grown as they are read where the data
            // could not fill as many. A constant false for the others, and nothing compiled.
            if (Unsafe.SizeOf<T>() > NinoElements.Amplification && NinoElements.HeavyElements<T, TFormatter>.Value)
            {
                reader = NinoElements.ReadGrowing<T, TFormatter>(ref value, length, reader);
                return;
            }

            NinoElements.Read<T, TFormatter>(
                NinoElements.EnsureLength(ref value, length, NinoElements.CopiedWhole<T, TFormatter>(),
                    NinoElements.ReadOverwrites<T, TFormatter>()), ref reader, prechecked: true);
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

        void INinoRawArrayFormatter.WriteTo(IBufferWriter<byte> buffer, Array array)
        {
            var values = Unsafe.As<T[]>(array);
            int length = values.Length;
            int size = WireSize(length);
            int payloadSize = size - NinoWire.LengthSize;
            var destination = buffer.GetSpan(size);
            var body = destination.Slice(NinoWire.LengthSize, payloadSize);
            if (length != 0)
            {
#if NETSTANDARD2_0
                var source = MemoryMarshal.AsBytes(values.AsSpan());
#else
                var source = MemoryMarshal.CreateReadOnlySpan(
                    ref Unsafe.As<T, byte>(ref NinoUnsafe.GetArrayDataReference(values)), payloadSize);
#endif
                // A caller-provided span may alias the source. Publish the header after the payload copy.
                if (!NinoLayout<T>.HasPadding || source.Overlaps(body))
                {
                    source.CopyTo(body);
                    NinoLayout<T>.ClearPadding(ref MemoryMarshal.GetReference(body), length);
                }
                else
                    NinoLayout<T>.CopyToWire(ref MemoryMarshal.GetReference(body),
                        ref MemoryMarshal.GetReference(source), length);
            }
            NinoUnsafe.Write(ref MemoryMarshal.GetReference(destination), length);
            buffer.Advance(size);
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

        /// <summary>
        /// Whether a length read as it is (-1 for null included) is past <see cref="NinoSettings.MaxCollectionLength"/>:
        /// -1 is then too, and takes the cold path that tells null from invalid.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool TooLong(int length) =>
            NinoLimits.LengthCapped && (uint)length > (uint)NinoLimits.MaxCollectionLength;

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
            if (existing == null && NinoLayout<T>.CopiesAsIs)
                return ReadNew(data);
            var reader = new Reader(data);
            var value = Unsafe.As<T[]>(existing);
            Deserialize(ref reader, ref value);
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
            ulong bytes = (ulong)(uint)length * (uint)Unsafe.SizeOf<T>();
            if (bytes > (uint)(data.Length - NinoWire.LengthSize) || TooLong(length))
            {
                if (length != NinoWire.NullLength)
                    NinoThrow.InvalidLength(length);
                return null;
            }

            var array = NinoUnsafe.NewArray<T>(length, uninitialized: true);
            NinoUnsafe.CopyToNew(ref Unsafe.As<T, byte>(ref NinoUnsafe.GetArrayDataReference(array)),
                ref Unsafe.Add(ref start, NinoWire.LengthSize), (uint)bytes);
            return array;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Serialize(T[] value, ref Writer writer) => writer.Write(value);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Deserialize(ref Reader reader, ref T[] value)
        {
            // Also bools, checked there (NinoLayout<T>.CopiesAsIs).
            if (!NinoLayout<T>.CopiesAsIs)
            {
                DeserializeConverted(ref reader, ref value);
                return;
            }

            // One bounds check for the count and the elements: a negative count (null, or invalid) is a huge
            // unsigned size and takes the cold path.
            int length = reader.UnsafeRead<int>();
            ulong bytes = (ulong)(uint)length * (uint)Unsafe.SizeOf<T>();
            if (bytes > (uint)reader.Remaining || TooLong(length))
            {
                if (length != NinoWire.NullLength)
                    NinoThrow.InvalidLength(length);
                NinoRecycler.Discard(ref value);
                return;
            }

            var array = NinoElements.EnsureLength(ref value, length, uninitialized: true);
            NinoUnsafe.CopyToNew(ref Unsafe.As<T, byte>(ref NinoUnsafe.GetArrayDataReference(array)),
                ref reader.UnsafeAdvance((int)bytes), (uint)bytes);
        }

        private static void DeserializeConverted(ref Reader reader, ref T[] value)
        {
            if (!reader.TryReadLengthOrDiscard(ref value, NinoLayout<T>.Size, out int length))
                return;

            // Bools and Untrusted mode's values are checked by the read: a new array takes the slot once filled.
            var current = value;
            var array = NinoElements.NewOrSame(current, length);
            reader.ReadUnmanagedRaw(array.AsSpan());
            NinoElements.Publish(ref value, current, array);
        }
    }

    /// <summary>
    /// <c>T?[]</c> of raw <typeparamref name="T"/>: length, then every element at a fixed stride in the canonical
    /// <c>Nullable&lt;T&gt;</c> layout (see <see cref="NinoNullables{T}"/>), written into one reservation and read
    /// without per-element calls. Not the bytes of <see cref="ArrayFormatter{T,TFormatter}"/> with
    /// <see cref="NullableFormatter{T,TFormatter}"/> (a presence byte, then the value only when present), which
    /// sequences declared as interfaces (<c>IList&lt;T?&gt;</c>, <c>IEnumerable&lt;T?&gt;</c>) write.
    /// </summary>
    public struct NullableArrayFormatter<[NinoRaw] T> : INinoFormatter<T?[]> where T : unmanaged
    {
        public void Serialize(T?[] value, ref Writer writer)
        {
            if (writer.TryWriteNullLength(value))
                return;

            NinoNullables<T>.Write(value, ref writer);
        }

        public void Deserialize(ref Reader reader, ref T?[] value)
        {
            if (!reader.TryReadLengthOrDiscard(ref value, NinoNullables<T>.Stride, out int length))
                return;

            // Uninitialized only for a block copy, after which the presence bytes are checked: such a new array
            // takes the slot once filled (see NinoElements.CopiedWhole).
            if (NinoNullables<T>.Raw)
            {
                var current = value;
                var array = NinoElements.NewOrSame(current, length);
                NinoNullables<T>.Read(array, ref reader);
                NinoElements.Publish(ref value, current, array);
                return;
            }

            NinoNullables<T>.Read(NinoElements.EnsureLength(ref value, length, overwritten: true), ref reader);
        }
    }

    /// <summary>
    /// <c>T?</c> sequences with unmanaged <c>T</c>, in the canonical (64-bit corefx) <c>Nullable&lt;T&gt;</c> layout:
    /// a presence byte, zero padding up to T's alignment, then T in its canonical layout (zero when null).
    /// Where the runtime lays <c>T?</c> out the same way the elements are copied as one block (padding cleared);
    /// elsewhere (old Mono puts the value first, x86 aligns 8-byte values to 4) they are converted one by one.
    /// </summary>
    internal static class NinoNullables<T> where T : unmanaged
    {
        private static readonly int Offset = NinoLayout<T>.Info.Align > 0 ? NinoLayout<T>.Info.Align : 1;

        /// <summary>Wire size of one element.</summary>
        public static readonly int Stride = Offset + NinoLayout<T>.WireSize;

        /// <summary>Whether <c>T?</c> memory equals its wire layout on this runtime.</summary>
        // Not for bool: its values are read one by one, checked (NinoLayout<T>.CopiesAsIs).
        internal static readonly bool Raw = NinoLayout<T>.CopiesAsIs && NullableLayout<T>.HasValueOffset == 0 &&
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
            ref byte dst = ref writer.ReserveCounted(values.Length, Stride);
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

        /// <summary>Reads <c>destination.Length</c> elements (the count has been read).</summary>
        public static void Read(Span<T?> destination, ref Reader reader)
        {
            ref byte src = ref reader.ReadElements(destination.Length, Stride);
            if (destination.IsEmpty)
                return;
            // Presence bytes other than 0 or 1 are invalid data (copied as they are, they would make HasValue true
            // and the value unequal to any other).
            NinoChecks.Flags(ref src, destination.Length, Stride);
            ref T? item = ref MemoryMarshal.GetReference(destination);
            if (Raw)
            {
                NinoUnsafe.CopyToNew(ref Unsafe.As<T?, byte>(ref item), ref src, (uint)(destination.Length * Stride));
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
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Serialize(ArraySegment<T> value, ref Writer writer)
        {
            if (!writer.TryWriteNullLength(value.Array))
                NinoCollections.WriteSpan<T, TFormatter>(value.AsSpan(), ref writer);
        }

        public void Deserialize(ref Reader reader, ref ArraySegment<T> value)
        {
            var array = NinoCollections.ReadArray<T, TFormatter>(ref reader);
            value = array == null ? default : new ArraySegment<T>(array);
        }
    }

    /// <summary>The lengths of a multidimensional array (1 past its rank) and its element count.</summary>
    internal struct ArrayShape
    {
        public int A, B, C, D, Total;
    }

    /// <summary>The lengths of the multidimensional array formatters.</summary>
    internal static class MultiDim
    {
        /// <summary>Writes the length of each dimension of <paramref name="value"/> (not null).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteLengths(Array value, int rank, ref Writer writer)
        {
            ref byte p = ref writer.Reserve(rank * NinoWire.LengthSize);
            NinoUnsafe.Write(ref p, 0, value.GetLength(0));
            NinoUnsafe.Write(ref p, NinoWire.LengthSize, value.GetLength(1));
            if (rank > 2)
                NinoUnsafe.Write(ref p, 2 * NinoWire.LengthSize, value.GetLength(2));
            if (rank > 3)
                NinoUnsafe.Write(ref p, 3 * NinoWire.LengthSize, value.GetLength(3));
        }

        /// <summary>
        /// Reads the <paramref name="rank"/> lengths (false when the first is -1, for null) and checks them: rejects
        /// negative lengths, lengths no array can have and more elements than bytes left (every element takes at
        /// least <paramref name="minElementSize"/> bytes, <see cref="NinoElements.MinWireSize{T,TFormatter}"/>). The
        /// first length alone is not checked against the remaining bytes: another dimension may be 0, so a single
        /// length says nothing about the payload.
        /// </summary>
        public static bool TryReadShape(ref Reader reader, int rank, int minElementSize, out ArrayShape shape)
        {
            int a;
            int headerSize = rank * NinoWire.LengthSize;
            ref byte tail = ref reader.Peek();
            if (!NinoLimits.LengthCapped && reader.Remaining >= headerSize &&
                (a = NinoUnsafe.Read<int>(ref tail)) >= 0)
            {
                reader.UnsafeAdvance(headerSize);
                tail = ref Unsafe.Add(ref tail, NinoWire.LengthSize);
            }
            else
            {
                // A null header ends at the first length. Preserve its checks before reading the tail.
                if (!reader.TryReadLength(0, out a))
                {
                    shape = default;
                    return false;
                }
                tail = ref reader.ReadBlock(headerSize - NinoWire.LengthSize);
            }

            int b = NinoUnsafe.Read<int>(ref tail);
            int c = rank > 2 ? NinoUnsafe.Read<int>(ref tail, NinoWire.LengthSize) : 1;
            int d = rank > 3 ? NinoUnsafe.Read<int>(ref tail, 2 * NinoWire.LengthSize) : 1;
            // Any sign bit or bit 30 requires an exact dimension check.
            if (((a | b | c | d) & unchecked((int)0xC0000000)) != 0)
                CheckDimensions(a, b, c, d);
            long total = (long)a * b;
            if (rank > 2)
            {
                // Check each prefix before multiplying to keep the next product in range.
                // CoreCLR also rejects a trailing-zero shape whose leading dimensions exceed this limit.
                if (total > uint.MaxValue)
                    NinoThrow.InvalidLength(int.MaxValue);
                total *= c;
                if (rank > 3)
                {
                    if (total > uint.MaxValue)
                        NinoThrow.InvalidLength(int.MaxValue);
                    total *= d;
                }
            }

            // Once the final product fits in a uint, multiplying by the minimum wire size fits in a long.
            if (total > uint.MaxValue || total * minElementSize > reader.Remaining ||
                NinoLimits.LengthCapped && total > NinoLimits.MaxCollectionLength)
                NinoThrow.InvalidLength((int)Math.Min(total, int.MaxValue));
            shape.A = a;
            shape.B = b;
            shape.C = c;
            shape.D = d;
            shape.Total = (int)total;
            return true;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void CheckDimensions(int a, int b, int c, int d)
        {
            if (b < 0 || c < 0 || d < 0)
                NinoThrow.InvalidLength(Math.Min(b, Math.Min(c, d)));
            // Large dimensions may still fit the runtime limit.
            int longest = Math.Max(Math.Max(a, b), Math.Max(c, d));
            if (longest > NinoUnsafe.MaxArrayLength)
                NinoThrow.InvalidLength(longest);
        }

        /// <summary>
        /// Copies <paramref name="items"/> into the elements of a multidimensional array starting at
        /// <paramref name="first"/>, as many as it holds.
        /// </summary>
        public static void Fill<T>(T[] items, ref T first)
        {
#if !NETSTANDARD2_0
            if (NinoUnsafe.IsReferenceOrContainsReferences<T>())
            {
                items.AsSpan().CopyTo(MemoryMarshal.CreateSpan(ref first, items.Length));
                return;
            }
#endif

            for (int i = 0; i < items.Length; i++)
                Unsafe.Add(ref first, i) = items[i];
        }

        /// <summary>Whether <paramref name="array"/> is not null and of <paramref name="shape"/>.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool SameShape(Array array, int rank, in ArrayShape shape)
        {
            if (array == null || array.GetLength(0) != shape.A || array.GetLength(1) != shape.B)
                return false;
            return (rank < 3 || array.GetLength(2) == shape.C) && (rank < 4 || array.GetLength(3) == shape.D);
        }
    }
}
