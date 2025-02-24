using System;
using System.Buffers;

namespace Nino.Core
{
    public interface INinoBufferWriter: IBufferWriter<byte>
    {
        int WrittenCount { get; }
        ReadOnlySpan<byte> WrittenSpan { get; }
        ReadOnlyMemory<byte> WrittenMemory { get; }
    }
}