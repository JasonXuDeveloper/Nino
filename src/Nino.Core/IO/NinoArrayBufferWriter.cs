using System;
using System.Buffers;
using System.Runtime.CompilerServices;
using Nino.Core.CompilerServices;

namespace Nino.Core
{
    /// <summary>
    /// A contiguous, growable byte buffer backed by an <see cref="ArrayPool{T}"/>: <see cref="ArrayPool{T}.Shared"/>,
    /// or the pool given to the constructor (for example one of pinned arrays for socket I/O).
    /// <see cref="Writer"/> writes into it directly, so serializing into a reused instance allocates nothing.
    /// Not thread safe: use one instance per thread (or per operation).
    /// </summary>
    public sealed class NinoArrayBufferWriter : IBufferWriter<byte>, IDisposable
    {
        private const int DefaultCapacity = 256;

        // Fields rather than properties: Writer reads both when it is created, reloads Buffer after growing it and
        // stores Index on every flush; a property is only free where the JIT inlines it (not under Mono's interpreter).

        /// <summary>The pooled backing array; replaced when the buffer grows.</summary>
        internal byte[] Buffer;

        /// <summary>Number of bytes written into <see cref="Buffer"/>.</summary>
        internal int Index;

        /// <summary>
        /// <see cref="NinoScratch"/>'s per-thread scratch buffer: <see cref="ScratchIdle"/> or
        /// <see cref="ScratchInUse"/> while it is cached, 0 otherwise (any other instance).
        /// </summary>
        internal byte ScratchState;

        internal const byte ScratchIdle = 1;
        internal const byte ScratchInUse = 2;

        /// <summary>The pool the backing arrays are rented from and returned to.</summary>
        private readonly ArrayPool<byte> _pool;

        public NinoArrayBufferWriter() : this(DefaultCapacity)
        {
        }

        public NinoArrayBufferWriter(int initialCapacity) : this(initialCapacity, ArrayPool<byte>.Shared)
        {
        }

        /// <summary>
        /// A buffer whose backing arrays come from <paramref name="pool"/>: it rents the first one here and on every
        /// growth, and returns the old one on growth and on <see cref="Dispose"/>.
        /// </summary>
        public NinoArrayBufferWriter(int initialCapacity, ArrayPool<byte> pool)
        {
            if (initialCapacity < 0)
                throw new ArgumentOutOfRangeException(nameof(initialCapacity));

            _pool = pool ?? throw new ArgumentNullException(nameof(pool));
            Buffer = initialCapacity == 0 ? Array.Empty<byte>() : pool.Rent(initialCapacity);
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
            get => new(Buffer, 0, Index);
        }

        /// <summary>The bytes written so far.</summary>
        public ReadOnlyMemory<byte> WrittenMemory => new(Buffer, 0, Index);

        /// <summary>Copies the written bytes into a new array.</summary>
        public byte[] ToArray()
        {
            // The written bytes fill the result, including when it is allocated uninitialized.
            int count = Index;
            var result = NinoUnsafe.NewArray<byte>(count, uninitialized: true);
            ref byte destination = ref NinoUnsafe.GetArrayDataReference(result);
            ref byte source = ref NinoUnsafe.GetArrayDataReference(Buffer);
            // Skip the call for short results that Copy handles inline.
            if (NinoUnsafe.Is64Bit && (uint)count <= 64)
                NinoUnsafe.Copy(ref destination, ref source, (uint)count);
            else
                NinoUnsafe.CopyToNew(ref destination, ref source, (uint)count);
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
                buffer = Grow(size);
            Index = index + size;
            return ref Unsafe.Add(ref NinoUnsafe.GetArrayDataReference(buffer), index);
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
        internal byte[] Grow(int sizeHint)
        {
            int current = Buffer.Length;
            int needed = checked(Index + sizeHint);
            int newSize = Math.Max(needed, current < DefaultCapacity ? DefaultCapacity : current * 2);
            if ((uint)newSize > NinoUnsafe.MaxArrayLength)
                newSize = Math.Max(needed, NinoUnsafe.MaxArrayLength);

            var newBuffer = _pool.Rent(newSize);
            var old = Buffer;
            int count = Index;
            if (count != 0)
                NinoUnsafe.CopyToNew(ref NinoUnsafe.GetArrayDataReference(newBuffer),
                    ref NinoUnsafe.GetArrayDataReference(old), (uint)count);
            Buffer = newBuffer;
            if (old.Length != 0)
                _pool.Return(old);
            return newBuffer;
        }

        /// <summary>
        /// Hands the backing array (and so the bytes written) to the caller, who returns it to this buffer's pool,
        /// and continues with an empty one of the same capacity rented in its place. For a write the caller still
        /// reads from after this buffer is reused (<see cref="Internal.NinoStreams"/>: an asynchronous stream write).
        /// </summary>
        internal byte[] TakeBuffer()
        {
            var taken = Buffer;
            Buffer = taken.Length == 0 ? taken : _pool.Rent(taken.Length);
            Index = 0;
            return taken;
        }

        /// <summary>Returns the underlying buffer to the pool. The instance must not be used afterwards.</summary>
        public void Dispose()
        {
            var old = Buffer;
            Buffer = Array.Empty<byte>();
            Index = 0;
            if (old.Length != 0)
                _pool.Return(old);
        }
    }
}
