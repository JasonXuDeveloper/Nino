using System;
using System.Linq;
using BenchmarkDotNet.Configs;

#nullable disable
namespace Nino.Benchmark;

/// <summary>
/// Arrays Nino copies as blocks, the other platforms' benchmark cases, serialized to a reused buffer writer and
/// deserialized into new instances:
///  - Unity's (BurstBenchmark, the kernels that pack and unpack padded elements): arrays of (int, long), (byte, int),
///    (double, long, double, float, byte) and <see cref="PaddedRaw"/> (short, long) of 32, 1,000 and 100,000
///    elements, the padding in memory left out on the wire; and 1,000 objects of a generic [NinoType] against its
///    non-generic twin (<see cref="Box{T}"/> over long, <see cref="LongBox"/>);
///  - Rust's: 10,000 doubles and 10,000 ints.
/// Against MemoryPack (which copies the tuples with their padding), MessagePack, protobuf-net and LuminPack. The timed
/// methods are RawArraysTest.Cases.g.cs (Adapters/, runtimes/csharp/tools/BenchGen); the v3 build times Nino 3.3.13
/// and MemoryPack on the same values.
/// </summary>
[BenchCase("IntLongPairs32")]
[BenchCase("IntLongPairs1000")]
[BenchCase("IntLongPairs100000", Quick = true)]
[BenchCase("ByteIntPairs32")]
[BenchCase("ByteIntPairs1000")]
[BenchCase("ByteIntPairs100000")]
[BenchCase("FiveTuples32")]
[BenchCase("FiveTuples1000")]
[BenchCase("FiveTuples100000")]
[BenchCase("PaddedRaws32")]
[BenchCase("PaddedRaws1000", Quick = true)]
[BenchCase("PaddedRaws100000")]
[BenchCase("Doubles10000")]
[BenchCase("Ints10000")]
[BenchCase("GenericBoxes")]
[BenchCase("LongBoxes")]
[BenchClass(Buffer = "4 << 20", Bytes = "{Library}{Case}Bytes", Order = "case")]
[PayloadColumn]
[HideColumns("StdDev", "RatioSD", "Error")]
[MinColumn, MaxColumn]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[NinoJob]
[GcServer(true)]
[MemoryDiagnoser]
[MarkdownExporterAttribute.GitHub]
public partial class RawArraysTest
{
    private static readonly Random Random = new(5);

    private static (int, long)[] IntLongPairs(int n) =>
        Enumerable.Range(0, n).Select(_ => (Random.Next(), (long)Random.Next() << 13)).ToArray();

    private static (byte, int)[] ByteIntPairs(int n) =>
        Enumerable.Range(0, n).Select(_ => ((byte)Random.Next(), Random.Next())).ToArray();

    private static (double, long, double, float, byte)[] FiveTuples(int n) => Enumerable.Range(0, n)
        .Select(_ => (Random.NextDouble(), (long)Random.Next(), Random.NextDouble(), (float)Random.NextDouble(),
            (byte)Random.Next()))
        .ToArray();

    private static PaddedRaw[] PaddedRaws(int n) =>
        Enumerable.Range(0, n).Select(i => new PaddedRaw { A = (short)i, B = Random.Next() }).ToArray();

    private static readonly (int, long)[] IntLongPairs32 = IntLongPairs(32);
    private static readonly (int, long)[] IntLongPairs1000 = IntLongPairs(1000);
    private static readonly (int, long)[] IntLongPairs100000 = IntLongPairs(100000);
    private static readonly (byte, int)[] ByteIntPairs32 = ByteIntPairs(32);
    private static readonly (byte, int)[] ByteIntPairs1000 = ByteIntPairs(1000);
    private static readonly (byte, int)[] ByteIntPairs100000 = ByteIntPairs(100000);
    private static readonly (double, long, double, float, byte)[] FiveTuples32 = FiveTuples(32);
    private static readonly (double, long, double, float, byte)[] FiveTuples1000 = FiveTuples(1000);
    private static readonly (double, long, double, float, byte)[] FiveTuples100000 = FiveTuples(100000);
    private static readonly PaddedRaw[] PaddedRaws32 = PaddedRaws(32);
    private static readonly PaddedRaw[] PaddedRaws1000 = PaddedRaws(1000);
    private static readonly PaddedRaw[] PaddedRaws100000 = PaddedRaws(100000);
    private static readonly double[] Doubles10000 = Enumerable.Range(0, 10000).Select(i => i * 1.5).ToArray();
    private static readonly int[] Ints10000 = Enumerable.Range(0, 10000).Select(i => i * 37 - 5000).ToArray();

    private static readonly Box<long>[] GenericBoxes = Enumerable.Range(0, 1000)
        .Select(i => new Box<long> { A = i, B = 2L * i, C = 3L * i, D = 4L * i }).ToArray();

    private static readonly LongBox[] LongBoxes = Enumerable.Range(0, 1000)
        .Select(i => new LongBox { A = i, B = 2L * i, C = 3L * i, D = 4L * i }).ToArray();
}
