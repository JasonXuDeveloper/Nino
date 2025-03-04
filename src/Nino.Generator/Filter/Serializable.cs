using Microsoft.CodeAnalysis;

namespace Nino.Generator.Filter;

public class Serializable: IFilter
{
    public bool Filter(ITypeSymbol symbol)
    {
        return symbol.IsSerializableType();
    }
}