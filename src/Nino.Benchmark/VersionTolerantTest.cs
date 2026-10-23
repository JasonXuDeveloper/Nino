using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using BenchmarkDotNet.Configs;
using MemoryPack;
using Nino.Core;

#nullable disable
namespace Nino.Benchmark;

/// <summary>
/// The members of <see cref="SimpleClass"/> in a version-tolerant type: members can be appended later and data written
/// before still reads. Nino prefixes the body with its length (<see cref="NinoFormat.VersionTolerant"/>); MemoryPack
/// writes a member count and the length of every member (<see cref="GenerateType.VersionTolerant"/>, which needs an
/// explicit <see cref="MemoryPackOrderAttribute"/> on each member).
/// </summary>
[NinoType(format: NinoFormat.VersionTolerant)]
[MemoryPackable(GenerateType.VersionTolerant)]
public sealed partial class TolerantClass
{
    [MemoryPackOrder(0)] public int Id;
    [MemoryPackOrder(1)] public bool Tag;
    [MemoryPackOrder(2)] public Guid Guid;
    [MemoryPackOrder(3)] public DateTime CreateTime;
    [MemoryPackOrder(4)] public string Name { get; set; }
    [MemoryPackOrder(5)] [NinoUtf8] public string Desc;
    [MemoryPackOrder(6)] public int[] Numbers { get; set; }
    [MemoryPackOrder(7)] public List<DateTime> Dates { get; set; }
    [MemoryPackOrder(8)] public Dictionary<int, string> Map1;
    [MemoryPackOrder(9)] public Dictionary<int, int> Map2 { get; set; }

    public static TolerantClass From(SimpleClass c) => new()
    {
        Id = c.Id, Tag = c.Tag, Guid = c.Guid, CreateTime = c.CreateTime, Name = c.Name, Desc = c.Desc,
        Numbers = c.Numbers, Dates = c.Dates, Map1 = c.Map1, Map2 = c.Map2,
    };
}

/// <summary>
/// <see cref="TolerantClass"/> and an array of 100, written into a reused buffer writer and read back, as
/// <see cref="SimpleTest"/> does with <see cref="SimpleClass"/>: the difference between the two is the cost of version
/// tolerance.
/// </summary>
[PayloadColumn]
[HideColumns("StdDev", "RatioSD", "Error")]
[MinColumn, MaxColumn]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[NinoJob]
[GcServer(true)]
[MemoryDiagnoser]
[MarkdownExporterAttribute.GitHub]
public class VersionTolerantTest
{
    private static readonly TolerantClass TolerantClass = TolerantClass.From(SimpleClass.Create());
    private static readonly TolerantClass[] TolerantClasses =
        Enumerable.Range(0, 100).Select(_ => TolerantClass.From(SimpleClass.Create())).ToArray();

    private static readonly byte[] NinoTolerantClass = NinoSerializer.Serialize(TolerantClass);
    private static readonly byte[] MemoryPackTolerantClass = MemoryPackSerializer.Serialize(TolerantClass);
    private static readonly byte[] NinoTolerantClasses = NinoSerializer.Serialize(TolerantClasses);
    private static readonly byte[] MemoryPackTolerantClasses = MemoryPackSerializer.Serialize(TolerantClasses);

    private NinoArrayBufferWriter _ninoBuffer;
    private ArrayBufferWriter<byte> _memoryPackBuffer;

    static VersionTolerantTest()
    {
        BenchmarkPayloadRegistry.Register(nameof(NinoSerializeTolerantClass), NinoTolerantClass.Length);
        BenchmarkPayloadRegistry.Register(nameof(MemoryPackSerializeTolerantClass), MemoryPackTolerantClass.Length);
        BenchmarkPayloadRegistry.Register(nameof(NinoSerializeTolerantClasses), NinoTolerantClasses.Length);
        BenchmarkPayloadRegistry.Register(nameof(MemoryPackSerializeTolerantClasses), MemoryPackTolerantClasses.Length);
    }

    [GlobalSetup]
    public void Setup()
    {
        _ninoBuffer = new NinoArrayBufferWriter(320 * 1024);
        _memoryPackBuffer = new ArrayBufferWriter<byte>(320 * 1024);
    }

    [Benchmark(Baseline = true), BenchmarkCategory("TolerantClassSerialize")]
    public int NinoSerializeTolerantClass()
    {
        _ninoBuffer.ResetWrittenCount();
        NinoSerializer.Serialize(TolerantClass, _ninoBuffer);
        return _ninoBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("TolerantClassSerialize")]
    public int MemoryPackSerializeTolerantClass()
    {
        _memoryPackBuffer.ResetWrittenCount();
        MemoryPackSerializer.Serialize(_memoryPackBuffer, TolerantClass);
        return _memoryPackBuffer.WrittenCount;
    }

    [Benchmark(Baseline = true), BenchmarkCategory("TolerantClassesSerialize")]
    public int NinoSerializeTolerantClasses()
    {
        _ninoBuffer.ResetWrittenCount();
        NinoSerializer.Serialize(TolerantClasses, _ninoBuffer);
        return _ninoBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("TolerantClassesSerialize")]
    public int MemoryPackSerializeTolerantClasses()
    {
        _memoryPackBuffer.ResetWrittenCount();
        MemoryPackSerializer.Serialize(_memoryPackBuffer, TolerantClasses);
        return _memoryPackBuffer.WrittenCount;
    }

    [Benchmark(Baseline = true), BenchmarkCategory("TolerantClassDeserialize")]
    public TolerantClass NinoDeserializeTolerantClass() =>
        NinoDeserializer.Deserialize<TolerantClass>(NinoTolerantClass);

    [Benchmark, BenchmarkCategory("TolerantClassDeserialize")]
    public TolerantClass MemoryPackDeserializeTolerantClass() =>
        MemoryPackSerializer.Deserialize<TolerantClass>(MemoryPackTolerantClass);

    [Benchmark(Baseline = true), BenchmarkCategory("TolerantClassesDeserialize")]
    public TolerantClass[] NinoDeserializeTolerantClasses() =>
        NinoDeserializer.Deserialize<TolerantClass[]>(NinoTolerantClasses);

    [Benchmark, BenchmarkCategory("TolerantClassesDeserialize")]
    public TolerantClass[] MemoryPackDeserializeTolerantClasses() =>
        MemoryPackSerializer.Deserialize<TolerantClass[]>(MemoryPackTolerantClasses);
}
