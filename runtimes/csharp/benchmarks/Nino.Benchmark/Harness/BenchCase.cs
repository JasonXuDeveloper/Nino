using System;

namespace Nino.Benchmark;

/// <summary>
/// A benchmark case of the class: BenchGen (runtimes/csharp/tools/BenchGen) writes its timed methods, one serialize
/// and one deserialize per library of Adapters/ that can express it, into the class's <c>.Cases.g.cs</c> file, named
/// <c>&lt;Library&gt;Serialize&lt;Name&gt;</c> and <c>&lt;Library&gt;Deserialize&lt;Name&gt;</c>, in the order of these
/// attributes. Each property is a format: <c>{Case}</c>, <c>{Library}</c>, <c>{Slot}</c> (the library's place in a
/// <c>Serialized&lt;Case&gt;</c> array) and the names of other properties are put in; one the case leaves out is the
/// class's (<see cref="BenchClassAttribute"/>).
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public sealed class BenchCaseAttribute(string name) : Attribute
{
    public string Name { get; } = name;

    /// <summary>The static field that holds the value (the class's <see cref="BenchClassAttribute.Value"/>).</summary>
    public string? Value { get; set; }

    /// <summary>Google.Protobuf's message for the value (a type of the namespace alias P); none: no Google.Protobuf rows.</summary>
    public string? Proto { get; set; }

    /// <summary>System.Text.Json's type info, a property of <see cref="JsonContext"/>; none: no System.Text.Json rows.</summary>
    public string? Json { get; set; }

    /// <summary>The source-generated System.Text.Json context of <see cref="Json"/> (the class's).</summary>
    public string? JsonContext { get; set; }

    /// <summary>
    /// The static field that holds the previous version of the value: the deserialize methods read its bytes
    /// (<see cref="BenchClassAttribute.OldBytes"/>), with <see cref="OldProto"/> and <see cref="OldJson"/>.
    /// </summary>
    public string? Old { get; set; }

    public string? OldProto { get; set; }

    public string? OldJson { get; set; }

    /// <summary>Deserialize into the object Setup read first, with each library that can (its DeserializeInto).</summary>
    public bool InPlace { get; set; }

    /// <summary>The v3 build's generated serializers (NinoGen; V3Versioned.NinoGen for a version-tolerant model).</summary>
    public string? NinoGen { get; set; }
}

/// <summary>What every <see cref="BenchCaseAttribute"/> of the class reads unless it says otherwise.</summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class BenchClassAttribute : Attribute
{
    /// <summary>The capacity of each library's buffer, a C# expression (320 * 1024).</summary>
    public string? Buffer { get; set; }

    /// <summary>The static field of a case's value ({Case}).</summary>
    public string? Value { get; set; }

    /// <summary>
    /// Where each library's bytes of a case go: an array per case (Serialized{Case}[{Slot}]) or a field per library
    /// and case ({Library}{Case}Payload).
    /// </summary>
    public string? Bytes { get; set; }

    /// <summary>The field of a library's bytes of a case's previous version ({Library}{Case}OldBytes).</summary>
    public string? OldBytes { get; set; }

    /// <summary>The order of the methods: verb (every case's Serialize methods, then the Deserialize ones) or case.</summary>
    public string? Order { get; set; }

    /// <summary>System.Text.Json's source-generated context (CaseJson).</summary>
    public string? JsonContext { get; set; }
}
