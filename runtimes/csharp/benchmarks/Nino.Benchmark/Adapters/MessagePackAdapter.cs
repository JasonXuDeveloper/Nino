// A BenchGen template (runtimes/csharp/tools/BenchGen), not compiled: MessagePack-CSharp, with its default options.
using System.Buffers;
using MessagePack;

namespace Nino.Benchmark.Adapters;

[BenchLibrary("MessagePack", Order = 2, Slot = 0, Builds = "v4")]
internal static class MessagePackAdapter
{
    public static void Setup(out ArrayBufferWriter<byte> buffer, int capacity)
    {
        buffer = new ArrayBufferWriter<byte>(capacity);
    }

    public static byte[] ToBytes<T>(T value) => MessagePackSerializer.Serialize(value);

    public static int Serialize<T>(ArrayBufferWriter<byte> buffer, T value)
    {
        buffer.ResetWrittenCount();
        MessagePackSerializer.Serialize(buffer, value);
        return buffer.WrittenCount;
    }

    public static T Deserialize<T>(byte[] bytes) => MessagePackSerializer.Deserialize<T>(bytes);
}
