using System;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace Nino.Generator.Generation;

/// <summary>
/// Receives the diagnostics found while a model is built, and those the analyzer finds itself. The analyzer passes
/// one to <see cref="ModelBuilder"/>; the generator passes none, so models carry no locations and an edit that only
/// moves code re-emits nothing.
/// </summary>
internal sealed class DiagnosticSink
{
    private readonly Action<Diagnostic> _report;

    public DiagnosticSink(Action<Diagnostic> report) => _report = report;

    /// <summary>Reports <paramref name="descriptor"/> at the first location of <paramref name="at"/>.</summary>
    public void Report(DiagnosticDescriptor descriptor, ISymbol at, params object[] args) =>
        _report(Diagnostic.Create(descriptor, at.Locations.FirstOrDefault(), args));
}
