using System;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Reports;
using BenchmarkDotNet.Running;
using Perfolizer.Metrology;


namespace Nino.Benchmark;
public class PayloadColumnAttribute : ColumnConfigBaseAttribute
{
    public PayloadColumnAttribute()
        : base(new PayloadColumn())
    {
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

    public string Legend => "Payload size";

    public string GetValue(Summary summary, BenchmarkCase benchmarkCase)
    {
        var methodInfo = benchmarkCase.Descriptor.WorkloadMethod;

        if (methodInfo.ReturnType == typeof(int))
        {
            var instance = Activator.CreateInstance(benchmarkCase.Descriptor.Type);
            var result = (int)methodInfo.Invoke(instance, null)!;
            return new SizeValue(result).ToString();
        }
        else
        {
            return "-";
        }
    }

    public string GetValue(Summary summary, BenchmarkCase benchmarkCase, SummaryStyle style)
    {
        return GetValue(summary, benchmarkCase);
    }

    public bool IsAvailable(Summary summary)
    {
        return true;
    }

    public bool IsDefault(Summary summary, BenchmarkCase benchmarkCase)
    {
        return false;
    }
}