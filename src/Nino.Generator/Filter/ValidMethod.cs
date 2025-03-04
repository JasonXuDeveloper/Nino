using System;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace Nino.Generator.Filter;

public class ValidMethod(Func<ITypeSymbol, IMethodSymbol, bool> validMethod)
    : IFilter
{
    public bool Filter(ITypeSymbol symbol)
    {
        var methods = symbol
            .GetMembers()
            .OfType<IMethodSymbol>()
            .ToList();

        if (!methods.Any()) return false;

        var validConstructors = methods.Where(c => validMethod(symbol, c)).ToList();

        // ensure valid constructors are public
        return validConstructors.Any(p => p.DeclaredAccessibility == Accessibility.Public);
    }
}