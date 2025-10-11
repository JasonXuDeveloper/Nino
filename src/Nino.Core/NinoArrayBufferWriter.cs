using System;
using System.Buffers;
using System.Runtime.CompilerServices;

namespace Nino.Core
{
    /// <summary>
    /// A wrapper for <see cref="ArrayBufferWriter{T}"/> that implements <see cref="INinoBufferWriter"/>.
    /// </summary>
    public class NinoArrayBufferWriter : INinoBufferWriter
    {
        private readonly ArrayBufferWriter<byte> _bufferWriter;

        public NinoArrayBufferWriter(int initialSizeHint = 0)
        {
            _bufferWriter = new ArrayBufferWriter<byte>(initialSizeHint);
        }

        public NinoArrayBufferWriter(ArrayBufferWriter<byte> bufferWriter)
        {
            _bufferWriter = bufferWriter;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Advance(int count)
        {
            _bufferWriter.Advance(count);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            return _bufferWriter.GetMemory(sizeHint);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Span<byte> GetSpan(int sizeHint = 0)
        {
            return _bufferWriter.GetSpan(sizeHint);
        }

        public int WrittenCount
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _bufferWriter.WrittenCount;
        }

        public ReadOnlySpan<byte> WrittenSpan
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _bufferWriter.WrittenSpan;
        }

        public ReadOnlyMemory<byte> WrittenMemory
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _bufferWriter.WrittenMemory;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Clear()
        {
            _bufferWriter.Clear();
        }

#if NET8_0_OR_GREATER
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void ResetWrittenCount()
        {
            _bufferWriter.ResetWrittenCount();
        }
#endif
        
        public static implicit operator ArrayBufferWriter<byte>(NinoArrayBufferWriter writer)
        {
            return writer._bufferWriter;
        }
    }
}