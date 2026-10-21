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
/// tuples holding a sealed and a non-sealed class as the type argument, against MemoryPack (a union attribute on the non-generic base, a <c>DynamicUnionFormatter</c>
/// for each closed generic one, which MemoryPack's attribute cannot be put on). A probe, so only a filter naming it
/// runs it.
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
    // First, so that the data below is serialized with them.
    private static readonly bool Unions = RegisterUnions();
    private static readonly string[] Names = Enumerable.Range(0, 8).Select(static i => "name" + i).ToArray();
    private static readonly ProbeSealedItem[] SealedItems =
        Enumerable.Range(0, 8).Select(static i => new ProbeSealedItem { X = i, Y = i * 2L }).ToArray();
    private static readonly ProbeOpenItem[] OpenItems =
        Enumerable.Range(0, 8).Select(static i => new ProbeOpenItem { X = i, Y = i * 2L }).ToArray();

    private static readonly ProbeBase[] NonGenericBase = Make<ProbeBase>(i => i switch { 0 => new ProbeA { V = i }, 1 => new ProbeB { V = i }, 2 => new ProbeC { V = i }, 3 => new ProbeD { V = i }, 4 => new ProbeE { V = i }, 5 => new ProbeF { V = i }, 6 => new ProbeG { V = i }, _ => new ProbeH { V = i } });
    private static readonly ProbeBox<int>[] IntBase = Make<ProbeBox<int>>(i => i switch { 0 => new ProbeBoxA { V = i }, 1 => new ProbeBoxB { V = i }, 2 => new ProbeBoxC { V = i }, 3 => new ProbeBoxD { V = i }, 4 => new ProbeBoxE { V = i }, 5 => new ProbeBoxF { V = i }, 6 => new ProbeBoxG { V = i }, _ => new ProbeBoxH { V = i } });
    private static readonly ProbeBox<(int, long)>[] UnmanagedTupleBase = Make<ProbeBox<(int, long)>>(i => i switch { 0 => new ProbeTupA { V = (i, i * 3L) }, 1 => new ProbeTupB { V = (i, i * 3L) }, 2 => new ProbeTupC { V = (i, i * 3L) }, 3 => new ProbeTupD { V = (i, i * 3L) }, 4 => new ProbeTupE { V = (i, i * 3L) }, 5 => new ProbeTupF { V = (i, i * 3L) }, 6 => new ProbeTupG { V = (i, i * 3L) }, _ => new ProbeTupH { V = (i, i * 3L) } });
    private static readonly ProbeBox<(int, string)>[] ManagedTupleBase = Make<ProbeBox<(int, string)>>(i => i switch { 0 => new ProbeStrA { V = (i, Names[i]) }, 1 => new ProbeStrB { V = (i, Names[i]) }, 2 => new ProbeStrC { V = (i, Names[i]) }, 3 => new ProbeStrD { V = (i, Names[i]) }, 4 => new ProbeStrE { V = (i, Names[i]) }, 5 => new ProbeStrF { V = (i, Names[i]) }, 6 => new ProbeStrG { V = (i, Names[i]) }, _ => new ProbeStrH { V = (i, Names[i]) } });
    private static readonly ProbeBox<(ProbeSealedItem, int)>[] SealedClassTupleBase = Make<ProbeBox<(ProbeSealedItem, int)>>(i => i switch { 0 => new ProbeScA { V = (SealedItems[i], i) }, 1 => new ProbeScB { V = (SealedItems[i], i) }, 2 => new ProbeScC { V = (SealedItems[i], i) }, 3 => new ProbeScD { V = (SealedItems[i], i) }, 4 => new ProbeScE { V = (SealedItems[i], i) }, 5 => new ProbeScF { V = (SealedItems[i], i) }, 6 => new ProbeScG { V = (SealedItems[i], i) }, _ => new ProbeScH { V = (SealedItems[i], i) } });
    private static readonly ProbeBox<(ProbeOpenItem, int)>[] OpenClassTupleBase = Make<ProbeBox<(ProbeOpenItem, int)>>(i => i switch { 0 => new ProbeOcA { V = (OpenItems[i], i) }, 1 => new ProbeOcB { V = (OpenItems[i], i) }, 2 => new ProbeOcC { V = (OpenItems[i], i) }, 3 => new ProbeOcD { V = (OpenItems[i], i) }, 4 => new ProbeOcE { V = (OpenItems[i], i) }, 5 => new ProbeOcF { V = (OpenItems[i], i) }, 6 => new ProbeOcG { V = (OpenItems[i], i) }, _ => new ProbeOcH { V = (OpenItems[i], i) } });

    private static readonly byte[] NinoNonGenericBaseBytes = NinoSerializer.Serialize(NonGenericBase);
    private static readonly byte[] MemoryPackNonGenericBaseBytes = MemoryPackSerializer.Serialize(NonGenericBase);
    private static readonly byte[] NinoIntBaseBytes = NinoSerializer.Serialize(IntBase);
    private static readonly byte[] MemoryPackIntBaseBytes = MemoryPackSerializer.Serialize(IntBase);
    private static readonly byte[] NinoUnmanagedTupleBaseBytes = NinoSerializer.Serialize(UnmanagedTupleBase);
    private static readonly byte[] MemoryPackUnmanagedTupleBaseBytes = MemoryPackSerializer.Serialize(UnmanagedTupleBase);
    private static readonly byte[] NinoManagedTupleBaseBytes = NinoSerializer.Serialize(ManagedTupleBase);
    private static readonly byte[] MemoryPackManagedTupleBaseBytes = MemoryPackSerializer.Serialize(ManagedTupleBase);
    private static readonly byte[] NinoSealedClassTupleBaseBytes = NinoSerializer.Serialize(SealedClassTupleBase);
    private static readonly byte[] MemoryPackSealedClassTupleBaseBytes = MemoryPackSerializer.Serialize(SealedClassTupleBase);
    private static readonly byte[] NinoOpenClassTupleBaseBytes = NinoSerializer.Serialize(OpenClassTupleBase);
    private static readonly byte[] MemoryPackOpenClassTupleBaseBytes = MemoryPackSerializer.Serialize(OpenClassTupleBase);

    private readonly NinoArrayBufferWriter _buffer = new(1 << 16);

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
        Check(IntBase, MemoryPackIntBaseBytes);
        Check(UnmanagedTupleBase, MemoryPackUnmanagedTupleBaseBytes);
        Check(ManagedTupleBase, MemoryPackManagedTupleBaseBytes);
        Check(SealedClassTupleBase, MemoryPackSealedClassTupleBaseBytes);
        Check(OpenClassTupleBase, MemoryPackOpenClassTupleBaseBytes);
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

    [Benchmark(Baseline = true), BenchmarkCategory("Serialize")]
    public int NinoSerializeNonGenericBase()
    {
        _buffer.ResetWrittenCount();
        NinoSerializer.Serialize(NonGenericBase, _buffer);
        return _buffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("Serialize")]
    public int MemoryPackSerializeNonGenericBase()
    {
        _buffer.ResetWrittenCount();
        MemoryPackSerializer.Serialize(_buffer, NonGenericBase);
        return _buffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("Serialize")]
    public int NinoSerializeIntBase()
    {
        _buffer.ResetWrittenCount();
        NinoSerializer.Serialize(IntBase, _buffer);
        return _buffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("Serialize")]
    public int MemoryPackSerializeIntBase()
    {
        _buffer.ResetWrittenCount();
        MemoryPackSerializer.Serialize(_buffer, IntBase);
        return _buffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("Serialize")]
    public int NinoSerializeUnmanagedTupleBase()
    {
        _buffer.ResetWrittenCount();
        NinoSerializer.Serialize(UnmanagedTupleBase, _buffer);
        return _buffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("Serialize")]
    public int MemoryPackSerializeUnmanagedTupleBase()
    {
        _buffer.ResetWrittenCount();
        MemoryPackSerializer.Serialize(_buffer, UnmanagedTupleBase);
        return _buffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("Serialize")]
    public int NinoSerializeManagedTupleBase()
    {
        _buffer.ResetWrittenCount();
        NinoSerializer.Serialize(ManagedTupleBase, _buffer);
        return _buffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("Serialize")]
    public int MemoryPackSerializeManagedTupleBase()
    {
        _buffer.ResetWrittenCount();
        MemoryPackSerializer.Serialize(_buffer, ManagedTupleBase);
        return _buffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("Serialize")]
    public int NinoSerializeSealedClassTupleBase()
    {
        _buffer.ResetWrittenCount();
        NinoSerializer.Serialize(SealedClassTupleBase, _buffer);
        return _buffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("Serialize")]
    public int MemoryPackSerializeSealedClassTupleBase()
    {
        _buffer.ResetWrittenCount();
        MemoryPackSerializer.Serialize(_buffer, SealedClassTupleBase);
        return _buffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("Serialize")]
    public int NinoSerializeOpenClassTupleBase()
    {
        _buffer.ResetWrittenCount();
        NinoSerializer.Serialize(OpenClassTupleBase, _buffer);
        return _buffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("Serialize")]
    public int MemoryPackSerializeOpenClassTupleBase()
    {
        _buffer.ResetWrittenCount();
        MemoryPackSerializer.Serialize(_buffer, OpenClassTupleBase);
        return _buffer.WrittenCount;
    }

    [Benchmark(Baseline = true), BenchmarkCategory("Deserialize")]
    public ProbeBase[] NinoDeserializeNonGenericBase() => NinoDeserializer.Deserialize<ProbeBase[]>(NinoNonGenericBaseBytes);

    [Benchmark, BenchmarkCategory("Deserialize")]
    public ProbeBase[] MemoryPackDeserializeNonGenericBase() => MemoryPackSerializer.Deserialize<ProbeBase[]>(MemoryPackNonGenericBaseBytes);

    [Benchmark, BenchmarkCategory("Deserialize")]
    public ProbeBox<int>[] NinoDeserializeIntBase() => NinoDeserializer.Deserialize<ProbeBox<int>[]>(NinoIntBaseBytes);

    [Benchmark, BenchmarkCategory("Deserialize")]
    public ProbeBox<int>[] MemoryPackDeserializeIntBase() => MemoryPackSerializer.Deserialize<ProbeBox<int>[]>(MemoryPackIntBaseBytes);

    [Benchmark, BenchmarkCategory("Deserialize")]
    public ProbeBox<(int, long)>[] NinoDeserializeUnmanagedTupleBase() => NinoDeserializer.Deserialize<ProbeBox<(int, long)>[]>(NinoUnmanagedTupleBaseBytes);

    [Benchmark, BenchmarkCategory("Deserialize")]
    public ProbeBox<(int, long)>[] MemoryPackDeserializeUnmanagedTupleBase() => MemoryPackSerializer.Deserialize<ProbeBox<(int, long)>[]>(MemoryPackUnmanagedTupleBaseBytes);

    [Benchmark, BenchmarkCategory("Deserialize")]
    public ProbeBox<(int, string)>[] NinoDeserializeManagedTupleBase() => NinoDeserializer.Deserialize<ProbeBox<(int, string)>[]>(NinoManagedTupleBaseBytes);

    [Benchmark, BenchmarkCategory("Deserialize")]
    public ProbeBox<(int, string)>[] MemoryPackDeserializeManagedTupleBase() => MemoryPackSerializer.Deserialize<ProbeBox<(int, string)>[]>(MemoryPackManagedTupleBaseBytes);

    [Benchmark, BenchmarkCategory("Deserialize")]
    public ProbeBox<(ProbeSealedItem, int)>[] NinoDeserializeSealedClassTupleBase() => NinoDeserializer.Deserialize<ProbeBox<(ProbeSealedItem, int)>[]>(NinoSealedClassTupleBaseBytes);

    [Benchmark, BenchmarkCategory("Deserialize")]
    public ProbeBox<(ProbeSealedItem, int)>[] MemoryPackDeserializeSealedClassTupleBase() => MemoryPackSerializer.Deserialize<ProbeBox<(ProbeSealedItem, int)>[]>(MemoryPackSealedClassTupleBaseBytes);

    [Benchmark, BenchmarkCategory("Deserialize")]
    public ProbeBox<(ProbeOpenItem, int)>[] NinoDeserializeOpenClassTupleBase() => NinoDeserializer.Deserialize<ProbeBox<(ProbeOpenItem, int)>[]>(NinoOpenClassTupleBaseBytes);

    [Benchmark, BenchmarkCategory("Deserialize")]
    public ProbeBox<(ProbeOpenItem, int)>[] MemoryPackDeserializeOpenClassTupleBase() => MemoryPackSerializer.Deserialize<ProbeBox<(ProbeOpenItem, int)>[]>(MemoryPackOpenClassTupleBaseBytes);
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
