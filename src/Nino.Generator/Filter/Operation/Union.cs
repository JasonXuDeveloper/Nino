using System.Collections.Generic;
using Microsoft.CodeAnalysis;

namespace Nino.Generator.Filter.Operation;

public class Union : IFilter
{
    private readonly List<IFilter> _filters = new();

    public Union With(params IFilter[] filter)
    {
        _filters.AddRange(filter);
        return this;
    }

    public bool Filter(ITypeSymbol symbol)
    {
        foreach (var filter in _filters)
        {
            if (filter.Filter(symbol))
                return true;
        }

        return false;
    }
}