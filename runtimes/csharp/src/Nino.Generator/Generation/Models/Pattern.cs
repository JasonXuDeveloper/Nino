using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace Nino.Generator.Generation;

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

    /// <summary>
    /// A place of the subtype parameter <see cref="Pattern.Symbol"/> that its base names more than once: every place
    /// must hold one type; the one part says what is written of it.
    /// </summary>
    Bound,
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
        Depth = parts.Length == 0 ? 0 : kind == PatternKind.Bound ? parts[0].Depth : 1 + parts.Max(static p => p.Depth);
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
        ITypeParameterSymbol parameter, Pattern written, ICollection<ITypeParameterSymbol> repeated)
    {
        switch (argument)
        {
            case ITypeParameterSymbol p:
                var place = SymbolEqualityComparer.Default.Equals(p, parameter) ? written
                    : parameters.Contains(p, SymbolEqualityComparer.Default) ? Free
                    : new Pattern(PatternKind.Exact, p, 0, System.Array.Empty<Pattern>());
                return repeated.Contains(p) ? new Pattern(PatternKind.Bound, p, 0, new[] { place }) : place;
            case IArrayTypeSymbol array when parameters.Any(p => TypeNames.Mentions(array, p)):
                return new Pattern(PatternKind.Array, null, array.Rank,
                    new[] { Of(array.ElementType, parameters, parameter, written, repeated) });
            case INamedTypeSymbol named when parameters.Any(p => TypeNames.Mentions(named, p)):
                return new Pattern(PatternKind.Named, named.OriginalDefinition, 0,
                    TypeNames.AllTypeArguments(named).Select(a => Of(a, parameters, parameter, written, repeated)).ToArray());
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

    /// <summary>
    /// Whether <paramref name="type"/> can be the argument this pattern comes from: its shape, its fixed types
    /// (<c>int</c> of <c>S&lt;U&gt; : Shelf&lt;Pair&lt;int, U&gt;&gt;</c>) and one type in every place of a repeated
    /// parameter, which it adds to <paramref name="bindings"/> (checked across arguments by
    /// <see cref="WrittenParameters.ValuesOf"/>). A part that names a type parameter fits anything: it may be any type.
    /// </summary>
    public bool Fits(ITypeSymbol type, ref Dictionary<ISymbol, ITypeSymbol>? bindings)
    {
        switch (Kind)
        {
            case PatternKind.Exact:
                return Symbol is ITypeParameterSymbol || !Differ(Symbol!, type);
            case PatternKind.Named or PatternKind.Array:
                // A type parameter may be of any shape (nothing is collected from it).
                if (PartsOf(type) is not { } parts)
                    return type is ITypeParameterSymbol;
                for (int i = 0; i < Parts.Length; i++)
                {
                    if (!Parts[i].Fits(parts[i], ref bindings))
                        return false;
                }

                return true;
            case PatternKind.Bound:
                bindings ??= new Dictionary<ISymbol, ITypeSymbol>(SymbolEqualityComparer.Default);
                if (bindings.TryGetValue(Symbol!, out var other))
                {
                    if (Differ(other, type))
                        return false;
                }
                else
                    bindings.Add(Symbol!, type);

                return Parts[0].Fits(type, ref bindings);
            default:
                return true;
        }
    }

    /// <summary>Whether two types are surely different: equal, or naming a type parameter (which may be either), is not.</summary>
    public static bool Differ(ITypeSymbol a, ITypeSymbol b) =>
        !SymbolEqualityComparer.Default.Equals(a, b) && !TypeNames.ContainsTypeParameter(a) &&
        !TypeNames.ContainsTypeParameter(b);

    /// <summary>
    /// Adds the values this pattern writes of the argument <paramref name="type"/>, which <see cref="Fits"/>, to
    /// <paramref name="values"/>.
    /// </summary>
    public void Collect(ITypeSymbol type, List<(ITypeSymbol Value, bool Inside)> values)
    {
        switch (Kind)
        {
            case PatternKind.Whole:
                values.Add((type, false));
                return;
            case PatternKind.Inside:
                values.Add((type, true));
                return;
            case PatternKind.Bound:
                Parts[0].Collect(type, values);
                return;
            case PatternKind.Named or PatternKind.Array when PartsOf(type) is { } parts:
                for (int i = 0; i < Parts.Length; i++)
                    Parts[i].Collect(parts[i], values);
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
