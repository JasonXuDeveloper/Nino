using Microsoft.CodeAnalysis;

namespace Nino.Generator.Filter;

public class String: IFilter
{
    public bool Filter(ITypeSymbol symbol)
    {
        return symbol.SpecialType == SpecialType.System_String;
    }
}