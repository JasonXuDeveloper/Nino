using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace Nino.Generator.Filter;

public class Trivial(params string[] trivialTypes) : IFilter
{
    private readonly HashSet<string> _trivialTypes = new(trivialTypes);

    public bool Filter(ITypeSymbol symbol)
    {
        return _trivialTypes.Contains(symbol.Name) || symbol.AllInterfaces.Any(i => _trivialTypes.Contains(i.Name));
    }
}