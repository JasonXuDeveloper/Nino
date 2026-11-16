using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Testing;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using VerifyCF = Microsoft.CodeAnalysis.CSharp.Testing.CSharpCodeFixVerifier<
    Nino.Generator.Analysis.NinoAnalyzer,
    Nino.Generator.NinoCodeFixProvider,
    Microsoft.CodeAnalysis.Testing.DefaultVerifier>;

namespace Nino.UnitTests;

[TestClass]
public partial class CodeFixTests
{
    private CSharpCodeFixTest<Generator.Analysis.NinoAnalyzer, Generator.NinoCodeFixProvider, DefaultVerifier>
        SetUpCodeFixTest(string code, string fixedCode, params DiagnosticResult[] diagnostics)
    {
        var test = new CSharpCodeFixTest<Generator.Analysis.NinoAnalyzer, Generator.NinoCodeFixProvider, DefaultVerifier>
        {
            TestState =
            {
                Sources = { code }
            },
            FixedState =
            {
                Sources = { fixedCode }
            }
        };

        if (diagnostics.Length > 0)
        {
            test.TestState.ExpectedDiagnostics.AddRange(diagnostics);
            // No fix offered: the diagnostics stay, and nothing is expected to change.
            if (code == fixedCode)
                test.FixedState.ExpectedDiagnostics.AddRange(diagnostics);
        }

        TestReferences.Apply(test.TestState);
        TestReferences.Apply(test.FixedState);

        return test;
    }
}
