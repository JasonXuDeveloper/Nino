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

        public Writer(NinoArrayBufferWriter buffer)
        {
            _owner = buffer ?? throw new ArgumentNullException(nameof(buffer));
            _buffer = buffer.Buffer;
            _pos = buffer.Index;
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

        /// <summary>Writes an unmanaged value.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Write<T>(T value) where T : unmanaged => UnsafeWrite(value);

        /// <summary>Writes a nullable unmanaged value: a has-value byte, then the value if present.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Write<T>(T? value) where T : unmanaged
        {
            if (!value.HasValue)
            {
                UnsafeWrite((byte)0);
                return;
            }

            ref byte dst = ref Reserve(1 + Unsafe.SizeOf<T>());
            dst = 1;
            NinoUnsafe.Write(ref dst, 1, value.GetValueOrDefault());
        }

        /// <summary>Writes a collection length; use -1 for null.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WriteLength(int length) => UnsafeWrite(length);

        /// <summary>Writes the null collection marker (-1).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WriteNullLength() => UnsafeWrite(-1);

        /// <summary>Reserves 4 bytes to be patched later and returns their position.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int ReserveInt32()
        {
            int pos = _pos;
            Reserve(4);
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
        public void PatchLength(int lengthPosition) => PatchInt32(lengthPosition, _pos - lengthPosition - 4);

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
            WriteRaw(MemoryMarshal.AsBytes(values));
        }

        /// <summary>Writes a length-prefixed block of unmanaged values.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Write<T>(ReadOnlySpan<T> values) where T : unmanaged
        {
            var bytes = MemoryMarshal.AsBytes(values);
            ref byte dst = ref Reserve(4 + bytes.Length);
            NinoUnsafe.Write(ref dst, values.Length);
            if (bytes.Length != 0)
                Unsafe.CopyBlockUnaligned(ref Unsafe.Add(ref dst, 4), ref MemoryMarshal.GetReference(bytes), (uint)bytes.Length);
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

        /// <summary>Writes a string as UTF-8: byte count (-1 for null), then the bytes.</summary>
        public void WriteUtf8(string value)
        {
            if (value == null)
            {
                WriteNullLength();
                return;
            }

            int maxBytes = checked(value.Length * 3);
            int lengthPos = _pos;
            ref byte dst = ref Reserve(4 + maxBytes);
            int written = EncodeUtf8(value, ref Unsafe.Add(ref dst, 4), maxBytes);
            NinoUnsafe.Write(ref dst, written);
            // give back the unused part of the worst-case reservation
            _pos = lengthPos + 4 + written;
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
