using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Nino.Generator.Common;

namespace Nino.Generator.Generation;

/// <summary>
/// Finds the closed generic <c>[NinoType]</c>s a compilation names (<c>Box&lt;int&gt;</c> as a member type, a base type,
/// a type argument of a top-level call, or a member of one of those), so the registry can register them by type id.
/// Generic <c>[NinoType]</c>s have one formatter definition and no registration of their own: without this, a
/// polymorphic read would have no way to map the id of <c>Box&lt;int&gt;</c> back to the type, and AOT runtimes would
/// have to close the formatter with <c>MakeGenericType</c>. Instantiations created only at run time (from type
/// parameters, or by reflection) are still found by the runtime when they are written, or through
/// <c>NinoPolymorphic.Register(Type)</c>.
/// </summary>
internal static class ClosedGenerics
{
    // Nesting followed through the members of closed generics (Box<T> holding a List<Item<T>>...): deep enough for
    // real types, bounded for recursive ones (Node<T> holding a Node<List<T>>).
    private const int MaxDepth = 4;

    /// <summary>Adds the closed generic <c>[NinoType]</c>s that <paramref name="type"/> names.</summary>
    public static void Collect(ITypeSymbol type, TypeFacts facts, List<TypeRegistration> into) =>
        Walk(type, facts, into, new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default), 0);

    /// <summary>Adds the closed generic <c>[NinoType]</c> base types of <paramref name="type"/> (and what they name).</summary>
    public static void CollectBases(INamedTypeSymbol type, TypeFacts facts, List<TypeRegistration> into)
    {
        var visited = new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default);
        for (var b = type.BaseType; b != null; b = b.BaseType)
        {
            if (b.IsGenericType)
                Walk(b, facts, into, visited, 0);
        }
    }

    private static void Walk(ITypeSymbol type, TypeFacts facts, List<TypeRegistration> into,
        HashSet<ITypeSymbol> visited, int depth)
    {
        if (depth > MaxDepth || !visited.Add(type))
            return;
        switch (type)
        {
            case IArrayTypeSymbol array:
                Walk(array.ElementType, facts, into, visited, depth);
                return;
            case INamedTypeSymbol { IsGenericType: true } named:
                foreach (var arg in TypeNames.AllTypeArguments(named))
                    Walk(arg, facts, into, visited, depth);
                if (!named.OriginalDefinition.IsNinoType() || !facts.Visibility.CanName(named))
                    return;
                Add(named, facts, into);
                // What the closed type names in turn: its base types and the types of its members.
                for (var b = named.BaseType; b != null; b = b.BaseType)
                {
                    if (b.IsGenericType)
                        Walk(b, facts, into, visited, depth + 1);
                }

                if (named.OriginalDefinition.GetNinoTypeOptions() is { } attr)
                {
                    foreach (var member in facts.Members.Of(named.OriginalDefinition, attr))
                    {
                        if (Substituted(named, member) is { } memberType)
                            Walk(memberType, facts, into, visited, depth + 1);
                    }
                }

                // A slot (Shelf<string>: abstract, an interface or a class that is not sealed) holds the closed
                // types of its generic subtypes with the arguments it gives them (ListShelf<string>): data another
                // process wrote names them by id, though no code here does.
                if (named.IsAbstract || named.TypeKind == TypeKind.Interface ||
                    named.TypeKind == TypeKind.Class && !named.IsSealed)
                {
                    foreach (var subtype in ClosedSubtypes(named, facts))
                        Walk(subtype, facts, into, visited, depth + 1);
                }

                return;
        }
    }

    /// <summary>
    /// The closed types of the generic <c>[NinoType]</c> definitions that derive from <paramref name="slot"/>'s
    /// definition, with the arguments that make them assignable to <paramref name="slot"/>
    /// (<c>ListShelf&lt;T&gt; : Shelf&lt;T&gt;</c> in a <c>Shelf&lt;string&gt;</c> slot: <c>ListShelf&lt;string&gt;</c>).
    /// A definition whose arguments the slot does not fix, or whose constraints they may not meet, is left out.
    /// </summary>
    private static IEnumerable<INamedTypeSymbol> ClosedSubtypes(INamedTypeSymbol slot, TypeFacts facts)
    {
        var definition = slot.OriginalDefinition;
        foreach (var candidate in facts.GenericDefinitionsFor(definition.ContainingAssembly))
        {
            if (SymbolEqualityComparer.Default.Equals(candidate, definition) || candidate.IsAbstract)
                continue;
            var parameters = candidate.TypeParameters;
            foreach (var ancestor in Ancestors(candidate))
            {
                if (!SymbolEqualityComparer.Default.Equals(ancestor.OriginalDefinition, definition))
                    continue;
                var bound = new ITypeSymbol?[parameters.Length];
                bool unified = true;
                var have = TypeNames.AllTypeArguments(ancestor).ToList();
                var want = TypeNames.AllTypeArguments(slot).ToList();
                for (int i = 0; i < have.Count && unified; i++)
                    unified = Unify(have[i], want[i], candidate, bound);
                if (unified && bound.All(b => b != null) && Satisfies(parameters, bound))
                    yield return candidate.Construct(bound!);
                break;
            }
        }
    }

    private static IEnumerable<INamedTypeSymbol> Ancestors(INamedTypeSymbol type)
    {
        for (var b = type.BaseType; b != null; b = b.BaseType)
            yield return b;
        foreach (var i in type.AllInterfaces)
            yield return i;
    }

    /// <summary>Binds the type parameters of <paramref name="owner"/> in <paramref name="pattern"/> to make it <paramref name="actual"/>.</summary>
    private static bool Unify(ITypeSymbol pattern, ITypeSymbol actual, INamedTypeSymbol owner, ITypeSymbol?[] bound)
    {
        switch (pattern)
        {
            case ITypeParameterSymbol p when SymbolEqualityComparer.Default.Equals(p.ContainingSymbol, owner):
                if (bound[p.Ordinal] is { } already)
                    return SymbolEqualityComparer.Default.Equals(already, actual);
                bound[p.Ordinal] = actual;
                return true;
            case IArrayTypeSymbol pa:
                return actual is IArrayTypeSymbol aa && pa.Rank == aa.Rank && Unify(pa.ElementType, aa.ElementType, owner, bound);
            case INamedTypeSymbol { IsGenericType: true } pn:
            {
                if (actual is not INamedTypeSymbol { IsGenericType: true } an ||
                    !SymbolEqualityComparer.Default.Equals(pn.OriginalDefinition, an.OriginalDefinition))
                    return false;
                var ps = TypeNames.AllTypeArguments(pn).ToList();
                var acts = TypeNames.AllTypeArguments(an).ToList();
                for (int i = 0; i < ps.Count; i++)
                {
                    if (!Unify(ps[i], acts[i], owner, bound))
                        return false;
                }

                return true;
            }
            default:
                return SymbolEqualityComparer.Default.Equals(pattern, actual);
        }
    }

    /// <summary>
    /// Whether <paramref name="arguments"/> meet the constraints of <paramref name="parameters"/>; a constraint type that
    /// names a type parameter is not checked here, and counts as unmet.
    /// </summary>
    private static bool Satisfies(IReadOnlyList<ITypeParameterSymbol> parameters, ITypeSymbol?[] arguments)
    {
        for (int i = 0; i < parameters.Count; i++)
        {
            var (p, a) = (parameters[i], arguments[i]!);
            if (a is ITypeParameterSymbol || a.IsRefLikeType || a.SpecialType == SpecialType.System_Void)
                return false;
            if (p.HasReferenceTypeConstraint && !a.IsReferenceType ||
                p.HasValueTypeConstraint && (!a.IsValueType ||
                    a.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T) ||
                p.HasUnmanagedTypeConstraint && !a.IsUnmanagedType)
                return false;
            if (p.HasConstructorConstraint && !a.IsValueType &&
                (a.IsAbstract || a is not INamedTypeSymbol named ||
                 !named.InstanceConstructors.Any(c => c.Parameters.Length == 0 && c.DeclaredAccessibility == Accessibility.Public)))
                return false;
            foreach (var constraint in p.ConstraintTypes)
            {
                if (TypeNames.ContainsTypeParameter(constraint) || !Implements(a, constraint))
                    return false;
            }
        }

        return true;
    }

    private static bool Implements(ITypeSymbol type, ITypeSymbol target)
    {
        if (SymbolEqualityComparer.Default.Equals(type, target))
            return true;
        for (var b = type.BaseType; b != null; b = b.BaseType)
        {
            if (SymbolEqualityComparer.Default.Equals(b, target))
                return true;
        }

        return type.AllInterfaces.Any(i => SymbolEqualityComparer.Default.Equals(i, target));
    }

    private static void Add(INamedTypeSymbol type, TypeFacts facts, List<TypeRegistration> into)
    {
        var definition = type.OriginalDefinition;
        var kind = TypeRules.Kind(type);
        bool concrete = TypeRules.IsConcrete(kind);
        // A struct is never tracked (the analyzer warns); classes follow the model's rule.
        bool tracked = kind is FormatterKind.Class or FormatterKind.SealedClass && TypeRules.IsTracked(definition);
        var (id, nameId) = facts.Ids(type);
        // Formatters generated by another assembly are internal to it: those are registered by reflection.
        string formatter = facts.Visibility.IsGeneratedHere(definition) ? FormatterResolver.GeneratedFormatter(type).ToString() : "";
        into.Add(new TypeRegistration(TypeNames.Global(type), formatter, id, nameId, concrete, tracked));
    }

    /// <summary>The type of <paramref name="member"/> (declared by the definition or a base) in the closed type.</summary>
    private static ITypeSymbol? Substituted(INamedTypeSymbol closed, ISymbol member)
    {
        var target = member.OriginalDefinition;
        for (var t = closed; t != null; t = t.BaseType)
        {
            foreach (var candidate in t.GetMembers(member.Name))
            {
                if (SymbolEqualityComparer.Default.Equals(candidate.OriginalDefinition, target))
                    return MemberCollector.MemberType(candidate);
            }
        }

        return null;
    }
}
