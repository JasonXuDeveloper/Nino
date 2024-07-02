using BenchmarkDotNet.Running;

namespace Nino.Benchmark
{
    public class Program
    {
        public static void Main(string[] args)
        {
#if !DEBUG
            BenchmarkRunner.Run<SerializationBenchmark>();
#else
            BenchmarkRunner.Run<SerializationBenchmark>(new BenchmarkDotNet.Configs.DebugInProcessConfig());
#endif
        }
    }
}