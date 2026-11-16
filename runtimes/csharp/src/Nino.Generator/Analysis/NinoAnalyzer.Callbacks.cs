using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Nino.Generator.Generation;

namespace Nino.Generator.Analysis;

public sealed partial class NinoAnalyzer
{
    private static void AnalyzeMethod(SymbolAnalysisContext context, NinoSymbols symbols)
    {
        var method = (IMethodSymbol)context.Symbol;
        if (method.GetAttributes().IsEmpty)
            return;
        ReportCallbacks(context, method, symbols);
        if (!NinoSymbols.Has(method, symbols.Factory))
            return;

        var errors = NinoSymbols.FactoryErrors(method);
        if (errors.Count > 0)
            context.ReportDiagnostic(Diagnostic.Create(NinoDiagnostics.InvalidFactory,
                method.Locations.FirstOrDefault(), method.Name, string.Join(", ", errors)));
    }

    /// <summary>
    /// NINO038-NINO040, NINO042 and NINO044: a method marked as a serialization callback that no type can call
    /// (<see cref="Callbacks.Problem"/>), at the attribute. What depends on the type calling it is reported with the
    /// type's model (<see cref="Callbacks.Build"/>), two methods for one callback with the type
    /// (<see cref="ReportDuplicateCallbacks"/>).
    /// </summary>
    private static void ReportCallbacks(SymbolAnalysisContext context, IMethodSymbol method, NinoSymbols symbols)
    {
        TypeFacts? facts = null;
        foreach (var (kind, attribute) in Callbacks.Marks(method, symbols))
        {
            facts ??= TypeFacts.For(context.Compilation, method.Locations.FirstOrDefault()?.SourceTree?.Options);
            if (Callbacks.Problem(method, kind, facts) is not { } problem)
                continue;
            var (rule, reason) = problem;
            string name = Callbacks.Display(method), callback = Callbacks.AttributeNames[(int)kind];
            var sink = new DiagnosticSink(context.ReportDiagnostic);
            if (rule == NinoDiagnostics.CallbackInaccessible)
                sink.Report(rule, attribute, method, name, callback, reason, method.ContainingType.ToDisplayString(),
                    "make it internal or public");
            else if (rule == NinoDiagnostics.CallbackOnRawStruct)
                sink.Report(rule, attribute, method, name, callback, method.ContainingType.ToDisplayString());
            else if (rule == NinoDiagnostics.CallbackStatic)
                sink.Report(rule, attribute, method, name, callback);
            else
                sink.Report(rule, attribute, method, name, callback, reason);
        }
    }

    /// <summary>NINO041: a second method <paramref name="type"/> marks as one callback, at its attribute.</summary>
    private static void ReportDuplicateCallbacks(DiagnosticSink sink, INamedTypeSymbol type, NinoSymbols symbols)
    {
        foreach (var (method, first, kind) in Callbacks.Duplicates(type, symbols))
        {
            string callback = Callbacks.AttributeNames[(int)kind];
            sink.Report(NinoDiagnostics.CallbackDuplicate, NinoSymbols.Get(method, symbols.Callbacks[(int)kind])!,
                method, type.ToDisplayString(), first.Name, method.Name, callback);
        }
    }
}
