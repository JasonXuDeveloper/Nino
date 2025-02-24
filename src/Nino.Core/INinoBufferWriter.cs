using System;
using System.Buffers;
using System.Runtime.CompilerServices;

namespace Nino.Core
{
    public interface INinoBufferWriter : IBufferWriter<byte>
    {
        int WrittenCount
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get;
        }

        ReadOnlySpan<byte> WrittenSpan
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get;
        }

        ReadOnlyMemory<byte> WrittenMemory
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get;
        }
    }
}