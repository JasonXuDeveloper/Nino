using Microsoft.CodeAnalysis;

namespace Nino.Generator.Filter.Operation;

public class Not(IFilter filter) : IFilter
{
    public bool Filter(ITypeSymbol symbol)
    {
        return !filter.Filter(symbol);
    }
}