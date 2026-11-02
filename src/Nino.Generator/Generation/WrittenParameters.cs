using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Nino.Generator.Common;

namespace Nino.Generator.Generation;

/// <summary>
/// Which type parameters of a generic <c>[NinoType]</c> definition its formatter writes, memoized for one compilation:
/// <c>T</c> of <c>Box&lt;T&gt; { T Value; }</c>, not of <c>Tag&lt;T&gt; { int Id; [NinoIgnore] T Scratch; }</c> nor of
/// <c>Outer&lt;T&gt; { Tag&lt;T&gt; Inner; }</c>. A closed instance writes exactly the type arguments in those positions,
/// so the checks of what a closed generic holds (NINO029's <c>Vector&lt;T&gt;</c>, NINO007's type argument without a
/// formatter) search only those, and the closed member types are never built: substituting them grows without end for
/// a type holding itself over a bigger argument (<c>Expanding&lt;T&gt;</c> holding <c>Expanding&lt;List&lt;T&gt;&gt;</c>).
/// </summary>
/// <remarks>
/// A parameter is written when a member the generator serializes (<see cref="MemberCache"/>: its own and its
/// <c>[NinoType]</c> bases', without the ones a <c>[NinoMemberFormatter]</c> writes its own way) has a type that writes
/// it: the parameter itself, an array of it, a type argument of a type that is not a <c>[NinoType]</c> (collections,
/// dictionaries, <c>Nullable</c>, tuples, <c>KeyValuePair</c>), or a type argument in a written position of another
/// generic <c>[NinoType]</c>. A class's own elements count too (the collection formatter of
/// <c>class Bag&lt;T&gt; : List&lt;T&gt;</c>), and so do the subtypes a slot dispatches to: a parameter of
/// <c>Shelf&lt;T&gt;</c> is written when a generic subtype (<c>ListShelf&lt;U&gt; : Shelf&lt;U&gt;</c>) writes a parameter
/// its base's argument names (anywhere in it: the closed search then looks inside that argument by its own rules,
/// so a subtype's parameter that the argument holds only in a position it never writes, <c>S&lt;U&gt; :
/// Shelf&lt;Tag&lt;U&gt;&gt;</c>, is not found; subtypes outside the compilation and its references cannot be seen at
/// all). The definitions reach each other in cycles (<c>A&lt;T&gt; { A&lt;List&lt;T&gt;&gt; Next; }</c>):
/// the answer is the least fixpoint over every definition reachable from the one asked (each bit only turns on, so at
/// most one round per parameter), which is exact: a parameter is written iff a finite path of serialized members
/// reaches it in a written position. Types that are not <c>[NinoType]</c>s count every type argument as written, as
/// Nino.Core's formatters write them.
/// </remarks>
internal sealed class WrittenParameters
{
    private readonly ConcurrentDictionary<ISymbol, bool[]> _written = new(SymbolEqualityComparer.Default);
    private readonly MemberCache _members;
    private readonly NinoSymbols _symbols;
    private readonly Func<IAssemblySymbol, IEnumerable<INamedTypeSymbol>> _genericDefinitions;

    /// <param name="genericDefinitions">The generic <c>[NinoType]</c> definitions that can derive from a definition of
    /// the given assembly (<see cref="TypeFacts.GenericDefinitionsFor"/>).</param>
    public WrittenParameters(MemberCache members, NinoSymbols symbols,
        Func<IAssemblySymbol, IEnumerable<INamedTypeSymbol>> genericDefinitions)
    {
        _members = members;
        _symbols = symbols;
        _genericDefinitions = genericDefinitions;
    }

    /// <summary>
    /// The type arguments of <paramref name="type"/>, a generic <c>[NinoType]</c> (closed or not), that its formatter
    /// writes, in the order of <see cref="TypeNames.AllTypeArguments"/>.
    /// </summary>
    public IEnumerable<ITypeSymbol> ArgumentsOf(INamedTypeSymbol type)
    {
        var arguments = TypeNames.AllTypeArguments(type);
        if (arguments.Count == 0)
            yield break;
        var written = Of(type.OriginalDefinition);
        for (int i = 0; i < arguments.Count; i++)
        {
            if (written[i])
                yield return arguments[i];
        }
    }

    /// <summary>
    /// Whether the formatter of <paramref name="definition"/>, a generic <c>[NinoType]</c> definition, writes each of
    /// its type parameters (<see cref="TypeNames.AllTypeParameters"/>, outermost containing type first).
    /// </summary>
    public bool[] Of(INamedTypeSymbol definition)
    {
        if (_written.TryGetValue(definition, out var found))
            return found;

        // Every definition reachable from this one that is not decided yet, then their fixpoint.
        var pending = new Dictionary<INamedTypeSymbol, Definition>(SymbolEqualityComparer.Default);
        var queue = new Queue<INamedTypeSymbol>();
        queue.Enqueue(definition);
        while (queue.Count != 0)
        {
            var next = queue.Dequeue();
            if (pending.ContainsKey(next) || _written.ContainsKey(next))
                continue;
            var node = Describe(next);
            pending.Add(next, node);
            foreach (var type in node.Writes)
                EnqueueDefinitions(type, queue);
            foreach (var (subtype, _) in node.Subtypes)
                queue.Enqueue(subtype);
        }

        bool changed = true;
        while (changed)
        {
            changed = false;
            foreach (var node in pending.Values)
            {
                for (int i = 0; i < node.Written.Length; i++)
                {
                    if (!node.Written[i] && IsWritten(node, i, pending))
                        node.Written[i] = changed = true;
                }
            }
        }

        foreach (var entry in pending)
            _written.TryAdd(entry.Key, entry.Value.Written);
        return _written[definition];
    }

