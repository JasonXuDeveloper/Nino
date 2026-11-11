using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Nino.Generator.Common;

namespace Nino.Generator.Generation;

/// <summary>
/// Finds the closed generic <c>[NinoType]</c>s a compilation names (<c>Box&lt;int&gt;</c> as a member type, a base type,
/// the type a top-level call names, or a member of one of those), so the registry can register them by type id.
/// Generic <c>[NinoType]</c>s have one formatter definition and no registration of their own: without this, a
/// polymorphic read would have no way to map the id of <c>Box&lt;int&gt;</c> back to the type, and AOT runtimes would
/// have to close the formatter with <c>MakeGenericType</c>. Instantiations created only at run time (from type
/// parameters, or by reflection) are still found by the runtime when they are written, or through
/// <c>NinoPolymorphic.TryRegister(Type)</c>.
/// </summary>
internal static class ClosedGenerics
{
    // Nesting followed through the members of closed generics (Box<T> holding a List<Item<T>>...): deep enough for
    // real types, bounded for recursive ones (Node<T> holding a Node<List<T>>).
    private const int MaxDepth = 4;

    /// <summary>Adds the closed generic <c>[NinoType]</c>s that <paramref name="type"/> names.</summary>
    public static void Collect(ITypeSymbol type, TypeFacts facts, List<TypeRegistration> into)
    {
        while (type is IArrayTypeSymbol array)
            type = array.ElementType;
        if (type is INamedTypeSymbol { IsGenericType: true })
            Walk(type, facts, into, new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default), 0);
    }

    /// <summary>Adds the closed generic <c>[NinoType]</c> base types of <paramref name="type"/> (and what they name).</summary>
    public static void CollectBases(INamedTypeSymbol type, TypeFacts facts, List<TypeRegistration> into)
    {
        HashSet<ITypeSymbol>? visited = null;
        for (var b = type.BaseType; b != null; b = b.BaseType)
        {
            if (b.IsGenericType)
                Walk(b, facts, into, visited ??= new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default), 0);
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
                WalkArguments(named, facts, into, visited, depth);
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

    private static void WalkArguments(INamedTypeSymbol type, TypeFacts facts, List<TypeRegistration> into,
        HashSet<ITypeSymbol> visited, int depth)
    {
        // Preserve the outer-to-inner order without flattening the arguments into a list.
        if (type.ContainingType is { } containing)
            WalkArguments(containing, facts, into, visited, depth);
        if (type.Arity != 0)
        {
            foreach (var argument in type.TypeArguments)
                Walk(argument, facts, into, visited, depth);
        }
    }

    /// <summary>
    /// The closed types of the generic <c>[NinoType]</c> definitions that derive from <paramref name="slot"/>'s
    /// definition, with the arguments that make them assignable to <paramref name="slot"/>
    /// (<c>ListShelf&lt;T&gt; : Shelf&lt;T&gt;</c> in a <c>Shelf&lt;string&gt;</c> slot: <c>ListShelf&lt;string&gt;</c>).
    /// The definitions are those the analyzer follows (<see cref="TypeFacts.GenericSubtypesOf"/>: this compilation and
    /// every referenced assembly that is the slot's or references it, nested in generic types too, whose containing
    /// types' parameters the slot binds like the subtype's own), so what is registered by id is what NINO007 and
    /// NINO029 search. A definition whose arguments the slot does not fix, or whose constraints they may not meet, is
    /// left out.
    /// </summary>
    private static IEnumerable<INamedTypeSymbol> ClosedSubtypes(INamedTypeSymbol slot, TypeFacts facts)
    {
        var want = TypeNames.AllTypeArguments(slot);
        foreach (var (candidate, ancestor) in facts.GenericSubtypesOf(slot.OriginalDefinition))
        {
            if (candidate.IsAbstract)
                continue;
            var parameters = TypeNames.AllTypeParameters(candidate);
            var bound = new ITypeSymbol?[parameters.Count];
            var have = TypeNames.AllTypeArguments(ancestor);
            bool unified = have.Count == want.Count;
            for (int i = 0; i < have.Count && unified; i++)
                unified = Unify(have[i], want[i], parameters, bound);
            if (unified && bound.All(b => b != null) && Satisfies(parameters, bound!))
                yield return Construct(candidate, bound!);
        }
    }

    /// <summary>
    /// <paramref name="definition"/> closed over <paramref name="arguments"/>, which hold the parameters of its
    /// containing types first (<see cref="TypeNames.AllTypeParameters"/>): <c>Outer&lt;int&gt;.Inner&lt;string&gt;</c>.
    /// </summary>
    private static INamedTypeSymbol Construct(INamedTypeSymbol definition, ITypeSymbol[] arguments)
    {
        var chain = new Stack<INamedTypeSymbol>();
        for (var level = definition; level != null; level = level.ContainingType)
            chain.Push(level);
        INamedTypeSymbol? closed = null;
        int next = 0;
        foreach (var level in chain)
        {
            var open = closed == null
                ? level
                : closed.GetTypeMembers(level.Name, level.Arity)
                    .First(t => SymbolEqualityComparer.Default.Equals(t.OriginalDefinition, level));
            closed = level.Arity == 0 ? open : open.Construct(arguments.Skip(next).Take(level.Arity).ToArray());
            next += level.Arity;
        }

        return closed!;
    }

    /// <summary>Binds <paramref name="parameters"/> in <paramref name="pattern"/> to make it <paramref name="actual"/>.</summary>
    private static bool Unify(ITypeSymbol pattern, ITypeSymbol actual, List<ITypeParameterSymbol> parameters,
        ITypeSymbol?[] bound)
    {
        switch (pattern)
        {
            case ITypeParameterSymbol p when parameters.IndexOf(p) is var i and >= 0:
                if (bound[i] is { } already)
                    return SymbolEqualityComparer.Default.Equals(already, actual);
                bound[i] = actual;
                return true;
            case IArrayTypeSymbol pa:
                return actual is IArrayTypeSymbol aa && pa.Rank == aa.Rank &&
                       Unify(pa.ElementType, aa.ElementType, parameters, bound);
            case INamedTypeSymbol { IsGenericType: true } pn:
            {
                if (actual is not INamedTypeSymbol { IsGenericType: true } an ||
                    !SymbolEqualityComparer.Default.Equals(pn.OriginalDefinition, an.OriginalDefinition))
                    return false;
                var ps = TypeNames.AllTypeArguments(pn);
                var acts = TypeNames.AllTypeArguments(an);
                for (int i = 0; i < ps.Count; i++)
                {
                    if (!Unify(ps[i], acts[i], parameters, bound))
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
    private static bool Satisfies(List<ITypeParameterSymbol> parameters, ITypeSymbol[] arguments)
    {
        for (int i = 0; i < parameters.Count; i++)
        {
            var (p, a) = (parameters[i], arguments[i]);
            if (a is ITypeParameterSymbol || a.IsRefLikeType || a.SpecialType == SpecialType.System_Void ||
                !NinoSymbols.MeetsCSharpConstraints(p, a))
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
        string formatter = facts.Visibility.IsGeneratedHere(definition) ? facts.Resolver.GeneratedFormatter(type).ToString() : "";
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
