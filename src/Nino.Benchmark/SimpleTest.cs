using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using MemoryPack;
using MessagePack;
using Nino.Core;

#nullable disable
namespace Nino.Benchmark;

[PayloadColumn]
[HideColumns("StdDev", "RatioSD", "Error")]
[MinColumn, MaxColumn]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[ShortRunJob(RuntimeMoniker.Net90)]
[GcServer(true)]
[MemoryDiagnoser]
[MarkdownExporterAttribute.GitHub]
public class SimpleTest
{
    private static readonly SimpleClass SimpleClass;
    private static readonly SimpleClass[] SimpleClasses;
    private static readonly SimpleStruct SimpleStruct;
    private static readonly SimpleStruct[] SimpleStructs;
    private static readonly Vector4[] Vectors;

    private static readonly NinoArrayBufferWriter NinoGenericBuffer = new(320 * 1024);
    private static readonly ArrayBufferWriter<byte> MemoryPackBuffer = new(320 * 1024);
    private static readonly ArrayBufferWriter<byte> MessagePackBuffer = new(320 * 1024);

    private static readonly byte[][] SerializedSimpleClass;
    private static readonly byte[][] SerializedSimpleStruct;
    private static readonly byte[][] SerializedSimpleClasses;
    private static readonly byte[][] SerializedSimpleStructs;
    private static readonly byte[][] SerializedVectors;

    public static readonly Dictionary<string, int> PayloadMap = new();

    static SimpleTest()
    {
        SimpleClass = SimpleClass.Create();
        SimpleClasses = Enumerable.Range(0, 100).Select(_ => SimpleClass.Create()).ToArray();
        SimpleStruct = SimpleStruct.Create();
        SimpleStructs = Enumerable.Range(0, 100).Select(_ => SimpleStruct.Create()).ToArray();
        var r = Random.Shared;
        Vectors = Enumerable.Range(0, 10000)
            .Select(_ => new Vector4(r.NextSingle(), r.NextSingle(),
                r.NextSingle(), r.NextSingle())).ToArray();

        SerializedSimpleClass = new byte[3][];
        SerializedSimpleClass[0] = MessagePackSerializer.Serialize(SimpleClass);
        SerializedSimpleClass[1] = MemoryPackSerializer.Serialize(SimpleClass);
        SerializedSimpleClass[2] = NinoSerializer.Serialize(SimpleClass);

        SerializedSimpleStruct = new byte[3][];
        SerializedSimpleStruct[0] = MessagePackSerializer.Serialize(SimpleStruct);
        SerializedSimpleStruct[1] = MemoryPackSerializer.Serialize(SimpleStruct);
        SerializedSimpleStruct[2] = NinoSerializer.Serialize(SimpleStruct);

        SerializedSimpleClasses = new byte[3][];
        SerializedSimpleClasses[0] = MessagePackSerializer.Serialize(SimpleClasses);
        SerializedSimpleClasses[1] = MemoryPackSerializer.Serialize(SimpleClasses);
        SerializedSimpleClasses[2] = NinoSerializer.Serialize(SimpleClasses);

        SerializedSimpleStructs = new byte[3][];
        SerializedSimpleStructs[0] = MessagePackSerializer.Serialize(SimpleStructs);
        SerializedSimpleStructs[1] = MemoryPackSerializer.Serialize(SimpleStructs);
        SerializedSimpleStructs[2] = NinoSerializer.Serialize(SimpleStructs);

        SerializedVectors = new byte[3][];
        SerializedVectors[0] = MessagePackSerializer.Serialize(Vectors);
        SerializedVectors[1] = MemoryPackSerializer.Serialize(Vectors);
        SerializedVectors[2] = NinoSerializer.Serialize(Vectors);

        PayloadMap.Add(nameof(NinoSerializeSimpleClass), SerializedSimpleClass[2].Length);
        PayloadMap.Add(nameof(NinoSerializeSimpleStruct), SerializedSimpleStruct[2].Length);
        PayloadMap.Add(nameof(NinoSerializeSimpleClasses), SerializedSimpleClasses[2].Length);
        PayloadMap.Add(nameof(NinoSerializeSimpleStructs), SerializedSimpleStructs[2].Length);
        PayloadMap.Add(nameof(NinoSerializeVectors), SerializedVectors[2].Length);

        PayloadMap.Add(nameof(MemoryPackSerializeSimpleClass), SerializedSimpleClass[1].Length);
        PayloadMap.Add(nameof(MemoryPackSerializeSimpleStruct), SerializedSimpleStruct[1].Length);
        PayloadMap.Add(nameof(MemoryPackSerializeSimpleClasses), SerializedSimpleClasses[1].Length);
        PayloadMap.Add(nameof(MemoryPackSerializeSimpleStructs), SerializedSimpleStructs[1].Length);
        PayloadMap.Add(nameof(MemoryPackSerializeVectors), SerializedVectors[1].Length);

        PayloadMap.Add(nameof(MessagePackSerializeSimpleClass), SerializedSimpleClass[0].Length);
        PayloadMap.Add(nameof(MessagePackSerializeSimpleStruct), SerializedSimpleStruct[0].Length);
        PayloadMap.Add(nameof(MessagePackSerializeSimpleClasses), SerializedSimpleClasses[0].Length);
        PayloadMap.Add(nameof(MessagePackSerializeSimpleStructs), SerializedSimpleStructs[0].Length);
        PayloadMap.Add(nameof(MessagePackSerializeVectors), SerializedVectors[0].Length);
    }

