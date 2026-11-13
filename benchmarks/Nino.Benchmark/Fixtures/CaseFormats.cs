using System.Numerics;
using System.Text.Json.Serialization;
using ProtoBuf;

#nullable disable
namespace Nino.Benchmark;

// What Google.Protobuf and System.Text.Json need for SimpleTest's and PolymorphicTest's data (the v4 build only).

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
public partial class CaseJson : JsonSerializerContext;
