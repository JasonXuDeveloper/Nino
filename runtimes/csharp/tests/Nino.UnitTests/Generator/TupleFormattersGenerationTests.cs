using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nino.CodeGen;
using Nino.Tests;

namespace Nino.UnitTests;

/// <summary>The checked-in output of runtimes/csharp/tools/CodeGen must match its template (compiled in from runtimes/csharp/tools/CodeGen).</summary>
[TestClass]
public class TupleFormattersGenerationTests
{
    [TestMethod]
    public void CheckedInFileMatchesTheTemplate()
    {
        string path = RepositoryFiles.CSharp(TupleFormattersTemplate.OutputPath);

        string expected = TupleFormattersTemplate.Generate();
        string actual = File.ReadAllText(path).Replace("\r\n", "\n");
        Assert.AreEqual(expected, actual,
            "TupleFormatters.g.cs is out of date: run `dotnet run --project runtimes/csharp/tools/CodeGen`.");
    }
}
