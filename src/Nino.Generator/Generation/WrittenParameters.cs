using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;
using Nino.Generator.Common;

namespace Nino.Generator.Generation;

/// <summary>
/// What the formatter of a generic <c>[NinoType]</c> definition writes of each of its type arguments, memoized for
/// one compilation: all of <c>T</c> for <c>Box&lt;T&gt; { T Value; }</c>, nothing of it for <c>Tag&lt;T&gt; { int Id;
/// [NinoIgnore] T Scratch; }</c> or <c>Outer&lt;T&gt; { Tag&lt;T&gt; Inner; }</c>, and the <c>U</c> of an argument
/// <c>Tag&lt;U&gt;</c> for <c>Shelf&lt;T&gt;</c> with a subtype <c>S&lt;U&gt; : Shelf&lt;Tag&lt;U&gt;&gt; { U Value; }</c>
/// (a <c>Shelf&lt;Tag&lt;X&gt;&gt;</c> slot holds an <c>S&lt;X&gt;</c>, which writes <c>X</c>). The checks of what a
/// closed generic holds (NINO029's <c>Vector&lt;T&gt;</c>, NINO007's type argument without a formatter) search
/// exactly the values <see cref="ValuesOf"/> finds in its arguments, and the closed member types are never built:
/// substituting them grows without end for a type holding itself over a bigger argument (<c>Expanding&lt;T&gt;</c>
/// holding <c>Expanding&lt;List&lt;T&gt;&gt;</c>).
/// </summary>
/// <remarks>
/// <para>
/// What a definition writes of an argument is a set of <see cref="Pattern"/>s. A definition writes a value of each
/// type its formatter writes: a member the generator serializes (<see cref="MemberCache"/>: its own and its
/// <c>[NinoType]</c> bases', without the ones a <c>[NinoMemberFormatter]</c> writes its own way), a class's own
/// <c>IEnumerable&lt;T&gt;</c> elements (the collection formatter of <c>class Bag&lt;T&gt; : List&lt;T&gt;</c>), and,
/// for a slot (an interface, an abstract or unsealed class), every generic subtype it dispatches to, base classes and
/// interfaces alike, at any depth of inheritance. A value of a parameter writes the whole argument; of an array or a
/// generic that is not a <c>[NinoType]</c>, whatever its elements and type arguments write (every type argument:
/// Nino.Core's formatters of collections, dictionaries, <c>Nullable</c>, tuples and <c>KeyValuePair</c> write them
/// all, and Nino.Core refuses at run time any other such generic with a <c>Vector&lt;T&gt;</c> in any argument,
/// <c>NinoReflection.HostSizedVectorIn</c>); of a generic <c>[NinoType]</c>, what its own patterns match in its
/// arguments. A subtype's patterns reach its base through the argument the base is given: the base's argument is
/// that argument with the subtype's patterns in place of its parameter.
/// </para>
/// <para>
/// The definitions reach each other in cycles (<c>A&lt;T&gt; { A&lt;List&lt;T&gt;&gt; Next; }</c>), so the answer is the
/// least fixpoint over every definition reachable from the one asked: sets only grow, and every pattern is built from
/// the declarations' own types to a bounded depth, so it ends. A pattern deeper than <see cref="MaxDepth"/>, or more
/// patterns than <see cref="MaxPatterns"/> for one argument (a subtype that also holds its base over its own
/// parameter matches <c>Tag&lt;Tag&lt;...&gt;&gt;</c> at every depth), become <see cref="Pattern.Inside"/>: every type
/// inside the argument counts, which may report an argument nothing writes but never misses one.
/// </para>
/// <para>
/// Subtypes are searched in this compilation and in every referenced assembly that is the definition's or references
/// it (<see cref="TypeFacts.GenericSubtypesOf"/>). A referenced class's members are read from its symbols, but a
/// compilation imports only the public and protected members of referenced classes: the members its published layout
/// (<c>[NinoTypeLayout]</c>) names that its symbols lack count as writing every parameter their type names
/// (<see cref="Pattern.Inside"/>), and every parameter of a referenced class without a layout does. Two things no
/// compilation can see stay unreported: a subtype declared in an assembly that references this one, and a subtype's
/// parameter its base does not name (<c>S&lt;U, W&gt; : Shelf&lt;U&gt;</c> writing <c>W</c>: a <c>Shelf&lt;int&gt;</c>
/// slot holds an <c>S&lt;int, W&gt;</c> for any <c>W</c>).
/// </para>
/// </remarks>
internal sealed class WrittenParameters
{
    /// <summary>The depth of a pattern beyond which it is <see cref="Pattern.Inside"/>.</summary>
    private const int MaxDepth = 6;

