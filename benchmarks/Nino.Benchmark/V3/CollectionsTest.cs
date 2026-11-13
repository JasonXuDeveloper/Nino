using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using BenchmarkDotNet.Configs;
using MemoryPack;
using Nino.Core;

#nullable disable
namespace Nino.Benchmark;

// CollectionsTest.cs's nullable collections ported to Nino 3.3.13 (NuGet), for the v3 build of this project
// (NINO_BENCH_V3=true): only those cases, Nino's and MemoryPack's, with the same data. Only the Nino calls differ, as in
// V3/SimpleTest.cs: NinoSerializer/NinoDeserializer -> NinoGen.Serializer/NinoGen.Deserializer, NinoArrayBufferWriter
// -> ArrayBufferWriter<byte>. Everything else is verbatim.

/// <summary>
/// Nullable values with every fifth one null (<c>long?[]</c>, <c>List&lt;int?&gt;</c>, <c>decimal?[]</c>), against
/// MemoryPack (as <c>SafeNullable&lt;T&gt;</c>, <see cref="NullableFieldsMemoryPack"/>).
/// </summary>
[PayloadColumn]
[HideColumns("StdDev", "RatioSD", "Error")]
[MinColumn, MaxColumn]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[NinoJob]
[GcServer(true)]
[MemoryDiagnoser]
[MarkdownExporterAttribute.GitHub]
public class CollectionsTest : IReadBack
{
    private const int N = 1000;

    private static readonly long?[] NullableLongs = Enumerable.Range(0, N).Select(i => i % 5 == 0 ? null : (long?)i * 31).ToArray();
    private static readonly List<int?> NullableInts = Enumerable.Range(0, N).Select(i => i % 5 == 0 ? null : (int?)i * 7).ToList();
    private static readonly decimal?[] NullableDecimals =
        Enumerable.Range(0, N).Select(i => i % 5 == 0 ? null : (decimal?)i / 8).ToArray();

    private static readonly SafeNullable<long>[] MemoryPackNullableLongs = SafeNullables.From(NullableLongs);
    private static readonly List<SafeNullable<int>> MemoryPackNullableInts = SafeNullables.From(NullableInts);
    private static readonly SafeNullable<decimal>[] MemoryPackNullableDecimals = SafeNullables.From(NullableDecimals);

    private static readonly byte[] NinoNullableLongsBytes = NinoGen.Serializer.Serialize(NullableLongs);
    private static readonly byte[] MemoryPackNullableLongsBytes = MemoryPackSerializer.Serialize(MemoryPackNullableLongs);
    private static readonly byte[] NinoNullableIntsBytes = NinoGen.Serializer.Serialize(NullableInts);
    private static readonly byte[] MemoryPackNullableIntsBytes = MemoryPackSerializer.Serialize(MemoryPackNullableInts);
    private static readonly byte[] NinoNullableDecimalsBytes = NinoGen.Serializer.Serialize(NullableDecimals);
    private static readonly byte[] MemoryPackNullableDecimalsBytes = MemoryPackSerializer.Serialize(MemoryPackNullableDecimals);

    // v3 3.3.13 has no NinoArrayBufferWriter; its IBufferWriter<byte> overload is the reused-buffer path.
    private ArrayBufferWriter<byte> _ninoBuffer;
    private ArrayBufferWriter<byte> _memoryPackBuffer;

    static CollectionsTest()
    {
        BenchmarkPayloadRegistry.Register(nameof(NinoSerializeNullableLongs), NinoNullableLongsBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(MemoryPackSerializeNullableLongs), MemoryPackNullableLongsBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(NinoSerializeNullableInts), NinoNullableIntsBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(MemoryPackSerializeNullableInts), MemoryPackNullableIntsBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(NinoSerializeNullableDecimals), NinoNullableDecimalsBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(MemoryPackSerializeNullableDecimals), MemoryPackNullableDecimalsBytes.Length);
    }

