using System.Collections.Generic;
using System.Numerics;
using System.Text.Json.Serialization;
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

/// <summary>System.Text.Json's source-generated metadata of the cases (fields included: most members are fields).</summary>
[JsonSourceGenerationOptions(IncludeFields = true)]
[JsonSerializable(typeof(SimpleClass))]
[JsonSerializable(typeof(SimpleClass[]))]
[JsonSerializable(typeof(SimpleStruct))]
[JsonSerializable(typeof(SimpleStruct[]))]
[JsonSerializable(typeof(Vector4[]))]
[JsonSerializable(typeof(PolymorphicClassBase))]
[JsonSerializable(typeof(PolymorphicClassBase[]))]
[JsonSerializable(typeof(PolymorphicFooBase[]))]
[JsonSerializable(typeof(NullableFields))]
[JsonSerializable(typeof(NullableFields[]))]
[JsonSerializable(typeof(long?[]))]
[JsonSerializable(typeof(List<int?>))]
[JsonSerializable(typeof(decimal?[]))]
public partial class CaseJson : JsonSerializerContext;
