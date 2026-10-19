using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Nino.Generator.Common;

namespace Nino.Generator.Generation;

/// <summary>
/// The unmanaged structs a compilation names whose layout the runtime measures (<c>NinoLayout</c> reads their
/// instance fields by reflection, to find padding and runtimes whose layout is not the wire layout). NativeAOT keeps
/// no field metadata unless asked, so the registry asks for it with <c>[DynamicDependency]</c> on these types (and
/// on the structs their fields hold). Without it the fields read as missing, the measured size does not match and the
/// value is copied as it lies in memory: padding is not cleared and a runtime with another layout is not detected.
/// </summary>
internal static class LayoutTypes
{
    // Bounded for recursive generic structs (S<T> holding an S<S<T>>...); real nesting is far shallower.
    private const int MaxDepth = 8;

    /// <summary>Adds the measured structs that <paramref name="type"/> names (itself, type arguments, elements, fields).</summary>
    public static void Collect(ITypeSymbol type, TypeFacts facts, ICollection<string> into) =>
        Walk(type, facts, into, new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default), 0);

    private static void Walk(ITypeSymbol type, TypeFacts facts, ICollection<string> into,
        HashSet<ITypeSymbol> visited, int depth)
    {
        if (depth > MaxDepth || !visited.Add(type))
            return;
        switch (type)
        {
            case IArrayTypeSymbol array:
                Walk(array.ElementType, facts, into, visited, depth);
                return;
            case INamedTypeSymbol named:
                foreach (var arg in TypeNames.AllTypeArguments(named))
                    Walk(arg, facts, into, visited, depth);
                if (named.TypeKind != TypeKind.Struct || !named.IsUnmanagedType || RawLayout.Leaf(named) != NinoLeaf.None ||
                    !facts.Visibility.CanName(named))
                    return;
                into.Add(TypeNames.Global(named));
                foreach (var member in named.GetMembers())
                {
                    if (member is IFieldSymbol { IsStatic: false } field)
                        Walk(field.Type, facts, into, visited, depth + 1);
                }

                return;
        }
    }

    /// <summary>
    /// Whether generated code can name <c>DynamicDependencyAttribute</c> and <c>DynamicallyAccessedMemberTypes</c>
    /// (.NET 5 and later, or polyfills): exactly one accessible definition of each, since a second one (a polyfill
    /// visible through InternalsVisibleTo) would make the names ambiguous.
    /// </summary>
    public static bool CanEmit(Compilation compilation) =>
        TypeNames.CanName(compilation, "System.Diagnostics.CodeAnalysis.DynamicDependencyAttribute") &&
        TypeNames.CanName(compilation, "System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes");

    /// <summary>Sorted, without duplicates: the registry's attribute list is stable across builds.</summary>
    public static EquatableArray<string> ToArray(IEnumerable<string> types) =>
        new(types.Distinct().OrderBy(t => t, System.StringComparer.Ordinal).ToArray());
}
