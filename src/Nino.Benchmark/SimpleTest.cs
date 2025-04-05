using System;
using System.Buffers;
using System.Linq;
using System.Numerics;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using MemoryPack;
using MessagePack;

#nullable disable
namespace Nino.Benchmark;

[PayloadColumn]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[MediumRunJob(RuntimeMoniker.Net90)]
[GcServer(true)]
[MarkdownExporter]
public class SimpleTest
{
    private readonly SimpleClass _simpleClass;
    private readonly SimpleClass[] _simpleClasses;
    private readonly SimpleStruct _simpleStruct;
    private readonly SimpleStruct[] _simpleStructs;
    private readonly Vector4[] _vectors;

    private static readonly ArrayBufferWriter<byte> BufferWriter = new ArrayBufferWriter<byte>(1024 * 1024 * 32);

    private readonly byte[][] _serializedSimpleClass;
    private readonly byte[][] _serializedSimpleStruct;
    private readonly byte[][] _serializedSimpleClasses;
    private readonly byte[][] _serializedSimpleStructs;
    private readonly byte[][] _serializedVectors;

    public SimpleTest()
    {
        _simpleClass = SimpleClass.Create();
        _simpleClasses = Enumerable.Range(0, 100).Select(_ => SimpleClass.Create()).ToArray();
        _simpleStruct = SimpleStruct.Create();
        _simpleStructs = Enumerable.Range(0, 100).Select(_ => SimpleStruct.Create()).ToArray();
        var r = Random.Shared;
        _vectors = Enumerable.Range(0, 10000)
            .Select(_ => new Vector4(r.NextSingle(), r.NextSingle(),
                r.NextSingle(), r.NextSingle())).ToArray();

        _serializedSimpleClass = new byte[3][];
        _serializedSimpleClass[0] = MessagePackSerializer.Serialize(_simpleClass);
        _serializedSimpleClass[1] = MemoryPackSerializer.Serialize(_simpleClass);
        _serializedSimpleClass[2] = NinoGen.Serializer.Serialize(_simpleClass);

        _serializedSimpleStruct = new byte[3][];
        _serializedSimpleStruct[0] = MessagePackSerializer.Serialize(_simpleStruct);
        _serializedSimpleStruct[1] = MemoryPackSerializer.Serialize(_simpleStruct);
        _serializedSimpleStruct[2] = NinoGen.Serializer.Serialize(_simpleStruct);

        _serializedSimpleClasses = new byte[3][];
        _serializedSimpleClasses[0] = MessagePackSerializer.Serialize(_simpleClasses);
        _serializedSimpleClasses[1] = MemoryPackSerializer.Serialize(_simpleClasses);
        _serializedSimpleClasses[2] = NinoGen.Serializer.Serialize(_simpleClasses);

        _serializedSimpleStructs = new byte[3][];
        _serializedSimpleStructs[0] = MessagePackSerializer.Serialize(_simpleStructs);
        _serializedSimpleStructs[1] = MemoryPackSerializer.Serialize(_simpleStructs);
        _serializedSimpleStructs[2] = NinoGen.Serializer.Serialize(_simpleStructs);

        _serializedVectors = new byte[3][];
        _serializedVectors[0] = MessagePackSerializer.Serialize(_vectors);
        _serializedVectors[1] = MemoryPackSerializer.Serialize(_vectors);
        _serializedVectors[2] = NinoGen.Serializer.Serialize(_vectors);
    }

    [Benchmark, BenchmarkCategory("SimpleClassSerialize")]
    public int MessagePackSerializeSimpleClass()
    {
        BufferWriter.ResetWrittenCount();
        MessagePackSerializer.Serialize(BufferWriter, _simpleClass);
        return BufferWriter.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("SimpleClassSerialize")]
    public int MemoryPackSerializeSimpleClass()
    {
        BufferWriter.ResetWrittenCount();
        MemoryPackSerializer.Serialize(BufferWriter, _simpleClass);
        return BufferWriter.WrittenCount;
    }

