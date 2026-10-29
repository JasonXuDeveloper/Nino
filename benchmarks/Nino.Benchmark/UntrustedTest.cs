using System;
using System.Collections.Generic;
using System.Linq;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using MemoryPack;
using Nino.Core;

#nullable disable
namespace Nino.Benchmark;

/// <summary>A raw struct (no [NinoType]) with fields Untrusted mode checks: a DateTime and a bool.</summary>
public struct CheckedSample
{
    public DateTime At;
    public double Value;
    public int Id;
    public bool Valid;
}

/// <summary>
/// Reads in <see cref="NinoSettings.Untrusted"/> mode, which checks every value no constructor makes (a DateTime past
/// its maximum, a bool other than 0 or 1) before copying raw values: a list of 1,000 raw structs with such fields,
/// against MemoryPack, which copies them unchecked. The setting is process-wide and frozen on first use, so it is set
/// in the static constructor, before this class touches Nino: BenchmarkDotNet runs each case in a process of its own
/// (its host, which only lists the cases, may have frozen it before). Under --verify and asm.py (one process for
/// every class) it is already frozen and the cases run in trusted mode.
/// </summary>
[HideColumns("StdDev", "RatioSD", "Error")]
[MinColumn, MaxColumn]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[NinoJob]
[GcServer(true)]
[MemoryDiagnoser]
[MarkdownExporterAttribute.GitHub]
public class UntrustedTest
{
    private const int N = 1000;
    private static readonly List<CheckedSample> Samples;
    private static readonly byte[] NinoSamplesBytes;
    private static readonly byte[] MemoryPackSamplesBytes;

    static UntrustedTest()
    {
        // Frozen already where another class used Nino first in this process: under --verify, asm.py's warm-up and
        // BenchmarkDotNet's host, none of which times a case.
        try
        {
            NinoSettings.Untrusted = true;
        }
        catch (InvalidOperationException)
        {
        }

        Samples = Enumerable.Range(0, N).Select(i => new CheckedSample
        {
            At = BenchmarkRandom.Now.AddSeconds(i * 37), Value = i * 0.25, Id = i * 7, Valid = i % 3 != 0,
        }).ToList();
        NinoSamplesBytes = NinoSerializer.Serialize(Samples);
        MemoryPackSamplesBytes = MemoryPackSerializer.Serialize(Samples);
    }

    [Benchmark(Baseline = true), BenchmarkCategory("UntrustedListDeserialize")]
    public List<CheckedSample> NinoDeserializeUntrustedList() =>
        NinoDeserializer.Deserialize<List<CheckedSample>>(NinoSamplesBytes);

    [Benchmark, BenchmarkCategory("UntrustedListDeserialize")]
    public List<CheckedSample> MemoryPackDeserializeUntrustedList() =>
        MemoryPackSerializer.Deserialize<List<CheckedSample>>(MemoryPackSamplesBytes);
}
