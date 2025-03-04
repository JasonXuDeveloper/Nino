using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace Nino.Generator.Filter;

public class NonTrivial : IFilter
{
    private readonly string _baseType;
    private readonly HashSet<string> _trivialTypes;

    public NonTrivial(string baseType, params string[] trivialTypes)
    {
        _baseType = baseType;
        _trivialTypes = new HashSet<string>(trivialTypes);
    }

    public bool Filter(ITypeSymbol symbol)
    {
        return symbol.AllInterfaces.Any(i => i.Name.StartsWith(_baseType)) &&
               !_trivialTypes.Any(t => symbol.Name.StartsWith(t));
    }
}