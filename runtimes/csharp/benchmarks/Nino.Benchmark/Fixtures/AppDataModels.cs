using System;
using System.Collections.Generic;
using System.Linq;
using LuminPack.Attribute;
using MemoryPack;
using MessagePack;
using Nino.Benchmark.Workloads;
using Nino.Core;
using ProtoBuf;

#nullable disable
namespace Nino.Benchmark.AppData;

// The TypeScript benchmark's scenarios (runtimes/typescript/bench, Program.cs), as AppDataTest's models: the shapes of
// application data a web client reads, each scenario's values as one array. The values are the TypeScript data's
// first variant (seed 42), made in the same order.

/// <summary>A value behind a type id: two levels below the base (Dog, then Puppy), and a sibling.</summary>
[NinoType]
[MemoryPackable, LuminPackable]
[MemoryPackUnion(0, typeof(Dog)), LuminPackUnion(0, typeof(Dog))]
[MemoryPackUnion(1, typeof(Puppy)), LuminPackUnion(1, typeof(Puppy))]
[MemoryPackUnion(2, typeof(Cat)), LuminPackUnion(2, typeof(Cat))]
[MessagePackObject]
[Union(0, typeof(Dog))]
[Union(1, typeof(Puppy))]
[Union(2, typeof(Cat))]
[ProtoContract]
[ProtoInclude(100, typeof(Dog))]
[ProtoInclude(101, typeof(Cat))]
public abstract partial class Animal
{
    [Key(0)] [ProtoMember(1)] public string Name;
}

[NinoType]
[MemoryPackable, LuminPackable]
[MessagePackObject]
[ProtoContract]
[ProtoInclude(100, typeof(Puppy))]
public partial class Dog : Animal
{
    [Key(1)] [ProtoMember(1)] public int Bones;
}

[NinoType]
[MemoryPackable, LuminPackable]
[MessagePackObject]
[ProtoContract]
public sealed partial class Puppy : Dog
{
    [Key(2)] [ProtoMember(1)] public bool Sleepy;
}

[NinoType]
[MemoryPackable, LuminPackable]
[MessagePackObject]
[ProtoContract]
public sealed partial class Cat : Animal
{
    [Key(1)] [ProtoMember(1)] public float Lives;
}

/// <summary>A typical record: numbers, short text, a small array, a map and a subtype behind a type id.</summary>
[NinoType]
[MemoryPackable, LuminPackable]
[MessagePackObject]
[ProtoContract]
public sealed partial class Player
{
    [Key(0)] [ProtoMember(1)] public long Id;
    [Key(1)] [ProtoMember(2)] public string Name;
    [Key(2)] [ProtoMember(3)] public int Level;
    [Key(3)] [ProtoMember(4)] public double Score;
    [Key(4)] [ProtoMember(5)] public bool Online;
    [Key(5)] [ProtoMember(6)] public Vec3 Position;
    [Key(6)] [ProtoMember(7)] public int[] Unlocked;
    [Key(7)] [ProtoMember(8)] public Dictionary<string, int> Inventory;
    [Key(8)] [ProtoMember(9)] public Animal Pet;
}

/// <summary>Values behind a type id.</summary>
[NinoType]
[MemoryPackable, LuminPackable]
[MessagePackObject]
[ProtoContract]
public sealed partial class Zoo
{
    [Key(0)] [ProtoMember(1)] public List<Animal> Animals;
}

[NinoType]
[MemoryPackable, LuminPackable]
[MessagePackObject]
[ProtoContract]
public sealed partial class Order
{
    [Key(0)] [ProtoMember(1)] public int Id;
    [Key(1)] [ProtoMember(2)] public string Sku;
    [Key(2)] [ProtoMember(3)] public string Customer;
    [Key(3)] [ProtoMember(4)] public int Quantity;
    [Key(4)] [ProtoMember(5)] public double Price;
    [Key(5)] [ProtoMember(6)] public bool Paid;
    [Key(6)] [ProtoMember(7)] public byte Status;
}

/// <summary>A list of small records.</summary>
[NinoType]
[MemoryPackable, LuminPackable]
[MessagePackObject]
[ProtoContract]
public sealed partial class OrderBook
{
    [Key(0)] [ProtoMember(1)] public List<Order> Orders;
}

/// <summary>Mostly text, some of it not Latin-1.</summary>
[NinoType]
[MemoryPackable, LuminPackable]
[MessagePackObject]
[ProtoContract]
public sealed partial class Article
{
    [Key(0)] [ProtoMember(1)] public string Title;
    [Key(1)] [ProtoMember(2)] public string Author;
    [Key(2)] [ProtoMember(3)] public string Body;
    [Key(3)] [ProtoMember(4)] public string Summary;
    [Key(4)] [ProtoMember(5)] public List<string> Tags;
    [Key(5)] [ProtoMember(6)] public int Views;
}

/// <summary>Arrays of small structs and numbers.</summary>
[NinoType]
[MemoryPackable, LuminPackable]
[MessagePackObject]
[ProtoContract]
public sealed partial class Mesh
{
    [Key(0)] [ProtoMember(1)] public string Name;
    [Key(1)] [ProtoMember(2)] public Vec3[] Vertices;
    [Key(2)] [ProtoMember(3)] public Vec3[] Normals;
    [Key(3)] [ProtoMember(4)] public int[] Indices;
}

[NinoType]
[MemoryPackable, LuminPackable]
[MessagePackObject]
[ProtoContract]
public sealed partial class Samples
{
    [Key(0)] [ProtoMember(1)] public double[] Values;
    [Key(1)] [ProtoMember(2)] public int[] Counts;
    [Key(2)] [ProtoMember(3)] public float[] Weights;
}

