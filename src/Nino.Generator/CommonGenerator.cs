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
        // The original compilation (potentially modified by IsValidCompilation) is kept for things like AssemblyName.
        // A separate analysisCompilation is created for internal type analysis with nullable context disabled.
        Compilation originalCompilation = result.newCompilation; 
        Compilation analysisCompilation = originalCompilation;

        if (originalCompilation is CSharpCompilation csOriginalCompilation)
        {
            if (csOriginalCompilation.Options.NullableContextOptions != NullableContextOptions.Disable)
            {
                analysisCompilation = csOriginalCompilation.WithOptions(
                    csOriginalCompilation.Options.WithNullableContextOptions(NullableContextOptions.Disable)
                );
            }
        }

        var ninoSymbols = syntaxes.GetNinoTypeSymbols(analysisCompilation);
        if (ninoSymbols.Count == 0) return;

        NinoGraph graph;
        List<NinoType> ninoTypes;
        try
        {
            CSharpParser parser = new(ninoSymbols);
            (graph, ninoTypes) = parser.Parse(analysisCompilation); // Use analysisCompilation
            spc.AddSource("NinoGraph.g.cs", $"/*\n{graph}\n*/");
            // Use originalCompilation for GetNamespace if it relies on original assembly/compilation name
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

        // Note: The curNamespace for generated code should ideally use the original compilation's AssemblyName.
        // The TypeConstGenerator etc. will receive analysisCompilation. If they need original compilation for specific aspects,
        // this design might need further refinement, but for type analysis, analysisCompilation is used.
        foreach (Type type in types)
        {
            var generator = (NinoCommonGenerator)Activator.CreateInstance(type, analysisCompilation, graph, ninoTypes);
            generator.Execute(spc);
        }
    }
}