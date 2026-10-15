using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Nino.Generator.Generation;

namespace Nino.Generator;

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
        NinoDiagnostics.InaccessibleType,
        NinoDiagnostics.SubTypeWithoutNinoType,
        NinoDiagnostics.RedundantMember,
        NinoDiagnostics.RedundantIgnore,
        NinoDiagnostics.AmbiguousMember,
        NinoDiagnostics.PrivateMember,
        NinoDiagnostics.DuplicateIndex,
        NinoDiagnostics.InvalidCustomFormatter,
        NinoDiagnostics.InvalidRefDeserialization,
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
            // Nothing to check in compilations that do not reference Nino.
            if (start.Compilation.GetTypeByMetadataName("Nino.Core.NinoTypeAttribute") == null)
                return;
            // Symbol actions run once per type, not once per partial declaration.
            start.RegisterSymbolAction(AnalyzeType, SymbolKind.NamedType);
            if (start.Compilation.GetTypeByMetadataName("Nino.Core.NinoRefDeserializationAttribute") is { } refAttribute)
                start.RegisterSymbolAction(c => AnalyzeMethod(c, refAttribute), SymbolKind.Method);
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
        var known = facts.Symbols;

        // The generator skips types it cannot name from outside them.
        if (!TypeFacts.IsEligible(type))
            sink.Report(NinoDiagnostics.InaccessibleType, type, type.ToDisplayString());
        else
            ModelBuilder.Build(type, facts, sink);

        if (NinoSymbols.Has(type, known.Pooled))
        {
            var missing = PoolHooks.Missing(type, context.CancellationToken).ToList();
            if (missing.Count > 0)
                sink.Report(NinoDiagnostics.PoolHooksNotImplemented, type, type.ToDisplayString(),
                    string.Join(" and ", missing.Select(name => name + "()")));
        }

        var indices = attr.AutoCollect ? null : new Dictionary<ushort, ISymbol>();
        HashSet<ushort>? reported = null;
        foreach (var member in type.GetMembers())
        {
            AttributeData? ninoMember = null, customFormatter = null;
            bool ignored = false;
            foreach (var a in member.GetAttributes())
            {
                var attributeClass = a.AttributeClass;
                if (attributeClass == null)
                    continue;
                if (SymbolEqualityComparer.Default.Equals(attributeClass, known.Member))
                    ninoMember = a;
                else if (SymbolEqualityComparer.Default.Equals(attributeClass, known.Ignore))
                    ignored = true;
                else if (SymbolEqualityComparer.Default.Equals(attributeClass, known.CustomFormatter))
                    customFormatter = a;
            }

            if (customFormatter is { ConstructorArguments: { Length: > 0 } args } &&
                args[0].Value is INamedTypeSymbol formatter && TypeFacts.MemberType(member) is { } memberType &&
                !known.IsCustomFormatterOf(formatter, memberType))
                sink.Report(NinoDiagnostics.InvalidCustomFormatter, member, formatter.ToDisplayString(),
                    memberType.ToDisplayString());

            bool annotated = ninoMember != null;
            // Non-public members are only collected with containNonPublicMembers: [NinoMember] cannot opt one in.
            bool dropped = annotated && member.DeclaredAccessibility != Accessibility.Public &&
                           !attr.ContainNonPublicMembers;
            if (dropped)
                sink.Report(NinoDiagnostics.PrivateMember, member, member.Name, type.Name);
            if (attr.AutoCollect)
            {
                if (annotated && !dropped)
                    sink.Report(NinoDiagnostics.RedundantMember, member, member.Name, type.Name);
                continue;
            }

            if (annotated && ignored)
                sink.Report(NinoDiagnostics.AmbiguousMember, member, member.Name, type.Name);
            else if (ignored)
                sink.Report(NinoDiagnostics.RedundantIgnore, member, member.Name, type.Name);

            if (ninoMember is { ConstructorArguments: { Length: > 0 } indexArgs } && indexArgs[0].Value is ushort index)
            {
                if (!indices!.TryGetValue(index, out var existing))
                    indices[index] = member;
                else if ((reported ??= new HashSet<ushort>()).Add(index))
                    sink.Report(NinoDiagnostics.DuplicateIndex, member, type.Name, member.Name,
                        existing.ContainingType.Name, existing.Name);
            }
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
        if (method.Parameters.Length > 0)
            errors.Add("must be parameterless");
        if (!SymbolEqualityComparer.Default.Equals(method.ReturnType, method.ContainingType))
            errors.Add($"must return {method.ContainingType.ToDisplayString()}");
        if (errors.Count > 0)
            context.ReportDiagnostic(Diagnostic.Create(NinoDiagnostics.InvalidRefDeserialization,
                method.Locations.FirstOrDefault(), method.Name, string.Join(", ", errors)));
    }

    /// <summary>Whether <paramref name="formatter"/> derives from <c>NinoFormatter&lt;memberType&gt;</c>.</summary>
}
