using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using MemoryPack;
using Nino.Core;

#nullable disable
namespace Nino.Benchmark;

/// <summary>
/// Lists of 1,000 tuples of 8 bytes in memory with padding, (byte, int) and (short, int): 5 and 6 bytes as blocks,
/// moved by the 64-byte permutes (AVX-512 VBMI, Arm64 table lookups), which no case of the suite reaches. The
/// suite's job, so a full run's numbers are comparable with the suite's; a probe, so only a filter naming it runs it.
/// </summary>
[HideColumns("StdDev", "RatioSD", "Error")]
[MinColumn, MaxColumn]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[NinoJob]
[GcServer(true)]
[MemoryDiagnoser]
[MarkdownExporterAttribute.GitHub]
public class SmallTupleProbe
{
    private const int N = 1000;
    private static readonly List<(byte, int)> ByteInts = Enumerable.Range(0, N).Select(i => ((byte)i, i * 7)).ToList();
    private static readonly List<(short, int)> ShortInts = Enumerable.Range(0, N).Select(i => ((short)i, i * 7)).ToList();

    private static readonly byte[] NinoByteIntsBytes = NinoSerializer.Serialize(ByteInts);
    private static readonly byte[] MemoryPackByteIntsBytes = MemoryPackSerializer.Serialize(ByteInts);
    private static readonly byte[] NinoShortIntsBytes = NinoSerializer.Serialize(ShortInts);
    private static readonly byte[] MemoryPackShortIntsBytes = MemoryPackSerializer.Serialize(ShortInts);

    private readonly NinoArrayBufferWriter _ninoBuffer = new(1 << 16);
    private readonly ArrayBufferWriter<byte> _memoryPackBuffer = new(1 << 16);

    [Benchmark(Baseline = true), BenchmarkCategory("ByteIntSerialize")]
    public int NinoSerializeByteInts()
    {
        _ninoBuffer.ResetWrittenCount();
        NinoSerializer.Serialize(ByteInts, _ninoBuffer);
        return _ninoBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("ByteIntSerialize")]
    public int MemoryPackSerializeByteInts()
    {
        _memoryPackBuffer.ResetWrittenCount();
        MemoryPackSerializer.Serialize(_memoryPackBuffer, ByteInts);
        return _memoryPackBuffer.WrittenCount;
    }

    [Benchmark(Baseline = true), BenchmarkCategory("ByteIntDeserialize")]
    public List<(byte, int)> NinoDeserializeByteInts() => NinoDeserializer.Deserialize<List<(byte, int)>>(NinoByteIntsBytes);

    [Benchmark, BenchmarkCategory("ByteIntDeserialize")]
    public List<(byte, int)> MemoryPackDeserializeByteInts() =>
        MemoryPackSerializer.Deserialize<List<(byte, int)>>(MemoryPackByteIntsBytes);

    [Benchmark(Baseline = true), BenchmarkCategory("ShortIntSerialize")]
    public int NinoSerializeShortInts()
    {
        _ninoBuffer.ResetWrittenCount();
        NinoSerializer.Serialize(ShortInts, _ninoBuffer);
        return _ninoBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("ShortIntSerialize")]
    public int MemoryPackSerializeShortInts()
    {
        _memoryPackBuffer.ResetWrittenCount();
        MemoryPackSerializer.Serialize(_memoryPackBuffer, ShortInts);
        return _memoryPackBuffer.WrittenCount;
    }

    [Benchmark(Baseline = true), BenchmarkCategory("ShortIntDeserialize")]
    public List<(short, int)> NinoDeserializeShortInts() => NinoDeserializer.Deserialize<List<(short, int)>>(NinoShortIntsBytes);

    [Benchmark, BenchmarkCategory("ShortIntDeserialize")]
    public List<(short, int)> MemoryPackDeserializeShortInts() =>
        MemoryPackSerializer.Deserialize<List<(short, int)>>(MemoryPackShortIntsBytes);
}
