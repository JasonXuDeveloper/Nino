// A BenchGen template (runtimes/csharp/tools/BenchGen), not compiled: System.Text.Json, source generated, UTF-8. A case
// names its type info ([BenchCase(Json = ...)]), a property of its JsonContext (CaseJson, Fixtures/CaseFormats.cs,
// unless the class or the case names another).
using System.Buffers;
using System.Text.Json;

namespace Nino.Benchmark.Adapters;

[BenchLibrary("Json", Order = 8, Slot = 5, Builds = "v4")]
[BenchArg("info", "{JsonContext}.Default.{Json}")]
internal static class JsonAdapter
{
    public static void Setup(out ArrayBufferWriter<byte> buffer, out Utf8JsonWriter writer, int capacity)
    {
        buffer = new ArrayBufferWriter<byte>(capacity);
        // What JsonSerializer's own writers use: no validation of the writer's calls.
        writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { SkipValidation = true });
    }

    public static byte[] ToBytes<T>(T value, JsonTypeInfo<T> info) => JsonSerializer.SerializeToUtf8Bytes(value, info);

    public static int Serialize<T>(ArrayBufferWriter<byte> buffer, Utf8JsonWriter writer, T value, JsonTypeInfo<T> info)
    {
        buffer.ResetWrittenCount();
        writer.Reset(buffer);
        JsonSerializer.Serialize(writer, value, info);
        return buffer.WrittenCount;
    }

    public static T Deserialize<T>(byte[] bytes, JsonTypeInfo<T> info) => JsonSerializer.Deserialize(bytes, info);
}
