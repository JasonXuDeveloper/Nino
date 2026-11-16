using System;
using System.Collections.Generic;
using System.Linq;
using BenchmarkDotNet.Configs;
#if !NINO_BENCH_V3
using LuminPack.Attribute;
using MemoryPack;
using MessagePack;
using Nino.Core;
using ProtoBuf;
#endif

#nullable disable
namespace Nino.Benchmark;

/// <summary>
/// Text, the string cases of the other platforms' benchmarks, serialized to a reused buffer writer and deserialized
/// into new instances:
///  - Go's, Rust's and TypeScript's: 1,000 short ASCII strings ("string 0" to "string 999"), and 1,000 strings of
///    ASCII, Latin-1 ("crème brûlée i", one byte a char in Nino) and other text ("строка 字符串 🎉 i", UTF-16 in Nino,
///    a char outside the BMP) in turn, also as a [NinoUtf8] string[] (each element UTF-8);
///  - Unity's (BurstBenchmark, the kernels that narrow and widen text): one string of 32, 1,000 and 100,000 printable
///    ASCII chars (a byte a char in Nino), of CJK chars (UTF-16 after the narrowing stops at the first char), the
///    ASCII ones as [NinoUtf8] (whose ASCII the kernels copy) and 1,000 CJK chars as [NinoUtf8] (all of it .NET's
///    encoder), and a list of 1,000 ASCII words of 8 to 40 chars.
/// Against MemoryPack (UTF-8 by default), MessagePack (UTF-8), protobuf-net (UTF-8) and LuminPack. The [NinoUtf8]
/// cases only in v4 (3.3.13 has no [NinoUtf8]). The timed methods are StringsTest.Cases.g.cs (Adapters/,
/// runtimes/csharp/tools/BenchGen).
/// </summary>
[BenchCase("AsciiStrings")]
[BenchCase("MixedStrings", Quick = true)]
[BenchCase("Utf8MixedStrings", Builds = "v4")]
[BenchCase("AsciiString32")]
[BenchCase("AsciiString1000")]
[BenchCase("AsciiString100000", Quick = true)]
[BenchCase("CjkString32")]
[BenchCase("CjkString1000", Quick = true)]
[BenchCase("CjkString100000")]
[BenchCase("Utf8AsciiString32", Builds = "v4")]
[BenchCase("Utf8AsciiString1000", Builds = "v4")]
[BenchCase("Utf8AsciiString100000", Builds = "v4")]
[BenchCase("Utf8CjkString1000", Builds = "v4")]
[BenchCase("AsciiWords")]
[BenchClass(Buffer = "1 << 20", Bytes = "{Library}{Case}Bytes", Order = "case")]
[PayloadColumn]
[HideColumns("StdDev", "RatioSD", "Error")]
[MinColumn, MaxColumn]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[NinoJob]
[GcServer(true)]
[MemoryDiagnoser]
[MarkdownExporterAttribute.GitHub]
public partial class StringsTest
{
    private static readonly string[] AsciiStrings = Enumerable.Range(0, 1000).Select(i => "string " + i).ToArray();

    private static readonly string[] MixedStrings = Enumerable.Range(0, 1000).Select(i => (i % 3) switch
    {
        0 => "string " + i,
        1 => "crème brûlée " + i,
        _ => "строка 字符串 🎉 " + i,
    }).ToArray();

    private static readonly Random Random = new(5);

    private static string Ascii(int n) => new(Enumerable.Range(0, n).Select(_ => (char)Random.Next(32, 127)).ToArray());
    private static string Cjk(int n) => new(Enumerable.Range(0, n).Select(_ => (char)Random.Next(0x4E00, 0x9FA6)).ToArray());

    private static readonly string AsciiString32 = Ascii(32);
    private static readonly string AsciiString1000 = Ascii(1000);
    private static readonly string AsciiString100000 = Ascii(100000);
    private static readonly string CjkString32 = Cjk(32);
    private static readonly string CjkString1000 = Cjk(1000);
    private static readonly string CjkString100000 = Cjk(100000);
    private static readonly List<string> AsciiWords = Enumerable.Range(0, 1000).Select(_ => Ascii(Random.Next(8, 41))).ToList();
#if !NINO_BENCH_V3
    private static readonly Utf8Strings Utf8MixedStrings = new() { Values = MixedStrings };
    private static readonly Utf8Text Utf8AsciiString32 = new() { Text = Ascii(32) };
    private static readonly Utf8Text Utf8AsciiString1000 = new() { Text = Ascii(1000) };
    private static readonly Utf8Text Utf8AsciiString100000 = new() { Text = Ascii(100000) };
    private static readonly Utf8Text Utf8CjkString1000 = new() { Text = Cjk(1000) };
#endif
}

#if !NINO_BENCH_V3
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
#endif
