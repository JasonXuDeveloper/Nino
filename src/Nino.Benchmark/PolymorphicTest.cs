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
    // Test single polymorphic class with nested polymorphic collections
    private static readonly PolymorphicClassBase PolymorphicInstance;

    // Test array of polymorphic base type with diverse derived types
    private static readonly PolymorphicFooBase[] PolymorphicFooArray;

    // Test array of polymorphic classes
    private static readonly PolymorphicClassBase[] PolymorphicClassArray;

    private NinoArrayBufferWriter _ninoBuffer;
    private ArrayBufferWriter<byte> _memoryPackBuffer;
    private ArrayBufferWriter<byte> _messagePackBuffer;

    private static readonly byte[][] SerializedPolymorphicClass;
    private static readonly byte[][] SerializedPolymorphicFooArray;
    private static readonly byte[][] SerializedPolymorphicClassArray;

    static PolymorphicTest()
    {
        // Single polymorphic class with 1000 nested polymorphic elements
        PolymorphicInstance = PolymorphicClass.Create();
        var polyClass = (PolymorphicClass)PolymorphicInstance;
        for (int i = 0; i < 1000; i++)
        {
            switch (i % 20)
            {
                case 0: polyClass.Numbers2.Add(FooA.Create()); break;
                case 1: polyClass.Numbers2.Add(FooB.Create()); break;
                case 2: polyClass.Numbers2.Add(FooC.Create()); break;
                case 3: polyClass.Numbers2.Add(FooD.Create()); break;
                case 4: polyClass.Numbers2.Add(FooE.Create()); break;
                case 5: polyClass.Numbers2.Add(FooF.Create()); break;
                case 6: polyClass.Numbers2.Add(FooG.Create()); break;
                case 7: polyClass.Numbers2.Add(FooH.Create()); break;
                case 8: polyClass.Numbers2.Add(FooI.Create()); break;
                case 9: polyClass.Numbers2.Add(FooJ.Create()); break;
                case 10: polyClass.Numbers2.Add(FooK.Create()); break;
                case 11: polyClass.Numbers2.Add(FooL.Create()); break;
                case 12: polyClass.Numbers2.Add(FooM.Create()); break;
                case 13: polyClass.Numbers2.Add(FooN.Create()); break;
                case 14: polyClass.Numbers2.Add(FooO.Create()); break;
                case 15: polyClass.Numbers2.Add(FooP.Create()); break;
                case 16: polyClass.Numbers2.Add(FooQ.Create()); break;
                case 17: polyClass.Numbers2.Add(FooR.Create()); break;
                case 18: polyClass.Numbers2.Add(FooS.Create()); break;
                case 19: polyClass.Numbers2.Add(FooZ.Create()); break;
            }
        }

        // Array of 1000 polymorphic base type elements with diverse derived types at different depths
        // This is the KEY test for polymorphism - base type array with various subtypes
        PolymorphicFooArray = new PolymorphicFooBase[1000];
        for (int i = 0; i < 1000; i++)
        {
            switch (i % 20)
            {
                case 0: PolymorphicFooArray[i] = FooA.Create(); break;  // Depth 1
                case 1: PolymorphicFooArray[i] = FooB.Create(); break;  // Depth 2
                case 2: PolymorphicFooArray[i] = FooC.Create(); break;  // Depth 3
                case 3: PolymorphicFooArray[i] = FooD.Create(); break;  // Depth 4
                case 4: PolymorphicFooArray[i] = FooE.Create(); break;  // Depth 5
                case 5: PolymorphicFooArray[i] = FooF.Create(); break;  // Depth 6
                case 6: PolymorphicFooArray[i] = FooG.Create(); break;  // Depth 7
                case 7: PolymorphicFooArray[i] = FooH.Create(); break;  // Depth 8
                case 8: PolymorphicFooArray[i] = FooI.Create(); break;  // Depth 9
                case 9: PolymorphicFooArray[i] = FooJ.Create(); break;  // Depth 9
                case 10: PolymorphicFooArray[i] = FooK.Create(); break; // Depth 9
                case 11: PolymorphicFooArray[i] = FooL.Create(); break; // Depth 9
                case 12: PolymorphicFooArray[i] = FooM.Create(); break; // Depth 9
                case 13: PolymorphicFooArray[i] = FooN.Create(); break; // Depth 9
                case 14: PolymorphicFooArray[i] = FooO.Create(); break; // Depth 9
                case 15: PolymorphicFooArray[i] = FooP.Create(); break; // Depth 9
                case 16: PolymorphicFooArray[i] = FooQ.Create(); break; // Depth 9
                case 17: PolymorphicFooArray[i] = FooR.Create(); break; // Depth 9
                case 18: PolymorphicFooArray[i] = FooS.Create(); break; // Depth 9
                case 19: PolymorphicFooArray[i] = FooZ.Create(); break; // Depth 9
            }
        }

        // Array of 100 polymorphic classes (all same type but tests polymorphic base type serialization)
        PolymorphicClassArray = Enumerable.Range(0, 100).Select(_ =>
        {
            var instance = (PolymorphicClassBase)PolymorphicClass.Create();
            var pClass = (PolymorphicClass)instance;
            // Each instance contains 50 polymorphic elements
            for (int i = 0; i < 50; i++)
            {
                switch (i % 20)
                {
                    case 0: pClass.Numbers2.Add(FooA.Create()); break;
                    case 1: pClass.Numbers2.Add(FooB.Create()); break;
                    case 2: pClass.Numbers2.Add(FooC.Create()); break;
                    case 3: pClass.Numbers2.Add(FooD.Create()); break;
                    case 4: pClass.Numbers2.Add(FooE.Create()); break;
                    case 5: pClass.Numbers2.Add(FooF.Create()); break;
                    case 6: pClass.Numbers2.Add(FooG.Create()); break;
                    case 7: pClass.Numbers2.Add(FooH.Create()); break;
                    case 8: pClass.Numbers2.Add(FooI.Create()); break;
                    case 9: pClass.Numbers2.Add(FooJ.Create()); break;
                    case 10: pClass.Numbers2.Add(FooK.Create()); break;
                    case 11: pClass.Numbers2.Add(FooL.Create()); break;
                    case 12: pClass.Numbers2.Add(FooM.Create()); break;
                    case 13: pClass.Numbers2.Add(FooN.Create()); break;
                    case 14: pClass.Numbers2.Add(FooO.Create()); break;
                    case 15: pClass.Numbers2.Add(FooP.Create()); break;
                    case 16: pClass.Numbers2.Add(FooQ.Create()); break;
                    case 17: pClass.Numbers2.Add(FooR.Create()); break;
                    case 18: pClass.Numbers2.Add(FooS.Create()); break;
                    case 19: pClass.Numbers2.Add(FooZ.Create()); break;
                }
            }
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

    [GlobalSetup]
    public void Setup()
    {
        _ninoBuffer = new NinoArrayBufferWriter(1024 * 1024);
        _memoryPackBuffer = new ArrayBufferWriter<byte>(1024 * 1024);
        _messagePackBuffer = new ArrayBufferWriter<byte>(1024 * 1024);
    }

    // Test 1: Single complex polymorphic class with nested polymorphic collections
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

    // Test 2: Array of base type with diverse derived types (KEY polymorphism test)
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

    // Test 3: Array of polymorphic classes with nested polymorphic collections
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
