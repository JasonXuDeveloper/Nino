using System;
using Microsoft.CodeAnalysis;

namespace Nino.Generator.Filter;

public class Interface(string interfaceName, Func<INamedTypeSymbol, bool>? restriction = null)
    : IFilter
{
    public bool Filter(ITypeSymbol symbol)
    {
        var baseType = symbol;
        while (baseType != null)
        {
            if (baseType.OriginalDefinition.GetDisplayString().EndsWith(interfaceName))
            {
                return restriction == null || restriction((INamedTypeSymbol)baseType);
            }

            baseType = baseType.BaseType;
        }

        if (symbol.OriginalDefinition.GetDisplayString().EndsWith(interfaceName))
        {
            return restriction == null || restriction((INamedTypeSymbol)symbol);
        }

        foreach (var interfaceType in symbol.AllInterfaces)
        {
            if (interfaceType.OriginalDefinition.GetDisplayString().EndsWith(interfaceName))
            {
                return restriction == null || restriction(interfaceType);
            }
        }

        return false;
    }
}