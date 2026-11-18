// The #355 PR 3 probe: generated with NINO_PROBE_STEPS (Nino.Benchmark.csproj), which older releases' generators do
// not know: left out of A/B runs against them.
#if !NINO_BENCH_LEGACY && !NINO_BENCH_V3
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using BenchmarkDotNet.Configs;
using Nino.Core;
using Nino.Core.CompilerServices;
using FooFormatter = NinoGen.Nino_Benchmark_PolymorphicFooBaseFormatter;
using SmallFormatter = NinoGen.Nino_Benchmark_StepProbeBaseFormatter;

namespace Nino.Benchmark;

#nullable disable

// Probes of #355's decision (comment 6985532772): whether a polymorphic base's elements written through one Core
// kernel generic over a generated step (NinoKernel.WriteEach / WriteEntries, the base's __Step) keep the gain of the
// generated per-base loops (WriteElements, WritePairs32). Every shape writes the same bytes (StepProbe.Check, and
// DispatchTests.StepKernelsWriteTheBytesOfTheElementLoop). Only a filter that names a probe runs it (Program.cs);
// the methods start with NinoStep for asm.py (jitwarm calls the cases named Nino*: --filter NinoStep).
//   Loop          today's generated loop (WriteElements / WritePairs32): the chain and the bodies inline
//   Step          the kernel and __Step: the chain inline, each body a direct NoInlining call (writer by reference)
//   StepInline    the kernel and __StepInline: the chain and the bodies inline in the step
//   PerValue      the formatter's Serialize per element, the writer by reference (the dispatch called per element)
//   PairComposed  dictionaries only: the kernel over a composed pair step (NinoPairStep: the key and __Step), which is
//                 shared code over the class value, reached per pair through the generic dictionary (call rax)

/// <summary>A small hierarchy for the probe: three subtypes, two raw bodies and one with a string.</summary>
[NinoType]
public abstract class StepProbeBase
{
    public int Id;
}

[NinoType]
public sealed class StepProbeA : StepProbeBase
{
    public long Ticks;
}

[NinoType]
public sealed class StepProbeB : StepProbeBase
{
    public double Ratio;
    public float Scale;
}

[NinoType]
public sealed class StepProbeC : StepProbeBase
{
    public string Name;
}

/// <summary>The shapes of writing one run of a polymorphic base, over a base type and its generated formatter.</summary>
internal static class StepShapes<T, TFormatter, TStep, TStepInline>
    where T : class
    where TFormatter : struct, INinoFormatter<T>
    where TStep : struct, INinoStep<T>
    where TStepInline : struct, INinoStep<T>
{
    public static void Loop(ReadOnlySpan<T> values, ref Writer writer) => default(TFormatter).WriteElements(values, ref writer);

    public static void Step(ReadOnlySpan<T> values, ref Writer writer) =>
        NinoKernel.WriteEach<T, TFormatter, TStep>(values, ref writer);

    public static void StepInline(ReadOnlySpan<T> values, ref Writer writer) =>
        NinoKernel.WriteEach<T, TFormatter, TStepInline>(values, ref writer);

    public static void PerValue(ReadOnlySpan<T> values, ref Writer writer)
    {
        ref T item = ref MemoryMarshal.GetReference(values);
        for (int left = values.Length; left > 0; left--, item = ref Unsafe.Add(ref item, 1))
            default(TFormatter).Serialize(item, ref writer);
    }

    public static void PairLoop(Dictionary<int, T> pairs, ref Writer writer) => default(TFormatter).WritePairs32(pairs, ref writer);

    public static void PairStep(Dictionary<int, T> pairs, ref Writer writer) =>
        NinoKernel.WritePairs<int, UnmanagedFormatter<int>, T, TFormatter, TStep>(pairs, ref writer);

    public static void PairStepInline(Dictionary<int, T> pairs, ref Writer writer) =>
        NinoKernel.WritePairs<int, UnmanagedFormatter<int>, T, TFormatter, TStepInline>(pairs, ref writer);

    public static void PairComposed(Dictionary<int, T> pairs, ref Writer writer) =>
        NinoKernel.WriteEntries<int, UnmanagedFormatter<int>, T, TFormatter,
            NinoPairStep<int, UnmanagedFormatter<int>, T, TStep>>(pairs, ref writer);

    public static void PairPerValue(Dictionary<int, T> pairs, ref Writer writer)
    {
        foreach (var pair in pairs)
        {
            writer.UnsafeWrite(pair.Key);
            default(TFormatter).Serialize(pair.Value, ref writer);
        }
    }
}

