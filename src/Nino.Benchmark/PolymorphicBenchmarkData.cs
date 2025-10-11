using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using MemoryPack;
using MessagePack;
using Nino.Core;

namespace Nino.Benchmark;

#nullable disable

[NinoType]
[MemoryPackable]
[MessagePackObject]
[MemoryPackUnion(1, typeof(PolymorphicClass))]
[Union(1, typeof(PolymorphicClass))]
public abstract partial class PolymorphicClassBase
{
    [Key(10)]
    public int BaseId;

    public abstract void Write();
}

[MemoryPackable]
[MessagePackObject]
[NinoType]
public sealed partial class PolymorphicClass : PolymorphicClassBase, IPolymorphicWriter
{
    [Key(0)] public int Id;
    [Key(1)] public Guid Card;
    [Key(2)] public bool Tag;
    [Key(4)] public int[] Numbers;
    [Key(5)] public List<int> Numbers2;
    [Key(6)] public Dictionary<int, PolymorphicFooBase> Map1;
    [Key(7)] public HashSet<int> Set;
    [Key(8)] public PolymorphicTransform Transform;

    public static PolymorphicClass Create()
    {
        Random random = Random.Shared;
        return new PolymorphicClass
        {
            Id = random.Next(),
            BaseId = random.Next(),
            Tag = random.Next() % 2 == 0,
            Card = Guid.NewGuid(),
            Numbers = Enumerable.Range(0, 100).Select(_ => random.Next()).ToArray(),
            Numbers2 = Enumerable.Range(0, 100).Select(x => x).ToList(),
            Map1 = new Dictionary<int, PolymorphicFooBase>
            {
                {1, PolymorphicFoo.Create()},
                {2, PolymorphicFoo.Create()},
                {3, PolymorphicFoo.Create()},
                {4, PolymorphicFoo.Create()},
                {5, PolymorphicFoo.Create()}
            },
            Transform = new PolymorphicTransform {X = 1, Y = 2, Z = 3},
            Set = new HashSet<int>(Enumerable.Range(0, 100).Select(x => x))
        };
    }

    public override void Write() => Console.WriteLine("PolymorphicClass says hello!");
}

[NinoType]
[MemoryPackable]
[MemoryPackUnion(1, typeof(PolymorphicFoo))]
[StructLayout(LayoutKind.Explicit)]
[Union(1, typeof(PolymorphicFoo))]
public abstract partial class PolymorphicFooBase
{
    [FieldOffset(0)]
    [Key(0)] public int Id;
}

[NinoType]
[MemoryPackable]
[MessagePackObject]
[StructLayout(LayoutKind.Explicit)]
public sealed partial class PolymorphicFoo : PolymorphicFooBase, IPolymorphicWriter
{
    [Key(2)]
    [FieldOffset(12)]
    public Guid Card;

    public void Write()
    {
        Console.WriteLine("PolymorphicFoo says hello!");
    }

    public static PolymorphicFoo Create()
    {
        return new PolymorphicFoo
        {
            Id = 1000,
            Card = Guid.NewGuid()
        };
    }
}

public interface IPolymorphicWriter
{
    public void Write();
}

[MemoryPackable]
[MessagePackObject]
[NinoType]
public partial struct PolymorphicTransform
{
    [Key(0)] public float X;
    [Key(1)] public float Y;
    [Key(2)] public float Z;
}
