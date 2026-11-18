// Probe for #320, bench/ab only (never on main): polymorphic dictionaries of 1 to 100 pairs, int and string keys,
// Nino against MemoryPack and LuminPack. One method per size so compare/ab.py pairs the libraries by name.
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using BenchmarkDotNet.Configs;
using LuminPack;
using LuminPack.Utility;
using MemoryPack;
using Nino.Core;

namespace Nino.Benchmark;

#nullable disable

[NinoJob]
[GcServer(true)]
[MemoryDiagnoser]
[MinColumn, MaxColumn]
[HideColumns("StdDev", "RatioSD", "Error")]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[MarkdownExporterAttribute.GitHub]
public class PolyDictProbe
{
    private NinoArrayBufferWriter _ninoBuffer;
    private ArrayBufferWriter<byte> _memoryPackBuffer;
    private LuminBufferWriter _luminPackBuffer;

    private static PolymorphicFooBase Foo(int i) => (i % 20) switch
    {
        0 => FooA.Create(), 1 => FooB.Create(), 2 => FooC.Create(), 3 => FooD.Create(), 4 => FooE.Create(),
        5 => FooF.Create(), 6 => FooG.Create(), 7 => FooH.Create(), 8 => FooI.Create(), 9 => FooJ.Create(),
        10 => FooK.Create(), 11 => FooL.Create(), 12 => FooM.Create(), 13 => FooN.Create(), 14 => FooO.Create(),
        15 => FooP.Create(), 16 => FooQ.Create(), 17 => FooR.Create(), 18 => FooS.Create(), _ => FooZ.Create(),
    };

