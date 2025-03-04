using System.Linq;
using Microsoft.CodeAnalysis;

namespace Nino.Generator.Filter;

public class Accessible : IFilter
{
    private bool IsAccessibleType(ITypeSymbol typeSymbol)
    {
        if (typeSymbol.DeclaredAccessibility == Accessibility.Private) return false;
        if (typeSymbol.DeclaredAccessibility == Accessibility.Protected) return false;
        return typeSymbol switch
        {
            INamedTypeSymbol namedTypeSymbol => namedTypeSymbol.TypeArguments.All(IsAccessibleType),
            IArrayTypeSymbol arrayTypeSymbol => IsAccessibleType(arrayTypeSymbol.ElementType),
            _ => true
        };
    }


    public bool Filter(ITypeSymbol symbol)
    {
        return IsAccessibleType(symbol);
    }
}