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

// FixedSizeTest's types, shared with its v3 port (V3/FixedSizeTest.cs).

/// <summary>Unmanaged members only (40 bytes, no padding): every value serializes to the same number of bytes.</summary>
[NinoType]
[MemoryPackable]
[MessagePackObject]
[ProtoContract]
public partial struct FixedStruct
{
    [Key(0)] [ProtoMember(1)] public int Id;
    [Key(1)] [ProtoMember(2)] public Vector3 Position;
    [Key(2)] [ProtoMember(3)] public long Ticks;
    [Key(3)] [ProtoMember(4)] public Guid Guid;

    public static FixedStruct Create(Random r) => new()
    {
        Id = r.Next(),
        Position = new Vector3(r.NextSingle(), r.NextSingle(), r.NextSingle()),
        Ticks = BenchmarkRandom.Now.AddSeconds(r.Next()).Ticks,
        Guid = BenchmarkRandom.NewGuid(),
    };
}

/// <summary>The members of <see cref="FixedStruct"/> in a sealed class: its size is fixed too (a null flag and 40 bytes).</summary>
[NinoType]
[MemoryPackable]
[MessagePackObject]
[ProtoContract]
public sealed partial class FixedClass
{
    [Key(0)] [ProtoMember(1)] public int Id;
    [Key(1)] [ProtoMember(2)] public Vector3 Position;
    [Key(2)] [ProtoMember(3)] public long Ticks;
    [Key(3)] [ProtoMember(4)] public Guid Guid;

    public static FixedClass Create(Random r) => new()
    {
        Id = r.Next(),
        Position = new Vector3(r.NextSingle(), r.NextSingle(), r.NextSingle()),
        Ticks = BenchmarkRandom.Now.AddSeconds(r.Next()).Ticks,
        Guid = BenchmarkRandom.NewGuid(),
    };
}
