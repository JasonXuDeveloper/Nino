using System;
using System.Linq;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Running;

namespace Nino.Benchmark
{
    public class Program
    {
        // Same as v4's Program.cs, plus a `--verify` quick-check mode that does not start BenchmarkDotNet.
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
