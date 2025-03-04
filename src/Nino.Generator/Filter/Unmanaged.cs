using Microsoft.CodeAnalysis;

namespace Nino.Generator.Filter;

public class Unmanaged: IFilter
{
    public bool Filter(ITypeSymbol symbol)
    {
        return symbol.IsUnmanagedType;
    }
}