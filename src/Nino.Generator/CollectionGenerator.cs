using System;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Nino.Generator.Collection;
using Nino.Generator.Template;

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
                    node is GenericNameSyntax or ArrayTypeSyntax or StackAllocArrayCreationExpressionSyntax or TupleTypeSyntax,
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
        var potentialTypes =
            allNinoRequiredTypes!.MergeTypes(types.Select(syntax => syntax.GetTypeSymbol(compilation)).ToList());
        potentialTypes.Sort((x, y) =>
            string.Compare(x.ToDisplayString(), y.ToDisplayString(), StringComparison.Ordinal));

        Type[] generatorTypes =
        [
            typeof(CollectionSerializerGenerator),
            typeof(CollectionDeserializerGenerator),
        ];

        foreach (Type type in generatorTypes)
        {
            var generator = (NinoCollectionGenerator)Activator.CreateInstance(type, compilation, potentialTypes);
            generator.Execute(context);
        }
    }
}