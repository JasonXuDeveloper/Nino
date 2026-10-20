using System.Collections.Generic;
using System.Linq;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Environments;
using BenchmarkDotNet.Jobs;
using Perfolizer.Horology;
using MemoryPack;
using Nino.Core;

namespace Nino.Benchmark;

/// <summary>Experiment: Nino.Core's NINO_X variants (NinoExperiment) on one machine, MemoryPack as the anchor.</summary>
public class VariantConfig : ManualConfig
{
    public VariantConfig()
    {
        var job = Job.Default.WithRuntime(CoreRuntime.Core10_0).WithLaunchCount(1)
            .WithIterationTime(TimeInterval.FromMilliseconds(250)).WithWarmupCount(3)
            .WithMinIterationCount(6).WithMaxIterationCount(10).WithMaxRelativeError(0.02);
        foreach (var x in new[] { "0", "1", "2", "3", "4", "7" })
            AddJob(job.WithEnvironmentVariables(new EnvironmentVariable("NINO_X", x)).WithId("X" + x));
    }
}

[Config(typeof(VariantConfig))]
public class VariantProbe
{
    private static readonly List<(int, long)> Tuples =
        Enumerable.Range(0, 1000).Select(i => (i, (long)i * 31)).ToList();
    private static readonly byte[] NinoBytes = NinoSerializer.Serialize(Tuples);
    private static readonly byte[] MemoryPackBytes = MemoryPackSerializer.Serialize(Tuples);
    private readonly NinoArrayBufferWriter _nino = new(1 << 16);
    private readonly System.Buffers.ArrayBufferWriter<byte> _memoryPack = new(1 << 16);

    [Benchmark]
    public int NinoWrite()
    {
        _nino.ResetWrittenCount();
        NinoSerializer.Serialize(Tuples, _nino);
        return _nino.WrittenCount;
    }

    [Benchmark]
    public int MemoryPackWrite()
    {
        _memoryPack.ResetWrittenCount();
        MemoryPackSerializer.Serialize(_memoryPack, Tuples);
        return _memoryPack.WrittenCount;
    }

    [Benchmark] public int NinoRead() => NinoDeserializer.Deserialize<List<(int, long)>>(NinoBytes).Count;
    [Benchmark] public int MemoryPackRead() => MemoryPackSerializer.Deserialize<List<(int, long)>>(MemoryPackBytes)!.Count;
}
