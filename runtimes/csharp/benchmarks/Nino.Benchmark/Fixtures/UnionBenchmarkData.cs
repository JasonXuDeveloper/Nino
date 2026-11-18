#if NET11_0_OR_GREATER && !NINO_BENCH_V3
#nullable disable
using Nino.Core;

namespace Nino.Benchmark
{
    /// <summary>
    /// A C# union (C# 15, .NET 11) of two numbers, a string, a raw struct and a class (PolymorphicTest's UnionArray):
    /// Nino writes the case's type id, then the case's value as its own formatter does (wire-format spec §7.12), and
    /// nino-gen writes it as a union in TypeScript, Go and Rust. Only the .NET 11 builds (the benchmark and the schema's
    /// net11.0 build, which nino-gen reads) compile it: Unity's C# 9 build and the v3 build do not.
    /// </summary>
    [NinoType]
    public union UnionValue(int, long, string, UnionPoint, UnionItem);

    /// <summary>A raw struct case of <see cref="UnionValue"/>.</summary>
    [NinoType]
    public struct UnionPoint
    {
        public int X;
        public int Y;
        public float Z;
    }

    /// <summary>A class case of <see cref="UnionValue"/>.</summary>
    [NinoType]
    public sealed class UnionItem
    {
        public int Id;
        public string Name;
        public double Score;
    }
}
#endif
