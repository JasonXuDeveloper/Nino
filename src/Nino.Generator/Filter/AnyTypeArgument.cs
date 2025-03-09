using System;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace Nino.Generator.Filter;

public class AnyTypeArgument : IFilter
{
    private readonly Func<ITypeSymbol, bool> _filter;

    public AnyTypeArgument(Func<ITypeSymbol, bool> filter)
    {
        _filter = filter;
    }

    public bool Filter(ITypeSymbol symbol)
    {
        if (symbol is INamedTypeSymbol namedTypeSymbol)
        {
            return namedTypeSymbol.TypeArguments.Any(_filter);
        }

        return false;
    }
}