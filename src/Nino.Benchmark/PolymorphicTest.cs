using System.Buffers;
using System.Linq;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using MemoryPack;
using MessagePack;
using Nino.Core;

namespace Nino.Benchmark;

#nullable disable

[PayloadColumn]
[HideColumns("StdDev", "RatioSD", "Error")]
[MinColumn, MaxColumn]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[ShortRunJob(RuntimeMoniker.Net90)]
[GcServer(true)]
[MemoryDiagnoser]
[MarkdownExporterAttribute.GitHub]
public class PolymorphicTest
{
    private static readonly PolymorphicClassBase PolymorphicInstance;
    private static readonly PolymorphicClassBase[] PolymorphicArray;
    private static readonly PolymorphicFooBase PolymorphicFooInstance;
    private static readonly PolymorphicFooBase[] PolymorphicFooArray;

    private NinoArrayBufferWriter _ninoBuffer;
    private ArrayBufferWriter<byte> _memoryPackBuffer;
    private ArrayBufferWriter<byte> _messagePackBuffer;

    private static readonly byte[][] SerializedPolymorphicClass;
    private static readonly byte[][] SerializedPolymorphicClasses;
    private static readonly byte[][] SerializedPolymorphicFoo;
    private static readonly byte[][] SerializedPolymorphicFoos;

    static PolymorphicTest()
    {
        PolymorphicInstance = PolymorphicClass.Create();
        PolymorphicArray = Enumerable.Range(0, 100).Select(_ => (PolymorphicClassBase)PolymorphicClass.Create()).ToArray();

        PolymorphicFooInstance = PolymorphicFoo.Create();
        PolymorphicFooArray = Enumerable.Range(0, 100).Select(_ => (PolymorphicFooBase)PolymorphicFoo.Create()).ToArray();

        SerializedPolymorphicClass = new byte[3][];
        SerializedPolymorphicClass[0] = MessagePackSerializer.Serialize(PolymorphicInstance);
        SerializedPolymorphicClass[1] = MemoryPackSerializer.Serialize(PolymorphicInstance);
        SerializedPolymorphicClass[2] = NinoSerializer.Serialize(PolymorphicInstance);

        SerializedPolymorphicClasses = new byte[3][];
        SerializedPolymorphicClasses[0] = MessagePackSerializer.Serialize(PolymorphicArray);
        SerializedPolymorphicClasses[1] = MemoryPackSerializer.Serialize(PolymorphicArray);
        SerializedPolymorphicClasses[2] = NinoSerializer.Serialize(PolymorphicArray);

        SerializedPolymorphicFoo = new byte[3][];
        SerializedPolymorphicFoo[0] = MessagePackSerializer.Serialize(PolymorphicFooInstance);
        SerializedPolymorphicFoo[1] = MemoryPackSerializer.Serialize(PolymorphicFooInstance);
        SerializedPolymorphicFoo[2] = NinoSerializer.Serialize(PolymorphicFooInstance);

        SerializedPolymorphicFoos = new byte[3][];
        SerializedPolymorphicFoos[0] = MessagePackSerializer.Serialize(PolymorphicFooArray);
        SerializedPolymorphicFoos[1] = MemoryPackSerializer.Serialize(PolymorphicFooArray);
        SerializedPolymorphicFoos[2] = NinoSerializer.Serialize(PolymorphicFooArray);

        BenchmarkPayloadRegistry.Register(nameof(NinoSerializePolymorphicClass), SerializedPolymorphicClass[2].Length);
        BenchmarkPayloadRegistry.Register(nameof(NinoSerializePolymorphicClasses), SerializedPolymorphicClasses[2].Length);
        BenchmarkPayloadRegistry.Register(nameof(NinoSerializePolymorphicFoo), SerializedPolymorphicFoo[2].Length);
        BenchmarkPayloadRegistry.Register(nameof(NinoSerializePolymorphicFoos), SerializedPolymorphicFoos[2].Length);

        BenchmarkPayloadRegistry.Register(nameof(MemoryPackSerializePolymorphicClass), SerializedPolymorphicClass[1].Length);
        BenchmarkPayloadRegistry.Register(nameof(MemoryPackSerializePolymorphicClasses), SerializedPolymorphicClasses[1].Length);
        BenchmarkPayloadRegistry.Register(nameof(MemoryPackSerializePolymorphicFoo), SerializedPolymorphicFoo[1].Length);
        BenchmarkPayloadRegistry.Register(nameof(MemoryPackSerializePolymorphicFoos), SerializedPolymorphicFoos[1].Length);

        BenchmarkPayloadRegistry.Register(nameof(MessagePackSerializePolymorphicClass), SerializedPolymorphicClass[0].Length);
        BenchmarkPayloadRegistry.Register(nameof(MessagePackSerializePolymorphicClasses), SerializedPolymorphicClasses[0].Length);
        BenchmarkPayloadRegistry.Register(nameof(MessagePackSerializePolymorphicFoo), SerializedPolymorphicFoo[0].Length);
        BenchmarkPayloadRegistry.Register(nameof(MessagePackSerializePolymorphicFoos), SerializedPolymorphicFoos[0].Length);
    }