    private static readonly Dictionary<int, PolymorphicFooBase> IntMap1 = Enumerable.Range(0, 1).ToDictionary(i => i * 7, Foo);
    private static readonly byte[] NinoIntMap1 = NinoSerializer.Serialize(IntMap1);
    private static readonly byte[] MemoryPackIntMap1 = MemoryPackSerializer.Serialize(IntMap1);
    private static readonly byte[] LuminPackIntMap1 = LuminPackBuffers.Bytes(IntMap1, (v, w) => LuminPackSerializer.Serialize(v, w));
    private static readonly Dictionary<int, PolymorphicFooBase> IntMap4 = Enumerable.Range(0, 4).ToDictionary(i => i * 7, Foo);
    private static readonly byte[] NinoIntMap4 = NinoSerializer.Serialize(IntMap4);
    private static readonly byte[] MemoryPackIntMap4 = MemoryPackSerializer.Serialize(IntMap4);
    private static readonly byte[] LuminPackIntMap4 = LuminPackBuffers.Bytes(IntMap4, (v, w) => LuminPackSerializer.Serialize(v, w));
    private static readonly Dictionary<int, PolymorphicFooBase> IntMap12 = Enumerable.Range(0, 12).ToDictionary(i => i * 7, Foo);
    private static readonly byte[] NinoIntMap12 = NinoSerializer.Serialize(IntMap12);
    private static readonly byte[] MemoryPackIntMap12 = MemoryPackSerializer.Serialize(IntMap12);
    private static readonly byte[] LuminPackIntMap12 = LuminPackBuffers.Bytes(IntMap12, (v, w) => LuminPackSerializer.Serialize(v, w));
    private static readonly Dictionary<int, PolymorphicFooBase> IntMap32 = Enumerable.Range(0, 32).ToDictionary(i => i * 7, Foo);
    private static readonly byte[] NinoIntMap32 = NinoSerializer.Serialize(IntMap32);
    private static readonly byte[] MemoryPackIntMap32 = MemoryPackSerializer.Serialize(IntMap32);
    private static readonly byte[] LuminPackIntMap32 = LuminPackBuffers.Bytes(IntMap32, (v, w) => LuminPackSerializer.Serialize(v, w));
    private static readonly Dictionary<int, PolymorphicFooBase> IntMap100 = Enumerable.Range(0, 100).ToDictionary(i => i * 7, Foo);
    private static readonly byte[] NinoIntMap100 = NinoSerializer.Serialize(IntMap100);
    private static readonly byte[] MemoryPackIntMap100 = MemoryPackSerializer.Serialize(IntMap100);
    private static readonly byte[] LuminPackIntMap100 = LuminPackBuffers.Bytes(IntMap100, (v, w) => LuminPackSerializer.Serialize(v, w));
    private static readonly Dictionary<string, PolymorphicFooBase> StringMap1 = Enumerable.Range(0, 1).ToDictionary(i => "key_" + i, Foo);
    private static readonly byte[] NinoStringMap1 = NinoSerializer.Serialize(StringMap1);
    private static readonly byte[] MemoryPackStringMap1 = MemoryPackSerializer.Serialize(StringMap1);
    private static readonly byte[] LuminPackStringMap1 = LuminPackBuffers.Bytes(StringMap1, (v, w) => LuminPackSerializer.Serialize(v, w));
    private static readonly Dictionary<string, PolymorphicFooBase> StringMap4 = Enumerable.Range(0, 4).ToDictionary(i => "key_" + i, Foo);
    private static readonly byte[] NinoStringMap4 = NinoSerializer.Serialize(StringMap4);
    private static readonly byte[] MemoryPackStringMap4 = MemoryPackSerializer.Serialize(StringMap4);
    private static readonly byte[] LuminPackStringMap4 = LuminPackBuffers.Bytes(StringMap4, (v, w) => LuminPackSerializer.Serialize(v, w));
    private static readonly Dictionary<string, PolymorphicFooBase> StringMap12 = Enumerable.Range(0, 12).ToDictionary(i => "key_" + i, Foo);
    private static readonly byte[] NinoStringMap12 = NinoSerializer.Serialize(StringMap12);
    private static readonly byte[] MemoryPackStringMap12 = MemoryPackSerializer.Serialize(StringMap12);
    private static readonly byte[] LuminPackStringMap12 = LuminPackBuffers.Bytes(StringMap12, (v, w) => LuminPackSerializer.Serialize(v, w));
    private static readonly Dictionary<string, PolymorphicFooBase> StringMap32 = Enumerable.Range(0, 32).ToDictionary(i => "key_" + i, Foo);
    private static readonly byte[] NinoStringMap32 = NinoSerializer.Serialize(StringMap32);
    private static readonly byte[] MemoryPackStringMap32 = MemoryPackSerializer.Serialize(StringMap32);
    private static readonly byte[] LuminPackStringMap32 = LuminPackBuffers.Bytes(StringMap32, (v, w) => LuminPackSerializer.Serialize(v, w));
    private static readonly Dictionary<string, PolymorphicFooBase> StringMap100 = Enumerable.Range(0, 100).ToDictionary(i => "key_" + i, Foo);
    private static readonly byte[] NinoStringMap100 = NinoSerializer.Serialize(StringMap100);
    private static readonly byte[] MemoryPackStringMap100 = MemoryPackSerializer.Serialize(StringMap100);
    private static readonly byte[] LuminPackStringMap100 = LuminPackBuffers.Bytes(StringMap100, (v, w) => LuminPackSerializer.Serialize(v, w));

    [GlobalSetup]
    public void Setup()
    {
        _ninoBuffer = new NinoArrayBufferWriter(1024 * 1024);
        _memoryPackBuffer = new ArrayBufferWriter<byte>(1024 * 1024);
        _luminPackBuffer = LuminPackBuffers.Rent();
    }

    [Benchmark(Baseline = true), BenchmarkCategory("IntMap1Serialize")]
    public int NinoSerializeIntMap1() { _ninoBuffer.ResetWrittenCount(); NinoSerializer.Serialize(IntMap1, _ninoBuffer); return _ninoBuffer.WrittenCount; }

    [Benchmark, BenchmarkCategory("IntMap1Serialize")]
    public int MemoryPackSerializeIntMap1() { _memoryPackBuffer.ResetWrittenCount(); MemoryPackSerializer.Serialize(_memoryPackBuffer, IntMap1); return _memoryPackBuffer.WrittenCount; }

    [Benchmark, BenchmarkCategory("IntMap1Serialize")]
    public int LuminPackSerializeIntMap1() { _luminPackBuffer.ResetCore(); LuminPackSerializer.Serialize(IntMap1, _luminPackBuffer); return _luminPackBuffer.CurrentIndex; }

    [Benchmark(Baseline = true), BenchmarkCategory("IntMap1Deserialize")]
    public Dictionary<int, PolymorphicFooBase> NinoDeserializeIntMap1() => NinoDeserializer.Deserialize<Dictionary<int, PolymorphicFooBase>>(NinoIntMap1);

