using System;
using System.Collections.Generic;
using Microsoft.CodeAnalysis;

namespace Nino.Generator.Metadata;

public class SymbolTupleEqualityComparer : IEqualityComparer<(ITypeSymbol, ITypeSymbol)>
{
    public static readonly SymbolTupleEqualityComparer Default = new();

    public bool Equals((ITypeSymbol, ITypeSymbol) x, (ITypeSymbol, ITypeSymbol) y)
    {
        return SymbolEqualityComparer.Default.Equals(x.Item1, y.Item1) &&
               SymbolEqualityComparer.Default.Equals(x.Item2, y.Item2);
    }

    public int GetHashCode((ITypeSymbol, ITypeSymbol) obj)
    {
        unchecked // Allow overflow
        {
            int hash = 17;
            hash = hash * 23 + (obj.Item1 != null ? SymbolEqualityComparer.Default.GetHashCode(obj.Item1) : 0);
            hash = hash * 23 + (obj.Item2 != null ? SymbolEqualityComparer.Default.GetHashCode(obj.Item2) : 0);
            return hash;
        }
    }
}

public class SymbolConversionEqualityComparer : IEqualityComparer<(ITypeSymbol From, ITypeSymbol To)>
{
    public static readonly SymbolConversionEqualityComparer Default = new();

    public bool Equals((ITypeSymbol From, ITypeSymbol To) x, (ITypeSymbol From, ITypeSymbol To) y)
    {
        return SymbolEqualityComparer.Default.Equals(x.From, y.From) &&
               SymbolEqualityComparer.Default.Equals(x.To, y.To);
    }

    public int GetHashCode((ITypeSymbol From, ITypeSymbol To) obj)
    {
        unchecked // Allow overflow
        {
            int hash = 17;
            hash = hash * 23 + (obj.From != null ? SymbolEqualityComparer.Default.GetHashCode(obj.From) : 0);
            hash = hash * 23 + (obj.To != null ? SymbolEqualityComparer.Default.GetHashCode(obj.To) : 0);
            return hash;
        }
    }
}
