using System.Buffers;
using System.Linq;
using BenchmarkDotNet.Configs;
using MemoryPack;
using MessagePack;
using Nino.Core;

namespace Nino.Benchmark;

#nullable disable

/// <summary>
/// Polymorphic data written through its base type, against MemoryPack's and MessagePack's unions: one class holding
/// 1,000 elements of 20 subtypes (one to nine levels below their base), an array of 1,000 such elements, and an array
/// of 100 classes holding 50 each.
/// </summary>
[PayloadColumn]
[HideColumns("StdDev", "RatioSD", "Error")]
[MinColumn, MaxColumn]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[NinoJob]
[GcServer(true)]
[MemoryDiagnoser]
[MarkdownExporterAttribute.GitHub]
public class PolymorphicTest
{
    private static readonly PolymorphicClassBase PolymorphicInstance;
    private static readonly PolymorphicFooBase[] PolymorphicFooArray;
    private static readonly PolymorphicClassBase[] PolymorphicClassArray;

    private NinoArrayBufferWriter _ninoBuffer;
    private ArrayBufferWriter<byte> _memoryPackBuffer;
    private ArrayBufferWriter<byte> _messagePackBuffer;

    private static readonly byte[][] SerializedPolymorphicClass;
    private static readonly byte[][] SerializedPolymorphicFooArray;
    private static readonly byte[][] SerializedPolymorphicClassArray;

    static PolymorphicTest()
    {
        // One polymorphic class holding 1,000 polymorphic elements.
        PolymorphicInstance = PolymorphicClass.Create();
        var polyClass = (PolymorphicClass)PolymorphicInstance;
        for (int i = 0; i < 1000; i++)
            polyClass.Numbers2.Add(Foo(i));

        // The key polymorphism case: an array of 1,000 elements of the base type, its 20 subtypes in turn, one to
        // nine levels deep.
        PolymorphicFooArray = new PolymorphicFooBase[1000];
        for (int i = 0; i < 1000; i++)
            PolymorphicFooArray[i] = Foo(i);

        // 100 polymorphic classes (one subtype, through the base type), each holding 50 polymorphic elements.
        PolymorphicClassArray = Enumerable.Range(0, 100).Select(_ =>
        {
            var instance = (PolymorphicClassBase)PolymorphicClass.Create();
            var pClass = (PolymorphicClass)instance;
            for (int i = 0; i < 50; i++)
                pClass.Numbers2.Add(Foo(i));
            return instance;
        }).ToArray();

        SerializedPolymorphicClass = new byte[3][];
        SerializedPolymorphicClass[0] = MessagePackSerializer.Serialize(PolymorphicInstance);
        SerializedPolymorphicClass[1] = MemoryPackSerializer.Serialize(PolymorphicInstance);
        SerializedPolymorphicClass[2] = NinoSerializer.Serialize(PolymorphicInstance);

        SerializedPolymorphicFooArray = new byte[3][];
        SerializedPolymorphicFooArray[0] = MessagePackSerializer.Serialize(PolymorphicFooArray);
        SerializedPolymorphicFooArray[1] = MemoryPackSerializer.Serialize(PolymorphicFooArray);
        SerializedPolymorphicFooArray[2] = NinoSerializer.Serialize(PolymorphicFooArray);

        SerializedPolymorphicClassArray = new byte[3][];
        SerializedPolymorphicClassArray[0] = MessagePackSerializer.Serialize(PolymorphicClassArray);
        SerializedPolymorphicClassArray[1] = MemoryPackSerializer.Serialize(PolymorphicClassArray);
        SerializedPolymorphicClassArray[2] = NinoSerializer.Serialize(PolymorphicClassArray);

        BenchmarkPayloadRegistry.Register(nameof(NinoSerializePolymorphicClass), SerializedPolymorphicClass[2].Length);
        BenchmarkPayloadRegistry.Register(nameof(NinoSerializePolymorphicFooArray), SerializedPolymorphicFooArray[2].Length);
        BenchmarkPayloadRegistry.Register(nameof(NinoSerializePolymorphicClassArray), SerializedPolymorphicClassArray[2].Length);

        BenchmarkPayloadRegistry.Register(nameof(MemoryPackSerializePolymorphicClass), SerializedPolymorphicClass[1].Length);
        BenchmarkPayloadRegistry.Register(nameof(MemoryPackSerializePolymorphicFooArray), SerializedPolymorphicFooArray[1].Length);
        BenchmarkPayloadRegistry.Register(nameof(MemoryPackSerializePolymorphicClassArray), SerializedPolymorphicClassArray[1].Length);

        BenchmarkPayloadRegistry.Register(nameof(MessagePackSerializePolymorphicClass), SerializedPolymorphicClass[0].Length);
        BenchmarkPayloadRegistry.Register(nameof(MessagePackSerializePolymorphicFooArray), SerializedPolymorphicFooArray[0].Length);
        BenchmarkPayloadRegistry.Register(nameof(MessagePackSerializePolymorphicClassArray), SerializedPolymorphicClassArray[0].Length);
    }

