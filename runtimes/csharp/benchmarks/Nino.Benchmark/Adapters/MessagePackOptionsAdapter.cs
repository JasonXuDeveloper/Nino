// A BenchGen template (runtimes/csharp/tools/BenchGen), not compiled: MessagePack with options of the case's
// ([BenchCase(MessagePackOptions = ...)]: resolvers its standard options lack, such as the immutable collections'), in
// place of MessagePackAdapter.cs for those cases; its methods have MessagePack's names and buffer.
using MessagePack;

namespace Nino.Benchmark.Adapters;

[BenchLibrary("MessagePack", Order = 5, Slot = 0, Builds = "v4", Like = "MessagePack", When = "MessagePackOptions")]
[BenchArg("options", "{MessagePackOptions}")]
internal static class MessagePackOptionsAdapter
{
    public static byte[] ToBytes<T>(T value) => MessagePackSerializer.Serialize(value, options);

    public static byte[] SerializeToArray<T>(T value) => MessagePackSerializer.Serialize(value, options);

    public static int Serialize<T>(ArrayBufferWriter<byte> buffer, T value)
    {
        buffer.ResetWrittenCount();
        MessagePackSerializer.Serialize(buffer, value, options);
        return buffer.WrittenCount;
    }

    public static T Deserialize<T>(byte[] bytes) => MessagePackSerializer.Deserialize<T>(bytes, options);
}
