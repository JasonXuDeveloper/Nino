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

        // Fields rather than properties: Writer reads both when it is created, reloads Buffer after growing it and
        // stores Index on every flush; a property is only free where the JIT inlines it (not under Mono's interpreter).

        /// <summary>The pooled backing array; replaced when the buffer grows.</summary>
        internal byte[] Buffer;

        /// <summary>Number of bytes written into <see cref="Buffer"/>.</summary>
        internal int Index;

        /// <summary>
        /// <see cref="NinoSerializer"/>'s per-thread scratch buffer: <see cref="ScratchIdle"/> or
        /// <see cref="ScratchInUse"/> while it is cached, 0 otherwise (any other instance).
        /// </summary>
        internal byte ScratchState;

        internal const byte ScratchIdle = 1;
        internal const byte ScratchInUse = 2;

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

            // Every byte is overwritten by the copy: a memmove of the written bytes (Index never exceeds the buffer's
            // length), where Buffer.BlockCopy would check both arrays' types and bounds first.
            int count = Index;
            var result = NinoUnsafe.AllocateUninitializedArray<byte>(count);
            Unsafe.CopyBlockUnaligned(ref NinoUnsafe.GetArrayDataReference(result),
                ref NinoUnsafe.GetArrayDataReference(Buffer), (uint)count);
            return result;
        }

        /// <summary>Resets the written count without clearing or releasing the buffer.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void ResetWrittenCount() => Index = 0;

        /// <summary>Reserves <paramref name="size"/> bytes and returns a reference to the first one.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal ref byte Reserve(int size)
        {
            // The array in a local: read again after the store to Index, the field would be loaded twice (the JIT
            // cannot tell the store does not change it).
            var buffer = Buffer;
            int index = Index;
            if ((uint)size > (uint)(buffer.Length - index))
                buffer = GrowFor(size);
            Index = index + size;
            return ref Unsafe.Add(ref NinoUnsafe.GetArrayDataReference(buffer), index);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private byte[] GrowFor(int size)
        {
            Grow(size);
            return Buffer;
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
