// A BenchGen template (runtimes/csharp/tools/BenchGen), not compiled: Nino v4. The methods' bodies become the timed
// methods of every class that declares cases ([BenchCase]); benchmarks/README.md, "Cases and adapters", says how.
using Nino.Core;

namespace Nino.Benchmark.Adapters;

[BenchLibrary("Nino", Order = 0, Slot = 2, Baseline = true, Builds = "v4")]
internal static class NinoAdapter
{
    public static void Setup(out NinoArrayBufferWriter buffer)
    {
        buffer = new NinoArrayBufferWriter(320 * 1024);
    }

    public static byte[] ToBytes<T>(T value) => NinoSerializer.Serialize(value);

    public static int Serialize<T>(NinoArrayBufferWriter buffer, T value)
    {
        buffer.ResetWrittenCount();
        NinoSerializer.Serialize(value, buffer);
        return buffer.WrittenCount;
    }

    public static T Deserialize<T>(byte[] bytes) => NinoDeserializer.Deserialize<T>(bytes);
}
