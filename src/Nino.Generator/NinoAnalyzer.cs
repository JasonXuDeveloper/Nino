using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Nino.Generator;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public class NinoAnalyzer : DiagnosticAnalyzer
{
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.Analyze |
                                               GeneratedCodeAnalysisFlags.ReportDiagnostics);
        context.EnableConcurrentExecution();

        // All Type declarations syntaxes with NinoTypeAttribute applied must be public
        context.RegisterSymbolAction(
            symbolContext =>
            {
                var symbol = symbolContext.Symbol;
                if (symbol is not INamedTypeSymbol typeSymbol) return;
                if (typeSymbol.IsUnmanagedType) return;
                if (!typeSymbol.IsNinoType()) return;

                if (!typeSymbol.IsAccessible())
                    symbolContext.ReportDiagnostic(Diagnostic.Create(
                        SupportedDiagnostics[1],
                        typeSymbol.Locations.First(),
                        typeSymbol.GetDisplayString()));
            },
            SymbolKind.NamedType);

        // A sub-type of a NinoType must also have the NinoTypeAttribute applied
        context.RegisterSyntaxNodeAction(
            syntaxContext =>
            {
                var typeDeclarationSyntax = (TypeDeclarationSyntax)syntaxContext.Node;
                var typeSymbol = typeDeclarationSyntax.GetTypeSymbol(syntaxContext);
                if (typeSymbol == null) return;
                if (typeSymbol.IsNinoType())
                {
                    // check redundant NinoMemberAttribute and NinoIgnoreAttribute
                    var attr = typeSymbol.GetAttributesCache().FirstOrDefault(a =>
                        a.AttributeClass != null &&
                        a.AttributeClass.ToDisplayString().EndsWith("NinoTypeAttribute"));
                    bool autoCollect = attr == null || (bool)(attr.ConstructorArguments[0].Value ?? false);
                    bool containNonPublic = attr != null && (bool)(attr.ConstructorArguments[1].Value ?? false);

                    // check if the type is nested
                    if (typeDeclarationSyntax.Parent is TypeDeclarationSyntax && containNonPublic)
                    {
                        syntaxContext.ReportDiagnostic(Diagnostic.Create(
                            SupportedDiagnostics[7],
                            typeDeclarationSyntax.Identifier.GetLocation(),
                            typeDeclarationSyntax.Identifier.Text));
                    }

                    foreach (var member in typeSymbol.GetMembers())
                    {
                        bool definedNinoMember = member.GetAttributesCache()
                            .Any(x => x.AttributeClass?.Name.EndsWith("NinoMemberAttribute") == true);
                        bool definedNinoIgnore =
                            member.GetAttributesCache().Any(x => x.AttributeClass?.Name == "NinoIgnoreAttribute");

                        // Check NinoCustomFormatterAttribute validation - NINO010
                        var customFormatterAttr = member.GetAttributesCache()
                            .FirstOrDefault(x => x.AttributeClass?.Name == "NinoCustomFormatterAttribute");
                        if (customFormatterAttr != null && customFormatterAttr.ConstructorArguments.Length > 0)
                        {
                            var formatterTypeArg = customFormatterAttr.ConstructorArguments[0];
                            if (formatterTypeArg.Value is INamedTypeSymbol formatterType)
                            {
                                // Get the member type for validation
                                ITypeSymbol? memberType = member switch
                                {
                                    IFieldSymbol field => field.Type,
                                    IPropertySymbol property => property.Type,
                                    _ => null
                                };

                                if (memberType != null)
                                {
                                    // Check if formatter inherits from NinoFormatter<memberType>
                                    var expectedBaseType = "NinoFormatter<" + memberType.ToDisplayString() + ">";
                                    bool isValidFormatter = IsValidNinoFormatter(formatterType, memberType);

                                    if (!isValidFormatter)
                                    {
                                        syntaxContext.ReportDiagnostic(Diagnostic.Create(
                                            SupportedDiagnostics[9], // NINO010
                                            member.Locations.First(),
                                            formatterType.ToDisplayString(),
                                            memberType.ToDisplayString()));
                                    }
                                }
                            }
                        }

                        // auto collect but manually annotated - nino004
                        if (autoCollect && definedNinoMember)
                        {
                            syntaxContext.ReportDiagnostic(Diagnostic.Create(
                                SupportedDiagnostics[3],
                                member.Locations.First(),
                                member.Name,
                                typeSymbol.Name));
                        }

                        // not auto collect and manually annotated but is private - nino007
                        if (!autoCollect && definedNinoMember &&
                            member.DeclaredAccessibility == Accessibility.Private &&
                            !containNonPublic)
                        {
                            syntaxContext.ReportDiagnostic(Diagnostic.Create(
                                SupportedDiagnostics[6],
                                member.Locations.First(),
                                member.Name,
                                typeSymbol.Name));
                        }

                        // not annotated but manually ignored when not manually annotated - nino005
                        if (!autoCollect && !definedNinoMember && definedNinoIgnore)
                        {
                            syntaxContext.ReportDiagnostic(Diagnostic.Create(
                                SupportedDiagnostics[4],
                                member.Locations.First(),
                                member.Name,
                                typeSymbol.Name));
                        }

                        // ambiguous annotation - nino006
                        if (!autoCollect && definedNinoIgnore && definedNinoMember)
                        {
                            syntaxContext.ReportDiagnostic(Diagnostic.Create(
                                SupportedDiagnostics[5],
                                member.Locations.First(),
                                member.Name,
                                typeSymbol.Name));
                        }
                    }

                    // Check for duplicate member indices - nino009
                    if (!autoCollect)
                    {
                        var memberIndices = new Dictionary<ushort, (string memberName, ISymbol member)>();
                        HashSet<ushort> reported = new();
                        HashSet<ISymbol> visited = new(SymbolEqualityComparer.Default);
                        foreach (var member in typeSymbol.GetMembers())
                        {
                            if (!visited.Add(member)) continue;

                            var ninoMemberAttr = member.GetAttributesCache()
                                .FirstOrDefault(x =>
                                    x.AttributeClass?.Name.EndsWith("NinoMemberAttribute") == true);

                            if (ninoMemberAttr != null && ninoMemberAttr.ConstructorArguments.Length > 0)
                            {
                                var indexArg = ninoMemberAttr.ConstructorArguments[0];
                                if (indexArg.Value != null)
                                {
                                    var index = (ushort)indexArg.Value;

                                    if (memberIndices.TryGetValue(index, out var existingMember))
                                    {
                                        if (!reported.Add(index)) continue;
                                        // Report duplicate for current member
                                        syntaxContext.ReportDiagnostic(Diagnostic.Create(
                                            SupportedDiagnostics[8],
                                            member.Locations.First(),
                                            typeSymbol.Name,
                                            member.Name,
                                            existingMember.member.ContainingType.Name,
                                            existingMember.memberName));
                                    }
                                    else
                                    {
                                        memberIndices[index] = (member.Name, member);
                                    }
                                }
                            }
                        }
                    }

                    return;
                }

                //check base type
                var baseType = typeSymbol.BaseType;
                while (baseType != null)
                {
                    if (baseType.IsNinoType())
                    {
                        syntaxContext.ReportDiagnostic(Diagnostic.Create(
                            SupportedDiagnostics[2],
                            typeDeclarationSyntax.Identifier.GetLocation(),
                            typeDeclarationSyntax.Identifier.Text,
                            baseType.Name));
                        break;
                    }

                    baseType = baseType.BaseType;
                }

                // check base interfaces
                foreach (var interfaceType in typeSymbol.AllInterfaces)
                {
                    if (interfaceType.IsNinoType())
                    {
                        syntaxContext.ReportDiagnostic(Diagnostic.Create(
                            SupportedDiagnostics[2],
                            typeDeclarationSyntax.Identifier.GetLocation(),
                            typeDeclarationSyntax.Identifier.Text,
                            interfaceType.Name));
                        break;
                    }
                }
            },
            SyntaxKind.ClassDeclaration, SyntaxKind.StructDeclaration,
            SyntaxKind.InterfaceDeclaration, SyntaxKind.RecordDeclaration,
            SyntaxKind.RecordStructDeclaration);
    }

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(new DiagnosticDescriptor("NINO001",
                "Nino encountered an error during the generation",
                "Something went wrong during the generation",
                "Nino",
                DiagnosticSeverity.Error, true),
            new DiagnosticDescriptor("NINO002",
                "'NinoType' should be applied to a public class",
                "[NinoType] should be applied to a public class (even if it is nested) (Check all levels of accessibility for {0})",
                "'Nino",
                DiagnosticSeverity.Error, true),
            new DiagnosticDescriptor("NINO003",
                "Sub-type of a NinoType should also have the NinoTypeAttribute applied",
                "Sub-type '{0}' of NinoType '{1}' should also have the NinoTypeAttribute applied to ensure serialization does not lead to undefined behavior",
                "Nino",
                DiagnosticSeverity.Warning, true),
            new DiagnosticDescriptor("NINO004",
                "Redundant NinoMemberAttribute",
                "Member '{0}' of NinoType '{1}' will be automatically collected, the NinoMemberAttribute is redundant",
                "Nino",
                DiagnosticSeverity.Warning, true),
            new DiagnosticDescriptor("NINO005",
                "Redundant NinoIgnoreAttribute",
                "Member '{0}' of NinoType '{1}' will not be collected at anytime, the NinoIgnoreAttribute is redundant",
                "Nino",
                DiagnosticSeverity.Warning, true),
            new DiagnosticDescriptor("NINO006",
                "Ambiguous member",
                "Member '{0}' of NinoType '{1}' is annotated with both NinoMemberAttribute and NinoIgnoreAttribute, it is ambiguous",
                "Nino",
                DiagnosticSeverity.Error, true),
            new DiagnosticDescriptor("NINO007",
                "Suspicious member",
                "Member '{0}' of NinoType '{1}' is private but this type is not marked as containing non-public members",
                "Nino",
                DiagnosticSeverity.Error, true),
            new DiagnosticDescriptor("NINO008",
                "Nested type that may contain non-public members",
                "NinoType '{0}' is allowed to contain non-public members so it cannot be nested",
                "Nino",
                DiagnosticSeverity.Error, true),
            new DiagnosticDescriptor("NINO009",
                "Duplicate member index",
                "Member '{0}.{1}' has the same index as another member '{2}.{3}'",
                "Nino",
                DiagnosticSeverity.Error, true),
            new DiagnosticDescriptor("NINO010",
                "Invalid custom formatter type",
                "Custom formatter '{0}' must inherit from NinoFormatter<{1}>",
                "Nino",
                DiagnosticSeverity.Error, true)
        );

    private static bool IsValidNinoFormatter(INamedTypeSymbol formatterType, ITypeSymbol memberType)
    {
        // Check if formatter inherits from NinoFormatter<memberType>
        var baseType = formatterType.BaseType;
        while (baseType != null)
        {
            if (baseType.Name == "NinoFormatter" &&
                baseType.IsGenericType &&
                baseType.TypeArguments.Length == 1)
            {
                var formatterGenericArg = baseType.TypeArguments[0];
                return SymbolEqualityComparer.Default.Equals(formatterGenericArg, memberType);
            }

            baseType = baseType.BaseType;
        }

        return false;
    }
}