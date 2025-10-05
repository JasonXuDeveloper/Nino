using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Nino.Generator.BuiltInType;
using Nino.Generator.Common;
using Nino.Generator.Metadata;
using Nino.Generator.Parser;
using Nino.Generator.Template;

namespace Nino.Generator;

[Generator(LanguageNames.CSharp)]
public class GlobalGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var ninoTypeModels = context.GetTypeSyntaxes()
            .Where(static syntax => syntax != null)
            .Collect();

        var typeDeclarations = context.SyntaxProvider
            .CreateSyntaxProvider(
                static (node, _) =>
                    node is GenericNameSyntax or ArrayTypeSyntax
                        or NullableTypeSyntax or TupleTypeSyntax,
                static (context, _) => (TypeSyntax)context.Node
            )
            .Where(static type => type != null)
            .Select(static (type, _) => type!)
            .Collect();

        var merged = context.CompilationProvider.Combine(typeDeclarations)
            .Combine(ninoTypeModels);

        // Add explicit caching and error boundaries
        context.RegisterSourceOutput(merged, static (spc, source) =>
        {
            var compilation = source.Left.Left;
            var typeSyntaxes = source.Left.Right;
            var ninoTypeSyntaxes = source.Right;

            // Add stability check
            if (compilation == null) return;

            try
            {
                var result = compilation.IsValidCompilation();
                if (!result.isValid) return;
                compilation = result.newCompilation;

                // all types
                HashSet<ITypeSymbol> allTypes = new(TupleSanitizedEqualityComparer.Default);

                // process all scanned type syntaxes (generic, array, nullable, tuple, parametrized nino types)
                foreach (var syntax in typeSyntaxes)
                {
                    var typeSymbol = syntax.GetTypeSymbol(compilation);
                    if (typeSymbol != null
                        && typeSymbol.IsAccessible()
                        && typeSymbol.CheckGenericValidity())
                    {
                        var type = typeSymbol.GetNormalizedTypeSymbol().GetPureType();
                        allTypes.Add(type);
                    }
                }

                // record all array element and generic type arguments
                Stack<ITypeSymbol> toProcess = new(allTypes);
                while (toProcess.Count > 0)
                {
                    var currentType = toProcess.Pop();
                    if (currentType is INamedTypeSymbol namedType && namedType.IsGenericType)
                    {
                        foreach (var arg in namedType.TypeArguments)
                        {
                            var pureArg = arg.GetNormalizedTypeSymbol().GetPureType();
                            if (allTypes.Add(pureArg))
                                toProcess.Push(pureArg);
                        }
                    }
                    else if (currentType is IArrayTypeSymbol arrayType)
                    {
                        var elemType = arrayType.ElementType.GetNormalizedTypeSymbol().GetPureType();
                        if (allTypes.Add(elemType))
                            toProcess.Push(elemType);
                    }
                }

                // process all explicitly marked nino types
                foreach (var ninoSyntax in ninoTypeSyntaxes)
                {
                    var typeSymbol = ninoSyntax.GetTypeSymbol(compilation);
                    if (typeSymbol != null
                        && typeSymbol.DeclaredAccessibility == Accessibility.Public
                        && typeSymbol.CheckGenericValidity())
                    {
                        var type = typeSymbol.GetNormalizedTypeSymbol().GetPureType();
                        allTypes.Add(type);
                    }
                }

                // parametrized nino types + concrete nino types
                HashSet<ITypeSymbol> ninoTypeSymbols = new(TupleSanitizedEqualityComparer.Default);
                // all recognizable potential types that might be serialized/deserialized
                HashSet<ITypeSymbol> potentialTypeSymbols = new(TupleSanitizedEqualityComparer.Default);

                // separate nino types and potential types
                foreach (var type in allTypes)
                {
                    if (type.IsNinoType())
                        ninoTypeSymbols.Add(type);
                    else
                        potentialTypeSymbols.Add(type);
                }

                NinoGraph graph;
                HashSet<NinoType> ninoTypes;
                try
                {
                    CSharpParser parser = new(ninoTypeSymbols);
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

                    spc.AddSource("NinoGraph.Error.g.cs",
                        $"/*\nParser failed: {e.Message}\nStack Trace:\n{e.StackTrace}\n*/");

                    // Create minimal fallback to prevent complete failure
                    graph = new NinoGraph(compilation, new HashSet<NinoType>());
                    ninoTypes = new HashSet<NinoType>();
                }

                // for each nino type, add its members' types to potential types
                foreach (var ninoType in ninoTypes)
                {
                    // add members' types to potential types
                    foreach (var member in ninoType.Members)
                    {
                        potentialTypeSymbols.Add(member.Type);
                    }
                }

                var distinctNinoTypes = ninoTypes.Distinct().ToList();
                var potentialTypes = potentialTypeSymbols
                    .Distinct(TupleSanitizedEqualityComparer.Default)
                    .OrderBy(static t => t.GetTypeHierarchyLevel())
                    .ToList();

                HashSet<ITypeSymbol> generatedTypes = new(TupleSanitizedEqualityComparer.Default);

                NinoBuiltInTypeGenerator[] builtInGenerators =
                {
                    new NullableGenerator(graph, potentialTypeSymbols, generatedTypes, compilation),
                    new KeyValuePairGenerator(graph, potentialTypeSymbols, generatedTypes, compilation),
                    new TupleGenerator(graph, potentialTypeSymbols, generatedTypes, compilation),
                    new ArrayGenerator(graph, potentialTypeSymbols, generatedTypes, compilation),
                    new DictionaryGenerator(graph, potentialTypeSymbols, generatedTypes, compilation),
                    new ListGenerator(graph, potentialTypeSymbols, generatedTypes, compilation),
                    new ArraySegmentGenerator(graph, potentialTypeSymbols, generatedTypes, compilation),
                    new QueueGenerator(graph, potentialTypeSymbols, generatedTypes, compilation),
                    new StackGenerator(graph, potentialTypeSymbols, generatedTypes, compilation),
                    new HashSetGenerator(graph, potentialTypeSymbols, generatedTypes, compilation),
                    new LinkedListGenerator(graph, potentialTypeSymbols, generatedTypes, compilation),
                };

                foreach (var type in potentialTypes)
                {
                    foreach (var generator in builtInGenerators)
                    {
                        if (generator.Filter(type))
                        {
                            generatedTypes.Add(type);
                            break;
                        }
                    }
                }

                foreach (var generator in builtInGenerators)
                {
                    ExecuteGenerator(generator, spc);
                }

                ExecuteGenerator(new TypeConstGenerator(compilation, graph, distinctNinoTypes), spc);
                ExecuteGenerator(new UnsafeAccessorGenerator(compilation, graph, distinctNinoTypes), spc);
                ExecuteGenerator(new PartialClassGenerator(compilation, graph, distinctNinoTypes), spc);
                ExecuteGenerator(new SerializerGenerator(compilation, graph, distinctNinoTypes), spc);
                ExecuteGenerator(new DeserializerGenerator(compilation, graph, distinctNinoTypes), spc);
            }
            catch (Exception e)
            {
                // Report but don't fail completely - let build succeed
                spc.ReportDiagnostic(Diagnostic.Create(
                    new DiagnosticDescriptor("NINO998", "Nino Generator Warning",
                        $"Generator encountered issue but continuing: {e.Message}",
                        "Nino.Generator",
                        DiagnosticSeverity.Warning, true), Location.None));
            }
        });
    }

    private static void ExecuteGenerator<T>(T generator, SourceProductionContext spc) where T : NinoGenerator
    {
        var generatorName = typeof(T).Name;
        try
        {
            generator.Execute(spc);
        }
        catch (Exception ex)
        {
            // Report specific generator failure with details
            spc.ReportDiagnostic(Diagnostic.Create(
                new DiagnosticDescriptor("NINO999",
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