    [GlobalSetup]
    public void Setup()
    {
        _ninoBuffer = new NinoArrayBufferWriter(320 * 1024);
        _memoryPackBuffer = new ArrayBufferWriter<byte>(320 * 1024);
        _messagePackBuffer = new ArrayBufferWriter<byte>(320 * 1024);
    }

    [Benchmark(Baseline = true), BenchmarkCategory("PolymorphicClassSerialize")]
    public int NinoSerializePolymorphicClass()
    {
        _ninoBuffer.ResetWrittenCount();
        NinoSerializer.Serialize(PolymorphicInstance, _ninoBuffer);
        return _ninoBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("PolymorphicClassSerialize")]
    public int MemoryPackSerializePolymorphicClass()
    {
        _memoryPackBuffer.ResetWrittenCount();
        MemoryPackSerializer.Serialize(_memoryPackBuffer, PolymorphicInstance);
        return _memoryPackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("PolymorphicClassSerialize")]
    public int MessagePackSerializePolymorphicClass()
    {
        _messagePackBuffer.ResetWrittenCount();
        MessagePackSerializer.Serialize(_messagePackBuffer, PolymorphicInstance);
        return _messagePackBuffer.WrittenCount;
    }

    [Benchmark(Baseline = true), BenchmarkCategory("PolymorphicClassArraySerialize")]
    public int NinoSerializePolymorphicClasses()
    {
        _ninoBuffer.ResetWrittenCount();
        NinoSerializer.Serialize(PolymorphicArray, _ninoBuffer);
        return _ninoBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("PolymorphicClassArraySerialize")]
    public int MemoryPackSerializePolymorphicClasses()
    {
        _memoryPackBuffer.ResetWrittenCount();
        MemoryPackSerializer.Serialize(_memoryPackBuffer, PolymorphicArray);
        return _memoryPackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("PolymorphicClassArraySerialize")]
    public int MessagePackSerializePolymorphicClasses()
    {
        _messagePackBuffer.ResetWrittenCount();
        MessagePackSerializer.Serialize(_messagePackBuffer, PolymorphicArray);
        return _messagePackBuffer.WrittenCount;
    }

    [Benchmark(Baseline = true), BenchmarkCategory("PolymorphicFooSerialize")]
    public int NinoSerializePolymorphicFoo()
    {
        _ninoBuffer.ResetWrittenCount();
        NinoSerializer.Serialize(PolymorphicFooInstance, _ninoBuffer);
        return _ninoBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("PolymorphicFooSerialize")]
    public int MemoryPackSerializePolymorphicFoo()
    {
        _memoryPackBuffer.ResetWrittenCount();
        MemoryPackSerializer.Serialize(_memoryPackBuffer, PolymorphicFooInstance);
        return _memoryPackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("PolymorphicFooSerialize")]
    public int MessagePackSerializePolymorphicFoo()
    {
        _messagePackBuffer.ResetWrittenCount();
        MessagePackSerializer.Serialize(_messagePackBuffer, PolymorphicFooInstance);
        return _messagePackBuffer.WrittenCount;
    }

