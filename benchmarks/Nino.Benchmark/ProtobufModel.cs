using System;
using System.IO;
using System.Numerics;
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
