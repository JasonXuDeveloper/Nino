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

    /// <summary>The facts of the files of a compilation whose preprocessor symbols differ from the first's, by symbols.</summary>
    private static readonly ConditionalWeakTable<Compilation, ConcurrentDictionary<string, TypeFacts>> Others = new();

    private readonly ConcurrentDictionary<ISymbol, bool> _delegatable = new(SymbolEqualityComparer.Default);
    private readonly ConcurrentDictionary<ISymbol, bool> _fixedSize = new(SymbolEqualityComparer.Default);
    private readonly ConcurrentDictionary<ISymbol, bool> _blocks = new(SymbolEqualityComparer.Default);
    private readonly ConcurrentDictionary<ITypeSymbol, int> _typeIds = new(SymbolEqualityComparer.Default);
    private readonly ConcurrentDictionary<ISymbol, int> _bounds = new(SymbolEqualityComparer.Default);
    private readonly ConcurrentDictionary<ISymbol, TypeShape?> _shapes = new(SymbolEqualityComparer.Default);
    private readonly Func<ISymbol, TypeShape?> _computeShape;
    [ThreadStatic] private static HashSet<ISymbol>? t_bounding;
    private readonly Func<ISymbol, bool> _computeDelegatable;
    private readonly Func<ISymbol, bool> _computeFixedSize;
    private readonly Func<ISymbol, bool> _computeBlock;
    private readonly ParseOptions? _options;
    private readonly Compilation _compilation;
    private readonly ConcurrentDictionary<IAssemblySymbol, INamedTypeSymbol[]> _genericDefinitions =
        new(SymbolEqualityComparer.Default);
    private readonly string[] _symbols;

    /// <summary>The facts of <paramref name="compilation"/> (whose files share <paramref name="options"/>).</summary>
    public static TypeFacts For(Compilation compilation, ParseOptions? options)
    {
        if (!Shared.TryGetValue(compilation, out var facts))
            facts = Shared.GetValue(compilation, c => new TypeFacts(c, options));
        // A project's files normally share one ParseOptions instance: compare symbols only when they do not.
        if (ReferenceEquals(options, facts._options) || SymbolsOf(options).SequenceEqual(facts._symbols))
            return facts;
        // Files of the same compilation with other preprocessor symbols get facts of their own, shared by every file
        // with the same symbols.
        return Others.GetValue(compilation, static _ => new ConcurrentDictionary<string, TypeFacts>())
            .GetOrAdd(ModelCache.SymbolKey(options), _ => new TypeFacts(compilation, options));
    }

    private static IEnumerable<string> SymbolsOf(ParseOptions? options) =>
        options?.PreprocessorSymbolNames ?? Array.Empty<string>();

    private TypeFacts(Compilation compilation, ParseOptions? options)
    {
        _options = options;
        _compilation = compilation;
        _computeDelegatable = s => ComputeIsDelegatable((INamedTypeSymbol)s);
        _computeFixedSize = s => ComputeIsFixedSize((INamedTypeSymbol)s);
        _computeBlock = s => ComputeIsBlock((INamedTypeSymbol)s);
        _computeShape = s => ((INamedTypeSymbol)s).GetNinoTypeOptions() is { } attr
            ? new TypeShape((INamedTypeSymbol)s, attr, this)
            : null;
        _symbols = SymbolsOf(options).ToArray();
        WeakTolerance = _symbols.Contains("WEAK_VERSION_TOLERANCE");
        Net8 = _symbols.Contains("NET8_0_OR_GREATER");
        Net9 = Net8 && _symbols.Contains("NET9_0_OR_GREATER");
        Symbols = new NinoSymbols(compilation);
        Visibility = new Visibility(compilation.Assembly);
        Members = new MemberCache(Symbols);
        Raw = new RawRules(Symbols, Members, compilation.Assembly, WeakTolerance);
        Resolver = new FormatterResolver(compilation, Symbols, Raw, Visibility);
    }

    /// <summary>
    /// The generic <c>[NinoType]</c> definitions that can be a subtype of a closed generic slot whose definition is in
    /// <paramref name="assembly"/>: those of this compilation and of that assembly (not nested in a generic type).
    /// </summary>
    public IEnumerable<INamedTypeSymbol> GenericDefinitionsFor(IAssemblySymbol assembly)
    {
        var own = GenericDefinitionsIn(_compilation.Assembly);
        return SymbolEqualityComparer.Default.Equals(assembly, _compilation.Assembly)
            ? own
            : own.Concat(GenericDefinitionsIn(assembly));
    }

    private INamedTypeSymbol[] GenericDefinitionsIn(IAssemblySymbol assembly) =>
        _genericDefinitions.GetOrAdd(assembly, static a =>
        {
            var found = new List<INamedTypeSymbol>();
            var namespaces = new Stack<INamespaceSymbol>();
            namespaces.Push(a.GlobalNamespace);
            while (namespaces.Count != 0)
            {
                var ns = namespaces.Pop();
                foreach (var child in ns.GetNamespaceMembers())
                    namespaces.Push(child);
                var types = new Stack<INamedTypeSymbol>(ns.GetTypeMembers());
                while (types.Count != 0)
                {
                    var type = types.Pop();
                    if (type.IsGenericType)
                    {
                        // A type nested in a generic type is closed with its container: left out.
                        if (type.IsNinoType())
                            found.Add(type);
                        continue;
                    }

                    foreach (var nested in type.GetTypeMembers())
                        types.Push(nested);
                }
            }

            return found.ToArray();
        });

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

    /// <summary>Whether a type with <paramref name="attr"/> is version tolerant: so marked, or <see cref="WeakTolerance"/>.</summary>
    public bool IsTolerant(NinoTypeOptions attr) => attr.VersionTolerant || WeakTolerance;

    /// <summary>
    /// What the <c>[NinoType]</c> <paramref name="type"/> is, as the rules read it (memoized); null for a type that is
    /// not one.
    /// </summary>
    public TypeShape? Shape(INamedTypeSymbol type) => _shapes.GetOrAdd(type, _computeShape);

    /// <summary><c>[UnsafeAccessor]</c> is available.</summary>
    public bool Net8 { get; }

    /// <summary><c>[UnsafeAccessor]</c> reaches members of generic types (declared in a generic type).</summary>
    public bool Net9 { get; }

    /// <summary>Runtime type id (hash of the runtime name): many members share a type such as <c>List&lt;int&gt;</c>.</summary>
    public int TypeId(ITypeSymbol type) =>
        _typeIds.GetOrAdd(type, static t => TypeNames.ComputeId(TypeNames.RuntimeName(t)));

    /// <summary>
    /// The ids of a <c>[NinoType]</c>: <c>Id</c>, its wire id (the hash of its <c>[NinoFormerName]</c>, else of its
    /// runtime name), and <c>NameId</c>, the hash of its runtime name, which a by-type lookup computes without
    /// reflection (the same unless the type was renamed).
    /// </summary>
    public (int Id, int NameId) Ids(INamedTypeSymbol type)
    {
        int nameId = TypeId(type);
        return NinoSymbols.Get(type.OriginalDefinition, Symbols.FormerName)?.ConstructorArguments
            .FirstOrDefault().Value is string formerName
            ? (TypeNames.ComputeId(formerName), nameId)
            : (nameId, nameId);
    }

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
    /// not a collection, whose members are all raw values, such structs or value tuples and pairs of raw values
    /// (<see cref="IsBlockItem"/>), without custom formatters. Computed from symbols only, so a type that holds it as a
    /// member, the type itself and the interceptors agree.
    /// </summary>
    public bool IsFixedSize(INamedTypeSymbol type)
    {
        if (type.IsGenericType || !(type.TypeKind == TypeKind.Struct || type.TypeKind == TypeKind.Class && type.IsSealed))
            return false;
        return _fixedSize.GetOrAdd(type, _computeFixedSize);
    }

    private bool ComputeIsFixedSize(INamedTypeSymbol type)
    {
        if (Shape(type) is not { GeneratedHere: true, Keyed: false, Tolerant: false } shape)
            return false;
        bool isStruct = shape.Kind == FormatterKind.Struct;
        if (isStruct ? shape.RawStruct : shape.Tracked || shape.Collection != null)
            return false;
        var members = Members.Of(type, shape.Options);
        // A struct without members writes nothing.
        if (isStruct && members.Count == 0)
            return false;
        foreach (var member in members)
        {
            if (NinoSymbols.Has(member, Symbols.CustomFormatter) || MemberCollector.MemberType(member) is not { } memberType)
                return false;
            if (Raw.IsRawUnmanaged(memberType) && RawLayout.IsPortable(memberType) || IsBlockItem(memberType))
                continue;
            // A struct of fixed size, written by its generated formatter.
            if (memberType is not INamedTypeSymbol { TypeKind: TypeKind.Struct } nested || !IsFixedSize(nested) ||
                Resolver.Resolve(nested, false) is not { Traits: var traits } ||
                (traits & FormatterTraits.Generated) == 0)
                return false;
        }

        return true;
    }

    /// <summary>
    /// Whether <paramref name="type"/> is a value tuple (up to seven items) or a <c>KeyValuePair</c> whose items are
    /// all raw values of the same layout on every runtime (<see cref="RawLayout.IsPortable"/>), written by Nino.Core's
    /// formatter: its <c>INinoBlockFormatter</c> writes it as a block of the items' bytes (<c>NinoBlocks</c>), the
    /// same bytes as its <c>Serialize</c>, of the same size on every runtime.
    /// </summary>
    public bool IsBlockItem(ITypeSymbol type)
    {
        if (type is not INamedTypeSymbol { IsGenericType: true } named || !named.IsValueType)
            return false;
        var definition = named.OriginalDefinition;
        if (!(TypeNames.IsValueTuple(definition) && named.Arity <= 7 ||
              TypeNames.HasMetadataName(definition, "System.Collections.Generic.KeyValuePair`2")))
            return false;
        if (Resolver.Resolve(named, false) is not { Traits: var traits } || (traits & FormatterTraits.Core) == 0)
            return false;
        foreach (var item in named.TypeArguments)
        {
            // Bound to a raw formatter: Nino.Core's for an unmanaged value, the generated one of a raw struct.
            if (!Raw.IsRawUnmanaged(item) || !RawLayout.IsPortable(item) ||
                Resolver.Resolve(item, false) is not { Traits: var itemTraits } ||
                (itemTraits & (FormatterTraits.Raw | FormatterTraits.Generated)) == 0)
                return false;
        }

        return true;
    }

    /// <summary>
    /// Whether the formatter of the <c>[NinoType]</c> struct <paramref name="type"/> implements
    /// <c>INinoBlockFormatter</c>, so collections of it are read and written as blocks (several values at a time by
    /// vector shuffles where its <c>BlockMap</c> allows it): a struct of fixed size (<see cref="IsFixedSize"/>; raw
    /// structs are copied whole already) whose bytes in memory are its members' (every instance field is a member, or
    /// an auto-property's backing field) and whose members generated code reads and assigns directly, after it is
    /// created (no <c>[NinoConstructor]</c>, no read-only or init-only member), each a raw value, a value tuple or pair
    /// of raw values (<see cref="IsBlockItem"/>), or another such struct. Computed from symbols only, so a struct that
    /// holds it and the struct itself agree.
    /// </summary>
    public bool IsBlock(INamedTypeSymbol type)
    {
        if (type.TypeKind != TypeKind.Struct || !IsFixedSize(type))
            return false;
        return _blocks.GetOrAdd(type, _computeBlock);
    }

    private bool ComputeIsBlock(INamedTypeSymbol type)
    {
        var shape = Shape(type)!;
        var members = Members.Of(type, shape.Options);
        var known = new HashSet<ISymbol>(members.Select(static m => m.OriginalDefinition), SymbolEqualityComparer.Default);
        // No byte of a value outside its members: reads of a block overwrite all of it.
        foreach (var field in type.GetMembers().OfType<IFieldSymbol>())
        {
            if (field.IsStatic || field.IsConst)
                continue;
            var member = field.AssociatedMember() ?? field;
            if (!known.Contains(member.OriginalDefinition))
                return false;
        }

        // Created as default and assigned member by member, as ReadBody does it in place.
        if (type.InstanceConstructors.Any(c => NinoSymbols.Has(c, Symbols.Constructor)) ||
            type.GetMembers().OfType<IMethodSymbol>().Any(m => m.IsStatic && NinoSymbols.Has(m, Symbols.Constructor)))
            return false;
        foreach (var member in members)
        {
            if (!Visibility.IsDirectlyAccessible(member) || Visibility.IsConstructionOnly(member))
                return false;
            // A property only as an auto-property: its setter stores the value as it is.
            if (member is IPropertySymbol property &&
                !type.GetMembers().OfType<IFieldSymbol>().Any(f =>
                    SymbolEqualityComparer.Default.Equals(f.AssociatedMember(), property)))
                return false;
            var memberType = MemberCollector.MemberType(member)!;
            if (Raw.IsRawUnmanaged(memberType) && RawLayout.IsPortable(memberType) || IsBlockItem(memberType))
                continue;
            if (memberType is not INamedTypeSymbol nested || !IsBlock(nested))
                return false;
        }

        return true;
    }

    // ----- nesting bound -------------------------------------------------------------------------------------

    /// <summary>A nesting no bound covers (<see cref="DepthBound"/>).</summary>
    public const int Unbounded = int.MaxValue;

    /// <summary>
    /// How deep below the current depth a value of <paramref name="type"/> can check the depth: the largest
    /// <c>k</c> such that writing or reading it may open a class body with <c>depth + k</c> bodies open, where
    /// <c>depth</c> is the count when it starts; -1 when it opens no class body, <see cref="Unbounded"/> when that is
    /// not known at compile time or not below <see cref="NinoWire.InlineDepth"/>. A sealed class checks at 0 and its
    /// members one level below; a struct counts no level; collections, nullables and tuples of Nino.Core are their
    /// elements. Unbounded: recursive types (directly or through other types and collections), classes that are not
    /// sealed or interfaces (a subtype may come from anywhere), generic types, types generated elsewhere, custom
    /// formatters, other libraries' formatters and <c>object</c>. When <c>depth + k</c> stays below
    /// <see cref="NinoWire.InlineDepth"/>, no body inside is deferred and the count is seen by nothing: the shallow
    /// twins (<see cref="HasShallow"/>) skip it. Computed from symbols only, so a type, the types holding it and the
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
        if (Shape(type) is not { GeneratedHere: true, Generic: false } shape)
            return Unbounded;
        bool isStruct = shape.Kind == FormatterKind.Struct;
        if (!isStruct && shape.Kind != FormatterKind.SealedClass)
            return Unbounded;
        if (isStruct && shape.RawStruct)
            return -1;

        int inner = -1;
        if (shape.Collection is { } collection)
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

        foreach (var member in Members.Of(type, shape.Options))
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
        return bound >= NinoWire.InlineDepth ? Unbounded : bound;
    }

    /// <summary>
    /// The twin of the generated formatter of <paramref name="type"/> for where its <see cref="DepthBound"/> is known
    /// to stay below <see cref="NinoWire.InlineDepth"/>: the same bytes, counting and checking no depth, its members
    /// through the twins of theirs. Sealed classes and structs that open a class body (a sealed class opens its own), not
    /// tracked (their own formatter is used there, which is correct anywhere); null for any other type.
    /// <see cref="TypeNames.Top"/> for a sealed class that has a <c>__Top</c> twin (<see cref="TypeRules.HasTop"/>,
    /// whose body nests nothing): that twin already counts and checks no depth, and is correct wherever no body inside
    /// it is deferred, which none is. Otherwise <see cref="TypeNames.Shallow"/>.
    /// </summary>
    public string? ShallowTwin(INamedTypeSymbol type)
    {
        int bound = TypeBound(type);
        if (bound == Unbounded || bound < 0)
            return null;
        // A bound is known only for a non-generic sealed class or struct generated here.
        var shape = Shape(type)!;
        if (shape.Kind != FormatterKind.SealedClass)
            return TypeNames.Shallow;
        if (shape.TrackReferences)
            return null;
        return TypeRules.HasTop(shape) ? TypeNames.Top : TypeNames.Shallow;
    }

    /// <summary>Whether the formatter of <paramref name="type"/> has a <c>__Shallow</c> twin (<see cref="ShallowTwin"/>).</summary>
    public bool HasShallow(INamedTypeSymbol type) => ShallowTwin(type) == TypeNames.Shallow;

    /// <summary>
    /// <paramref name="formatter"/> (resolved as <paramref name="resolved"/>) with the generated formatters it is
    /// composed of replaced by their twins (<see cref="ShallowTwin"/>).
    /// </summary>
    public FormatterRef ShallowFormatter(FormatterRef formatter, ResolvedFormatter resolved, bool utf8)
    {
        if (!resolved.OpensBodies)
            return formatter;
        var tops = new HashSet<string>();
        var shallow = new HashSet<string>();
        CollectShallow(resolved, utf8, tops, shallow, 0);
        return formatter.WithTwins(tops, shallow);
    }

    private void CollectShallow(ResolvedFormatter resolved, bool utf8, HashSet<string> tops, HashSet<string> shallow,
        int level)
    {
        if (!resolved.OpensBodies || level > 32)
            return;
        if (resolved.Body is { } body)
        {
            var twin = ShallowTwin(body);
            if (twin != null)
                (twin == TypeNames.Top ? tops : shallow).Add(resolved.Code.Name);
            return;
        }

        if (resolved.Elements.IsDefault)
            return;
        foreach (var element in resolved.Elements)
        {
            if (Resolver.Resolve(element, utf8) is { } inner)
                CollectShallow(inner, utf8, tops, shallow, level + 1);
        }
    }
}
