using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Testing;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Verify = Microsoft.CodeAnalysis.CSharp.Testing.CSharpAnalyzerVerifier<
    Nino.Generator.Analysis.NinoAnalyzer,
    Microsoft.CodeAnalysis.Testing.DefaultVerifier>;

namespace Nino.UnitTests;

/// <summary>
/// The serialization callbacks' rules (NINO038-NINO044) and their code fixes: every rule fires where a marked method
/// is never called, and stays quiet on every form Nino calls.
/// </summary>
[TestClass]
public class CallbackAnalyzerTests
{
    private static Task Analyze(string code, params DiagnosticResult[] expected)
    {
        var test = new CSharpAnalyzerTest<Generator.Analysis.NinoAnalyzer, DefaultVerifier>
        {
            TestState = { Sources = { code } },
        };
        test.TestState.ExpectedDiagnostics.AddRange(expected);
        TestReferences.Apply(test.TestState);
        return test.RunAsync();
    }

    private static Task Fix(string code, string fixedCode, params DiagnosticResult[] expected)
    {
        var test = new CSharpCodeFixTest<Generator.Analysis.NinoAnalyzer, Generator.NinoCodeFixProvider, DefaultVerifier>
        {
            TestState = { Sources = { code } },
            FixedState = { Sources = { fixedCode } },
        };
        test.TestState.ExpectedDiagnostics.AddRange(expected);
        if (code == fixedCode)
            test.FixedState.ExpectedDiagnostics.AddRange(expected);
        TestReferences.Apply(test.TestState);
        TestReferences.Apply(test.FixedState);
        return test.RunAsync();
    }

    [TestMethod]
    public async Task EveryFormNinoCallsReportsNothing()
    {
        var code = @"
using Nino.Core;

[NinoType]
public class Base
{
    public int A;
    [NinoOnSerializing] public void Saving() { }
    [NinoOnSerialized] internal void Saved() { }
    [NinoOnDeserializing] internal static void Loading() { }
    [NinoOnDeserialized] protected internal virtual void Loaded() { }
}

public class Derived : Base
{
    [NinoOnSerializing] internal void DerivedSaving() { }
    [NinoOnDeserializing] internal void DerivedLoading() { }
    protected internal override void Loaded() { }
}

[NinoType(format: NinoFormat.VersionTolerant)]
public struct Tolerant
{
    public int X;
    [NinoOnDeserializing, NinoOnDeserialized] internal void Both() { }
}

[NinoType]
public record Recreated(int A)
{
    [NinoOnDeserializing] internal static void Loading() { }
    [NinoOnDeserialized] internal void Loaded() { }
}

[NinoType]
public abstract class Shape
{
    public int Id;
    [NinoOnDeserialized] internal abstract void Loaded();
}

[NinoType]
public sealed class Generic<T>
{
    public T Value;
    [NinoOnDeserializing] internal static void Loading() { }
    [NinoOnSerializing] internal void Saving() { }
}
";
        await Analyze(code);
    }