    /// <summary>The number of patterns of one argument beyond which they are <see cref="Pattern.Inside"/>.</summary>
    private const int MaxPatterns = 16;

    private static readonly ConditionalWeakTable<IAssemblySymbol, Dictionary<ISymbol, string>> Layouts = new();

    private readonly ConcurrentDictionary<ISymbol, Pattern[][]> _written = new(SymbolEqualityComparer.Default);
    private readonly MemberCache _members;
    private readonly NinoSymbols _symbols;
    private readonly Func<INamedTypeSymbol, IReadOnlyList<(INamedTypeSymbol Subtype, INamedTypeSymbol Ancestor)>> _subtypes;

    /// <param name="subtypes">The generic <c>[NinoType]</c> definitions deriving from a definition, with the ancestor
    /// through which they do (<see cref="TypeFacts.GenericSubtypesOf"/>).</param>
    public WrittenParameters(MemberCache members, NinoSymbols symbols,
        Func<INamedTypeSymbol, IReadOnlyList<(INamedTypeSymbol Subtype, INamedTypeSymbol Ancestor)>> subtypes)
    {
        _members = members;
        _symbols = symbols;
        _subtypes = subtypes;
    }

    /// <summary>
    /// The values the formatter of <paramref name="type"/>, a generic <c>[NinoType]</c> (closed or not), writes of its
    /// type arguments: an argument or a part of one, each searched by its own rules, or, when
    /// <c>Inside</c>, together with every type inside it.
    /// </summary>
    public IEnumerable<(ITypeSymbol Value, bool Inside)> ValuesOf(INamedTypeSymbol type)
    {
        var arguments = TypeNames.AllTypeArguments(type);
        if (arguments.Count == 0)
            return Array.Empty<(ITypeSymbol, bool)>();
        var written = Of(type.OriginalDefinition);
        var values = new List<(ITypeSymbol, bool)>();
        for (int i = 0; i < arguments.Count; i++)
        {
            foreach (var pattern in written[i])
                pattern.Match(arguments[i], values);
        }

        return values;
    }