internal static class StepProbeData
{
    /// <summary>The <paramref name="i"/>th of LuminPack's 20 IFoo subtypes in turn (as PolymorphicTest).</summary>
    public static PolymorphicFooBase Foo(int i) => (i % 20) switch
    {
        0 => FooA.Create(), 1 => FooB.Create(), 2 => FooC.Create(), 3 => FooD.Create(), 4 => FooE.Create(),
        5 => FooF.Create(), 6 => FooG.Create(), 7 => FooH.Create(), 8 => FooI.Create(), 9 => FooJ.Create(),
        10 => FooK.Create(), 11 => FooL.Create(), 12 => FooM.Create(), 13 => FooN.Create(), 14 => FooO.Create(),
        15 => FooP.Create(), 16 => FooQ.Create(), 17 => FooR.Create(), 18 => FooS.Create(), _ => FooZ.Create(),
    };

    public static StepProbeBase Small(int i) => (i % 3) switch
    {
        0 => new StepProbeA { Id = i, Ticks = 638_000_000_000_000_000L + i },
        1 => new StepProbeB { Id = i, Ratio = i * 0.5, Scale = 1.25f },
        _ => new StepProbeC { Id = i, Name = "step" + i },
    };

    public delegate void WriteRun<in TCollection>(TCollection values, ref Writer writer);

    public delegate void WriteSpan<T>(ReadOnlySpan<T> values, ref Writer writer);

    /// <summary>Writes a length (or count) and the run through <paramref name="write"/>; returns the bytes.</summary>
    public static int Write<TCollection>(NinoArrayBufferWriter buffer, int length, TCollection values,
        WriteRun<TCollection> write)
    {
        buffer.ResetWrittenCount();
        var writer = new Writer(buffer);
        writer.WriteLength(length);
        write(values, ref writer);
        writer.Flush();
        return buffer.WrittenCount;
    }

    /// <summary>Throws unless every shape wrote what NinoSerializer writes for the collection.</summary>
    public static void Check(string name, byte[] expected, NinoArrayBufferWriter buffer, Func<int>[] shapes)
    {
        foreach (var shape in shapes)
        {
            shape();
            if (!buffer.WrittenSpan.SequenceEqual(expected))
                throw new InvalidOperationException($"StepProbe {name}: a shape wrote other bytes than NinoSerializer.");
        }
    }
}

/// <summary>
/// Experiment (#355): 1,000 elements of a polymorphic base in an array or a list, LuminPack's 20-subtype IFoo
/// (PolymorphicFooArray's values) and a 3-subtype hierarchy, written by each shape above.
/// </summary>
[NinoJob]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
[HideColumns("StdDev", "RatioSD", "Error")]
[MinColumn, MaxColumn]
public class StepProbe
{
    private const int Length = 1000;

    [Params(false, true)]
    public bool List;

    private readonly NinoArrayBufferWriter _buffer = new(1024 * 1024);
    private PolymorphicFooBase[] _foo;
    private StepProbeBase[] _small;

    private ReadOnlySpan<PolymorphicFooBase> Foos => List ? CollectionsMarshal.AsSpan(_fooList) : _foo;
    private ReadOnlySpan<StepProbeBase> Smalls => List ? CollectionsMarshal.AsSpan(_smallList) : _small;
    private List<PolymorphicFooBase> _fooList;
    private List<StepProbeBase> _smallList;

