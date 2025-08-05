using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Nino.Generator.Common;
using Nino.Generator.Metadata;
using Nino.Generator.Parser;

namespace Nino.Generator;

[Generator(LanguageNames.CSharp)]
public class GlobalGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        // Create stable incremental providers with proper equatable keys
        var ninoTypeModels = context.GetTypeSyntaxes()
            .Where(static syntax => syntax != null)
            .Collect();
            
        var compilationAndClasses = context.CompilationProvider.Combine(ninoTypeModels);

        // Register the syntax receiver with stable equality
        var typeDeclarations = context.SyntaxProvider
            .CreateSyntaxProvider(
                static (node, _) =>
                    node is GenericNameSyntax or ArrayTypeSyntax or StackAllocArrayCreationExpressionSyntax
                        or TupleTypeSyntax,
                static (context, _) =>
                    context.Node switch
                    {
                        GenericNameSyntax genericNameSyntax => (TypeSyntax)genericNameSyntax,
                        ArrayTypeSyntax arrayTypeSyntax => arrayTypeSyntax,
                        StackAllocArrayCreationExpressionSyntax stackAllocArrayCreationExpressionSyntax =>
                            stackAllocArrayCreationExpressionSyntax.Type,
                        TupleTypeSyntax tupleTypeSyntax => tupleTypeSyntax,
                        _ => null
                    })
            .Where(static type => type != null)
            .Select(static (type, _) => type!)
            .Collect();

        // Combine with more stable ordering
        var compilationAndTypes = context.CompilationProvider.Combine(typeDeclarations);
        var merged = compilationAndTypes.Combine(compilationAndClasses);

        // Add explicit caching and error boundaries
        context.RegisterSourceOutput(merged, static (spc, source) =>
        {
            var compilation = source.Left.Left;
            var collectionTypes = source.Left.Right;
            var syntaxes = source.Right.Right;
            
            // Add stability check
            if (compilation == null) return;
            
            try
            {
                ExecuteWithStability(compilation, syntaxes, collectionTypes, spc);
            }
            catch (Exception e)
            {
                // Report but don't fail completely - let build succeed
                spc.ReportDiagnostic(Diagnostic.Create(
                    new DiagnosticDescriptor("NINO998", "Nino Generator Warning",
                        $"Generator encountered issue but continuing: {e.Message}",
                        "Nino.Generator",
                        DiagnosticSeverity.Info, true), Location.None));
            }
        });
    }

    private static void ExecuteWithStability(Compilation compilation, ImmutableArray<CSharpSyntaxNode> syntaxes,
        ImmutableArray<TypeSyntax> collectionTypes,
        SourceProductionContext spc)
    {
        var result = compilation.IsValidCompilation();
        if (!result.isValid) return;
        compilation = result.newCompilation;

        // Add null checks for stability
        if (syntaxes.IsDefault) syntaxes = ImmutableArray<CSharpSyntaxNode>.Empty;
        if (collectionTypes.IsDefault) collectionTypes = ImmutableArray<TypeSyntax>.Empty;

        var allNinoRequiredTypes = collectionTypes.GetAllNinoRequiredTypes(compilation);
        var potentialTypesLst =
            allNinoRequiredTypes!.MergeTypes(collectionTypes.Select(syntax => syntax.GetTypeSymbol(compilation))
                .ToList());
        potentialTypesLst.Sort((x, y) =>
            string.Compare(x.GetSanitizedDisplayString(), y.GetSanitizedDisplayString(), StringComparison.Ordinal));
        var potentialTypes = new HashSet<ITypeSymbol>
                (potentialTypesLst, new NinoTypeHelper.TupleSanitizedEqualityComparer())
            .ToList();

        var ninoSymbols = syntaxes.GetNinoTypeSymbols(compilation)
            .ToList();

        NinoGraph graph;
        List<NinoType> ninoTypes;
        try
        {
            CSharpParser parser = new(ninoSymbols);
            (graph, ninoTypes) = parser.Parse(compilation);
            
            // Generate debug info with stability check
            var curNamespace = compilation.AssemblyName?.GetNamespace() ?? "DefaultNamespace";
            spc.AddSource($"{curNamespace}.Graph.g.cs", $"/*\n{graph}\n*/");
            spc.AddSource($"{curNamespace}.Types.g.cs",
                $"/*\n{string.Join("\n", ninoTypes.Where(t => t.Members.Count > 0))}\n*/");
        }
        catch (Exception e)
        {
            // Log error but don't completely fail generation
            spc.ReportDiagnostic(Diagnostic.Create(
                new DiagnosticDescriptor("NINO000", "Nino Generator Parse Error",
                    $"Parser failed, falling back to minimal generation: {e.Message}",
                    "Nino.Generator",
                    DiagnosticSeverity.Warning, true), Location.None));
            
            // Create minimal fallback to prevent complete failure
            graph = new NinoGraph(compilation, new List<NinoType>());
            ninoTypes = new List<NinoType>();
        }

        // Execute generators with individual error boundaries and error reporting
        ExecuteGeneratorSafely(() => new TypeConstGenerator(compilation, graph, ninoTypes).Execute(spc), 
            "TypeConstGenerator", spc);
        ExecuteGeneratorSafely(() => new UnsafeAccessorGenerator(compilation, graph, ninoTypes).Execute(spc), 
            "UnsafeAccessorGenerator", spc);
        ExecuteGeneratorSafely(() => new PartialClassGenerator(compilation, graph, ninoTypes).Execute(spc), 
            "PartialClassGenerator", spc);
        ExecuteGeneratorSafely(() => new SerializerGenerator(compilation, graph, ninoTypes, potentialTypes).Execute(spc), 
            "SerializerGenerator", spc);
        ExecuteGeneratorSafely(() => new DeserializerGenerator(compilation, graph, ninoTypes, potentialTypes).Execute(spc), 
            "DeserializerGenerator", spc);
    }
    
    private static void ExecuteGeneratorSafely(Action generatorAction, string generatorName, SourceProductionContext spc)
    {
        try
        {
            generatorAction();
        }
        catch (Exception ex)
        {
            // Report specific generator failure with details
            spc.ReportDiagnostic(Diagnostic.Create(
                new DiagnosticDescriptor($"NINO{generatorName.GetHashCode() % 1000:D3}", 
                    $"{generatorName} Error",
                    $"{generatorName} failed: {ex.GetType().Name} - {ex.Message}",
                    "Nino.Generator",
                    DiagnosticSeverity.Warning, 
                    true,
                    description: $"Stack trace: {ex.StackTrace}"),
                Location.None));
            
            // Also add a comment in generated code for debugging
            spc.AddSource($"{generatorName}.Error.g.cs", 
                $@"/*
{generatorName} failed to generate code.
Error: {ex.GetType().Name}: {ex.Message}

Stack Trace:
{ex.StackTrace}

This error has been logged as a warning and other generators will continue.
*/");
        }
    }

}