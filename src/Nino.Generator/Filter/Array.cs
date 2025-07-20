using System;
using Microsoft.CodeAnalysis;

namespace Nino.Generator.Filter;

public class Array(Func<IArrayTypeSymbol, bool>? filter = null) : IFilter
{
    public bool Filter(ITypeSymbol symbol)
    {
        if (symbol.TypeKind == TypeKind.Array)
        {
            return filter?.Invoke((IArrayTypeSymbol)symbol) ?? true;
        }

        return false;
    }
}