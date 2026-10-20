using System.Linq;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Reports;
using BenchmarkDotNet.Running;
using Perfolizer.Horology;
using Perfolizer.Metrology;

namespace Nino.Benchmark
{
    public class Program
    {
        // One joined summary, and --filter is honored (RunAllJoined ignores it). Times always in ns and sizes in B,
        // as the published report has always shown them: report.yml merges the reports of classes that ran in
        // parallel (benchmarks/merge_reports.py), whose cells then compare as they are.
        // `--verify` checks the cases (Verify.cs) and does not start BenchmarkDotNet.
        public static int Main(string[] args)
        {
            if (args.Contains("--verify"))
                return Verify.Run();
            if (args.Contains("--time"))
                return ProbeTimer.Run(args);

            BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly)
                .Run(args, DefaultConfig.Instance.WithOptions(ConfigOptions.JoinSummary)
                    .WithSummaryStyle(SummaryStyle.Default.WithTimeUnit(TimeUnit.Nanosecond)
                        .WithSizeUnit(SizeUnit.B)));
            return 0;
        }
    }
}
