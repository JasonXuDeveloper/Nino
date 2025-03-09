using System;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace Nino.Generator.Filter;

public class ValidIndexer
    : IFilter
{
    private readonly Func<ITypeSymbol, IPropertySymbol, bool> _validIndexer;
    
    public ValidIndexer(Func<ITypeSymbol, IPropertySymbol, bool> validIndexer)
    {
        _validIndexer = validIndexer;
    }
    
    public bool Filter(ITypeSymbol symbol)
    {
        var indexers = symbol
            .GetMembers()
            .OfType<IPropertySymbol>()
            .Where(p => p.IsIndexer)
            .ToList();

        if (!indexers.Any()) return false;

        var validIndexers = indexers.Where(i => _validIndexer(symbol, i)).ToList();
        if (!validIndexers.Any()) return false;

        //ensure the valid indexer has public getter and setter
        var hasValidIndexer = validIndexers.Any(p =>
            p.GetMethod?.DeclaredAccessibility == Accessibility.Public &&
            p.SetMethod?.DeclaredAccessibility == Accessibility.Public);

        return hasValidIndexer;
    }
}