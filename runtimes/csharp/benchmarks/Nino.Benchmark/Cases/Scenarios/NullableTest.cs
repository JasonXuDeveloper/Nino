using System;
using System.Linq;
using BenchmarkDotNet.Configs;
using MemoryPack;
#if !NINO_BENCH_V3
using BenchmarkDotNet.Attributes;
using Nino.Core;
using ProtoBuf;
using P = Nino.Benchmark.Proto;
#endif

#nullable disable
namespace Nino.Benchmark;

/// <summary>
/// Nullable values against MemoryPack, MessagePack, protobuf-net, Google.Protobuf (the messages cases.proto has for the
/// contracts in CaseFormats.cs, parsed from protobuf-net's bytes outside the timed region) and System.Text.Json (source
/// generated, UTF-8):
///  - nullable fields: a class of eight nullable members of common types (<c>int?</c>, <c>long?</c>, <c>double?</c>,
///    <c>bool?</c>, <c>DateTime?</c>, <c>Guid?</c>, <c>decimal?</c>, an enum), half of them null
///    (<see cref="NullableFields"/>), alone and in an array of 100 whose objects alternate which half is null;
///  - nullable values on their own: one <c>SimpleStruct?</c> that holds a value, <c>SimpleStruct?[100]</c> and
///    <c>SimpleClass[100]</c> with every fifth element null, and one <c>SimpleClass</c> reference that is null (SimpleTest's
///    SimpleClass case is its counterpart with a value), and <c>string[1000]</c> with every fifth element null
///    (NullableTest.NullableStrings.cs);
///  - a raw struct with nullable members, alone and in an array of 1,000 (NullableTest.RawNullable.cs);
///  - a struct that holds a reference, with a value and nullable, alone and in an array of 100
///    (NullableTest.ManagedStruct.cs).
/// MemoryPack writes the nullable structs as <c>SafeNullable&lt;T&gt;</c> (<see cref="NullableFieldsMemoryPack"/> says
/// why), copied outside the timed region; protobuf a null as an absent message field (CaseFormats.cs).
/// The timed methods are NullableTest.Cases.g.cs, generated from the cases below and in the other parts of the class
/// and the adapters (Adapters/, runtimes/csharp/tools/BenchGen); the methods written here are the ones no adapter
/// expresses (a protobuf contract's member read back, MemoryPack's conversion of a model of its own). The v3 build
/// times Nino 3.3.13 and MemoryPack on the same values; the other nullable forms are v4's (NinoNullable&lt;T&gt; is,
/// and MemoryPack's other forms compare with it).
/// </summary>
[BenchCase("NullableFields", Value = "Fields", MemoryPackValue = "FieldsMemoryPack", NinoNullableValue = "FieldsNino",
    MemoryPackUnsafeValue = "FieldsUnsafe", NullableForms = true, Proto = "NullableFields", Json = "NullableFields")]
[BenchCase("NullableFieldsArray", Quick = true, Value = "FieldsArray", MemoryPackValue = "FieldsArrayMemoryPack",
    NinoNullableValue = "FieldsArrayNino", MemoryPackUnsafeValue = "FieldsArrayUnsafe", NullableForms = true,
    Proto = "NullableFieldsList", Json = "NullableFieldsArray",
    Broken = "LuminPack wrong-read: reads 100 NullableFields back other than it wrote (one reads back right)")]
[BenchCase("NullableStruct", Value = "Struct", MemoryPackValue = "StructMemoryPack", ProtobufValue = "StructProtobuf",
    NinoNullableValue = "StructNino", NullableForms = true, Proto = "OptionalSimpleStruct", Json = "NullableSimpleStruct")]
[BenchCase("NullableStructs", Quick = true, Value = "Structs", MemoryPackValue = "StructsMemoryPack", ProtobufValue = "StructsProtobuf",
    NinoNullableValue = "StructsNino", NullableForms = true, Proto = "OptionalSimpleStructList",
    Json = "NullableSimpleStructArray")]
[BenchCase("NullableObject", Value = "NullObject", ProtobufValue = "NullObjectProtobuf", Proto = "OptionalSimpleClass",
    Json = "SimpleClass", ReadsNull = true)]
