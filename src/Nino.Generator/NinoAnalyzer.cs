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
                if (typeSymbol.IsNinoType()) return;
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
                "Sub-type '{0}' of NinoType '{1}' should also have the NinoTypeAttribute applied",
                "Nino",
                DiagnosticSeverity.Error, true)
        );
}