using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
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
        // Scan types directly marked with [NinoType]
        var ninoTypeModels = context.GetTypeSyntaxes()
            .Where(static syntax => syntax != null)
            .Collect();

        // Scan all type declarations (including those that may inherit from NinoType)
        var allTypeDeclarations = context.SyntaxProvider
            .CreateSyntaxProvider<CSharpSyntaxNode>(
                static (node, _) => node is TypeDeclarationSyntax,
                static (context, _) => (CSharpSyntaxNode)context.Node
            )
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
            .Combine(ninoTypeModels)
            .Combine(allTypeDeclarations);

        // Add explicit caching and error boundaries
        context.RegisterSourceOutput(merged, static (spc, source) =>
        {
            var compilation = source.Left.Left.Left;
            var typeSyntaxes = source.Left.Left.Right;
            var ninoTypeSyntaxes = source.Left.Right;
            var allTypeDeclarations = source.Right;

            // Add stability check
            if (compilation == null) return;

            try
            {
                var (isValid, newCompilation, isUnityAssembly) = compilation.IsValidCompilation();
                if (!isValid) return;
                compilation = newCompilation;

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

                    // explore members of nino types
                    if (currentType.IsNinoType())
                    {
                        var members = currentType.GetMembers();
                        foreach (var member in members)
                        {
                            if (member is IPropertySymbol prop)
                            {
                                var propType = prop.Type.GetNormalizedTypeSymbol().GetPureType();
                                if (allTypes.Add(propType))
                                    toProcess.Push(propType);
                            }
                            else if (member is IFieldSymbol field)
                            {
                                var fieldType = field.Type.GetNormalizedTypeSymbol().GetPureType();
                                if (allTypes.Add(fieldType))
                                    toProcess.Push(fieldType);
                            }
                        }
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

                // process all type declarations (including those inheriting from NinoType)
                foreach (var typeSyntax in allTypeDeclarations)
                {
                    var typeSymbol = typeSyntax.GetTypeSymbol(compilation);
                    if (typeSymbol != null
                        && typeSymbol.DeclaredAccessibility == Accessibility.Public
                        && typeSymbol.CheckGenericValidity()
                        && typeSymbol.IsNinoType()) // IsNinoType now checks inheritance
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
                HashSet<ITypeSymbol> generatedTypes = new(TupleSanitizedEqualityComparer.Default);

                ExecuteGenerator(
                    new NinoBuiltInTypesGenerator(graph, potentialTypeSymbols, generatedTypes, compilation, isUnityAssembly), spc);
                ExecuteGenerator(new TypeConstGenerator(compilation, graph, distinctNinoTypes), spc);
                ExecuteGenerator(new UnsafeAccessorGenerator(compilation, graph, distinctNinoTypes), spc);
                ExecuteGenerator(new PartialClassGenerator(compilation, graph, distinctNinoTypes), spc);
                ExecuteGenerator(new SerializerGenerator(compilation, graph, distinctNinoTypes, generatedTypes, isUnityAssembly), spc);
                ExecuteGenerator(new DeserializerGenerator(compilation, graph, distinctNinoTypes, generatedTypes, isUnityAssembly), spc);
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