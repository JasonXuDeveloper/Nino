// A BenchGen template (runtimes/csharp/tools/BenchGen), not compiled: LuminPack, through its fastest documented calls:
// into a reused LuminBufferWriter (ResetForReuse per operation: an independent session, Harness/LuminPackBuffers.cs), and
// from bytes into a new object. Its writers are sized up front, as the other libraries' are. A case it cannot run (a bug
// of its 2.0.0-rc.2) is declared with Broken = "LuminPack <kind>: <reason>" (bench-cases.json; the charts show error).
using LuminPack;
using LuminPack.Utility;

namespace Nino.Benchmark.Adapters;

[BenchLibrary("LuminPack", Order = 9, Slot = 9, Builds = "v4")]
internal static class LuminPackAdapter
{
    public static void Setup(out LuminBufferWriter buffer, int capacity)
    {
        buffer = LuminPackBuffers.Rent();
    }

    public static byte[] ToBytes<T>(T value) => LuminPackBuffers.Bytes(value, (v, w) => LuminPackSerializer.Serialize(v, w));

    // [BenchClass(Serialize = "array")]: to a new byte[], the API's own size.
    public static byte[] SerializeToArray<T>(T value) => LuminPackSerializer.Serialize(value);

    public static int Serialize<T>(LuminBufferWriter buffer, T value)
    {
        buffer.ResetForReuse();
        LuminPackSerializer.Serialize(value, buffer);
        return buffer.CurrentIndex;
    }

    public static T Deserialize<T>(byte[] bytes)
    {
        T result = default;
        LuminPackSerializer.Deserialize(bytes, ref result);
        return result;
    }
}
