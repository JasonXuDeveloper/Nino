using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Reports;
using BenchmarkDotNet.Running;

namespace Nino.Benchmark;

public class PayloadColumnAttribute() : ColumnConfigBaseAttribute(new PayloadColumn());

public static class BenchmarkPayloadRegistry
{
    private static readonly ConcurrentDictionary<string, int> PayloadSizes = new();
    private static readonly ConcurrentDictionary<string, byte[]> PayloadBytes = new();

    public static void Register(string methodName, int size)
    {
        PayloadSizes[methodName] = size;
    }

    /// <summary>A serialize method's bytes (the bytes its library's deserialize method reads): their size, and the bytes
    /// for --write-data. The classes BenchGen writes register these.</summary>
    public static void Register(string methodName, byte[] bytes)
    {
        PayloadSizes[methodName] = bytes.Length;
        PayloadBytes[methodName] = bytes;
    }

    /// <summary>Every serialize method's registered bytes, by method name.</summary>
    public static IEnumerable<KeyValuePair<string, byte[]>> Bytes => PayloadBytes;

    public static bool TryGet(string methodName, out int size)
    {
        return PayloadSizes.TryGetValue(methodName, out size);
    }
}

public class PayloadColumn : IColumn
{
    public string Id => nameof(PayloadColumn);
    public string ColumnName => "Payload";
    public bool AlwaysShow => true;
    public ColumnCategory Category => ColumnCategory.Custom;
    public int PriorityInCategory => 0;
    public bool IsNumeric => true;
    public UnitType UnitType => UnitType.Size;
    public string Legend => "Serialized payload size in bytes (from setup)";

    public bool IsAvailable(Summary summary) => true;
    public bool IsDefault(Summary summary, BenchmarkCase benchmarkCase) => false;

    public string GetValue(Summary summary, BenchmarkCase benchmarkCase)
    {
        var methodName = benchmarkCase.Descriptor.WorkloadMethod.Name;
        var declaringType = benchmarkCase.Descriptor.WorkloadMethod.DeclaringType;
        if (declaringType != null)
        {
            // A class whose setup throws (e.g. built against an older library without a formatter it uses, as
            // ab-bench.yml does for its base ref) has no rows; it must not take the other classes' summary down.
            try
            {
                RuntimeHelpers.RunClassConstructor(declaringType.TypeHandle);
            }
            catch (TypeInitializationException)
            {
                return "-";
            }
        }

        if (BenchmarkPayloadRegistry.TryGet(methodName, out var size))
            return size.ToString("N0", CultureInfo.InvariantCulture) + " B";

        return "-";
    }

    public string GetValue(Summary summary, BenchmarkCase benchmarkCase, SummaryStyle style)
        => GetValue(summary, benchmarkCase);
}
