using Microsoft.CodeAnalysis;

namespace Nino.Generator.Generation;

/// <summary>What generated code in one assembly can name, read and assign.</summary>
internal sealed class Visibility
{
    private readonly IAssemblySymbol _assembly;

    public Visibility(IAssemblySymbol assembly) => _assembly = assembly;

    /// <summary>Whether a formatter can be generated for the type (it must be visible to generated code).</summary>
    public static bool IsEligible(INamedTypeSymbol type) =>
        !type.IsStatic && !type.IsRefLikeType &&
        type.TypeKind is TypeKind.Class or TypeKind.Struct or TypeKind.Interface && IsAccessibleChain(type);

    /// <summary>Whether the type and its containing types are all public or internal.</summary>
    public static bool IsAccessibleChain(INamedTypeSymbol type)
    {
        for (ISymbol? s = type; s is INamedTypeSymbol t; s = t.ContainingType)
        {
            if (t.DeclaredAccessibility is not (Accessibility.Public or Accessibility.Internal
                or Accessibility.ProtectedOrInternal))
                return false;
        }

        return true;
    }

    /// <summary>Whether <paramref name="type"/> belongs to this assembly and generated code can name it.</summary>
    public bool IsOwnEligible(INamedTypeSymbol type) =>
        SymbolEqualityComparer.Default.Equals(type.ContainingAssembly, _assembly) && IsEligible(type.OriginalDefinition);

    /// <summary>Whether this compilation generates a formatter for <paramref name="type"/>.</summary>
    public bool IsGeneratedHere(INamedTypeSymbol type) => IsOwnEligible(type) && type.OriginalDefinition.IsNinoType();

    /// <summary>
    /// Whether generated code in this assembly can name <paramref name="symbol"/> (a member, accessor or
    /// constructor): public, or internal to this assembly. Null (no accessor) counts as visible.
    /// </summary>
    public bool IsVisible(ISymbol? symbol) => symbol == null || symbol.DeclaredAccessibility switch
    {
        Accessibility.Public => true,
        Accessibility.Internal or Accessibility.ProtectedOrInternal =>
            SymbolEqualityComparer.Default.Equals(symbol.ContainingAssembly, _assembly),
        _ => false,
    };

    /// <summary>Whether generated code can read and assign the member directly (<c>value.Member</c>).</summary>
    public bool IsDirectlyAccessible(ISymbol member) =>
        IsVisible(member) && (member is not IPropertySymbol property ||
                              (IsVisible(property.GetMethod) && IsVisible(property.SetMethod)));

    /// <summary>Whether the member can only be assigned while constructing (readonly field, init-only or no setter).</summary>
    public static bool IsConstructionOnly(ISymbol member) => member switch
    {
        IFieldSymbol field => field.IsReadOnly,
        IPropertySymbol property => property.SetMethod == null || property.SetMethod.IsInitOnly,
        _ => false,
    };
}
