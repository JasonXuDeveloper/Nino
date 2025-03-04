using Microsoft.CodeAnalysis;

namespace Nino.Generator.Filter;

public class Nullable : IFilter
{
    public bool Filter(ITypeSymbol symbol)
    {
        return symbol.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T;
    }
}