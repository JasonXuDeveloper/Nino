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
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[ShortRunJob(RuntimeMoniker.Net90)]
[GcServer(true)]
[MarkdownExporter]
public class SimpleTest
{
    private readonly SimpleClass _simpleClass;
    private readonly SimpleClass[] _simpleClasses;
    private readonly SimpleStruct _simpleStruct;
    private readonly SimpleStruct[] _simpleStructs;
    private readonly Vector4[] _vectors;

    private static readonly NinoArrayBufferWriter NinoBuffer = new(1024 * 1024 * 32);
    private static readonly ArrayBufferWriter<byte> MemoryPackBuffer = new(1024 * 1024 * 32);
    private static readonly ArrayBufferWriter<byte> MessagePackBuffer = new(1024 * 1024 * 32);

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

        // warm up
        _ = NinoGen.Deserializer.Deserialize<byte>(new byte[1]);
    }

    [Benchmark(Baseline = true), BenchmarkCategory("SimpleClassSerialize")]
    public int NinoSerializeSimpleClassFast()
    {
        NinoBuffer.ResetWrittenCount();
        NinoGen.Serializer.Serialize(_simpleClass, NinoBuffer);
        return NinoBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("SimpleClassSerialize")]
    public int NinoSerializeSimpleClassGeneric()
    {
        NinoBuffer.ResetWrittenCount();
        NinoGen.Serializer.Serialize<SimpleClass>(_simpleClass, NinoBuffer);
        return NinoBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("SimpleClassSerialize")]
    public int MemoryPackSerializeSimpleClass()
    {
        MemoryPackBuffer.ResetWrittenCount();
        MemoryPackSerializer.Serialize(MemoryPackBuffer, _simpleClass);
        return MemoryPackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("SimpleClassSerialize")]
    public int MessagePackSerializeSimpleClass()
    {
        MessagePackBuffer.ResetWrittenCount();
        MessagePackSerializer.Serialize(MessagePackBuffer, _simpleClass);
        return MessagePackBuffer.WrittenCount;
    }

    [Benchmark(Baseline = true), BenchmarkCategory("SimpleStructSerialize")]
    public int NinoSerializeSimpleStructFast()
    {
        NinoBuffer.ResetWrittenCount();
        NinoGen.Serializer.Serialize(_simpleStruct, NinoBuffer);
        return NinoBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("SimpleStructSerialize")]
    public int NinoSerializeSimpleStructGeneric()
    {
        NinoBuffer.ResetWrittenCount();
        // ReSharper disable once RedundantTypeArgumentsOfMethod
        NinoGen.Serializer.Serialize<SimpleStruct>(_simpleStruct, NinoBuffer);
        return NinoBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("SimpleStructSerialize")]
    public int MemoryPackSerializeSimpleStruct()
    {
        MemoryPackBuffer.ResetWrittenCount();
        MemoryPackSerializer.Serialize(MemoryPackBuffer, _simpleStruct);
        return MemoryPackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("SimpleStructSerialize")]
    public int MessagePackSerializeSimpleStruct()
    {
        MessagePackBuffer.ResetWrittenCount();
        MessagePackSerializer.Serialize(MessagePackBuffer, _simpleStruct);
        return MessagePackBuffer.WrittenCount;
    }

    [Benchmark(Baseline = true), BenchmarkCategory("SimpleClassesSerialize")]
    public int NinoSerializeSimpleClassesFast()
    {
        NinoBuffer.ResetWrittenCount();
        NinoGen.Serializer.Serialize(_simpleClasses, NinoBuffer);
        return NinoBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("SimpleClassesSerialize")]
    public int NinoSerializeSimpleClassesGeneric()
    {
        NinoBuffer.ResetWrittenCount();
        NinoGen.Serializer.Serialize<SimpleClass[]>(_simpleClasses, NinoBuffer);
        return NinoBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("SimpleClassesSerialize")]
    public int MemoryPackSerializeSimpleClasses()
    {
        MemoryPackBuffer.ResetWrittenCount();
        MemoryPackSerializer.Serialize(MemoryPackBuffer, _simpleClasses);
        return MemoryPackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("SimpleClassesSerialize")]
    public int MessagePackSerializeSimpleClasses()
    {
        MessagePackBuffer.ResetWrittenCount();
        MessagePackSerializer.Serialize(MessagePackBuffer, _simpleClasses);
        return MessagePackBuffer.WrittenCount;
    }

    [Benchmark(Baseline = true), BenchmarkCategory("SimpleStructsSerialize")]
    public int NinoSerializeSimpleStructsFast()
    {
        NinoBuffer.ResetWrittenCount();
        NinoGen.Serializer.Serialize(_simpleStructs, NinoBuffer);
        return NinoBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("SimpleStructsSerialize")]
    public int NinoSerializeSimpleStructsGeneric()
    {
        NinoBuffer.ResetWrittenCount();
        NinoGen.Serializer.Serialize<SimpleStruct[]>(_simpleStructs, NinoBuffer);
        return NinoBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("SimpleStructsSerialize")]
    public int MemoryPackSerializeSimpleStructs()
    {
        MemoryPackBuffer.ResetWrittenCount();
        MemoryPackSerializer.Serialize(MemoryPackBuffer, _simpleStructs);
        return MemoryPackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("SimpleStructsSerialize")]
    public int MessagePackSerializeSimpleStructs()
    {
        MessagePackBuffer.ResetWrittenCount();
        MessagePackSerializer.Serialize(MessagePackBuffer, _simpleStructs);
        return MessagePackBuffer.WrittenCount;
    }

    [Benchmark(Baseline = true), BenchmarkCategory("VectorsSerialize")]
    public int NinoSerializeVectorsFast()
    {
        NinoBuffer.ResetWrittenCount();
        NinoGen.Serializer.Serialize(_vectors, NinoBuffer);
        return NinoBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("VectorsSerialize")]
    public int NinoSerializeVectorsGeneric()
    {
        NinoBuffer.ResetWrittenCount();
        NinoGen.Serializer.Serialize<Vector4[]>(_vectors, NinoBuffer);
        return NinoBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("VectorsSerialize")]
    public int MemoryPackSerializeVectors()
    {
        MemoryPackBuffer.ResetWrittenCount();
        MemoryPackSerializer.Serialize(MemoryPackBuffer, _vectors);
        return MemoryPackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("VectorsSerialize")]
    public int MessagePackSerializeVectors()
    {
        MessagePackBuffer.ResetWrittenCount();
        MessagePackSerializer.Serialize(MessagePackBuffer, _vectors);
        return MessagePackBuffer.WrittenCount;
    }

    [Benchmark(Baseline = true), BenchmarkCategory("SimpleClassDeserialize")]
    public SimpleClass NinoDeserializeSimpleClassFast()
    {
        NinoGen.Deserializer.Deserialize(_serializedSimpleClass[2], out SimpleClass ret);
        return ret;
    }

    [Benchmark, BenchmarkCategory("SimpleClassDeserialize")]
    public SimpleClass NinoDeserializeSimpleClassGeneric()
    {
        return NinoGen.Deserializer.Deserialize<SimpleClass>(_serializedSimpleClass[2]);
    }

    [Benchmark, BenchmarkCategory("SimpleClassDeserialize")]
    public SimpleClass MemoryPackDeserializeSimpleClass()
    {
        return MemoryPackSerializer.Deserialize<SimpleClass>(_serializedSimpleClass[1]);
    }

    [Benchmark, BenchmarkCategory("SimpleClassDeserialize")]
    public SimpleClass MessagePackDeserializeSimpleClass()
    {
        return MessagePackSerializer.Deserialize<SimpleClass>(_serializedSimpleClass[0]);
    }

    [Benchmark(Baseline = true), BenchmarkCategory("SimpleStructDeserialize")]
    public SimpleStruct NinoDeserializeSimpleStructFast()
    {
        NinoGen.Deserializer.Deserialize(_serializedSimpleStruct[2], out SimpleStruct ret);
        return ret;
    }

    [Benchmark, BenchmarkCategory("SimpleStructDeserialize")]
    public SimpleStruct NinoDeserializeSimpleStructGeneric()
    {
        return NinoGen.Deserializer.Deserialize<SimpleStruct>(_serializedSimpleStruct[2]);
    }

    [Benchmark, BenchmarkCategory("SimpleStructDeserialize")]
    public SimpleStruct MemoryPackDeserializeSimpleStruct()
    {
        return MemoryPackSerializer.Deserialize<SimpleStruct>(_serializedSimpleStruct[1]);
    }

    [Benchmark, BenchmarkCategory("SimpleStructDeserialize")]
    public SimpleStruct MessagePackDeserializeSimpleStruct()
    {
        return MessagePackSerializer.Deserialize<SimpleStruct>(_serializedSimpleStruct[0]);
    }

    [Benchmark(Baseline = true), BenchmarkCategory("SimpleClassesDeserialize")]
    public SimpleClass[] NinoDeserializeSimpleClassesFast()
    {
        NinoGen.Deserializer.Deserialize(_serializedSimpleClasses[2], out SimpleClass[] ret);
        return ret;
    }

    [Benchmark, BenchmarkCategory("SimpleClassesDeserialize")]
    public SimpleClass[] NinoDeserializeSimpleClassesGeneric()
    {
        return NinoGen.Deserializer.Deserialize<SimpleClass[]>(_serializedSimpleClasses[2]);
    }

    [Benchmark, BenchmarkCategory("SimpleClassesDeserialize")]
    public SimpleClass[] MemoryPackDeserializeSimpleClasses()
    {
        return MemoryPackSerializer.Deserialize<SimpleClass[]>(_serializedSimpleClasses[1]);
    }

    [Benchmark, BenchmarkCategory("SimpleClassesDeserialize")]
    public SimpleClass[] MessagePackDeserializeSimpleClasses()
    {
        return MessagePackSerializer.Deserialize<SimpleClass[]>(_serializedSimpleClasses[0]);
    }

    [Benchmark(Baseline = true), BenchmarkCategory("SimpleStructsDeserialize")]
    public SimpleStruct[] NinoDeserializeSimpleStructsFast()
    {
        NinoGen.Deserializer.Deserialize(_serializedSimpleStructs[2], out SimpleStruct[] ret);
        return ret;
    }

    [Benchmark, BenchmarkCategory("SimpleStructsDeserialize")]
    public SimpleStruct[] NinoDeserializeSimpleStructsGeneric()
    {
        return NinoGen.Deserializer.Deserialize<SimpleStruct[]>(_serializedSimpleStructs[2]);
    }

    [Benchmark, BenchmarkCategory("SimpleStructsDeserialize")]
    public SimpleStruct[] MemoryPackDeserializeSimpleStructs()
    {
        return MemoryPackSerializer.Deserialize<SimpleStruct[]>(_serializedSimpleStructs[1]);
    }

    [Benchmark, BenchmarkCategory("SimpleStructsDeserialize")]
    public SimpleStruct[] MessagePackDeserializeSimpleStructs()
    {
        return MessagePackSerializer.Deserialize<SimpleStruct[]>(_serializedSimpleStructs[0]);
    }

    [Benchmark(Baseline = true), BenchmarkCategory("VectorsDeserialize")]
    public Vector4[] NinoDeserializeVectorsFast()
    {
        NinoGen.Deserializer.Deserialize(_serializedVectors[2], out Vector4[] ret);
        return ret;
    }

    [Benchmark, BenchmarkCategory("VectorsDeserialize")]
    public Vector4[] NinoDeserializeVectorsGeneric()
    {
        return NinoGen.Deserializer.Deserialize<Vector4[]>(_serializedVectors[2]);
    }

    [Benchmark, BenchmarkCategory("VectorsDeserialize")]
    public Vector4[] MemoryPackDeserializeVectors()
    {
        return MemoryPackSerializer.Deserialize<Vector4[]>(_serializedVectors[1]);
    }

    [Benchmark, BenchmarkCategory("VectorsDeserialize")]
    public Vector4[] MessagePackDeserializeVectors()
    {
        return MessagePackSerializer.Deserialize<Vector4[]>(_serializedVectors[0]);
    }
}