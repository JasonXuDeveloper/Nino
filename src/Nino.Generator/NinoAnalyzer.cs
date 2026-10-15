using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Nino.Generator.Generation;

namespace Nino.Generator;

/// <summary>Checks how the Nino attributes are used. Diagnostics about generation itself come from the generator.</summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class NinoAnalyzer : DiagnosticAnalyzer
{
    private static readonly ImmutableArray<DiagnosticDescriptor> Supported = ImmutableArray.Create(
        NinoDiagnostics.InaccessibleType,
        NinoDiagnostics.SubTypeWithoutNinoType,
        NinoDiagnostics.RedundantMember,
        NinoDiagnostics.RedundantIgnore,
        NinoDiagnostics.AmbiguousMember,
        NinoDiagnostics.PrivateMember,
        NinoDiagnostics.DuplicateIndex,
        NinoDiagnostics.InvalidCustomFormatter,
        NinoDiagnostics.InvalidRefDeserialization);

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
            var refDeserialization = start.Compilation.GetTypeByMetadataName("Nino.Core.NinoRefDeserializationAttribute");
            // Symbol actions run once per type, not once per partial declaration.
            start.RegisterSymbolAction(AnalyzeType, SymbolKind.NamedType);
            if (refDeserialization != null)
                start.RegisterSymbolAction(c => AnalyzeMethod(c, refDeserialization), SymbolKind.Method);
        });
    }

    private static void AnalyzeType(SymbolAnalysisContext context)
    {
        var type = (INamedTypeSymbol)context.Symbol;
        var attr = type.GetNinoTypeAttribute();
        if (attr == null)
        {
            ReportMissingNinoType(context, type);
            return;
        }

        // The generator skips types it cannot name from outside them.
        if (!TypeFacts.IsEligible(type))
            Report(context, NinoDiagnostics.InaccessibleType, type, type.GetDisplayString());

        var indices = attr.autoCollect ? null : new Dictionary<ushort, ISymbol>();
        HashSet<ushort>? reported = null;
        foreach (var member in type.GetMembers())
        {
            AttributeData? ninoMember = null, customFormatter = null;
            bool ignored = false;
            foreach (var a in member.GetAttributes())
            {
                switch (a.AttributeClass?.Name)
                {
                    case "NinoMemberAttribute": ninoMember = a; break;
                    case "NinoIgnoreAttribute": ignored = true; break;
                    case "NinoCustomFormatterAttribute": customFormatter = a; break;
                }
            }

            if (customFormatter is { ConstructorArguments: { Length: > 0 } args } &&
                args[0].Value is INamedTypeSymbol formatter &&
                MemberType(member) is { } memberType && !IsFormatterOf(formatter, memberType))
                Report(context, NinoDiagnostics.InvalidCustomFormatter, member, formatter.ToDisplayString(),
                    memberType.ToDisplayString());

            bool annotated = ninoMember != null;
            if (attr.autoCollect)
            {
                if (annotated)
                    Report(context, NinoDiagnostics.RedundantMember, member, member.Name, type.Name);
                continue;
            }

            if (annotated && ignored)
                Report(context, NinoDiagnostics.AmbiguousMember, member, member.Name, type.Name);
            else if (ignored)
                Report(context, NinoDiagnostics.RedundantIgnore, member, member.Name, type.Name);
            if (annotated && member.DeclaredAccessibility == Accessibility.Private && !attr.containNonPublicMembers)
                Report(context, NinoDiagnostics.PrivateMember, member, member.Name, type.Name);

            if (ninoMember is { ConstructorArguments: { Length: > 0 } indexArgs } && indexArgs[0].Value is ushort index)
            {
                if (!indices!.TryGetValue(index, out var existing))
                    indices[index] = member;
                else if ((reported ??= new HashSet<ushort>()).Add(index))
                    Report(context, NinoDiagnostics.DuplicateIndex, member, type.Name, member.Name,
                        existing.ContainingType.Name, existing.Name);
            }
        }
    }

    /// <summary>A type deriving from a NinoType that does not pass its attribute on must declare its own.</summary>
    private static void ReportMissingNinoType(SymbolAnalysisContext context, INamedTypeSymbol type)
    {
        if (type.TypeKind is not (TypeKind.Class or TypeKind.Struct or TypeKind.Interface))
            return;
        for (var baseType = type.BaseType; baseType != null; baseType = baseType.BaseType)
        {
            if (baseType.IsNinoType())
            {
                Report(context, NinoDiagnostics.SubTypeWithoutNinoType, type, type.Name, baseType.Name);
                return;
            }
        }

        foreach (var interfaceType in type.AllInterfaces)
        {
            if (interfaceType.IsNinoType())
            {
                Report(context, NinoDiagnostics.SubTypeWithoutNinoType, type, type.Name, interfaceType.Name);
                return;
            }
        }
    }

    private static void AnalyzeMethod(SymbolAnalysisContext context, INamedTypeSymbol attribute)
    {
        var method = (IMethodSymbol)context.Symbol;
        if (!method.GetAttributes().Any(a => SymbolEqualityComparer.Default.Equals(a.AttributeClass, attribute)))
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
            Report(context, NinoDiagnostics.InvalidRefDeserialization, method, method.Name, string.Join(", ", errors));
    }

    private static void Report(SymbolAnalysisContext context, DiagnosticDescriptor descriptor, ISymbol symbol,
        params object[] args) =>
        context.ReportDiagnostic(Diagnostic.Create(descriptor, symbol.Locations.FirstOrDefault(), args));

    private static ITypeSymbol? MemberType(ISymbol member) => member switch
    {
        IFieldSymbol field => field.Type,
        IPropertySymbol property => property.Type,
        _ => null
    };

    /// <summary>Whether <paramref name="formatter"/> derives from <c>NinoFormatter&lt;memberType&gt;</c>.</summary>
    private static bool IsFormatterOf(INamedTypeSymbol formatter, ITypeSymbol memberType)
    {
        for (var baseType = formatter.BaseType; baseType != null; baseType = baseType.BaseType)
        {
            if (baseType is { Name: "NinoFormatter", TypeArguments.Length: 1 })
                return SymbolEqualityComparer.Default.Equals(baseType.TypeArguments[0], memberType);
        }

        return false;
    }
}