    /// <summary>
    /// What the formatter of <paramref name="definition"/>, a generic <c>[NinoType]</c> definition, writes of each of
    /// its type parameters (<see cref="TypeNames.AllTypeParameters"/>, outermost containing type first): no pattern
    /// when it writes nothing of it.
    /// </summary>
    public Pattern[][] Of(INamedTypeSymbol definition)
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
                    var grown = Normalize(node.Written[i], Compute(node, i, pending));
                    if (!grown.SetEquals(node.Written[i]))
                    {
                        node.Written[i] = grown;
                        changed = true;
                    }
                }
            }
        }

        foreach (var entry in pending)
            _written.TryAdd(entry.Key, entry.Value.Written.Select(static set => set.ToArray()).ToArray());
        return _written[definition];
    }

    /// <summary>One definition under decision: its parameters, what it writes, and its generic subtypes.</summary>
    private sealed class Definition
    {
        public List<ITypeParameterSymbol> Parameters = null!;

        /// <summary>What it writes of each parameter, as decided so far.</summary>
        public HashSet<Pattern>[] Written = null!;

        /// <summary>The types its formatter writes values of, in terms of its own type parameters.</summary>
        public List<ITypeSymbol> Writes = null!;

        /// <summary>The parameters members its symbols lack (of a referenced class) may write.</summary>
        public bool[] Hidden = null!;

        /// <summary>Generic <c>[NinoType]</c>s deriving from it, with the type arguments they give it.</summary>
        public List<(INamedTypeSymbol Subtype, List<ITypeSymbol> Arguments)> Subtypes = null!;
    }

    private Definition Describe(INamedTypeSymbol definition)
    {
        var parameters = TypeNames.AllTypeParameters(definition);
        var writes = new List<ITypeSymbol>();
        var hidden = new bool[parameters.Count];
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

            MarkHidden(definition, parameters, hidden);

            // A slot writes the subtypes it holds.
            if (TypeRules.HasSubtypes(TypeRules.Kind(definition)))
            {
                foreach (var (subtype, ancestor) in _subtypes(definition))
                    subtypes.Add((subtype, TypeNames.AllTypeArguments(ancestor).ToList()));
            }
        }

        var written = new HashSet<Pattern>[parameters.Count];
        for (int i = 0; i < written.Length; i++)
            written[i] = new HashSet<Pattern>();
        return new Definition
        {
            Parameters = parameters, Written = written, Writes = writes, Hidden = hidden, Subtypes = subtypes,
        };
    }

    /// <summary>
    /// Marks the parameters of <paramref name="definition"/> that serialized members of it or of its base classes
    /// declared in referenced assemblies may write though its symbols lack them: a compilation imports only the
    /// public and protected members of a referenced class, so a private <c>[NinoMember] T _value</c> is found only in
    /// the layout its generator published (<c>[assembly: NinoTypeLayout]</c>, <c>name:type:encoding</c> per member).
    /// A referenced class without a layout may hide any member: all its parameters are marked.
    /// </summary>
    private void MarkHidden(INamedTypeSymbol definition, List<ITypeParameterSymbol> parameters, bool[] hidden)
    {
        for (var level = definition; level != null && level.IsNinoType(); level = level.BaseType)
        {
            if (level.TypeKind != TypeKind.Class || level.ContainingAssembly is null or ISourceAssemblySymbol)
                continue;
            var levelDefinition = level.OriginalDefinition;
            var levelParameters = TypeNames.AllTypeParameters(levelDefinition);
            if (levelParameters.Count == 0)
                continue;
            var named = new bool[levelParameters.Count];
            if (!LayoutsOf(level.ContainingAssembly).TryGetValue(levelDefinition, out var layout))
            {
                for (int k = 0; k < named.Length; k++)
                    named[k] = true;
            }
            else
            {
                var visible = new HashSet<string>(levelDefinition.GetNinoTypeOptions() is { } attr
                    ? _members.Of(levelDefinition, attr).Select(static m => m.Name)
                    : Enumerable.Empty<string>());
                foreach (var entry in layout.Split(';'))
                {
                    int colon = entry.IndexOf(':');
                    if (colon <= 0 || entry[0] == '[' || visible.Contains(entry.Substring(0, colon)))
                        continue;
                    var identifiers = Identifiers(entry.Substring(colon + 1));
                    for (int k = 0; k < named.Length; k++)
                        named[k] |= identifiers.Contains(levelParameters[k].Name);
                }
            }

            // The level's parameters as this definition gives them.
            var arguments = TypeNames.AllTypeArguments(level);
            for (int k = 0; k < named.Length; k++)
            {
                if (!named[k])
                    continue;
                for (int i = 0; i < parameters.Count; i++)
                    hidden[i] |= TypeNames.Mentions(arguments[k], parameters[i]);
            }
        }
    }

    private static HashSet<string> Identifiers(string text)
    {
        var found = new HashSet<string>();
        int start = -1;
        for (int i = 0; i <= text.Length; i++)
        {
            bool part = i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_');
            if (part && start < 0)
                start = i;
            else if (!part && start >= 0)
            {
                found.Add(text.Substring(start, i - start));
                start = -1;
            }
        }

        return found;
    }

    /// <summary>The <c>Members</c> of each <c>[NinoTypeLayout]</c> a referenced assembly publishes, by type definition.</summary>
    private static Dictionary<ISymbol, string> LayoutsOf(IAssemblySymbol assembly) =>
        Layouts.GetValue(assembly, static a =>
        {
            var layouts = new Dictionary<ISymbol, string>(SymbolEqualityComparer.Default);
            foreach (var attribute in a.GetAttributes())
            {
                if (attribute.AttributeClass is { Name: "NinoTypeLayoutAttribute" } layout &&
                    layout.ContainingNamespace?.ToDisplayString() == NinoNames.CoreNamespace &&
                    attribute.ConstructorArguments.Length == 7 &&
                    attribute.ConstructorArguments[0].Value is INamedTypeSymbol type &&
                    attribute.ConstructorArguments[6].Value is string members)
                    layouts[type.OriginalDefinition] = members;
            }

            return layouts;
        });

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

    /// <summary>What <paramref name="node"/> writes of its parameter <paramref name="index"/>, from what the others write so far.</summary>
    private IEnumerable<Pattern> Compute(Definition node, int index, Dictionary<INamedTypeSymbol, Definition> pending)
    {
        var parameter = node.Parameters[index];
        if (node.Hidden[index])
            yield return Pattern.Inside;
        foreach (var type in node.Writes)
        {
            foreach (var pattern in Find(type, parameter, pending))
                yield return pattern;
        }

        // A subtype writes what its patterns match of the argument it gives this one: ListShelf<U> : Shelf<U> writes
        // all of a Shelf<X>'s X, S<U> : Shelf<Tag<U>> the Y of a Shelf<Tag<Y>>.
        foreach (var (subtype, arguments) in node.Subtypes)
        {
            var subtypeParameters = TypeNames.AllTypeParameters(subtype);
            var written = WrittenOf(subtype, pending);
            for (int q = 0; q < written.Count; q++)
            {
                if (written[q].Count == 0 || !TypeNames.Mentions(arguments[index], subtypeParameters[q]))
                    continue;
                foreach (var pattern in written[q])
                    yield return Pattern.Of(arguments[index], subtypeParameters, subtypeParameters[q], pattern);
            }
        }
    }

    /// <summary>
    /// <paramref name="found"/> added to <paramref name="known"/>: a pattern too deep, or too many of them, become
    /// <see cref="Pattern.Inside"/>, which then stands alone.
    /// </summary>
    private static HashSet<Pattern> Normalize(HashSet<Pattern> known, IEnumerable<Pattern> found)
    {
        if (known.Contains(Pattern.Inside))
            return known;
        var all = new HashSet<Pattern>(known);
        foreach (var pattern in found)
        {
            if (pattern.Kind == PatternKind.Inside || pattern.Depth > MaxDepth || all.Add(pattern) && all.Count > MaxPatterns)
                return new HashSet<Pattern> { Pattern.Inside };
        }

        return all;
    }

    /// <summary>What <paramref name="definition"/> writes of each parameter, as decided so far.</summary>
    private IReadOnlyList<IReadOnlyCollection<Pattern>> WrittenOf(INamedTypeSymbol definition,
        Dictionary<INamedTypeSymbol, Definition> pending) =>
        pending.TryGetValue(definition, out var node) ? node.Written : _written[definition];

    /// <summary>
    /// What a value of <paramref name="type"/> (in terms of the definition's parameters) writes of the argument of
    /// <paramref name="parameter"/>.
    /// </summary>
    private IEnumerable<Pattern> Find(ITypeSymbol type, ITypeParameterSymbol parameter,
        Dictionary<INamedTypeSymbol, Definition> pending)
    {
        switch (type)
        {
            case ITypeParameterSymbol p:
                if (SymbolEqualityComparer.Default.Equals(p, parameter))
                    yield return Pattern.Whole;
                yield break;
            case IArrayTypeSymbol array:
                foreach (var pattern in Find(array.ElementType, parameter, pending))
                    yield return pattern;
                yield break;
            case INamedTypeSymbol named:
                var arguments = TypeNames.AllTypeArguments(named);
                if (arguments.Count == 0)
                    yield break;
                var definition = named.OriginalDefinition;
                if (definition.GetNinoTypeOptions() == null)
                {
                    // Not a [NinoType]: Nino.Core writes (or refuses) every type argument.
                    foreach (var argument in arguments)
                    {
                        foreach (var pattern in Find(argument, parameter, pending))
                            yield return pattern;
                    }

                    yield break;
                }

                var written = WrittenOf(definition, pending);
                for (int i = 0; i < arguments.Count; i++)
                {
                    foreach (var pattern in written[i])
                    {
                        foreach (var composed in Compose(pattern, arguments[i], parameter, pending))
                            yield return composed;
                    }
                }

                yield break;
        }
    }

    /// <summary>
    /// What <paramref name="pattern"/>, matched against the argument <paramref name="argument"/> (in terms of the
    /// definition's parameters), writes of the argument of <paramref name="parameter"/>.
    /// </summary>
    private IEnumerable<Pattern> Compose(Pattern pattern, ITypeSymbol argument, ITypeParameterSymbol parameter,
        Dictionary<INamedTypeSymbol, Definition> pending)
    {
        switch (pattern.Kind)
        {
            case PatternKind.Whole:
                return Find(argument, parameter, pending);
            case PatternKind.Inside:
                return TypeNames.Mentions(argument, parameter) ? new[] { Pattern.Inside } : Array.Empty<Pattern>();
            case PatternKind.Named or PatternKind.Array:
                // The parameter itself: its argument must have the pattern's shape.
                if (argument is ITypeParameterSymbol p)
                {
                    return SymbolEqualityComparer.Default.Equals(p, parameter)
                        ? new[] { pattern }
                        : Array.Empty<Pattern>();
                }

                // An argument of another shape never holds the subtype the pattern comes from.
                if (pattern.PartsOf(argument) is not { } parts)
                    return Array.Empty<Pattern>();
                return pattern.Parts.SelectMany((part, i) => Compose(part, parts[i], parameter, pending));
            default:
                return Array.Empty<Pattern>();
        }
    }
}

