// A BenchGen template (runtimes/csharp/tools/BenchGen), not compiled: protobuf-net, through the models' contracts
// (Fixtures/ProtobufModel.cs). Its bytes are what Google.Protobuf's messages are parsed from.
using System;
using System.IO;
using ProtoBuf;

namespace Nino.Benchmark.Adapters;

[BenchLibrary("Protobuf", Order = 3, Slot = 3, Builds = "v4")]
internal static class ProtobufAdapter
{
    public static void Setup(out MemoryStream stream, int capacity)
    {
        stream = new MemoryStream(capacity);
    }

    public static byte[] ToBytes<T>(T value) => ProtobufModel.Serialize(value);

    public static int Serialize<T>(MemoryStream stream, T value)
    {
        stream.SetLength(0);
        Serializer.Serialize(stream, value);
        return (int)stream.Length;
    }

    public static T Deserialize<T>(byte[] bytes) => Serializer.Deserialize<T>(new ReadOnlySpan<byte>(bytes));
}
