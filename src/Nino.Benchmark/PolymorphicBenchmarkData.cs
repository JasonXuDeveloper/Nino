using System;
using System.Collections.Generic;
using System.Linq;
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
    [Key(1)] public bool Tag;
    [Key(2)] public int[] Numbers;
    [Key(3)] public List<PolymorphicFooBase> Numbers2;
    [Key(4)] public Dictionary<int, PolymorphicFooBase> Map1;
    [Key(5)] public FooD Foo;
    [Key(6)] public HashSet<int> Set;

    public static PolymorphicClass Create()
    {
        Random random = Random.Shared;
        return new PolymorphicClass
        {
            Id = random.Next(),
            BaseId = random.Next(),
            Tag = random.Next() % 2 == 0,
            Numbers = Enumerable.Range(0, 100).Select(_ => random.Next()).ToArray(),
            Numbers2 = new List<PolymorphicFooBase>(),
            Map1 = new Dictionary<int, PolymorphicFooBase>
            {
                {1, FooA.Create()},
                {2, FooB.Create()},
                {3, FooC.Create()},
                {4, FooD.Create()},
                {5, FooA.Create()},
                {6, FooB.Create()},
                {7, FooC.Create()},
                {8, FooD.Create()},
                {9, FooA.Create()},
                {10, FooB.Create()},
                {11, FooC.Create()},
                {12, FooD.Create()}
            },
            Foo = new FooD
            {
                Id = 1000,
                Card = new PolymorphicTransform(random.Next(), random.Next(), random.Next()),
                CardB = new PolymorphicTransform(random.Next(), random.Next(), random.Next()),
                CardC = new PolymorphicTransform(random.Next(), random.Next(), random.Next()),
                CardD = new PolymorphicTransform(random.Next(), random.Next(), random.Next())
            },
            Set = new HashSet<int>(Enumerable.Range(0, 100).Select(_ => random.Next()).ToList())
        };
    }

    public override void Write() => Console.WriteLine("PolymorphicClass says hello!");
}

[NinoType]
[MemoryPackable]
[MemoryPackUnion(1, typeof(FooA))]
[MemoryPackUnion(2, typeof(FooB))]
[MemoryPackUnion(3, typeof(FooC))]
[MemoryPackUnion(4, typeof(FooD))]
[MemoryPackUnion(5, typeof(FooE))]
[MemoryPackUnion(6, typeof(FooF))]
[MemoryPackUnion(7, typeof(FooG))]
[MemoryPackUnion(8, typeof(FooH))]
[MemoryPackUnion(9, typeof(FooI))]
[MemoryPackUnion(10, typeof(FooJ))]
[MemoryPackUnion(11, typeof(FooK))]
[MemoryPackUnion(12, typeof(FooL))]
[MemoryPackUnion(13, typeof(FooM))]
[MemoryPackUnion(14, typeof(FooN))]
[MemoryPackUnion(15, typeof(FooO))]
[MemoryPackUnion(16, typeof(FooP))]
[MemoryPackUnion(17, typeof(FooQ))]
[MemoryPackUnion(18, typeof(FooR))]
[MemoryPackUnion(19, typeof(FooS))]
[MemoryPackUnion(20, typeof(FooZ))]
[Union(1, typeof(FooA))]
[Union(2, typeof(FooB))]
[Union(3, typeof(FooC))]
[Union(4, typeof(FooD))]
[Union(5, typeof(FooE))]
[Union(6, typeof(FooF))]
[Union(7, typeof(FooG))]
[Union(8, typeof(FooH))]
[Union(9, typeof(FooI))]
[Union(10, typeof(FooJ))]
[Union(11, typeof(FooK))]
[Union(12, typeof(FooL))]
[Union(13, typeof(FooM))]
[Union(14, typeof(FooN))]
[Union(15, typeof(FooO))]
[Union(16, typeof(FooP))]
[Union(17, typeof(FooQ))]
[Union(18, typeof(FooR))]
[Union(19, typeof(FooS))]
[Union(20, typeof(FooZ))]
public abstract partial class PolymorphicFooBase
{
    [Key(0)] public int Id;
}

[NinoType]
[MemoryPackable]
[MessagePackObject]
public partial class FooA : PolymorphicFooBase, IPolymorphicWriter
{
    [Key(1)] public PolymorphicTransform Card;

    public void Write() => Console.WriteLine("FooA says hello!");

    public static FooA Create()
    {
        return new FooA
        {
            Id = 1000,
            Card = new PolymorphicTransform { X = 1, Y = 2, Z = 3 }
        };
    }
}

[NinoType]
[MemoryPackable]
[MessagePackObject]
public partial class FooB : FooA
{
    [Key(2)] public PolymorphicTransform CardB;

    public static new FooB Create()
    {
        return new FooB
        {
            Id = 1000,
            Card = new PolymorphicTransform { X = 1, Y = 2, Z = 3 },
            CardB = new PolymorphicTransform { X = 1, Y = 2, Z = 3 }
        };
    }
}