    [GlobalSetup]
    public void Setup()
    {
        _foo = new PolymorphicFooBase[Length];
        _small = new StepProbeBase[Length];
        for (int i = 0; i < Length; i++)
        {
            _foo[i] = StepProbeData.Foo(i);
            _small[i] = StepProbeData.Small(i);
        }

        _fooList = new List<PolymorphicFooBase>(_foo);
        _smallList = new List<StepProbeBase>(_small);
        StepProbeData.Check("Foo20", NinoSerializer.Serialize(_foo), _buffer,
            new Func<int>[] { NinoStepFoo20Loop, NinoStepFoo20Step, NinoStepFoo20StepInline, NinoStepFoo20PerValue });
        StepProbeData.Check("Small3", NinoSerializer.Serialize(_small), _buffer,
            new Func<int>[] { NinoStepSmall3Loop, NinoStepSmall3Step, NinoStepSmall3StepInline, NinoStepSmall3PerValue });
    }

    private int Foo(StepProbeData.WriteSpan<PolymorphicFooBase> write) => Write(Foos, write);

    private int Small(StepProbeData.WriteSpan<StepProbeBase> write) => Write(Smalls, write);

    /// <summary>The length and the run (one delegate call per run, the same for every shape).</summary>
    private int Write<T>(ReadOnlySpan<T> values, StepProbeData.WriteSpan<T> write)
    {
        _buffer.ResetWrittenCount();
        var writer = new Writer(_buffer);
        writer.WriteLength(values.Length);
        write(values, ref writer);
        writer.Flush();
        return _buffer.WrittenCount;
    }

    [Benchmark(Baseline = true), BenchmarkCategory("Foo20")]
    public int NinoStepFoo20Loop() => Foo(StepShapes<PolymorphicFooBase, FooFormatter, FooFormatter.__Step, FooFormatter.__StepInline>.Loop);

    [Benchmark, BenchmarkCategory("Foo20")]
    public int NinoStepFoo20Step() => Foo(StepShapes<PolymorphicFooBase, FooFormatter, FooFormatter.__Step, FooFormatter.__StepInline>.Step);

    [Benchmark, BenchmarkCategory("Foo20")]
    public int NinoStepFoo20StepInline() => Foo(StepShapes<PolymorphicFooBase, FooFormatter, FooFormatter.__Step, FooFormatter.__StepInline>.StepInline);

    [Benchmark, BenchmarkCategory("Foo20")]
    public int NinoStepFoo20PerValue() => Foo(StepShapes<PolymorphicFooBase, FooFormatter, FooFormatter.__Step, FooFormatter.__StepInline>.PerValue);

    [Benchmark(Baseline = true), BenchmarkCategory("Small3")]
    public int NinoStepSmall3Loop() => Small(StepShapes<StepProbeBase, SmallFormatter, SmallFormatter.__Step, SmallFormatter.__StepInline>.Loop);

    [Benchmark, BenchmarkCategory("Small3")]
    public int NinoStepSmall3Step() => Small(StepShapes<StepProbeBase, SmallFormatter, SmallFormatter.__Step, SmallFormatter.__StepInline>.Step);

    [Benchmark, BenchmarkCategory("Small3")]
    public int NinoStepSmall3StepInline() => Small(StepShapes<StepProbeBase, SmallFormatter, SmallFormatter.__Step, SmallFormatter.__StepInline>.StepInline);

    [Benchmark, BenchmarkCategory("Small3")]
    public int NinoStepSmall3PerValue() => Small(StepShapes<StepProbeBase, SmallFormatter, SmallFormatter.__Step, SmallFormatter.__StepInline>.PerValue);
}

/// <summary>
/// Experiment (#355): a <c>Dictionary&lt;int, Base&gt;</c> of 12, 32 and 100 pairs (values in turn over the
/// subtypes), written by today's generated pair loop (WritePairs32), the kernel over the entries with the key by its
/// formatter and the value by the base's step (NinoKernel.WritePairs), the same through a composed pair step
/// (NinoKernel.WriteEntries, NinoPairStep), and per pair through the formatter.
/// </summary>
[NinoJob]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
[HideColumns("StdDev", "RatioSD", "Error")]
[MinColumn, MaxColumn]
public class StepDictProbe
{
    [Params(12, 32, 100)]
    public int Pairs = 12;

    private readonly NinoArrayBufferWriter _buffer = new(1024 * 1024);
    private Dictionary<int, PolymorphicFooBase> _foo;
    private Dictionary<int, StepProbeBase> _small;