/// <summary>The kinds of <see cref="Pattern"/>.</summary>
internal enum PatternKind
{
    /// <summary>Nothing is written of the argument.</summary>
    Free,

    /// <summary>The argument is written as a whole, by its own rules.</summary>
    Whole,

    /// <summary>The argument and every type inside it may be written.</summary>
    Inside,

    /// <summary>A type the argument must be (no part of it is written).</summary>
    Exact,

    /// <summary>A constructed generic of <see cref="Pattern.Symbol"/>, its type arguments matched by the parts.</summary>
    Named,

    /// <summary>An array of <see cref="Pattern.Rank"/>, its element matched by the one part.</summary>
    Array,
}

/// <summary>
/// What a generic <c>[NinoType]</c> writes of one of its type arguments (<see cref="WrittenParameters"/>): the whole
/// argument, every type inside it, or, when the argument has a shape (<c>Tag&lt;Whole&gt;</c> of a subtype
/// <c>S&lt;U&gt; : Shelf&lt;Tag&lt;U&gt;&gt;</c>), what its parts write. Equal by structure.
/// </summary>
internal sealed class Pattern : IEquatable<Pattern>
{
    public static readonly Pattern Free = new(PatternKind.Free, null, 0, System.Array.Empty<Pattern>());
    public static readonly Pattern Whole = new(PatternKind.Whole, null, 0, System.Array.Empty<Pattern>());
    public static readonly Pattern Inside = new(PatternKind.Inside, null, 0, System.Array.Empty<Pattern>());

