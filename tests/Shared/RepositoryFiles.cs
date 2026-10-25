using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Nino.Tests;

/// <summary>
/// Files of the repository a test checks (templates' output, release tracking, generated files). The repository root
/// comes from the build (RepositoryFiles.props, as assembly metadata), not from [CallerFilePath], which CI builds map
/// to /_/ (ContinuousIntegrationBuild). Linked into each test project that imports RepositoryFiles.props.
/// </summary>
internal static class RepositoryFiles
{
    /// <summary>The repository root the tests were built from; null when it is not there (tests run without the sources).</summary>
    public static readonly string? Root = Find();

    private static string? Find()
    {
        string? root = typeof(RepositoryFiles).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .SingleOrDefault(a => a.Key == "NinoRepositoryRoot")?.Value;
        return root != null && File.Exists(Path.Combine(root, "Nino.slnx")) ? root : null;
    }

    /// <summary>The path of a file under the repository root; inconclusive when the tests run without the sources.</summary>
    public static string Of(string relative)
    {
        if (Root == null)
            Assert.Inconclusive("the repository is not available (the tests run without the sources)");
        return Path.Combine(Root!, relative);
    }
}
