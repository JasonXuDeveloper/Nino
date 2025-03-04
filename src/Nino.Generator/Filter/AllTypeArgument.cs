using System;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace Nino.Generator.Filter;

public class AllTypeArgument(Func<ITypeSymbol, bool> filter) : IFilter
{
    public bool Filter(ITypeSymbol symbol)
    {
        if (symbol is INamedTypeSymbol namedTypeSymbol)
        {
            return namedTypeSymbol.TypeArguments.All(filter);
        }

        return false;
    }
}