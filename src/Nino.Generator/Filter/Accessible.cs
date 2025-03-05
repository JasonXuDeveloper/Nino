using System.Linq;
using Microsoft.CodeAnalysis;

namespace Nino.Generator.Filter;

public class Accessible : IFilter
{
    private bool IsAccessibleType(ITypeSymbol typeSymbol)
    {
        if (typeSymbol.DeclaredAccessibility == Accessibility.NotApplicable) return true;
        if (typeSymbol.DeclaredAccessibility != Accessibility.Public) return false;
        var ret = typeSymbol switch
        {
            INamedTypeSymbol namedTypeSymbol => namedTypeSymbol.TypeArguments.All(IsAccessibleType),
            IArrayTypeSymbol arrayTypeSymbol => IsAccessibleType(arrayTypeSymbol.ElementType),
            _ => true
        };

        if (ret && typeSymbol.ContainingType != null)
        {
            return IsAccessibleType(typeSymbol.ContainingType);
        }

        return ret;
    }


    public bool Filter(ITypeSymbol symbol)
    {
        return IsAccessibleType(symbol);
    }
}