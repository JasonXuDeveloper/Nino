// Generic polymorphic bases need a generator with the fix for #203 (rc.7): left out of A/B runs against older releases.
#if !NINO_BENCH_LEGACY
using System;
using System.Linq;
using BenchmarkDotNet.Configs;
using MemoryPack;
using MemoryPack.Formatters;
#if !NINO_BENCH_V3
using Nino.Core;
using ProtoBuf.Meta;
#endif

#nullable disable
namespace Nino.Benchmark;

/// <summary>
/// Arrays of 1,000 subtypes in random order (eight types each), through a non-generic base and through the closed
/// instances of a generic base (<c>ProbeBox&lt;T&gt;</c>) with a primitive, an unmanaged tuple, a managed tuple and
/// tuples holding a sealed and a non-sealed class as the type argument, against MemoryPack: a union attribute on the
/// non-generic base, and a <c>DynamicUnionFormatter</c> registered for each closed generic one, as MemoryPack's union
/// attribute cannot be put on a generic type (MEMPACK013, MEMPACK014). protobuf-net takes <c>[ProtoInclude]</c> on the
/// non-generic base and the subtypes of each closed generic one added to its runtime model; MessagePack only the
/// non-generic base: its unions are attributes on the base type, which cannot name different subtypes for each type
/// argument.
/// The timed methods are GenericPolymorphicTest.Cases.g.cs, generated from the cases below and the adapters (Adapters/,
/// runtimes/csharp/tools/BenchGen); the v3 build times Nino 3.3.13, which writes every [NinoType] class with its type id
/// (a base and its subtypes need nothing more), and MemoryPack on the same values.
/// </summary>
[BenchCase("NonGenericBase")]
[BenchCase("GenericIntBase", Quick = true, Without = "MessagePack")]
[BenchCase("GenericUnmanagedTupleBase", Without = "MessagePack",
    Broken = "LuminPack throws: writing a generic base over a tuple, Type ProbeTupF is not annotated in ProbeBox<(int, long)>")]
[BenchCase("GenericManagedTupleBase", Without = "MessagePack",
    Broken = "LuminPack throws: writing a generic base over a tuple, Type ProbeTupF is not annotated in ProbeBox<(int, long)>")]
[BenchCase("GenericSealedClassTupleBase", Without = "MessagePack",
    Broken = "LuminPack throws: writing a generic base over a tuple, Type ProbeTupF is not annotated in ProbeBox<(int, long)>")]
[BenchCase("GenericOpenClassTupleBase", Without = "MessagePack",
    Broken = "LuminPack throws: writing a generic base over a tuple, Type ProbeTupF is not annotated in ProbeBox<(int, long)>")]
