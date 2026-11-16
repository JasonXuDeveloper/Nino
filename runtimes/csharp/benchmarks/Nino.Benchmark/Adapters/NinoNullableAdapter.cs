// A BenchGen template (runtimes/csharp/tools/BenchGen), not compiled: Nino writing a case's values as NinoNullable<T>
// (its own nullable, with a flag Nino can copy as a block) instead of T?, for the cases that time the nullable forms
// ([BenchCase(NullableForms = true)]); the value is the class's NinoNullableValue field. Nino's templates, through
// Nino's buffer.
namespace Nino.Benchmark.Adapters;

[BenchLibrary("NinoNullable", Order = 1, Slot = 6, Builds = "v4", Like = "Nino", When = "NullableForms")]
internal static class NinoNullableAdapter;
