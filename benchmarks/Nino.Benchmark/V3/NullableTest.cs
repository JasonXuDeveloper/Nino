using System;
using System.Buffers;
using System.Linq;
using BenchmarkDotNet.Configs;
using MemoryPack;
using MessagePack;
using Nino.Core;

#nullable disable
namespace Nino.Benchmark;

// NullableTest.cs ported to Nino 3.3.13 (NuGet), for the v3 build of this project (NINO_BENCH_V3=true), with the same
// model (Fixtures/NullableBenchmarkData.cs) and data. Only the Nino calls differ, as in V3/SimpleTest.cs:
// NinoSerializer/NinoDeserializer -> NinoGen.Serializer/NinoGen.Deserializer, NinoArrayBufferWriter ->
// ArrayBufferWriter<byte>; and the protobuf-net, Google.Protobuf and System.Text.Json cases are left out (the v3 run
// times Nino and MemoryPack only). Everything else is verbatim.

/// <summary>
/// A class of eight nullable members of common types, half of them null, alone and in an array of 100. Against
/// MemoryPack (through <see cref="NullableFieldsMemoryPack"/>: SafeNullable members) and MessagePack.
/// </summary>
[PayloadColumn]
[HideColumns("StdDev", "RatioSD", "Error")]
[MinColumn, MaxColumn]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[NinoJob]
[GcServer(true)]
[MemoryDiagnoser]
[MarkdownExporterAttribute.GitHub]
public class NullableTest : IReadBack
{
    private static readonly NullableFields NullableClass;
    private static readonly NullableFields[] NullableClasses;
    private static readonly NullableFieldsMemoryPack NullableClassMemoryPack;
    private static readonly NullableFieldsMemoryPack[] NullableClassesMemoryPack;

    // v3 3.3.13 has no NinoArrayBufferWriter; its IBufferWriter<byte> overload is the reused-buffer path.
    private ArrayBufferWriter<byte> _ninoBuffer;
    private ArrayBufferWriter<byte> _memoryPackBuffer;
    private ArrayBufferWriter<byte> _messagePackBuffer;

    private static readonly byte[][] SerializedNullableClass;
    private static readonly byte[][] SerializedNullableClasses;

    static NullableTest()
    {
        var random = new Random(20240607);
        NullableClass = NullableFields.Create(0, random);
        NullableClasses = Enumerable.Range(0, 100).Select(i => NullableFields.Create(i, random)).ToArray();
        NullableClassMemoryPack = NullableFieldsMemoryPack.From(NullableClass);
        NullableClassesMemoryPack = SafeNullables.From(NullableClasses);

        SerializedNullableClass = new byte[3][];
        SerializedNullableClass[0] = MessagePackSerializer.Serialize(NullableClass);
        SerializedNullableClass[1] = MemoryPackSerializer.Serialize(NullableClassMemoryPack);
        SerializedNullableClass[2] = NinoGen.Serializer.Serialize(NullableClass);

        SerializedNullableClasses = new byte[3][];
        SerializedNullableClasses[0] = MessagePackSerializer.Serialize(NullableClasses);
        SerializedNullableClasses[1] = MemoryPackSerializer.Serialize(NullableClassesMemoryPack);
        SerializedNullableClasses[2] = NinoGen.Serializer.Serialize(NullableClasses);

        string[] libraries = ["MessagePack", "MemoryPack", "Nino"];
        for (int i = 0; i < libraries.Length; i++)
        {
            BenchmarkPayloadRegistry.Register($"{libraries[i]}SerializeNullableClass", SerializedNullableClass[i].Length);
            BenchmarkPayloadRegistry.Register($"{libraries[i]}SerializeNullableClasses", SerializedNullableClasses[i].Length);
        }
    }

    [GlobalSetup]
    public void Setup()
    {
        _ninoBuffer = new ArrayBufferWriter<byte>(64 * 1024);
        _memoryPackBuffer = new ArrayBufferWriter<byte>(64 * 1024);
        _messagePackBuffer = new ArrayBufferWriter<byte>(64 * 1024);
    }

    /// <summary>Verify.cs: MemoryPack's SafeNullable model as <see cref="NullableFields"/>.</summary>
    public object ReadBack(object result, Type model) => SafeNullables.ReadBack(result);

    [Benchmark(Baseline = true), BenchmarkCategory("NullableClassSerialize")]
    public int NinoSerializeNullableClass()
    {
        _ninoBuffer.ResetWrittenCount();
        NinoGen.Serializer.Serialize(NullableClass, _ninoBuffer);
        return _ninoBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("NullableClassSerialize")]
    public int MemoryPackSerializeNullableClass()
    {
        _memoryPackBuffer.ResetWrittenCount();
        MemoryPackSerializer.Serialize(_memoryPackBuffer, NullableClassMemoryPack);
        return _memoryPackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("NullableClassSerialize")]
    public int MessagePackSerializeNullableClass()
    {
        _messagePackBuffer.ResetWrittenCount();
        MessagePackSerializer.Serialize(_messagePackBuffer, NullableClass);
        return _messagePackBuffer.WrittenCount;
    }

    [Benchmark(Baseline = true), BenchmarkCategory("NullableClassesSerialize")]
    public int NinoSerializeNullableClasses()
    {
        _ninoBuffer.ResetWrittenCount();
        NinoGen.Serializer.Serialize(NullableClasses, _ninoBuffer);
        return _ninoBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("NullableClassesSerialize")]
    public int MemoryPackSerializeNullableClasses()
    {
        _memoryPackBuffer.ResetWrittenCount();
        MemoryPackSerializer.Serialize(_memoryPackBuffer, NullableClassesMemoryPack);
        return _memoryPackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("NullableClassesSerialize")]
    public int MessagePackSerializeNullableClasses()
    {
        _messagePackBuffer.ResetWrittenCount();
        MessagePackSerializer.Serialize(_messagePackBuffer, NullableClasses);
        return _messagePackBuffer.WrittenCount;
    }

    [Benchmark(Baseline = true), BenchmarkCategory("NullableClassDeserialize")]
    public NullableFields NinoDeserializeNullableClass()
    {
        NinoGen.Deserializer.Deserialize(SerializedNullableClass[2], out NullableFields ret);
        return ret;
    }

    [Benchmark, BenchmarkCategory("NullableClassDeserialize")]
    public NullableFieldsMemoryPack MemoryPackDeserializeNullableClass() =>
        MemoryPackSerializer.Deserialize<NullableFieldsMemoryPack>(SerializedNullableClass[1]);

    [Benchmark, BenchmarkCategory("NullableClassDeserialize")]
    public NullableFields MessagePackDeserializeNullableClass() =>
        MessagePackSerializer.Deserialize<NullableFields>(SerializedNullableClass[0]);

    [Benchmark(Baseline = true), BenchmarkCategory("NullableClassesDeserialize")]
    public NullableFields[] NinoDeserializeNullableClasses()
    {
        NinoGen.Deserializer.Deserialize(SerializedNullableClasses[2], out NullableFields[] ret);
        return ret;
    }

    [Benchmark, BenchmarkCategory("NullableClassesDeserialize")]
    public NullableFieldsMemoryPack[] MemoryPackDeserializeNullableClasses() =>
        MemoryPackSerializer.Deserialize<NullableFieldsMemoryPack[]>(SerializedNullableClasses[1]);

    [Benchmark, BenchmarkCategory("NullableClassesDeserialize")]
    public NullableFields[] MessagePackDeserializeNullableClasses() =>
        MessagePackSerializer.Deserialize<NullableFields[]>(SerializedNullableClasses[0]);
}