    /// <summary>The <paramref name="i"/>th of the 20 subtypes in turn (FooA is one level below the base, FooI to FooZ nine).</summary>
    private static PolymorphicFooBase Foo(int i) => (i % 20) switch
    {
        0 => FooA.Create(),
        1 => FooB.Create(),
        2 => FooC.Create(),
        3 => FooD.Create(),
        4 => FooE.Create(),
        5 => FooF.Create(),
        6 => FooG.Create(),
        7 => FooH.Create(),
        8 => FooI.Create(),
        9 => FooJ.Create(),
        10 => FooK.Create(),
        11 => FooL.Create(),
        12 => FooM.Create(),
        13 => FooN.Create(),
        14 => FooO.Create(),
        15 => FooP.Create(),
        16 => FooQ.Create(),
        17 => FooR.Create(),
        18 => FooS.Create(),
        _ => FooZ.Create(),
    };

    [GlobalSetup]
    public void Setup()
    {
        _ninoBuffer = new NinoArrayBufferWriter(1024 * 1024);
        _memoryPackBuffer = new ArrayBufferWriter<byte>(1024 * 1024);
        _messagePackBuffer = new ArrayBufferWriter<byte>(1024 * 1024);
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

    [Benchmark(Baseline = true), BenchmarkCategory("PolymorphicFooArraySerialize")]
    public int NinoSerializePolymorphicFooArray()
    {
        _ninoBuffer.ResetWrittenCount();
        NinoSerializer.Serialize(PolymorphicFooArray, _ninoBuffer);
        return _ninoBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("PolymorphicFooArraySerialize")]
    public int MemoryPackSerializePolymorphicFooArray()
    {
        _memoryPackBuffer.ResetWrittenCount();
        MemoryPackSerializer.Serialize(_memoryPackBuffer, PolymorphicFooArray);
        return _memoryPackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("PolymorphicFooArraySerialize")]
    public int MessagePackSerializePolymorphicFooArray()
    {
        _messagePackBuffer.ResetWrittenCount();
        MessagePackSerializer.Serialize(_messagePackBuffer, PolymorphicFooArray);
        return _messagePackBuffer.WrittenCount;
    }

    [Benchmark(Baseline = true), BenchmarkCategory("PolymorphicClassArraySerialize")]
    public int NinoSerializePolymorphicClassArray()
    {
        _ninoBuffer.ResetWrittenCount();
        NinoSerializer.Serialize(PolymorphicClassArray, _ninoBuffer);
        return _ninoBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("PolymorphicClassArraySerialize")]
    public int MemoryPackSerializePolymorphicClassArray()
    {
        _memoryPackBuffer.ResetWrittenCount();
        MemoryPackSerializer.Serialize(_memoryPackBuffer, PolymorphicClassArray);
        return _memoryPackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("PolymorphicClassArraySerialize")]
    public int MessagePackSerializePolymorphicClassArray()
    {
        _messagePackBuffer.ResetWrittenCount();
        MessagePackSerializer.Serialize(_messagePackBuffer, PolymorphicClassArray);
        return _messagePackBuffer.WrittenCount;
    }

    // Deserialization tests
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

    [Benchmark(Baseline = true), BenchmarkCategory("PolymorphicFooArrayDeserialize")]
    public PolymorphicFooBase[] NinoDeserializePolymorphicFooArray()
    {
        return NinoDeserializer.Deserialize<PolymorphicFooBase[]>(SerializedPolymorphicFooArray[2]);
    }

    [Benchmark, BenchmarkCategory("PolymorphicFooArrayDeserialize")]
    public PolymorphicFooBase[] MemoryPackDeserializePolymorphicFooArray()
    {
        return MemoryPackSerializer.Deserialize<PolymorphicFooBase[]>(SerializedPolymorphicFooArray[1]);
    }

    [Benchmark, BenchmarkCategory("PolymorphicFooArrayDeserialize")]
    public PolymorphicFooBase[] MessagePackDeserializePolymorphicFooArray()
    {
        return MessagePackSerializer.Deserialize<PolymorphicFooBase[]>(SerializedPolymorphicFooArray[0]);
    }

    [Benchmark(Baseline = true), BenchmarkCategory("PolymorphicClassArrayDeserialize")]
    public PolymorphicClassBase[] NinoDeserializePolymorphicClassArray()
    {
        return NinoDeserializer.Deserialize<PolymorphicClassBase[]>(SerializedPolymorphicClassArray[2]);
    }

    [Benchmark, BenchmarkCategory("PolymorphicClassArrayDeserialize")]
    public PolymorphicClassBase[] MemoryPackDeserializePolymorphicClassArray()
    {
        return MemoryPackSerializer.Deserialize<PolymorphicClassBase[]>(SerializedPolymorphicClassArray[1]);
    }

    [Benchmark, BenchmarkCategory("PolymorphicClassArrayDeserialize")]
    public PolymorphicClassBase[] MessagePackDeserializePolymorphicClassArray()
    {
        return MessagePackSerializer.Deserialize<PolymorphicClassBase[]>(SerializedPolymorphicClassArray[0]);
    }
}