    private readonly int _hash;

    private Pattern(PatternKind kind, ITypeSymbol? symbol, int rank, Pattern[] parts)
    {
        Kind = kind;
        Symbol = symbol;
        Rank = rank;
        Parts = parts;
        Depth = parts.Length == 0 ? 0 : 1 + parts.Max(static p => p.Depth);
        int hash = (int)kind * 31 + rank;
        if (symbol != null)
            hash = hash * 31 + SymbolEqualityComparer.Default.GetHashCode(symbol);
        foreach (var part in parts)
            hash = hash * 31 + part._hash;
        _hash = hash;
    }

    public PatternKind Kind { get; }

    /// <summary>The type of <see cref="PatternKind.Exact"/>, the generic definition of <see cref="PatternKind.Named"/>.</summary>
    public ITypeSymbol? Symbol { get; }

    /// <summary>The rank of <see cref="PatternKind.Array"/>.</summary>
    public int Rank { get; }

    public Pattern[] Parts { get; }

    /// <summary>How many shapes deep the pattern goes.</summary>
    public int Depth { get; }

    /// <summary>
    /// The pattern <paramref name="argument"/>, a type argument a subtype gives its base in terms of the subtype's
    /// <paramref name="parameters"/>, is of the base's argument when the subtype writes <paramref name="written"/> of
    /// its parameter <paramref name="parameter"/> (and nothing of the others).
    /// </summary>
    public static Pattern Of(ITypeSymbol argument, List<ITypeParameterSymbol> parameters,
        ITypeParameterSymbol parameter, Pattern written)
    {
        switch (argument)
        {
            case ITypeParameterSymbol p:
                if (SymbolEqualityComparer.Default.Equals(p, parameter))
                    return written;
                return parameters.Contains(p, SymbolEqualityComparer.Default) ? Free : new Pattern(PatternKind.Exact, p, 0, System.Array.Empty<Pattern>());
            case IArrayTypeSymbol array when parameters.Any(p => TypeNames.Mentions(array, p)):
                return new Pattern(PatternKind.Array, null, array.Rank,
                    new[] { Of(array.ElementType, parameters, parameter, written) });
            case INamedTypeSymbol named when parameters.Any(p => TypeNames.Mentions(named, p)):
                return new Pattern(PatternKind.Named, named.OriginalDefinition, 0,
                    TypeNames.AllTypeArguments(named).Select(a => Of(a, parameters, parameter, written)).ToArray());
            default:
                return new Pattern(PatternKind.Exact, argument, 0, System.Array.Empty<Pattern>());
        }
    }

