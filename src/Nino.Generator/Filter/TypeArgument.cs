using System;
using Microsoft.CodeAnalysis;

namespace Nino.Generator.Filter;

public class TypeArgument(int index, Func<ITypeSymbol, bool> filter) : IFilter
{
    public bool Filter(ITypeSymbol symbol)
    {
        if (symbol is INamedTypeSymbol namedTypeSymbol)
        {
            if (namedTypeSymbol.TypeArguments.Length > index)
            {
                return filter(namedTypeSymbol.TypeArguments[index]);
            }
        }

        return false;
    }
}