using System;
using Microsoft.CodeAnalysis;

namespace Nino.Generator.Filter;

public class Array : IFilter
{
    private readonly Func<IArrayTypeSymbol, bool>? _filter;

    public Array(Func<IArrayTypeSymbol, bool>? filter = null)
    {
        _filter = filter;
    }

    public bool Filter(ITypeSymbol symbol)
    {
        if (symbol.TypeKind == TypeKind.Array)
        {
            return _filter?.Invoke((IArrayTypeSymbol)symbol) ?? true;
        }

        return false;
    }
}