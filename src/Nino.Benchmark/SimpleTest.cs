using System;
using System.Buffers;
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

    private NinoArrayBufferWriter _ninoBuffer;
    private ArrayBufferWriter<byte> _memoryPackBuffer;
    private ArrayBufferWriter<byte> _messagePackBuffer;

    private static readonly byte[][] SerializedSimpleClass;
    private static readonly byte[][] SerializedSimpleStruct;
    private static readonly byte[][] SerializedSimpleClasses;
    private static readonly byte[][] SerializedSimpleStructs;
    private static readonly byte[][] SerializedVectors;

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

        BenchmarkPayloadRegistry.Register(nameof(NinoSerializeSimpleClass), SerializedSimpleClass[2].Length);
        BenchmarkPayloadRegistry.Register(nameof(NinoSerializeSimpleStruct), SerializedSimpleStruct[2].Length);
        BenchmarkPayloadRegistry.Register(nameof(NinoSerializeSimpleClasses), SerializedSimpleClasses[2].Length);
        BenchmarkPayloadRegistry.Register(nameof(NinoSerializeSimpleStructs), SerializedSimpleStructs[2].Length);
        BenchmarkPayloadRegistry.Register(nameof(NinoSerializeVectors), SerializedVectors[2].Length);

        BenchmarkPayloadRegistry.Register(nameof(MemoryPackSerializeSimpleClass), SerializedSimpleClass[1].Length);
        BenchmarkPayloadRegistry.Register(nameof(MemoryPackSerializeSimpleStruct), SerializedSimpleStruct[1].Length);
        BenchmarkPayloadRegistry.Register(nameof(MemoryPackSerializeSimpleClasses), SerializedSimpleClasses[1].Length);
        BenchmarkPayloadRegistry.Register(nameof(MemoryPackSerializeSimpleStructs), SerializedSimpleStructs[1].Length);
        BenchmarkPayloadRegistry.Register(nameof(MemoryPackSerializeVectors), SerializedVectors[1].Length);

        BenchmarkPayloadRegistry.Register(nameof(MessagePackSerializeSimpleClass), SerializedSimpleClass[0].Length);
        BenchmarkPayloadRegistry.Register(nameof(MessagePackSerializeSimpleStruct), SerializedSimpleStruct[0].Length);
        BenchmarkPayloadRegistry.Register(nameof(MessagePackSerializeSimpleClasses), SerializedSimpleClasses[0].Length);
        BenchmarkPayloadRegistry.Register(nameof(MessagePackSerializeSimpleStructs), SerializedSimpleStructs[0].Length);
        BenchmarkPayloadRegistry.Register(nameof(MessagePackSerializeVectors), SerializedVectors[0].Length);
    }

    [GlobalSetup]
    public void Setup()
    {
        _ninoBuffer = new NinoArrayBufferWriter(320 * 1024);
        _memoryPackBuffer = new ArrayBufferWriter<byte>(320 * 1024);
        _messagePackBuffer = new ArrayBufferWriter<byte>(320 * 1024);
    }

    [Benchmark(Baseline = true), BenchmarkCategory("SimpleClassSerialize")]
    public int NinoSerializeSimpleClass()
    {
        _ninoBuffer.ResetWrittenCount();
        NinoSerializer.Serialize(SimpleClass, _ninoBuffer);
        return _ninoBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("SimpleClassSerialize")]
    public int MemoryPackSerializeSimpleClass()
    {
        _memoryPackBuffer.ResetWrittenCount();
        MemoryPackSerializer.Serialize(_memoryPackBuffer, SimpleClass);
        return _memoryPackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("SimpleClassSerialize")]
    public int MessagePackSerializeSimpleClass()
    {
        _messagePackBuffer.ResetWrittenCount();
        MessagePackSerializer.Serialize(_messagePackBuffer, SimpleClass);
        return _messagePackBuffer.WrittenCount;
    }

    [Benchmark(Baseline = true), BenchmarkCategory("SimpleStructSerialize")]
    public int NinoSerializeSimpleStruct()
    {
        _ninoBuffer.ResetWrittenCount();
        NinoSerializer.Serialize(SimpleStruct, _ninoBuffer);
        return _ninoBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("SimpleStructSerialize")]
    public int MemoryPackSerializeSimpleStruct()
    {
        _memoryPackBuffer.ResetWrittenCount();
        MemoryPackSerializer.Serialize(_memoryPackBuffer, SimpleStruct);
        return _memoryPackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("SimpleStructSerialize")]
    public int MessagePackSerializeSimpleStruct()
    {
        _messagePackBuffer.ResetWrittenCount();
        MessagePackSerializer.Serialize(_messagePackBuffer, SimpleStruct);
        return _messagePackBuffer.WrittenCount;
    }

    [Benchmark(Baseline = true), BenchmarkCategory("SimpleClassesSerialize")]
    public int NinoSerializeSimpleClasses()
    {
        _ninoBuffer.ResetWrittenCount();
        NinoSerializer.Serialize(SimpleClasses, _ninoBuffer);
        return _ninoBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("SimpleClassesSerialize")]
    public int MemoryPackSerializeSimpleClasses()
    {
        _memoryPackBuffer.ResetWrittenCount();
        MemoryPackSerializer.Serialize(_memoryPackBuffer, SimpleClasses);
        return _memoryPackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("SimpleClassesSerialize")]
    public int MessagePackSerializeSimpleClasses()
    {
        _messagePackBuffer.ResetWrittenCount();
        MessagePackSerializer.Serialize(_messagePackBuffer, SimpleClasses);
        return _messagePackBuffer.WrittenCount;
    }

    [Benchmark(Baseline = true), BenchmarkCategory("SimpleStructsSerialize")]
    public int NinoSerializeSimpleStructs()
    {
        _ninoBuffer.ResetWrittenCount();
        NinoSerializer.Serialize(SimpleStructs, _ninoBuffer);
        return _ninoBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("SimpleStructsSerialize")]
    public int MemoryPackSerializeSimpleStructs()
    {
        _memoryPackBuffer.ResetWrittenCount();
        MemoryPackSerializer.Serialize(_memoryPackBuffer, SimpleStructs);
        return _memoryPackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("SimpleStructsSerialize")]
    public int MessagePackSerializeSimpleStructs()
    {
        _messagePackBuffer.ResetWrittenCount();
        MessagePackSerializer.Serialize(_messagePackBuffer, SimpleStructs);
        return _messagePackBuffer.WrittenCount;
    }

    [Benchmark(Baseline = true), BenchmarkCategory("VectorsSerialize")]
    public int NinoSerializeVectors()
    {
        _ninoBuffer.ResetWrittenCount();
        NinoSerializer.Serialize(Vectors, _ninoBuffer);
        return _ninoBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("VectorsSerialize")]
    public int MemoryPackSerializeVectors()
    {
        _memoryPackBuffer.ResetWrittenCount();
        MemoryPackSerializer.Serialize(_memoryPackBuffer, Vectors);
        return _memoryPackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("VectorsSerialize")]
    public int MessagePackSerializeVectors()
    {
        _messagePackBuffer.ResetWrittenCount();
        MessagePackSerializer.Serialize(_messagePackBuffer, Vectors);
        return _messagePackBuffer.WrittenCount;
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
