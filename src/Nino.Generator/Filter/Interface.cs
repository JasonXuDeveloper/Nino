using System;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace Nino.Generator.Filter;

public class Interface : IFilter
{
    private readonly string _interfaceName;
    private readonly Func<INamedTypeSymbol, bool>? _restriction;

    public Interface(string interfaceName, Func<INamedTypeSymbol, bool>? restriction = null)
    {
        _interfaceName = interfaceName;
        _restriction = restriction;
    }

    public bool Filter(ITypeSymbol symbol)
    {
        var baseType = symbol;
        while (baseType != null)
        {
            if (baseType.OriginalDefinition.ToDisplayString().EndsWith(_interfaceName))
            {
                if (_restriction != null)
                {
                    return _restriction((INamedTypeSymbol)baseType);
                }

                return true;
            }

            baseType = baseType.BaseType;
        }

        if (symbol.OriginalDefinition.ToDisplayString().EndsWith(_interfaceName))
        {
            if (_restriction != null)
            {
                return _restriction((INamedTypeSymbol)symbol);
            }

            return true;
        }

        var interfaceSymbol =
            symbol.AllInterfaces.FirstOrDefault(type =>
                type.OriginalDefinition.ToDisplayString().EndsWith(_interfaceName));
        if (interfaceSymbol != null)
        {
            if (_restriction != null)
            {
                return _restriction(interfaceSymbol);
            }

            return true;
        }

        return false;
    }
}