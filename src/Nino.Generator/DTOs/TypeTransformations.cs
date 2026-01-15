using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Nino.Generator.DTOs;

/// <summary>
/// Transforms Roslyn ISymbol objects into value-based DTOs for incremental caching.
/// </summary>
public static class TypeTransformations
{
    /// <summary>
    /// Extract compilation metadata into a value-based DTO.
    /// </summary>
    public static CompilationMetadataDto ExtractCompilationMetadata(Compilation compilation)
    {
        var isUnityAssembly = compilation.ReferencedAssemblyNames.Any(a =>
            a.Name == "UnityEngine" ||
            a.Name == "UnityEngine.CoreModule" ||
            a.Name == "UnityEditor");

        var hasNinoCoreUsage = compilation.SyntaxTrees.Any(tree =>
            tree.GetRoot().DescendantNodes()
                .OfType<UsingDirectiveSyntax>()
                .Any(u => u.Name.ToString().Contains("Nino.Core")));

        var referencedAssemblyNames = compilation.ReferencedAssemblyNames
            .Select(a => a.Name)
            .ToArray();

        return new CompilationMetadataDto(
            AssemblyName: compilation.AssemblyName ?? "Unknown",
            IsUnityAssembly: isUnityAssembly,
            HasNinoCoreUsage: hasNinoCoreUsage,
            ReferencedAssemblyNames: new EquatableArray<string>(referencedAssemblyNames)
        );
    }
}
