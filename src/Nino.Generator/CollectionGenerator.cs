using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Nino.Generator.Collection;

namespace Nino.Generator;

[Generator(LanguageNames.CSharp)]
public class CollectionGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        // Register the syntax receiver
        var typeDeclarations = context.SyntaxProvider
            .CreateSyntaxProvider(
                static (node, _) =>
                    node is GenericNameSyntax or ArrayTypeSyntax or StackAllocArrayCreationExpressionSyntax,
                static (context, _) =>
                    context.Node switch
                    {
                        GenericNameSyntax genericNameSyntax => genericNameSyntax,
                        ArrayTypeSyntax arrayTypeSyntax => arrayTypeSyntax,
                        StackAllocArrayCreationExpressionSyntax stackAllocArrayCreationExpressionSyntax =>
                            stackAllocArrayCreationExpressionSyntax.Type,
                        _ => null
                    })
            .Where(type => type != null);

        // Combine the results and generate source code
        var compilationAndTypes = context.CompilationProvider.Combine(typeDeclarations.Collect());
        context.RegisterSourceOutput(compilationAndTypes, GenerateSource!);
    }

    private void GenerateSource(SourceProductionContext context,
        (Compilation Compilation, ImmutableArray<TypeSyntax> Types) input)
    {
        var (compilation, types) = input;
        var result = compilation.IsValidCompilation();
        if (!result.isValid) return;
        compilation = result.newCompilation;
        var allNinoRequiredTypes = types.GetAllNinoRequiredTypes(compilation);
        var serializeTypeSymbols = types.GetPotentialCollectionTypes(allNinoRequiredTypes, compilation);
        var deserializeTypeSymbols = types.GetPotentialCollectionTypes(allNinoRequiredTypes, compilation, true);

        if (serializeTypeSymbols.Count > 0)
        {
            CollectionSerializerGenerator serializerGenerator = new(compilation, serializeTypeSymbols);
            serializerGenerator.Execute(context);
        }

        if (deserializeTypeSymbols.Count > 0)
        {
            CollectionDeserializerGenerator deserializerGenerator = new(compilation, deserializeTypeSymbols);
            deserializerGenerator.Execute(context);
        }
    }
}