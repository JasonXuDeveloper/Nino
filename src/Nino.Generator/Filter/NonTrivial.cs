using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace Nino.Generator.Filter;

public class NonTrivial(string baseType, params string[] trivialTypes) : IFilter
{
    private readonly HashSet<string> _trivialTypes = new(trivialTypes);

    public bool Filter(ITypeSymbol symbol)
    {
        return symbol.AllInterfaces.Any(i => i.Name.StartsWith(baseType)) &&
               !_trivialTypes.Any(t => symbol.Name.StartsWith(t));
    }
}