/// <summary>Maps keyed and valued by short text.</summary>
[NinoType]
[MemoryPackable, LuminPackable]
[MessagePackObject]
[ProtoContract]
public sealed partial class Lookup
{
    [Key(0)] [ProtoMember(1)] public Dictionary<string, int> Counts;
    [Key(1)] [ProtoMember(2)] public Dictionary<int, string> Names;
}

[NinoType]
[MemoryPackable, LuminPackable]
[MessagePackObject]
[ProtoContract]
public sealed partial class Event
{
    [Key(0)] [ProtoMember(1)] public long At;
    [Key(1)] [ProtoMember(2)] public long UserId;
    [Key(2)] [ProtoMember(3)] public int Kind;
    [Key(3)] [ProtoMember(4)] public long[] Refs;
}

/// <summary>Each scenario's values, as the TypeScript benchmark's first variant makes them.</summary>
public static class AppDataValues
{
    public static readonly Player[] Players;
    public static readonly Zoo[] Zoos;
    public static readonly OrderBook[] OrderBooks;
    public static readonly Article[] Articles;
    public static readonly Mesh[] Meshes;
    public static readonly Samples[] SampleSets;
    public static readonly Lookup[] Lookups;
    public static readonly Event[] Events;

    static AppDataValues()
    {
        var rng = new Random(42);
        string Word(int n) => new string(Enumerable.Range(0, n).Select(_ => (char)('a' + rng.Next(26))).ToArray());
        string Sentence(int words) => string.Join(" ", Enumerable.Range(0, words).Select(_ => Word(rng.Next(2, 10))));
        Vec3 V() => new() { X = (float)rng.NextDouble(), Y = (float)rng.NextDouble(), Z = (float)rng.NextDouble() };
        Animal Pet(int i) => (i % 3) switch
        {
            0 => new Dog { Name = "pet" + i, Bones = i },
            1 => new Cat { Name = "pet" + i, Lives = 9 },
            _ => new Puppy { Name = "pet" + i, Bones = i, Sleepy = i % 2 == 0 },
        };

        Players = Enumerable.Range(0, 1000).Select(i => new Player
        {
            Id = 1_000_000_000L + i, Name = "Player " + i, Level = rng.Next(100), Score = rng.NextDouble() * 1e4,
            Online = i % 2 == 0, Position = V(), Unlocked = Enumerable.Range(0, 8).Select(_ => rng.Next(1000)).ToArray(),
            Inventory = new Dictionary<string, int> { ["sword"] = rng.Next(5), ["shield"] = rng.Next(5) }, Pet = Pet(i),
        }).ToArray();
        Zoos = Enumerable.Range(0, 50).Select(i => new Zoo
        {
            Animals = Enumerable.Range(0, 100).Select(k => Pet(i + k)).ToList(),
        }).ToArray();
        OrderBooks = Enumerable.Range(0, 100).Select(b => new OrderBook
        {
            Orders = Enumerable.Range(0, 50).Select(i => new Order
            {
                Id = b * 1000 + i, Sku = "SKU-" + rng.Next(100000).ToString("D6"), Customer = Word(6) + " " + Word(8),
                Quantity = rng.Next(1, 20), Price = Math.Round(rng.NextDouble() * 1000, 2), Paid = rng.Next(2) == 0,
                Status = (byte)rng.Next(4),
            }).ToList(),
        }).ToArray();
        Articles = Enumerable.Range(0, 200).Select(i => new Article
        {
            Title = Sentence(8), Author = Word(5) + " " + Word(7), Body = Sentence(300),
            Summary = i % 2 == 0 ? "数据序列化的性能测试，包含中文文本。" + Sentence(5) : "Café crème — résumé naïve " + Sentence(10),
            Tags = Enumerable.Range(0, 5).Select(_ => Word(6)).ToList(), Views = rng.Next(),
        }).ToArray();
        Meshes = Enumerable.Range(0, 20).Select(i => new Mesh
        {
            Name = "mesh" + i,
            Vertices = Enumerable.Range(0, 500).Select(_ => V()).ToArray(),
            Normals = Enumerable.Range(0, 500).Select(_ => V()).ToArray(),
            Indices = Enumerable.Range(0, 1500).Select(_ => rng.Next(500)).ToArray(),
        }).ToArray();
        SampleSets = Enumerable.Range(0, 20).Select(_ => new Samples
        {
            Values = Enumerable.Range(0, 1000).Select(_ => rng.NextDouble() * 1e6).ToArray(),
            Counts = Enumerable.Range(0, 1000).Select(_ => rng.Next(-1000000, 1000000)).ToArray(),
            Weights = Enumerable.Range(0, 1000).Select(_ => (float)rng.NextDouble()).ToArray(),
        }).ToArray();
        Lookups = Enumerable.Range(0, 50).Select(_ => new Lookup
        {
            Counts = Enumerable.Range(0, 100).ToDictionary(i => "key_" + Word(6) + i, _ => rng.Next(100000)),
            Names = Enumerable.Range(0, 100).ToDictionary(i => i * 7, _ => Word(10)),
        }).ToArray();
        Events = Enumerable.Range(0, 1000).Select(i => new Event
        {
            At = 1760790645123L * 10000 + i, UserId = rng.NextInt64(1L << 40), Kind = rng.Next(10),
            Refs = Enumerable.Range(0, 4).Select(_ => rng.NextInt64()).ToArray(),
        }).ToArray();
    }
}
