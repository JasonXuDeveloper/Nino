using System;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Nino.Generator.Common;
using Nino.Generator.Template;

namespace Nino.Generator;

[Generator(LanguageNames.CSharp)]
public class CommonGenerator : IIncrementalGenerator
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
        var (inheritanceMap,
            subTypeMap,
            topNinoTypes) = ninoSymbols.GetInheritanceMap();

        Type[] types =
        [
            typeof(TypeConstGenerator),
            typeof(UnsafeAccessorGenerator),
            typeof(PartialClassGenerator),
            typeof(SerializerGenerator),
            typeof(DeserializerGenerator)
        ];

        foreach (Type type in types)
        {
            var generator = (NinoCommonGenerator)Activator.CreateInstance(type, compilation, ninoSymbols,
                inheritanceMap,
                subTypeMap, topNinoTypes);
            generator.Execute(spc);
        }
    }
}