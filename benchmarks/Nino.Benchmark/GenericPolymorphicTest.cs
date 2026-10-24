// Generic polymorphic bases need a generator with the fix for #203 (rc.7): left out of A/B runs against older releases.
#if !NINO_BENCH_LEGACY
using System;
using System.Linq;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using MemoryPack;
using MemoryPack.Formatters;
using Nino.Core;

#nullable disable
namespace Nino.Benchmark;

/// <summary>
/// Arrays of 1,000 subtypes in random order (eight types each), through a non-generic base and through the closed
/// instances of a generic base (<c>ProbeBox&lt;T&gt;</c>) with a primitive, an unmanaged tuple, a managed tuple and
/// tuples holding a sealed and a non-sealed class as the type argument, against MemoryPack: a union attribute on the
/// non-generic base, and a <c>DynamicUnionFormatter</c> registered for each closed generic one, as MemoryPack's union
/// attribute cannot be put on a generic type (MEMPACK013, MEMPACK014).
/// </summary>
[PayloadColumn]
[HideColumns("StdDev", "RatioSD", "Error")]
[MinColumn, MaxColumn]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[NinoJob]
[GcServer(true)]
[MemoryDiagnoser]
[MarkdownExporterAttribute.GitHub]
public class GenericPolymorphicTest
{
    private const int N = 1000;
    // First, so that the data below is serialized with them.
    private static readonly bool Unions = RegisterUnions();
    private static readonly string[] Names = Enumerable.Range(0, 8).Select(static i => "name" + i).ToArray();
    private static readonly ProbeSealedItem[] SealedItems =
        Enumerable.Range(0, 8).Select(static i => new ProbeSealedItem { X = i, Y = i * 2L }).ToArray();
    private static readonly ProbeOpenItem[] OpenItems =
        Enumerable.Range(0, 8).Select(static i => new ProbeOpenItem { X = i, Y = i * 2L }).ToArray();

    private static readonly ProbeBase[] NonGenericBase = Make<ProbeBase>(i => i switch { 0 => new ProbeA { V = i }, 1 => new ProbeB { V = i }, 2 => new ProbeC { V = i }, 3 => new ProbeD { V = i }, 4 => new ProbeE { V = i }, 5 => new ProbeF { V = i }, 6 => new ProbeG { V = i }, _ => new ProbeH { V = i } });
    private static readonly ProbeBox<int>[] GenericIntBase = Make<ProbeBox<int>>(i => i switch { 0 => new ProbeBoxA { V = i }, 1 => new ProbeBoxB { V = i }, 2 => new ProbeBoxC { V = i }, 3 => new ProbeBoxD { V = i }, 4 => new ProbeBoxE { V = i }, 5 => new ProbeBoxF { V = i }, 6 => new ProbeBoxG { V = i }, _ => new ProbeBoxH { V = i } });
    private static readonly ProbeBox<(int, long)>[] GenericUnmanagedTupleBase = Make<ProbeBox<(int, long)>>(i => i switch { 0 => new ProbeTupA { V = (i, i * 3L) }, 1 => new ProbeTupB { V = (i, i * 3L) }, 2 => new ProbeTupC { V = (i, i * 3L) }, 3 => new ProbeTupD { V = (i, i * 3L) }, 4 => new ProbeTupE { V = (i, i * 3L) }, 5 => new ProbeTupF { V = (i, i * 3L) }, 6 => new ProbeTupG { V = (i, i * 3L) }, _ => new ProbeTupH { V = (i, i * 3L) } });
    private static readonly ProbeBox<(int, string)>[] GenericManagedTupleBase = Make<ProbeBox<(int, string)>>(i => i switch { 0 => new ProbeStrA { V = (i, Names[i]) }, 1 => new ProbeStrB { V = (i, Names[i]) }, 2 => new ProbeStrC { V = (i, Names[i]) }, 3 => new ProbeStrD { V = (i, Names[i]) }, 4 => new ProbeStrE { V = (i, Names[i]) }, 5 => new ProbeStrF { V = (i, Names[i]) }, 6 => new ProbeStrG { V = (i, Names[i]) }, _ => new ProbeStrH { V = (i, Names[i]) } });
    private static readonly ProbeBox<(ProbeSealedItem, int)>[] GenericSealedClassTupleBase = Make<ProbeBox<(ProbeSealedItem, int)>>(i => i switch { 0 => new ProbeScA { V = (SealedItems[i], i) }, 1 => new ProbeScB { V = (SealedItems[i], i) }, 2 => new ProbeScC { V = (SealedItems[i], i) }, 3 => new ProbeScD { V = (SealedItems[i], i) }, 4 => new ProbeScE { V = (SealedItems[i], i) }, 5 => new ProbeScF { V = (SealedItems[i], i) }, 6 => new ProbeScG { V = (SealedItems[i], i) }, _ => new ProbeScH { V = (SealedItems[i], i) } });
    private static readonly ProbeBox<(ProbeOpenItem, int)>[] GenericOpenClassTupleBase = Make<ProbeBox<(ProbeOpenItem, int)>>(i => i switch { 0 => new ProbeOcA { V = (OpenItems[i], i) }, 1 => new ProbeOcB { V = (OpenItems[i], i) }, 2 => new ProbeOcC { V = (OpenItems[i], i) }, 3 => new ProbeOcD { V = (OpenItems[i], i) }, 4 => new ProbeOcE { V = (OpenItems[i], i) }, 5 => new ProbeOcF { V = (OpenItems[i], i) }, 6 => new ProbeOcG { V = (OpenItems[i], i) }, _ => new ProbeOcH { V = (OpenItems[i], i) } });

