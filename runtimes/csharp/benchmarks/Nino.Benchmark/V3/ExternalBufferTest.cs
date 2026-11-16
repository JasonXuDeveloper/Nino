using System;
using System.Buffers;
using System.Linq;
using BenchmarkDotNet.Configs;
using MemoryPack;

#nullable disable
namespace Nino.Benchmark;

// Cases/Scenarios/ExternalBufferTest.cs ported to Nino 3.3.13 (NuGet), for the v3 build of this project
// (NINO_BENCH_V3=true): the same data, method names and job, Nino's and MemoryPack's cases. Only the Nino calls differ,
// as in V3/SimpleTest.cs, and two cases take v3's own way where it has no overload for the type:
//  - Memory<int>: v3's generator rejects it (CS8377: it is not unmanaged, and v3 has no formatter for it), but writes
//    Serialize(Span<T>, IBufferWriter<byte>) for unmanaged T: the case writes MemoryValue.Span, the same 1,000 ints.
//  - ReadOnlySequence<byte>: v3 reads a ReadOnlySpan<byte> only, so the case copies the segments into one span of a
//    reused array first, as v4 does inside its own overload.

/// <summary>
/// ExternalBufferTest's values written by Nino v3 to the caller's <see cref="ArrayBufferWriter{T}"/> (reset per
/// operation) and an <c>int[]</c> read from a <see cref="ReadOnlySequence{T}"/> of three segments, against MemoryPack:
/// <c>int[1000]</c>, <c>Memory&lt;int&gt;</c> of 1,000, <c>SimpleClass[100]</c> and the segmented <c>int[1000]</c>.
/// </summary>
[PayloadColumn]
[HideColumns("StdDev", "RatioSD", "Error")]
[MinColumn, MaxColumn]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[NinoJob]
[GcServer(true)]
[MemoryDiagnoser]
[MarkdownExporterAttribute.GitHub]
public class ExternalBufferTest
{
    private const int N = 1000;

    private static readonly int[] Ints = Enumerable.Range(0, N).Select(i => i * 7919).ToArray();
    private static readonly Memory<int> MemoryValue = Enumerable.Range(0, N).ToArray();
    private static readonly SimpleClass[] SimpleClasses;

    private static readonly ReadOnlySequence<byte> NinoSegmentedInts;
    private static readonly ReadOnlySequence<byte> MemoryPackSegmentedInts;

    // Large enough for every case, so no write grows it once warmed.
    private readonly ArrayBufferWriter<byte> _buffer = new(1 << 20);
    private readonly byte[] _segmentScratch = new byte[1 << 16];

    static ExternalBufferTest()
    {
        SimpleClasses = Enumerable.Range(0, 100).Select(_ => SimpleClass.Create()).ToArray();

        NinoSegmentedInts = Split(NinoGen.Serializer.Serialize(Ints));
        MemoryPackSegmentedInts = Split(MemoryPackSerializer.Serialize(Ints));

        BenchmarkPayloadRegistry.Register(nameof(NinoSerializeExternalInts), NinoGen.Serializer.Serialize(Ints).Length);
        BenchmarkPayloadRegistry.Register(nameof(MemoryPackSerializeExternalInts), MemoryPackSerializer.Serialize(Ints).Length);
        BenchmarkPayloadRegistry.Register(nameof(NinoSerializeExternalMemory), NinoGen.Serializer.Serialize(MemoryValue.Span).Length);
        BenchmarkPayloadRegistry.Register(nameof(MemoryPackSerializeExternalMemory), MemoryPackSerializer.Serialize(MemoryValue).Length);
        BenchmarkPayloadRegistry.Register(nameof(NinoSerializeExternalClasses), NinoGen.Serializer.Serialize(SimpleClasses).Length);
        BenchmarkPayloadRegistry.Register(nameof(MemoryPackSerializeExternalClasses), MemoryPackSerializer.Serialize(SimpleClasses).Length);
    }

    /// <summary>Three segments of about a third of <paramref name="bytes"/> each, each in an array of its own.</summary>
    private static ReadOnlySequence<byte> Split(byte[] bytes)
    {
        int third = bytes.Length / 3;
        var first = new Segment(bytes.AsMemory(0, third).ToArray(), 0);
        var second = first.Append(bytes.AsMemory(third, third).ToArray());
        var last = second.Append(bytes.AsMemory(2 * third).ToArray());
        return new ReadOnlySequence<byte>(first, 0, last, last.Memory.Length);
    }

    private sealed class Segment : ReadOnlySequenceSegment<byte>
    {
        public Segment(byte[] bytes, long runningIndex)
        {
            Memory = bytes;
            RunningIndex = runningIndex;
        }

        public Segment Append(byte[] bytes)
        {
            var next = new Segment(bytes, RunningIndex + Memory.Length);
            Next = next;
            return next;
        }
    }

    [Benchmark(Baseline = true), BenchmarkCategory("ExternalIntsSerialize")]
    public int NinoSerializeExternalInts()
    {
        _buffer.ResetWrittenCount();
        NinoGen.Serializer.Serialize(Ints, _buffer);
        return _buffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("ExternalIntsSerialize")]
    public int MemoryPackSerializeExternalInts()
    {
        _buffer.ResetWrittenCount();
        MemoryPackSerializer.Serialize(_buffer, Ints);
        return _buffer.WrittenCount;
    }

    [Benchmark(Baseline = true), BenchmarkCategory("ExternalMemorySerialize")]
    public int NinoSerializeExternalMemory()
    {
        _buffer.ResetWrittenCount();
        NinoGen.Serializer.Serialize(MemoryValue.Span, _buffer);
        return _buffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("ExternalMemorySerialize")]
    public int MemoryPackSerializeExternalMemory()
    {
        _buffer.ResetWrittenCount();
        MemoryPackSerializer.Serialize(_buffer, MemoryValue);
        return _buffer.WrittenCount;
    }

    [Benchmark(Baseline = true), BenchmarkCategory("ExternalClassesSerialize")]
    public int NinoSerializeExternalClasses()
    {
        _buffer.ResetWrittenCount();
        NinoGen.Serializer.Serialize(SimpleClasses, _buffer);
        return _buffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("ExternalClassesSerialize")]
    public int MemoryPackSerializeExternalClasses()
    {
        _buffer.ResetWrittenCount();
        MemoryPackSerializer.Serialize(_buffer, SimpleClasses);
        return _buffer.WrittenCount;
    }

    [Benchmark(Baseline = true), BenchmarkCategory("SegmentedIntsDeserialize")]
    public int[] NinoDeserializeSegmentedInts()
    {
        // v3 reads a span only: the segments are copied into one first (into a reused array, as v4 copies them).
        var bytes = _segmentScratch.AsSpan(0, (int)NinoSegmentedInts.Length);
        NinoSegmentedInts.CopyTo(bytes);
        NinoGen.Deserializer.Deserialize(bytes, out int[] ret);
        return ret;
    }

    [Benchmark, BenchmarkCategory("SegmentedIntsDeserialize")]
    public int[] MemoryPackDeserializeSegmentedInts() => MemoryPackSerializer.Deserialize<int[]>(MemoryPackSegmentedInts);
}

