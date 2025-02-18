using System.Buffers;
using System.Linq;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using MemoryPack;
using MessagePack;

#nullable disable
namespace Nino.Benchmark;

[PayloadColumn]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[SimpleJob(RuntimeMoniker.Net90, warmupCount: 1, iterationCount: 20)]
[MarkdownExporter]
public class SimpleTest
{
    private readonly SimpleClass _simpleClass;
    private readonly SimpleClass[] _simpleClasses;
    private readonly SimpleStruct _simpleStruct;
    private readonly SimpleStruct[] _simpleStructs;

    private readonly ArrayBufferWriter<byte> _bufferWriter;

    private readonly byte[][] _serializedSimpleClass;
    private readonly byte[][] _serializedSimpleStruct;
    private readonly byte[][] _serializedSimpleClasses;
    private readonly byte[][] _serializedSimpleStructs;

    public SimpleTest()
    {
        _simpleClass = SimpleClass.Create();
        _simpleClasses = Enumerable.Range(0, 100).Select(_ => SimpleClass.Create()).ToArray();
        _simpleStruct = SimpleStruct.Create();
        _simpleStructs = Enumerable.Range(0, 100).Select(_ => SimpleStruct.Create()).ToArray();

        _bufferWriter = new ArrayBufferWriter<byte>(1024);

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
    }

    [Benchmark, BenchmarkCategory("SimpleClassSerialize")]
    public int MessagePackSerializeSimpleClass()
    {
        _bufferWriter.Clear();
        MessagePackSerializer.Serialize(_bufferWriter, _simpleClass);
        return _bufferWriter.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("SimpleClassSerialize")]
    public int MemoryPackSerializeSimpleClass()
    {
        _bufferWriter.Clear();
        MemoryPackSerializer.Serialize(_bufferWriter, _simpleClass);
        return _bufferWriter.WrittenCount;
    }

    [Benchmark(Baseline = true), BenchmarkCategory("SimpleClassSerialize")]
    public int NinoSerializeSimpleClass()
    {
        _bufferWriter.Clear();
        NinoGen.Serializer.Serialize(_simpleClass, _bufferWriter);
        return _bufferWriter.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("SimpleStructSerialize")]
    public int MessagePackSerializeSimpleStruct()
    {
        _bufferWriter.Clear();
        MessagePackSerializer.Serialize(_bufferWriter, _simpleStruct);
        return _bufferWriter.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("SimpleStructSerialize")]
    public int MemoryPackSerializeSimpleStruct()
    {
        _bufferWriter.Clear();
        MemoryPackSerializer.Serialize(_bufferWriter, _simpleStruct);
        return _bufferWriter.WrittenCount;
    }

    [Benchmark(Baseline = true), BenchmarkCategory("SimpleStructSerialize")]
    public int NinoSerializeSimpleStruct()
    {
        _bufferWriter.Clear();
        NinoGen.Serializer.Serialize(_simpleStruct, _bufferWriter);
        return _bufferWriter.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("SimpleClassesSerialize")]
    public int MessagePackSerializeSimpleClasses()
    {
        _bufferWriter.Clear();
        MessagePackSerializer.Serialize(_bufferWriter, _simpleClasses);
        return _bufferWriter.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("SimpleClassesSerialize")]
    public int MemoryPackSerializeSimpleClasses()
    {
        _bufferWriter.Clear();
        MemoryPackSerializer.Serialize(_bufferWriter, _simpleClasses);
        return _bufferWriter.WrittenCount;
    }

    [Benchmark(Baseline = true), BenchmarkCategory("SimpleClassesSerialize")]
    public int NinoSerializeSimpleClasses()
    {
        _bufferWriter.Clear();
        NinoGen.Serializer.Serialize(_simpleClasses, _bufferWriter);
        return _bufferWriter.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("SimpleStructsSerialize")]
    public int MessagePackSerializeSimpleStructs()
    {
        _bufferWriter.Clear();
        MessagePackSerializer.Serialize(_bufferWriter, _simpleStructs);
        return _bufferWriter.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("SimpleStructsSerialize")]
    public int MemoryPackSerializeSimpleStructs()
    {
        _bufferWriter.Clear();
        MemoryPackSerializer.Serialize(_bufferWriter, _simpleStructs);
        return _bufferWriter.WrittenCount;
    }

    [Benchmark(Baseline = true), BenchmarkCategory("SimpleStructsSerialize")]
    public int NinoSerializeSimpleStructs()
    {
        _bufferWriter.Clear();
        NinoGen.Serializer.Serialize(_simpleStructs, _bufferWriter);
        return _bufferWriter.WrittenCount;
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
}