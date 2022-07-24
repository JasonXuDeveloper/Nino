using System.Collections.Generic;
using BenchmarkDotNet.Attributes;
using Nino.Shared.IO;

namespace Nino.Benchmark
{
    [MarkdownExporter, AsciiDocExporter, HtmlExporter, CsvExporter, RPlotExporter]
    [Config(typeof(BenchmarkConfig2))]
    public class ExtensibleBufferBenchmark
    {
        [Benchmark]
        [Arguments(100)]
        [Arguments(1000)]
        [Arguments(10000)]
        [Arguments(100000)]
        [Arguments(1000000)]
        [Arguments(10000000)]
        [Arguments(100000000)]
        public void ByteExtensibleBufferInsertV1(int testCount)
        {
            ExtensibleBuffer<byte> buffer = new ExtensibleBuffer<byte>();
            for (int i = 0; i < testCount; i++)
            {
                buffer[i] = (byte)i;
            }
        }
        
        [Benchmark]
        [Arguments(100)]
        [Arguments(1000)]
        [Arguments(10000)]
        [Arguments(100000)]
        [Arguments(1000000)]
        [Arguments(10000000)]
        [Arguments(100000000)]
        public void ByteExtensibleBufferInsertV2(int testCount)
        {
            ExtensibleBuffer<byte> buffer =
                new ExtensibleBuffer<byte>(testCount);
            for (int i = 0; i < testCount; i++)
            {
                buffer[i] = (byte)i;
            }
        }

        [Benchmark]
        [Arguments(100)]
        [Arguments(1000)]
        [Arguments(10000)]
        [Arguments(100000)]
        [Arguments(1000000)]
        [Arguments(10000000)]
        [Arguments(100000000)]
        public void ByteListInsertV1(int testCount)
        {
            List<byte> buffer = new List<byte>();
            for (int i = 0; i < testCount; i++)
            {
                buffer.Add((byte)i);
            }
        }
        
        [Benchmark]
        [Arguments(100)]
        [Arguments(1000)]
        [Arguments(10000)]
        [Arguments(100000)]
        [Arguments(1000000)]
        [Arguments(10000000)]
        [Arguments(100000000)]
        public void ByteListInsertV2(int testCount)
        {
            List<byte> buffer = new List<byte>(testCount);
            for (int i = 0; i < testCount; i++)
            {
                buffer.Add((byte)i);
            }
        }
        
        [Benchmark]
        [Arguments(100)]
        [Arguments(1000)]
        [Arguments(10000)]
        [Arguments(100000)]
        [Arguments(1000000)]
        [Arguments(10000000)]
        [Arguments(100000000)]
        public void IntExtensibleBufferInsertV1(int testCount)
        {
            ExtensibleBuffer<int> buffer = new ExtensibleBuffer<int>();
            for (int i = 0; i < testCount; i++)
            {
                buffer[i] = i;
            }
        }
        
        [Benchmark]
        [Arguments(100)]
        [Arguments(1000)]
        [Arguments(10000)]
        [Arguments(100000)]
        [Arguments(1000000)]
        [Arguments(10000000)]
        [Arguments(100000000)]
        public void IntExtensibleBufferInsertV2(int testCount)
        {
            ExtensibleBuffer<int> buffer =
                new ExtensibleBuffer<int>(testCount);
            for (int i = 0; i < testCount; i++)
            {
                buffer[i] = i;
            }
        }
        
        [Benchmark]
        [Arguments(100)]
        [Arguments(1000)]
        [Arguments(10000)]
        [Arguments(100000)]
        [Arguments(1000000)]
        [Arguments(10000000)]
        [Arguments(100000000)]
        public void IntListInsertV1(int testCount)
        {
            List<int> buffer = new List<int>();
            for (int i = 0; i < testCount; i++)
            {
                buffer.Add(i);
            }
        }
        
        [Benchmark]
        [Arguments(100)]
        [Arguments(1000)]
        [Arguments(10000)]
        [Arguments(100000)]
        [Arguments(1000000)]
        [Arguments(10000000)]
        [Arguments(100000000)]
        public void IntListInsertV2(int testCount)
        {
            List<int> buffer = new List<int>(testCount);
            for (int i = 0; i < testCount; i++)
            {
                buffer.Add(i);
            }
        }
    }
}