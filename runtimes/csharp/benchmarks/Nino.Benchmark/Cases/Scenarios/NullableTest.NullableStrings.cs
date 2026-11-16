using System;
using System.Linq;
#if !NINO_BENCH_V3
using BenchmarkDotNet.Attributes;
using ProtoBuf;
#endif

#nullable disable
namespace Nino.Benchmark;

/// <summary>
/// Nullable references in a collection of strings: <c>string[1000]</c> with every fifth element null (NullableObjects is
/// the same of a class, SimpleClass[100]). Every library writes a null string natively but protobuf, which has no
/// null in a repeated field: protobuf-net wraps each element (<see cref="NullableStringList"/>), and Google.Protobuf
/// reads the message that generates. A reference has no other form: <c>NinoNullable&lt;T&gt;</c> and
/// <c>SafeNullable&lt;T&gt;</c> take an unmanaged T.
/// </summary>
[BenchCase("NullableStrings", Value = "Strings", ProtobufValue = "StringsProtobuf", Proto = "NullableStringList",
    Json = "StringArray")]
public partial class NullableTest
{
    private static readonly string[] Strings =
        Enumerable.Range(0, 1000).Select(i => i % 5 == 0 ? null : "item-" + i.ToString("D4")).ToArray();
#if !NINO_BENCH_V3
    private static readonly NullableStringList StringsProtobuf = new() { Items = Strings };

    // protobuf-net reads the strings through its contract: the collection it holds.
    [Benchmark, BenchmarkCategory("NullableStringsDeserialize")]
    public string[] ProtobufDeserializeNullableStrings() =>
        Serializer.Deserialize<NullableStringList>(new ReadOnlySpan<byte>(ProtobufNullableStringsBytes)).Items;
#endif
}
