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

/// <summary>
/// The same members as <see cref="SimpleClass"/>, pooled by Nino. Kept out of <see cref="SimpleTest"/>: reusing
/// instances is not what the other serializers are measured doing there, so it gets a table of its own, against
/// MemoryPack's own overwrite (deserialize into an existing instance). MessagePack and protobuf-net have no overwrite
/// (protobuf-net's deserialize into an existing instance merges: it appends to lists), so they read new instances only.
/// </summary>
[NinoType, NinoPooled]
[MemoryPackable]
[MessagePackObject]
[ProtoContract]
public sealed partial class PooledSimpleClass
{
    [Key(0)] [ProtoMember(1)] public int Id;
    [Key(1)] [ProtoMember(2)] public bool Tag;
    [Key(2)] [ProtoMember(3)] public Guid Guid;
    [Key(3)] [ProtoMember(4)] public DateTime CreateTime;
    [Key(4)] [ProtoMember(5)] public string Name { get; set; }
    [Key(5)] [ProtoMember(6)] [NinoUtf8] public string Desc;
    [Key(6)] [ProtoMember(7)] public int[] Numbers { get; set; }
    [Key(7)] [ProtoMember(8)] public List<DateTime> Dates { get; set; }
    [Key(8)] [ProtoMember(9)] public Dictionary<int, string> Map1;
    [Key(9)] [ProtoMember(10)] public Dictionary<int, int> Map2 { get; set; }

    private static partial IEnumerable<PooledSimpleClass> OnNinoPoolWarmup() => null;

    private partial void OnNinoPoolReturn()
    {
    }

    public static PooledSimpleClass From(SimpleClass c) => new()
    {
        Id = c.Id, Tag = c.Tag, Guid = c.Guid, CreateTime = c.CreateTime, Name = c.Name, Desc = c.Desc,
        Numbers = c.Numbers, Dates = c.Dates, Map1 = c.Map1, Map2 = c.Map2,
    };
}

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
