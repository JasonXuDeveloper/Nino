using System;
using System.Collections.Generic;
using System.Linq;
using MemoryPack;
using MessagePack;
using Nino.Core;
using ProtoBuf;

#nullable disable
namespace Nino.Benchmark;

// PooledGraphTest's element, shared with the v3 build of its Leaves cases (V3/PooledGraphTest.cs), which has no pool
// (3.3.13 has no [NinoPooled]).

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