    [Benchmark(Baseline = true), BenchmarkCategory("SimpleClassSerialize")]
    public int NinoSerializeSimpleClass()
    {
        NinoGenericBuffer.ResetWrittenCount();
        NinoSerializer.Serialize(SimpleClass, NinoGenericBuffer);
        return NinoGenericBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("SimpleClassSerialize")]
    public int MemoryPackSerializeSimpleClass()
    {
        MemoryPackBuffer.Clear();
        MemoryPackSerializer.Serialize(MemoryPackBuffer, SimpleClass);
        return MemoryPackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("SimpleClassSerialize")]
    public int MessagePackSerializeSimpleClass()
    {
        MessagePackBuffer.Clear();
        MessagePackSerializer.Serialize(MessagePackBuffer, SimpleClass);
        return MessagePackBuffer.WrittenCount;
    }

    [Benchmark(Baseline = true), BenchmarkCategory("SimpleStructSerialize")]
    public int NinoSerializeSimpleStruct()
    {
        NinoGenericBuffer.ResetWrittenCount();
        NinoSerializer.Serialize(SimpleStruct, NinoGenericBuffer);
        return NinoGenericBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("SimpleStructSerialize")]
    public int MemoryPackSerializeSimpleStruct()
    {
        MemoryPackBuffer.Clear();
        MemoryPackSerializer.Serialize(MemoryPackBuffer, SimpleStruct);
        return MemoryPackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("SimpleStructSerialize")]
    public int MessagePackSerializeSimpleStruct()
    {
        MessagePackBuffer.Clear();
        MessagePackSerializer.Serialize(MessagePackBuffer, SimpleStruct);
        return MessagePackBuffer.WrittenCount;
    }

    [Benchmark(Baseline = true), BenchmarkCategory("SimpleClassesSerialize")]
    public int NinoSerializeSimpleClasses()
    {
        NinoGenericBuffer.ResetWrittenCount();
        NinoSerializer.Serialize(SimpleClasses, NinoGenericBuffer);
        return NinoGenericBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("SimpleClassesSerialize")]
    public int MemoryPackSerializeSimpleClasses()
    {
        MemoryPackBuffer.Clear();
        MemoryPackSerializer.Serialize(MemoryPackBuffer, SimpleClasses);
        return MemoryPackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("SimpleClassesSerialize")]
    public int MessagePackSerializeSimpleClasses()
    {
        MessagePackBuffer.Clear();
        MessagePackSerializer.Serialize(MessagePackBuffer, SimpleClasses);
        return MessagePackBuffer.WrittenCount;
    }

    [Benchmark(Baseline = true), BenchmarkCategory("SimpleStructsSerialize")]
    public int NinoSerializeSimpleStructs()
    {
        NinoGenericBuffer.ResetWrittenCount();
        NinoSerializer.Serialize(SimpleStructs, NinoGenericBuffer);
        return NinoGenericBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("SimpleStructsSerialize")]
    public int MemoryPackSerializeSimpleStructs()
    {
        MemoryPackBuffer.Clear();
        MemoryPackSerializer.Serialize(MemoryPackBuffer, SimpleStructs);
        return MemoryPackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("SimpleStructsSerialize")]
    public int MessagePackSerializeSimpleStructs()
    {
        MessagePackBuffer.Clear();
        MessagePackSerializer.Serialize(MessagePackBuffer, SimpleStructs);
        return MessagePackBuffer.WrittenCount;
    }

