using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Testing;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using VerifyCF = Microsoft.CodeAnalysis.CSharp.Testing.CSharpCodeFixVerifier<
    Nino.Generator.Analysis.NinoAnalyzer,
    Nino.Generator.NinoCodeFixProvider,
    Microsoft.CodeAnalysis.Testing.DefaultVerifier>;

namespace Nino.UnitTests;

// WEAK_VERSION_TOLERANCE does not make types version tolerant (NINO032).
public partial class CodeFixTests
{
    /// <summary>A test whose project defines WEAK_VERSION_TOLERANCE, before the fix and after it.</summary>
    private CSharpCodeFixTest<Generator.Analysis.NinoAnalyzer, Generator.NinoCodeFixProvider, DefaultVerifier>
        WeakToleranceTest(string code, string fixedCode, params DiagnosticResult[] diagnostics)
    {
        var test = SetUpCodeFixTest(code, fixedCode, diagnostics);
        test.SolutionTransforms.Add((solution, project) => solution.WithProjectParseOptions(project,
            ((Microsoft.CodeAnalysis.CSharp.CSharpParseOptions)solution.GetProject(project)!.ParseOptions!)
            .WithPreprocessorSymbols("WEAK_VERSION_TOLERANCE")));
        return test;
    }

    private const string KeepBytes = "add format: NinoFormat.VersionTolerant to its [NinoType]";

    [TestMethod]
    public async Task NINO032_MakeVersionTolerant()
    {
        // Each positional NinoType gets the format the symbol gave it; keyed and tolerant ones, and types that take
        // their [NinoType] from one of this project's types, keep theirs.
        var code = @"
using Nino.Core;

[{|#0:NinoType|}]
public class Plain
{
    public int A;
}

public class Derived : Plain
{
    public int B;
}

[{|#1:NinoType(false)|}]
public struct Point
{
    [NinoMember(0)] public int X;
    [NinoMember(1)] public int Y;
}

[{|#2:Nino.Core.NinoType(true, false, true, Nino.Core.NinoFormat.Positional)|}]
public interface IShape
{
}

[NinoType(format: NinoFormat.Keyed)]
public class Keyed
{
    public int A;
}

[NinoType(format: NinoFormat.VersionTolerant)]
public class Tolerant
{
    public int A;
}";

        var fixedCode = @"
using Nino.Core;

[NinoType(format: NinoFormat.VersionTolerant)]
public class Plain
{
    public int A;
}

public class Derived : Plain
{
    public int B;
}

[NinoType(false, format: NinoFormat.VersionTolerant)]
public struct Point
{
    [NinoMember(0)] public int X;
    [NinoMember(1)] public int Y;
}

[Nino.Core.NinoType(true, false, true, NinoFormat.VersionTolerant)]
public interface IShape
{
}

[NinoType(format: NinoFormat.Keyed)]
public class Keyed
{
    public int A;
}

[NinoType(format: NinoFormat.VersionTolerant)]
public class Tolerant
{
    public int A;
}";

        await WeakToleranceTest(code, fixedCode,
            VerifyCF.Diagnostic("NINO032").WithLocation(0).WithArguments("Plain", KeepBytes),
            VerifyCF.Diagnostic("NINO032").WithLocation(1).WithArguments("Point", KeepBytes),
            VerifyCF.Diagnostic("NINO032").WithLocation(2).WithArguments("IShape", KeepBytes)).RunAsync();
    }

    [TestMethod]
    public async Task NINO032_DeclaresTheInheritedOptions()
    {
        // The [NinoType] comes from another assembly (built without the symbol): the type declares one with the
        // options it took, and the format.
        var code = @"
using Nino.Core;

public partial class {|#0:Derived|} : Nino.UnitTests.Subset.SubsetClassWithPrivateField
{
    [NinoMember(10)] public int Extra;
}";

        var fixedCode = @"
using Nino.Core;

[NinoType(autoCollect: false, includeNonPublicMembers: true, format: NinoFormat.VersionTolerant)]
public partial class Derived : Nino.UnitTests.Subset.SubsetClassWithPrivateField
{
    [NinoMember(10)] public int Extra;
}";

        const string attribute = "NinoType(autoCollect: false, includeNonPublicMembers: true, format: NinoFormat.VersionTolerant)";
        var test = WeakToleranceTest(code, fixedCode,
            VerifyCF.Diagnostic("NINO032").WithLocation(0).WithArguments("Derived",
                $"declare [{attribute}] on it (the options it takes from " +
                "'Nino.UnitTests.Subset.SubsetClassWithPrivateField' in another assembly)"));
        var subset = Microsoft.CodeAnalysis.MetadataReference.CreateFromFile(
            typeof(Nino.UnitTests.Subset.SubsetClassWithPrivateField).Assembly.Location);
        test.TestState.AdditionalReferences.Add(subset);
        test.FixedState.AdditionalReferences.Add(subset);
        await test.RunAsync();
    }

    [TestMethod]
    public async Task NINO032_NamesTheFormatAsTheFileCan()
    {
        // Without using Nino.Core (the attribute through an alias), the format is written qualified.
        var code = @"
using Tolerant = Nino.Core.NinoTypeAttribute;

[{|#0:Tolerant|}]
public class Plain
{
    public int A;
}";

        var fixedCode = @"
using Tolerant = Nino.Core.NinoTypeAttribute;

[Tolerant(format: Nino.Core.NinoFormat.VersionTolerant)]
public class Plain
{
    public int A;
}";

        await WeakToleranceTest(code, fixedCode,
            VerifyCF.Diagnostic("NINO032").WithLocation(0).WithArguments("Plain", KeepBytes)).RunAsync();
    }

    [TestMethod]
    public async Task NINO032_NotWithoutTheSymbol()
    {
        var code = @"
using Nino.Core;

[NinoType]
public class Plain
{
    public int A;
}";

        await SetUpCodeFixTest(code, code).RunAsync();
    }
}
