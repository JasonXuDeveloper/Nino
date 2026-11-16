// A BenchGen template (runtimes/csharp/tools/BenchGen), not compiled: MemoryPack, in both builds (the v3 run times it
// on the same machine as the v4 run, compare/compare_v3.py).
using System.Buffers;
using MemoryPack;

namespace Nino.Benchmark.Adapters;

[BenchLibrary("MemoryPack", Order = 2, Slot = 1, Builds = "v4 v3")]
internal static class MemoryPackAdapter
{
    public static void Setup(out ArrayBufferWriter<byte> buffer, int capacity)
    {
        buffer = new ArrayBufferWriter<byte>(capacity);
    }

    public static byte[] ToBytes<T>(T value) => MemoryPackSerializer.Serialize(value);

    // [BenchClass(Serialize = "array")]: to a new byte[], the API's own size.
    public static byte[] SerializeToArray<T>(T value) => MemoryPackSerializer.Serialize(value);

    public static int Serialize<T>(ArrayBufferWriter<byte> buffer, T value)
    {
        buffer.ResetWrittenCount();
        MemoryPackSerializer.Serialize(buffer, value);
        return buffer.WrittenCount;
    }

    public static T Deserialize<T>(byte[] bytes) => MemoryPackSerializer.Deserialize<T>(bytes);

    // [BenchCase(InPlace = true)]: its overwrite of the object Setup read first.
    public static T DeserializeInto<T>(byte[] bytes, ref T target)
    {
        MemoryPackSerializer.Deserialize(bytes, ref target);
        return target;
    }
}
