using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;
using Nino.Generator.Common;

namespace Nino.Generator.Generation;

/// <summary>
/// Everything model builds ask about the symbols of one compilation, memoized and shared by every transform and
/// analyzer action on it: the Nino attribute types (<see cref="Symbols"/>), what generated code can access
/// (<see cref="Visibility"/>), serialized members (<see cref="Members"/>), raw layout (<see cref="Raw"/>), formatters
/// (<see cref="Resolver"/>), delegation and type ids. Symbols belong to their compilation, which is what an instance
/// lives as long as.
/// </summary>
internal sealed class TypeFacts
{
    private static readonly ConditionalWeakTable<Compilation, TypeFacts> Shared = new();

    private readonly ConcurrentDictionary<ISymbol, bool> _delegatable = new(SymbolEqualityComparer.Default);
    private readonly ConcurrentDictionary<ISymbol, bool> _fixedSize = new(SymbolEqualityComparer.Default);
    private readonly ConcurrentDictionary<ITypeSymbol, int> _typeIds = new(SymbolEqualityComparer.Default);
    private readonly ConcurrentDictionary<ISymbol, int> _bounds = new(SymbolEqualityComparer.Default);
    [ThreadStatic] private static HashSet<ISymbol>? t_bounding;
    private readonly Func<ISymbol, bool> _computeDelegatable;
    private readonly Func<ISymbol, bool> _computeFixedSize;
    private readonly ParseOptions? _options;
    private readonly string[] _symbols;

    /// <summary>The facts of <paramref name="compilation"/> (whose files share <paramref name="options"/>).</summary>
    public static TypeFacts For(Compilation compilation, ParseOptions? options)
    {
        if (!Shared.TryGetValue(compilation, out var facts))
            facts = Shared.GetValue(compilation, c => new TypeFacts(c, options));
        // A project's files normally share one ParseOptions instance: compare symbols only when they do not.
        if (ReferenceEquals(options, facts._options) || SymbolsOf(options).SequenceEqual(facts._symbols))
            return facts;
        // Files of the same compilation with other preprocessor symbols get facts of their own.
        return new TypeFacts(compilation, options);
    }

    private static IEnumerable<string> SymbolsOf(ParseOptions? options) =>
        options?.PreprocessorSymbolNames ?? Array.Empty<string>();

    private TypeFacts(Compilation compilation, ParseOptions? options)
    {
        Compilation = compilation;
        _options = options;
        _computeDelegatable = s => ComputeIsDelegatable((INamedTypeSymbol)s);
        _computeFixedSize = s => ComputeIsFixedSize((INamedTypeSymbol)s);
        _symbols = SymbolsOf(options).ToArray();
        WeakTolerance = _symbols.Contains("WEAK_VERSION_TOLERANCE");
        Net8 = _symbols.Contains("NET8_0_OR_GREATER");
        Symbols = new NinoSymbols(compilation);
        Visibility = new Visibility(compilation.Assembly);
        Members = new MemberCache(Symbols);
        Raw = new RawRules(Symbols, Members, compilation.Assembly, WeakTolerance);
        Resolver = new FormatterResolver(compilation, Symbols, Raw, Visibility);
    }

    public Compilation Compilation { get; }

    /// <summary>The Nino attribute types of this compilation.</summary>
    public NinoSymbols Symbols { get; }

    /// <summary>What generated code in this assembly can access.</summary>
    public Visibility Visibility { get; }

    /// <summary>Serialized members of each type (memoized).</summary>
    public MemberCache Members { get; }

    /// <summary>Which values are written as raw bytes (memoized).</summary>
    public RawRules Raw { get; }

    /// <summary>Formatter resolution for this compilation (memoized).</summary>
    public FormatterResolver Resolver { get; }

    /// <summary>The assembly defines WEAK_VERSION_TOLERANCE: all of its own types are version tolerant.</summary>
    public bool WeakTolerance { get; }

    /// <summary><c>[UnsafeAccessor]</c> is available.</summary>
    public bool Net8 { get; }

    /// <summary>Runtime type id (hash of the runtime name): many members share a type such as <c>List&lt;int&gt;</c>.</summary>
    public int TypeId(ITypeSymbol type) =>
        _typeIds.GetOrAdd(type, static t => TypeNames.ComputeId(TypeNames.RuntimeName(t)));

    /// <summary>
    /// Whether a class's formatter emits static <c>WriteMembers</c>/<c>ReadMembers</c> that subtypes can call: a
    /// non-sealed, non-keyed <c>[NinoType]</c> class generated in this assembly whose members are all directly
    /// readable and assignable.
    /// Computed from symbols only, so a subtype's transform reaches the same answer as the base's own.
    /// </summary>
    public bool IsDelegatable(INamedTypeSymbol type)
    {
        // Sealed classes have no subtypes to delegate to them.
        if (type.TypeKind != TypeKind.Class || type.IsStatic || type.IsSealed)
            return false;
        return _delegatable.GetOrAdd(type, _computeDelegatable);
    }

