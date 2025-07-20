using System;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace Nino.Generator.Filter;

public class ValidProperty(Func<ITypeSymbol, IPropertySymbol, bool> validMethod) : IFilter
{
    public bool Filter(ITypeSymbol symbol)
    {
        var properties = symbol.GetMembers().OfType<IPropertySymbol>().ToList();

        if (!properties.Any()) return false;

        var validProperty = properties.Where(c => validMethod(symbol, c)).ToList();

        if (!validProperty.Any()) return false;
        
        // ensure valid constructors are public
        return validProperty.Any(p => p.DeclaredAccessibility == Accessibility.Public);
    }
}