    [Benchmark, BenchmarkCategory("IntMap1Deserialize")]
    public Dictionary<int, PolymorphicFooBase> MemoryPackDeserializeIntMap1() => MemoryPackSerializer.Deserialize<Dictionary<int, PolymorphicFooBase>>(MemoryPackIntMap1);

    [Benchmark, BenchmarkCategory("IntMap1Deserialize")]
    public Dictionary<int, PolymorphicFooBase> LuminPackDeserializeIntMap1() { Dictionary<int, PolymorphicFooBase> result = default; LuminPackSerializer.Deserialize(LuminPackIntMap1, ref result); return result; }

    [Benchmark(Baseline = true), BenchmarkCategory("IntMap4Serialize")]
    public int NinoSerializeIntMap4() { _ninoBuffer.ResetWrittenCount(); NinoSerializer.Serialize(IntMap4, _ninoBuffer); return _ninoBuffer.WrittenCount; }

    [Benchmark, BenchmarkCategory("IntMap4Serialize")]
    public int MemoryPackSerializeIntMap4() { _memoryPackBuffer.ResetWrittenCount(); MemoryPackSerializer.Serialize(_memoryPackBuffer, IntMap4); return _memoryPackBuffer.WrittenCount; }

    [Benchmark, BenchmarkCategory("IntMap4Serialize")]
    public int LuminPackSerializeIntMap4() { _luminPackBuffer.ResetCore(); LuminPackSerializer.Serialize(IntMap4, _luminPackBuffer); return _luminPackBuffer.CurrentIndex; }

    [Benchmark(Baseline = true), BenchmarkCategory("IntMap4Deserialize")]
    public Dictionary<int, PolymorphicFooBase> NinoDeserializeIntMap4() => NinoDeserializer.Deserialize<Dictionary<int, PolymorphicFooBase>>(NinoIntMap4);

    [Benchmark, BenchmarkCategory("IntMap4Deserialize")]
    public Dictionary<int, PolymorphicFooBase> MemoryPackDeserializeIntMap4() => MemoryPackSerializer.Deserialize<Dictionary<int, PolymorphicFooBase>>(MemoryPackIntMap4);

    [Benchmark, BenchmarkCategory("IntMap4Deserialize")]
    public Dictionary<int, PolymorphicFooBase> LuminPackDeserializeIntMap4() { Dictionary<int, PolymorphicFooBase> result = default; LuminPackSerializer.Deserialize(LuminPackIntMap4, ref result); return result; }

    [Benchmark(Baseline = true), BenchmarkCategory("IntMap12Serialize")]
    public int NinoSerializeIntMap12() { _ninoBuffer.ResetWrittenCount(); NinoSerializer.Serialize(IntMap12, _ninoBuffer); return _ninoBuffer.WrittenCount; }

    [Benchmark, BenchmarkCategory("IntMap12Serialize")]
    public int MemoryPackSerializeIntMap12() { _memoryPackBuffer.ResetWrittenCount(); MemoryPackSerializer.Serialize(_memoryPackBuffer, IntMap12); return _memoryPackBuffer.WrittenCount; }

    [Benchmark, BenchmarkCategory("IntMap12Serialize")]
    public int LuminPackSerializeIntMap12() { _luminPackBuffer.ResetCore(); LuminPackSerializer.Serialize(IntMap12, _luminPackBuffer); return _luminPackBuffer.CurrentIndex; }

    [Benchmark(Baseline = true), BenchmarkCategory("IntMap12Deserialize")]
    public Dictionary<int, PolymorphicFooBase> NinoDeserializeIntMap12() => NinoDeserializer.Deserialize<Dictionary<int, PolymorphicFooBase>>(NinoIntMap12);

    [Benchmark, BenchmarkCategory("IntMap12Deserialize")]
    public Dictionary<int, PolymorphicFooBase> MemoryPackDeserializeIntMap12() => MemoryPackSerializer.Deserialize<Dictionary<int, PolymorphicFooBase>>(MemoryPackIntMap12);

    [Benchmark, BenchmarkCategory("IntMap12Deserialize")]
    public Dictionary<int, PolymorphicFooBase> LuminPackDeserializeIntMap12() { Dictionary<int, PolymorphicFooBase> result = default; LuminPackSerializer.Deserialize(LuminPackIntMap12, ref result); return result; }

