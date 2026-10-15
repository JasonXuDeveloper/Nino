using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;

namespace Nino.Generator.Generation;

/// <summary>
/// Facts about types that model builds ask for (serialized members, raw layout, delegation), memoized for one
/// compilation and shared by every transform and analyzer action on it: a struct or base class is reached by many
/// members and subtypes, so a deep hierarchy collects each level's members once instead of once per subtype.
/// Symbols belong to their compilation, which is what an instance lives as long as.
/// </summary>
internal sealed class TypeFacts
{
    private static readonly ConditionalWeakTable<Compilation, TypeFacts> Shared = new();

    private readonly ConcurrentDictionary<ISymbol, bool> _rawStructs = new(SymbolEqualityComparer.Default);
    private readonly ConcurrentDictionary<ISymbol, bool> _delegatable = new(SymbolEqualityComparer.Default);
    private readonly ConcurrentDictionary<ISymbol, IReadOnlyList<ISymbol>> _members = new(SymbolEqualityComparer.Default);
    private readonly ConcurrentDictionary<ITypeSymbol, int> _typeIds = new(SymbolEqualityComparer.Default);
    private readonly string[] _symbols;

    /// <summary>The facts of <paramref name="compilation"/> (whose files share <paramref name="options"/>).</summary>
    public static TypeFacts For(Compilation compilation, ParseOptions? options)
    {
        var facts = Shared.GetValue(compilation, c => new TypeFacts(c, options));
        // A project's files share their parse options; anything else gets facts of its own.
        return SymbolsOf(options).SequenceEqual(facts._symbols) ? facts : new TypeFacts(compilation, options);
    }

    private static IEnumerable<string> SymbolsOf(ParseOptions? options) =>
        options?.PreprocessorSymbolNames ?? System.Array.Empty<string>();

    private TypeFacts(Compilation compilation, ParseOptions? options)
    {
        Assembly = compilation.Assembly;
        Symbols = new NinoSymbols(compilation);
        _symbols = SymbolsOf(options).ToArray();
        WeakTolerance = _symbols.Contains("WEAK_VERSION_TOLERANCE");
        Net8 = _symbols.Contains("NET8_0_OR_GREATER");
        Resolver = new FormatterResolver(this);
    }

    public IAssemblySymbol Assembly { get; }

    /// <summary>The Nino attribute types of this compilation.</summary>
    public NinoSymbols Symbols { get; }

    /// <summary>Formatter resolution for this compilation (memoized).</summary>
    public FormatterResolver Resolver { get; }

    /// <summary>The assembly defines WEAK_VERSION_TOLERANCE: all of its own types are version tolerant.</summary>
    public bool WeakTolerance { get; }

    /// <summary><c>[UnsafeAccessor]</c> is available.</summary>
    public bool Net8 { get; }

    /// <summary>Whether WEAK_VERSION_TOLERANCE applies to <paramref name="type"/> (only types of the defining assembly).</summary>
    public bool IsWeak(ITypeSymbol type) =>
        WeakTolerance && SymbolEqualityComparer.Default.Equals(type.ContainingAssembly, Assembly);

