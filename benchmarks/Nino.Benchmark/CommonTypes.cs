using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using MemoryPack;
using Nino.Core;

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
public partial struct InventoryItem
{
    public int ItemId;
    public short Count;
    public bool Bound;
}

/// <summary>
/// A point of a recorded path: of fixed size but not raw (a value tuple member, laid out in memory with padding the
/// wire leaves out), 4 + 4 + 6 + 1 = 15 bytes, 20 in memory.
/// </summary>
[NinoType]
[MemoryPackable]
public partial struct Waypoint
{
    public float X;
    public float Y;
    public (int Tick, short Zone) Stamp;
    public bool Visited;
}

/// <summary>A game save record: scalars, an enum, a nullable, a struct list, a string dictionary, bytes and strings.</summary>
[NinoType]
[MemoryPackable]
public partial class Player
{
    public int Id;
    public string Name;
    public UnitKind Kind;
    public float Hp;
    public Vector3 Position;
    public int? GuildId;
    public DateTime LastLogin;
    public List<InventoryItem> Items;
    public Dictionary<string, int> Stats;
    public byte[] Avatar;
    public List<string> Friends;

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
public partial class OrderLine
{
    public string Sku;
    public int Quantity;
    public decimal Price;
}

/// <summary>A business record: Guid, DateTimeOffset, decimal, an optional note, a list of classes and a string set.</summary>
[NinoType]
[MemoryPackable]
public partial class Order
{
    public long Id;
    public Guid Customer;
    public DateTimeOffset PlacedAt;
    public decimal Total;
    public string Note;
    public List<OrderLine> Lines;
    public HashSet<string> Tags;

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
public abstract partial class Shape
{
    public int Color;
}

[NinoType]
[MemoryPackable]
public sealed partial class Circle : Shape
{
    public float Radius;
}

[NinoType]
[MemoryPackable]
public sealed partial class Rect : Shape
{
    public float Width, Height;
}

/// <summary>A recursive type: each node has a label and a list of children.</summary>
[NinoType]
[MemoryPackable]
public partial class TreeNode
{
    public int Value;
    public string Label;
    public List<TreeNode> Children;

    public static TreeNode Create(Random r, int depth) => new()
    {
        Value = r.Next(),
        Label = "node" + r.Next(1000),
        Children = depth == 0
            ? new List<TreeNode>()
            : Enumerable.Range(0, 4).Select(_ => Create(r, depth - 1)).ToList()
    };
}
