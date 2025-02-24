using System;
using System.Buffers;
using System.Runtime.CompilerServices;

namespace Nino.Core
{
    public readonly struct NinoArrayBufferWriter : INinoBufferWriter
    {
        private readonly ArrayBufferWriter<byte> _bufferWriter;

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

        public NinoArrayBufferWriter(ArrayBufferWriter<byte> bufferWriter)
        {
            _bufferWriter = bufferWriter;
        }

        public NinoArrayBufferWriter(int initialCapacity)
        {
            _bufferWriter = new ArrayBufferWriter<byte>(initialCapacity);
        }

        public void Advance(int count)
        {
            _bufferWriter.Advance(count);
        }

        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            return _bufferWriter.GetMemory(sizeHint);
        }

        public Span<byte> GetSpan(int sizeHint = 0)
        {
            return _bufferWriter.GetSpan(sizeHint);
        }

        public static implicit operator ArrayBufferWriter<byte>(NinoArrayBufferWriter writer) => writer._bufferWriter;
        public static implicit operator NinoArrayBufferWriter(ArrayBufferWriter<byte> writer) => new(writer);
    }
}