    [TestMethod]
    public async Task NINO038_WrongSignature()
    {
        var code = @"
using Nino.Core;

[NinoType]
public class TestClass
{
    public int A;
    [{|#0:NinoOnSerializing|}] internal void WithParameter(int a) { }
    [{|#1:NinoOnSerialized|}] internal int Returns() => 0;
    [{|#2:NinoOnDeserialized|}] internal void Generic<T>() { }
}
";
        await Analyze(code,
            Verify.Diagnostic("NINO038").WithLocation(0).WithArguments("TestClass.WithParameter", "NinoOnSerializing",
                "takes parameters"),
            Verify.Diagnostic("NINO038").WithLocation(1).WithArguments("TestClass.Returns", "NinoOnSerialized",
                "returns 'int'"),
            Verify.Diagnostic("NINO038").WithLocation(2).WithArguments("TestClass.Generic", "NinoOnDeserialized",
                "is generic"));
    }

    [TestMethod]
    public async Task NINO039_NotAccessible_MadeInternal()
    {
        var code = @"
using Nino.Core;

[NinoType]
public class TestClass
{
    public int A;
    [{|#0:NinoOnSerializing|}] private void Saving() { }
    [{|#1:NinoOnDeserialized|}] protected void Loaded() { }
}
";
        var fixedCode = @"
using Nino.Core;

[NinoType]
public class TestClass
{
    public int A;
    [NinoOnSerializing] internal void Saving() { }
    [NinoOnDeserialized] internal void Loaded() { }
}
";
        await Fix(code, fixedCode,
            Verify.Diagnostic("NINO039").WithLocation(0).WithArguments("TestClass.Saving", "NinoOnSerializing",
                "private", "TestClass", "make it internal or public"),
            Verify.Diagnostic("NINO039").WithLocation(1).WithArguments("TestClass.Loaded", "NinoOnDeserialized",
                "protected", "TestClass", "make it internal or public"));
    }

    [TestMethod]
    public async Task NINO039_InternalToTheBasesAssembly()
    {
        const string library = @"
using Nino.Core;

[NinoType]
public class LibraryBase
{
    public int A;
    [NinoOnDeserialized] internal void Loaded() { }
}
";
        var code = @"
public class {|#0:Derived|} : LibraryBase
{
    public int B;
}
";
        var test = new CSharpAnalyzerTest<Generator.Analysis.NinoAnalyzer, DefaultVerifier>
        {
            TestState = { Sources = { code } },
        };
        test.TestState.AdditionalProjects["Library"].Sources.Add(library);
        test.TestState.AdditionalProjectReferences.Add("Library");
        TestReferences.Apply(test.TestState);
        TestReferences.Apply(test.TestState.AdditionalProjects["Library"]);
        test.TestState.ExpectedDiagnostics.Add(Verify.Diagnostic("NINO039").WithLocation(0).WithArguments(
            "LibraryBase.Loaded", "NinoOnDeserialized", "internal to Library", "Derived", "make it public"));
        await test.RunAsync();
    }

    [TestMethod]
    public async Task NINO040_NotANinoType_Removed()
    {
        var code = @"
using Nino.Core;

public class Plain
{
    [{|#0:NinoOnSerializing|}] internal void Saving() { }
}

[NinoType]
public interface IShape
{
    [{|#1:NinoOnDeserialized|}] void Loaded();
}
";
        var fixedCode = @"
using Nino.Core;

public class Plain
{
    internal void Saving() { }
}

[NinoType]
public interface IShape
{
    void Loaded();
}
";
        await Fix(code, fixedCode,
            Verify.Diagnostic("NINO040").WithLocation(0).WithArguments("Plain.Saving", "NinoOnSerializing",
                "'Plain' is not a [NinoType]"),
            Verify.Diagnostic("NINO040").WithLocation(1).WithArguments("IShape.Loaded", "NinoOnDeserialized",
                "is declared by interface 'IShape'"));
    }

    [TestMethod]
    public async Task NINO041_TwoMethodsForOneCallback()
    {
        var code = @"
using Nino.Core;

[NinoType]
public class TestClass
{
    public int A;
    [NinoOnDeserialized] internal void First() { }
    [{|#0:NinoOnDeserialized|}] internal void Second() { }
    [NinoOnSerializing] internal void Saving() { }
}

[NinoType]
public class Derived : TestClass
{
    // A level of its own: not a second method.
    [NinoOnDeserialized] internal void Third() { }
}
";
        await Analyze(code,
            Verify.Diagnostic("NINO041").WithLocation(0).WithArguments("TestClass", "First", "Second",
                "NinoOnDeserialized"));
    }

    [TestMethod]
    public async Task NINO042_StaticWhereTheValueIsNeeded()
    {
        var code = @"
using Nino.Core;

[NinoType]
public class TestClass
{
    public int A;
    [{|#0:NinoOnSerializing|}] internal static void Saving() { }
    [{|#1:NinoOnDeserialized|}] internal static void Loaded() { }
    [NinoOnDeserializing] internal static void Loading() { }
}
";
        await Analyze(code,
            Verify.Diagnostic("NINO042").WithLocation(0).WithArguments("TestClass.Saving", "NinoOnSerializing"),
            Verify.Diagnostic("NINO042").WithLocation(1).WithArguments("TestClass.Loaded", "NinoOnDeserialized"));
    }

    [TestMethod]
    public async Task NINO043_InstanceBeforeTheMembersOfARecreatedType()
    {
        var code = @"
using Nino.Core;

[NinoType]
public record Point(int X, int Y)
{
    [NinoOnDeserializing] internal void {|#0:Loading|}() { }
}

[NinoType]
public class InitOnly
{
    public int A { get; init; }
    [NinoOnDeserializing] internal void {|#1:Loading|}() { }
}

[NinoType]
public class Base
{
    public int A;
    [NinoOnDeserializing] internal void Loading() { }
}

[NinoType]
public class {|#2:Constructed|} : Base
{
    public readonly int B;
    public Constructed(int a, int b) { A = a; B = b; }
}
";
        await Analyze(code,
            Verify.Diagnostic("NINO043").WithLocation(0).WithArguments("Point.Loading", "Point",
                "a constructor that takes them"),
            Verify.Diagnostic("NINO043").WithLocation(1).WithArguments("InitOnly.Loading", "InitOnly",
                "it has init-only or readonly members"),
            Verify.Diagnostic("NINO043").WithLocation(2).WithArguments("Base.Loading", "Constructed",
                "a constructor that takes them"));
    }

    [TestMethod]
    public async Task NINO044_RawStruct()
    {
        var code = @"
using Nino.Core;

[NinoType]
public struct Raw
{
    public int X;
    public float Y;
    [{|#0:NinoOnDeserialized|}] internal void Loaded() { }
}

// Version tolerant: written member by member, so its callbacks run.
[NinoType(format: NinoFormat.VersionTolerant)]
public struct Tolerant
{
    public int X;
    [NinoOnDeserialized] internal void Loaded() { }
}
";
        await Analyze(code,
            Verify.Diagnostic("NINO044").WithLocation(0).WithArguments("Raw.Loaded", "NinoOnDeserialized", "Raw"));
    }
}