[NinoType]
[MemoryPackable]
[MessagePackObject]
public partial class FooC : FooB
{
    [Key(3)] public PolymorphicTransform CardC;

    public static new FooC Create()
    {
        return new FooC
        {
            Id = 1000,
            Card = new PolymorphicTransform { X = 1, Y = 2, Z = 3 },
            CardB = new PolymorphicTransform { X = 1, Y = 2, Z = 3 },
            CardC = new PolymorphicTransform { X = 1, Y = 2, Z = 3 }
        };
    }
}

[NinoType]
[MemoryPackable]
[MessagePackObject]
public partial class FooD : FooC
{
    [Key(4)] public PolymorphicTransform CardD;

    public static new FooD Create()
    {
        return new FooD
        {
            Id = 1000,
            Card = new PolymorphicTransform { X = 1, Y = 2, Z = 3 },
            CardB = new PolymorphicTransform { X = 1, Y = 2, Z = 3 },
            CardC = new PolymorphicTransform { X = 1, Y = 2, Z = 3 },
            CardD = new PolymorphicTransform { X = 1, Y = 2, Z = 3 }
        };
    }
}

[NinoType]
[MemoryPackable]
[MessagePackObject]
public partial class FooE : FooD
{
    public static new FooE Create()
    {
        return new FooE
        {
            Id = 1000,
            Card = new PolymorphicTransform { X = 1, Y = 2, Z = 3 },
            CardB = new PolymorphicTransform { X = 1, Y = 2, Z = 3 },
            CardC = new PolymorphicTransform { X = 1, Y = 2, Z = 3 },
            CardD = new PolymorphicTransform { X = 1, Y = 2, Z = 3 }
        };
    }
}

[NinoType]
[MemoryPackable]
[MessagePackObject]
public partial class FooF : FooE
{
    public static new FooF Create()
    {
        return new FooF
        {
            Id = 1000,
            Card = new PolymorphicTransform { X = 1, Y = 2, Z = 3 },
            CardB = new PolymorphicTransform { X = 1, Y = 2, Z = 3 },
            CardC = new PolymorphicTransform { X = 1, Y = 2, Z = 3 },
            CardD = new PolymorphicTransform { X = 1, Y = 2, Z = 3 }
        };
    }
}

[NinoType]
[MemoryPackable]
[MessagePackObject]
public partial class FooG : FooF
{
    public static new FooG Create()
    {
        return new FooG
        {
            Id = 1000,
            Card = new PolymorphicTransform { X = 1, Y = 2, Z = 3 },
            CardB = new PolymorphicTransform { X = 1, Y = 2, Z = 3 },
            CardC = new PolymorphicTransform { X = 1, Y = 2, Z = 3 },
            CardD = new PolymorphicTransform { X = 1, Y = 2, Z = 3 }
        };
    }
}

[NinoType]
[MemoryPackable]
[MessagePackObject]
public partial class FooH : FooG
{
    public static new FooH Create()
    {
        return new FooH
        {
            Id = 1000,
            Card = new PolymorphicTransform { X = 1, Y = 2, Z = 3 },
            CardB = new PolymorphicTransform { X = 1, Y = 2, Z = 3 },
            CardC = new PolymorphicTransform { X = 1, Y = 2, Z = 3 },
            CardD = new PolymorphicTransform { X = 1, Y = 2, Z = 3 }
        };
    }
}

[NinoType]
[MemoryPackable]
[MessagePackObject]
public partial class FooI : FooH
{
    public static new FooI Create()
    {
        return new FooI
        {
            Id = 1000,
            Card = new PolymorphicTransform { X = 1, Y = 2, Z = 3 },
            CardB = new PolymorphicTransform { X = 1, Y = 2, Z = 3 },
            CardC = new PolymorphicTransform { X = 1, Y = 2, Z = 3 },
            CardD = new PolymorphicTransform { X = 1, Y = 2, Z = 3 }
        };
    }
}

[NinoType]
[MemoryPackable]
[MessagePackObject]
public partial class FooJ : FooH
{
    public static new FooJ Create()
    {
        return new FooJ
        {
            Id = 1000,
            Card = new PolymorphicTransform { X = 1, Y = 2, Z = 3 },
            CardB = new PolymorphicTransform { X = 1, Y = 2, Z = 3 },
            CardC = new PolymorphicTransform { X = 1, Y = 2, Z = 3 },
            CardD = new PolymorphicTransform { X = 1, Y = 2, Z = 3 }
        };
    }
}

[NinoType]
[MemoryPackable]
[MessagePackObject]
public partial class FooK : FooH
{
    public static new FooK Create()
    {
        return new FooK
        {
            Id = 1000,
            Card = new PolymorphicTransform { X = 1, Y = 2, Z = 3 },
            CardB = new PolymorphicTransform { X = 1, Y = 2, Z = 3 },
            CardC = new PolymorphicTransform { X = 1, Y = 2, Z = 3 },
            CardD = new PolymorphicTransform { X = 1, Y = 2, Z = 3 }
        };
    }
}

