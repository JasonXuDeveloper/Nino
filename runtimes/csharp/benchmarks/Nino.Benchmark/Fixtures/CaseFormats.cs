using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Numerics;
using System.Text.Json.Serialization;
using Nino.Benchmark.Workloads;
using ProtoBuf;

#nullable disable
namespace Nino.Benchmark;

// What Google.Protobuf and System.Text.Json need for SimpleTest's, PolymorphicTest's and NullableTest's data and
// CollectionsTest's nullable collections (the v4 build only).

// Google.Protobuf: protobuf-net writes an array at the top level as the repeated field 1 of a message; these contracts
// name that message, so cases.proto (ProtoFile.Cases) has a message for each array case. They are never serialized.

[ProtoContract]
public sealed class SimpleClassList
{
    [ProtoMember(1)] public SimpleClass[] Items;
}

[ProtoContract]
public sealed class SimpleStructList
{
    [ProtoMember(1)] public SimpleStruct[] Items;
}

[ProtoContract]
public sealed class VectorList
{
    [ProtoMember(1)] public Vector4[] Items;
}

[ProtoContract]
public sealed class PolymorphicFooList
{
    [ProtoMember(1)] public PolymorphicFooBase[] Items;
}

[ProtoContract]
public sealed class PolymorphicClassList
{
    [ProtoMember(1)] public PolymorphicClassBase[] Items;
}

[ProtoContract, CompatibilityLevel(CompatibilityLevel.Level300)]
public sealed class NullableFieldsList
{
    [ProtoMember(1)] public NullableFields[] Items;
}

// CollectionsTest's nullable collections. Protobuf has no null in a repeated field (protobuf-net throws on one: "An
// element ... was null"), so protobuf-net writes them through these contracts, each element wrapped in a message with
// an optional value ([NullWrappedValue]: an empty message is null), its documented way, and Google.Protobuf reads the
// messages they generate. Level300 writes decimal as a string, as the workloads do (the default level's bcl.Decimal
// wrapper gets a name protoc rejects).

[ProtoContract, CompatibilityLevel(CompatibilityLevel.Level300)]
public sealed class NullableLongList
{
    [ProtoMember(1), NullWrappedValue] public long?[] Items;
}

[ProtoContract, CompatibilityLevel(CompatibilityLevel.Level300)]
public sealed class NullableIntList
{
    [ProtoMember(1), NullWrappedValue] public List<int?> Items;
}

[ProtoContract, CompatibilityLevel(CompatibilityLevel.Level300)]
public sealed class NullableDecimalList
{
    [ProtoMember(1), NullWrappedValue] public decimal?[] Items;
}

/// <summary>
/// CollectionsTest's other collections of <c>int?</c> (CollectionsTest.NullableCollections.cs), one contract for every
/// collection type protobuf-net writes as a repeated field: <see cref="NullableIntList"/> around
/// <typeparamref name="TCollection"/>. Not in cases.proto: Google.Protobuf has one repeated field for every sequence,
/// so its case of each would be NullableInts' again.
/// </summary>
[ProtoContract, CompatibilityLevel(CompatibilityLevel.Level300)]
public sealed class NullableIntsIn<TCollection>
{
    [ProtoMember(1), NullWrappedValue] public TCollection Items;
}

// NullableTest's string[] with nulls: a null string is wrapped as the collections' nulls are (Level300:
// google.protobuf.StringValue); and its raw struct with nullable members in an array (RawNullable, Level300 as
// NullableFields), named for cases.proto.

[ProtoContract, CompatibilityLevel(CompatibilityLevel.Level300)]
public sealed class NullableStringList
{
    [ProtoMember(1), NullWrappedValue] public string[] Items;
}

[ProtoContract, CompatibilityLevel(CompatibilityLevel.Level300)]
public sealed class RawNullableList
{
    [ProtoMember(1)] public RawNullable[] Items;
}

// NullableTest's nullable structs and nullable references. A null message is an absent field, so one nullable struct is
// a message with an optional SimpleStruct field. protobuf-net cannot wrap a Nullable<T> of a message type in a repeated
// field ([NullWrappedValue] throws "No serializer for type System.Nullable`1[SimpleStruct]"), so a SimpleStruct?[] is
// written as an array of that message, the same bytes [NullWrappedValue] writes for a reference: protobuf-net's data is
// that array, made outside the timed region, as MemoryPack's SafeNullable copies are. A null SimpleClass is wrapped with
// [NullWrappedValue] (a message per element with an optional value: empty is null).