    private static readonly byte[] NinoNonGenericBaseBytes = NinoSerializer.Serialize(NonGenericBase);
    private static readonly byte[] MemoryPackNonGenericBaseBytes = MemoryPackSerializer.Serialize(NonGenericBase);
    private static readonly byte[] NinoGenericIntBaseBytes = NinoSerializer.Serialize(GenericIntBase);
    private static readonly byte[] MemoryPackGenericIntBaseBytes = MemoryPackSerializer.Serialize(GenericIntBase);
    private static readonly byte[] NinoGenericUnmanagedTupleBaseBytes = NinoSerializer.Serialize(GenericUnmanagedTupleBase);
    private static readonly byte[] MemoryPackGenericUnmanagedTupleBaseBytes = MemoryPackSerializer.Serialize(GenericUnmanagedTupleBase);
    private static readonly byte[] NinoGenericManagedTupleBaseBytes = NinoSerializer.Serialize(GenericManagedTupleBase);
    private static readonly byte[] MemoryPackGenericManagedTupleBaseBytes = MemoryPackSerializer.Serialize(GenericManagedTupleBase);
    private static readonly byte[] NinoGenericSealedClassTupleBaseBytes = NinoSerializer.Serialize(GenericSealedClassTupleBase);
    private static readonly byte[] MemoryPackGenericSealedClassTupleBaseBytes = MemoryPackSerializer.Serialize(GenericSealedClassTupleBase);
    private static readonly byte[] NinoGenericOpenClassTupleBaseBytes = NinoSerializer.Serialize(GenericOpenClassTupleBase);
    private static readonly byte[] MemoryPackGenericOpenClassTupleBaseBytes = MemoryPackSerializer.Serialize(GenericOpenClassTupleBase);

    private readonly NinoArrayBufferWriter _buffer = new(1 << 16);

    static GenericPolymorphicTest()
    {
        BenchmarkPayloadRegistry.Register(nameof(NinoSerializeNonGenericBase), NinoNonGenericBaseBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(MemoryPackSerializeNonGenericBase), MemoryPackNonGenericBaseBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(NinoSerializeGenericIntBase), NinoGenericIntBaseBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(MemoryPackSerializeGenericIntBase), MemoryPackGenericIntBaseBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(NinoSerializeGenericUnmanagedTupleBase), NinoGenericUnmanagedTupleBaseBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(MemoryPackSerializeGenericUnmanagedTupleBase), MemoryPackGenericUnmanagedTupleBaseBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(NinoSerializeGenericManagedTupleBase), NinoGenericManagedTupleBaseBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(MemoryPackSerializeGenericManagedTupleBase), MemoryPackGenericManagedTupleBaseBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(NinoSerializeGenericSealedClassTupleBase), NinoGenericSealedClassTupleBaseBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(MemoryPackSerializeGenericSealedClassTupleBase), MemoryPackGenericSealedClassTupleBaseBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(NinoSerializeGenericOpenClassTupleBase), NinoGenericOpenClassTupleBaseBytes.Length);
        BenchmarkPayloadRegistry.Register(nameof(MemoryPackSerializeGenericOpenClassTupleBase), MemoryPackGenericOpenClassTupleBaseBytes.Length);
    }

