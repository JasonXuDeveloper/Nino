using System;
using System.Runtime.InteropServices;

namespace Nino.Core
{
    internal unsafe ref struct SpanBufferReader
    {
        private ReadOnlySpan<byte> data;

        public SpanBufferReader(ReadOnlySpan<byte> data)
        {
            if (data == null)
            {
                throw new ArgumentNullException(nameof(data));
            }
            this.data = data;
        }

        public void Get<T>(out T value) where T : unmanaged
        {
            value = MemoryMarshal.Read<T>(data);
            data = data.Slice(sizeof(T));
        }

        public void Get(out bool value)
        {
            value = data[0] != 0;
            data = data.Slice(1);
        }

        public void GetBytes(int length, out ReadOnlySpan<byte> bytes)
        {
            bytes = data.Slice(0, length);
            data = data.Slice(length);
        }
    }
}