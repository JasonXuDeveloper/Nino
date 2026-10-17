using System;
using System.Linq;
using System.Numerics;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using MemoryPack;
using Nino.Core;

#nullable disable
namespace Nino.Benchmark;

/// <summary>Unmanaged members only (40 bytes, no padding): every value serializes to the same number of bytes.</summary>
[NinoType]
[MemoryPackable]
public partial struct FixedStruct
{
    public int Id;
    public Vector3 Position;
    public long Ticks;
    public Guid Guid;

    public static FixedStruct Create(Random r) => new()
    {
        Id = r.Next(),
        Position = new Vector3(r.NextSingle(), r.NextSingle(), r.NextSingle()),
        Ticks = DateTime.Now.AddSeconds(r.Next()).Ticks,
        Guid = Guid.NewGuid(),
    };
}

/// <summary>The members of <see cref="FixedStruct"/> in a sealed class: its size is fixed too (a null flag and 40 bytes).</summary>
[NinoType]
[MemoryPackable]
public sealed partial class FixedClass
{
    public int Id;
    public Vector3 Position;
    public long Ticks;
    public Guid Guid;

    public static FixedClass Create(Random r) => new()
    {
        Id = r.Next(),
        Position = new Vector3(r.NextSingle(), r.NextSingle(), r.NextSingle()),
        Ticks = DateTime.Now.AddSeconds(r.Next()).Ticks,
        Guid = Guid.NewGuid(),
    };
}

/// <summary>
/// Types of fixed size (<see cref="NinoSerializer.GetFixedSize{T}"/> is not -1): <c>Serialize(value)</c> returns a new
/// <c>byte[]</c> allocated at exactly that size and writes the value straight into it, with no scratch buffer, writer
/// or copy. Measured through the <c>byte[]</c> APIs of both serializers (the allocation of the result is part of every
/// serialize call), plus an array of 100 of the classes, whose length varies and which takes the regular path.
/// </summary>
[PayloadColumn]
[HideColumns("StdDev", "RatioSD", "Error")]
[MinColumn, MaxColumn]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[NinoJob]
[GcServer(true)]
[MemoryDiagnoser]
[MarkdownExporterAttribute.GitHub]
public class FixedSizeTest
{
    private static readonly FixedStruct FixedStruct = FixedStruct.Create(new Random(4));
    private static readonly FixedClass FixedClass = FixedClass.Create(new Random(5));
    private static readonly FixedClass[] FixedClasses =
        Enumerable.Range(0, 100).Select(i => FixedClass.Create(new Random(6 + i))).ToArray();

    private static readonly byte[] NinoFixedStruct = NinoSerializer.Serialize(FixedStruct);
    private static readonly byte[] MemoryPackFixedStruct = MemoryPackSerializer.Serialize(FixedStruct);
    private static readonly byte[] NinoFixedClass = NinoSerializer.Serialize(FixedClass);
    private static readonly byte[] MemoryPackFixedClass = MemoryPackSerializer.Serialize(FixedClass);
    private static readonly byte[] NinoFixedClasses = NinoSerializer.Serialize(FixedClasses);
    private static readonly byte[] MemoryPackFixedClasses = MemoryPackSerializer.Serialize(FixedClasses);

    static FixedSizeTest()
    {
        // These cases measure the fixed-size path: stop if either type ever stops taking it.
        if (NinoSerializer.GetFixedSize<FixedStruct>() < 0 || NinoSerializer.GetFixedSize<FixedClass>() < 0)
            throw new InvalidOperationException("FixedStruct and FixedClass must be of fixed size");

        BenchmarkPayloadRegistry.Register(nameof(NinoSerializeFixedStruct), NinoFixedStruct.Length);
        BenchmarkPayloadRegistry.Register(nameof(MemoryPackSerializeFixedStruct), MemoryPackFixedStruct.Length);
        BenchmarkPayloadRegistry.Register(nameof(NinoSerializeFixedClass), NinoFixedClass.Length);
        BenchmarkPayloadRegistry.Register(nameof(MemoryPackSerializeFixedClass), MemoryPackFixedClass.Length);
        BenchmarkPayloadRegistry.Register(nameof(NinoSerializeFixedClasses), NinoFixedClasses.Length);
        BenchmarkPayloadRegistry.Register(nameof(MemoryPackSerializeFixedClasses), MemoryPackFixedClasses.Length);
    }

    [Benchmark(Baseline = true), BenchmarkCategory("FixedStructSerialize")]
    public byte[] NinoSerializeFixedStruct() => NinoSerializer.Serialize(FixedStruct);

    [Benchmark, BenchmarkCategory("FixedStructSerialize")]
    public byte[] MemoryPackSerializeFixedStruct() => MemoryPackSerializer.Serialize(FixedStruct);

    [Benchmark(Baseline = true), BenchmarkCategory("FixedClassSerialize")]
    public byte[] NinoSerializeFixedClass() => NinoSerializer.Serialize(FixedClass);

    [Benchmark, BenchmarkCategory("FixedClassSerialize")]
    public byte[] MemoryPackSerializeFixedClass() => MemoryPackSerializer.Serialize(FixedClass);

    [Benchmark(Baseline = true), BenchmarkCategory("FixedClassesSerialize")]
    public byte[] NinoSerializeFixedClasses() => NinoSerializer.Serialize(FixedClasses);

    [Benchmark, BenchmarkCategory("FixedClassesSerialize")]
    public byte[] MemoryPackSerializeFixedClasses() => MemoryPackSerializer.Serialize(FixedClasses);

    [Benchmark(Baseline = true), BenchmarkCategory("FixedStructDeserialize")]
    public FixedStruct NinoDeserializeFixedStruct() => NinoDeserializer.Deserialize<FixedStruct>(NinoFixedStruct);

    [Benchmark, BenchmarkCategory("FixedStructDeserialize")]
    public FixedStruct MemoryPackDeserializeFixedStruct() =>
        MemoryPackSerializer.Deserialize<FixedStruct>(MemoryPackFixedStruct);

    [Benchmark(Baseline = true), BenchmarkCategory("FixedClassDeserialize")]
    public FixedClass NinoDeserializeFixedClass() => NinoDeserializer.Deserialize<FixedClass>(NinoFixedClass);

    [Benchmark, BenchmarkCategory("FixedClassDeserialize")]
    public FixedClass MemoryPackDeserializeFixedClass() =>
        MemoryPackSerializer.Deserialize<FixedClass>(MemoryPackFixedClass);

    [Benchmark(Baseline = true), BenchmarkCategory("FixedClassesDeserialize")]
    public FixedClass[] NinoDeserializeFixedClasses() =>
        NinoDeserializer.Deserialize<FixedClass[]>(NinoFixedClasses);

    [Benchmark, BenchmarkCategory("FixedClassesDeserialize")]
    public FixedClass[] MemoryPackDeserializeFixedClasses() =>
        MemoryPackSerializer.Deserialize<FixedClass[]>(MemoryPackFixedClasses);
}