    [Benchmark(Baseline = true), BenchmarkCategory("PolymorphicFooArraySerialize")]
    public int NinoSerializePolymorphicFoos()
    {
        _ninoBuffer.ResetWrittenCount();
        NinoSerializer.Serialize(PolymorphicFooArray, _ninoBuffer);
        return _ninoBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("PolymorphicFooArraySerialize")]
    public int MemoryPackSerializePolymorphicFoos()
    {
        _memoryPackBuffer.ResetWrittenCount();
        MemoryPackSerializer.Serialize(_memoryPackBuffer, PolymorphicFooArray);
        return _memoryPackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("PolymorphicFooArraySerialize")]
    public int MessagePackSerializePolymorphicFoos()
    {
        _messagePackBuffer.ResetWrittenCount();
        MessagePackSerializer.Serialize(_messagePackBuffer, PolymorphicFooArray);
        return _messagePackBuffer.WrittenCount;
    }

    [Benchmark(Baseline = true), BenchmarkCategory("PolymorphicClassDeserialize")]
    public PolymorphicClassBase NinoDeserializePolymorphicClass()
    {
        return NinoDeserializer.Deserialize<PolymorphicClassBase>(SerializedPolymorphicClass[2]);
    }

    [Benchmark, BenchmarkCategory("PolymorphicClassDeserialize")]
    public PolymorphicClassBase MemoryPackDeserializePolymorphicClass()
    {
        return MemoryPackSerializer.Deserialize<PolymorphicClassBase>(SerializedPolymorphicClass[1]);
    }

    [Benchmark, BenchmarkCategory("PolymorphicClassDeserialize")]
    public PolymorphicClassBase MessagePackDeserializePolymorphicClass()
    {
        return MessagePackSerializer.Deserialize<PolymorphicClassBase>(SerializedPolymorphicClass[0]);
    }

    [Benchmark(Baseline = true), BenchmarkCategory("PolymorphicClassArrayDeserialize")]
    public PolymorphicClassBase[] NinoDeserializePolymorphicClasses()
    {
        return NinoDeserializer.Deserialize<PolymorphicClassBase[]>(SerializedPolymorphicClasses[2]);
    }

    [Benchmark, BenchmarkCategory("PolymorphicClassArrayDeserialize")]
    public PolymorphicClassBase[] MemoryPackDeserializePolymorphicClasses()
    {
        return MemoryPackSerializer.Deserialize<PolymorphicClassBase[]>(SerializedPolymorphicClasses[1]);
    }

    [Benchmark, BenchmarkCategory("PolymorphicClassArrayDeserialize")]
    public PolymorphicClassBase[] MessagePackDeserializePolymorphicClasses()
    {
        return MessagePackSerializer.Deserialize<PolymorphicClassBase[]>(SerializedPolymorphicClasses[0]);
    }

    [Benchmark(Baseline = true), BenchmarkCategory("PolymorphicFooDeserialize")]
    public PolymorphicFooBase NinoDeserializePolymorphicFoo()
    {
        return NinoDeserializer.Deserialize<PolymorphicFooBase>(SerializedPolymorphicFoo[2]);
    }

    [Benchmark, BenchmarkCategory("PolymorphicFooDeserialize")]
    public PolymorphicFooBase MemoryPackDeserializePolymorphicFoo()
    {
        return MemoryPackSerializer.Deserialize<PolymorphicFooBase>(SerializedPolymorphicFoo[1]);
    }

    [Benchmark, BenchmarkCategory("PolymorphicFooDeserialize")]
    public PolymorphicFooBase MessagePackDeserializePolymorphicFoo()
    {
        return MessagePackSerializer.Deserialize<PolymorphicFooBase>(SerializedPolymorphicFoo[0]);
    }

    [Benchmark(Baseline = true), BenchmarkCategory("PolymorphicFooArrayDeserialize")]
    public PolymorphicFooBase[] NinoDeserializePolymorphicFoos()
    {
        return NinoDeserializer.Deserialize<PolymorphicFooBase[]>(SerializedPolymorphicFoos[2]);
    }

    [Benchmark, BenchmarkCategory("PolymorphicFooArrayDeserialize")]
    public PolymorphicFooBase[] MemoryPackDeserializePolymorphicFoos()
    {
        return MemoryPackSerializer.Deserialize<PolymorphicFooBase[]>(SerializedPolymorphicFoos[1]);
    }

    [Benchmark, BenchmarkCategory("PolymorphicFooArrayDeserialize")]
    public PolymorphicFooBase[] MessagePackDeserializePolymorphicFoos()
    {
        return MessagePackSerializer.Deserialize<PolymorphicFooBase[]>(SerializedPolymorphicFoos[0]);
    }
}
