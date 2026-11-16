using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Testing;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nino.Tests;
using Verify = Microsoft.CodeAnalysis.CSharp.Testing.CSharpAnalyzerVerifier<
    Nino.Generator.Analysis.NinoAnalyzer,
    Microsoft.CodeAnalysis.Testing.DefaultVerifier>;

namespace Nino.UnitTests;

[TestClass]
public partial class AnalyzerTests
{
    private CSharpAnalyzerTest<Generator.Analysis.NinoAnalyzer, DefaultVerifier> SetUpAnalyzerTest(string code,
        params DiagnosticResult[] diagnostics)
    {
        var test = new CSharpAnalyzerTest<Generator.Analysis.NinoAnalyzer, DefaultVerifier>
        {
            TestState =
            {
                Sources = { code }
            }
        };

        if (diagnostics.Length > 0)
        {
            test.TestState.ExpectedDiagnostics.AddRange(diagnostics);
        }

        TestReferences.Apply(test.TestState);

        return test;
    }

    /// <summary>
    /// The release tracking files (AnalyzerReleases.*.md) list every rule with its severity and its title, as the
    /// descriptors declare them.
    /// </summary>
    [TestMethod]
    public void ReleaseTrackingMatchesTheDescriptors()
    {
        string dir = RepositoryFiles.Of(System.IO.Path.Combine("src", "Nino.Generator"));
        var listed = new System.Collections.Generic.Dictionary<string, (string Severity, string Title)>();
        foreach (string file in new[] { "AnalyzerReleases.Shipped.md", "AnalyzerReleases.Unshipped.md" })
        {
            foreach (string line in System.IO.File.ReadAllLines(System.IO.Path.Combine(dir, file)))
            {
                var cells = line.Split('|');
                if (cells.Length == 4 && cells[0].Trim().StartsWith("NINO", System.StringComparison.Ordinal))
                    listed.Add(cells[0].Trim(), (cells[2].Trim(), cells[3].Trim()));
            }
        }

        var descriptors = new Generator.Analysis.NinoAnalyzer().SupportedDiagnostics;
        foreach (var d in descriptors)
        {
            Assert.IsTrue(listed.TryGetValue(d.Id, out var entry), $"{d.Id} is not listed");
            Assert.AreEqual(d.DefaultSeverity.ToString(), entry.Severity, d.Id);
            Assert.AreEqual(d.Title.ToString(), entry.Title, d.Id);
        }
    }
}