[NinoType]
[MemoryPackable]
[MessagePackObject]
public partial class FooL : FooH
{
    public static new FooL Create()
    {
        return new FooL
        {
            Id = 1000,
            Card = new PolymorphicTransform { X = 1, Y = 2, Z = 3 },
            CardB = new PolymorphicTransform { X = 1, Y = 2, Z = 3 },
            CardC = new PolymorphicTransform { X = 1, Y = 2, Z = 3 },
            CardD = new PolymorphicTransform { X = 1, Y = 2, Z = 3 }
        };
    }
}

[NinoType]
[MemoryPackable]
[MessagePackObject]
public partial class FooM : FooH
{
    public static new FooM Create()
    {
        return new FooM
        {
            Id = 1000,
            Card = new PolymorphicTransform { X = 1, Y = 2, Z = 3 },
            CardB = new PolymorphicTransform { X = 1, Y = 2, Z = 3 },
            CardC = new PolymorphicTransform { X = 1, Y = 2, Z = 3 },
            CardD = new PolymorphicTransform { X = 1, Y = 2, Z = 3 }
        };
    }
}

[NinoType]
[MemoryPackable]
[MessagePackObject]
public partial class FooN : FooH
{
    public static new FooN Create()
    {
        return new FooN
        {
            Id = 1000,
            Card = new PolymorphicTransform { X = 1, Y = 2, Z = 3 },
            CardB = new PolymorphicTransform { X = 1, Y = 2, Z = 3 },
            CardC = new PolymorphicTransform { X = 1, Y = 2, Z = 3 },
            CardD = new PolymorphicTransform { X = 1, Y = 2, Z = 3 }
        };
    }
}

[NinoType]
[MemoryPackable]
[MessagePackObject]
public partial class FooO : FooH
{
    public static new FooO Create()
    {
        return new FooO
        {
            Id = 1000,
            Card = new PolymorphicTransform { X = 1, Y = 2, Z = 3 },
            CardB = new PolymorphicTransform { X = 1, Y = 2, Z = 3 },
            CardC = new PolymorphicTransform { X = 1, Y = 2, Z = 3 },
            CardD = new PolymorphicTransform { X = 1, Y = 2, Z = 3 }
        };
    }
}

[NinoType]
[MemoryPackable]
[MessagePackObject]
public partial class FooP : FooH
{
    public static new FooP Create()
    {
        return new FooP
        {
            Id = 1000,
            Card = new PolymorphicTransform { X = 1, Y = 2, Z = 3 },
            CardB = new PolymorphicTransform { X = 1, Y = 2, Z = 3 },
            CardC = new PolymorphicTransform { X = 1, Y = 2, Z = 3 },
            CardD = new PolymorphicTransform { X = 1, Y = 2, Z = 3 }
        };
    }
}

[NinoType]
[MemoryPackable]
[MessagePackObject]
public partial class FooQ : FooH
{
    public static new FooQ Create()
    {
        return new FooQ
        {
            Id = 1000,
            Card = new PolymorphicTransform { X = 1, Y = 2, Z = 3 },
            CardB = new PolymorphicTransform { X = 1, Y = 2, Z = 3 },
            CardC = new PolymorphicTransform { X = 1, Y = 2, Z = 3 },
            CardD = new PolymorphicTransform { X = 1, Y = 2, Z = 3 }
        };
    }
}

[NinoType]
[MemoryPackable]
[MessagePackObject]
public partial class FooR : FooH
{
    public static new FooR Create()
    {
        return new FooR
        {
            Id = 1000,
            Card = new PolymorphicTransform { X = 1, Y = 2, Z = 3 },
            CardB = new PolymorphicTransform { X = 1, Y = 2, Z = 3 },
            CardC = new PolymorphicTransform { X = 1, Y = 2, Z = 3 },
            CardD = new PolymorphicTransform { X = 1, Y = 2, Z = 3 }
        };
    }
}

[NinoType]
[MemoryPackable]
[MessagePackObject]
public partial class FooS : FooH
{
    public static new FooS Create()
    {
        return new FooS
        {
            Id = 1000,
            Card = new PolymorphicTransform { X = 1, Y = 2, Z = 3 },
            CardB = new PolymorphicTransform { X = 1, Y = 2, Z = 3 },
            CardC = new PolymorphicTransform { X = 1, Y = 2, Z = 3 },
            CardD = new PolymorphicTransform { X = 1, Y = 2, Z = 3 }
        };
    }
}

[NinoType]
[MemoryPackable]
[MessagePackObject]
public partial class FooZ : FooH
{
    public static new FooZ Create()
    {
        return new FooZ
        {
            Id = 1000,
            Card = new PolymorphicTransform { X = 1, Y = 2, Z = 3 },
            CardB = new PolymorphicTransform { X = 1, Y = 2, Z = 3 },
            CardC = new PolymorphicTransform { X = 1, Y = 2, Z = 3 },
            CardD = new PolymorphicTransform { X = 1, Y = 2, Z = 3 }
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

    public PolymorphicTransform(float x, float y, float z)
    {
        X = x;
        Y = y;
        Z = z;
    }
}
