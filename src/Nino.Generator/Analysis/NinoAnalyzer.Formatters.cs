using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;
using Nino.Generator.Generation;
using Nino.Generator.Common;

namespace Nino.Generator.Analysis;

public sealed partial class NinoAnalyzer
{
    /// <summary>
    /// One of Nino.Core's generic formatters made by hand (<c>new UnmanagedFormatter&lt;Vector&lt;int&gt;&gt;()</c>,
    /// <c>default(...)</c>) whose value is or holds <c>Vector&lt;T&gt;</c> (NINO029): it throws when used. A value
    /// stored straight into a field, property or local of its own type is reported there instead
    /// (<see cref="AnalyzeFormatterStorage"/>), once.
    /// </summary>
    private static void AnalyzeFormatter(OperationAnalysisContext context, IAssemblySymbol core)
    {
        // Cheapest rejection first: a generic type of Nino.Core.
        if (!IsCoreGeneric(context.Operation.Type, core, out var formatter) ||
            StoredIntoItsOwnType(context.Operation, formatter))
            return;
        ReportFormatter(context.ReportDiagnostic, context.Compilation, context.Operation.Syntax, formatter,
            $"The value of this {Display(formatter)}");
    }

    /// <summary>
    /// A field or property whose type is one of Nino.Core's generic formatters over <c>Vector&lt;T&gt;</c> (NINO029):
    /// a formatter struct needs no constructor call (a field of one is ready to use as it is), so the declaration is
    /// reported, not only the <c>new</c> or <c>default</c> that may fill it.
    /// </summary>
    private static void AnalyzeFormatterStorage(SymbolAnalysisContext context, IAssemblySymbol core)
    {
        var symbol = context.Symbol;
        var type = symbol switch
        {
            IFieldSymbol { IsImplicitlyDeclared: false } field => field.Type,
            IPropertySymbol property => property.Type,
            _ => null,
        };
        if (!IsCoreGeneric(type, core, out var formatter) ||
            symbol.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(context.CancellationToken) is not { } syntax)
            return;
        // A serialized member of a [NinoType] whose type holds the vector is reported by the member rule, once.
        var facts = TypeFacts.For(context.Compilation, syntax.SyntaxTree.Options);
        if (symbol.ContainingType.GetNinoTypeOptions() is { } attr &&
            !facts.Symbols.HasCustomFormatter(symbol) && facts.Raw.HostSizedVectorIn(formatter) != null &&
            facts.Members.Of(symbol.ContainingType, attr).Contains(symbol, SymbolEqualityComparer.Default))
            return;
        ReportFormatter(context.ReportDiagnostic, context.Compilation, syntax, formatter,
                $"The {Display(formatter)} in '{symbol.ContainingType.Name}.{symbol.Name}'", symbol.Locations[0]);
    }

    /// <summary><see cref="AnalyzeFormatterStorage"/> for a local (<c>UnmanagedFormatter&lt;Vector&lt;int&gt;&gt; f;</c>).</summary>
    private static void AnalyzeFormatterLocal(OperationAnalysisContext context, IAssemblySymbol core)
    {
        var local = ((IVariableDeclaratorOperation)context.Operation).Symbol;
        if (IsCoreGeneric(local.Type, core, out var formatter))
            ReportFormatter(context.ReportDiagnostic, context.Compilation, context.Operation.Syntax, formatter,
                $"The {Display(formatter)} in '{local.Name}'", local.Locations[0]);
    }

    private static bool IsCoreGeneric(ITypeSymbol? type, IAssemblySymbol core, out INamedTypeSymbol formatter)
    {
        formatter = (type as INamedTypeSymbol)!;
        return formatter is { IsGenericType: true } &&
               SymbolEqualityComparer.Default.Equals(formatter.ContainingAssembly, core);
    }

    private static string Display(INamedTypeSymbol formatter) =>
        formatter.OriginalDefinition.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);

    /// <summary>
    /// Whether <paramref name="value"/> goes straight into a field, property or local declared in source with its own
    /// type <paramref name="formatter"/>, whose declaration NINO029 reports.
    /// </summary>
    private static bool StoredIntoItsOwnType(IOperation value, INamedTypeSymbol formatter)
    {
        // A default literal reaches its target through an implicit conversion.
        while (value.Parent is IConversionOperation { IsImplicit: true } conversion)
            value = conversion;
        ISymbol? target = value.Parent switch
        {
            IFieldInitializerOperation initializer => initializer.InitializedFields.FirstOrDefault(),
            IPropertyInitializerOperation initializer => initializer.InitializedProperties.FirstOrDefault(),
            IVariableInitializerOperation { Parent: IVariableDeclaratorOperation declarator } => declarator.Symbol,
            ISimpleAssignmentOperation assignment when assignment.Value == value => assignment.Target switch
            {
                IFieldReferenceOperation field => field.Field,
                IPropertyReferenceOperation property => property.Property,
                ILocalReferenceOperation local => local.Local,
                _ => null,
            },
            _ => null,
        };
        var type = target switch
        {
            IFieldSymbol field => field.Type,
            IPropertySymbol property => property.Type,
            ILocalSymbol local => local.Type,
            _ => null,
        };
        return type != null && SymbolEqualityComparer.Default.Equals(type, formatter) &&
               target!.Locations.Any(static l => l.IsInSource);
    }

    private static void ReportFormatter(System.Action<Diagnostic> report, Compilation compilation, SyntaxNode syntax,
        INamedTypeSymbol formatter, string what, Location? location = null)
    {
        if (TypeFacts.For(compilation, syntax.SyntaxTree.Options).Raw.HostSizedVectorWrittenBy(formatter, out var type)
                is { } vector && type != null)
            report(Diagnostic.Create(NinoDiagnostics.HostSizedVector, location ?? syntax.GetLocation(), what,
                type.ToDisplayString(), NinoDiagnostics.HoldsVector(type, vector)));
    }

    private static RawRules Raw(OperationAnalysisContext context, IOperation operation) =>
        TypeFacts.For(context.Compilation, operation.Syntax.SyntaxTree.Options).Raw;
}