    private static bool RegisterUnions()
    {
        MemoryPackFormatterProvider.Register(new DynamicUnionFormatter<ProbeBox<int>>((0, typeof(ProbeBoxA)), (1, typeof(ProbeBoxB)), (2, typeof(ProbeBoxC)), (3, typeof(ProbeBoxD)), (4, typeof(ProbeBoxE)), (5, typeof(ProbeBoxF)), (6, typeof(ProbeBoxG)), (7, typeof(ProbeBoxH))));
        MemoryPackFormatterProvider.Register(new DynamicUnionFormatter<ProbeBox<(int, long)>>((0, typeof(ProbeTupA)), (1, typeof(ProbeTupB)), (2, typeof(ProbeTupC)), (3, typeof(ProbeTupD)), (4, typeof(ProbeTupE)), (5, typeof(ProbeTupF)), (6, typeof(ProbeTupG)), (7, typeof(ProbeTupH))));
        MemoryPackFormatterProvider.Register(new DynamicUnionFormatter<ProbeBox<(int, string)>>((0, typeof(ProbeStrA)), (1, typeof(ProbeStrB)), (2, typeof(ProbeStrC)), (3, typeof(ProbeStrD)), (4, typeof(ProbeStrE)), (5, typeof(ProbeStrF)), (6, typeof(ProbeStrG)), (7, typeof(ProbeStrH))));
        MemoryPackFormatterProvider.Register(new DynamicUnionFormatter<ProbeBox<(ProbeSealedItem, int)>>((0, typeof(ProbeScA)), (1, typeof(ProbeScB)), (2, typeof(ProbeScC)), (3, typeof(ProbeScD)), (4, typeof(ProbeScE)), (5, typeof(ProbeScF)), (6, typeof(ProbeScG)), (7, typeof(ProbeScH))));
        MemoryPackFormatterProvider.Register(new DynamicUnionFormatter<ProbeBox<(ProbeOpenItem, int)>>((0, typeof(ProbeOcA)), (1, typeof(ProbeOcB)), (2, typeof(ProbeOcC)), (3, typeof(ProbeOcD)), (4, typeof(ProbeOcE)), (5, typeof(ProbeOcF)), (6, typeof(ProbeOcG)), (7, typeof(ProbeOcH))));
        return true;
    }

    /// <summary>Both libraries read back what was written: the values read are written by Nino as the originals are.</summary>
    [GlobalSetup]
    public void Verify()
    {
        Check(NonGenericBase, MemoryPackNonGenericBaseBytes);
        Check(GenericIntBase, MemoryPackGenericIntBaseBytes);
        Check(GenericUnmanagedTupleBase, MemoryPackGenericUnmanagedTupleBaseBytes);
        Check(GenericManagedTupleBase, MemoryPackGenericManagedTupleBaseBytes);
        Check(GenericSealedClassTupleBase, MemoryPackGenericSealedClassTupleBaseBytes);
        Check(GenericOpenClassTupleBase, MemoryPackGenericOpenClassTupleBaseBytes);
    }

    private static void Check<T>(T[] values, byte[] memoryPack)
    {
        byte[] nino = NinoSerializer.Serialize(values);
        if (!nino.AsSpan().SequenceEqual(NinoSerializer.Serialize(NinoDeserializer.Deserialize<T[]>(nino))) ||
            !nino.AsSpan().SequenceEqual(NinoSerializer.Serialize(MemoryPackSerializer.Deserialize<T[]>(memoryPack))))
            throw new InvalidOperationException($"{typeof(T)} does not round-trip");
    }

    private static T[] Make<T>(Func<int, T> create)
    {
        var random = new Random(42);
        return Enumerable.Range(0, N).Select(_ => create(random.Next(8))).ToArray();
    }

    [Benchmark(Baseline = true), BenchmarkCategory("NonGenericBaseSerialize")]
    public int NinoSerializeNonGenericBase()
    {
        _buffer.ResetWrittenCount();
        NinoSerializer.Serialize(NonGenericBase, _buffer);
        return _buffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("NonGenericBaseSerialize")]
    public int MemoryPackSerializeNonGenericBase()
    {
        _buffer.ResetWrittenCount();
        MemoryPackSerializer.Serialize(_buffer, NonGenericBase);
        return _buffer.WrittenCount;
    }

