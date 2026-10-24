using System;
using System.IO;
using System.Linq;
using System.Numerics;
using BenchmarkDotNet.Configs;
using MemoryPack;
using MessagePack;
using Nino.Core;
using ProtoBuf;

#nullable disable
namespace Nino.Benchmark;

/// <summary>Unmanaged members only (40 bytes, no padding): every value serializes to the same number of bytes.</summary>
[NinoType]
[MemoryPackable]
[MessagePackObject]
[ProtoContract]
public partial struct FixedStruct
{
    [Key(0)] [ProtoMember(1)] public int Id;
    [Key(1)] [ProtoMember(2)] public Vector3 Position;
    [Key(2)] [ProtoMember(3)] public long Ticks;
    [Key(3)] [ProtoMember(4)] public Guid Guid;

    public static FixedStruct Create(Random r) => new()
    {
        Id = r.Next(),
        Position = new Vector3(r.NextSingle(), r.NextSingle(), r.NextSingle()),
        Ticks = BenchmarkRandom.Now.AddSeconds(r.Next()).Ticks,
        Guid = BenchmarkRandom.NewGuid(),
    };
}

/// <summary>The members of <see cref="FixedStruct"/> in a sealed class: its size is fixed too (a null flag and 40 bytes).</summary>
[NinoType]
[MemoryPackable]
[MessagePackObject]
[ProtoContract]
public sealed partial class FixedClass
{
    [Key(0)] [ProtoMember(1)] public int Id;
    [Key(1)] [ProtoMember(2)] public Vector3 Position;
    [Key(2)] [ProtoMember(3)] public long Ticks;
    [Key(3)] [ProtoMember(4)] public Guid Guid;

    public static FixedClass Create(Random r) => new()
    {
        Id = r.Next(),
        Position = new Vector3(r.NextSingle(), r.NextSingle(), r.NextSingle()),
        Ticks = BenchmarkRandom.Now.AddSeconds(r.Next()).Ticks,
        Guid = BenchmarkRandom.NewGuid(),
    };
}

