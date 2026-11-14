using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nino.Core.CompilerServices;
using Nino.Core.Internal;

namespace Nino.Core
{
    // Unmanaged values, lengths, presence, raw bytes and element reservations; the cursor is in Writer.cs.
    public ref partial struct Writer
    {
        /// <summary>Writes an unmanaged value. <typeparamref name="T"/> must not contain references.</summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void UnsafeWrite<T>(T value)
        {
            int size = Unsafe.SizeOf<T>();
            NinoUnsafe.Write(ref Ensure(size), value);
            Skip(size);
        }

        /// <summary>
        /// Writes an unmanaged value in its canonical layout (padding cleared, converted on runtimes that lay the
        /// type out differently). <typeparamref name="T"/> must not contain references.
        /// </summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WriteValue<T>(in T value) => NinoLayout<T>.Write(ref Reserve(NinoLayout<T>.Size), in value);

        /// <summary>Writes an unmanaged value.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Write<T>(in T value) where T : unmanaged => WriteValue(in value);

        /// <summary>Writes a nullable unmanaged value: a presence byte, then the value if present.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Write<T>(T? value) where T : unmanaged => WriteNullableRaw(value);

        /// <summary>
        /// <see cref="Write{T}(T?)"/> of a value whose memory is its wire layout (unmanaged values, raw structs): the
        /// presence byte and the value in one reservation.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void WriteNullableRaw<T>(T? value) where T : struct
        {
            if (!value.HasValue)
            {
                UnsafeWrite(NinoWire.Null);
                return;
            }

            ref byte dst = ref Reserve(1 + NinoLayout<T>.Size);
#if NET8_0_OR_GREATER
            if (NullableWords<T>.Writes)
            {
                NullableWords<T>.Write(ref dst, in value);
                return;
            }
#endif
            dst = NinoWire.NotNull;
#if NET8_0_OR_GREATER
            // Pass the nullable's value directly to the layout writer, which takes it by readonly reference.
            NinoLayout<T>.Write(ref Unsafe.Add(ref dst, 1), in Nullable.GetValueRefOrDefaultRef(in value));
#else
            NinoLayout<T>.Write(ref Unsafe.Add(ref dst, 1), value.GetValueOrDefault());
#endif
        }

        /// <summary>Writes a collection length; use -1 for null.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WriteLength(int length) => UnsafeWrite(length);

        /// <summary>Writes the null collection marker (-1).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WriteNullLength() => UnsafeWrite(NinoWire.NullLength);

        /// <summary>
        /// Writes the null collection marker when <paramref name="value"/> is null, and returns whether it did: the
        /// prologue of the formatters of collections, whose length follows otherwise.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal bool TryWriteNullLength(object value)
        {
            if (value != null)
                return false;
            WriteNullLength();
            return true;
        }

        /// <summary>
        /// Writes the presence byte of a sealed class (or tuple), and returns whether <paramref name="value"/> is
        /// present: its body follows then.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal bool WritePresence(object value)
        {
            if (value == null)
            {
                UnsafeWrite(NinoWire.Null);
                return false;
            }

            UnsafeWrite(NinoWire.NotNull);
            return true;
        }

        /// <summary>Writes raw bytes without a length prefix.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WriteRaw(ReadOnlySpan<byte> bytes)
        {
            if (bytes.IsEmpty)
                return;
            NinoUnsafe.Copy(ref Reserve(bytes.Length), ref MemoryMarshal.GetReference(bytes), (uint)bytes.Length);
        }

        /// <summary>Writes the elements of an unmanaged span as one block, without a length prefix.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WriteUnmanagedRaw<T>(ReadOnlySpan<T> values) where T : unmanaged
        {
            if (!NinoLayout<T>.IsCanonical)
            {
                foreach (var item in values)
                    WriteValue(item);
                return;
            }

            if (values.IsEmpty)
                return;
            NinoLayout<T>.CopyToWire(ref ReserveElements(values.Length, Unsafe.SizeOf<T>()),
                ref Unsafe.As<T, byte>(ref MemoryMarshal.GetReference(values)), values.Length);
        }

        /// <summary>Writes a length-prefixed block of unmanaged values.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Write<T>(ReadOnlySpan<T> values) where T : unmanaged
        {
            if (!NinoLayout<T>.IsCanonical)
            {
                WriteLength(values.Length);
                WriteUnmanagedRaw(values);
                return;
            }

            // AsBytes would check the multiplication with imul/jo (see ReserveCounted).
            int count = values.Length;
            ref byte dst = ref ReserveCounted(count, Unsafe.SizeOf<T>());
            if (count == 0)
                return;
            ref byte src = ref Unsafe.As<T, byte>(ref MemoryMarshal.GetReference(values));
            if (NinoLayout<T>.HasPadding)
                NinoLayout<T>.CopyToWire(ref dst, ref src, count);
            else
                NinoUnsafe.Copy(ref dst, ref src, (uint)count * (uint)Unsafe.SizeOf<T>());
        }

        /// <summary>
        /// Writes the length <paramref name="count"/> and reserves the <paramref name="elementSize"/>-byte elements
        /// after it, in one reservation; returns a reference to the first element. The caller writes every element.
        /// </summary>
        // The size in 64 bits: it cannot overflow, and the capacity check rejects a size too large for an int on the
        // cold path, where a checked int size would test the multiplication and the addition on every call.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal ref byte ReserveCounted(int count, int elementSize)
        {
            ref byte dst = ref ReserveLarge(NinoWire.LengthSize + (ulong)(uint)count * (uint)elementSize);
            NinoUnsafe.Write(ref dst, count);
            return ref Unsafe.Add(ref dst, NinoWire.LengthSize);
        }

        /// <summary>
        /// Reserves <paramref name="count"/> (not negative) elements of <paramref name="elementSize"/> bytes each and
        /// returns a reference to the first one; the size in 64 bits, as in <see cref="ReserveCounted"/>.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal ref byte ReserveElements(int count, int elementSize) =>
            ref ReserveLarge((ulong)(uint)count * (uint)elementSize);

        /// <summary><see cref="Reserve"/> for a size that may not fit in an int (rejected when it does not).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal ref byte ReserveLarge(ulong size)
        {
            if (size > (uint)Avail)
            {
                int pos = Pos;
                SetBuffer(GrowLarge(_owner, pos, size), pos);
            }
            ref byte block = ref Cur;
            Skip((int)size);
            return ref block;
        }

        /// <summary>
        /// Writes the string header <paramref name="header"/> (see <see cref="NinoStrings.ReadHeader"/>), reserves
        /// the <paramref name="header"/> bytes after it (up to 4 GB, rejected past 2 GB) and returns a reference to
        /// them. A header of one or three bytes (up to 32,767 chars) is written inline, the three as one store of four
        /// bytes (its last the body's first, which the caller overwrites); a longer one out of line. The body is found
        /// from the cursor, so only the header stays live past the reserve.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal ref byte ReserveText(uint header)
        {
            int start = header < 0x80 ? 1 : header <= ushort.MaxValue ? 3 : NinoStrings.MaxHeaderSize;
            ref byte block = ref ReserveLarge((ulong)start + header);
            if (header < 0x80)
                block = (byte)header;
            else if (header <= ushort.MaxValue)
                NinoUnsafe.Write(ref block, (header << 8) | NinoStrings.Header16);
            else
                NinoStrings.WriteLongHeader(ref block, header);
            return ref Unsafe.Subtract(ref Cur, (nuint)header);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static byte[] GrowLarge(NinoArrayBufferWriter owner, int pos, ulong size)
        {
            if (size > int.MaxValue)
                NinoThrow.TooLarge();
            return Grow(owner, pos, (int)size);
        }

        /// <summary>Writes a length-prefixed unmanaged array (-1 for null).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Write<T>(T[] values) where T : unmanaged
        {
            if (!TryWriteNullLength(values))
                Write((ReadOnlySpan<T>)values);
        }
    }
}
