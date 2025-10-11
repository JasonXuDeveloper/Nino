using BenchmarkDotNet.Running;

namespace Nino.Benchmark
{
    public class Program
    {
        public static void Main(string[] args)
        {
            BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).RunAllJoined(args: args);
        }
    }
}