using System;
using MemoryPack;
using MessagePack;
using Nino.Core;
using ProtoBuf;

#nullable disable
namespace Nino.Benchmark.Workloads;

// The payloads of WorkloadTest: what services and games send, one model per workload that every library writes with
// its own attributes. Google.Protobuf writes the messages of workloads.proto, which protobuf-net's contracts of these
// models generate (Verify checks that the file is current; `--write-proto` rewrites it), so both write the same
// messages. The conventions every model follows:
//  - protobuf-net at CompatibilityLevel.Level300, the well-known types Google.Protobuf uses: DateTime as
//    google.protobuf.Timestamp, decimal and Guid as strings. Every DateTime is UTC (what a timestamp carries).
//  - A type with subtypes numbers them from 1 ([ProtoInclude]) and its own members from 20: protobuf-net reads the
//    subtype's field before the base's, and Google.Protobuf writes fields in the order of their numbers.
//  - Every list has an element and every string that is not null has a character: protobuf writes no empty list or
//    string, so they would read back as null.
//  - A feature a library lacks is measured through its common workaround, said where the model uses it: shared
//    objects are written as copies by MessagePack and protobuf (each reference its own copy, read back as equal
//    copies), and a library that cannot deserialize into an existing object reads a new one.

/// <summary>An engine's own vector type (game engines bring their own math types).</summary>
[NinoType, MemoryPackable, MessagePackObject, ProtoContract]
public partial struct Vec3
{
    [Key(0)] [ProtoMember(1)] public float X;
    [Key(1)] [ProtoMember(2)] public float Y;
    [Key(2)] [ProtoMember(3)] public float Z;
}

[NinoType, MemoryPackable, MessagePackObject, ProtoContract]
public partial struct Quat
{
    [Key(0)] [ProtoMember(1)] public float X;
    [Key(1)] [ProtoMember(2)] public float Y;
    [Key(2)] [ProtoMember(3)] public float Z;
    [Key(3)] [ProtoMember(4)] public float W;
}

public static class Data
{
    public static readonly DateTime Epoch = new(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);

    private static readonly string[] First = ["Jane", "Wei", "Amara", "Lucas", "Priya", "Kenji", "Sofia", "Omar"];
    private static readonly string[] Last = ["Doe", "Zhang", "Okafor", "Silva", "Sharma", "Sato", "Rossi", "Haddad"];

    public static string Name(Random r) => First[r.Next(First.Length)] + " " + Last[r.Next(Last.Length)];

    public static Guid NewGuid(Random r)
    {
        Span<byte> bytes = stackalloc byte[16];
        r.NextBytes(bytes);
        return new Guid(bytes);
    }

    public static Vec3 V(Random r, float scale) =>
        new() { X = (r.NextSingle() * 2 - 1) * scale, Y = (r.NextSingle() * 2 - 1) * scale, Z = (r.NextSingle() * 2 - 1) * scale };

    public static Quat Q(Random r) => new() { X = 0, Y = r.NextSingle(), Z = 0, W = r.NextSingle() };

    /// <summary>A paragraph of HTML, as a Q&amp;A site stores a post's body.</summary>
    public static string Html(Random r, int sentences)
    {
        var words = new[] { "serializer", "buffer", "the", "async", "value", "returns", "null", "when", "list", "type",
            "generic", "method", "this", "struct", "because", "allocation", "call", "string", "faster", "cache" };
        var text = new System.Text.StringBuilder("<p>");
        for (int s = 0; s < sentences; s++)
        {
            int n = r.Next(8, 16);
            for (int w = 0; w < n; w++) text.Append(w == 0 ? "" : " ").Append(words[r.Next(words.Length)]);
            text.Append(s % 3 == 2 ? ".</p>\n<p>" : ". ");
        }

        return text.Append("<code>var x = Serializer.Deserialize&lt;T&gt;(bytes);</code></p>").ToString();
    }
}
