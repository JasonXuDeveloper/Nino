using System;
using Microsoft.CodeAnalysis;

namespace Nino.Generator.Filter;

public class TypeArgument : IFilter
{
    private readonly int _index;
    private readonly Func<ITypeSymbol, bool> _filter;
    
    public TypeArgument(int index, Func<ITypeSymbol, bool> filter)
    {
        _index = index;
        _filter = filter;
    }
    
    public bool Filter(ITypeSymbol symbol)
    {
        if (symbol is INamedTypeSymbol namedTypeSymbol)
        {
            if (namedTypeSymbol.TypeArguments.Length > _index)
            {
                return _filter(namedTypeSymbol.TypeArguments[_index]);
            }
        }

        return false;
    }
}