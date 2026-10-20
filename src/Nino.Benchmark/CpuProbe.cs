using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Environments;
using BenchmarkDotNet.Jobs;
using MemoryPack;
using Nino.Core;
using Perfolizer.Horology;

namespace Nino.Benchmark;

/// <summary>
/// Experiment: the same cases with AVX-512 on, off (DOTNET_EnableAVX512=0: .NET 10 ignores DOTNET_EnableAVX512F) and
/// with 64-byte vectors not preferred (Avx512F still supported, Vector512 not accelerated).
/// </summary>
public class ProbeConfig : ManualConfig
{
    public ProbeConfig()
    {
        var job = Job.Default.WithRuntime(CoreRuntime.Core10_0).WithLaunchCount(1)
            .WithIterationTime(TimeInterval.FromMilliseconds(250)).WithWarmupCount(3)
            .WithMinIterationCount(6).WithMaxIterationCount(10).WithMaxRelativeError(0.02);
        AddJob(job.WithId("Avx512"));
        AddJob(job.WithEnvironmentVariables(new EnvironmentVariable("DOTNET_EnableAVX512", "0")).WithId("Avx2"));
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
    [Params(4000, 8000, 16000, 32000, 64000)]
    public int Bytes = 4000;

    private readonly byte[] _source = GC.AllocateArray<byte>(1 << 17, pinned: true);
    private readonly byte[] _destination = GC.AllocateArray<byte>(1 << 17, pinned: true);

    [Benchmark(Baseline = true)]
    public int Memmove() { _source.AsSpan(0, Bytes).CopyTo(_destination.AsSpan(4)); return Bytes; }

    [Benchmark]
    public int NinoCopy() { NinoUnsafe.Copy(ref _destination[4], ref MemoryMarshal.GetArrayDataReference(_source), (uint)Bytes); return Bytes; }

    /// <summary>The 32-byte vector loop of NinoUnsafe.Copy at every size (it calls Memmove above 4 KB).</summary>
    [Benchmark]
    public int Vector256Loop()
    {
        ref byte destination = ref _destination[4];
        ref byte source = ref MemoryMarshal.GetArrayDataReference(_source);
        nuint size = (nuint)Bytes;
        Vector256.LoadUnsafe(ref source).StoreUnsafe(ref destination);
        nuint i = 32 - ((nuint)(nint)Marshal.UnsafeAddrOfPinnedArrayElement(_destination, 4) & 31);
        nuint end = size - 32;
        for (; i + 96 <= end; i += 128)
        {
            var a = Vector256.LoadUnsafe(ref source, i);
            var b = Vector256.LoadUnsafe(ref source, i + 32);
            var c = Vector256.LoadUnsafe(ref source, i + 64);
            var d = Vector256.LoadUnsafe(ref source, i + 96);
            a.StoreUnsafe(ref destination, i);
            b.StoreUnsafe(ref destination, i + 32);
            c.StoreUnsafe(ref destination, i + 64);
            d.StoreUnsafe(ref destination, i + 96);
        }
        for (; i <= end; i += 32)
            Vector256.LoadUnsafe(ref source, i).StoreUnsafe(ref destination, i);
        Vector256.LoadUnsafe(ref source, end).StoreUnsafe(ref destination, end);
        return Bytes;
    }
}

/// <summary>Experiment: `--time [name...]` times SerializeProbe's methods outside BenchmarkDotNet (best of 15 rounds).</summary>
public static class ProbeTimer
{
    public static int Run(string[] args)
    {
        if (args.Contains("--check"))
            TupleProbe.Verify();
        var probe = new SerializeProbe();
        var tuples = new TupleProbe();
        var cases = new (string Name, Func<int> Run)[]
        {
            ("NinoTuples", probe.NinoTuples), ("MemoryPackTuples", probe.MemoryPackTuples),
            ("NinoPairs", probe.NinoPairs), ("MemoryPackPairs", probe.MemoryPackPairs),
            ("NinoItems", probe.NinoItems), ("MemoryPackItems", probe.MemoryPackItems),
            ("Tuple.NinoWrite", tuples.NinoWrite), ("Tuple.MemoryPackWrite", tuples.MemoryPackWrite),
            ("Tuple.NinoRead", tuples.NinoRead), ("Tuple.MemoryPackRead", tuples.MemoryPackRead),
            ("Tuple.Memcpy", tuples.Memcpy), ("Tuple.WriteScalar", tuples.WriteScalar),
            ("Tuple.WritePermute", tuples.WritePermute), ("Tuple.WritePermuteFull", tuples.WritePermuteFull),
            ("Tuple.WriteLanes", tuples.WriteLanes), ("Tuple.WriteZip", tuples.WriteZip),
            ("Tuple.ReadScalar", tuples.ReadScalar), ("Tuple.ReadPermute", tuples.ReadPermute),
            ("Tuple.ReadZip", tuples.ReadZip), ("Tuple.WriteTbl", tuples.WriteTbl), ("Tuple.ReadTbl", tuples.ReadTbl),
            ("Tuple.AllocList", tuples.AllocList), ("Tuple.ReadNewZeroed", tuples.ReadNewZeroed),
            ("Tuple.ReadNewUninitialized", tuples.ReadNewUninitialized), ("Tuple.AllocUninitialized", tuples.AllocUninitialized),
        };
        var names = args.Where(a => !a.StartsWith("--")).ToArray();
        foreach (var (name, run) in cases)
        {
            if (names.Length > 0 && !names.Any(n => name.Contains(n)) || run() < 0)
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
            Console.WriteLine($"{name,-24} {best,7:F1} ns");
        }
        return 0;
    }
}