    [GlobalSetup]
    public void Setup()
    {
        _ninoBuffer = new ArrayBufferWriter<byte>(1 << 16);
        _memoryPackBuffer = new ArrayBufferWriter<byte>(1 << 16);
    }

    /// <summary>Verify.cs: MemoryPack's SafeNullable collections as the nullable collections.</summary>
    public object ReadBack(object result, System.Type model) => SafeNullables.ReadBack(result);

    [Benchmark(Baseline = true), BenchmarkCategory("NullableLongsSerialize")]
    public int NinoSerializeNullableLongs()
    {
        _ninoBuffer.ResetWrittenCount();
        NinoGen.Serializer.Serialize(NullableLongs, _ninoBuffer);
        return _ninoBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("NullableLongsSerialize")]
    public int MemoryPackSerializeNullableLongs()
    {
        _memoryPackBuffer.ResetWrittenCount();
        MemoryPackSerializer.Serialize(_memoryPackBuffer, MemoryPackNullableLongs);
        return _memoryPackBuffer.WrittenCount;
    }

    [Benchmark(Baseline = true), BenchmarkCategory("NullableIntsSerialize")]
    public int NinoSerializeNullableInts()
    {
        _ninoBuffer.ResetWrittenCount();
        NinoGen.Serializer.Serialize(NullableInts, _ninoBuffer);
        return _ninoBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("NullableIntsSerialize")]
    public int MemoryPackSerializeNullableInts()
    {
        _memoryPackBuffer.ResetWrittenCount();
        MemoryPackSerializer.Serialize(_memoryPackBuffer, MemoryPackNullableInts);
        return _memoryPackBuffer.WrittenCount;
    }

    [Benchmark(Baseline = true), BenchmarkCategory("NullableDecimalsSerialize")]
    public int NinoSerializeNullableDecimals()
    {
        _ninoBuffer.ResetWrittenCount();
        NinoGen.Serializer.Serialize(NullableDecimals, _ninoBuffer);
        return _ninoBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("NullableDecimalsSerialize")]
    public int MemoryPackSerializeNullableDecimals()
    {
        _memoryPackBuffer.ResetWrittenCount();
        MemoryPackSerializer.Serialize(_memoryPackBuffer, MemoryPackNullableDecimals);
        return _memoryPackBuffer.WrittenCount;
    }

    [Benchmark(Baseline = true), BenchmarkCategory("NullableLongsDeserialize")]
    public long?[] NinoDeserializeNullableLongs()
    {
        NinoGen.Deserializer.Deserialize(NinoNullableLongsBytes, out long?[] ret);
        return ret;
    }

    [Benchmark, BenchmarkCategory("NullableLongsDeserialize")]
    public SafeNullable<long>[] MemoryPackDeserializeNullableLongs() =>
        MemoryPackSerializer.Deserialize<SafeNullable<long>[]>(MemoryPackNullableLongsBytes);

    [Benchmark(Baseline = true), BenchmarkCategory("NullableIntsDeserialize")]
    public List<int?> NinoDeserializeNullableInts()
    {
        NinoGen.Deserializer.Deserialize(NinoNullableIntsBytes, out List<int?> ret);
        return ret;
    }

    [Benchmark, BenchmarkCategory("NullableIntsDeserialize")]
    public List<SafeNullable<int>> MemoryPackDeserializeNullableInts() =>
        MemoryPackSerializer.Deserialize<List<SafeNullable<int>>>(MemoryPackNullableIntsBytes);

    [Benchmark(Baseline = true), BenchmarkCategory("NullableDecimalsDeserialize")]
    public decimal?[] NinoDeserializeNullableDecimals()
    {
        NinoGen.Deserializer.Deserialize(NinoNullableDecimalsBytes, out decimal?[] ret);
        return ret;
    }

    [Benchmark, BenchmarkCategory("NullableDecimalsDeserialize")]
    public SafeNullable<decimal>[] MemoryPackDeserializeNullableDecimals() =>
        MemoryPackSerializer.Deserialize<SafeNullable<decimal>[]>(MemoryPackNullableDecimalsBytes);
}
