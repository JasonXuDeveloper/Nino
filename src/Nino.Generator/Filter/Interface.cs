using System;
using System.Linq;
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
            if (baseType.OriginalDefinition.ToDisplayString().EndsWith(interfaceName))
            {
                if (restriction != null)
                {
                    return restriction((INamedTypeSymbol)baseType);
                }

                return true;
            }

            baseType = baseType.BaseType;
        }

        if (symbol.OriginalDefinition.ToDisplayString().EndsWith(interfaceName))
        {
            if (restriction != null)
            {
                return restriction((INamedTypeSymbol)symbol);
            }

            return true;
        }

        var interfaceSymbol =
            symbol.AllInterfaces.FirstOrDefault(type =>
                type.OriginalDefinition.ToDisplayString().EndsWith(interfaceName));
        if (interfaceSymbol != null)
        {
            if (restriction != null)
            {
                return restriction(interfaceSymbol);
            }

            return true;
        }

        return false;
    }
}