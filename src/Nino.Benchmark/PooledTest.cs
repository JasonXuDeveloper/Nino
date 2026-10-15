using System;
using System.Collections.Generic;
using System.Linq;
using BenchmarkDotNet.Configs;
using MemoryPack;
using Nino.Core;

#nullable disable
namespace Nino.Benchmark;

/// <summary>
/// The same members as <see cref="SimpleClass"/>, pooled by Nino. Kept out of <see cref="SimpleTest"/>: reusing
/// instances is not what the other serializers are measured doing there, so it gets a table of its own, against
/// MemoryPack's own overwrite (deserialize into an existing instance).
/// </summary>
[NinoType, NinoPooled]
[MemoryPackable]
public sealed partial class PooledSimpleClass
{
    public int Id;
    public bool Tag;
    public Guid Guid;
    public DateTime CreateTime;
    public string Name { get; set; }
    [NinoUtf8] public string Desc;
    public int[] Numbers { get; set; }
    public List<DateTime> Dates { get; set; }
    public Dictionary<int, string> Map1;
    public Dictionary<int, int> Map2 { get; set; }

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
[MemoryDiagnoser]
[MarkdownExporterAttribute.GitHub]
public class PooledTest
{
    private static readonly PooledSimpleClass Value = PooledSimpleClass.From(SimpleClass.Create());
    private static readonly byte[] NinoData = NinoSerializer.Serialize(Value);
    private static readonly byte[] MemoryPackData = MemoryPackSerializer.Serialize(Value);

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
    public PooledSimpleClass MemoryPackDeserializeOverwrite()
    {
        MemoryPackSerializer.Deserialize(MemoryPackData, ref _memoryPackTarget);
        return _memoryPackTarget;
    }
}
