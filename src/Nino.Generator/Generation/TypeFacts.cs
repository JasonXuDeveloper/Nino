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
    private readonly ConcurrentDictionary<ITypeSymbol, int> _typeIds = new(SymbolEqualityComparer.Default);
    private readonly Func<ISymbol, bool> _computeDelegatable;
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
}
