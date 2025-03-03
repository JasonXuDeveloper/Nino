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
        context.RegisterSyntaxNodeAction(
            syntaxContext =>
            {
                var typeDeclarationSyntax = (TypeDeclarationSyntax)syntaxContext.Node;
                // Check if the type declaration has the NinoTypeAttribute applied
                if (typeDeclarationSyntax.AttributeLists.SelectMany(x => x.Attributes)
                    .All(x => x.Name.ToString() != "NinoType")) return;
                if (typeDeclarationSyntax.Modifiers.Count == 0 ||
                    typeDeclarationSyntax.Modifiers.All(x => !x.IsKind(SyntaxKind.PublicKeyword)))
                {
                    syntaxContext.ReportDiagnostic(Diagnostic.Create(
                        SupportedDiagnostics[1],
                        typeDeclarationSyntax.Identifier.GetLocation(),
                        typeDeclarationSyntax.Identifier.Text,
                        typeDeclarationSyntax.Modifiers.Any()
                            ? string.Join(", ", typeDeclarationSyntax.Modifiers.Select(x => x.Text))
                            : "internal"));
                }
            },
            SyntaxKind.ClassDeclaration, SyntaxKind.StructDeclaration,
            SyntaxKind.InterfaceDeclaration, SyntaxKind.RecordDeclaration,
            SyntaxKind.RecordStructDeclaration);

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
                    var attr = typeSymbol.GetAttributes().FirstOrDefault(a =>
                        a.AttributeClass != null &&
                        a.AttributeClass.ToDisplayString().EndsWith("NinoTypeAttribute"));
                    bool autoCollect = attr == null || (bool)(attr.ConstructorArguments[0].Value ?? false);
                    bool containNonPublic = attr != null && (bool)(attr.ConstructorArguments[1].Value ?? false);

                    foreach (var member in typeSymbol.GetMembers())
                    {
                        bool definedNinoMember = member.GetAttributes()
                            .Any(x => x.AttributeClass?.Name.EndsWith("NinoMemberAttribute") == true);
                        bool definedNinoIgnore =
                            member.GetAttributes().Any(x => x.AttributeClass?.Name == "NinoIgnoreAttribute");

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
                        if (!autoCollect && definedNinoMember  &&
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
                "[NinoType] should be applied to a public class ({0} is {1})",
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
                "Ambiguous Member",
                "Member '{0}' of NinoType '{1}' is annotated with both NinoMemberAttribute and NinoIgnoreAttribute, it is ambiguous",
                "Nino",
                DiagnosticSeverity.Error, true),
            new DiagnosticDescriptor("NINO007",
                "Suspicious member",
                "Member '{0}' of NinoType '{1}' is private but this type is not marked as containing non-public members",
                "Nino",
                DiagnosticSeverity.Error, true)
        );
}