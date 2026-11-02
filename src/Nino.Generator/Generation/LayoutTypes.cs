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

    /// <summary>
    /// Adds the measured structs that <paramref name="type"/> names (itself, type arguments, elements, fields) to
    /// <paramref name="into"/> where generated code can name them, and the function pointer fields of each
    /// (<see cref="FunctionPointerFields"/>) to <paramref name="pointers"/>.
    /// </summary>
    public static void Collect(ITypeSymbol type, TypeFacts facts, ICollection<string> into, ICollection<string> pointers) =>
        Walk(type, facts, into, pointers, new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default), 0);

    private static void Walk(ITypeSymbol type, TypeFacts facts, ICollection<string> into, ICollection<string> pointers,
        HashSet<ITypeSymbol> visited, int depth)
    {
        if (depth > MaxDepth || !visited.Add(type))
            return;
        switch (type)
        {
            case IArrayTypeSymbol array:
                Walk(array.ElementType, facts, into, pointers, visited, depth);
                return;
            case INamedTypeSymbol named:
                foreach (var arg in TypeNames.AllTypeArguments(named))
                    Walk(arg, facts, into, pointers, visited, depth);
                if (named.TypeKind != TypeKind.Struct || !named.IsUnmanagedType || RawLayout.Leaf(named) != NinoLeaf.None)
                    return;
                // A struct generated code cannot name is still measured (inside one it can), and so are its fields.
                if (facts.Visibility.CanName(named))
                    into.Add(TypeNames.Global(named));
                if (FunctionPointerFields(named) is { } registration)
                    pointers.Add(registration);
                foreach (var member in named.GetMembers())
                {
                    if (member is IFieldSymbol { IsStatic: false } field)
                        Walk(field.Type, facts, into, pointers, visited, depth + 1);
                }

                return;
        }
    }

    /// <summary>
    /// The arguments of the registry's <c>NinoFunctionPointerFields.Register</c> call for the function pointer
    /// (<c>delegate*</c>) fields of <paramref name="type"/>: its assembly's name, its definition's runtime name
    /// (<see cref="TypeNames.RuntimeName"/>, as <c>NinoTypeId.GetRuntimeName</c> reads it) and the fields' names, as C#
    /// literals; null without such a field. Names, not types: the struct need not be nameable (a private one inside a
    /// public one), and registering loads no type. Nino.Core lays these fields out as pointers, zero-extended to 8
    /// bytes, where reflection cannot tell them from an <c>IntPtr</c> (Mono, IL2CPP) or reports them as a function
    /// pointer type (.NET 8 and later).
    /// </summary>
    public static string? FunctionPointerFields(INamedTypeSymbol type)
    {
        List<string>? fields = null;
        foreach (var member in type.GetMembers())
        {
            if (member is IFieldSymbol { IsStatic: false, Type: IFunctionPointerTypeSymbol } field)
                (fields ??= new List<string>()).Add(Literal(field.MetadataName));
        }

        if (fields == null || type.ContainingAssembly is not { } assembly)
            return null;
        return string.Join(", ", new[] { Literal(assembly.Name), Literal(TypeNames.RuntimeName(type.OriginalDefinition)) }
            .Concat(fields));
    }

    private static string Literal(string value) =>
        Microsoft.CodeAnalysis.CSharp.SymbolDisplay.FormatLiteral(value, quote: true);

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
