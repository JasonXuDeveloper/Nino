using System;
using System.IO;
using System.Numerics;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using Nino.Benchmark.Workloads;
using ProtoBuf;
using ProtoBuf.Meta;

#nullable disable
namespace Nino.Benchmark;

/// <summary>
/// What protobuf-net needs beyond the attributes on the benchmark types, set up on its default model before the first
/// value is written: the fields of <see cref="Vector3"/> and <see cref="Vector4"/> (BCL types that carry no
/// attributes), and a surrogate for <see cref="DateTimeOffset"/>, which protobuf-net has no serializer for (its
/// documented way for such a type). A class whose data holds one of them reads <see cref="Ready"/> in its first static
/// field, so the model is set up before that class serializes anything.
/// </summary>
public static class ProtobufModel
{
    public static readonly bool Ready = Configure();

    private static bool Configure()
    {
        var model = RuntimeTypeModel.Default;
        model.Add(typeof(Vector3), false).Add(1, nameof(Vector3.X)).Add(2, nameof(Vector3.Y)).Add(3, nameof(Vector3.Z));
        model.Add(typeof(Vector4), false).Add(1, nameof(Vector4.X)).Add(2, nameof(Vector4.Y)).Add(3, nameof(Vector4.Z))
            .Add(4, nameof(Vector4.W));
        model.Add(typeof(DateTimeOffset), false).SetSurrogate(typeof(DateTimeOffsetSurrogate));
        return true;
    }

    /// <summary>The bytes protobuf-net writes for <paramref name="value"/>, through the stream API the benchmarks time.</summary>
    public static byte[] Serialize<T>(T value)
    {
        using var stream = new MemoryStream();
        Serializer.Serialize(stream, value);
        return stream.ToArray();
    }

    /// <summary>
    /// Verify.cs (<see cref="IReadBack"/>): a Google.Protobuf message read back through protobuf-net as the case's
    /// <paramref name="model"/>, any other result as it is. The message's subtype fields (each <c>oneof</c>) go first:
    /// protobuf-net reads the subtype's field before the base's, and Google.Protobuf writes fields in the order of
    /// their numbers, which put the base's first where a type numbers its subtypes after its own members
    /// (SimpleTest's and PolymorphicTest's models, written long before Google.Protobuf joined).
    /// </summary>
    public static object ReadBack(object result, Type model) => result is IMessage message
        ? Serializer.NonGeneric.Deserialize(model, new MemoryStream(SubtypesFirst(message.ToByteArray(), message.Descriptor)))
        : result;

    /// <summary>The fields of a message of type <paramref name="descriptor"/>, those of a <c>oneof</c> first, at every level.</summary>
    private static byte[] SubtypesFirst(byte[] bytes, MessageDescriptor descriptor)
    {
        var input = new CodedInputStream(bytes);
        using var first = new MemoryStream();
        using var rest = new MemoryStream();
        var firstOutput = new CodedOutputStream(first);
        var restOutput = new CodedOutputStream(rest);
        uint tag;
        while ((tag = input.ReadTag()) != 0)
        {
            var field = descriptor.FindFieldByNumber(WireFormat.GetTagFieldNumber(tag));
            var output = field?.RealContainingOneof != null ? firstOutput : restOutput;
            output.WriteTag(tag);
            switch (WireFormat.GetTagWireType(tag))
            {
                case WireFormat.WireType.Varint: output.WriteUInt64(input.ReadUInt64()); break;
                case WireFormat.WireType.Fixed32: output.WriteFixed32(input.ReadFixed32()); break;
                case WireFormat.WireType.Fixed64: output.WriteFixed64(input.ReadFixed64()); break;
                case WireFormat.WireType.LengthDelimited:
                    var value = input.ReadBytes();
                    // A message (a map's entries too) is reordered inside; strings and packed scalars are copied.
                    output.WriteBytes(field?.FieldType == FieldType.Message
                        ? ByteString.CopyFrom(SubtypesFirst(value.ToByteArray(), field.MessageType))
                        : value);
                    break;
                default: throw new InvalidDataException($"wire type {WireFormat.GetTagWireType(tag)} in {descriptor.Name}");
            }
        }

        firstOutput.Flush();
        restOutput.Flush();
        first.Write(rest.GetBuffer(), 0, (int)rest.Length);
        return first.ToArray();
    }
}

