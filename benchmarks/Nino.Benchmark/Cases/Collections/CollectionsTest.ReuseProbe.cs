using System.Collections.Generic;
using BenchmarkDotNet.Attributes;
using Nino.Core;

namespace Nino.Benchmark;

// A/B probe, not for main: raw T? runs and a HashSet<int?> read into storage that already holds them.
public partial class CollectionsTest
{
    private static List<int?> _reuseInts;
    private static long?[] _reuseLongs;
    private static decimal?[] _reuseDecimals;
    private static HashSet<int?> _reuseHashSet;

    [Benchmark, BenchmarkCategory("ReuseProbeInts")]
    public List<int?> NinoReuseNullableInts()
    {
        _reuseInts ??= NinoDeserializer.Deserialize<List<int?>>(NinoNullableIntsBytes);
        NinoDeserializer.Deserialize(NinoNullableIntsBytes, ref _reuseInts);
        return _reuseInts;
    }

    [Benchmark, BenchmarkCategory("ReuseProbeLongs")]
    public long?[] NinoReuseNullableLongs()
    {
        _reuseLongs ??= NinoDeserializer.Deserialize<long?[]>(NinoNullableLongsBytes);
        NinoDeserializer.Deserialize(NinoNullableLongsBytes, ref _reuseLongs);
        return _reuseLongs;
    }

    [Benchmark, BenchmarkCategory("ReuseProbeDecimals")]
    public decimal?[] NinoReuseNullableDecimals()
    {
        _reuseDecimals ??= NinoDeserializer.Deserialize<decimal?[]>(NinoNullableDecimalsBytes);
        NinoDeserializer.Deserialize(NinoNullableDecimalsBytes, ref _reuseDecimals);
        return _reuseDecimals;
    }

    [Benchmark, BenchmarkCategory("ReuseProbeHashSet")]
    public HashSet<int?> NinoReuseNullableHashSetInts()
    {
        _reuseHashSet ??= NinoDeserializer.Deserialize<HashSet<int?>>(NinoNullableHashSetIntsBytes);
        NinoDeserializer.Deserialize(NinoNullableHashSetIntsBytes, ref _reuseHashSet);
        return _reuseHashSet;
    }
}
