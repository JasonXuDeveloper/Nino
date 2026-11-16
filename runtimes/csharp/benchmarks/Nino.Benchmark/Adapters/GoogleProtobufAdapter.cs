// A BenchGen template (runtimes/csharp/tools/BenchGen), not compiled: Google.Protobuf, through the messages protoc
// generates from the class's .proto (Fixtures/cases.proto, Workloads/workloads.proto). A case names its message
// ([BenchCase(Proto = ...)], a type of the namespace the class's alias P names); the message is parsed from
// protobuf-net's bytes outside the timed region (Prepare), and Verify.cs reads it back through protobuf-net (the
// class's IReadBack).
using System;
using System.Buffers;
using Google.Protobuf;

namespace Nino.Benchmark.Adapters;

[BenchLibrary("GoogleProtobuf", Order = 4, Slot = 4, Builds = "v4", Prepared = "Proto")]
[BenchArg("Message", "P.{Proto}")]
internal static class GoogleProtobufAdapter
{
    public static void Setup(out ArrayBufferWriter<byte> buffer, int capacity)
    {
        buffer = new ArrayBufferWriter<byte>(capacity);
    }

    public static Message Prepare<T>(T value) => Message.Parser.ParseFrom(ProtobufModel.Serialize(value));

    public static byte[] ToBytes(Message value) => value.ToByteArray();

    public static int Serialize(ArrayBufferWriter<byte> buffer, Message value)
    {
        buffer.ResetWrittenCount();
        value.WriteTo(buffer);
        return buffer.WrittenCount;
    }

    public static Message Deserialize(byte[] bytes) => Message.Parser.ParseFrom(new ReadOnlySpan<byte>(bytes));
}
