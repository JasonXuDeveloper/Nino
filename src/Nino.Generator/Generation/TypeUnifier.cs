using Microsoft.CodeAnalysis;

namespace Nino.Generator.Generation;

/// <summary>
/// Matches a type written in a formatter's type parameters (a pattern such as <c>List&lt;T&gt;</c>) against a
/// concrete type, binding the parameters by ordinal. A bound parameter must match exactly (nullable annotations
/// aside). Bindings are only committed when the whole match succeeds.
/// </summary>
internal static class TypeUnifier
{
    public static bool Unify(ITypeSymbol pattern, ITypeSymbol actual, ITypeSymbol?[] types)
    {
        var trial = (ITypeSymbol?[])types.Clone();
        if (!Match(pattern, actual, trial))
            return false;
        trial.CopyTo(types, 0);
        return true;
    }

    /// <summary>
    /// Unifies <paramref name="pattern"/> with <paramref name="actual"/> or the first of its base types and
    /// interfaces of the same definition (for type constraints such as <c>where TList : List&lt;T&gt;</c>).
    /// </summary>
    public static bool UnifyWithSupertype(ITypeSymbol pattern, ITypeSymbol actual, ITypeSymbol?[] types)
    {
        if (Unify(pattern, actual, types))
            return true;
        if (pattern is not INamedTypeSymbol named || actual is not INamedTypeSymbol)
            return false;
        var definition = named.OriginalDefinition;
        if (named.TypeKind == TypeKind.Interface)
        {
            // A type implementing the interface for two different arguments (ICollection<int> and
            // ICollection<string>) is ambiguous: it matches neither, whatever order the interfaces are listed in.
            ITypeSymbol?[]? found = null;
            foreach (var i in actual.AllInterfaces)
            {
                if (!SymbolEqualityComparer.Default.Equals(i.OriginalDefinition, definition))
                    continue;
                var trial = (ITypeSymbol?[])types.Clone();
                if (!Unify(pattern, i, trial))
                    continue;
                if (found != null && !SameBindings(found, trial))
                    return false;
                found = trial;
            }

            if (found == null)
                return false;
            found.CopyTo(types, 0);
            return true;
        }

        for (var b = actual.BaseType; b != null; b = b.BaseType)
        {
            if (SymbolEqualityComparer.Default.Equals(b.OriginalDefinition, definition))
                return Unify(pattern, b, types);
        }

        return false;
    }

    private static bool SameBindings(ITypeSymbol?[] a, ITypeSymbol?[] b)
    {
        for (int i = 0; i < a.Length; i++)
        {
            if (!SymbolEqualityComparer.Default.Equals(a[i], b[i]))
                return false;
        }

        return true;
    }

    /// <summary>Whether every formatter type parameter in <paramref name="type"/> is bound.</summary>
    public static bool IsBound(ITypeSymbol type, ITypeSymbol?[] types) => type switch
    {
        ITypeParameterSymbol p => p.TypeParameterKind == TypeParameterKind.Type && p.Ordinal < types.Length &&
                                  types[p.Ordinal] != null,
        IArrayTypeSymbol a => IsBound(a.ElementType, types),
        INamedTypeSymbol n => AllBound(n, types),
        _ => true,
    };

    private static bool AllBound(INamedTypeSymbol type, ITypeSymbol?[] types)
    {
        foreach (var arg in type.TypeArguments)
        {
            if (!IsBound(arg, types))
                return false;
        }

        return true;
    }

    /// <summary>Replaces the formatter type parameters of a fully bound <paramref name="type"/>.</summary>
    public static ITypeSymbol? Substitute(ITypeSymbol type, ITypeSymbol?[] types, Compilation compilation)
    {
        switch (type)
        {
            case ITypeParameterSymbol p:
                return types[p.Ordinal];
            case IArrayTypeSymbol a:
                return Substitute(a.ElementType, types, compilation) is { } element
                    ? compilation.CreateArrayTypeSymbol(element, a.Rank)
                    : null;
            case INamedTypeSymbol { IsGenericType: true } n:
            {
                var args = new ITypeSymbol[n.TypeArguments.Length];
                for (int i = 0; i < args.Length; i++)
                {
                    if (Substitute(n.TypeArguments[i], types, compilation) is not { } arg)
                        return null;
                    args[i] = arg;
                }

                return n.OriginalDefinition.Construct(args);
            }
            default:
                return type;
        }
    }

    private static bool Match(ITypeSymbol pattern, ITypeSymbol actual, ITypeSymbol?[] types)
    {
        switch (pattern)
        {
            case ITypeParameterSymbol { TypeParameterKind: TypeParameterKind.Type } p when p.Ordinal < types.Length:
                if (types[p.Ordinal] is { } bound)
                    return SymbolEqualityComparer.Default.Equals(bound, actual);
                if (actual is IPointerTypeSymbol or IFunctionPointerTypeSymbol)
                    return false;
                types[p.Ordinal] = actual.WithNullableAnnotation(NullableAnnotation.NotAnnotated);
                return true;
            case IArrayTypeSymbol a:
                return actual is IArrayTypeSymbol b && a.Rank == b.Rank && a.IsSZArray == b.IsSZArray &&
                       Match(a.ElementType, b.ElementType, types);
            case INamedTypeSymbol { IsGenericType: true } n:
            {
                if (actual is not INamedTypeSymbol m)
                    return false;
                if (m.IsTupleType && m.TupleUnderlyingType is { } underlying)
                    m = underlying;
                if (!SymbolEqualityComparer.Default.Equals(n.OriginalDefinition, m.OriginalDefinition))
                    return false;
                for (int i = 0; i < n.TypeArguments.Length; i++)
                {
                    if (!Match(n.TypeArguments[i], m.TypeArguments[i], types))
                        return false;
                }

                return true;
            }
            default:
                return SymbolEqualityComparer.Default.Equals(pattern, actual);
        }
    }
}
