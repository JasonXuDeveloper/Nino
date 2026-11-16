// A BenchGen template (runtimes/csharp/tools/BenchGen), not compiled: Nino v3 3.3.13 from NuGet, for the v3 build
// (NINO_BENCH_V3=true). Its rows keep the name Nino, so a v3 report lines up with a v4 one (compare/compare_v3.py).
// 3.3.13 has no NinoArrayBufferWriter: its IBufferWriter<byte> overload is the reused-buffer path. It has no read into
// an existing object either (InPlace cases read a new one, as MessagePack does), and a case of the version-tolerant
// project calls that project's serializers ([BenchCase(NinoGen = "V3Versioned.NinoGen")]).
using System.Buffers;
using Nino.Core;

namespace Nino.Benchmark.Adapters;

[BenchLibrary("Nino", Order = 0, Slot = 2, Baseline = true, Builds = "v3")]
[BenchArg("NinoGen", "{NinoGen}")]
internal static class NinoV3Adapter
{
    public static void Setup(out ArrayBufferWriter<byte> buffer, int capacity)
    {
        buffer = new ArrayBufferWriter<byte>(capacity);
    }

    public static byte[] ToBytes<T>(T value) => NinoGen.Serializer.Serialize(value);

    public static int Serialize<T>(ArrayBufferWriter<byte> buffer, T value)
    {
        buffer.ResetWrittenCount();
        NinoGen.Serializer.Serialize(value, buffer);
        return buffer.WrittenCount;
    }

    public static T Deserialize<T>(byte[] bytes)
    {
        NinoGen.Deserializer.Deserialize(bytes, out T ret);
        return ret;
    }
}