    /// <summary>
    /// The parts of <paramref name="type"/> the <see cref="Parts"/> of a <see cref="PatternKind.Named"/> or
    /// <see cref="PatternKind.Array"/> pattern match, or null when it has another shape.
    /// </summary>
    public IReadOnlyList<ITypeSymbol>? PartsOf(ITypeSymbol type)
    {
        switch (Kind)
        {
            case PatternKind.Array:
                return type is IArrayTypeSymbol array && array.Rank == Rank ? new[] { array.ElementType } : null;
            case PatternKind.Named:
                if (type is not INamedTypeSymbol named ||
                    !SymbolEqualityComparer.Default.Equals(named.OriginalDefinition, Symbol))
                    return null;
                var arguments = TypeNames.AllTypeArguments(named);
                return arguments.Count == Parts.Length ? arguments : null;
            default:
                return null;
        }
    }

    /// <summary>Adds the values this pattern writes of the closed argument <paramref name="type"/> to <paramref name="values"/>.</summary>
    public void Match(ITypeSymbol type, List<(ITypeSymbol Value, bool Inside)> values)
    {
        switch (Kind)
        {
            case PatternKind.Whole:
                values.Add((type, false));
                return;
            case PatternKind.Inside:
                values.Add((type, true));
                return;
            case PatternKind.Named or PatternKind.Array when PartsOf(type) is { } parts:
                for (int i = 0; i < Parts.Length; i++)
                    Parts[i].Match(parts[i], values);
                return;
        }
    }

    public bool Equals(Pattern? other)
    {
        if (ReferenceEquals(this, other))
            return true;
        if (other is null || other._hash != _hash || other.Kind != Kind || other.Rank != Rank ||
            !SymbolEqualityComparer.Default.Equals(other.Symbol, Symbol) || other.Parts.Length != Parts.Length)
            return false;
        for (int i = 0; i < Parts.Length; i++)
        {
            if (!Parts[i].Equals(other.Parts[i]))
                return false;
        }

        return true;
    }

    public override bool Equals(object? obj) => Equals(obj as Pattern);

    public override int GetHashCode() => _hash;
}