    private bool ComputeIsDelegatable(INamedTypeSymbol type)
    {
        if (!Visibility.IsOwnEligible(type))
            return false;
        var attr = type.GetNinoTypeOptions();
        if (attr == null || attr.Keyed)
            return false;

        foreach (var member in Members.Of(type, attr))
        {
            if (Visibility.IsConstructionOnly(member) || !Visibility.IsDirectlyAccessible(member))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Whether every value of <paramref name="type"/> (every non-null one, for a sealed class) is written in the same
    /// number of bytes, so its formatter implements <c>INinoFixedSizeFormatter</c>: a non-generic struct that is not
    /// raw, or a sealed class, generated in this assembly, positional (not version tolerant or keyed), not tracked and
    /// not a collection, whose members are all raw values or such structs, without custom formatters. Computed from
    /// symbols only, so a type that holds it as a member, the type itself and the interceptors agree.
    /// </summary>
    public bool IsFixedSize(INamedTypeSymbol type)
    {
        if (type.IsGenericType || !(type.TypeKind == TypeKind.Struct || type.TypeKind == TypeKind.Class && type.IsSealed))
            return false;
        return _fixedSize.GetOrAdd(type, _computeFixedSize);
    }

    private bool ComputeIsFixedSize(INamedTypeSymbol type)
    {
        if (!Visibility.IsGeneratedHere(type))
            return false;
        var attr = type.GetNinoTypeOptions();
        if (attr == null || attr.Keyed || attr.VersionTolerant || WeakTolerance)
            return false;
        bool isStruct = type.TypeKind == TypeKind.Struct;
        if (isStruct ? Raw.IsRawStruct(type) : ModelBuilder.IsTracked(type) || Resolver.ResolveCollection(type) != null)
            return false;
        var members = Members.Of(type, attr);
        // A struct without members writes nothing.
        if (isStruct && members.Count == 0)
            return false;
        foreach (var member in members)
        {
            if (NinoSymbols.Has(member, Symbols.CustomFormatter) || MemberCollector.MemberType(member) is not { } memberType)
                return false;
            if (Raw.IsRawUnmanaged(memberType) && RawLayout.IsPortable(memberType))
                continue;
            // A struct of fixed size, written by its generated formatter.
            if (memberType is not INamedTypeSymbol { TypeKind: TypeKind.Struct } nested || !IsFixedSize(nested) ||
                Resolver.Resolve(nested, false)?.Code != FormatterResolver.GeneratedFormatter(nested))
                return false;
        }

        return true;
    }

    // ----- nesting bound -------------------------------------------------------------------------------------

    /// <summary>
    /// <c>NinoDepth.Inline</c> in Nino.Core: a class body opened with this many bodies open is deferred.
    /// </summary>
    public const int InlineDepth = 128;

    /// <summary>A nesting no bound covers (<see cref="DepthBound"/>).</summary>
    public const int Unbounded = int.MaxValue;

    /// <summary>
    /// How deep below the current depth a value of <paramref name="type"/> can check the depth: the largest
    /// <c>k</c> such that writing or reading it may open a class body with <c>depth + k</c> bodies open, where
    /// <c>depth</c> is the count when it starts; -1 when it opens no class body, <see cref="Unbounded"/> when that is
    /// not known at compile time or not below <see cref="InlineDepth"/>. A sealed class checks at 0 and its members
    /// one level below; a struct counts no level; collections, nullables and tuples of Nino.Core are their
    /// elements. Unbounded: recursive types (directly or through other types and collections), classes that are not
    /// sealed or interfaces (a subtype may come from anywhere), generic types, types generated elsewhere, custom
    /// formatters, other libraries' formatters and <c>object</c>. When <c>depth + k</c> stays below
    /// <see cref="InlineDepth"/>, no body inside is deferred and the count is seen by nothing: the shallow twins
    /// (<see cref="HasShallow"/>) skip it. Computed from symbols only, so a type, the types holding it and the
    /// interceptors agree.
    /// </summary>
    public int DepthBound(ITypeSymbol type, bool utf8)
    {
        if (Resolver.Resolve(type, utf8) is not { } resolved)
            return Unbounded;
        if (!resolved.OpensBodies)
            return -1;
        if (resolved.Body != null)
            return TypeBound(resolved.Body);
        if (resolved.Elements.IsDefault)
            return Unbounded;
        // A collection reached again through its own elements (class Tree : List<Tree>) is recursive.
        var computing = t_bounding ??= new HashSet<ISymbol>(SymbolEqualityComparer.Default);
        if (!computing.Add(type))
            return Unbounded;
        try
        {
            int bound = -1;
            foreach (var element in resolved.Elements)
                bound = Math.Max(bound, DepthBound(element, utf8));
            return bound;
        }
        finally
        {
            computing.Remove(type);
        }
    }

    /// <summary><see cref="DepthBound"/> of a <c>[NinoType]</c> generated here (its body).</summary>
    public int TypeBound(INamedTypeSymbol type)
    {
        if (_bounds.TryGetValue(type, out int bound))
            return bound;
        // A type reached again while its own bound is computed is recursive: unbounded, and so is every type on
        // the way (each reaches itself through it), which is what they compute.
        var computing = t_bounding ??= new HashSet<ISymbol>(SymbolEqualityComparer.Default);
        if (!computing.Add(type))
            return Unbounded;
        try
        {
            bound = ComputeTypeBound(type);
        }
        finally
        {
            computing.Remove(type);
        }

        return _bounds.GetOrAdd(type, bound);
    }

    private int ComputeTypeBound(INamedTypeSymbol type)
    {
        if (!Visibility.IsGeneratedHere(type) || TypeNames.AllTypeArguments(type).Count != 0 ||
            type.GetNinoTypeOptions() is not { } attr)
            return Unbounded;
        bool isStruct = type.TypeKind == TypeKind.Struct;
        if (!isStruct && (type.TypeKind != TypeKind.Class || !type.IsSealed || type.IsAbstract))
            return Unbounded;
        if (isStruct && Raw.IsRawStruct(type))
            return -1;

        int inner = -1;
        if (!isStruct && Resolver.ResolveCollection(type) is { } collection)
        {
            if (!collection.OpensBodies)
                inner = -1;
            else if (collection.Elements.IsDefault)
                return Unbounded;
            else
            {
                foreach (var element in collection.Elements)
                    inner = Math.Max(inner, DepthBound(element, false));
            }
        }

        foreach (var member in Members.Of(type, attr))
        {
            if (NinoSymbols.Has(member, Symbols.CustomFormatter))
                return Unbounded;
            if (MemberCollector.MemberType(member) is not { } memberType)
                continue;
            inner = Math.Max(inner, DepthBound(memberType.WithNullableAnnotation(NullableAnnotation.NotAnnotated),
                NinoSymbols.Has(member, Symbols.Utf8)));
            if (inner == Unbounded)
                return Unbounded;
        }

        // A struct counts no level; a class body checks at 0 and opens its members' one level below.
        int bound = isStruct ? inner : inner + 1;
        return bound >= InlineDepth ? Unbounded : bound;
    }

    /// <summary>
    /// Whether the generated formatter of <paramref name="type"/> has a <c>__Shallow</c> twin: the same bytes, for
    /// where its <see cref="DepthBound"/> is known to stay below <see cref="InlineDepth"/>, counting and checking no
    /// depth, its members through the twins of theirs. Sealed classes and structs that open a class body, not
    /// tracked (their own formatter is used there, which is correct anywhere).
    /// </summary>
    public bool HasShallow(INamedTypeSymbol type)
    {
        int bound = TypeBound(type);
        return bound != Unbounded && bound >= 0 && !(type.TypeKind == TypeKind.Class && ModelBuilder.IsTracked(type));
    }

    /// <summary>
    /// <paramref name="formatter"/> (resolved as <paramref name="resolved"/>) with the generated formatters it is
    /// composed of replaced by their <c>__Shallow</c> twins (<see cref="HasShallow"/>).
    /// </summary>
    public string ShallowFormatter(string formatter, ResolvedFormatter resolved, bool utf8)
    {
        if (!resolved.OpensBodies)
            return formatter;
        var twins = new HashSet<string>();
        CollectShallow(resolved, utf8, twins, 0);
        return twins.Count == 0 ? formatter : TypeNames.WithTwin(formatter, twins, ".__Shallow");
    }

    private void CollectShallow(ResolvedFormatter resolved, bool utf8, HashSet<string> twins, int level)
    {
        if (!resolved.OpensBodies || level > 32)
            return;
        if (resolved.Body is { } body)
        {
            if (HasShallow(body))
                twins.Add(resolved.Code);
            return;
        }

        if (resolved.Elements.IsDefault)
            return;
        foreach (var element in resolved.Elements)
        {
            if (Resolver.Resolve(element, utf8) is { } inner)
                CollectShallow(inner, utf8, twins, level + 1);
        }
    }
}
