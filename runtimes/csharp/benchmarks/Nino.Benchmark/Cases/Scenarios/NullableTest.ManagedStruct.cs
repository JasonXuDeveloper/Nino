using System;
#if !NINO_BENCH_V3
using BenchmarkDotNet.Attributes;
using ProtoBuf;
#endif

#nullable disable
namespace Nino.Benchmark;

/// <summary>
/// A struct that holds a reference (<see cref="ManagedStruct"/>: no block copy, each member written on its own), with a
/// value and as <c>ManagedStruct?</c>: one value, an array of 100, one <c>ManagedStruct?</c> that holds a value and
/// <c>ManagedStruct?[100]</c> with every fifth element null. Each pair shows what the nullable wrapper costs a struct
/// that is not raw. MemoryPack writes <c>ManagedStruct?</c> with its own nullable formatter (a flag, then the value):
/// SafeNullable takes an unmanaged T only, and nothing here is copied as memory, so there is no padding to leak and no
/// other form to time. protobuf writes the nullable ones as <see cref="OptionalManagedStruct"/> (CaseFormats.cs).
/// </summary>
[BenchCase("ManagedStruct", Value = "Managed", Proto = "ManagedStruct", Json = "ManagedStruct")]
[BenchCase("ManagedStructs", Value = "Manageds", Proto = "ManagedStructList", Json = "ManagedStructArray")]
[BenchCase("NullableManagedStruct", Value = "NullableManaged", ProtobufValue = "NullableManagedProtobuf",
    Proto = "OptionalManagedStruct", Json = "NullableManagedStruct")]
[BenchCase("NullableManagedStructs", Value = "NullableManageds", ProtobufValue = "NullableManagedsProtobuf",
    Proto = "OptionalManagedStructList", Json = "NullableManagedStructArray")]
public partial class NullableTest
{
    private static readonly ManagedStruct Managed = ManagedStruct.Create(0);
    private static readonly ManagedStruct[] Manageds = ManagedStruct.CreateArray(100);
    private static readonly ManagedStruct? NullableManaged = Managed;
    private static readonly ManagedStruct?[] NullableManageds = ManagedStruct.CreateNullableArray(100);
#if !NINO_BENCH_V3
    private static readonly OptionalManagedStruct NullableManagedProtobuf = new() { Value = NullableManaged };
    private static readonly OptionalManagedStructList NullableManagedsProtobuf =
        new() { Items = OptionalManagedStruct.From(NullableManageds) };

    // protobuf-net reads a nullable value through its contract: the member that holds it.
    [Benchmark, BenchmarkCategory("NullableManagedStructDeserialize")]
    public ManagedStruct? ProtobufDeserializeNullableManagedStruct() =>
        Serializer.Deserialize<OptionalManagedStruct>(new ReadOnlySpan<byte>(ProtobufNullableManagedStructBytes)).Value;

    [Benchmark, BenchmarkCategory("NullableManagedStructsDeserialize")]
    public OptionalManagedStruct[] ProtobufDeserializeNullableManagedStructs() =>
        Serializer.Deserialize<OptionalManagedStructList>(new ReadOnlySpan<byte>(ProtobufNullableManagedStructsBytes)).Items;
#endif
}