[BenchCase("NullableObjects", Value = "Objects", ProtobufValue = "ObjectsProtobuf", Proto = "NullableObjectList",
    Json = "SimpleClassArray")]
[BenchClass(Bytes = "{Library}{Case}Bytes", Order = "case")]
[PayloadColumn]
[HideColumns("StdDev", "RatioSD", "Error")]
[MinColumn, MaxColumn]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[NinoJob]
[GcServer(true)]
[MemoryDiagnoser]
[MarkdownExporterAttribute.GitHub]
public partial class NullableTest : IReadBack
{
    // The values, in this order: the members of the first take the random sequence's first values.
    private static readonly Random ValuesRandom = new(20240607);
    private static readonly NullableFields Fields = NullableFields.Create(0, ValuesRandom);
    private static readonly NullableFields[] FieldsArray =
        Enumerable.Range(0, 100).Select(i => NullableFields.Create(i, ValuesRandom)).ToArray();
    private static readonly NullableFieldsMemoryPack FieldsMemoryPack = NullableFieldsMemoryPack.From(Fields);
    private static readonly NullableFieldsMemoryPack[] FieldsArrayMemoryPack = SafeNullables.From(FieldsArray);
    // Every fifth element null, as CollectionsTest's nullable collections.
    private static readonly SimpleStruct? Struct = SimpleStruct.Create();
    private static readonly SimpleStruct?[] Structs =
        Enumerable.Range(0, 100).Select(i => i % 5 == 0 ? null : (SimpleStruct?)SimpleStruct.Create()).ToArray();
    private static readonly SimpleClass NullObject = null;
    private static readonly SimpleClass[] Objects =
        Enumerable.Range(0, 100).Select(i => i % 5 == 0 ? null : SimpleClass.Create()).ToArray();
    private static readonly SafeNullable<SimpleStruct> StructMemoryPack = Struct;
    private static readonly SafeNullable<SimpleStruct>[] StructsMemoryPack = SafeNullables.From(Structs);
#if !NINO_BENCH_V3
    // protobuf's contracts holding the nullable values (CaseFormats.cs).
    private static readonly OptionalSimpleStruct StructProtobuf = new() { Value = Struct };
    private static readonly OptionalSimpleStructList StructsProtobuf = new() { Items = OptionalSimpleStruct.From(Structs) };
    private static readonly OptionalSimpleClass NullObjectProtobuf = new() { Value = NullObject };
    private static readonly NullableObjectList ObjectsProtobuf = new() { Items = Objects };
    // The other forms (NullableTest.NullableForms.cs): Nino's NinoNullable<T> and MemoryPack's unsafe model.
    private static readonly NullableFieldsNino FieldsNino = NullableFieldsNino.From(Fields);
    private static readonly NullableFieldsNino[] FieldsArrayNino = Array.ConvertAll(FieldsArray, NullableFieldsNino.From);
    private static readonly NinoNullable<SimpleStruct> StructNino = Struct;
    private static readonly NinoNullable<SimpleStruct>[] StructsNino = NullableForms.Nino(Structs);
    private static readonly NullableFieldsUnsafe FieldsUnsafe = NullableFieldsUnsafe.From(Fields);
    private static readonly NullableFieldsUnsafe[] FieldsArrayUnsafe = Array.ConvertAll(FieldsArray, NullableFieldsUnsafe.From);

    partial void OnSetup() => ProtoFile.Cases.Check();

