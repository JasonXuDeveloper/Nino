using System;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace Nino.Generator.Filter;

public class ValidMethod
    : IFilter
{
    private readonly Func<ITypeSymbol, IMethodSymbol, bool> _validMethod;
    
    public ValidMethod(Func<ITypeSymbol, IMethodSymbol, bool> validMethod)
    {
        _validMethod = validMethod;
    }
    
    public bool Filter(ITypeSymbol symbol)
    {
        var methods = symbol
            .GetMembers()
            .OfType<IMethodSymbol>()
            .ToList();

        if (!methods.Any()) return false;

        var validMethodCandidate = methods.Where(c => _validMethod(symbol, c)).ToList();

        // ensure valid constructors are public
        return validMethodCandidate.Any(p => p.DeclaredAccessibility == Accessibility.Public);
    }
}