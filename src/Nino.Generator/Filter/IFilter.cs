using Microsoft.CodeAnalysis;

namespace Nino.Generator.Filter;

public interface IFilter
{
    bool Filter(ITypeSymbol symbol);
}