/// <summary>
/// A .proto file Google.Protobuf's messages are compiled from (Grpc.Tools' protoc, at build time): what protobuf-net's
/// contracts of the models generate (<c>RuntimeTypeModel.GetSchema</c>, proto3), so both libraries write the same
/// messages and Google.Protobuf's data can be parsed from protobuf-net's bytes. The setup of a class that uses one
/// fails while it is stale (<see cref="Check"/>); <c>Nino.Benchmark --write-proto</c>, run in benchmarks/Nino.Benchmark,
/// rewrites every file (<see cref="Write"/>).
/// </summary>
public sealed class ProtoFile(string path, string package, string csharpNamespace, string models, params Type[] types)
{
    /// <summary>SimpleTest's and PolymorphicTest's models, with protobuf-net's default representation of DateTime and
    /// Guid (its bcl.proto, which Fixtures/protobuf-net/bcl.proto holds for protoc).</summary>
    public static readonly ProtoFile Cases = new("Fixtures/cases.proto", "nino.cases", "Nino.Benchmark.Proto",
        "SimpleTest and PolymorphicTest models", typeof(SimpleClassList), typeof(SimpleStructList), typeof(VectorList),
        typeof(PolymorphicFooList), typeof(PolymorphicClassList));

    public static readonly ProtoFile Workloads = new("Workloads/workloads.proto", "nino.workloads",
        "Nino.Benchmark.Workloads.Proto", "workload models", typeof(MediaContent), typeof(StackOverflowPage),
        typeof(RpcCall), typeof(ProfileV2), typeof(ProfileV1), typeof(EventStream), typeof(LogBatch),
        typeof(StateSnapshot), typeof(LockstepFrames), typeof(AoiUpdate), typeof(WorldRegion), typeof(CardBattleLog),
        typeof(IdleSaveV2), typeof(IdleSaveV1));

    public static readonly ProtoFile[] All = [Cases, Workloads];

    /// <summary>The file's name, which is also its embedded resource's (Nino.Benchmark.csproj).</summary>
    private string Name => Path.GetFileName(path);

    /// <summary>The .proto protobuf-net writes for the models' contracts.</summary>
    public string Generate()
    {
        _ = ProtobufModel.Ready;
        var options = new SchemaGenerationOptions { Syntax = ProtoSyntax.Proto3, Package = package };
        foreach (var type in types)
            options.Types.Add(type);
        var schema = RuntimeTypeModel.Default.GetSchema(options);
        return $"// Generated by Nino.Benchmark --write-proto from the protobuf-net contracts of the {models}: do not edit.\n" +
               schema.Replace($"package {package};", $"package {package};\noption csharp_namespace = \"{csharpNamespace}\";")
                   .Replace("\r\n", "\n");
    }

    /// <summary>Throws while the compiled file is not what the contracts generate.</summary>
    public void Check()
    {
        using var stream = typeof(ProtoFile).Assembly.GetManifestResourceStream(Name)!;
        if (new StreamReader(stream).ReadToEnd().Replace("\r\n", "\n") != Generate())
            throw new InvalidOperationException($"{Name} is not what the {models}' protobuf-net contracts say: run Nino.Benchmark --write-proto");
    }

    public void Write() => File.WriteAllText(path, Generate());
}

/// <summary>A <see cref="DateTimeOffset"/> as protobuf-net writes it here: its clock time in ticks and its offset in minutes.</summary>
[ProtoContract]
public struct DateTimeOffsetSurrogate
{
    [ProtoMember(1)] public long Ticks;
    [ProtoMember(2)] public short OffsetMinutes;

    public static implicit operator DateTimeOffsetSurrogate(DateTimeOffset value) =>
        new() { Ticks = value.Ticks, OffsetMinutes = (short)(value.Offset.Ticks / TimeSpan.TicksPerMinute) };

    public static implicit operator DateTimeOffset(DateTimeOffsetSurrogate value) =>
        new(value.Ticks, TimeSpan.FromMinutes(value.OffsetMinutes));
}
