using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Nino.Core
{
    internal ref struct SpanBufferReader
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

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Get<T>(out T value) where T : unmanaged
        {
            value = Unsafe.ReadUnaligned<T>(ref MemoryMarshal.GetReference(data));
            data = data.Slice(Unsafe.SizeOf<T>());
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Get(out bool value)
        {
            value = data[0] != 0;
            data = data.Slice(1);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void GetBytes(int length, out ReadOnlySpan<byte> bytes)
        {
            bytes = data.Slice(0, length);
            data = data.Slice(length);
        }
    }
}