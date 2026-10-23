using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nino.Core;

#nullable disable
namespace Nino.UnitTests;

/// <summary>
/// In-memory compilations for tests that run the Nino generator on source of their own: the running runtime's
/// assemblies and Nino.Core as references, then the generator, then the image emitted and loaded. Analyzer and
/// code-fix tests (Microsoft.CodeAnalysis.Testing) take their references from <see cref="TestReferences"/> instead.
/// </summary>
internal static class TestCompilation
{
    public static readonly CSharpParseOptions Parse = new CSharpParseOptions(LanguageVersion.Latest);

    // Created once: a MetadataReference is immutable, and reading every platform assembly per test is slow.
    private static readonly Lazy<MetadataReference[]> Platform = new Lazy<MetadataReference[]>(() =>
        ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")).Split(Path.PathSeparator)
        .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
        .Append(MetadataReference.CreateFromFile(typeof(NinoTypeAttribute).Assembly.Location))
        .ToArray());

    /// <summary>The running runtime's assemblies, Nino.Core and <paramref name="extra"/>.</summary>
    public static List<MetadataReference> References(IEnumerable<MetadataReference> extra = null)
    {
        var references = Platform.Value.ToList();
        if (extra != null)
            references.AddRange(extra);
        return references;
    }

    /// <summary>
    /// A library of <paramref name="sources"/> (parsed with <see cref="Parse"/>), named <paramref name="prefix"/> and
    /// a new GUID: types of one name in two loaded assemblies stay apart, and so do the generator's cache entries.
    /// </summary>
    public static CSharpCompilation Create(string prefix, IEnumerable<string> sources, bool allowUnsafe = true,
        IEnumerable<MetadataReference> extraReferences = null) =>
        Create(prefix + Guid.NewGuid().ToString("N"), sources.Select(s => CSharpSyntaxTree.ParseText(s, Parse)),
            allowUnsafe, extraReferences);

    /// <summary>A library named <paramref name="name"/>, of <paramref name="trees"/>.</summary>
    public static CSharpCompilation Create(string name, IEnumerable<SyntaxTree> trees, bool allowUnsafe = true,
        IEnumerable<MetadataReference> extraReferences = null) =>
        CSharpCompilation.Create(name, trees, References(extraReferences),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: allowUnsafe));

    /// <summary>Runs the Nino generator on <paramref name="compilation"/>.</summary>
    public static GeneratorDriverRunResult Generate(Compilation compilation) =>
        CSharpGeneratorDriver.Create(new Generator.Generation.NinoGenerator()).RunGenerators(compilation)
            .GetRunResult();

    /// <summary>Emits <paramref name="compilation"/>, failing the test with its errors, and loads it.</summary>
    public static Assembly EmitAndLoad(Compilation compilation)
    {
        using var stream = new MemoryStream();
        var result = compilation.Emit(stream);
        Assert.IsTrue(result.Success,
            string.Join("\n", result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
        return Assembly.Load(stream.ToArray());
    }
}
