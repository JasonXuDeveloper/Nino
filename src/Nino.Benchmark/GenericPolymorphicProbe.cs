using System;
using System.Linq;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using Nino.Core;

#nullable disable
namespace Nino.Benchmark;

/// <summary>
/// Arrays of 1,000 subtypes in random order (eight types), through a non-generic base, which the generated dispatch
/// covers, and through a closed generic base (<c>ProbeBox&lt;int&gt;</c>), which goes through the registry. The suite's job;
/// a probe, so only a filter naming it runs it.
/// </summary>
[HideColumns("StdDev", "RatioSD", "Error")]
[MinColumn, MaxColumn]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[NinoJob]
[GcServer(true)]
[MemoryDiagnoser]
[MarkdownExporterAttribute.GitHub]
public class GenericPolymorphicProbe
{
    private const int N = 1000;

    private static readonly ProbeBase[] Plain = Make(i => i switch
    {
        0 => new ProbeA { V = i }, 1 => new ProbeB { V = i }, 2 => new ProbeC { V = i }, 3 => new ProbeD { V = i },
        4 => new ProbeE { V = i }, 5 => new ProbeF { V = i }, 6 => new ProbeG { V = i }, _ => (ProbeBase)new ProbeH { V = i }
    });

    private static readonly ProbeBox<int>[] Generic = Make(i => i switch
    {
        0 => new ProbeBoxA { V = i }, 1 => new ProbeBoxB { V = i }, 2 => new ProbeBoxC { V = i }, 3 => new ProbeBoxD { V = i },
        4 => new ProbeBoxE { V = i }, 5 => new ProbeBoxF { V = i }, 6 => new ProbeBoxG { V = i }, _ => (ProbeBox<int>)new ProbeBoxH { V = i }
    });

    private static readonly byte[] PlainBytes = NinoSerializer.Serialize(Plain);
    private static readonly byte[] GenericBytes = NinoSerializer.Serialize(Generic);

    private readonly NinoArrayBufferWriter _buffer = new(1 << 16);

    private static T[] Make<T>(Func<int, T> create)
    {
        var random = new Random(42);
        return Enumerable.Range(0, N).Select(_ => create(random.Next(8))).ToArray();
    }

    [Benchmark(Baseline = true), BenchmarkCategory("Serialize")]
    public int NinoSerializeNonGenericBase()
    {
        _buffer.ResetWrittenCount();
        NinoSerializer.Serialize(Plain, _buffer);
        return _buffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("Serialize")]
    public int NinoSerializeGenericBase()
    {
        _buffer.ResetWrittenCount();
        NinoSerializer.Serialize(Generic, _buffer);
        return _buffer.WrittenCount;
    }

    [Benchmark(Baseline = true), BenchmarkCategory("Deserialize")]
    public ProbeBase[] NinoDeserializeNonGenericBase() => NinoDeserializer.Deserialize<ProbeBase[]>(PlainBytes);

    [Benchmark, BenchmarkCategory("Deserialize")]
    public ProbeBox<int>[] NinoDeserializeGenericBase() => NinoDeserializer.Deserialize<ProbeBox<int>[]>(GenericBytes);
}

[NinoType] public abstract class ProbeBase { public int V; }
[NinoType] public sealed class ProbeA : ProbeBase { }
[NinoType] public sealed class ProbeB : ProbeBase { }
[NinoType] public sealed class ProbeC : ProbeBase { }
[NinoType] public sealed class ProbeD : ProbeBase { }
[NinoType] public sealed class ProbeE : ProbeBase { }
[NinoType] public sealed class ProbeF : ProbeBase { }
[NinoType] public sealed class ProbeG : ProbeBase { }
[NinoType] public sealed class ProbeH : ProbeBase { }

[NinoType] public abstract class ProbeBox<T> { public T V; }
[NinoType] public sealed class ProbeBoxA : ProbeBox<int> { }
[NinoType] public sealed class ProbeBoxB : ProbeBox<int> { }
[NinoType] public sealed class ProbeBoxC : ProbeBox<int> { }
[NinoType] public sealed class ProbeBoxD : ProbeBox<int> { }
[NinoType] public sealed class ProbeBoxE : ProbeBox<int> { }
[NinoType] public sealed class ProbeBoxF : ProbeBox<int> { }
[NinoType] public sealed class ProbeBoxG : ProbeBox<int> { }
[NinoType] public sealed class ProbeBoxH : ProbeBox<int> { }
