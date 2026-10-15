using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace Nino.Core
{
    /// <summary>
    /// Reads Nino binary data. Every read is bounds checked, and lengths are validated against the remaining
    /// bytes before anything is allocated.
    /// </summary>
    public ref struct Reader
    {
        private readonly ReadOnlySpan<byte> _data;
        private int _pos;
        private NinoReferenceReadTable _refs;

        public Reader(ReadOnlySpan<byte> data)
        {
            _data = data;
            _pos = 0;
            _refs = null;
        }

        /// <summary>
        /// Ends reference tracking for this reader. Called by the <see cref="NinoDeserializer"/> APIs that create
        /// the reader; the table is reused by the next call. Slices of this reader must not be used afterwards.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void ReleaseReferences()
        {
            if (_refs == null)
                return;
            NinoReferenceReadTable.Return(_refs);
            _refs = null;
        }

        /// <summary>
        /// Reads what <see cref="Writer.WriteReference"/> wrote. Returns true when a new object follows: the
        /// caller reads it, calls <see cref="SetReference"/> as soon as the instance exists (so cycles resolve),
        /// and <see cref="EndReference"/> when done. Otherwise <paramref name="value"/> is now null or the
        /// object read earlier.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool ReadReference<T>(ref T value, out int index) where T : class
        {
            int head = UnsafeRead<int>();
            index = 0;
            if (head == -1)
            {
                index = BeginReference();
                return true;
            }

            if (head == 0)
                NinoRecycler.Discard(ref value);
            else
                value = GetReference<T>(head);
            return false;
        }

        private int BeginReference() => (_refs ??= NinoReferenceReadTable.Rent()).Begin();

        private T GetReference<T>(int index) where T : class
        {
            if (_refs == null)
                NinoThrow.InvalidReference(index);
            var obj = _refs.Get(index);
            if (obj is T t)
                return t;
            NinoThrow.ReferenceTypeMismatch(typeof(T), obj.GetType());
            return null;
        }

        /// <summary>Records the instance of the object whose header was just read.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetReference(object value)
        {
            var refs = _refs;
            if (refs != null && refs.Pending != 0)
                refs.Set(refs.Pending, value);
        }

        /// <summary>Records the object read after <see cref="ReadReference{T}"/> returned true.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void EndReference(int index, object value) => _refs.Set(index, value);

        /// <summary>
        /// Call when done with a reader returned by <see cref="ReadSlice"/>: objects it read stay visible to
        /// later back references.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void EndSlice(in Reader slice)
        {
            if (_refs == null)
                _refs = slice._refs;
        }

        /// <summary>Number of bytes consumed so far.</summary>
        public int Position
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _pos;
        }

        /// <summary>Number of bytes left.</summary>
        public int Remaining
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _data.Length - _pos;
        }

        /// <summary>Whether all bytes have been consumed.</summary>
        public bool Eof
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _pos >= _data.Length;
        }

        /// <summary>
        /// Consumes <paramref name="size"/> bytes and returns a reference to the first one.
        /// Use <see cref="NinoUnsafe.Read{T}(ref byte, int)"/> to decode the block.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ref byte ReadBlock(int size)
        {
            int pos = _pos;
            if ((uint)size > (uint)(_data.Length - pos))
                NinoThrow.EndOfData();
            _pos = pos + size;
            return ref Unsafe.Add(ref MemoryMarshal.GetReference(_data), pos);
        }

        /// <summary>Skips <paramref name="count"/> bytes.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Advance(int count) => ReadBlock(count);

        /// <summary>Reads an unmanaged value. <typeparamref name="T"/> must not contain references.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public T UnsafeRead<T>()
        {
            int size = Unsafe.SizeOf<T>();
            int pos = _pos;
            if ((uint)size > (uint)(_data.Length - pos))
                NinoThrow.EndOfData();
            _pos = pos + size;
            return NinoUnsafe.Read<T>(ref Unsafe.Add(ref MemoryMarshal.GetReference(_data), pos));
        }

        /// <summary>
        /// Reads an unmanaged value written by <see cref="Writer.WriteValue{T}"/> (converted on runtimes that lay
        /// the type out differently). <typeparamref name="T"/> must not contain references.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public T ReadValue<T>() => NinoLayout<T>.Read(ref ReadBlock(NinoLayout<T>.Size));

        /// <summary>Reads an unmanaged value.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public T Read<T>() where T : unmanaged => ReadValue<T>();

        /// <summary>Reads an unmanaged value.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Read<T>(out T value) where T : unmanaged => value = ReadValue<T>();

        /// <summary>Reads a bool (any non-zero byte is true).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Read(out bool value) => value = UnsafeRead<byte>() != 0;

        /// <summary>Reads a nullable unmanaged value written by <see cref="Writer.Write{T}(T?)"/>.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Read<T>(out T? value) where T : unmanaged
        {
            if (UnsafeRead<byte>() == 0)
            {
                value = null;
                return;
            }

            value = ReadValue<T>();
        }

        /// <summary>Consumes 4 bytes if they equal <paramref name="expected"/>; otherwise consumes nothing.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryReadInt32(int expected)
        {
            int pos = _pos;
            if (_data.Length - pos < 4 ||
                NinoUnsafe.Read<int>(ref Unsafe.Add(ref MemoryMarshal.GetReference(_data), pos)) != expected)
                return false;
            _pos = pos + 4;
            return true;
        }

        /// <summary>Consumes 8 bytes if they equal <paramref name="expected"/>; otherwise consumes nothing.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryReadInt64(long expected)
        {
            int pos = _pos;
            if (_data.Length - pos < 8 ||
                NinoUnsafe.Read<long>(ref Unsafe.Add(ref MemoryMarshal.GetReference(_data), pos)) != expected)
                return false;
            _pos = pos + 8;
            return true;
        }

        /// <summary>
        /// Reads a collection length. Returns -1 for null. Throws if the payload cannot possibly contain
        /// <c>length * minElementSize</c> more bytes.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int ReadLength(int minElementSize = 1)
        {
            int length = UnsafeRead<int>();
            if (length < 0)
            {
                if (length != -1)
                    NinoThrow.InvalidLength(length);
                return -1;
            }

            if ((long)length * minElementSize > Remaining)
                NinoThrow.InvalidLength(length);
            return length;
        }

        /// <summary>Reads a length-prefixed sub-reader (used for version-tolerant bodies).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Reader ReadSlice()
        {
            int length = UnsafeRead<int>();
            if ((uint)length > (uint)Remaining)
                NinoThrow.InvalidLength(length);
            var slice = new Reader(_data.Slice(_pos, length));
            slice._refs = _refs;
            _pos += length;
            return slice;
        }

        /// <summary>Reads <paramref name="length"/> raw bytes.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ReadOnlySpan<byte> ReadRaw(int length)
        {
            if ((uint)length > (uint)Remaining)
                NinoThrow.EndOfData();
            var span = _data.Slice(_pos, length);
            _pos += length;
            return span;
        }

        /// <summary>Copies a block of unmanaged values (no length prefix) into <paramref name="destination"/>.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void ReadUnmanagedRaw<T>(Span<T> destination) where T : unmanaged
        {
            if (!NinoLayout<T>.IsCanonical)
            {
                for (int i = 0; i < destination.Length; i++)
                    destination[i] = ReadValue<T>();
                return;
            }

            var bytes = MemoryMarshal.AsBytes(destination);
            ReadRaw(bytes.Length).CopyTo(bytes);
        }

        /// <summary>Reads a length-prefixed unmanaged array written by <see cref="Writer.Write{T}(T[])"/>.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Read<T>(out T[] value) where T : unmanaged
        {
            value = null;
            default(UnmanagedArrayFormatter<T>).Deserialize(ref value, ref this);
        }

        private static string EmptyOrNull(int length)
        {
            if (length == 0)
                return string.Empty;
            if (length != -1)
                NinoThrow.InvalidLength(length);
            return null;
        }

        /// <summary>Reads <paramref name="count"/> elements of <paramref name="elementSize"/> bytes (count &gt; 0).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private ref byte ReadBlockOf(int count, int elementSize)
        {
            int pos = _pos;
            ulong size = (ulong)(uint)count * (uint)elementSize;
            if (size > (uint)(_data.Length - pos))
                NinoThrow.EndOfData();
            _pos = pos + (int)size;
            return ref Unsafe.Add(ref MemoryMarshal.GetReference(_data), pos);
        }

        /// <summary>Reads a UTF-16 string written by <see cref="Writer.Write(string)"/>.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Read(out string value) => value = ReadString();

        /// <summary>Reads a UTF-8 string written by <see cref="Writer.WriteUtf8(string)"/>.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void ReadUtf8(out string value) => value = ReadUtf8();

        /// <summary>Reads a UTF-16 string written by <see cref="Writer.Write(string)"/>.</summary>
        public unsafe string ReadString()
        {
            int length = UnsafeRead<int>();
            if (length <= 0)
                return EmptyOrNull(length);

            // One bounds check: the string is only allocated after it.
            ref byte src = ref ReadBlockOf(length, sizeof(char));
#if NETSTANDARD2_0
            fixed (byte* p = &src)
            {
                return new string((char*)p, 0, length);
            }
#else
            return new string(MemoryMarshal.CreateReadOnlySpan(ref Unsafe.As<byte, char>(ref src), length));
#endif
        }

        /// <summary>Reads a UTF-8 string written by <see cref="Writer.WriteUtf8(string)"/>.</summary>
        public unsafe string ReadUtf8()
        {
            int length = UnsafeRead<int>();
            if (length <= 0)
                return EmptyOrNull(length);

            ref byte src = ref ReadBlockOf(length, 1);
#if NETSTANDARD2_0
            fixed (byte* p = &src)
            {
                return Encoding.UTF8.GetString(p, length);
            }
#else
            return Encoding.UTF8.GetString(MemoryMarshal.CreateReadOnlySpan(ref src, length));
#endif
        }
    }
}