[ProtoContract]
public struct OptionalSimpleStruct
{
    [ProtoMember(1)] public SimpleStruct? Value;

    public static OptionalSimpleStruct[] From(SimpleStruct?[] values) =>
        System.Array.ConvertAll(values, v => new OptionalSimpleStruct { Value = v });

    public static SimpleStruct?[] ToNullable(OptionalSimpleStruct[] values) =>
        System.Array.ConvertAll(values, v => v.Value);
}

[ProtoContract]
public sealed class OptionalSimpleStructList
{
    [ProtoMember(1)] public OptionalSimpleStruct[] Items;
}

// NullableTest's ManagedStruct (a struct with a string) and CollectionsTest's Vec3?[] (the workloads' Vec3): the same
// contracts as SimpleStruct's, a list that names the top-level array and an optional field for a nullable struct.

[ProtoContract]
public sealed class ManagedStructList
{
    [ProtoMember(1)] public ManagedStruct[] Items;
}

[ProtoContract]
public struct OptionalManagedStruct
{
    [ProtoMember(1)] public ManagedStruct? Value;

    public static OptionalManagedStruct[] From(ManagedStruct?[] values) =>
        System.Array.ConvertAll(values, v => new OptionalManagedStruct { Value = v });

    public static ManagedStruct?[] ToNullable(OptionalManagedStruct[] values) =>
        System.Array.ConvertAll(values, v => v.Value);
}

[ProtoContract]
public sealed class OptionalManagedStructList
{
    [ProtoMember(1)] public OptionalManagedStruct[] Items;
}

[ProtoContract]
public struct OptionalVec3
{
    [ProtoMember(1)] public Vec3? Value;

    public static OptionalVec3[] From(Vec3?[] values) =>
        System.Array.ConvertAll(values, v => new OptionalVec3 { Value = v });

    public static Vec3?[] ToNullable(OptionalVec3[] values) =>
        System.Array.ConvertAll(values, v => v.Value);
}

[ProtoContract]
public sealed class OptionalVec3List
{
    [ProtoMember(1)] public OptionalVec3[] Items;
}

/// <summary>One nullable SimpleClass: protobuf has no null message at the top level, so it is an optional field (absent: null).</summary>
[ProtoContract]
public sealed class OptionalSimpleClass
{
    [ProtoMember(1)] public SimpleClass Value;
}

[ProtoContract]
public sealed class NullableObjectList
{
    [ProtoMember(1), NullWrappedValue] public SimpleClass[] Items;
}

/// <summary>System.Text.Json's source-generated metadata of the cases (fields included: most members are fields).</summary>
[JsonSourceGenerationOptions(IncludeFields = true)]
[JsonSerializable(typeof(SimpleClass))]
[JsonSerializable(typeof(SimpleClass[]))]
[JsonSerializable(typeof(SimpleStruct))]
[JsonSerializable(typeof(SimpleStruct[]))]
[JsonSerializable(typeof(SimpleStruct?))]
[JsonSerializable(typeof(SimpleStruct?[]))]
[JsonSerializable(typeof(Vector4[]))]
[JsonSerializable(typeof(PolymorphicClassBase))]
[JsonSerializable(typeof(PolymorphicClassBase[]))]
[JsonSerializable(typeof(PolymorphicFooBase[]))]
[JsonSerializable(typeof(NullableFields))]
[JsonSerializable(typeof(NullableFields[]))]
[JsonSerializable(typeof(long?[]))]
[JsonSerializable(typeof(List<int?>))]
[JsonSerializable(typeof(decimal?[]))]
[JsonSerializable(typeof(ManagedStruct))]
[JsonSerializable(typeof(ManagedStruct[]))]
[JsonSerializable(typeof(ManagedStruct?))]
[JsonSerializable(typeof(ManagedStruct?[]))]
[JsonSerializable(typeof(Vec3?[]))]
[JsonSerializable(typeof(Queue<int?>))]
[JsonSerializable(typeof(HashSet<int?>))]
[JsonSerializable(typeof(LinkedList<int?>))]
[JsonSerializable(typeof(Collection<int?>))]
[JsonSerializable(typeof(ConcurrentQueue<int?>))]
[JsonSerializable(typeof(Memory<int?>))]
[JsonSerializable(typeof(IList<int?>))]
[JsonSerializable(typeof(RawNullable))]
[JsonSerializable(typeof(RawNullable[]))]
[JsonSerializable(typeof(string[]))]
public partial class CaseJson : JsonSerializerContext;
