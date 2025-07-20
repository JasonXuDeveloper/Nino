using System;
using Microsoft.CodeAnalysis;

namespace Nino.Generator.Filter;

#nullable disable
public class Interface(string interfaceName, Func<INamedTypeSymbol, bool> restriction = null)
    : IFilter
{
    public bool Filter(ITypeSymbol symbol)
    {
        // Fast path: check if the symbol itself matches
        if (IsMatchingInterface(symbol, out var matchedSymbol))
        {
            return restriction?.Invoke(matchedSymbol) ?? true;
        }

        // Check base type hierarchy
        var baseType = symbol.BaseType;
        while (baseType != null)
        {
            if (IsMatchingInterface(baseType, out matchedSymbol))
            {
                return restriction?.Invoke(matchedSymbol) ?? true;
            }
            baseType = baseType.BaseType;
        }

        // Check implemented interfaces
        foreach (var interfaceType in symbol.AllInterfaces)
        {
            if (IsMatchingInterface(interfaceType, out matchedSymbol))
            {
                return restriction?.Invoke(matchedSymbol) ?? true;
            }
        }

        return false;
    }

    private bool IsMatchingInterface(ITypeSymbol typeSymbol, out INamedTypeSymbol namedTypeSymbol)
    {
        namedTypeSymbol = typeSymbol as INamedTypeSymbol ?? typeSymbol.OriginalDefinition as INamedTypeSymbol;

        if (namedTypeSymbol == null)
            return false;

        // Fast path: check name first (much faster than ToDisplayString)
        var symbolName = namedTypeSymbol.Name;
        var interfaceBaseName = GetInterfaceBaseName();

        // Quick name-based filtering to avoid expensive ToDisplayString calls
        if (!symbolName.Equals(interfaceBaseName, StringComparison.Ordinal) &&
            !symbolName.StartsWith(interfaceBaseName, StringComparison.Ordinal))
        {
            return false;
        }

        // Only use expensive ToDisplayString when name suggests a potential match
        return namedTypeSymbol.OriginalDefinition.GetDisplayString().EndsWith(interfaceName, StringComparison.Ordinal);
    }

    private string GetInterfaceBaseName()
    {
        var genericIndex = interfaceName.IndexOf('<');
        return genericIndex > 0 ? interfaceName.Substring(0, genericIndex) : interfaceName;
    }
}