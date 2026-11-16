using System;
using System.Collections.Generic;
using System.Linq;
using BenchmarkDotNet.Configs;
using MemoryPack;
using MessagePack;
using Nino.Core;
using ProtoBuf;

#nullable disable
namespace Nino.Benchmark;

[HideColumns("StdDev", "RatioSD", "Error")]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[NinoJob]
[GcServer(true)]
[MemoryDiagnoser]
[MarkdownExporterAttribute.GitHub]
public class PooledTest
{
    private static readonly PooledSimpleClass Value = PooledSimpleClass.From(SimpleClass.Create());
    private static readonly byte[] NinoData = NinoSerializer.Serialize(Value);
    private static readonly byte[] MemoryPackData = MemoryPackSerializer.Serialize(Value);
    private static readonly byte[] MessagePackData = MessagePackSerializer.Serialize(Value);
    private static readonly byte[] ProtobufData = ProtobufModel.Serialize(Value);

    private PooledSimpleClass _memoryPackTarget;

    [GlobalSetup]
    public void Setup() => _memoryPackTarget = MemoryPackSerializer.Deserialize<PooledSimpleClass>(MemoryPackData);

    [Benchmark(Baseline = true), BenchmarkCategory("PooledDeserialize")]
    public PooledSimpleClass NinoDeserializeNew()
    {
        // Nothing is ever returned here, so the pool stays empty and every call creates the graph.
        return NinoDeserializer.Deserialize<PooledSimpleClass>(NinoData);
    }

    [Benchmark, BenchmarkCategory("PooledDeserialize")]
    public int NinoDeserializePooled()
    {
        var value = NinoDeserializer.Deserialize<PooledSimpleClass>(NinoData);
        int id = value.Id;
        value.ReturnToPool();
        return id;
    }

    [Benchmark, BenchmarkCategory("PooledDeserialize")]
    public PooledSimpleClass MemoryPackDeserializeNew() => MemoryPackSerializer.Deserialize<PooledSimpleClass>(MemoryPackData);

    [Benchmark, BenchmarkCategory("PooledDeserialize")]
    public PooledSimpleClass MessagePackDeserializeNew() => MessagePackSerializer.Deserialize<PooledSimpleClass>(MessagePackData);

    [Benchmark, BenchmarkCategory("PooledDeserialize")]
    public PooledSimpleClass ProtobufDeserializeNew() =>
        Serializer.Deserialize<PooledSimpleClass>(new ReadOnlySpan<byte>(ProtobufData));

    [Benchmark, BenchmarkCategory("PooledDeserialize")]
    public PooledSimpleClass MemoryPackDeserializeOverwrite()
    {
        MemoryPackSerializer.Deserialize(MemoryPackData, ref _memoryPackTarget);
        return _memoryPackTarget;
    }
}