    [Benchmark(Baseline = true), BenchmarkCategory("GenericIntBaseSerialize")]
    public int NinoSerializeGenericIntBase()
    {
        _buffer.ResetWrittenCount();
        NinoSerializer.Serialize(GenericIntBase, _buffer);
        return _buffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("GenericIntBaseSerialize")]
    public int MemoryPackSerializeGenericIntBase()
    {
        _buffer.ResetWrittenCount();
        MemoryPackSerializer.Serialize(_buffer, GenericIntBase);
        return _buffer.WrittenCount;
    }

    [Benchmark(Baseline = true), BenchmarkCategory("GenericUnmanagedTupleBaseSerialize")]
    public int NinoSerializeGenericUnmanagedTupleBase()
    {
        _buffer.ResetWrittenCount();
        NinoSerializer.Serialize(GenericUnmanagedTupleBase, _buffer);
        return _buffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("GenericUnmanagedTupleBaseSerialize")]
    public int MemoryPackSerializeGenericUnmanagedTupleBase()
    {
        _buffer.ResetWrittenCount();
        MemoryPackSerializer.Serialize(_buffer, GenericUnmanagedTupleBase);
        return _buffer.WrittenCount;
    }

    [Benchmark(Baseline = true), BenchmarkCategory("GenericManagedTupleBaseSerialize")]
    public int NinoSerializeGenericManagedTupleBase()
    {
        _buffer.ResetWrittenCount();
        NinoSerializer.Serialize(GenericManagedTupleBase, _buffer);
        return _buffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("GenericManagedTupleBaseSerialize")]
    public int MemoryPackSerializeGenericManagedTupleBase()
    {
        _buffer.ResetWrittenCount();
        MemoryPackSerializer.Serialize(_buffer, GenericManagedTupleBase);
        return _buffer.WrittenCount;
    }

    [Benchmark(Baseline = true), BenchmarkCategory("GenericSealedClassTupleBaseSerialize")]
    public int NinoSerializeGenericSealedClassTupleBase()
    {
        _buffer.ResetWrittenCount();
        NinoSerializer.Serialize(GenericSealedClassTupleBase, _buffer);
        return _buffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("GenericSealedClassTupleBaseSerialize")]
    public int MemoryPackSerializeGenericSealedClassTupleBase()
    {
        _buffer.ResetWrittenCount();
        MemoryPackSerializer.Serialize(_buffer, GenericSealedClassTupleBase);
        return _buffer.WrittenCount;
    }

    [Benchmark(Baseline = true), BenchmarkCategory("GenericOpenClassTupleBaseSerialize")]
    public int NinoSerializeGenericOpenClassTupleBase()
    {
        _buffer.ResetWrittenCount();
        NinoSerializer.Serialize(GenericOpenClassTupleBase, _buffer);
        return _buffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("GenericOpenClassTupleBaseSerialize")]
    public int MemoryPackSerializeGenericOpenClassTupleBase()
    {
        _buffer.ResetWrittenCount();
        MemoryPackSerializer.Serialize(_buffer, GenericOpenClassTupleBase);
        return _buffer.WrittenCount;
    }

    [Benchmark(Baseline = true), BenchmarkCategory("NonGenericBaseDeserialize")]
    public ProbeBase[] NinoDeserializeNonGenericBase() => NinoDeserializer.Deserialize<ProbeBase[]>(NinoNonGenericBaseBytes);

    [Benchmark, BenchmarkCategory("NonGenericBaseDeserialize")]
    public ProbeBase[] MemoryPackDeserializeNonGenericBase() => MemoryPackSerializer.Deserialize<ProbeBase[]>(MemoryPackNonGenericBaseBytes);

    [Benchmark(Baseline = true), BenchmarkCategory("GenericIntBaseDeserialize")]
    public ProbeBox<int>[] NinoDeserializeGenericIntBase() => NinoDeserializer.Deserialize<ProbeBox<int>[]>(NinoGenericIntBaseBytes);

    [Benchmark, BenchmarkCategory("GenericIntBaseDeserialize")]
    public ProbeBox<int>[] MemoryPackDeserializeGenericIntBase() => MemoryPackSerializer.Deserialize<ProbeBox<int>[]>(MemoryPackGenericIntBaseBytes);

    [Benchmark(Baseline = true), BenchmarkCategory("GenericUnmanagedTupleBaseDeserialize")]
    public ProbeBox<(int, long)>[] NinoDeserializeGenericUnmanagedTupleBase() => NinoDeserializer.Deserialize<ProbeBox<(int, long)>[]>(NinoGenericUnmanagedTupleBaseBytes);

