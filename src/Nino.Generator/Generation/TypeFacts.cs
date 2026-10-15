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
        return facts._symbols.SequenceEqual(Symbols(options)) ? facts : new TypeFacts(compilation, options);
    }

    private static string[] Symbols(ParseOptions? options) =>
        options?.PreprocessorSymbolNames.ToArray() ?? System.Array.Empty<string>();

    private TypeFacts(Compilation compilation, ParseOptions? options)
    {
        Assembly = compilation.Assembly;
        _symbols = Symbols(options);
        WeakTolerance = _symbols.Contains(NinoTypeHelper.WeakVersionToleranceSymbol);
        Net8 = _symbols.Contains("NET8_0_OR_GREATER");
        Resolver = new FormatterResolver(this);
    }

    public IAssemblySymbol Assembly { get; }

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

    public static bool HasAttribute(ISymbol symbol, string name) =>
        symbol.GetAttributes().Any(a => a.AttributeClass?.Name == name);

    public static AttributeData? GetAttribute(ISymbol symbol, string name) =>
        symbol.GetAttributes().FirstOrDefault(a => a.AttributeClass?.Name == name);

    /// <summary>Whether this compilation generates a formatter for <paramref name="type"/>.</summary>
    public bool IsGeneratedHere(INamedTypeSymbol type)
    {
        var definition = type.OriginalDefinition;
        return SymbolEqualityComparer.Default.Equals(definition.ContainingAssembly, Assembly) &&
               IsEligible(definition) && definition.IsNinoType();
    }

    /// <summary>Runtime type id (hash of the runtime name): many members share a type such as <c>List&lt;int&gt;</c>.</summary>
    public int TypeId(ITypeSymbol type) =>
        _typeIds.GetOrAdd(type, static t => TypeNames.ComputeId(TypeNames.RuntimeName(t)));

    /// <summary>Serialized members of <paramref name="type"/> (whose effective attribute is <paramref name="attr"/>), in order.</summary>
    public IReadOnlyList<ISymbol> Members(INamedTypeSymbol type, NinoTypeAttribute attr)
    {
        if (!_members.TryGetValue(type, out var result))
            _members[type] = result = MemberCollector.Collect(type, attr);
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
    /// non-sealed <c>[NinoType]</c> class generated in this assembly whose members are all directly readable and
    /// assignable.
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
        if (!SymbolEqualityComparer.Default.Equals(type.ContainingAssembly, Assembly) ||
            !IsEligible(type.OriginalDefinition))
            return false;
        var attr = type.GetNinoTypeAttribute();
        if (attr == null || attr.keyed)
            return false;

        bool Visible(ISymbol? s) => s == null || s.DeclaredAccessibility == Accessibility.Public ||
                                    (s.DeclaredAccessibility is Accessibility.Internal or Accessibility.ProtectedOrInternal &&
                                     SymbolEqualityComparer.Default.Equals(s.ContainingAssembly, Assembly));

        foreach (var member in Members(type, attr))
        {
            switch (member)
            {
                case IFieldSymbol field:
                    if (field.IsReadOnly || !Visible(field))
                        return false;
                    break;
                case IPropertySymbol property:
                    if (property.GetMethod == null || property.SetMethod == null || property.SetMethod.IsInitOnly ||
                        !Visible(property) || !Visible(property.GetMethod) || !Visible(property.SetMethod))
                        return false;
                    break;
                default:
                    return false;
            }
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
        if (!type.IsValueType || !type.IsUnmanagedType || type.TypeKind != TypeKind.Struct)
            return false;
        // WEAK_VERSION_TOLERANCE only applies to the types of the assembly that defines it.
        if (IsWeak(type))
            return false;
        var attr = type.GetNinoTypeAttribute();
        if (attr == null || attr.versionTolerant || attr.keyed)
            return false;
        var layout = GetAttribute(type, "StructLayoutAttribute");
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
            if (HasAttribute(member, "NinoCustomFormatterAttribute"))
                return false;
            // Every field must itself be raw: Nullable, tuples, nint or DateTimeOffset have their own formats.
            if (!IsRawUnmanaged(field.Type))
                return false;
        }

        return true;
    }
}
