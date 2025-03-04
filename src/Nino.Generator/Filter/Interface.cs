using System.Linq;
using Microsoft.CodeAnalysis;

namespace Nino.Generator.Filter;

public class Interface(string interfaceName) : IFilter
{
    public bool Filter(ITypeSymbol symbol)
    {
        if (symbol.TypeKind == TypeKind.Interface)
            return symbol.Name == interfaceName;

        var interfaceTypes = symbol.AllInterfaces;
        return interfaceTypes.Any(type => type.Name == interfaceName);
    }
}