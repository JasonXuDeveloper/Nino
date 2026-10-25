using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nino.CodeGen;
using Nino.Tests;

namespace Nino.UnitTests;

/// <summary>The checked-in multidimensional array formatters must match their template (compiled in from tools/CodeGen).</summary>
[TestClass]
public class MultiDimFormattersGenerationTests
{
    [TestMethod]
    public void CheckedInFileMatchesTheTemplate()
    {
        string path = RepositoryFiles.Of(MultiDimFormattersTemplate.OutputPath);

        string expected = MultiDimFormattersTemplate.Generate();
        string actual = File.ReadAllText(path).Replace("\r\n", "\n");
        Assert.AreEqual(expected, actual,
            "MultiDimFormatters.g.cs is out of date: run `dotnet run --project tools/CodeGen` from the repository root.");
    }
}
