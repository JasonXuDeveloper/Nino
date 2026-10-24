using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Nino.UnitTests;

/// <summary>
/// Files of the repository the tests check (templates' output, release tracking). Found by walking up from the test
/// assembly to Nino.slnx, not through [CallerFilePath]: CI builds map source paths to /_/ (ContinuousIntegrationBuild).
/// </summary>
internal static class RepositoryFiles
{
    private static readonly string? Root = Find();

    private static string? Find()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Nino.slnx")))
                return dir.FullName;
        return null;
    }

    /// <summary>The path of a file under the repository root; inconclusive when the tests run without the sources.</summary>
    public static string Of(string relative)
    {
        if (Root == null)
            Assert.Inconclusive("the repository is not available (the tests run without the sources)");
        return Path.Combine(Root, relative);
    }
}
