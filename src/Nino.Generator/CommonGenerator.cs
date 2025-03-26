using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Nino.Generator.Common;
using Nino.Generator.Metadata;
using Nino.Generator.Parser;
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
        if (ninoSymbols.Count == 0) return;

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

        Type[] types =
        {
            typeof(TypeConstGenerator),
            typeof(UnsafeAccessorGenerator),
            typeof(PartialClassGenerator),
            typeof(SerializerGenerator),
            typeof(DeserializerGenerator)
        };

        foreach (Type type in types)
        {
            var generator = (NinoCommonGenerator)Activator.CreateInstance(type, compilation, graph, ninoTypes);
            generator.Execute(spc);
        }
    }
}