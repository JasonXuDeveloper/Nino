using System;
using System.Buffers;
using System.Linq;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using MemoryPack;
using MessagePack;
using Nino.Core;
using ProtoBuf;

#nullable disable
namespace Nino.Benchmark;

/// <summary>
/// The caller's buffers rather than Nino's own: values written to an <see cref="ArrayBufferWriter{T}"/> (reset per
/// operation, so nothing is allocated), which Nino fills through its external-writer paths (raw blocks straight into
/// the writer's span, other values through its scratch buffer), and an <c>int[]</c> read from a
/// <see cref="ReadOnlySequence{T}"/> of three segments, which Nino first copies into one span. Every serializer gets the
/// same writer and the same three segments: <c>int[1000]</c> and <c>Memory&lt;int&gt;</c> of 1,000 (raw blocks),
/// <c>SimpleClass[100]</c> (a class graph), and the segmented <c>int[1000]</c>. protobuf-net has no serializer for
/// <c>Memory&lt;T&gt;</c>.
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
    private static readonly ReadOnlySequence<byte> MessagePackSegmentedInts;
    private static readonly ReadOnlySequence<byte> ProtobufSegmentedInts;

    // Large enough for every case, so no write grows it once warmed.
    private readonly ArrayBufferWriter<byte> _buffer = new(1 << 20);

    static ExternalBufferTest()
    {
        _ = ProtobufModel.Ready;
        SimpleClasses = Enumerable.Range(0, 100).Select(_ => SimpleClass.Create()).ToArray();

        NinoSegmentedInts = Split(NinoSerializer.Serialize(Ints));
        MemoryPackSegmentedInts = Split(MemoryPackSerializer.Serialize(Ints));
        MessagePackSegmentedInts = Split(MessagePackSerializer.Serialize(Ints));
        ProtobufSegmentedInts = Split(ProtobufModel.Serialize(Ints));

        BenchmarkPayloadRegistry.Register(nameof(NinoSerializeExternalInts), NinoSerializer.Serialize(Ints).Length);
        BenchmarkPayloadRegistry.Register(nameof(MemoryPackSerializeExternalInts), MemoryPackSerializer.Serialize(Ints).Length);
        BenchmarkPayloadRegistry.Register(nameof(MessagePackSerializeExternalInts), MessagePackSerializer.Serialize(Ints).Length);
        BenchmarkPayloadRegistry.Register(nameof(ProtobufSerializeExternalInts), ProtobufModel.Serialize(Ints).Length);
        BenchmarkPayloadRegistry.Register(nameof(NinoSerializeExternalMemory), NinoSerializer.Serialize(MemoryValue).Length);
        BenchmarkPayloadRegistry.Register(nameof(MemoryPackSerializeExternalMemory), MemoryPackSerializer.Serialize(MemoryValue).Length);
        BenchmarkPayloadRegistry.Register(nameof(MessagePackSerializeExternalMemory), MessagePackSerializer.Serialize(MemoryValue).Length);
        BenchmarkPayloadRegistry.Register(nameof(NinoSerializeExternalClasses), NinoSerializer.Serialize(SimpleClasses).Length);
        BenchmarkPayloadRegistry.Register(nameof(MemoryPackSerializeExternalClasses), MemoryPackSerializer.Serialize(SimpleClasses).Length);
        BenchmarkPayloadRegistry.Register(nameof(MessagePackSerializeExternalClasses), MessagePackSerializer.Serialize(SimpleClasses).Length);
        BenchmarkPayloadRegistry.Register(nameof(ProtobufSerializeExternalClasses), ProtobufModel.Serialize(SimpleClasses).Length);
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
        NinoSerializer.Serialize(Ints, _buffer);
        return _buffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("ExternalIntsSerialize")]
    public int MemoryPackSerializeExternalInts()
    {
        _buffer.ResetWrittenCount();
        MemoryPackSerializer.Serialize(_buffer, Ints);
        return _buffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("ExternalIntsSerialize")]
    public int MessagePackSerializeExternalInts()
    {
        _buffer.ResetWrittenCount();
        MessagePackSerializer.Serialize(_buffer, Ints);
        return _buffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("ExternalIntsSerialize")]
    public int ProtobufSerializeExternalInts()
    {
        _buffer.ResetWrittenCount();
        Serializer.Serialize<int[]>(_buffer, Ints);
        return _buffer.WrittenCount;
    }

    [Benchmark(Baseline = true), BenchmarkCategory("ExternalMemorySerialize")]
    public int NinoSerializeExternalMemory()
    {
        _buffer.ResetWrittenCount();
        NinoSerializer.Serialize(MemoryValue, _buffer);
        return _buffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("ExternalMemorySerialize")]
    public int MemoryPackSerializeExternalMemory()
    {
        _buffer.ResetWrittenCount();
        MemoryPackSerializer.Serialize(_buffer, MemoryValue);
        return _buffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("ExternalMemorySerialize")]
    public int MessagePackSerializeExternalMemory()
    {
        _buffer.ResetWrittenCount();
        MessagePackSerializer.Serialize(_buffer, MemoryValue);
        return _buffer.WrittenCount;
    }

    [Benchmark(Baseline = true), BenchmarkCategory("ExternalClassesSerialize")]
    public int NinoSerializeExternalClasses()
    {
        _buffer.ResetWrittenCount();
        NinoSerializer.Serialize(SimpleClasses, _buffer);
        return _buffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("ExternalClassesSerialize")]
    public int MemoryPackSerializeExternalClasses()
    {
        _buffer.ResetWrittenCount();
        MemoryPackSerializer.Serialize(_buffer, SimpleClasses);
        return _buffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("ExternalClassesSerialize")]
    public int MessagePackSerializeExternalClasses()
    {
        _buffer.ResetWrittenCount();
        MessagePackSerializer.Serialize(_buffer, SimpleClasses);
        return _buffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("ExternalClassesSerialize")]
    public int ProtobufSerializeExternalClasses()
    {
        _buffer.ResetWrittenCount();
        Serializer.Serialize<SimpleClass[]>(_buffer, SimpleClasses);
        return _buffer.WrittenCount;
    }

    [Benchmark(Baseline = true), BenchmarkCategory("SegmentedIntsDeserialize")]
    public int[] NinoDeserializeSegmentedInts() => NinoDeserializer.Deserialize<int[]>(NinoSegmentedInts);

    [Benchmark, BenchmarkCategory("SegmentedIntsDeserialize")]
    public int[] MemoryPackDeserializeSegmentedInts() => MemoryPackSerializer.Deserialize<int[]>(MemoryPackSegmentedInts);

    [Benchmark, BenchmarkCategory("SegmentedIntsDeserialize")]
    public int[] MessagePackDeserializeSegmentedInts() => MessagePackSerializer.Deserialize<int[]>(MessagePackSegmentedInts);

    [Benchmark, BenchmarkCategory("SegmentedIntsDeserialize")]
    public int[] ProtobufDeserializeSegmentedInts() => Serializer.Deserialize<int[]>(ProtobufSegmentedInts);
}
