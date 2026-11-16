// A BenchGen template (runtimes/csharp/tools/BenchGen), not compiled: MemoryPack given T? as it is, which copies the
// uninitialized padding of an unmanaged T? into its bytes (MemoryPackAdapter writes the class's MemoryPackValue, a
// SafeNullable<T> copy), for the cases that time the nullable forms ([BenchCase(NullableForms = true)]). MemoryPack's
// templates, through MemoryPack's buffer.
namespace Nino.Benchmark.Adapters;

[BenchLibrary("MemoryPackUnsafe", Order = 4, Slot = 8, Builds = "v4", Like = "MemoryPack", When = "NullableForms")]
internal static class MemoryPackUnsafeAdapter;
