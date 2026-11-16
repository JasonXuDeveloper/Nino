#if NET11_0_OR_GREATER
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Verify = Microsoft.CodeAnalysis.CSharp.Testing.CSharpAnalyzerVerifier<
    Nino.Generator.Analysis.NinoAnalyzer,
    Microsoft.CodeAnalysis.Testing.DefaultVerifier>;

namespace Nino.UnitTests;

// C# 15 unions (NINO045): parsed with the test projects' Roslyn (NinoTestRoslynVersion), the attribute and interface
// the compiler needs declared as the .NET 11 runtime declares them (the reference assemblies the tests use predate
// them).
public partial class AnalyzerTests
{
    private const string UnionRuntime = @"
namespace System.Runtime.CompilerServices
{
    [System.AttributeUsage(System.AttributeTargets.Class | System.AttributeTargets.Struct, AllowMultiple = false, Inherited = false)]
    public sealed class UnionAttribute : System.Attribute { }

    public interface IUnion
    {
        object Value { get; }
    }
}
";

    private Task RunUnionTest(string code, params Microsoft.CodeAnalysis.Testing.DiagnosticResult[] diagnostics)
    {
        var test = SetUpAnalyzerTest(code, diagnostics);
        test.TestState.Sources.Add(UnionRuntime);
        test.SolutionTransforms.Add((solution, projectId) =>
        {
            var options = (CSharpParseOptions)solution.GetProject(projectId)!.ParseOptions!;
            return solution.WithProjectParseOptions(projectId, options.WithLanguageVersion(LanguageVersion.Preview));
        });
        return test.RunAsync();
    }

    [TestMethod]
    public async Task NINO045_UnsupportedUnionCase()
    {
        var code = @"
using Nino.Core;

public delegate void Callback();

[NinoType]
public union {|#0:Bad|}(int, Callback);

[NinoType]
public union Good(int, string);
";

        await RunUnionTest(code,
            Verify.Diagnostic("NINO045").WithLocation(0).WithArguments("Callback", "Bad"));
    }
}
#endif
