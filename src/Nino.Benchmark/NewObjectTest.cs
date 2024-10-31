using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;

namespace Nino.Benchmark;

[MinColumn, MaxColumn]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[SimpleJob(RuntimeMoniker.Net80, iterationCount: 100)]
public class NewObjectTest
{
    public class TestClass
    {
        public int A;
        public SimpleStruct B;
        public SimpleClass C { get; set; }
        public SimpleClass[] D;
        public SimpleStruct[] E { get; set; }
    }

    private void Get<T>(out T ret) where T : new()
    {
        ret = new T();
    }

    private void Get<T>(out T[] ret) where T : new()
    {
        ret = new T[100];
    }

    [Benchmark(Baseline = true)]
    public TestClass InitializerNew()
    {
        Get(out int a);
        Get(out SimpleStruct b);
        Get(out SimpleClass c);
        Get(out SimpleClass[] d);
        Get(out SimpleStruct[] e);
        return new TestClass
        {
            A = a,
            B = b,
            C = c,
            D = d,
            E = e
        };
    }

    [Benchmark]
    public TestClass MixedNew()
    {
        TestClass test = new TestClass();
        Get(out test.A);
        Get(out test.B);
        Get(out SimpleClass c);
        test.C = c;
        Get(out test.D);
        Get(out SimpleStruct[] e);
        test.E = e;
        return test;
    }

    [Benchmark]
    public TestClass AssignNew()
    {
        Get(out int a);
        Get(out SimpleStruct b);
        Get(out SimpleClass c);
        Get(out SimpleClass[] d);
        Get(out SimpleStruct[] e);
        TestClass test = new TestClass();
        test.A = a;
        test.B = b;
        test.C = c;
        test.D = d;
        test.E = e;
        return test;
    }
}