    /// <summary>Whether a formatter can be generated for the type (it must be visible to generated code).</summary>
    public static bool IsEligible(INamedTypeSymbol type)
    {
        if (type.IsStatic || type.IsRefLikeType || type.TypeKind is not (TypeKind.Class or TypeKind.Struct
                or TypeKind.Interface))
            return false;
        for (ISymbol? s = type; s is INamedTypeSymbol t; s = t.ContainingType)
        {
            if (t.DeclaredAccessibility is not (Accessibility.Public or Accessibility.Internal
                or Accessibility.ProtectedOrInternal))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Whether generated code in this assembly can name <paramref name="symbol"/> (a member, accessor or
    /// constructor): public, or internal to this assembly. Null (no accessor) counts as visible.
    /// </summary>
    public bool IsVisible(ISymbol? symbol) => symbol == null || symbol.DeclaredAccessibility switch
    {
        Accessibility.Public => true,
        Accessibility.Internal or Accessibility.ProtectedOrInternal =>
            SymbolEqualityComparer.Default.Equals(symbol.ContainingAssembly, Assembly),
        _ => false,
    };

    /// <summary>Whether generated code can read and assign the member directly (<c>value.Member</c>).</summary>
    public bool IsDirectlyAccessible(ISymbol member) =>
        IsVisible(member) && (member is not IPropertySymbol property ||
                              (IsVisible(property.GetMethod) && IsVisible(property.SetMethod)));

    /// <summary>Whether the member can only be assigned while constructing (readonly field, init-only or no setter).</summary>
    public static bool IsConstructionOnly(ISymbol member) => member switch
    {
        IFieldSymbol field => field.IsReadOnly,
        IPropertySymbol property => property.SetMethod == null || property.SetMethod.IsInitOnly,
        _ => false,
    };

    /// <summary>The type of a field or property; null for other members.</summary>
    public static ITypeSymbol? MemberType(ISymbol member) => member switch
    {
        IFieldSymbol field => field.Type,
        IPropertySymbol property => property.Type,
        _ => null,
    };

    /// <summary>Whether this compilation generates a formatter for <paramref name="type"/>.</summary>
    public bool IsGeneratedHere(INamedTypeSymbol type) => IsOwnEligible(type) && type.OriginalDefinition.IsNinoType();

    // A type of this assembly that generated code can name.
    private bool IsOwnEligible(INamedTypeSymbol type) =>
        SymbolEqualityComparer.Default.Equals(type.ContainingAssembly, Assembly) && IsEligible(type.OriginalDefinition);

    /// <summary>Runtime type id (hash of the runtime name): many members share a type such as <c>List&lt;int&gt;</c>.</summary>
    public int TypeId(ITypeSymbol type) =>
        _typeIds.GetOrAdd(type, static t => TypeNames.ComputeId(TypeNames.RuntimeName(t)));

    /// <summary>Serialized members of <paramref name="type"/> (whose effective attribute is <paramref name="attr"/>), in order.</summary>
    public IReadOnlyList<ISymbol> Members(INamedTypeSymbol type, NinoTypeOptions attr)
    {
        if (!_members.TryGetValue(type, out var result))
            _members[type] = result = MemberCollector.Collect(type, attr, Symbols);
        return result; // races compute the same list
    }

    /// <summary>Whether values of <paramref name="type"/> are written as their raw bytes.</summary>
    public bool IsRawUnmanaged(ITypeSymbol type)
    {
        if (type is ITypeParameterSymbol || !type.IsUnmanagedType)
            return false;
        if (type.IsNinoType())
            return type is INamedTypeSymbol ninoStruct && IsRawStruct(ninoStruct);
        if (type.SpecialType is SpecialType.System_IntPtr or SpecialType.System_UIntPtr || type is IPointerTypeSymbol)
            return false; // pointer-sized: written as 8 bytes
        if (type is INamedTypeSymbol named)
        {
            if (named.IsTupleType)
                return false;
            var definition = named.OriginalDefinition;
            if (definition.SpecialType == SpecialType.System_Nullable_T)
                return false;
            var name = TypeNames.MetadataName(definition);
            if (name == "System.Collections.Generic.KeyValuePair`2" || name.StartsWith("System.ValueTuple`", System.StringComparison.Ordinal) ||
                name == "System.DateTimeOffset")
                return false;
        }

        return true;
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
        if (_delegatable.TryGetValue(type, out bool known))
            return known;
        bool result = ComputeIsDelegatable(type);
        _delegatable[type] = result;
        return result;
    }

    private bool ComputeIsDelegatable(INamedTypeSymbol type)
    {
        if (!IsOwnEligible(type))
            return false;
        var attr = type.GetNinoTypeOptions();
        if (attr == null || attr.Keyed)
            return false;

        foreach (var member in Members(type, attr))
        {
            if (IsConstructionOnly(member) || !IsDirectlyAccessible(member))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Whether a <c>[NinoType]</c> struct is written as its raw memory: unmanaged, not version tolerant, and every
    /// instance field is serialized (nested <c>[NinoType]</c> structs must qualify too). Such structs are copied as
    /// one block, and arrays/lists of them with a single memory copy.
    /// </summary>
    public bool IsRawStruct(INamedTypeSymbol type)
    {
        if (!type.IsValueType || type.TypeKind != TypeKind.Struct)
            return false;
        if (_rawStructs.TryGetValue(type, out bool known))
            return known;
        bool raw = ComputeIsRawStruct(type);
        _rawStructs[type] = raw;
        return raw;
    }

    private bool ComputeIsRawStruct(INamedTypeSymbol type)
    {
        if (!type.IsUnmanagedType)
            return false;
        // WEAK_VERSION_TOLERANCE only applies to the types of the assembly that defines it.
        if (IsWeak(type))
            return false;
        var attr = type.GetNinoTypeOptions();
        if (attr == null || attr.VersionTolerant || attr.Keyed)
            return false;
        var layout = NinoSymbols.Get(type, Symbols.StructLayout);
        if (layout != null && layout.ConstructorArguments.Length > 0 &&
            layout.ConstructorArguments[0].Value is int kind && kind != 0 /* Sequential */)
            return false;

        var members = new HashSet<ISymbol>(Members(type.OriginalDefinition, attr),
            SymbolEqualityComparer.Default);
        foreach (var field in type.GetMembers().OfType<IFieldSymbol>())
        {
            if (field.IsStatic || field.IsConst)
                continue;
            var member = field.AssociatedSymbol ?? field;
            if (!members.Contains(member.OriginalDefinition))
                return false;
            if (NinoSymbols.Has(member, Symbols.CustomFormatter))
                return false;
            // Every field must itself be raw: Nullable, tuples, nint or DateTimeOffset have their own formats.
            if (!IsRawUnmanaged(field.Type))
                return false;
        }

        return true;
    }
}