    [Benchmark(Baseline = true), BenchmarkCategory("IntMap32Serialize")]
    public int NinoSerializeIntMap32() { _ninoBuffer.ResetWrittenCount(); NinoSerializer.Serialize(IntMap32, _ninoBuffer); return _ninoBuffer.WrittenCount; }

    [Benchmark, BenchmarkCategory("IntMap32Serialize")]
    public int MemoryPackSerializeIntMap32() { _memoryPackBuffer.ResetWrittenCount(); MemoryPackSerializer.Serialize(_memoryPackBuffer, IntMap32); return _memoryPackBuffer.WrittenCount; }

    [Benchmark, BenchmarkCategory("IntMap32Serialize")]
    public int LuminPackSerializeIntMap32() { _luminPackBuffer.ResetCore(); LuminPackSerializer.Serialize(IntMap32, _luminPackBuffer); return _luminPackBuffer.CurrentIndex; }

    [Benchmark(Baseline = true), BenchmarkCategory("IntMap32Deserialize")]
    public Dictionary<int, PolymorphicFooBase> NinoDeserializeIntMap32() => NinoDeserializer.Deserialize<Dictionary<int, PolymorphicFooBase>>(NinoIntMap32);

    [Benchmark, BenchmarkCategory("IntMap32Deserialize")]
    public Dictionary<int, PolymorphicFooBase> MemoryPackDeserializeIntMap32() => MemoryPackSerializer.Deserialize<Dictionary<int, PolymorphicFooBase>>(MemoryPackIntMap32);

    [Benchmark, BenchmarkCategory("IntMap32Deserialize")]
    public Dictionary<int, PolymorphicFooBase> LuminPackDeserializeIntMap32() { Dictionary<int, PolymorphicFooBase> result = default; LuminPackSerializer.Deserialize(LuminPackIntMap32, ref result); return result; }

    [Benchmark(Baseline = true), BenchmarkCategory("IntMap100Serialize")]
    public int NinoSerializeIntMap100() { _ninoBuffer.ResetWrittenCount(); NinoSerializer.Serialize(IntMap100, _ninoBuffer); return _ninoBuffer.WrittenCount; }

    [Benchmark, BenchmarkCategory("IntMap100Serialize")]
    public int MemoryPackSerializeIntMap100() { _memoryPackBuffer.ResetWrittenCount(); MemoryPackSerializer.Serialize(_memoryPackBuffer, IntMap100); return _memoryPackBuffer.WrittenCount; }

    [Benchmark, BenchmarkCategory("IntMap100Serialize")]
    public int LuminPackSerializeIntMap100() { _luminPackBuffer.ResetCore(); LuminPackSerializer.Serialize(IntMap100, _luminPackBuffer); return _luminPackBuffer.CurrentIndex; }

    [Benchmark(Baseline = true), BenchmarkCategory("IntMap100Deserialize")]
    public Dictionary<int, PolymorphicFooBase> NinoDeserializeIntMap100() => NinoDeserializer.Deserialize<Dictionary<int, PolymorphicFooBase>>(NinoIntMap100);

    [Benchmark, BenchmarkCategory("IntMap100Deserialize")]
    public Dictionary<int, PolymorphicFooBase> MemoryPackDeserializeIntMap100() => MemoryPackSerializer.Deserialize<Dictionary<int, PolymorphicFooBase>>(MemoryPackIntMap100);

    [Benchmark, BenchmarkCategory("IntMap100Deserialize")]
    public Dictionary<int, PolymorphicFooBase> LuminPackDeserializeIntMap100() { Dictionary<int, PolymorphicFooBase> result = default; LuminPackSerializer.Deserialize(LuminPackIntMap100, ref result); return result; }

    [Benchmark(Baseline = true), BenchmarkCategory("StringMap1Serialize")]
    public int NinoSerializeStringMap1() { _ninoBuffer.ResetWrittenCount(); NinoSerializer.Serialize(StringMap1, _ninoBuffer); return _ninoBuffer.WrittenCount; }

    [Benchmark, BenchmarkCategory("StringMap1Serialize")]
    public int MemoryPackSerializeStringMap1() { _memoryPackBuffer.ResetWrittenCount(); MemoryPackSerializer.Serialize(_memoryPackBuffer, StringMap1); return _memoryPackBuffer.WrittenCount; }

