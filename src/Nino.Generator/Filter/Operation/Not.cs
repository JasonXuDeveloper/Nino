using Microsoft.CodeAnalysis;

namespace Nino.Generator.Filter.Operation;

public class Not: IFilter
{
    private readonly IFilter _filter;

    public Not(IFilter filter)
    {
        _filter = filter;
    }

    public bool Filter(ITypeSymbol symbol)
    {
        return !_filter.Filter(symbol);
    }
}