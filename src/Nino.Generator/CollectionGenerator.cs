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
            .Where(type => type != null);

        // Combine the results and generate source code
        var compilationAndTypes = context.CompilationProvider.Combine(typeDeclarations.Collect());
        context.RegisterSourceOutput(compilationAndTypes, GenerateSource!);
    }

    private void GenerateSource(SourceProductionContext context,
        (Compilation Compilation, ImmutableArray<TypeSyntax> Types) input)
    {
        var (originalInputCompilation, types) = input; // Renamed for clarity
        var validResult = originalInputCompilation.IsValidCompilation();
        if (!validResult.isValid) return;
        Compilation baseCompilation = validResult.newCompilation; // This is the compilation to be used for non-analysis tasks

        Compilation analysisCompilation = baseCompilation;
        if (baseCompilation is CSharpCompilation csBaseCompilation)
        {
            if (csBaseCompilation.Options.NullableContextOptions != NullableContextOptions.Disable)
            {
                analysisCompilation = csBaseCompilation.WithOptions(
                    csBaseCompilation.Options.WithNullableContextOptions(NullableContextOptions.Disable)
                );
            }
        }

        // Resolve the NinoTypeAttribute symbol using analysisCompilation
        var ninoTypeAttributeSymbol = analysisCompilation.GetTypeByMetadataName(NinoTypeHelper.NinoTypeAttributeFullName);
        var allNinoRequiredTypes = types.GetAllNinoRequiredTypes(analysisCompilation, ninoTypeAttributeSymbol);
        
        // Note: types.Select(syntax => syntax.GetTypeSymbol(compilation)) should use analysisCompilation if GetTypeSymbol is sensitive to nullable context
        // However, GetTypeSymbol typically gets the symbol as-is, and pureType is used later.
        // For safety, if GetTypeSymbol's behavior under different nullable contexts for the *same syntax* could differ,
        // then analysisCompilation should be used. Assuming GetTypeSymbol is robust enough or that downstream pureType handles it.
        // Let's use analysisCompilation for GetTypeSymbol for consistency in analysis.
        var selectedTypeSymbols = types.Select(syntax => syntax.GetTypeSymbol(analysisCompilation)).ToList();

        var potentialTypes =
            allNinoRequiredTypes!.MergeTypes(selectedTypeSymbols, analysisCompilation, ninoTypeAttributeSymbol);
        
        potentialTypes.Sort((x, y) =>
            string.Compare(x.ToDisplayString(), y.ToDisplayString(), StringComparison.Ordinal));

        Type[] generatorTypes =
        {
            typeof(CollectionSerializerGenerator),
            typeof(CollectionDeserializerGenerator),
        };

        foreach (Type type in generatorTypes)
        {
            // Pass analysisCompilation to sub-generators
            var generator = (NinoCollectionGenerator)Activator.CreateInstance(type, analysisCompilation, potentialTypes);
            generator.Execute(context);
        }
    }
}