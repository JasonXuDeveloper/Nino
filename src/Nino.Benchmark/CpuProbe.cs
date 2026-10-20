using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Environments;
using BenchmarkDotNet.Jobs;
using MemoryPack;
using Nino.Core;
using Perfolizer.Horology;

namespace Nino.Benchmark;

/// <summary>Experiment: the same cases with AVX-512 on and off (DOTNET_EnableAVX512F=0).</summary>
public class ProbeConfig : ManualConfig
{
    public ProbeConfig()
    {
        var job = Job.Default.WithRuntime(CoreRuntime.Core10_0).WithLaunchCount(1)
            .WithIterationTime(TimeInterval.FromMilliseconds(250)).WithWarmupCount(3)
            .WithMinIterationCount(6).WithMaxIterationCount(10).WithMaxRelativeError(0.02);
        AddJob(job.WithId("Avx512"));
        AddJob(job.WithEnvironmentVariables(new EnvironmentVariable("DOTNET_EnableAVX512F", "0")).WithId("NoAvx512"));
        AddJob(job.WithEnvironmentVariables(new EnvironmentVariable("DOTNET_PreferredVectorBitWidth", "256")).WithId("Prefer256"));
    }
}

[Config(typeof(ProbeConfig))]
public class SerializeProbe
{
    private static readonly List<(int, long)> Tuples = Enumerable.Range(0, 1000).Select(i => (i, (long)i * 31)).ToList();
    private static readonly KeyValuePair<int, int>[] Pairs =
        Enumerable.Range(0, 1000).Select(i => new KeyValuePair<int, int>(i, i * 7)).ToArray();
    private static readonly InventoryItem[] Items = Enumerable.Range(0, 1000)
        .Select(i => new InventoryItem { ItemId = i, Count = (short)(i % 100), Bound = i % 3 == 0 }).ToArray();

    private readonly NinoArrayBufferWriter _nino = new(1 << 16);
    private readonly ArrayBufferWriter<byte> _mp = new(1 << 16);

    [Benchmark] public int NinoTuples() { _nino.ResetWrittenCount(); NinoSerializer.Serialize(Tuples, _nino); return _nino.WrittenCount; }
    [Benchmark] public int MemoryPackTuples() { _mp.ResetWrittenCount(); MemoryPackSerializer.Serialize(_mp, Tuples); return _mp.WrittenCount; }
    [Benchmark] public int NinoPairs() { _nino.ResetWrittenCount(); NinoSerializer.Serialize(Pairs, _nino); return _nino.WrittenCount; }
    [Benchmark] public int MemoryPackPairs() { _mp.ResetWrittenCount(); MemoryPackSerializer.Serialize(_mp, Pairs); return _mp.WrittenCount; }
    [Benchmark] public int NinoItems() { _nino.ResetWrittenCount(); NinoSerializer.Serialize(Items, _nino); return _nino.WrittenCount; }
    [Benchmark] public int MemoryPackItems() { _mp.ResetWrittenCount(); MemoryPackSerializer.Serialize(_mp, Items); return _mp.WrittenCount; }
}

/// <summary>Experiment: NinoUnsafe.Copy against Memmove (Span.CopyTo), into a buffer at offset 4.</summary>
[Config(typeof(ProbeConfig))]
public class CopyProbe
{
    [Params(2048, 4000, 8000, 16000)]
    public int Bytes;

    private readonly byte[] _source = new byte[1 << 15];
    private readonly byte[] _destination = new byte[1 << 15];

    [Benchmark(Baseline = true)]
    public void Memmove() => _source.AsSpan(0, Bytes).CopyTo(_destination.AsSpan(4));

    [Benchmark]
    public void NinoCopy() => NinoUnsafe.Copy(ref _destination[4], ref MemoryMarshal.GetArrayDataReference(_source), (uint)Bytes);
}

/// <summary>Experiment: `--time [name...]` times SerializeProbe's methods outside BenchmarkDotNet (best of 15 rounds).</summary>
public static class ProbeTimer
{
    public static int Run(string[] args)
    {
        var probe = new SerializeProbe();
        var cases = new (string Name, Func<int> Run)[]
        {
            ("NinoTuples", probe.NinoTuples), ("MemoryPackTuples", probe.MemoryPackTuples),
            ("NinoPairs", probe.NinoPairs), ("MemoryPackPairs", probe.MemoryPackPairs),
            ("NinoItems", probe.NinoItems), ("MemoryPackItems", probe.MemoryPackItems),
        };
        var names = args.Where(a => !a.StartsWith("--")).ToArray();
        foreach (var (name, run) in cases)
        {
            if (names.Length > 0 && !names.Any(n => name.Contains(n)))
                continue;
            // Long enough for tiered compilation to reach Tier1 (call counting starts after 100 ms of quiet).
            var warm = System.Diagnostics.Stopwatch.StartNew();
            while (warm.ElapsedMilliseconds < 2000)
                run();
            double best = double.MaxValue;
            for (int round = 0; round < 15; round++)
            {
                const int n = 20_000;
                var watch = System.Diagnostics.Stopwatch.StartNew();
                for (int i = 0; i < n; i++)
                    run();
                best = Math.Min(best, watch.Elapsed.TotalNanoseconds / n);
            }
            Console.WriteLine($"{name,-18} {best,7:F1} ns");
        }
        return 0;
    }
}
