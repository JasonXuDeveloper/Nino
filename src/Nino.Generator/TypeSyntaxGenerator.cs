using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Nino.Generator;

[Generator(LanguageNames.CSharp)]
public class TypeSyntaxGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var ninoTypeModels = context.GetTypeSyntaxes();
        var compilationAndClasses = context.CompilationProvider.Combine(ninoTypeModels.Collect());
        context.RegisterSourceOutput(compilationAndClasses, (spc, source) => Execute(source.Left, source.Right, spc));
    }

    private static void Execute(Compilation compilation, ImmutableArray<CSharpSyntaxNode> syntaxes,
        SourceProductionContext spc)
    {
        var result = compilation.IsValidCompilation();
        if (!result.isValid) return;
        compilation = result.newCompilation;

        var ninoSymbols = syntaxes.GetNinoTypeSymbols(compilation);
        
        TypeConstGenerator typeConstGenerator = new(compilation, ninoSymbols);
        UnsafeAccessorGenerator unsafeAccessorGenerator = new(compilation, ninoSymbols);
        PartialClassGenerator partialClassGenerator = new(compilation, ninoSymbols);
        
        typeConstGenerator.Execute(spc);
        unsafeAccessorGenerator.Execute(spc);
        partialClassGenerator.Execute(spc);
    }
}