    /// <summary>One definition under decision: its parameters, what it writes, and its generic subtypes.</summary>
    private sealed class Definition
    {
        public List<ITypeParameterSymbol> Parameters = null!;
        public bool[] Written = null!;

        /// <summary>The types its formatter writes values of, in terms of its own type parameters.</summary>
        public List<ITypeSymbol> Writes = null!;

        /// <summary>Generic <c>[NinoType]</c>s deriving from it, with the type arguments they give it.</summary>
        public List<(INamedTypeSymbol Subtype, List<ITypeSymbol> Arguments)> Subtypes = null!;
    }

    private Definition Describe(INamedTypeSymbol definition)
    {
        var parameters = TypeNames.AllTypeParameters(definition);
        var writes = new List<ITypeSymbol>();
        var subtypes = new List<(INamedTypeSymbol, List<ITypeSymbol>)>();
        if (definition.GetNinoTypeOptions() is { } options)
        {
            foreach (var member in _members.Of(definition, options))
            {
                // A [NinoMemberFormatter] writes the member its own way (its type cannot name T: attributes take
                // closed types only).
                if (MemberCollector.MemberType(member) is { } type && !NinoSymbols.Has(member, _symbols.CustomFormatter))
                    writes.Add(type);
            }

            // A class that is a collection writes its elements through its collection formatter too.
            if (definition.TypeKind == TypeKind.Class && !definition.IsAbstract)
            {
                foreach (var face in definition.AllInterfaces)
                {
                    if (face.OriginalDefinition.SpecialType == SpecialType.System_Collections_Generic_IEnumerable_T)
                        writes.Add(face.TypeArguments[0]);
                }
            }

            // A slot writes the subtypes it holds.
            if (TypeRules.HasSubtypes(TypeRules.Kind(definition)))
            {
                foreach (var candidate in _genericDefinitions(definition.ContainingAssembly))
                {
                    if (SymbolEqualityComparer.Default.Equals(candidate, definition))
                        continue;
                    var ancestor = Ancestors(candidate).FirstOrDefault(a =>
                        SymbolEqualityComparer.Default.Equals(a.OriginalDefinition, definition));
                    if (ancestor != null)
                        subtypes.Add((candidate, TypeNames.AllTypeArguments(ancestor).ToList()));
                }
            }
        }

        return new Definition
        {
            Parameters = parameters, Written = new bool[parameters.Count], Writes = writes, Subtypes = subtypes,
        };
    }

    private static IEnumerable<INamedTypeSymbol> Ancestors(INamedTypeSymbol type)
    {
        for (var b = type.BaseType; b != null; b = b.BaseType)
            yield return b;
        foreach (var i in type.AllInterfaces)
            yield return i;
    }

    /// <summary>Queues the generic <c>[NinoType]</c> definitions <paramref name="type"/> names.</summary>
    private static void EnqueueDefinitions(ITypeSymbol type, Queue<INamedTypeSymbol> queue)
    {
        switch (type)
        {
            case IArrayTypeSymbol array:
                EnqueueDefinitions(array.ElementType, queue);
                return;
            case INamedTypeSymbol named:
                var arguments = TypeNames.AllTypeArguments(named);
                if (arguments.Count == 0)
                    return;
                if (named.OriginalDefinition.GetNinoTypeOptions() != null)
                    queue.Enqueue(named.OriginalDefinition);
                foreach (var argument in arguments)
                    EnqueueDefinitions(argument, queue);
                return;
        }
    }

    private bool IsWritten(Definition node, int index, Dictionary<INamedTypeSymbol, Definition> pending)
    {
        var parameter = node.Parameters[index];
        foreach (var type in node.Writes)
        {
            if (Writes(type, parameter, pending))
                return true;
        }

        // A subtype that writes one of its parameters writes the argument of this one that names it
        // (ListShelf<U> : Shelf<U>: a Shelf<X> slot holds a ListShelf<X>).
        foreach (var (subtype, arguments) in node.Subtypes)
        {
            var written = WrittenOf(subtype, pending);
            var subtypeParameters = TypeNames.AllTypeParameters(subtype);
            for (int q = 0; q < written.Length; q++)
            {
                if (written[q] && TypeNames.Mentions(arguments[index], subtypeParameters[q]))
                    return true;
            }
        }

        return false;
    }

    /// <summary>The written parameters of <paramref name="definition"/> as decided so far.</summary>
    private bool[] WrittenOf(INamedTypeSymbol definition, Dictionary<INamedTypeSymbol, Definition> pending) =>
        pending.TryGetValue(definition, out var node) ? node.Written : _written[definition];

    /// <summary>Whether a value of <paramref name="type"/> writes <paramref name="parameter"/>.</summary>
    private bool Writes(ITypeSymbol type, ITypeParameterSymbol parameter,
        Dictionary<INamedTypeSymbol, Definition> pending)
    {
        switch (type)
        {
            case ITypeParameterSymbol p:
                return SymbolEqualityComparer.Default.Equals(p, parameter);
            case IArrayTypeSymbol array:
                return Writes(array.ElementType, parameter, pending);
            case INamedTypeSymbol named:
                var arguments = TypeNames.AllTypeArguments(named);
                if (arguments.Count == 0)
                    return false;
                var definition = named.OriginalDefinition;
                bool[]? written = definition.GetNinoTypeOptions() != null ? WrittenOf(definition, pending) : null;
                for (int i = 0; i < arguments.Count; i++)
                {
                    if ((written == null || written[i]) && Writes(arguments[i], parameter, pending))
                        return true;
                }

                return false;
            default:
                return false;
        }
    }
}
