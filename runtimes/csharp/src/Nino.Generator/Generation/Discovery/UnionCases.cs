using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Nino.Generator.Generation;

/// <summary>Recognizes C# union structs without depending on new Roslyn APIs.</summary>
internal static class UnionCases
{
    public static bool IsUnion(INamedTypeSymbol type)
    {
        if (type.TypeKind != TypeKind.Struct || type.GetMembers("Value").Length == 0)
            return false;
        foreach (var reference in type.DeclaringSyntaxReferences)
        {
            if (reference.GetSyntax() is TypeDeclarationSyntax { Keyword.Text: "union" })
                return true;
        }

        // The compiler synthesizes this attribute on emit.
        if (type.DeclaringSyntaxReferences.Length == 0)
        {
            foreach (var attribute in type.GetAttributes())
            {
                if (attribute.AttributeClass is { } marker &&
                    TypeNames.HasMetadataName(marker, "System.Runtime.CompilerServices.UnionAttribute"))
                    return true;
            }
        }

        return false;
    }

    /// <summary>The compiler's case constructors, in declaration order and with substituted type arguments.</summary>
    public static IEnumerable<ITypeSymbol> Of(INamedTypeSymbol type)
    {
        foreach (var constructor in type.InstanceConstructors)
        {
            if (constructor.DeclaredAccessibility == Accessibility.Public && constructor.Parameters.Length == 1)
                yield return constructor.Parameters[0].Type;
        }
    }
}
