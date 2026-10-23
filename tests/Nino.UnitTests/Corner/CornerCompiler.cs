using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Nino.Core;

#nullable disable
namespace Nino.UnitTests.Corner;

/// <summary>
/// Compiles a source snippet with the Nino generator and analyzer in memory, for corner cases that cannot live in
/// the test project itself (they do not compile, or must come from another assembly like a hot-update dll).
/// </summary>
internal static class CornerCompiler
{
    public sealed class Result
    {
        public ImmutableArray<Diagnostic> AnalyzerDiagnostics;
        public ImmutableArray<Diagnostic> Errors;
        public string GeneratedCode;
        public Assembly Assembly;
        public byte[] Image;

        public string ErrorText => string.Join("\n", Errors);
        public bool HasAnalyzer(string id) => AnalyzerDiagnostics.Any(d => d.Id == id);
    }

    public static Result Compile(string source, bool allowUnsafe = false, bool load = true,
        string[] preprocessorSymbols = null, params Assembly[] extraReferences) =>
        Compile(source, allowUnsafe, load, preprocessorSymbols,
            extraReferences.Select(extra => (MetadataReference)MetadataReference.CreateFromFile(extra.Location)));

    /// <summary>
    /// Compiles <paramref name="source"/> (not loaded) against the images of earlier results, which it sees as
    /// metadata, as a package or command-line reference does.
    /// </summary>
    public static Result CompileAgainst(string source, params Result[] references) =>
        Compile(source, false, false, null, references.Select(r => (MetadataReference)MetadataReference.CreateFromImage(r.Image)));

    private static Result Compile(string source, bool allowUnsafe, bool load, string[] preprocessorSymbols,
        System.Collections.Generic.IEnumerable<MetadataReference> extraReferences)
    {
        var parse = new CSharpParseOptions(LanguageVersion.Latest, preprocessorSymbols: preprocessorSymbols);
        var refs = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p)).ToList();
        refs.Add(MetadataReference.CreateFromFile(typeof(NinoTypeAttribute).Assembly.Location));
        refs.AddRange(extraReferences);
        var compilation = CSharpCompilation.Create("Corner" + Guid.NewGuid().ToString("N"),
            new[] { CSharpSyntaxTree.ParseText(source, parse) }, refs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: allowUnsafe,
                nullableContextOptions: NullableContextOptions.Disable));

        var run = CSharpGeneratorDriver.Create(new Generator.Generation.NinoGenerator())
            .RunGenerators(compilation).GetRunResult();
        var output = compilation.AddSyntaxTrees(run.GeneratedTrees);

        var analyzers = ImmutableArray.Create<DiagnosticAnalyzer>(new Generator.Analysis.NinoAnalyzer());
        var result = new Result
        {
            AnalyzerDiagnostics = compilation.WithAnalyzers(analyzers).GetAnalyzerDiagnosticsAsync().Result,
            GeneratedCode = string.Concat(run.GeneratedTrees.Select(t => t.ToString())),
        };

        using var stream = new MemoryStream();
        var emit = output.Emit(stream);
        result.Errors = emit.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ToImmutableArray();
        if (emit.Success)
            result.Image = stream.ToArray();
        if (emit.Success && load)
            result.Assembly = Assembly.Load(stream.ToArray());
        return result;
    }
}
