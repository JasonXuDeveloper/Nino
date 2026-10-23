using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Nino.Generator.Generation;
using Nino.Generator.Common;

namespace Nino.Generator.Analysis;

/// <summary>
/// Reports every Nino diagnostic: attribute usage, and what the generator would find while building a type's model
/// (the same <see cref="ModelBuilder"/>). The generator itself reports nothing, so its models carry no locations.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class NinoAnalyzer : DiagnosticAnalyzer
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
            if (symbols.Factory is { } factoryAttribute)
                start.RegisterSymbolAction(c => AnalyzeMethod(c, factoryAttribute), SymbolKind.Method);
            start.RegisterSyntaxNodeAction(AnalyzeAssemblyAttributes, SyntaxKind.AttributeList);
        });
    }

    // [assembly: NinoFormatter(...)] registrations: one that FormatterRules cannot use is otherwise silently ignored.
    private static void AnalyzeAssemblyAttributes(SyntaxNodeAnalysisContext context)
    {
        var list = (AttributeListSyntax)context.Node;
        if (list.Target?.Identifier.IsKind(SyntaxKind.AssemblyKeyword) != true)
            return;
        var symbols = TypeFacts.For(context.Compilation, list.SyntaxTree.Options).Symbols;
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

    private static void AnalyzeType(SymbolAnalysisContext context)
    {
        var type = (INamedTypeSymbol)context.Symbol;
        var sink = new DiagnosticSink(context.ReportDiagnostic);
        var attr = type.GetNinoTypeOptions();
        if (attr == null)
        {
            ReportMissingNinoType(sink, type);
            return;
        }

        // The Nino attribute types and the members collected are shared with the generator's model builds.
        var facts = TypeFacts.For(context.Compilation, type.DeclaringSyntaxReferences.FirstOrDefault()?.SyntaxTree.Options);
        // Collecting the findings also caches the members, so the model build below reuses them.
        var findings = facts.Members.FindingsOf(type, attr);

        // The generator skips types it cannot name from outside them.
        if (!Visibility.IsEligible(type))
            sink.Report(NinoDiagnostics.InaccessibleType, type, type.ToDisplayString());
        else
            ModelBuilder.Build(type, facts, context.CancellationToken, sink);

        if (NinoSymbols.Has(type, facts.Symbols.Pooled))
        {
            var missing = PoolHooks.Missing(type, context.CancellationToken).ToList();
            if (missing.Count > 0)
                sink.Report(NinoDiagnostics.PoolHooksNotImplemented, type, type.ToDisplayString(),
                    string.Join(" and ", missing.Select(name => name + "()")));
        }

        ReportFormerNames(sink, type, facts.Symbols);

        int duplicates = 0;
        foreach (var finding in findings)
            Report(sink, type, finding, ref duplicates);
    }

    /// <summary>
    /// The type's <c>[NinoFormerName]</c>s. Its type id comes from one: the generator would take the first,
    /// <c>NinoTypeId.Of</c> would throw. And one name gives every instantiation of a generic type the same id, which
    /// collides once two of them are written with their id (<c>NinoPolymorphic</c> registers each by it): a type with
    /// a <c>[NinoType]</c> base or interface, or one that has subtypes (an interface, an abstract or unsealed class).
    /// </summary>
    private static void ReportFormerNames(DiagnosticSink sink, INamedTypeSymbol type, NinoSymbols known)
    {
        var formerNames = type.GetAttributes()
            .Where(a => SymbolEqualityComparer.Default.Equals(a.AttributeClass, known.FormerName)).ToList();
        if (formerNames.Count > 1)
            sink.Report(NinoDiagnostics.MultipleTypeFormerNames, formerNames[1], type, type.ToDisplayString());
        if (formerNames.Count != 0 && TypeNames.AllTypeParameters(type).Count != 0 &&
            (type.TypeKind == TypeKind.Interface || type.IsAbstract || type.TypeKind == TypeKind.Class && !type.IsSealed ||
             type.NinoAncestors().Any()))
            sink.Report(NinoDiagnostics.GenericTypeFormerName, formerNames[0], type, type.ToDisplayString(),
                formerNames[0].ConstructorArguments.FirstOrDefault().Value as string ?? "");
    }

    /// <summary>Reports what <see cref="MemberCollector"/> found about a member <paramref name="type"/> declares.</summary>
    private static void Report(DiagnosticSink sink, INamedTypeSymbol type, MemberFinding finding, ref int duplicates)
    {
        var member = finding.Member;
        switch (finding.Kind)
        {
            case MemberFindingKind.InvalidCustomFormatter:
                sink.Report(NinoDiagnostics.InvalidCustomFormatter, member, finding.Formatter!.ToDisplayString(),
                    MemberCollector.MemberType(member)!.ToDisplayString());
                break;
            case MemberFindingKind.NonPublicMember:
                sink.Report(NinoDiagnostics.PrivateMember, member, member.Name, type.Name);
                break;
            case MemberFindingKind.RedundantMember:
                sink.Report(NinoDiagnostics.RedundantMember, member, member.Name, type.Name);
                break;
            case MemberFindingKind.RedundantIgnore:
                sink.Report(NinoDiagnostics.RedundantIgnore, member, member.Name, type.Name);
                break;
            case MemberFindingKind.AmbiguousMember:
                sink.Report(NinoDiagnostics.AmbiguousMember, member, member.Name, type.Name);
                break;
            case MemberFindingKind.DuplicateIndex:
                var other = finding.Other!;
                sink.ReportWith(NinoDiagnostics.DuplicateIndex, member,
                    ImmutableDictionary<string, string?>.Empty.Add(NinoDiagnostics.DuplicateRank,
                        (duplicates++).ToString(System.Globalization.CultureInfo.InvariantCulture)),
                    type.Name, member.Name, other.ContainingType.Name, other.Name);
                break;
        }
    }

    /// <summary>A type deriving from a NinoType that does not pass its attribute on must declare its own.</summary>
    private static void ReportMissingNinoType(DiagnosticSink sink, INamedTypeSymbol type)
    {
        if (type.TypeKind is TypeKind.Class or TypeKind.Struct or TypeKind.Interface &&
            type.NinoAncestors().FirstOrDefault() is { } ancestor)
            sink.Report(NinoDiagnostics.SubTypeWithoutNinoType, type, type.Name, ancestor.Name);
    }

    private static void AnalyzeMethod(SymbolAnalysisContext context, INamedTypeSymbol attribute)
    {
        var method = (IMethodSymbol)context.Symbol;
        if (!NinoSymbols.Has(method, attribute))
            return;

        var errors = NinoSymbols.FactoryErrors(method);
        if (errors.Count > 0)
            context.ReportDiagnostic(Diagnostic.Create(NinoDiagnostics.InvalidFactory,
                method.Locations.FirstOrDefault(), method.Name, string.Join(", ", errors)));
    }
}
