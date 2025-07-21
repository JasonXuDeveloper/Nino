using System;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace Nino.Generator.Filter;

public class ValidIndexer(Func<ITypeSymbol, IPropertySymbol, bool> validIndexer) : IFilter
{
    public bool Filter(ITypeSymbol symbol)
    {
        var indexers = symbol
            .GetMembers()
            .OfType<IPropertySymbol>()
            .Where(p => p.IsIndexer)
            .ToList();

        return indexers.Count > 0 && indexers.Any(i => validIndexer(symbol, i));
    }
}