/// <summary>
/// Types of fixed size (<see cref="NinoSerializer.GetFixedSize{T}"/> is not -1): <c>Serialize(value)</c> returns a new
/// <c>byte[]</c> allocated at exactly that size and writes the value straight into it, with no scratch buffer, writer
/// or copy. Measured through the <c>byte[]</c> APIs of both serializers (the allocation of the result is part of every
/// serialize call), plus an array of 100 of the classes, whose length varies and which takes the regular path.
/// MessagePack through its <c>byte[]</c> API too; protobuf-net has none, so it writes to a reused stream and copies
/// the result into a new array of its size.
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
    // First, so that the data below is serialized with it.
    private static readonly bool ProtobufReady = ProtobufModel.Ready;
    private static readonly FixedStruct FixedStruct = FixedStruct.Create(new Random(4));
    private static readonly FixedClass FixedClass = FixedClass.Create(new Random(5));
    private static readonly FixedClass[] FixedClasses =
        Enumerable.Range(0, 100).Select(i => FixedClass.Create(new Random(6 + i))).ToArray();

    private static readonly byte[] NinoFixedStruct = NinoSerializer.Serialize(FixedStruct);
    private static readonly byte[] MemoryPackFixedStruct = MemoryPackSerializer.Serialize(FixedStruct);
    private static readonly byte[] MessagePackFixedStruct = MessagePackSerializer.Serialize(FixedStruct);
    private static readonly byte[] ProtobufFixedStruct = ProtobufModel.Serialize(FixedStruct);
    private static readonly byte[] NinoFixedClass = NinoSerializer.Serialize(FixedClass);
    private static readonly byte[] MemoryPackFixedClass = MemoryPackSerializer.Serialize(FixedClass);
    private static readonly byte[] MessagePackFixedClass = MessagePackSerializer.Serialize(FixedClass);
    private static readonly byte[] ProtobufFixedClass = ProtobufModel.Serialize(FixedClass);
    private static readonly byte[] NinoFixedClasses = NinoSerializer.Serialize(FixedClasses);
    private static readonly byte[] MemoryPackFixedClasses = MemoryPackSerializer.Serialize(FixedClasses);
    private static readonly byte[] MessagePackFixedClasses = MessagePackSerializer.Serialize(FixedClasses);
    private static readonly byte[] ProtobufFixedClasses = ProtobufModel.Serialize(FixedClasses);

    static FixedSizeTest()
    {
        // These cases measure the fixed-size path: stop if either type ever stops taking it.
        if (NinoSerializer.GetFixedSize<FixedStruct>() < 0 || NinoSerializer.GetFixedSize<FixedClass>() < 0)
            throw new InvalidOperationException("FixedStruct and FixedClass must be of fixed size");

        BenchmarkPayloadRegistry.Register(nameof(NinoSerializeFixedStruct), NinoFixedStruct.Length);
        BenchmarkPayloadRegistry.Register(nameof(MemoryPackSerializeFixedStruct), MemoryPackFixedStruct.Length);
        BenchmarkPayloadRegistry.Register(nameof(MessagePackSerializeFixedStruct), MessagePackFixedStruct.Length);
        BenchmarkPayloadRegistry.Register(nameof(ProtobufSerializeFixedStruct), ProtobufFixedStruct.Length);
        BenchmarkPayloadRegistry.Register(nameof(NinoSerializeFixedClass), NinoFixedClass.Length);
        BenchmarkPayloadRegistry.Register(nameof(MemoryPackSerializeFixedClass), MemoryPackFixedClass.Length);
        BenchmarkPayloadRegistry.Register(nameof(MessagePackSerializeFixedClass), MessagePackFixedClass.Length);
        BenchmarkPayloadRegistry.Register(nameof(ProtobufSerializeFixedClass), ProtobufFixedClass.Length);
        BenchmarkPayloadRegistry.Register(nameof(NinoSerializeFixedClasses), NinoFixedClasses.Length);
        BenchmarkPayloadRegistry.Register(nameof(MemoryPackSerializeFixedClasses), MemoryPackFixedClasses.Length);
        BenchmarkPayloadRegistry.Register(nameof(MessagePackSerializeFixedClasses), MessagePackFixedClasses.Length);
        BenchmarkPayloadRegistry.Register(nameof(ProtobufSerializeFixedClasses), ProtobufFixedClasses.Length);
    }

    private readonly MemoryStream _protobufStream = new(16 * 1024);

    [Benchmark(Baseline = true), BenchmarkCategory("FixedStructSerialize")]
    public byte[] NinoSerializeFixedStruct() => NinoSerializer.Serialize(FixedStruct);

    [Benchmark, BenchmarkCategory("FixedStructSerialize")]
    public byte[] MemoryPackSerializeFixedStruct() => MemoryPackSerializer.Serialize(FixedStruct);

    [Benchmark, BenchmarkCategory("FixedStructSerialize")]
    public byte[] MessagePackSerializeFixedStruct() => MessagePackSerializer.Serialize(FixedStruct);

    [Benchmark, BenchmarkCategory("FixedStructSerialize")]
    public byte[] ProtobufSerializeFixedStruct()
    {
        _protobufStream.SetLength(0);
        Serializer.Serialize(_protobufStream, FixedStruct);
        return _protobufStream.ToArray();
    }

    [Benchmark(Baseline = true), BenchmarkCategory("FixedClassSerialize")]
    public byte[] NinoSerializeFixedClass() => NinoSerializer.Serialize(FixedClass);

    [Benchmark, BenchmarkCategory("FixedClassSerialize")]
    public byte[] MemoryPackSerializeFixedClass() => MemoryPackSerializer.Serialize(FixedClass);

    [Benchmark, BenchmarkCategory("FixedClassSerialize")]
    public byte[] MessagePackSerializeFixedClass() => MessagePackSerializer.Serialize(FixedClass);

    [Benchmark, BenchmarkCategory("FixedClassSerialize")]
    public byte[] ProtobufSerializeFixedClass()
    {
        _protobufStream.SetLength(0);
        Serializer.Serialize(_protobufStream, FixedClass);
        return _protobufStream.ToArray();
    }

    [Benchmark(Baseline = true), BenchmarkCategory("FixedClassesSerialize")]
    public byte[] NinoSerializeFixedClasses() => NinoSerializer.Serialize(FixedClasses);

    [Benchmark, BenchmarkCategory("FixedClassesSerialize")]
    public byte[] MemoryPackSerializeFixedClasses() => MemoryPackSerializer.Serialize(FixedClasses);

    [Benchmark, BenchmarkCategory("FixedClassesSerialize")]
    public byte[] MessagePackSerializeFixedClasses() => MessagePackSerializer.Serialize(FixedClasses);

    [Benchmark, BenchmarkCategory("FixedClassesSerialize")]
    public byte[] ProtobufSerializeFixedClasses()
    {
        _protobufStream.SetLength(0);
        Serializer.Serialize(_protobufStream, FixedClasses);
        return _protobufStream.ToArray();
    }

    [Benchmark(Baseline = true), BenchmarkCategory("FixedStructDeserialize")]
    public FixedStruct NinoDeserializeFixedStruct() => NinoDeserializer.Deserialize<FixedStruct>(NinoFixedStruct);

    [Benchmark, BenchmarkCategory("FixedStructDeserialize")]
    public FixedStruct MemoryPackDeserializeFixedStruct() =>
        MemoryPackSerializer.Deserialize<FixedStruct>(MemoryPackFixedStruct);

    [Benchmark, BenchmarkCategory("FixedStructDeserialize")]
    public FixedStruct MessagePackDeserializeFixedStruct() => MessagePackSerializer.Deserialize<FixedStruct>(MessagePackFixedStruct);

    [Benchmark, BenchmarkCategory("FixedStructDeserialize")]
    public FixedStruct ProtobufDeserializeFixedStruct() => Serializer.Deserialize<FixedStruct>(new ReadOnlySpan<byte>(ProtobufFixedStruct));

    [Benchmark(Baseline = true), BenchmarkCategory("FixedClassDeserialize")]
    public FixedClass NinoDeserializeFixedClass() => NinoDeserializer.Deserialize<FixedClass>(NinoFixedClass);

    [Benchmark, BenchmarkCategory("FixedClassDeserialize")]
    public FixedClass MemoryPackDeserializeFixedClass() =>
        MemoryPackSerializer.Deserialize<FixedClass>(MemoryPackFixedClass);

    [Benchmark, BenchmarkCategory("FixedClassDeserialize")]
    public FixedClass MessagePackDeserializeFixedClass() => MessagePackSerializer.Deserialize<FixedClass>(MessagePackFixedClass);

    [Benchmark, BenchmarkCategory("FixedClassDeserialize")]
    public FixedClass ProtobufDeserializeFixedClass() => Serializer.Deserialize<FixedClass>(new ReadOnlySpan<byte>(ProtobufFixedClass));

    [Benchmark(Baseline = true), BenchmarkCategory("FixedClassesDeserialize")]
    public FixedClass[] NinoDeserializeFixedClasses() =>
        NinoDeserializer.Deserialize<FixedClass[]>(NinoFixedClasses);

    [Benchmark, BenchmarkCategory("FixedClassesDeserialize")]
    public FixedClass[] MemoryPackDeserializeFixedClasses() =>
        MemoryPackSerializer.Deserialize<FixedClass[]>(MemoryPackFixedClasses);

    [Benchmark, BenchmarkCategory("FixedClassesDeserialize")]
    public FixedClass[] MessagePackDeserializeFixedClasses() => MessagePackSerializer.Deserialize<FixedClass[]>(MessagePackFixedClasses);

    [Benchmark, BenchmarkCategory("FixedClassesDeserialize")]
    public FixedClass[] ProtobufDeserializeFixedClasses() => Serializer.Deserialize<FixedClass[]>(new ReadOnlySpan<byte>(ProtobufFixedClasses));
}
