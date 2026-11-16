// A BenchGen template (runtimes/csharp/tools/BenchGen), not compiled: MemoryPack's SafeNullable<T> with the copy to and
// from T? in the timed region, for the cases that time the nullable forms ([BenchCase(NullableForms = true)]): what an
// application holding T? pays to write MemoryPack's canonical bytes. It reads MemoryPack's bytes, through MemoryPack's
// buffer; a value with a conversion of its own is written by the class (NullableTest).
using MemoryPack;

namespace Nino.Benchmark.Adapters;

[BenchLibrary("MemoryPackConverted", Order = 3, Slot = 7, Builds = "v4", Like = "MemoryPack", When = "NullableForms",
    Reads = "MemoryPack")]
[BenchArg("TSafe", "{Type:MemoryPackValue}")]
internal static class MemoryPackConvertedAdapter
{
    public static int Serialize<T>(ArrayBufferWriter<byte> buffer, T value)
    {
        buffer.ResetWrittenCount();
        MemoryPackSerializer.Serialize(buffer, SafeNullables.From(value));
        return buffer.WrittenCount;
    }

    public static T Deserialize<T>(byte[] bytes) => NullableForms.ToNullable(MemoryPackSerializer.Deserialize<TSafe>(bytes));
}
