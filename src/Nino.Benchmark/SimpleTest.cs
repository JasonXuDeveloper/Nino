using System.Linq;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using MemoryPack;
using MessagePack;

#nullable disable
namespace Nino.Benchmark;

[PayloadColumn]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[SimpleJob(RuntimeMoniker.Net90, warmupCount: 1, iterationCount: 10)]
[MarkdownExporter]
public class SimpleTest
{
    private readonly SimpleClass _simpleClass;
    private readonly SimpleClass[] _simpleClasses;
    private readonly SimpleStruct _simpleStruct;
    private readonly SimpleStruct[] _simpleStructs;

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

        _serializedSimpleClass = new byte[3][];
        _serializedSimpleClass[0] = MessagePackSerializer.Serialize(_simpleClass);
        _serializedSimpleClass[1] = MemoryPackSerializer.Serialize(_simpleClass);
        _serializedSimpleClass[2] = Nino_Benchmark_Nino.Serializer.Serialize(_simpleClass);

        _serializedSimpleStruct = new byte[3][];
        _serializedSimpleStruct[0] = MessagePackSerializer.Serialize(_simpleStruct);
        _serializedSimpleStruct[1] = MemoryPackSerializer.Serialize(_simpleStruct);
        _serializedSimpleStruct[2] = Nino_Benchmark_Nino.Serializer.Serialize(_simpleStruct);

        _serializedSimpleClasses = new byte[3][];
        _serializedSimpleClasses[0] = MessagePackSerializer.Serialize(_simpleClasses);
        _serializedSimpleClasses[1] = MemoryPackSerializer.Serialize(_simpleClasses);
        _serializedSimpleClasses[2] = Nino_Benchmark_Nino.Serializer.Serialize(_simpleClasses);