    [GlobalSetup]
    public void Setup()
    {
        _foo = new Dictionary<int, PolymorphicFooBase>();
        _small = new Dictionary<int, StepProbeBase>();
        for (int i = 0; i < Pairs; i++)
        {
            _foo.Add(i, StepProbeData.Foo(i));
            _small.Add(i, StepProbeData.Small(i));
        }

        StepProbeData.Check("Foo20 pairs", NinoSerializer.Serialize(_foo), _buffer,
            new Func<int>[] { NinoStepFoo20PairLoop, NinoStepFoo20PairStep, NinoStepFoo20PairStepInline, NinoStepFoo20PairComposed, NinoStepFoo20PairPerValue });
        StepProbeData.Check("Small3 pairs", NinoSerializer.Serialize(_small), _buffer,
            new Func<int>[] { NinoStepSmall3PairLoop, NinoStepSmall3PairStep, NinoStepSmall3PairStepInline, NinoStepSmall3PairComposed, NinoStepSmall3PairPerValue });
    }

    private int Foo(StepProbeData.WriteRun<Dictionary<int, PolymorphicFooBase>> write) =>
        StepProbeData.Write(_buffer, _foo.Count, _foo, write);

    private int Small(StepProbeData.WriteRun<Dictionary<int, StepProbeBase>> write) =>
        StepProbeData.Write(_buffer, _small.Count, _small, write);

    [Benchmark(Baseline = true), BenchmarkCategory("Foo20")]
    public int NinoStepFoo20PairLoop() => Foo(StepShapes<PolymorphicFooBase, FooFormatter, FooFormatter.__Step, FooFormatter.__StepInline>.PairLoop);

    [Benchmark, BenchmarkCategory("Foo20")]
    public int NinoStepFoo20PairStep() => Foo(StepShapes<PolymorphicFooBase, FooFormatter, FooFormatter.__Step, FooFormatter.__StepInline>.PairStep);

    [Benchmark, BenchmarkCategory("Foo20")]
    public int NinoStepFoo20PairStepInline() => Foo(StepShapes<PolymorphicFooBase, FooFormatter, FooFormatter.__Step, FooFormatter.__StepInline>.PairStepInline);

    [Benchmark, BenchmarkCategory("Foo20")]
    public int NinoStepFoo20PairComposed() => Foo(StepShapes<PolymorphicFooBase, FooFormatter, FooFormatter.__Step, FooFormatter.__StepInline>.PairComposed);

    [Benchmark, BenchmarkCategory("Foo20")]
    public int NinoStepFoo20PairPerValue() => Foo(StepShapes<PolymorphicFooBase, FooFormatter, FooFormatter.__Step, FooFormatter.__StepInline>.PairPerValue);

    [Benchmark(Baseline = true), BenchmarkCategory("Small3")]
    public int NinoStepSmall3PairLoop() => Small(StepShapes<StepProbeBase, SmallFormatter, SmallFormatter.__Step, SmallFormatter.__StepInline>.PairLoop);

    [Benchmark, BenchmarkCategory("Small3")]
    public int NinoStepSmall3PairStep() => Small(StepShapes<StepProbeBase, SmallFormatter, SmallFormatter.__Step, SmallFormatter.__StepInline>.PairStep);

    [Benchmark, BenchmarkCategory("Small3")]
    public int NinoStepSmall3PairStepInline() => Small(StepShapes<StepProbeBase, SmallFormatter, SmallFormatter.__Step, SmallFormatter.__StepInline>.PairStepInline);

    [Benchmark, BenchmarkCategory("Small3")]
    public int NinoStepSmall3PairComposed() => Small(StepShapes<StepProbeBase, SmallFormatter, SmallFormatter.__Step, SmallFormatter.__StepInline>.PairComposed);

    [Benchmark, BenchmarkCategory("Small3")]
    public int NinoStepSmall3PairPerValue() => Small(StepShapes<StepProbeBase, SmallFormatter, SmallFormatter.__Step, SmallFormatter.__StepInline>.PairPerValue);
}
#endif
