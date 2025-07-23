using System.Collections.Generic;
using System.Linq;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Reports;
using BenchmarkDotNet.Running;
using Perfolizer.Metrology;

namespace Nino.Benchmark;

public class PayloadColumnAttribute() : ColumnConfigBaseAttribute(new PayloadColumn());

public class PayloadColumn : IColumn
{
    public string Id => nameof(PayloadColumn);
    public string ColumnName => "Payload";
    public bool AlwaysShow => true;
    public ColumnCategory Category => ColumnCategory.Custom;
    public int PriorityInCategory => 0;
    public bool IsNumeric => true;
    public UnitType UnitType => UnitType.Size;
    public string Legend => "Serialized payload size (from setup)";

    public bool IsAvailable(Summary summary) => true;
    public bool IsDefault(Summary summary, BenchmarkCase benchmarkCase) => false;

    public string GetValue(Summary summary, BenchmarkCase benchmarkCase)
    {
        var methodName = benchmarkCase.Descriptor.WorkloadMethod.Name;
        if (SimpleTest.PayloadMap.TryGetValue(methodName, out var size))
            return new SizeValue(size).ToString();

        return "-";
    }

    public string GetValue(Summary summary, BenchmarkCase benchmarkCase, SummaryStyle style)
        => GetValue(summary, benchmarkCase);
}