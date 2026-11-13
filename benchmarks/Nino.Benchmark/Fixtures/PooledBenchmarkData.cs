using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using MemoryPack;
using MessagePack;
using Nino.Core;
using ProtoBuf;

#nullable disable
namespace Nino.Benchmark;

// The pooled types of PooledTest and PooledGraphTest, shared with their v3 ports (V3/PooledTest.cs,
// V3/PooledGraphTest.cs), which have no pool: 3.3.13 has no [NinoPooled].

/// <summary>A pooled leaf: primitives and a list.</summary>
#if NINO_BENCH_V3
[NinoType]
#else
[NinoType, NinoPooled]
#endif
[MemoryPackable]
[MessagePackObject]
[ProtoContract]
public sealed partial class PooledLeaf
{
    [Key(0)] [ProtoMember(1)] public int A;
    [Key(1)] [ProtoMember(2)] public float B;
    [Key(2)] [ProtoMember(3)] public List<int> Items;

#if !NINO_BENCH_V3
    private static partial IEnumerable<PooledLeaf> OnNinoPoolWarmup() => null;

    private partial void OnNinoPoolReturn()
    {
    }
#endif

    public static PooledLeaf Create(Random r) =>
        new() { A = r.Next(), B = r.NextSingle(), Items = Enumerable.Range(0, 8).Select(_ => r.Next()).ToList() };
}

/// <summary>The same as <see cref="PooledLeaf"/>, not pooled.</summary>
[NinoType]
[MemoryPackable]
[MessagePackObject]
[ProtoContract]
public sealed partial class PlainLeaf
{
    [Key(0)] [ProtoMember(1)] public int A;
    [Key(1)] [ProtoMember(2)] public float B;
    [Key(2)] [ProtoMember(3)] public List<int> Items;

    public static PlainLeaf Create(Random r) =>
        new() { A = r.Next(), B = r.NextSingle(), Items = Enumerable.Range(0, 8).Select(_ => r.Next()).ToList() };
}

/// <summary>No strings: primitives, an array, a list, a dictionary, a pooled child and a list of pooled children.</summary>
#if NINO_BENCH_V3
[NinoType]
#else
[NinoType, NinoPooled]
#endif
[MemoryPackable]
[MessagePackObject]
[ProtoContract]
public sealed partial class PooledNode
{
    [Key(0)] [ProtoMember(1)] public int Id;
    [Key(1)] [ProtoMember(2)] public long Ticks;
    [Key(2)] [ProtoMember(3)] public double X;
    [Key(3)] [ProtoMember(4)] public double Y;
    [Key(4)] [ProtoMember(5)] public bool Flag;
    [Key(5)] [ProtoMember(6)] public Guid Guid;
    [Key(6)] [ProtoMember(7)] public int[] Data;
    [Key(7)] [ProtoMember(8)] public List<long> Values;
    [Key(8)] [ProtoMember(9)] public Dictionary<int, int> Map;
    [Key(9)] [ProtoMember(10)] public PooledLeaf Leaf;
    [Key(10)] [ProtoMember(11)] public List<PooledLeaf> Leaves;

#if !NINO_BENCH_V3
    private static partial IEnumerable<PooledNode> OnNinoPoolWarmup() => null;

    private partial void OnNinoPoolReturn()
    {
    }
#endif

    public static PooledNode Create(Random r) => new()
    {
        Id = r.Next(), Ticks = r.NextInt64(), X = r.NextDouble(), Y = r.NextDouble(), Flag = true,
        Guid = BenchmarkRandom.NewGuid(),
        Data = Enumerable.Range(0, 32).Select(_ => r.Next()).ToArray(),
        Values = Enumerable.Range(0, 16).Select(_ => r.NextInt64()).ToList(),
        Map = Enumerable.Range(0, 16).ToDictionary(i => i, _ => r.Next()),
        Leaf = PooledLeaf.Create(r),
        Leaves = Enumerable.Range(0, 8).Select(_ => PooledLeaf.Create(r)).ToList(),
    };
}

/// <summary>
/// The same members as <see cref="SimpleClass"/>, pooled by Nino. Kept out of <see cref="SimpleTest"/>: reusing
/// instances is not what the other serializers are measured doing there, so it gets a table of its own, against
/// MemoryPack's own overwrite (deserialize into an existing instance). MessagePack and protobuf-net have no overwrite
/// (protobuf-net's deserialize into an existing instance merges: it appends to lists), so they read new instances only.
/// </summary>
#if NINO_BENCH_V3
[NinoType]
#else
[NinoType, NinoPooled]
#endif
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

#if !NINO_BENCH_V3
    private static partial IEnumerable<PooledSimpleClass> OnNinoPoolWarmup() => null;

    private partial void OnNinoPoolReturn()
    {
    }
#endif

    public static PooledSimpleClass From(SimpleClass c) => new()
    {
        Id = c.Id, Tag = c.Tag, Guid = c.Guid, CreateTime = c.CreateTime, Name = c.Name, Desc = c.Desc,
        Numbers = c.Numbers, Dates = c.Dates, Map1 = c.Map1, Map2 = c.Map2,
    };
}
