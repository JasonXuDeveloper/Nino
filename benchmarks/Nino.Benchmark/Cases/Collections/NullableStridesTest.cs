using System;
using System.Linq;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using Nino.Core;

#nullable disable
namespace Nino.Benchmark;

/// <summary>A user's struct of five longs: its T? is 48 bytes (a flag padded to 8, then 40).</summary>
public struct FiveLongs
{
    public long A, B, C, D, E;
}

/// <summary>
/// For runner A/B only (claude/simd-coverage, #279): raw T? runs whose stride no vector clears across elements
/// without AVX-512 VBMI, 1,000 elements with every fifth one null, written to a reused buffer writer and read into
/// new arrays: <c>Guid?</c> (20 bytes: one 32-byte vector an element with AVX2) and <c>FiveLongs?</c> (48 bytes).
/// </summary>
[HideColumns("StdDev", "RatioSD", "Error")]
[MinColumn, MaxColumn]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[NinoJob]
[GcServer(true)]
[MemoryDiagnoser]
[MarkdownExporterAttribute.GitHub]
public class NullableStridesTest
{
    private const int N = 1000;

    private static readonly Guid?[] NullableGuids = Enumerable.Range(0, N)
        .Select(i => i % 5 == 0 ? null : (Guid?)new Guid(i, (short)i, 3, 4, 5, 6, 7, 8, 9, 10, 11)).ToArray();

    private static readonly FiveLongs?[] NullableFiveLongs = Enumerable.Range(0, N)
        .Select(i => i % 5 == 0 ? null : (FiveLongs?)new FiveLongs { A = i, B = -i, C = i * 3L, D = 7, E = i }).ToArray();

    private static readonly byte[] NinoNullableGuidsBytes = NinoSerializer.Serialize(NullableGuids);
    private static readonly byte[] NinoNullableFiveLongsBytes = NinoSerializer.Serialize(NullableFiveLongs);

    private readonly NinoArrayBufferWriter _ninoBuffer = new(1 << 20);

    [Benchmark(Baseline = true), BenchmarkCategory("NullableGuidsSerialize")]
    public int NinoSerializeNullableGuids()
    {
        _ninoBuffer.ResetWrittenCount();
        NinoSerializer.Serialize(NullableGuids, _ninoBuffer);
        return _ninoBuffer.WrittenCount;
    }

    [Benchmark(Baseline = true), BenchmarkCategory("NullableGuidsDeserialize")]
    public Guid?[] NinoDeserializeNullableGuids() => NinoDeserializer.Deserialize<Guid?[]>(NinoNullableGuidsBytes);

    [Benchmark(Baseline = true), BenchmarkCategory("NullableFiveLongsSerialize")]
    public int NinoSerializeNullableFiveLongs()
    {
        _ninoBuffer.ResetWrittenCount();
        NinoSerializer.Serialize(NullableFiveLongs, _ninoBuffer);
        return _ninoBuffer.WrittenCount;
    }

    [Benchmark(Baseline = true), BenchmarkCategory("NullableFiveLongsDeserialize")]
    public FiveLongs?[] NinoDeserializeNullableFiveLongs() =>
        NinoDeserializer.Deserialize<FiveLongs?[]>(NinoNullableFiveLongsBytes);
}
