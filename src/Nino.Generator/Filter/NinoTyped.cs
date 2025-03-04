using Microsoft.CodeAnalysis;

namespace Nino.Generator.Filter;

public class NinoTyped: IFilter
{
    public bool Filter(ITypeSymbol symbol)
    {
        return symbol.IsNinoType();
    }
}