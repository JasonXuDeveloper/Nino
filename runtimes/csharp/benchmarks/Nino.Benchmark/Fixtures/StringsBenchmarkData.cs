#if !NINO_BENCH_V3
using LuminPack.Attribute;
using MemoryPack;
using MessagePack;
using Nino.Core;
using ProtoBuf;

#nullable disable
namespace Nino.Benchmark
{
    // StringsTest's [NinoUtf8] models (v4 only: 3.3.13 has no [NinoUtf8]).

    /// <summary>A [NinoUtf8] string (StringsTest): Nino writes it as UTF-8, as the other libraries write every string.</summary>
    [NinoType]
    [MemoryPackable, LuminPackable]
    [MessagePackObject]
    [ProtoContract]
    public sealed partial class Utf8Text
    {
        [Key(0)] [ProtoMember(1)] [NinoUtf8] public string Text;
    }

    /// <summary>A [NinoUtf8] string[] (StringsTest): each element UTF-8, Go's and Rust's strings-mixed-utf8 case.</summary>
    [NinoType]
    [MemoryPackable, LuminPackable]
    [MessagePackObject]
    [ProtoContract]
    public sealed partial class Utf8Strings
    {
        [Key(0)] [ProtoMember(1)] [NinoUtf8] public string[] Values;
    }
}
#endif