        _serializedSimpleStructs = new byte[3][];
        _serializedSimpleStructs[0] = MessagePackSerializer.Serialize(_simpleStructs);
        _serializedSimpleStructs[1] = MemoryPackSerializer.Serialize(_simpleStructs);
        _serializedSimpleStructs[2] = Nino_Benchmark_Nino.Serializer.Serialize(_simpleStructs);
    }

    [Benchmark(Baseline = true), BenchmarkCategory("SimpleClassSerialize")]
    public byte[] MessagePackSerializeSimpleClass()
    {
        return MessagePackSerializer.Serialize(_simpleClass);
    }

    [Benchmark, BenchmarkCategory("SimpleClassSerialize")]
    public byte[] MemoryPackSerializeSimpleClass()
    {
        return MemoryPackSerializer.Serialize(_simpleClass);
    }

    [Benchmark, BenchmarkCategory("SimpleClassSerialize")]
    public byte[] NinoSerializeSimpleClass()
    {
        return Nino_Benchmark_Nino.Serializer.Serialize(_simpleClass);
    }

    [Benchmark(Baseline = true), BenchmarkCategory("SimpleStructSerialize")]
    public byte[] MessagePackSerializeSimpleStruct()
    {
        return MessagePackSerializer.Serialize(_simpleStruct);
    }

    [Benchmark, BenchmarkCategory("SimpleStructSerialize")]
    public byte[] MemoryPackSerializeSimpleStruct()
    {
        return MemoryPackSerializer.Serialize(_simpleStruct);
    }

    [Benchmark, BenchmarkCategory("SimpleStructSerialize")]
    public byte[] NinoSerializeSimpleStruct()
    {
        return Nino_Benchmark_Nino.Serializer.Serialize(_simpleStruct);
    }

    [Benchmark(Baseline = true), BenchmarkCategory("SimpleClassesSerialize")]
    public byte[] MessagePackSerializeSimpleClasses()
    {
        return MessagePackSerializer.Serialize(_simpleClasses);
    }

    [Benchmark, BenchmarkCategory("SimpleClassesSerialize")]
    public byte[] MemoryPackSerializeSimpleClasses()
    {
        return MemoryPackSerializer.Serialize(_simpleClasses);
    }

    [Benchmark, BenchmarkCategory("SimpleClassesSerialize")]
    public byte[] NinoSerializeSimpleClasses()
    {
        return Nino_Benchmark_Nino.Serializer.Serialize(_simpleClasses);
    }

    [Benchmark(Baseline = true), BenchmarkCategory("SimpleStructsSerialize")]
    public byte[] MessagePackSerializeSimpleStructs()
    {
        return MessagePackSerializer.Serialize(_simpleStructs);
    }

    [Benchmark, BenchmarkCategory("SimpleStructsSerialize")]
    public byte[] MemoryPackSerializeSimpleStructs()
    {
        return MemoryPackSerializer.Serialize(_simpleStructs);
    }

    [Benchmark, BenchmarkCategory("SimpleStructsSerialize")]
    public byte[] NinoSerializeSimpleStructs()
    {
        return Nino_Benchmark_Nino.Serializer.Serialize(_simpleStructs);
    }

    [Benchmark(Baseline = true), BenchmarkCategory("SimpleClassDeserialize")]
    public SimpleClass MessagePackDeserializeSimpleClass()
    {
        return MessagePackSerializer.Deserialize<SimpleClass>(_serializedSimpleClass[0]);
    }

    [Benchmark, BenchmarkCategory("SimpleClassDeserialize")]
    public SimpleClass MemoryPackDeserializeSimpleClass()
    {
        return MemoryPackSerializer.Deserialize<SimpleClass>(_serializedSimpleClass[1]);
    }

    [Benchmark, BenchmarkCategory("SimpleClassDeserialize")]
    public SimpleClass NinoDeserializeSimpleClass()
    {
        Nino_Benchmark_Nino.Deserializer.Deserialize(_serializedSimpleClass[2], out SimpleClass ret);
        return ret;
    }

    [Benchmark(Baseline = true), BenchmarkCategory("SimpleStructDeserialize")]
    public SimpleStruct MessagePackDeserializeSimpleStruct()
    {
        return MessagePackSerializer.Deserialize<SimpleStruct>(_serializedSimpleStruct[0]);
    }

    [Benchmark, BenchmarkCategory("SimpleStructDeserialize")]
    public SimpleStruct MemoryPackDeserializeSimpleStruct()
    {
        return MemoryPackSerializer.Deserialize<SimpleStruct>(_serializedSimpleStruct[1]);
    }

    [Benchmark, BenchmarkCategory("SimpleStructDeserialize")]
    public SimpleStruct NinoDeserializeSimpleStruct()
    {
        Nino_Benchmark_Nino.Deserializer.Deserialize(_serializedSimpleStruct[2], out SimpleStruct ret);
        return ret;
    }

    [Benchmark(Baseline = true), BenchmarkCategory("SimpleClassesDeserialize")]
    public SimpleClass[] MessagePackDeserializeSimpleClasses()
    {
        return MessagePackSerializer.Deserialize<SimpleClass[]>(_serializedSimpleClasses[0]);
    }

    [Benchmark, BenchmarkCategory("SimpleClassesDeserialize")]
    public SimpleClass[] MemoryPackDeserializeSimpleClasses()
    {
        return MemoryPackSerializer.Deserialize<SimpleClass[]>(_serializedSimpleClasses[1]);
    }

    [Benchmark, BenchmarkCategory("SimpleClassesDeserialize")]
    public SimpleClass[] NinoDeserializeSimpleClasses()
    {
        Nino_Benchmark_Nino.Deserializer.Deserialize(_serializedSimpleClasses[2], out SimpleClass[] ret);
        return ret;
    }

    [Benchmark(Baseline = true), BenchmarkCategory("SimpleStructsDeserialize")]
    public SimpleStruct[] MessagePackDeserializeSimpleStructs()
    {
        return MessagePackSerializer.Deserialize<SimpleStruct[]>(_serializedSimpleStructs[0]);
    }

    [Benchmark, BenchmarkCategory("SimpleStructsDeserialize")]
    public SimpleStruct[] MemoryPackDeserializeSimpleStructs()
    {
        return MemoryPackSerializer.Deserialize<SimpleStruct[]>(_serializedSimpleStructs[1]);
    }

    [Benchmark, BenchmarkCategory("SimpleStructsDeserialize")]
    public SimpleStruct[] NinoDeserializeSimpleStructs()
    {
        Nino_Benchmark_Nino.Deserializer.Deserialize(_serializedSimpleStructs[2], out SimpleStruct[] ret);
        return ret;
    }
}