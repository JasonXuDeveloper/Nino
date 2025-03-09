using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace Nino.Generator.Filter;

public class Trivial : IFilter
{
    private readonly HashSet<string> _trivialTypes;

    public Trivial(params string[] trivialTypes)
    {
        _trivialTypes = new HashSet<string>(trivialTypes);
    }

    public bool Filter(ITypeSymbol symbol)
    {
        return _trivialTypes.Contains(symbol.Name) || symbol.AllInterfaces.Any(i => _trivialTypes.Contains(i.Name));
    }
}