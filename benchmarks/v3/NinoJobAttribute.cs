using System;
using BenchmarkDotNet.Environments;
using BenchmarkDotNet.Jobs;
using Perfolizer.Horology;

namespace Nino.Benchmark;

/// <summary>
/// Same as src/Nino.Benchmark/NinoJobAttribute.cs.
/// The job of every benchmark class: <c>[ShortRunJob(RuntimeMoniker.Net10_0)]</c> (benchmarks/run.sh and report.yml
/// set the counts on the command line), or with NINO_BENCH_QUICK=1 a shorter job for development runs: 250 ms
/// iterations, 3 warm-up iterations and 6 to 10 measured ones, stopping once the error is within 2% of the mean.
/// Quick numbers are for trends and regressions, not for publishing.
/// </summary>
public class NinoJobAttribute() : JobConfigBaseAttribute(Create())
{
    private static Job Create()
    {
        if (Environment.GetEnvironmentVariable("NINO_BENCH_QUICK") == "1")
            return Job.Default
                .WithRuntime(CoreRuntime.Core10_0)
                .WithId("Quick")
                .WithLaunchCount(1)
                .WithIterationTime(TimeInterval.FromMilliseconds(250))
                .WithWarmupCount(3)
                .WithMinIterationCount(6)
                .WithMaxIterationCount(10)
                .WithMaxRelativeError(0.02);
        // The id ShortRunJobAttribute gives it, so reports read as before.
        return Job.ShortRun.WithRuntime(CoreRuntime.Core10_0).WithId("ShortRun-.NET 10.0");
    }
}
