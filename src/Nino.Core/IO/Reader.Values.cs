using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nino.Core.CompilerServices;
using Nino.Core.Internal;

namespace Nino.Core
{
    // Position, blocks, unmanaged values, lengths, presence and raw bytes; the cursor is in Reader.cs.
    public ref partial struct Reader
    {
        /// <summary>
        /// Position in the data: bytes consumed so far. A reader from <see cref="ReadSlice"/> counts from the start
        /// of the data of the reader it was made from.
        /// </summary>
        public int Position
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => Pos;
        }

        /// <summary>Number of bytes left.</summary>
        public int Remaining
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => Avail;
        }

        /// <summary>Whether all bytes have been consumed.</summary>
        public bool Eof
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => Avail <= 0;
        }

        /// <summary>
        /// Consumes <paramref name="size"/> bytes and returns a reference to the first one, for a custom formatter that
        /// decodes them itself. The data is little-endian (decode it with <c>BinaryPrimitives</c>, for instance). The
        /// reference points into the input: read only those <paramref name="size"/> bytes and never write through it.
        /// </summary>
        /// <exception cref="NinoDataException"><paramref name="size"/> is negative or more than <see cref="Remaining"/>
        /// (nothing is consumed).</exception>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ref byte ReadBlock(int size)
        {
            ref byte block = ref Cur;
            if ((uint)size > (uint)Avail)
                NinoThrow.EndOfData();
            Skip(size);
            return ref block;
        }

        /// <summary>
        /// Consumes <paramref name="count"/> (not negative) elements of <paramref name="elementSize"/> bytes each and
        /// returns a reference to the first one. The size in 64 bits, so it cannot overflow: one compare, where a
        /// checked int product would test the multiplication too.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal ref byte ReadElements(int count, int elementSize)
        {
            ulong size = (ulong)(uint)count * (uint)elementSize;
            ref byte block = ref Cur;
            if (size > (uint)Avail)
                NinoThrow.EndOfData();
            Skip((int)size);
            return ref block;
        }

        /// <summary>The next byte, not consumed: the caller already checked the bytes it looks at are there.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal ref byte Peek() => ref Cur;

        /// <summary>Consumes <paramref name="size"/> bytes the caller already checked are there.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal ref byte UnsafeAdvance(int size)
        {
            ref byte block = ref Cur;
            Skip(size);
            return ref block;
        }

        /// <summary>Skips <paramref name="count"/> bytes; throws as <see cref="ReadBlock"/> does.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Advance(int count) => ReadBlock(count);

        /// <summary>Reads an unmanaged value. <typeparamref name="T"/> must not contain references.</summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public T UnsafeRead<T>()
        {
            int size = Unsafe.SizeOf<T>();
            ref byte block = ref Cur;
            if ((uint)size > (uint)Avail)
                NinoThrow.EndOfData();
            Skip(size);
            return NinoUnsafe.Read<T>(ref block);
        }

        /// <summary>
        /// Generated code: <see cref="UnsafeRead{T}"/> of a raw member whose type may hold values no constructor makes,
        /// as <see cref="NinoUnsafe.ReadChecked{T}"/>.
        /// </summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public T ReadChecked<T>()
        {
            T value = UnsafeRead<T>();
            return NinoLayout<T>.Checks == null ? value : NinoChecks.Checked(value);
        }

        /// <summary>
        /// Reads an unmanaged value written by <see cref="Writer.WriteValue{T}"/> (converted on runtimes that lay
        /// the type out differently). <typeparamref name="T"/> must not contain references.
        /// </summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public T ReadValue<T>() => NinoLayout<T>.Read(ref ReadBlock(NinoLayout<T>.Size));

        /// <summary>Reads an unmanaged value.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public T Read<T>() where T : unmanaged => ReadValue<T>();

        /// <summary>Reads an unmanaged value.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Read<T>(out T value) where T : unmanaged => value = ReadValue<T>();

        /// <summary>Reads a bool: a byte of 0 or 1 (any other is invalid data).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Read(out bool value) => value = ReadBool();

        /// <summary>
        /// Generated code: reads a bool member, a byte of 0 or 1; any other is invalid data. Its own method rather
        /// than a check in <see cref="UnsafeRead{T}"/>, which every other raw member is read with.
        /// </summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool ReadBool() => NinoUnsafe.CheckedBool(UnsafeRead<byte>());

        /// <summary>Reads a nullable unmanaged value written by <see cref="Writer.Write{T}(T?)"/>.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Read<T>(out T? value) where T : unmanaged => value = ReadNullableRaw<T>();

        /// <summary>
        /// Reads one <see cref="NinoNullable{T}"/> written as a <c>T?</c> of a raw <typeparamref name="T"/> (a presence
        /// byte, then the value when present): the checks of <see cref="ReadNullableRaw{T}(ref T?)"/>, the value made
        /// by the type itself.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void ReadNinoNullable<T>(ref NinoNullable<T> value) where T : unmanaged
        {
            int size = NinoLayout<T>.Size;
            int avail = Avail;
            ref byte block = ref Cur;
            if ((uint)size < (uint)avail && block == NinoWire.NotNull)
            {
                Skip(1 + size);
                if (NinoNullables<T>.Wide && !NinoLimits.Untrusted)
                    NinoNullables<T>.StoreWide(ref value, ref Unsafe.Add(ref block, 1));
                else
                    value = new NinoNullable<T>(NinoLayout<T>.Read(ref Unsafe.Add(ref block, 1)));
                return;
            }

            // Only null is left: no byte, a value without its bytes and a byte other than 0 or 1 throw.
            if (avail == 0 || block != NinoWire.Null)
                NinoThrow.NotNull(avail == 0 ? -1 : block);
            Skip(1);
            value = default;
        }

        /// <summary>
        /// Reads what <see cref="Writer.WriteNullableRaw{T}"/> wrote: the presence byte and the value with one bounds
        /// check where both fit in the remaining bytes, else (near the end, where a null takes only its byte) one each.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal T? ReadNullableRaw<T>() where T : struct
        {
            T? value = null;
            ReadNullableRaw(ref value);
            return value;
        }

        /// <summary>
        /// <see cref="ReadNullableRaw{T}()"/> over <paramref name="value"/>: a null read discards the value held
        /// (<see cref="NinoRecycler.DiscardNullable{T}"/>), told by the presence byte, never by a <c>HasValue</c>
        /// read here, which made the JIT split the value into its fields (see there).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void ReadNullableRaw<T>(ref T? value) where T : struct
        {
            int size = NinoLayout<T>.Size;
            int avail = Avail;
            ref byte block = ref Cur;
            if ((uint)size < (uint)avail && block == NinoWire.NotNull)
            {
                Skip(1 + size);
#if NET8_0_OR_GREATER
                if (NullableWords<T>.Applies)
                {
                    NullableWords<T>.Read(ref block, ref value);
                    return;
                }
#endif
                value = NinoLayout<T>.Read(ref Unsafe.Add(ref block, 1));
                return;
            }

            // Only null is left: no byte, a value without its bytes and a byte other than 0 or 1 throw. The value is
            // never read here through the Nullable constructor (a write of each field), which made the JIT split it.
            if (avail == 0 || block != NinoWire.Null)
                NinoThrow.NotNull(avail == 0 ? -1 : block);
            Skip(1);
            NinoRecycler.DiscardNullable(ref value);
        }

        /// <summary>Consumes 4 bytes if they equal <paramref name="expected"/>; otherwise consumes nothing.</summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryReadInt32(int expected)
        {
            if (Avail < sizeof(int) || NinoUnsafe.Read<int>(ref Cur) != expected)
                return false;
            Skip(sizeof(int));
            return true;
        }

        /// <summary>Consumes 8 bytes if they equal <paramref name="expected"/>; otherwise consumes nothing.</summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryReadInt64(long expected)
        {
            if (Avail < sizeof(long) || NinoUnsafe.Read<long>(ref Cur) != expected)
                return false;
            Skip(sizeof(long));
            return true;
        }

        /// <summary>Generated code consumes a matching keyed header and its raw value together.</summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryReadKeyed<T>(long header, out T value) where T : unmanaged
        {
            int size = sizeof(long) + Unsafe.SizeOf<T>();
            ref byte start = ref Cur;
            if (Avail < size || NinoUnsafe.Read<long>(ref start) != header)
            {
                value = default;
                return false;
            }

            value = NinoUnsafe.Read<T>(ref start, sizeof(long));
            Skip(size);
            return true;
        }

        /// <summary>
        /// Reads a collection length. Returns -1 for null. Throws if the payload cannot possibly contain
        /// <c>length * minElementSize</c> more bytes.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int ReadLength(int minElementSize = 1) =>
            // A constant in the formatters: one compare covers null, invalid and too long.
            TryReadLength(Math.Max(minElementSize, 0), out int count) ? count : NinoWire.NullLength;

        /// <summary>
        /// Reads a collection length into <paramref name="length"/>; false for null. Throws if the payload cannot
        /// possibly contain <c>length * minElementSize</c> more bytes (<paramref name="minElementSize"/> not negative).
        /// The sign is tested first: the callers then know the length is not negative, which drops the checks of
        /// <c>new List&lt;T&gt;(length)</c>, <c>CollectionsMarshal.SetCount</c> and the like.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal bool TryReadLength(int minElementSize, out int length)
        {
            length = UnsafeRead<int>();
            if (length < 0)
            {
                if (length != NinoWire.NullLength)
                    NinoThrow.InvalidLength(length);
                return false;
            }

            if ((ulong)(uint)length * (uint)minElementSize > (uint)Avail || NinoLimits.TooLong(length))
                NinoThrow.InvalidLength(length);
            return true;
        }

        /// <summary>
        /// <see cref="TryReadLength(int, out int)"/> in the prologue of a collection formatter: for null, the
        /// collection in <paramref name="value"/> is dropped (<see cref="NinoRecycler.Discard{T}"/>) and false
        /// returned.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal bool TryReadLengthOrDiscard<T>(ref T value, int minElementSize, out int length)
        {
            if (TryReadLength(minElementSize, out length))
                return true;
            NinoRecycler.Discard(ref value);
            return false;
        }

        /// <summary>
        /// Reads the presence byte of a sealed class (or tuple, or nullable value): false for null, after
        /// dropping the instance in <paramref name="value"/> (<see cref="NinoRecycler.Discard{T}"/>); true when the
        /// body follows.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal bool TryReadPresent<T>(ref T value)
        {
            if (ReadPresent())
                return true;
            NinoRecycler.Discard(ref value);
            return false;
        }

        /// <summary>
        /// Reads a presence byte: true for 1, a value follows; false for 0, null. Any other byte is
        /// invalid data. The common case, a value, takes one compare.
        /// </summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool ReadPresent()
        {
            byte flag = UnsafeRead<byte>();
            if (flag == NinoWire.NotNull)
                return true;
            if (flag != NinoWire.Null)
                NinoThrow.InvalidPresence(flag);
            return false;
        }

        /// <summary>Reads <paramref name="length"/> raw bytes.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ReadOnlySpan<byte> ReadRaw(int length)
        {
#if !NET8_0_OR_GREATER
            if ((uint)length > (uint)Avail)
                NinoThrow.EndOfData();
            var span = _data.Slice(_pos, length);
            Skip(length);
            return span;
#else
            return MemoryMarshal.CreateReadOnlySpan(ref ReadBlock(length), length);
#endif
        }

        /// <summary>Copies a block of unmanaged values (no length prefix) into <paramref name="destination"/>.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void ReadUnmanagedRaw<T>(Span<T> destination) where T : unmanaged
        {
            if (!NinoLayout<T>.CopiesAsIs)
            {
                this = ReadUnmanagedOther(destination, this);
                return;
            }

            // One bounds check and a block copy (NinoUnsafe.CopyToNew): AsBytes, Slice and CopyTo would add an overflow
            // check, two more bounds checks and a call through SpanHelpers.Memmove's indirection cell.
            int count = destination.Length;
            NinoUnsafe.CopyToNew(ref Unsafe.As<T, byte>(ref MemoryMarshal.GetReference(destination)),
                ref ReadElements(count, Unsafe.SizeOf<T>()), (uint)count * (uint)Unsafe.SizeOf<T>());
        }

        /// <summary>
        /// <see cref="ReadUnmanagedRaw{T}"/> for values not copied as they are (<see cref="NinoLayout{T}.CopiesAsIs"/>):
        /// bools, checked in the data before one block copy (a byte other than 0 or 1 is invalid data), and values of a
        /// layout this runtime converts, one by one. Out of line, with the reader by value.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static Reader ReadUnmanagedOther<T>(Span<T> destination, Reader reader) where T : unmanaged
        {
            if (NinoLayout<T>.IsCanonical)
            {
                int count = destination.Length;
                ref byte src = ref reader.ReadElements(count, Unsafe.SizeOf<T>());
                NinoChecks.Values<T>(ref src, count, Unsafe.SizeOf<T>());
                NinoUnsafe.CopyToNew(ref Unsafe.As<T, byte>(ref MemoryMarshal.GetReference(destination)), ref src,
                    (uint)count * (uint)Unsafe.SizeOf<T>());
                return reader;
            }

            for (int i = 0; i < destination.Length; i++)
                destination[i] = reader.ReadValue<T>();
            return reader;
        }

        /// <summary>Reads a length-prefixed unmanaged array written by <see cref="Writer.Write{T}(T[])"/>.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Read<T>(out T[] value) where T : unmanaged
        {
            value = null;
            default(UnmanagedArrayFormatter<T>).Deserialize(ref this, ref value);
        }
    }
}