    /// <summary>
    /// Verify.cs: MemoryPack's SafeNullable data, protobuf-net's OptionalSimpleStruct array and Google.Protobuf's messages
    /// as the case's nullable values.
    /// </summary>
    public object ReadBack(object result, Type model) => result switch
    {
        P.OptionalSimpleStruct => ((OptionalSimpleStruct)ProtobufModel.ReadBack(result, typeof(OptionalSimpleStruct))).Value,
        P.OptionalSimpleStructList => OptionalSimpleStruct.ToNullable(
            ((OptionalSimpleStructList)ProtobufModel.ReadBack(result, typeof(OptionalSimpleStructList))).Items),
        P.OptionalSimpleClass => ((OptionalSimpleClass)ProtobufModel.ReadBack(result, typeof(OptionalSimpleClass))).Value,
        P.NullableObjectList => ((NullableObjectList)ProtobufModel.ReadBack(result, typeof(NullableObjectList))).Items,
        P.NullableStringList => ((NullableStringList)ProtobufModel.ReadBack(result, typeof(NullableStringList))).Items,
        OptionalSimpleStruct[] items => OptionalSimpleStruct.ToNullable(items),
        P.OptionalManagedStruct => ((OptionalManagedStruct)ProtobufModel.ReadBack(result, typeof(OptionalManagedStruct))).Value,
        P.OptionalManagedStructList => OptionalManagedStruct.ToNullable(
            ((OptionalManagedStructList)ProtobufModel.ReadBack(result, typeof(OptionalManagedStructList))).Items),
        OptionalManagedStruct[] items => OptionalManagedStruct.ToNullable(items),
        _ => ProtobufModel.ReadBack(NullableForms.ReadBack(result), model),
    };

    // protobuf-net reads a nullable value through its contract: the member that holds it.
    [Benchmark, BenchmarkCategory("NullableStructDeserialize")]
    public SimpleStruct? ProtobufDeserializeNullableStruct() =>
        Serializer.Deserialize<OptionalSimpleStruct>(new ReadOnlySpan<byte>(ProtobufNullableStructBytes)).Value;

    [Benchmark, BenchmarkCategory("NullableStructsDeserialize")]
    public OptionalSimpleStruct[] ProtobufDeserializeNullableStructs() =>
        Serializer.Deserialize<OptionalSimpleStructList>(new ReadOnlySpan<byte>(ProtobufNullableStructsBytes)).Items;

    [Benchmark, BenchmarkCategory("NullableObjectDeserialize")]
    public SimpleClass ProtobufDeserializeNullableObject() =>
        Serializer.Deserialize<OptionalSimpleClass>(new ReadOnlySpan<byte>(ProtobufNullableObjectBytes)).Value;

    [Benchmark, BenchmarkCategory("NullableObjectsDeserialize")]
    public SimpleClass[] ProtobufDeserializeNullableObjects() =>
        Serializer.Deserialize<NullableObjectList>(new ReadOnlySpan<byte>(ProtobufNullableObjectsBytes)).Items;

    // MemoryPack's conversion of a model of its own (NullableFieldsMemoryPack) and of one value.
    [Benchmark, BenchmarkCategory("NullableFieldsSerialize")]
    public int MemoryPackConvertedSerializeNullableFields()
    {
        _memoryPackBuffer.ResetWrittenCount();
        MemoryPackSerializer.Serialize(_memoryPackBuffer, NullableFieldsMemoryPack.From(Fields));
        return _memoryPackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("NullableFieldsDeserialize")]
    public NullableFields MemoryPackConvertedDeserializeNullableFields() =>
        MemoryPackSerializer.Deserialize<NullableFieldsMemoryPack>(MemoryPackNullableFieldsBytes).ToNullable();

    [Benchmark, BenchmarkCategory("NullableFieldsArrayDeserialize")]
    public NullableFields[] MemoryPackConvertedDeserializeNullableFieldsArray() =>
        Array.ConvertAll(MemoryPackSerializer.Deserialize<NullableFieldsMemoryPack[]>(MemoryPackNullableFieldsArrayBytes),
            v => v.ToNullable());

    [Benchmark, BenchmarkCategory("NullableStructSerialize")]
    public int MemoryPackConvertedSerializeNullableStruct()
    {
        _memoryPackBuffer.ResetWrittenCount();
        MemoryPackSerializer.Serialize(_memoryPackBuffer, (SafeNullable<SimpleStruct>)Struct);
        return _memoryPackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("NullableStructDeserialize")]
    public SimpleStruct? MemoryPackConvertedDeserializeNullableStruct() =>
        MemoryPackSerializer.Deserialize<SafeNullable<SimpleStruct>>(MemoryPackNullableStructBytes).ToNullable();
#else
    /// <summary>Verify.cs: MemoryPack's SafeNullable data as the case's nullable values.</summary>
    public object ReadBack(object result, Type model) => SafeNullables.ReadBack(result);
#endif
}
