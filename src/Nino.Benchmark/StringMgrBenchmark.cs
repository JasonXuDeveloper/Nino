
using System;
using BenchmarkDotNet.Attributes;
using Nino.Shared.Mgr;

namespace Nino.Benchmark
{
    [Config(typeof(StrMgrBenchmarkConfig))]
    public class StringMgrBenchmark
    {
        private const string Str = "asd|12|3ed|sdf|dcvge|er34|3454gv|dwcrf|3435tx|edfw|2zr|3e2r|dqw|";

        [Benchmark(Baseline = true)]
        public void SystemSplit() => Str.Split('|');
        
        [Benchmark]
        public void StringMgrSplit() => Str.AsSpan().Split('|');
    }
}