    [Benchmark, BenchmarkCategory("GenericUnmanagedTupleBaseDeserialize")]
    public ProbeBox<(int, long)>[] MemoryPackDeserializeGenericUnmanagedTupleBase() => MemoryPackSerializer.Deserialize<ProbeBox<(int, long)>[]>(MemoryPackGenericUnmanagedTupleBaseBytes);

    [Benchmark(Baseline = true), BenchmarkCategory("GenericManagedTupleBaseDeserialize")]
    public ProbeBox<(int, string)>[] NinoDeserializeGenericManagedTupleBase() => NinoDeserializer.Deserialize<ProbeBox<(int, string)>[]>(NinoGenericManagedTupleBaseBytes);

    [Benchmark, BenchmarkCategory("GenericManagedTupleBaseDeserialize")]
    public ProbeBox<(int, string)>[] MemoryPackDeserializeGenericManagedTupleBase() => MemoryPackSerializer.Deserialize<ProbeBox<(int, string)>[]>(MemoryPackGenericManagedTupleBaseBytes);

    [Benchmark(Baseline = true), BenchmarkCategory("GenericSealedClassTupleBaseDeserialize")]
    public ProbeBox<(ProbeSealedItem, int)>[] NinoDeserializeGenericSealedClassTupleBase() => NinoDeserializer.Deserialize<ProbeBox<(ProbeSealedItem, int)>[]>(NinoGenericSealedClassTupleBaseBytes);

    [Benchmark, BenchmarkCategory("GenericSealedClassTupleBaseDeserialize")]
    public ProbeBox<(ProbeSealedItem, int)>[] MemoryPackDeserializeGenericSealedClassTupleBase() => MemoryPackSerializer.Deserialize<ProbeBox<(ProbeSealedItem, int)>[]>(MemoryPackGenericSealedClassTupleBaseBytes);

    [Benchmark(Baseline = true), BenchmarkCategory("GenericOpenClassTupleBaseDeserialize")]
    public ProbeBox<(ProbeOpenItem, int)>[] NinoDeserializeGenericOpenClassTupleBase() => NinoDeserializer.Deserialize<ProbeBox<(ProbeOpenItem, int)>[]>(NinoGenericOpenClassTupleBaseBytes);

    [Benchmark, BenchmarkCategory("GenericOpenClassTupleBaseDeserialize")]
    public ProbeBox<(ProbeOpenItem, int)>[] MemoryPackDeserializeGenericOpenClassTupleBase() => MemoryPackSerializer.Deserialize<ProbeBox<(ProbeOpenItem, int)>[]>(MemoryPackGenericOpenClassTupleBaseBytes);
}

[NinoType, MemoryPackable] public sealed partial class ProbeSealedItem { public int X; public long Y; }
[NinoType, MemoryPackable] public partial class ProbeOpenItem { public int X; public long Y; }

[NinoType, MemoryPackable]
[MemoryPackUnion(0, typeof(ProbeA))]
[MemoryPackUnion(1, typeof(ProbeB))]
[MemoryPackUnion(2, typeof(ProbeC))]
[MemoryPackUnion(3, typeof(ProbeD))]
[MemoryPackUnion(4, typeof(ProbeE))]
[MemoryPackUnion(5, typeof(ProbeF))]
[MemoryPackUnion(6, typeof(ProbeG))]
[MemoryPackUnion(7, typeof(ProbeH))]
public abstract partial class ProbeBase { public int V; }
[NinoType, MemoryPackable] public sealed partial class ProbeA : ProbeBase { }
[NinoType, MemoryPackable] public sealed partial class ProbeB : ProbeBase { }
[NinoType, MemoryPackable] public sealed partial class ProbeC : ProbeBase { }
[NinoType, MemoryPackable] public sealed partial class ProbeD : ProbeBase { }
[NinoType, MemoryPackable] public sealed partial class ProbeE : ProbeBase { }
[NinoType, MemoryPackable] public sealed partial class ProbeF : ProbeBase { }
[NinoType, MemoryPackable] public sealed partial class ProbeG : ProbeBase { }
[NinoType, MemoryPackable] public sealed partial class ProbeH : ProbeBase { }