    [Benchmark(Baseline = true), BenchmarkCategory("SimpleClassSerialize")]
    public int NinoSerializeSimpleClass()
    {
        BufferWriter.ResetWrittenCount();
        NinoGen.Serializer.Serialize(_simpleClass, BufferWriter);
        return BufferWriter.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("SimpleStructSerialize")]
    public int MessagePackSerializeSimpleStruct()
    {
        BufferWriter.ResetWrittenCount();
        MessagePackSerializer.Serialize(BufferWriter, _simpleStruct);
        return BufferWriter.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("SimpleStructSerialize")]
    public int MemoryPackSerializeSimpleStruct()
    {
        BufferWriter.ResetWrittenCount();
        MemoryPackSerializer.Serialize(BufferWriter, _simpleStruct);
        return BufferWriter.WrittenCount;
    }

    [Benchmark(Baseline = true), BenchmarkCategory("SimpleStructSerialize")]
    public int NinoSerializeSimpleStruct()
    {
        BufferWriter.ResetWrittenCount();
        NinoGen.Serializer.Serialize(_simpleStruct, BufferWriter);
        return BufferWriter.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("SimpleClassesSerialize")]
    public int MessagePackSerializeSimpleClasses()
    {
        BufferWriter.ResetWrittenCount();
        MessagePackSerializer.Serialize(BufferWriter, _simpleClasses);
        return BufferWriter.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("SimpleClassesSerialize")]
    public int MemoryPackSerializeSimpleClasses()
    {
        BufferWriter.ResetWrittenCount();
        MemoryPackSerializer.Serialize(BufferWriter, _simpleClasses);
        return BufferWriter.WrittenCount;
    }

    [Benchmark(Baseline = true), BenchmarkCategory("SimpleClassesSerialize")]
    public int NinoSerializeSimpleClasses()
    {
        BufferWriter.ResetWrittenCount();
        NinoGen.Serializer.Serialize(_simpleClasses, BufferWriter);
        return BufferWriter.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("SimpleStructsSerialize")]
    public int MessagePackSerializeSimpleStructs()
    {
        BufferWriter.ResetWrittenCount();
        MessagePackSerializer.Serialize(BufferWriter, _simpleStructs);
        return BufferWriter.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("SimpleStructsSerialize")]
    public int MemoryPackSerializeSimpleStructs()
    {
        BufferWriter.ResetWrittenCount();
        MemoryPackSerializer.Serialize(BufferWriter, _simpleStructs);
        return BufferWriter.WrittenCount;
    }

    [Benchmark(Baseline = true), BenchmarkCategory("SimpleStructsSerialize")]
    public int NinoSerializeSimpleStructs()
    {
        BufferWriter.ResetWrittenCount();
        NinoGen.Serializer.Serialize(_simpleStructs, BufferWriter);
        return BufferWriter.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("VectorsSerialize")]
    public int MessagePackSerializeVectors()
    {
        BufferWriter.ResetWrittenCount();
        MessagePackSerializer.Serialize(BufferWriter, _vectors);
        return BufferWriter.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("VectorsSerialize")]
    public int MemoryPackSerializeVectors()
    {
        BufferWriter.ResetWrittenCount();
        MemoryPackSerializer.Serialize(BufferWriter, _vectors);
        return BufferWriter.WrittenCount;
    }

    [Benchmark(Baseline = true), BenchmarkCategory("VectorsSerialize")]
    public int NinoSerializeVectors()
    {
        BufferWriter.ResetWrittenCount();
        NinoGen.Serializer.Serialize(_vectors, BufferWriter);
        return BufferWriter.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("SimpleClassDeserialize")]
    public SimpleClass MessagePackDeserializeSimpleClass()
    {
        return MessagePackSerializer.Deserialize<SimpleClass>(_serializedSimpleClass[0]);
    }

