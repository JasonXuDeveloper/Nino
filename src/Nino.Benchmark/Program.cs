using BenchmarkDotNet.Running;

namespace Nino.Benchmark
{
    public class Program
    {
        public static void Main(string[] args)
        {
#if !DEBUG
            //BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
            // BenchmarkRunner.Run<ExtensibleBufferBenchmark>();
            BenchmarkRunner.Run<SerializationBenchmark>();
            // BenchmarkRunner.Run<StringMgrBenchmark>();
#else
            BenchmarkRunner.Run<SerializationBenchmark>(new BenchmarkDotNet.Configs.DebugInProcessConfig());
#endif
        }
    }
}