using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Nino.Generator.Collection;
using Nino.Generator.Common;
using Nino.Generator.Metadata;
using Nino.Generator.Parser;

namespace Nino.Generator;

[Generator(LanguageNames.CSharp)]
public class GlobalGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var ninoTypeModels = context.GetTypeSyntaxes();
        var compilationAndClasses = context.CompilationProvider.Combine(ninoTypeModels.Collect());

        // Register the syntax receiver
        var typeDeclarations = context.SyntaxProvider
            .CreateSyntaxProvider(
                static (node, _) =>
                    node is GenericNameSyntax or ArrayTypeSyntax or StackAllocArrayCreationExpressionSyntax
                        or TupleTypeSyntax,
                static (context, _) =>
                    context.Node switch
                    {
                        GenericNameSyntax genericNameSyntax => genericNameSyntax,
                        ArrayTypeSyntax arrayTypeSyntax => arrayTypeSyntax,
                        StackAllocArrayCreationExpressionSyntax stackAllocArrayCreationExpressionSyntax =>
                            stackAllocArrayCreationExpressionSyntax.Type,
                        TupleTypeSyntax tupleTypeSyntax => tupleTypeSyntax,
                        _ => null
                    })
            .Where(type => type != null)
            .Select((type, _) => type!);

        // Combine the results and generate source code
        var compilationAndTypes = context.CompilationProvider.Combine(typeDeclarations.Collect());

        // merge
        var merged = compilationAndTypes.Combine(compilationAndClasses);

        context.RegisterSourceOutput(merged, (spc, source) =>
        {
            var compilation = source.Left.Left;
            var collectionTypes = source.Left.Right;
            var syntaxes = source.Right.Right;
            Execute(compilation, syntaxes, collectionTypes, spc);
        });
    }

    private static void Execute(Compilation compilation, ImmutableArray<CSharpSyntaxNode> syntaxes,
        ImmutableArray<TypeSyntax> collectionTypes,
        SourceProductionContext spc)
    {
        var result = compilation.IsValidCompilation();
        if (!result.isValid) return;
        compilation = result.newCompilation;

        var allNinoRequiredTypes = collectionTypes.GetAllNinoRequiredTypes(compilation);
        var potentialTypesLst =
            allNinoRequiredTypes!.MergeTypes(collectionTypes.Select(syntax => syntax.GetTypeSymbol(compilation))
                .ToList());
        potentialTypesLst.Sort((x, y) =>
            string.Compare(x.GetDisplayString(), y.GetDisplayString(), StringComparison.Ordinal));
        var potentialTypes = new HashSet<ITypeSymbol>(potentialTypesLst, SymbolEqualityComparer.Default).ToList();

        var ninoSymbols = syntaxes.GetNinoTypeSymbols(compilation);

        NinoGraph graph;
        List<NinoType> ninoTypes;
        try
        {
            CSharpParser parser = new(ninoSymbols);
            (graph, ninoTypes) = parser.Parse(compilation);
            spc.AddSource("NinoGraph.g.cs", $"/*\n{graph}\n*/");
            spc.AddSource("NinoTypes.g.cs", $"/*\n{string.Join("\n", ninoTypes.Where(t => t.Members.Count > 0))}\n*/");
        }
        catch (Exception e)
        {
            spc.ReportDiagnostic(Diagnostic.Create(
                new DiagnosticDescriptor("NINO000", "Nino Generator",
                    $"An error occurred while exporting graphs: {e.GetType()} {e.Message}, {e.StackTrace}",
                    "Nino.Generator",
                    DiagnosticSeverity.Error, true), Location.None));
            return;
        }

        new TypeConstGenerator(compilation, graph, ninoTypes).Execute(spc);
        new UnsafeAccessorGenerator(compilation, graph, ninoTypes).Execute(spc);
        new PartialClassGenerator(compilation, graph, ninoTypes).Execute(spc);
        new SerializerGenerator(compilation, graph, ninoTypes).Execute(spc);
        new DeserializerGenerator(compilation, graph, ninoTypes, potentialTypes).Execute(spc);
        new CollectionSerializerGenerator(compilation, potentialTypes).Execute(spc);
    }
}