    [Benchmark, BenchmarkCategory("SimpleClassDeserialize")]
    public SimpleClass MemoryPackDeserializeSimpleClass()
    {
        return MemoryPackSerializer.Deserialize<SimpleClass>(_serializedSimpleClass[1]);
    }

    [Benchmark(Baseline = true), BenchmarkCategory("SimpleClassDeserialize")]
    public SimpleClass NinoDeserializeSimpleClass()
    {
        NinoGen.Deserializer.Deserialize(_serializedSimpleClass[2], out SimpleClass ret);
        return ret;
    }

    [Benchmark, BenchmarkCategory("SimpleStructDeserialize")]
    public SimpleStruct MessagePackDeserializeSimpleStruct()
    {
        return MessagePackSerializer.Deserialize<SimpleStruct>(_serializedSimpleStruct[0]);
    }

    [Benchmark, BenchmarkCategory("SimpleStructDeserialize")]
    public SimpleStruct MemoryPackDeserializeSimpleStruct()
    {
        return MemoryPackSerializer.Deserialize<SimpleStruct>(_serializedSimpleStruct[1]);
    }

    [Benchmark(Baseline = true), BenchmarkCategory("SimpleStructDeserialize")]
    public SimpleStruct NinoDeserializeSimpleStruct()
    {
        NinoGen.Deserializer.Deserialize(_serializedSimpleStruct[2], out SimpleStruct ret);
        return ret;
    }

    [Benchmark, BenchmarkCategory("SimpleClassesDeserialize")]
    public SimpleClass[] MessagePackDeserializeSimpleClasses()
    {
        return MessagePackSerializer.Deserialize<SimpleClass[]>(_serializedSimpleClasses[0]);
    }

    [Benchmark, BenchmarkCategory("SimpleClassesDeserialize")]
    public SimpleClass[] MemoryPackDeserializeSimpleClasses()
    {
        return MemoryPackSerializer.Deserialize<SimpleClass[]>(_serializedSimpleClasses[1]);
    }

    [Benchmark(Baseline = true), BenchmarkCategory("SimpleClassesDeserialize")]
    public SimpleClass[] NinoDeserializeSimpleClasses()
    {
        NinoGen.Deserializer.Deserialize(_serializedSimpleClasses[2], out SimpleClass[] ret);
        return ret;
    }

    [Benchmark, BenchmarkCategory("SimpleStructsDeserialize")]
    public SimpleStruct[] MessagePackDeserializeSimpleStructs()
    {
        return MessagePackSerializer.Deserialize<SimpleStruct[]>(_serializedSimpleStructs[0]);
    }

    [Benchmark, BenchmarkCategory("SimpleStructsDeserialize")]
    public SimpleStruct[] MemoryPackDeserializeSimpleStructs()
    {
        return MemoryPackSerializer.Deserialize<SimpleStruct[]>(_serializedSimpleStructs[1]);
    }

    [Benchmark(Baseline = true), BenchmarkCategory("SimpleStructsDeserialize")]
    public SimpleStruct[] NinoDeserializeSimpleStructs()
    {
        NinoGen.Deserializer.Deserialize(_serializedSimpleStructs[2], out SimpleStruct[] ret);
        return ret;
    }

    [Benchmark, BenchmarkCategory("VectorsDeserialize")]
    public Vector4[] MessagePackDeserializeVectors()
    {
        return MessagePackSerializer.Deserialize<Vector4[]>(_serializedVectors[0]);
    }

    [Benchmark, BenchmarkCategory("VectorsDeserialize")]
    public Vector4[] MemoryPackDeserializeVectors()
    {
        return MemoryPackSerializer.Deserialize<Vector4[]>(_serializedVectors[1]);
    }

    [Benchmark(Baseline = true), BenchmarkCategory("VectorsDeserialize")]
    public Vector4[] NinoDeserializeVectors()
    {
        NinoGen.Deserializer.Deserialize(_serializedVectors[2], out Vector4[] ret);
        return ret;
    }
}