using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;
using Nino.Generator.Generation;
using Nino.Generator.Common;

namespace Nino.Generator.Analysis;

/// <summary>
/// Reports every Nino diagnostic: attribute usage, and what the generator would find while building a type's model
/// (the same <see cref="ModelBuilder"/>). The generator itself reports nothing, so its models carry no locations.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed partial class NinoAnalyzer : DiagnosticAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => NinoDiagnostics.All;

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(start =>
        {
            // Nothing to check in compilations that do not reference Nino (checked before building any facts).
            if (start.Compilation.GetTypeByMetadataName(NinoNames.NinoTypeAttributeMetadataName) == null)
                return;
            var symbols = TypeFacts.For(start.Compilation, start.Compilation.SyntaxTrees.FirstOrDefault()?.Options).Symbols;
            // Symbol actions run once per type, not once per partial declaration.
            start.RegisterSymbolAction(AnalyzeType, SymbolKind.NamedType);
            if (symbols.Factory != null || symbols.Callbacks.Any(static c => c != null))
                start.RegisterSymbolAction(c => AnalyzeMethod(c, symbols), SymbolKind.Method);
            start.RegisterSyntaxNodeAction(AnalyzeAssemblyAttributes, SyntaxKind.AttributeList);
            if (symbols.IFormatter?.ContainingAssembly is { } core)
            {
                start.RegisterOperationAction(c => AnalyzeCall(c, symbols), OperationKind.Invocation);
                start.RegisterOperationAction(c => AnalyzeFormatter(c, core), OperationKind.ObjectCreation,
                    OperationKind.DefaultValue);
                start.RegisterSymbolAction(c => AnalyzeFormatterStorage(c, core), SymbolKind.Field, SymbolKind.Property);
                start.RegisterOperationAction(c => AnalyzeFormatterLocal(c, core), OperationKind.VariableDeclarator);
            }
        });
    }

    // [assembly: NinoFormatter(...)] registrations: one that FormatterRules cannot use is otherwise silently ignored.
    // [assembly: NinoLayouts(...)]: the layout baseline, unreadable or holding types the code no longer has.
    private static void AnalyzeAssemblyAttributes(SyntaxNodeAnalysisContext context)
    {
        var list = (AttributeListSyntax)context.Node;
        if (list.Target?.Identifier.IsKind(SyntaxKind.AssemblyKeyword) != true)
            return;
        var facts = TypeFacts.For(context.Compilation, list.SyntaxTree.Options);
        var symbols = facts.Symbols;
        if (LayoutCheck.For(context.Compilation, facts) is { } layouts &&
            layouts.Attribute.ApplicationSyntaxReference is { } layoutsSyntax &&
            layoutsSyntax.SyntaxTree == list.SyntaxTree && list.Span.Contains(layoutsSyntax.Span))
            ReportBaseline(context, layouts);
        if (symbols.FormatterAttribute == null)
            return;
        foreach (var attribute in context.Compilation.Assembly.GetAttributes())
        {
            if (attribute.ApplicationSyntaxReference is not { } syntax || syntax.SyntaxTree != list.SyntaxTree ||
                !list.Span.Contains(syntax.Span) ||
                !SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, symbols.FormatterAttribute))
                continue;
            FormatterRules.Read(attribute, symbols, out var error);
            if (error == null)
                continue;
            var formatter = attribute.ConstructorArguments[0].Value as ITypeSymbol;
            context.ReportDiagnostic(Diagnostic.Create(NinoDiagnostics.InvalidFormatterRegistration,
                Location.Create(syntax.SyntaxTree, syntax.Span), formatter?.ToDisplayString(), error));
        }
    }

    /// <summary>
    /// A call whose value is or holds <c>Vector&lt;T&gt;</c> (NINO029): a top-level <c>NinoSerializer</c> /
    /// <c>NinoDeserializer</c> call, for which the generator registers and intercepts nothing, or a generic method of
    /// <c>Writer</c>, <c>Reader</c> or <c>NinoDirect</c> used by hand; each throws.
    /// </summary>
    private static void AnalyzeCall(OperationAnalysisContext context, NinoSymbols symbols)
    {
        var invocation = (IInvocationOperation)context.Operation;
        var method = invocation.TargetMethod;
        // Cheapest rejection first: a Serialize / Deserialize name or a generic method, then the class.
        bool serializes = method.Name.StartsWith("Serialize", System.StringComparison.Ordinal) ||
                          method.Name.StartsWith("Deserialize", System.StringComparison.Ordinal);
        if (!serializes && !method.IsGenericMethod)
            return;
        var owner = method.ContainingType;
        ITypeSymbol? type, vector;
        if (serializes && (SymbolEqualityComparer.Default.Equals(owner, symbols.Serializer) ||
                           SymbolEqualityComparer.Default.Equals(owner, symbols.Deserializer)))
        {
            // The type the call needs a formatter for, as the generator finds it.
            type = invocation.Syntax is InvocationExpressionSyntax call && invocation.SemanticModel is { } model
                ? symbols.CallType(method, call, model, context.CancellationToken)
                : null;
            vector = type == null ? null : Raw(context, invocation).HostSizedVectorIn(type);
        }
        else if (method.IsGenericMethod && (SymbolEqualityComparer.Default.Equals(owner, symbols.Writer) ||
                                            SymbolEqualityComparer.Default.Equals(owner, symbols.Reader) ||
                                            SymbolEqualityComparer.Default.Equals(owner, symbols.Direct)))
            vector = Raw(context, invocation).HostSizedVectorWrittenBy(method, out type);
        else
            return;

        if (vector != null && type != null)
            context.ReportDiagnostic(Diagnostic.Create(NinoDiagnostics.HostSizedVector, invocation.Syntax.GetLocation(),
                $"The value of this {owner.Name}.{method.Name} call", type.ToDisplayString(),
                NinoDiagnostics.HoldsVector(type, vector)));
    }
}
