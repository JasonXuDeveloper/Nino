using System;
using System.Text;
using BenchmarkDotNet.Attributes;
using Nino.Shared.Mgr;

namespace Nino.Benchmark
{
    [Config(typeof(StrMgrBenchmarkConfig))]
    public class StringMgrBenchmark
    {
        private string Str
        {
            get
            {
                var sb = new StringBuilder();
                for (int i = 0; i < 10000; i++)
                {
                    sb.Append(i).Append('|');
                }

                return sb.ToString();
            }
        }
        
        [Benchmark(Baseline = true)]
        public void SystemSplit() => Str.Split('|');
        
        [Benchmark]
        public void StringMgrSplit() => Str.AsSpan().Split('|');
    }
}