[BenchClass(Buffer = "1 << 16", Bytes = "{Library}{Case}Bytes", Condition = "!NINO_BENCH_LEGACY")]
[PayloadColumn]
[HideColumns("StdDev", "RatioSD", "Error")]
[MinColumn, MaxColumn]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[NinoJob]
[GcServer(true)]
[MemoryDiagnoser]
[MarkdownExporterAttribute.GitHub]
public partial class GenericPolymorphicTest
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

    private static bool RegisterUnions()
    {
        MemoryPackFormatterProvider.Register(new DynamicUnionFormatter<ProbeBox<int>>((0, typeof(ProbeBoxA)), (1, typeof(ProbeBoxB)), (2, typeof(ProbeBoxC)), (3, typeof(ProbeBoxD)), (4, typeof(ProbeBoxE)), (5, typeof(ProbeBoxF)), (6, typeof(ProbeBoxG)), (7, typeof(ProbeBoxH))));
        MemoryPackFormatterProvider.Register(new DynamicUnionFormatter<ProbeBox<(int, long)>>((0, typeof(ProbeTupA)), (1, typeof(ProbeTupB)), (2, typeof(ProbeTupC)), (3, typeof(ProbeTupD)), (4, typeof(ProbeTupE)), (5, typeof(ProbeTupF)), (6, typeof(ProbeTupG)), (7, typeof(ProbeTupH))));
        MemoryPackFormatterProvider.Register(new DynamicUnionFormatter<ProbeBox<(int, string)>>((0, typeof(ProbeStrA)), (1, typeof(ProbeStrB)), (2, typeof(ProbeStrC)), (3, typeof(ProbeStrD)), (4, typeof(ProbeStrE)), (5, typeof(ProbeStrF)), (6, typeof(ProbeStrG)), (7, typeof(ProbeStrH))));
        MemoryPackFormatterProvider.Register(new DynamicUnionFormatter<ProbeBox<(ProbeSealedItem, int)>>((0, typeof(ProbeScA)), (1, typeof(ProbeScB)), (2, typeof(ProbeScC)), (3, typeof(ProbeScD)), (4, typeof(ProbeScE)), (5, typeof(ProbeScF)), (6, typeof(ProbeScG)), (7, typeof(ProbeScH))));
        MemoryPackFormatterProvider.Register(new DynamicUnionFormatter<ProbeBox<(ProbeOpenItem, int)>>((0, typeof(ProbeOcA)), (1, typeof(ProbeOcB)), (2, typeof(ProbeOcC)), (3, typeof(ProbeOcD)), (4, typeof(ProbeOcE)), (5, typeof(ProbeOcF)), (6, typeof(ProbeOcG)), (7, typeof(ProbeOcH))));
#if !NINO_BENCH_V3
        ProtobufSubtypes<int>(typeof(ProbeBoxA), typeof(ProbeBoxB), typeof(ProbeBoxC), typeof(ProbeBoxD), typeof(ProbeBoxE), typeof(ProbeBoxF), typeof(ProbeBoxG), typeof(ProbeBoxH));
        ProtobufSubtypes<(int, long)>(typeof(ProbeTupA), typeof(ProbeTupB), typeof(ProbeTupC), typeof(ProbeTupD), typeof(ProbeTupE), typeof(ProbeTupF), typeof(ProbeTupG), typeof(ProbeTupH));
        ProtobufSubtypes<(int, string)>(typeof(ProbeStrA), typeof(ProbeStrB), typeof(ProbeStrC), typeof(ProbeStrD), typeof(ProbeStrE), typeof(ProbeStrF), typeof(ProbeStrG), typeof(ProbeStrH));
        ProtobufSubtypes<(ProbeSealedItem, int)>(typeof(ProbeScA), typeof(ProbeScB), typeof(ProbeScC), typeof(ProbeScD), typeof(ProbeScE), typeof(ProbeScF), typeof(ProbeScG), typeof(ProbeScH));
        ProtobufSubtypes<(ProbeOpenItem, int)>(typeof(ProbeOcA), typeof(ProbeOcB), typeof(ProbeOcC), typeof(ProbeOcD), typeof(ProbeOcE), typeof(ProbeOcF), typeof(ProbeOcG), typeof(ProbeOcH));
#endif
        return true;
    }

    private static T[] Make<T>(Func<int, T> create)
    {
        var random = new Random(42);
        return Enumerable.Range(0, N).Select(_ => create(random.Next(8))).ToArray();
    }
#if !NINO_BENCH_V3

    /// <summary>protobuf-net's way for a generic base: the subtypes of one closed type, added to its model.</summary>
    private static void ProtobufSubtypes<T>(params Type[] subtypes)
    {
        var meta = RuntimeTypeModel.Default[typeof(ProbeBox<T>)];
        for (int i = 0; i < subtypes.Length; i++)
            meta.AddSubType(10 + i, subtypes[i]);
    }

    /// <summary>
    /// Both libraries read back what was written: the values read are written by Nino as the originals are (v3's
    /// per-type overloads cannot express this generic check; --verify checks the same for both builds).
    /// </summary>
    partial void OnSetup()
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

#endif
}
#endif