    [Benchmark, BenchmarkCategory("StringMap1Serialize")]
    public int LuminPackSerializeStringMap1() { _luminPackBuffer.ResetCore(); LuminPackSerializer.Serialize(StringMap1, _luminPackBuffer); return _luminPackBuffer.CurrentIndex; }

    [Benchmark(Baseline = true), BenchmarkCategory("StringMap1Deserialize")]
    public Dictionary<string, PolymorphicFooBase> NinoDeserializeStringMap1() => NinoDeserializer.Deserialize<Dictionary<string, PolymorphicFooBase>>(NinoStringMap1);

    [Benchmark, BenchmarkCategory("StringMap1Deserialize")]
    public Dictionary<string, PolymorphicFooBase> MemoryPackDeserializeStringMap1() => MemoryPackSerializer.Deserialize<Dictionary<string, PolymorphicFooBase>>(MemoryPackStringMap1);

    [Benchmark, BenchmarkCategory("StringMap1Deserialize")]
    public Dictionary<string, PolymorphicFooBase> LuminPackDeserializeStringMap1() { Dictionary<string, PolymorphicFooBase> result = default; LuminPackSerializer.Deserialize(LuminPackStringMap1, ref result); return result; }

    [Benchmark(Baseline = true), BenchmarkCategory("StringMap4Serialize")]
    public int NinoSerializeStringMap4() { _ninoBuffer.ResetWrittenCount(); NinoSerializer.Serialize(StringMap4, _ninoBuffer); return _ninoBuffer.WrittenCount; }

    [Benchmark, BenchmarkCategory("StringMap4Serialize")]
    public int MemoryPackSerializeStringMap4() { _memoryPackBuffer.ResetWrittenCount(); MemoryPackSerializer.Serialize(_memoryPackBuffer, StringMap4); return _memoryPackBuffer.WrittenCount; }

    [Benchmark, BenchmarkCategory("StringMap4Serialize")]
    public int LuminPackSerializeStringMap4() { _luminPackBuffer.ResetCore(); LuminPackSerializer.Serialize(StringMap4, _luminPackBuffer); return _luminPackBuffer.CurrentIndex; }

    [Benchmark(Baseline = true), BenchmarkCategory("StringMap4Deserialize")]
    public Dictionary<string, PolymorphicFooBase> NinoDeserializeStringMap4() => NinoDeserializer.Deserialize<Dictionary<string, PolymorphicFooBase>>(NinoStringMap4);

    [Benchmark, BenchmarkCategory("StringMap4Deserialize")]
    public Dictionary<string, PolymorphicFooBase> MemoryPackDeserializeStringMap4() => MemoryPackSerializer.Deserialize<Dictionary<string, PolymorphicFooBase>>(MemoryPackStringMap4);

    [Benchmark, BenchmarkCategory("StringMap4Deserialize")]
    public Dictionary<string, PolymorphicFooBase> LuminPackDeserializeStringMap4() { Dictionary<string, PolymorphicFooBase> result = default; LuminPackSerializer.Deserialize(LuminPackStringMap4, ref result); return result; }

    [Benchmark(Baseline = true), BenchmarkCategory("StringMap12Serialize")]
    public int NinoSerializeStringMap12() { _ninoBuffer.ResetWrittenCount(); NinoSerializer.Serialize(StringMap12, _ninoBuffer); return _ninoBuffer.WrittenCount; }

    [Benchmark, BenchmarkCategory("StringMap12Serialize")]
    public int MemoryPackSerializeStringMap12() { _memoryPackBuffer.ResetWrittenCount(); MemoryPackSerializer.Serialize(_memoryPackBuffer, StringMap12); return _memoryPackBuffer.WrittenCount; }

    [Benchmark, BenchmarkCategory("StringMap12Serialize")]
    public int LuminPackSerializeStringMap12() { _luminPackBuffer.ResetCore(); LuminPackSerializer.Serialize(StringMap12, _luminPackBuffer); return _luminPackBuffer.CurrentIndex; }

    [Benchmark(Baseline = true), BenchmarkCategory("StringMap12Deserialize")]
    public Dictionary<string, PolymorphicFooBase> NinoDeserializeStringMap12() => NinoDeserializer.Deserialize<Dictionary<string, PolymorphicFooBase>>(NinoStringMap12);

