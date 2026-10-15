using System;
using System.Buffers;
using System.Runtime.CompilerServices;

namespace Nino.Core
{
    /// <summary>
    /// A contiguous, growable byte buffer backed by <see cref="ArrayPool{T}.Shared"/>.
    /// <see cref="Writer"/> writes into it directly, so serializing into a reused instance allocates nothing.
    /// Not thread safe: use one instance per thread (or per operation).
    /// </summary>
    public sealed class NinoArrayBufferWriter : IBufferWriter<byte>, IDisposable
    {
        private const int DefaultCapacity = 256;
        private const int MaxArrayLength = 0x7FFFFFC7;

        internal byte[] Buffer;
        internal int Index;

        public NinoArrayBufferWriter() : this(DefaultCapacity)
        {
        }

        public NinoArrayBufferWriter(int initialCapacity)
        {
            if (initialCapacity < 0)
                throw new ArgumentOutOfRangeException(nameof(initialCapacity));

            Buffer = initialCapacity == 0 ? Array.Empty<byte>() : ArrayPool<byte>.Shared.Rent(initialCapacity);
        }

        /// <summary>Number of bytes written so far.</summary>
        public int WrittenCount
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => Index;
        }

        /// <summary>Total capacity of the underlying buffer.</summary>
        public int Capacity => Buffer.Length;

        /// <summary>Remaining capacity before the buffer has to grow.</summary>
        public int FreeCapacity => Buffer.Length - Index;

        /// <summary>The bytes written so far.</summary>
        public ReadOnlySpan<byte> WrittenSpan
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => new ReadOnlySpan<byte>(Buffer, 0, Index);
        }

        /// <summary>The bytes written so far.</summary>
        public ReadOnlyMemory<byte> WrittenMemory => new ReadOnlyMemory<byte>(Buffer, 0, Index);

        /// <summary>Copies the written bytes into a new array.</summary>
        public byte[] ToArray()
        {
            if (Index == 0)
                return Array.Empty<byte>();

            var result = new byte[Index];
            System.Buffer.BlockCopy(Buffer, 0, result, 0, Index);
            return result;
        }

        /// <summary>Resets the written count without clearing or releasing the buffer.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void ResetWrittenCount() => Index = 0;

        /// <summary>Resets the written count without clearing or releasing the buffer.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Clear() => Index = 0;

        /// <summary>Reserves <paramref name="size"/> bytes and returns a reference to the first one.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal ref byte Reserve(int size)
        {
            int index = Index;
            if ((uint)size > (uint)(Buffer.Length - index))
                Grow(size);
            Index = index + size;
            return ref Unsafe.Add(ref NinoUnsafe.GetArrayDataReference(Buffer), index);
        }

        public void Advance(int count)
        {
            if (count < 0 || Index > Buffer.Length - count)
                throw new ArgumentOutOfRangeException(nameof(count));
            Index += count;
        }

        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            EnsureCapacity(sizeHint);
            return Buffer.AsMemory(Index);
        }

        public Span<byte> GetSpan(int sizeHint = 0)
        {
            EnsureCapacity(sizeHint);
            return Buffer.AsSpan(Index);
        }

        /// <summary>Ensures at least <paramref name="sizeHint"/> (minimum 1) free bytes.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void EnsureCapacity(int sizeHint)
        {
            if (sizeHint < 1)
                sizeHint = 1;
            if (sizeHint > Buffer.Length - Index)
                Grow(sizeHint);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal void Grow(int sizeHint)
        {
            int current = Buffer.Length;
            int needed = checked(Index + sizeHint);
            int newSize = Math.Max(needed, current < DefaultCapacity ? DefaultCapacity : current * 2);
            if ((uint)newSize > MaxArrayLength)
                newSize = Math.Max(needed, MaxArrayLength);

            var newBuffer = ArrayPool<byte>.Shared.Rent(newSize);
            System.Buffer.BlockCopy(Buffer, 0, newBuffer, 0, Index);
            var old = Buffer;
            Buffer = newBuffer;
            if (old.Length != 0)
                ArrayPool<byte>.Shared.Return(old);
        }

        /// <summary>Returns the underlying buffer to the pool. The instance must not be used afterwards.</summary>
        public void Dispose()
        {
            var old = Buffer;
            Buffer = Array.Empty<byte>();
            Index = 0;
            if (old.Length != 0)
                ArrayPool<byte>.Shared.Return(old);
        }
    }
}