    [Benchmark(Baseline = true), BenchmarkCategory("VectorsSerialize")]
    public int NinoSerializeVectors()
    {
        NinoGenericBuffer.ResetWrittenCount();
        NinoSerializer.Serialize(Vectors, NinoGenericBuffer);
        return NinoGenericBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("VectorsSerialize")]
    public int MemoryPackSerializeVectors()
    {
        MemoryPackBuffer.Clear();
        MemoryPackSerializer.Serialize(MemoryPackBuffer, Vectors);
        return MemoryPackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("VectorsSerialize")]
    public int MessagePackSerializeVectors()
    {
        MessagePackBuffer.Clear();
        MessagePackSerializer.Serialize(MessagePackBuffer, Vectors);
        return MessagePackBuffer.WrittenCount;
    }

    [Benchmark(Baseline = true), BenchmarkCategory("SimpleClassDeserialize")]
    public SimpleClass NinoDeserializeSimpleClass()
    {
        return NinoDeserializer.Deserialize<SimpleClass>(SerializedSimpleClass[2]);
    }

    [Benchmark, BenchmarkCategory("SimpleClassDeserialize")]
    public SimpleClass MemoryPackDeserializeSimpleClass()
    {
        return MemoryPackSerializer.Deserialize<SimpleClass>(SerializedSimpleClass[1]);
    }

    [Benchmark, BenchmarkCategory("SimpleClassDeserialize")]
    public SimpleClass MessagePackDeserializeSimpleClass()
    {
        return MessagePackSerializer.Deserialize<SimpleClass>(SerializedSimpleClass[0]);
    }

    [Benchmark(Baseline = true), BenchmarkCategory("SimpleStructDeserialize")]
    public SimpleStruct NinoDeserializeSimpleStruct()
    {
        return NinoDeserializer.Deserialize<SimpleStruct>(SerializedSimpleStruct[2]);
    }

    [Benchmark, BenchmarkCategory("SimpleStructDeserialize")]
    public SimpleStruct MemoryPackDeserializeSimpleStruct()
    {
        return MemoryPackSerializer.Deserialize<SimpleStruct>(SerializedSimpleStruct[1]);
    }

    [Benchmark, BenchmarkCategory("SimpleStructDeserialize")]
    public SimpleStruct MessagePackDeserializeSimpleStruct()
    {
        return MessagePackSerializer.Deserialize<SimpleStruct>(SerializedSimpleStruct[0]);
    }

    [Benchmark(Baseline = true), BenchmarkCategory("SimpleClassesDeserialize")]
    public SimpleClass[] NinoDeserializeSimpleClasses()
    {
        return NinoDeserializer.Deserialize<SimpleClass[]>(SerializedSimpleClasses[2]);
    }

    [Benchmark, BenchmarkCategory("SimpleClassesDeserialize")]
    public SimpleClass[] MemoryPackDeserializeSimpleClasses()
    {
        return MemoryPackSerializer.Deserialize<SimpleClass[]>(SerializedSimpleClasses[1]);
    }

    [Benchmark, BenchmarkCategory("SimpleClassesDeserialize")]
    public SimpleClass[] MessagePackDeserializeSimpleClasses()
    {
        return MessagePackSerializer.Deserialize<SimpleClass[]>(SerializedSimpleClasses[0]);
    }

    [Benchmark(Baseline = true), BenchmarkCategory("SimpleStructsDeserialize")]
    public SimpleStruct[] NinoDeserializeSimpleStructs()
    {
        return NinoDeserializer.Deserialize<SimpleStruct[]>(SerializedSimpleStructs[2]);
    }

    [Benchmark, BenchmarkCategory("SimpleStructsDeserialize")]
    public SimpleStruct[] MemoryPackDeserializeSimpleStructs()
    {
        return MemoryPackSerializer.Deserialize<SimpleStruct[]>(SerializedSimpleStructs[1]);
    }

    [Benchmark, BenchmarkCategory("SimpleStructsDeserialize")]
    public SimpleStruct[] MessagePackDeserializeSimpleStructs()
    {
        return MessagePackSerializer.Deserialize<SimpleStruct[]>(SerializedSimpleStructs[0]);
    }

    [Benchmark(Baseline = true), BenchmarkCategory("VectorsDeserialize")]
    public Vector4[] NinoDeserializeVectors()
    {
        return NinoDeserializer.Deserialize<Vector4[]>(SerializedVectors[2]);
    }

    [Benchmark, BenchmarkCategory("VectorsDeserialize")]
    public Vector4[] MemoryPackDeserializeVectors()
    {
        return MemoryPackSerializer.Deserialize<Vector4[]>(SerializedVectors[1]);
    }

    [Benchmark, BenchmarkCategory("VectorsDeserialize")]
    public Vector4[] MessagePackDeserializeVectors()
    {
        return MessagePackSerializer.Deserialize<Vector4[]>(SerializedVectors[0]);
    }
}