using System;
using System.Runtime.CompilerServices;
using BenchmarkDotNet.Attributes;
using Nino.Core;

namespace Nino.Benchmark;

/// <summary>Experiment: a new int[] filled by one copy, allocated zeroed or uninitialized, by size.</summary>
[NinoJob]
public class AllocProbe
{
    [Params(512, 1000, 4096, 16384, 65536)]
    public int Ints;

    private int[] _source;
    private byte[] _nino;

    [GlobalSetup]
    public void Setup()
    {
        _source = new int[Ints];
        for (int i = 0; i < Ints; i++) _source[i] = i;
        _nino = NinoSerializer.Serialize(_source);
    }

    [Benchmark(Baseline = true)]
    public int[] Zeroed()
    {
        var a = new int[Ints];
        _source.AsSpan().CopyTo(a);
        return a;
    }

    [Benchmark]
    public int[] Uninitialized()
    {
        var a = GC.AllocateUninitializedArray<int>(Ints);
        _source.AsSpan().CopyTo(a);
        return a;
    }

    [Benchmark]
    public int[] NinoArray() => NinoDeserializer.Deserialize<int[]>(_nino);

    [Benchmark]
    public object NinoMemory() => NinoDeserializer.Deserialize<Memory<int>>(_nino);
}
