using System;
using System.Buffers;
using System.Runtime.CompilerServices;

namespace Nino.Core
{
    /// <summary>
    /// An extension of <see cref="IBufferWriter{T}"/> that provides additional methods for accessing the written data.
    /// </summary>
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