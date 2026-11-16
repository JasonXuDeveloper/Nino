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

// ScatteredTest's item, shared with its v3 port (V3/ScatteredTest.cs).

/// <summary>A small class with a string, for <see cref="ScatteredTest"/>.</summary>
[NinoType]
[MemoryPackable]
[MessagePackObject]
[ProtoContract]
public sealed partial class ScatteredItem
{
    [Key(0)] [ProtoMember(1)] public int Id;
    [Key(1)] [ProtoMember(2)] public string Name;
    [Key(2)] [ProtoMember(3)] public long Value;
}
