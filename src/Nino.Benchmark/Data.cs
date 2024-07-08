using System;
using System.Collections.Generic;
using System.Linq;
using MemoryPack;
using MessagePack;
using Nino.Core;

namespace Nino.Benchmark;

[NinoType]
[MemoryPackable]
[MessagePackObject]
public partial class SimpleClass
{
    [Key(0)]
    public int Id;

    [Key(1)]
    public string Name { get; set; }
    [Key(2)]
    public int[] Numbers { get; set; }
    [Key(3)]
    public List<DateTime> Dates { get; set; }

    [Key(4)]
    public Dictionary<int, string> Map1;

    [Key(5)]
    public Dictionary<int, int> Map2 { get; set; }

    public static SimpleClass Create()
    {
        Random random = new Random();
        return new SimpleClass
        {
            Id = random.Next(),
            Name = "SimpleClass",
            Numbers = Enumerable.Range(0, 100).Select(n => random.Next()).ToArray(),
            Dates = Enumerable.Range(0, 10).Select(n => DateTime.Now.AddSeconds(random.Next())).ToList(),
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
    [Key(0)]
    public int Id;
    [Key(1)]
    public DateTime CreateTime;

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