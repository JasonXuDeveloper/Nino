using System.IO;
using System.Runtime.CompilerServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nino.CodeGen;

namespace Nino.UnitTests;

/// <summary>The checked-in output of tools/CodeGen must match its template (compiled in from tools/CodeGen).</summary>
[TestClass]
public class TupleFormattersGenerationTests
{
    private static string RepositoryRoot([CallerFilePath] string path = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, "..", ".."));

    [TestMethod]
    public void CheckedInFileMatchesTheTemplate()
    {
        string path = Path.Combine(RepositoryRoot(), TupleFormattersTemplate.OutputPath);
        if (!File.Exists(path))
            Assert.Inconclusive($"{path} is not available (the tests run without the sources)");

        string expected = TupleFormattersTemplate.Generate();
        string actual = File.ReadAllText(path).Replace("\r\n", "\n");
        Assert.AreEqual(expected, actual,
            "TupleFormatters.g.cs is out of date: run `dotnet run --project tools/CodeGen` from the repository root.");
    }
}
