// The classes of the Unity benchmark (BurstBenchmark): the shape of the .NET benchmark's SimpleClass
// (benchmarks/Nino.Benchmark/SimpleBenchmarkData.cs), so the IL2CPP numbers compare with the .NET ones.
#if NINO_BURST && NINO_UNITY_BENCH
using System;
using System.Collections.Generic;
using System.Linq;
using Nino.Core;

namespace Test.Runtime.Burst
{
    [NinoType]
    public abstract partial class BenchClassBase
    {
        public int Id;
        public bool Tag;
        public Guid Guid;
        public DateTime CreateTime;
    }

    [NinoType]
    public sealed partial class BenchClass : BenchClassBase
    {
        public string Name { get; set; }
        [NinoUtf8] public string Desc;
        public int[] Numbers { get; set; }
        public List<DateTime> Dates { get; set; }
        public Dictionary<int, string> Map1;
        public Dictionary<int, int> Map2 { get; set; }

        private static readonly DateTime Now = new DateTime(2025, 1, 1, 12, 0, 0, DateTimeKind.Utc);

        public static BenchClass Create(Random random)
        {
            Guid NewGuid()
            {
                var bytes = new byte[16];
                random.NextBytes(bytes);
                return new Guid(bytes);
            }

            return new BenchClass
            {
                Id = random.Next(),
                Tag = random.Next() % 2 == 0,
                Guid = NewGuid(),
                CreateTime = Now,
                Name = NewGuid().ToString(),
                Desc = string.Concat(Enumerable.Range(0, 10).Select(_ => NewGuid().ToString())),
                Numbers = Enumerable.Range(0, 100).Select(_ => random.Next()).ToArray(),
                Dates = Enumerable.Range(0, 10).Select(_ => Now.AddSeconds(random.Next())).ToList(),
                Map1 = Enumerable.Range(0, 10).ToDictionary(n => n, n => n.ToString()),
                Map2 = Enumerable.Range(0, 10).ToDictionary(n => n, n => n * 2),
            };
        }
    }

    /// <summary>
    /// A generic [NinoType] and its non-generic twin (BurstBenchmark's two "4 longs" cases): a member of type T calls the
    /// formatter the closed type names, where it made a virtual call through CachedFormatter&lt;T&gt; per value under
    /// IL2CPP before the generated formatters took their type arguments' formatters; the two should now take the same
    /// time.
    /// </summary>
    [NinoType]
    public sealed partial class BenchBox<T>
    {
        public T A;
        public T B;
        public T C;
        public T D;
    }

    /// <summary>The non-generic twin of <see cref="BenchBox{T}"/> over <c>long</c>.</summary>
    [NinoType]
    public sealed partial class BenchLongs
    {
        public long A;
        public long B;
        public long C;
        public long D;
    }
}
#endif
