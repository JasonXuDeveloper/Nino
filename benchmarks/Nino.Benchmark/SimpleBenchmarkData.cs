using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using MemoryPack;
using MessagePack;
using Nino.Core;
using ProtoBuf;

namespace Nino.Benchmark;

#nullable disable

/// <summary>
/// The source of the benchmark data: seeded, with a fixed date, so every benchmark process, and the v3 and v4 runs,
/// serialize the same values (MessagePack's integers vary in size with them).
/// </summary>
internal static class BenchmarkRandom
{
    public static readonly Random Shared = new Random(20240607);

    // Local, as BenchmarkRandom.Now was: MessagePack converts local times to UTC when it writes them.
    public static readonly DateTime Now = new DateTime(2025, 1, 1, 12, 0, 0, DateTimeKind.Local);

    public static Guid NewGuid()
    {
        var bytes = new byte[16];
        Shared.NextBytes(bytes);
        return new Guid(bytes);
    }
}

[NinoType]
[MemoryPackable]
[MessagePackObject]
[MemoryPackUnion(0, typeof(SimpleClass))]
[Union(0, typeof(SimpleClass))]
[ProtoContract]
[ProtoInclude(100, typeof(SimpleClass))]
public abstract partial class SimpleClassBase
{
    [Key(0)] [ProtoMember(1)] public int Id;
    [Key(1)] [ProtoMember(2)] public bool Tag;
    [Key(2)] [ProtoMember(3)] public Guid Guid;
    [Key(3)] [ProtoMember(4)] public DateTime CreateTime;
}

[NinoType]
[MemoryPackable]
[MessagePackObject]
[ProtoContract]
public sealed partial class SimpleClass : SimpleClassBase
{
    [Key(4)] [ProtoMember(1)] public string Name { get; set; }
    [Key(5)] [ProtoMember(2)] [NinoUtf8] public string Desc;
    [Key(6)] [ProtoMember(3)] public int[] Numbers { get; set; }
    [Key(7)] [ProtoMember(4)] public List<DateTime> Dates { get; set; }
    [Key(8)] [ProtoMember(5)] public Dictionary<int, string> Map1;
    [Key(9)] [ProtoMember(6)] public Dictionary<int, int> Map2 { get; set; }

    public static SimpleClass Create()
    {
        Random random = BenchmarkRandom.Shared;
        return new SimpleClass
        {
            Id = random.Next(),
            Tag = random.Next() % 2 == 0,
            Guid = BenchmarkRandom.NewGuid(),
            CreateTime = BenchmarkRandom.Now,
            Name = BenchmarkRandom.NewGuid().ToString(),
            Desc = Enumerable.Range(0, 10).Select(_ => BenchmarkRandom.NewGuid().ToString()).Aggregate((a, b) => a + b),
            Numbers = Enumerable.Range(0, 100).Select(_ => random.Next()).ToArray(),
            Dates = Enumerable.Range(0, 10).Select(_ => BenchmarkRandom.Now.AddSeconds(random.Next())).ToList(),
            Map1 = Enumerable.Range(0, 10).ToDictionary(n => n, n => n.ToString()),
            Map2 = Enumerable.Range(0, 10).ToDictionary(n => n, n => n * 2)
        };
    }
}

[NinoType]
[MemoryPackable]
[MessagePackObject]
[ProtoContract]
[StructLayout(LayoutKind.Sequential, Pack = 4)] // int + DateTime with no padding: 12 bytes
public partial struct SimpleStruct
{
    [Key(0)] [ProtoMember(1)] public int Id;
    [Key(1)] [ProtoMember(2)] public DateTime CreateTime;

    public static SimpleStruct Create()
    {
        Random random = BenchmarkRandom.Shared;
        return new SimpleStruct
        {
            Id = random.Next(),
            CreateTime = BenchmarkRandom.Now.AddSeconds(random.Next())
        };
    }
}