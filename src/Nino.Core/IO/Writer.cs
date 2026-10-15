using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace Nino.Core
{
    /// <summary>
    /// Writes Nino binary data into a <see cref="NinoArrayBufferWriter"/>.
    /// <para>
    /// The writer caches the destination buffer; call <see cref="Flush"/> (or <see cref="Dispose"/>) when done so
    /// the buffer's <see cref="NinoArrayBufferWriter.WrittenCount"/> is updated. The top-level
    /// <see cref="NinoSerializer"/> APIs do this for you.
    /// </para>
    /// </summary>
    public ref struct Writer
    {
        private readonly NinoArrayBufferWriter _owner;
        private Span<byte> _buffer;
        private int _pos;
        private NinoReferenceWriteTable _refs;

        /// <exception cref="PlatformNotSupportedException">The host is big-endian.</exception>
        public Writer(NinoArrayBufferWriter buffer)
        {
            // A JIT-time constant on CoreCLR, NativeAOT and Mono: the check folds away.
            if (!BitConverter.IsLittleEndian)
                NinoThrow.BigEndianHost();
            _owner = buffer ?? throw new ArgumentNullException(nameof(buffer));
            _buffer = buffer.Buffer;
            _pos = buffer.Index;
            _refs = null;
        }

        /// <summary>
        /// Ends reference tracking: later objects are not matched against the ones written so far. Called by
        /// the <see cref="NinoSerializer"/> APIs that create the writer; the table is reused by the next call.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void ReleaseReferences()
        {
            if (_refs == null)
                return;
            NinoReferenceWriteTable.Return(_refs);
            _refs = null;
        }

        /// <summary>
        /// Writes the reference header of an object of a type marked <c>trackReferences</c>: <c>0</c> for null,
        /// the 1-based index of an object already written by this writer, or <c>-1</c> for a new object, in which
        /// case it returns true and the caller writes the object (see <see cref="NinoWire"/>).
        /// </summary>
        public bool WriteReference(object value)
        {
            if (value == null)
            {
                UnsafeWrite(NinoWire.NullReference);
                return false;
            }

            int index = (_refs ??= NinoReferenceWriteTable.Rent()).GetOrAdd(value);
            UnsafeWrite(index == 0 ? NinoWire.NewReference : index);
            return index == 0;
        }

        /// <summary>Absolute position in the underlying buffer.</summary>
        public int Position
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _pos;
        }

        /// <summary>Commits the written bytes to the underlying buffer.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Flush() => _owner.Index = _pos;

        /// <summary>Same as <see cref="Flush"/>; enables <c>using var writer = ...</c>.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Dispose() => Flush();

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void Grow(int size)
        {
            _owner.Index = _pos;
            _owner.Grow(size);
            _buffer = _owner.Buffer;
        }

        /// <summary>
        /// Reserves <paramref name="size"/> bytes and returns a reference to the first one.
        /// Use <see cref="NinoUnsafe.Write{T}(ref byte, int, T)"/> to fill the block.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ref byte Reserve(int size)
        {
            int pos = _pos;
            if ((uint)size > (uint)(_buffer.Length - pos))
                Grow(size);
            _pos = pos + size;
            return ref Unsafe.Add(ref MemoryMarshal.GetReference(_buffer), pos);
        }

        /// <summary>Writes an unmanaged value. <typeparamref name="T"/> must not contain references.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void UnsafeWrite<T>(T value)
        {
            int size = Unsafe.SizeOf<T>();
            int pos = _pos;
            if ((uint)size > (uint)(_buffer.Length - pos))
                Grow(size);
            NinoUnsafe.Write(ref Unsafe.Add(ref MemoryMarshal.GetReference(_buffer), pos), value);
            _pos = pos + size;
        }

        /// <summary>
        /// Writes an unmanaged value in its canonical layout (padding cleared, converted on runtimes that lay the
        /// type out differently). <typeparamref name="T"/> must not contain references.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WriteValue<T>(T value) => NinoLayout<T>.Write(ref Reserve(NinoLayout<T>.Size), value);

        /// <summary>Writes an unmanaged value.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Write<T>(T value) where T : unmanaged => WriteValue(value);

        /// <summary>Writes a nullable unmanaged value: a has-value byte, then the value if present.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Write<T>(T? value) where T : unmanaged
        {
            if (!value.HasValue)
            {
                UnsafeWrite(NinoWire.Null);
                return;
            }

            ref byte dst = ref Reserve(1 + NinoLayout<T>.Size);
            dst = NinoWire.NotNull;
            NinoLayout<T>.Write(ref Unsafe.Add(ref dst, 1), value.GetValueOrDefault());
        }

        /// <summary>Writes a collection length; use -1 for null.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WriteLength(int length) => UnsafeWrite(length);

        /// <summary>Writes the null collection marker (-1).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WriteNullLength() => UnsafeWrite(NinoWire.NullLength);

        /// <summary>Reserves 4 bytes to be patched later and returns their position.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int ReserveInt32()
        {
            int pos = _pos;
            Reserve(sizeof(int));
            return pos;
        }

        /// <summary>Overwrites 4 bytes at an absolute position previously returned by <see cref="ReserveInt32"/>.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void PatchInt32(int position, int value)
        {
            NinoUnsafe.Write(ref Unsafe.Add(ref MemoryMarshal.GetReference(_buffer), position), value);
        }

        /// <summary>
        /// Writes, at <paramref name="lengthPosition"/>, the number of bytes written after that 4-byte slot.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void PatchLength(int lengthPosition) =>
            PatchInt32(lengthPosition, _pos - lengthPosition - NinoWire.LengthSize);

        /// <summary>Writes raw bytes without a length prefix.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WriteRaw(ReadOnlySpan<byte> bytes)
        {
            if (bytes.IsEmpty)
                return;
            Unsafe.CopyBlockUnaligned(ref Reserve(bytes.Length), ref MemoryMarshal.GetReference(bytes), (uint)bytes.Length);
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
            NinoLayout<T>.CopyToWire(ref Reserve(values.Length * Unsafe.SizeOf<T>()),
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

            var bytes = MemoryMarshal.AsBytes(values);
            NinoLayout<T>.WriteLengthPrefixed(ref Reserve(NinoWire.LengthSize + bytes.Length),
                ref MemoryMarshal.GetReference(bytes), values.Length);
        }

        /// <summary>Writes a length-prefixed unmanaged array (-1 for null).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Write<T>(T[] values) where T : unmanaged
        {
            if (values == null)
            {
                WriteNullLength();
                return;
            }

            Write((ReadOnlySpan<T>)values);
        }

        /// <summary>Writes a string as UTF-16: char count (-1 for null), then the code units.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Write(string value)
        {
            if (value == null)
            {
                WriteNullLength();
                return;
            }

            Write(value.AsSpan());
        }

        /// <summary>Longest string <see cref="WriteUtf8"/> reserves 3 bytes per char for.</summary>
        private const int Utf8WorstCaseMaxChars = 1024;

        /// <summary>Writes a string as UTF-8: byte count (-1 for null), then the bytes.</summary>
        public void WriteUtf8(string value)
        {
            if (value == null)
            {
                WriteNullLength();
                return;
            }

            // Short strings reserve the worst case (3 bytes per char) and encode in one pass; long ones count their
            // bytes first, so the buffer does not grow to three times the string (or overflow the count).
            int maxBytes = value.Length <= Utf8WorstCaseMaxChars
                ? value.Length * 3
                : Encoding.UTF8.GetByteCount(value);
            int lengthPos = _pos;
            ref byte dst = ref Reserve(NinoWire.LengthSize + maxBytes);
            int written = EncodeUtf8(value, ref Unsafe.Add(ref dst, NinoWire.LengthSize), maxBytes);
            NinoUnsafe.Write(ref dst, written);
            // give back the unused part of the worst-case reservation
            _pos = lengthPos + NinoWire.LengthSize + written;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static unsafe int EncodeUtf8(string value, ref byte destination, int capacity)
        {
            if (value.Length == 0)
                return 0;
#if NET8_0_OR_GREATER
            var status = System.Text.Unicode.Utf8.FromUtf16(value.AsSpan(), MemoryMarshal.CreateSpan(ref destination, capacity),
                out _, out int written, replaceInvalidSequences: true);
            if (status != System.Buffers.OperationStatus.Done)
                NinoThrow.InvalidData("Failed to encode UTF-8 string");
            return written;
#else
            fixed (char* src = value)
            fixed (byte* dst = &destination)
            {
                return Encoding.UTF8.GetBytes(src, value.Length, dst, capacity);
            }
#endif
        }
    }
}
