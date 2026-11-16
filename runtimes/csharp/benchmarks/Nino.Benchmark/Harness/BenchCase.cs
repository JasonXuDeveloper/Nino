using System;

namespace Nino.Benchmark;

/// <summary>
/// A benchmark case of the class: BenchGen (runtimes/csharp/tools/BenchGen) writes its timed methods, one serialize
/// and one deserialize per library of Adapters/ that can express it, into the class's <c>.Cases.g.cs</c> file, named
/// <c>&lt;Library&gt;Serialize&lt;Name&gt;</c> and <c>&lt;Library&gt;Deserialize&lt;Name&gt;</c>. The value is the
/// class's static field named <see cref="Name"/>; the cases' methods follow the order of these attributes.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public sealed class BenchCaseAttribute(string name) : Attribute
{
    public string Name { get; } = name;

    /// <summary>Google.Protobuf's message for the value (a type of Nino.Benchmark.Proto); none: no Google.Protobuf rows.</summary>
    public string? Proto { get; set; }

    /// <summary>System.Text.Json's type info (a property of <c>CaseJson</c>); none: no System.Text.Json rows.</summary>
    public string? Json { get; set; }
}