[NinoType] public abstract class ProbeBox<T> { public T V; }
[NinoType, MemoryPackable] public sealed partial class ProbeBoxA : ProbeBox<int> { }
[NinoType, MemoryPackable] public sealed partial class ProbeBoxB : ProbeBox<int> { }
[NinoType, MemoryPackable] public sealed partial class ProbeBoxC : ProbeBox<int> { }
[NinoType, MemoryPackable] public sealed partial class ProbeBoxD : ProbeBox<int> { }
[NinoType, MemoryPackable] public sealed partial class ProbeBoxE : ProbeBox<int> { }
[NinoType, MemoryPackable] public sealed partial class ProbeBoxF : ProbeBox<int> { }
[NinoType, MemoryPackable] public sealed partial class ProbeBoxG : ProbeBox<int> { }
[NinoType, MemoryPackable] public sealed partial class ProbeBoxH : ProbeBox<int> { }
[NinoType, MemoryPackable] public sealed partial class ProbeTupA : ProbeBox<(int, long)> { }
[NinoType, MemoryPackable] public sealed partial class ProbeTupB : ProbeBox<(int, long)> { }
[NinoType, MemoryPackable] public sealed partial class ProbeTupC : ProbeBox<(int, long)> { }
[NinoType, MemoryPackable] public sealed partial class ProbeTupD : ProbeBox<(int, long)> { }
[NinoType, MemoryPackable] public sealed partial class ProbeTupE : ProbeBox<(int, long)> { }
[NinoType, MemoryPackable] public sealed partial class ProbeTupF : ProbeBox<(int, long)> { }
[NinoType, MemoryPackable] public sealed partial class ProbeTupG : ProbeBox<(int, long)> { }
[NinoType, MemoryPackable] public sealed partial class ProbeTupH : ProbeBox<(int, long)> { }
[NinoType, MemoryPackable] public sealed partial class ProbeStrA : ProbeBox<(int, string)> { }
[NinoType, MemoryPackable] public sealed partial class ProbeStrB : ProbeBox<(int, string)> { }
[NinoType, MemoryPackable] public sealed partial class ProbeStrC : ProbeBox<(int, string)> { }
[NinoType, MemoryPackable] public sealed partial class ProbeStrD : ProbeBox<(int, string)> { }
[NinoType, MemoryPackable] public sealed partial class ProbeStrE : ProbeBox<(int, string)> { }
[NinoType, MemoryPackable] public sealed partial class ProbeStrF : ProbeBox<(int, string)> { }
[NinoType, MemoryPackable] public sealed partial class ProbeStrG : ProbeBox<(int, string)> { }
[NinoType, MemoryPackable] public sealed partial class ProbeStrH : ProbeBox<(int, string)> { }
[NinoType, MemoryPackable] public sealed partial class ProbeScA : ProbeBox<(ProbeSealedItem, int)> { }
[NinoType, MemoryPackable] public sealed partial class ProbeScB : ProbeBox<(ProbeSealedItem, int)> { }
[NinoType, MemoryPackable] public sealed partial class ProbeScC : ProbeBox<(ProbeSealedItem, int)> { }
[NinoType, MemoryPackable] public sealed partial class ProbeScD : ProbeBox<(ProbeSealedItem, int)> { }
[NinoType, MemoryPackable] public sealed partial class ProbeScE : ProbeBox<(ProbeSealedItem, int)> { }
[NinoType, MemoryPackable] public sealed partial class ProbeScF : ProbeBox<(ProbeSealedItem, int)> { }
[NinoType, MemoryPackable] public sealed partial class ProbeScG : ProbeBox<(ProbeSealedItem, int)> { }
[NinoType, MemoryPackable] public sealed partial class ProbeScH : ProbeBox<(ProbeSealedItem, int)> { }
[NinoType, MemoryPackable] public sealed partial class ProbeOcA : ProbeBox<(ProbeOpenItem, int)> { }
[NinoType, MemoryPackable] public sealed partial class ProbeOcB : ProbeBox<(ProbeOpenItem, int)> { }
[NinoType, MemoryPackable] public sealed partial class ProbeOcC : ProbeBox<(ProbeOpenItem, int)> { }
[NinoType, MemoryPackable] public sealed partial class ProbeOcD : ProbeBox<(ProbeOpenItem, int)> { }
[NinoType, MemoryPackable] public sealed partial class ProbeOcE : ProbeBox<(ProbeOpenItem, int)> { }
[NinoType, MemoryPackable] public sealed partial class ProbeOcF : ProbeBox<(ProbeOpenItem, int)> { }
[NinoType, MemoryPackable] public sealed partial class ProbeOcG : ProbeBox<(ProbeOpenItem, int)> { }
[NinoType, MemoryPackable] public sealed partial class ProbeOcH : ProbeBox<(ProbeOpenItem, int)> { }
#endif
