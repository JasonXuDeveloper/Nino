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
    private static readonly ImmutableArray<DiagnosticDescriptor> Supported = ImmutableArray.Create(
        NinoDiagnostics.MissingConstructorMember,
        NinoDiagnostics.NoUsableConstructor,
        NinoDiagnostics.InaccessibleConstructor,
        NinoDiagnostics.InaccessibleType,
        NinoDiagnostics.SubTypeWithoutNinoType,
        NinoDiagnostics.RedundantMember,
        NinoDiagnostics.RedundantIgnore,
        NinoDiagnostics.FormerNameNotKeyed,
        NinoDiagnostics.AmbiguousMember,
        NinoDiagnostics.PrivateMember,
        NinoDiagnostics.DuplicateIndex,
        NinoDiagnostics.HiddenMember,
        NinoDiagnostics.EmptyStruct,
        NinoDiagnostics.InvalidCustomFormatter,
        NinoDiagnostics.InvalidFactory,
        NinoDiagnostics.UnserializableMember,
        NinoDiagnostics.NotPartial,
        NinoDiagnostics.ReadOnlyMember,
        NinoDiagnostics.KeyCollision,
        NinoDiagnostics.TrackReferencesOnStruct,
        NinoDiagnostics.TrackReferencesNeedsAssignableMembers,
        NinoDiagnostics.InvalidPooledType,
        NinoDiagnostics.PoolHooksNotImplemented,
        NinoDiagnostics.InvalidFormatterRegistration);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => Supported;

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
            ModelBuilder.Build(type, facts, sink);

        if (NinoSymbols.Has(type, facts.Symbols.Pooled))
        {
            var missing = PoolHooks.Missing(type, context.CancellationToken).ToList();
            if (missing.Count > 0)
                sink.Report(NinoDiagnostics.PoolHooksNotImplemented, type, type.ToDisplayString(),
                    string.Join(" and ", missing.Select(name => name + "()")));
        }

        foreach (var finding in findings)
            Report(sink, type, finding);
    }

    /// <summary>Reports what <see cref="MemberCollector"/> found about a member <paramref name="type"/> declares.</summary>
    private static void Report(DiagnosticSink sink, INamedTypeSymbol type, MemberFinding finding)
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
                sink.Report(NinoDiagnostics.DuplicateIndex, member, type.Name, member.Name, other.ContainingType.Name,
                    other.Name);
                break;
        }
    }

    /// <summary>A type deriving from a NinoType that does not pass its attribute on must declare its own.</summary>
    private static void ReportMissingNinoType(DiagnosticSink sink, INamedTypeSymbol type)
    {
        if (type.TypeKind is not (TypeKind.Class or TypeKind.Struct or TypeKind.Interface))
            return;
        for (var baseType = type.BaseType; baseType != null; baseType = baseType.BaseType)
        {
            if (baseType.IsNinoType())
            {
                sink.Report(NinoDiagnostics.SubTypeWithoutNinoType, type, type.Name, baseType.Name);
                return;
            }
        }

        foreach (var interfaceType in type.AllInterfaces)
        {
            if (interfaceType.IsNinoType())
            {
                sink.Report(NinoDiagnostics.SubTypeWithoutNinoType, type, type.Name, interfaceType.Name);
                return;
            }
        }
    }

    private static void AnalyzeMethod(SymbolAnalysisContext context, INamedTypeSymbol attribute)
    {
        var method = (IMethodSymbol)context.Symbol;
        if (!NinoSymbols.Has(method, attribute))
            return;

        var errors = new List<string>();
        if (method.DeclaredAccessibility != Accessibility.Public)
            errors.Add("must be public");
        if (!method.IsStatic)
            errors.Add("must be static");
        if (!ConstructionPlanner.CallableWithoutArguments(method))
            errors.Add("must be callable without arguments (parameterless, or every parameter optional)");
        if (!SymbolEqualityComparer.Default.Equals(method.ReturnType, method.ContainingType))
            errors.Add($"must return {method.ContainingType.ToDisplayString()}");
        if (errors.Count > 0)
            context.ReportDiagnostic(Diagnostic.Create(NinoDiagnostics.InvalidFactory,
                method.Locations.FirstOrDefault(), method.Name, string.Join(", ", errors)));
    }
}
