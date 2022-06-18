using System;
using Nino.Benchmark;
using BenchmarkDotNet.Running;

namespace Nino
{
    public class Program
    {
        public static void Main(string[] args)
        {
#if !DEBUG
            //BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
            BenchmarkRunner.Run<SerializationBenchmark>();
#else
            BenchmarkRunner.Run<SerializationBenchmark>(new BenchmarkDotNet.Configs.DebugInProcessConfig());
#endif
        }
    }
}