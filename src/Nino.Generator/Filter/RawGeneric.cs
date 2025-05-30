using System.Linq;
using Microsoft.CodeAnalysis;

namespace Nino.Generator.Filter;

public class RawGeneric : IFilter
{
    private bool IsTypeParameter(ITypeSymbol typeSymbol)
    {
        if (typeSymbol.TypeKind == TypeKind.TypeParameter) return true;
        if (typeSymbol is INamedTypeSymbol namedTypeSymbol)
        {
            return namedTypeSymbol.TypeArguments.Any(IsTypeParameter);
        }

        if (typeSymbol is IArrayTypeSymbol arrayTypeSymbol)
        {
            return IsTypeParameter(arrayTypeSymbol.ElementType);
        }

        return false;
    }

    public bool Filter(ITypeSymbol symbol)
    {
        if (symbol is INamedTypeSymbol namedTypeSymbol)
        {
            if (!namedTypeSymbol.IsGenericType)
                return false;

            if (namedTypeSymbol.TypeArguments.Any(IsTypeParameter))
                return true;
        }

        if (symbol is IArrayTypeSymbol arrayTypeSymbol)
        {
            return IsTypeParameter(arrayTypeSymbol.ElementType);
        }

        return false;
    }
}