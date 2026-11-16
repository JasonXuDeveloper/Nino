using System.Buffers;
using LuminPack.Utility;

namespace Nino.Benchmark;

/// <summary>
/// LuminPack's reusable writers, sized up front as the other libraries' writers are (1 MiB), and the bytes its cases
/// read, written through one. Why sized: LuminPack 1.1.9 writes a list of polymorphic elements past the end of a writer
/// that has to grow (its pooled writers start at 32 KiB): PolymorphicTest's class of 1,000 elements corrupted the
/// native heap through <c>LuminPackSerializer.Serialize(value)</c> from about 400 elements on.
/// </summary>
public static class LuminPackBuffers
{
    private const int Capacity = 1 << 20;

    public delegate void Write<T>(T value, LuminBufferWriter writer);

    /// <summary>A writer of LuminPack's pool with room for <see cref="Capacity"/> bytes; the case's class keeps it.</summary>
    public static LuminBufferWriter Rent()
    {
        var writer = LuminBufferWriterPool.Rent();
        // 1.1.9's writer has no size hint (no IBufferWriter<byte>): loading that many bytes grows it, and ResetCore
        // keeps the storage.
        var room = new ReadOnlySequence<byte>(new byte[Capacity]);
        writer.LoadSequence(in room);
        writer.ResetCore();
        return writer;
    }

    /// <summary>
    /// The bytes <paramref name="write"/> writes of <paramref name="value"/> (<c>LuminPackSerializer.Serialize(v, w)</c>:
    /// a parameter, since a static readonly field cannot be passed by <c>in</c> from a lambda).
    /// </summary>
    public static byte[] Bytes<T>(T value, Write<T> write)
    {
        var writer = Rent();
        write(value, writer);
        byte[] bytes = writer.GetSpan().ToArray();
        LuminBufferWriterPool.Return(writer);
        return bytes;
    }
}
