using System;
using System.Linq;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Running;

namespace Nino.Benchmark
{
    public class Program
    {
        // v4's Program.cs without its probes and fixed units: `--verify` checks the cases (Verify.cs) and does not
        // start BenchmarkDotNet; anything else runs BenchmarkDotNet, with one joined summary.
        public static int Main(string[] args)
        {
            if (args.Contains("--verify"))
                return Verify.Run();

            BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly)
                .Run(args, DefaultConfig.Instance.WithOptions(ConfigOptions.JoinSummary));
            return 0;
        }
    }
}
