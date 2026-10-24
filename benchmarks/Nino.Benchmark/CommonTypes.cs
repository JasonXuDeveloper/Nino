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

public enum UnitKind : byte
{
    Warrior,
    Mage,
    Archer
}

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

/// <summary>A game save record: scalars, an enum, a nullable, a struct list, a string dictionary, bytes and strings.</summary>
[NinoType]
[MemoryPackable]
[MessagePackObject]
[ProtoContract]
public partial class Player
{
    [Key(0)] [ProtoMember(1)] public int Id;
    [Key(1)] [ProtoMember(2)] public string Name;
    [Key(2)] [ProtoMember(3)] public UnitKind Kind;
    [Key(3)] [ProtoMember(4)] public float Hp;
    [Key(4)] [ProtoMember(5)] public Vector3 Position;
    [Key(5)] [ProtoMember(6)] public int? GuildId;
    [Key(6)] [ProtoMember(7)] public DateTime LastLogin;
    [Key(7)] [ProtoMember(8)] public List<InventoryItem> Items;
    [Key(8)] [ProtoMember(9)] public Dictionary<string, int> Stats;
    [Key(9)] [ProtoMember(10)] public byte[] Avatar;
    [Key(10)] [ProtoMember(11)] public List<string> Friends;

    public static Player Create(int i) => new()
    {
        Id = i,
        Name = "player" + i,
        Kind = (UnitKind)(i % 3),
        Hp = i * 1.5f,
        Position = new Vector3(i, i + 1, i + 2),
        GuildId = i % 2 == 0 ? i : null,
        LastLogin = new DateTime(2026, 1, 1).AddMinutes(i),
        Items = Enumerable.Range(0, 20)
            .Select(j => new InventoryItem { ItemId = j, Count = (short)j, Bound = j % 2 == 0 }).ToList(),
        Stats = new Dictionary<string, int> { ["str"] = i, ["dex"] = i + 1, ["int"] = i + 2, ["vit"] = i + 3 },
        Avatar = Enumerable.Range(0, 64).Select(j => (byte)(i + j)).ToArray(),
        Friends = Enumerable.Range(0, 5).Select(j => "friend" + (i + j)).ToList(),
    };
}

/// <summary>A line of an <see cref="Order"/> (not sealed, as most classes are).</summary>
[NinoType]
[MemoryPackable]
[MessagePackObject]
[ProtoContract]
public partial class OrderLine
{
    [Key(0)] [ProtoMember(1)] public string Sku;
    [Key(1)] [ProtoMember(2)] public int Quantity;
    [Key(2)] [ProtoMember(3)] public decimal Price;
}

/// <summary>A business record: Guid, DateTimeOffset, decimal, an optional note, a list of classes and a string set.</summary>
[NinoType]
[MemoryPackable]
[MessagePackObject]
[ProtoContract]
public partial class Order
{
    [Key(0)] [ProtoMember(1)] public long Id;
    [Key(1)] [ProtoMember(2)] public Guid Customer;
    [Key(2)] [ProtoMember(3)] public DateTimeOffset PlacedAt;
    [Key(3)] [ProtoMember(4)] public decimal Total;
    [Key(4)] [ProtoMember(5)] public string Note;
    [Key(5)] [ProtoMember(6)] public List<OrderLine> Lines;
    [Key(6)] [ProtoMember(7)] public HashSet<string> Tags;

    public static Order Create(int i, Random r) => new()
    {
        Id = i,
        Customer = new Guid(Enumerable.Range(0, 16).Select(_ => (byte)r.Next(256)).ToArray()),
        PlacedAt = new DateTimeOffset(2026, 3, 4, 5, 6, 7, TimeSpan.FromHours(8)).AddMinutes(i),
        Total = 123.45m * i,
        Note = i % 3 == 0 ? null : "leave at the door",
        Lines = Enumerable.Range(0, 5)
            .Select(j => new OrderLine { Sku = "SKU-" + j, Quantity = j + 1, Price = 9.99m * j }).ToList(),
        Tags = new HashSet<string> { "gift", "express" },
    };
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
