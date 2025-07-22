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
[MemoryPackUnion(0, typeof(SimpleClass))]
[Union(0, typeof(SimpleClass))]
public abstract partial class SimpleClassBase
{
    [Key(0)] public int Id;
    [Key(1)] public bool Tag;
    [Key(2)] public Guid Guid;
    [Key(3)] public DateTime CreateTime;
}

[NinoType]
[MemoryPackable]
[MessagePackObject]
public partial class SimpleClass : SimpleClassBase
{
    [Key(4)] public string Name { get; set; }
    [Key(5)] [NinoUtf8] public string Desc;
    [Key(6)] public int[] Numbers { get; set; }
    [Key(7)] public List<DateTime> Dates { get; set; }
    [Key(8)] public Dictionary<int, string> Map1;
    [Key(9)] public Dictionary<int, int> Map2 { get; set; }

    public static SimpleClass Create()
    {
        Random random = new Random();
        return new SimpleClass
        {
            Id = random.Next(),
            Tag = random.Next() % 2 == 0,
            Guid = Guid.NewGuid(),
            CreateTime = DateTime.Now,
            Name = Guid.NewGuid().ToString(),
            Desc = Enumerable.Range(0, 10).Select(_ => Guid.NewGuid().ToString()).Aggregate((a, b) => a + b),
            Numbers = Enumerable.Range(0, 100).Select(_ => random.Next()).ToArray(),
            Dates = Enumerable.Range(0, 10).Select(_ => DateTime.Now.AddSeconds(random.Next())).ToList(),
            Map1 = Enumerable.Range(0, 10).ToDictionary(n => n, n => n.ToString()),
            Map2 = Enumerable.Range(0, 10).ToDictionary(n => n, n => n * 2)
        };
    }
}

[NinoType]
[MemoryPackable]
[MessagePackObject]
public partial struct SimpleStruct
{
    [Key(0)] public int Id;
    [Key(1)] public DateTime CreateTime;

    public static SimpleStruct Create()
    {
        Random random = new Random();
        return new SimpleStruct
        {
            Id = random.Next(),
            CreateTime = DateTime.Now.AddSeconds(random.Next())
        };
    }
}