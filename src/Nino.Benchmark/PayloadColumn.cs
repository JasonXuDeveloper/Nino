using System;
using System.Collections.Concurrent;
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

    public static void Register(string methodName, int size)
    {
        PayloadSizes[methodName] = size;
    }

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
            // arm64-bench.yml does for its base ref) has no rows; it must not take the other classes' summary down.
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