    [Benchmark, BenchmarkCategory("StringMap12Deserialize")]
    public Dictionary<string, PolymorphicFooBase> MemoryPackDeserializeStringMap12() => MemoryPackSerializer.Deserialize<Dictionary<string, PolymorphicFooBase>>(MemoryPackStringMap12);

    [Benchmark, BenchmarkCategory("StringMap12Deserialize")]
    public Dictionary<string, PolymorphicFooBase> LuminPackDeserializeStringMap12() { Dictionary<string, PolymorphicFooBase> result = default; LuminPackSerializer.Deserialize(LuminPackStringMap12, ref result); return result; }

    [Benchmark(Baseline = true), BenchmarkCategory("StringMap32Serialize")]
    public int NinoSerializeStringMap32() { _ninoBuffer.ResetWrittenCount(); NinoSerializer.Serialize(StringMap32, _ninoBuffer); return _ninoBuffer.WrittenCount; }

    [Benchmark, BenchmarkCategory("StringMap32Serialize")]
    public int MemoryPackSerializeStringMap32() { _memoryPackBuffer.ResetWrittenCount(); MemoryPackSerializer.Serialize(_memoryPackBuffer, StringMap32); return _memoryPackBuffer.WrittenCount; }

    [Benchmark, BenchmarkCategory("StringMap32Serialize")]
    public int LuminPackSerializeStringMap32() { _luminPackBuffer.ResetCore(); LuminPackSerializer.Serialize(StringMap32, _luminPackBuffer); return _luminPackBuffer.CurrentIndex; }

    [Benchmark(Baseline = true), BenchmarkCategory("StringMap32Deserialize")]
    public Dictionary<string, PolymorphicFooBase> NinoDeserializeStringMap32() => NinoDeserializer.Deserialize<Dictionary<string, PolymorphicFooBase>>(NinoStringMap32);

    [Benchmark, BenchmarkCategory("StringMap32Deserialize")]
    public Dictionary<string, PolymorphicFooBase> MemoryPackDeserializeStringMap32() => MemoryPackSerializer.Deserialize<Dictionary<string, PolymorphicFooBase>>(MemoryPackStringMap32);

    [Benchmark, BenchmarkCategory("StringMap32Deserialize")]
    public Dictionary<string, PolymorphicFooBase> LuminPackDeserializeStringMap32() { Dictionary<string, PolymorphicFooBase> result = default; LuminPackSerializer.Deserialize(LuminPackStringMap32, ref result); return result; }

    [Benchmark(Baseline = true), BenchmarkCategory("StringMap100Serialize")]
    public int NinoSerializeStringMap100() { _ninoBuffer.ResetWrittenCount(); NinoSerializer.Serialize(StringMap100, _ninoBuffer); return _ninoBuffer.WrittenCount; }

    [Benchmark, BenchmarkCategory("StringMap100Serialize")]
    public int MemoryPackSerializeStringMap100() { _memoryPackBuffer.ResetWrittenCount(); MemoryPackSerializer.Serialize(_memoryPackBuffer, StringMap100); return _memoryPackBuffer.WrittenCount; }

    [Benchmark, BenchmarkCategory("StringMap100Serialize")]
    public int LuminPackSerializeStringMap100() { _luminPackBuffer.ResetCore(); LuminPackSerializer.Serialize(StringMap100, _luminPackBuffer); return _luminPackBuffer.CurrentIndex; }

    [Benchmark(Baseline = true), BenchmarkCategory("StringMap100Deserialize")]
    public Dictionary<string, PolymorphicFooBase> NinoDeserializeStringMap100() => NinoDeserializer.Deserialize<Dictionary<string, PolymorphicFooBase>>(NinoStringMap100);

    [Benchmark, BenchmarkCategory("StringMap100Deserialize")]
    public Dictionary<string, PolymorphicFooBase> MemoryPackDeserializeStringMap100() => MemoryPackSerializer.Deserialize<Dictionary<string, PolymorphicFooBase>>(MemoryPackStringMap100);

    [Benchmark, BenchmarkCategory("StringMap100Deserialize")]
    public Dictionary<string, PolymorphicFooBase> LuminPackDeserializeStringMap100() { Dictionary<string, PolymorphicFooBase> result = default; LuminPackSerializer.Deserialize(LuminPackStringMap100, ref result); return result; }

}
