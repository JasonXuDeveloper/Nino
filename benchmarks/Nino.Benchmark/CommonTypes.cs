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

/// <summary>A small struct with padding (4 + 2 + 1 bytes of members in 8).</summary>
[NinoType]
[MemoryPackable]
[MessagePackObject]
[ProtoContract]
public partial struct InventoryItem
{
    [Key(0)] [ProtoMember(1)] public int ItemId;
    [Key(1)] [ProtoMember(2)] public short Count;
    [Key(2)] [ProtoMember(3)] public bool Bound;
}

/// <summary>
/// A point of a recorded path: of fixed size but not raw (a value tuple member, laid out in memory with padding the
/// wire leaves out), 4 + 4 + 6 + 1 = 15 bytes, 20 in memory.
/// </summary>
[NinoType]
[MemoryPackable]
[MessagePackObject]
[ProtoContract]
public partial struct Waypoint
{
    [Key(0)] [ProtoMember(1)] public float X;
    [Key(1)] [ProtoMember(2)] public float Y;
    [Key(2)] [ProtoMember(3)] public (int Tick, short Zone) Stamp;
    [Key(3)] [ProtoMember(4)] public bool Visited;
}

/// <summary>An abstract base with two sealed subtypes, read and written through the base.</summary>
[NinoType]
[MemoryPackable]
[MemoryPackUnion(0, typeof(Circle))]
[MemoryPackUnion(1, typeof(Rect))]
[MessagePackObject]
[Union(0, typeof(Circle))]
[Union(1, typeof(Rect))]
[ProtoContract]
[ProtoInclude(100, typeof(Circle))]
[ProtoInclude(101, typeof(Rect))]
public abstract partial class Shape
{
    [Key(0)] [ProtoMember(1)] public int Color;
}

[NinoType]
[MemoryPackable]
[MessagePackObject]
[ProtoContract]
public sealed partial class Circle : Shape
{
    [Key(1)] [ProtoMember(1)] public float Radius;
}

[NinoType]
[MemoryPackable]
[MessagePackObject]
[ProtoContract]
public sealed partial class Rect : Shape
{
    [Key(1)] [ProtoMember(1)] public float Width;
    [Key(2)] [ProtoMember(2)] public float Height;
}

/// <summary>
/// A recursive type: each node has a label and a list of children. Not a protobuf contract: protobuf cannot tell an
/// empty list from none, so the leaves' empty <see cref="Children"/> would read back as null.
/// </summary>
[NinoType]
[MemoryPackable]
[MessagePackObject]
public partial class TreeNode
{
    [Key(0)] public int Value;
    [Key(1)] public string Label;
    [Key(2)] public List<TreeNode> Children;

    public static TreeNode Create(Random r, int depth) => new()
    {
        Value = r.Next(),
        Label = "node" + r.Next(1000),
        Children = depth == 0
            ? new List<TreeNode>()
            : Enumerable.Range(0, 4).Select(_ => Create(r, depth - 1)).ToList()
    };
}
