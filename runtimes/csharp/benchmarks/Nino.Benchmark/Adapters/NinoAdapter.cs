// A BenchGen template (runtimes/csharp/tools/BenchGen), not compiled: Nino v4. The methods' bodies become the timed
// methods of every class that declares cases ([BenchCase]); benchmarks/README.md, "Cases and adapters", says how.
using Nino.Core;

namespace Nino.Benchmark.Adapters;

[BenchLibrary("Nino", Order = 0, Slot = 2, Baseline = true, Builds = "v4")]
internal static class NinoAdapter
{
    public static void Setup(out NinoArrayBufferWriter buffer, int capacity)
    {
        buffer = new NinoArrayBufferWriter(capacity);
    }

    public static byte[] ToBytes<T>(T value) => NinoSerializer.Serialize(value);

    // [BenchClass(Serialize = "array")]: to a new byte[], the API's own size.
    public static byte[] SerializeToArray<T>(T value) => NinoSerializer.Serialize(value);

    public static int Serialize<T>(NinoArrayBufferWriter buffer, T value)
    {
        buffer.ResetWrittenCount();
        NinoSerializer.Serialize(value, buffer);
        return buffer.WrittenCount;
    }

    public static T Deserialize<T>(byte[] bytes) => NinoDeserializer.Deserialize<T>(bytes);

    // [BenchCase(InPlace = true)]: into the object Setup read first.
    public static T DeserializeInto<T>(byte[] bytes, ref T target)
    {
        NinoDeserializer.Deserialize(bytes, ref target);
        return target;
    }
}
