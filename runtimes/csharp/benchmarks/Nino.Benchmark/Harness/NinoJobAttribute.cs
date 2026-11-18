using System;
using System.Reflection;
using BenchmarkDotNet.Environments;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Toolchains;
using BenchmarkDotNet.Toolchains.CsProj;
using BenchmarkDotNet.Toolchains.DotNetCli;
using Perfolizer.Horology;

namespace Nino.Benchmark;

/// <summary>
/// The job of every benchmark class: the settings of the published numbers, 3 launches of 5 warm-up and 15 measured
/// iterations on .NET 11, under the id <c>[ShortRunJob]</c> gives for it (BenchmarkDotNet 0.15.8 has no .NET 11 moniker,
/// so the runtime is made by hand, <see cref="Net11"/>; the reports and the docs site read the id); or with NINO_BENCH_QUICK=1 a shorter job for development runs: 250 ms iterations, 3 warm-up
/// iterations and 6 to 10 measured ones, stopping once the error is within 2% of the mean. Quick numbers are for
/// trends and regressions, not for publishing.
/// </summary>
public class NinoJobAttribute() : JobConfigBaseAttribute(Create())
{
    /// <summary>
    /// .NET 11 under the moniker of .NET 10, built for net11.0 by <see cref="Net11Toolchain"/>.
    /// <see cref="CoreRuntime.CreateForNewVersion"/> gives the moniker <c>NotRecognized</c>, and 0.15.8 asks every
    /// runtime's moniker for its version (the SDK check, the runner's build partitions), which throws
    /// <see cref="NotImplementedException"/> before any benchmark runs. The moniker only decides those checks (an SDK of
    /// 10 or later) and the toolchain it would pick (net10.0, which the project no longer builds); the reports say
    /// .NET 11.0. Drop both for <c>CoreRuntime.Core11_0</c> with a BenchmarkDotNet that has it.
    /// </summary>
    private static readonly CoreRuntime Net11 = (CoreRuntime)Activator.CreateInstance(typeof(CoreRuntime),
        BindingFlags.Instance | BindingFlags.NonPublic, null, [RuntimeMoniker.Net10_0, "net11.0", ".NET 11.0"], null)!;

    private static readonly IToolchain Net11Toolchain = CsProjCoreToolchain.From(new NetCoreAppSettings("net11.0", null, ".NET 11.0"));

    /// <summary><paramref name="job"/> on .NET 11: every job of this project (the probes' configs too) goes through it.</summary>
    public static Job OnNet11(Job job) => job.WithRuntime(Net11).WithToolchain(Net11Toolchain);

    private static Job Create()
    {
        if (Environment.GetEnvironmentVariable("NINO_BENCH_QUICK") == "1")
            return OnNet11(Job.Default)
                .WithId("Quick")
                .WithLaunchCount(1)
                .WithIterationTime(TimeInterval.FromMilliseconds(250))
                .WithWarmupCount(3)
                .WithMinIterationCount(6)
                .WithMaxIterationCount(10)
                .WithMaxRelativeError(0.02);
        // The id ShortRunJobAttribute gives it, so reports read as before. report.yml still passes these counts on
        // the command line for release tags whose benchmarks predate them here.
        return OnNet11(Job.ShortRun)
            .WithId("ShortRun-.NET 11.0")
            .WithLaunchCount(3)
            .WithWarmupCount(5)
            .WithIterationCount(15);
    }
}
