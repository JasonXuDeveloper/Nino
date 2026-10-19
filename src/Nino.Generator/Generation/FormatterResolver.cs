using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Nino.Generator.Common;

namespace Nino.Generator.Generation;

/// <summary>
/// A formatter as C# source. <see cref="Registered"/>: a struct formatter of the closed type that is registered as
/// its global formatter (so AOT platforms have the instantiation); false for generated, cached and custom ones.
/// <see cref="Factories"/>: the creators of the types bound to <c>[NinoCreated]</c> parameters of the formatter and of
/// its element formatters, registered with <c>NinoActivator&lt;T&gt;</c>.
/// <see cref="OpensBodies"/>: whether the formatter may write or read a class body (<c>NinoObject.WriteBody</c>), so
/// a body holding it nests; false only for strings, raw values and Nino.Core's formatters over such elements.
/// What it may open, for the nesting bound (<see cref="TypeFacts.DepthBound"/>): <see cref="Body"/>, the
/// <c>[NinoType]</c> whose generated formatter it is; <see cref="Elements"/>, the element types a Nino.Core formatter
/// writes through their own formatters (default when unknown: another library's formatter, <c>object</c>, a cached
/// formatter). Neither is part of a model.
/// <see cref="Encoding"/>: the bytes it writes, as the published layout shows them (<see cref="WireEncoding"/>).
/// </summary>
internal readonly record struct ResolvedFormatter(string Code, bool Registered, string Encoding,
    EquatableArray<FactoryRegistration> Factories = default, bool OpensBodies = true,
    INamedTypeSymbol? Body = null, ImmutableArray<ITypeSymbol> Elements = default);

/// <summary>
/// Chooses the formatter (as C# source) for a member type. Built in: strings, <c>object</c>, raw unmanaged values,
/// <c>[NinoType]</c>s. Everything else comes from the <c>[assembly: NinoFormatter]</c> registrations
/// (<see cref="FormatterRules"/>): Nino.Core declares its BCL formatters that way, and so can any library. The
/// runtime resolver (<c>Nino.Core.NinoFormatterResolver</c>) applies the same rules, so generated code and
/// runtime-resolved formatters write the same bytes.
/// One instance per <see cref="TypeFacts"/> (per compilation): results are memoized, since the same member types
/// (<c>string</c>, <c>List&lt;int&gt;</c>...) recur across all types. Names ignore nullable annotations, and so does
/// the memo.
/// </summary>
internal sealed class FormatterResolver
{
    private const string Core = TypeNames.Core;
    private readonly Compilation _compilation;
    private readonly NinoSymbols _symbols;
    private readonly RawRules _raw;
    private readonly Visibility _visibility;
    private readonly FormatterRules _rules;
    private readonly ConcurrentDictionary<ITypeSymbol, ResolvedFormatter?> _plain = new(SymbolEqualityComparer.Default);
    private readonly ConcurrentDictionary<ITypeSymbol, ResolvedFormatter?> _utf8 = new(SymbolEqualityComparer.Default);

    [System.ThreadStatic] private static HashSet<ITypeSymbol>? t_resolving;

    public FormatterResolver(Compilation compilation, NinoSymbols symbols, RawRules raw, Visibility visibility)
    {
        _compilation = compilation;
        _symbols = symbols;
        _raw = raw;
        _visibility = visibility;
        _rules = new FormatterRules(compilation, symbols);
    }

    public static string Cached(ITypeSymbol type) => $"{Core}CachedFormatter<{TypeNames.Global(type)}>";

    private static ResolvedFormatter CachedFormatter(ITypeSymbol type) =>
        new(Cached(type), false, WireEncoding.Type(type));

    public static string GeneratedFormatter(INamedTypeSymbol type)
    {
        var name = TypeNames.Gen + TypeNames.FormatterName(type.OriginalDefinition);
        var args = TypeNames.AllTypeArguments(type);
        return args.Count == 0 ? name : $"{name}<{string.Join(", ", args.Select(TypeNames.Global))}>";
    }

    /// <summary>
    /// Returns the formatter for <paramref name="type"/>, or null if Nino cannot serialize it at all.
    /// Type parameters (and <c>[NinoType]</c>s generated elsewhere) are resolved at runtime through
    /// <c>CachedFormatter&lt;T&gt;</c>.
    /// </summary>
    public ResolvedFormatter? Resolve(ITypeSymbol type, bool utf8)
    {
        type = type.WithNullableAnnotation(NullableAnnotation.NotAnnotated);
        // Where the runtime has no numeric IntPtr (netstandard, Unity), nint and nuint are symbols of their own:
        // resolve them as the IntPtr and UIntPtr they compile to.
        if (type is INamedTypeSymbol { IsNativeIntegerType: true })
            type = _compilation.GetSpecialType(type.SpecialType);
        var memo = utf8 ? _utf8 : _plain;
        if (memo.TryGetValue(type, out var formatter))
            return formatter;
        // A collection of itself (class Tree : List<Tree>) reaches its own type again: resolve that at runtime.
        var resolving = t_resolving ??= new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default);
        if (!resolving.Add(type))
            return CachedFormatter(type);
        try
        {
            formatter = ResolveCore(type, utf8);
        }
        finally
        {
            resolving.Remove(type);
        }

        memo[type] = formatter;
        return formatter;
    }

    /// <summary>
    /// The collection formatter of a <c>[NinoType]</c> class that is also a collection (derived from
    /// <c>List&lt;T&gt;</c>, implementing <c>ICollection&lt;T&gt;</c>...), used for its elements; null if none.
    /// </summary>
    public ResolvedFormatter? ResolveCollection(INamedTypeSymbol type)
    {
        var factories = new List<FactoryRegistration>();
        return Registered(FirstMatch(_rules.Open, type, false, factories, out bool opens, out var elements,
            out string encoding), factories, opens, elements, encoding);
    }

    private ResolvedFormatter? ResolveCore(ITypeSymbol type, bool utf8)
    {
        switch (type)
        {
            case ITypeParameterSymbol:
                return CachedFormatter(type);
            case IArrayTypeSymbol array:
            {
                var factories = new List<FactoryRegistration>();
                return Registered(FirstMatch(_rules.ForArray(array.IsSZArray ? 1 : -array.Rank), type, utf8, factories,
                    out bool opens, out var elements, out string encoding), factories, opens, elements, encoding);
            }
            case INamedTypeSymbol named:
                return ResolveNamed(named, utf8);
            default:
                return null; // pointers
        }
    }

    private ResolvedFormatter? ResolveNamed(INamedTypeSymbol type, bool utf8)
    {
        if (type.IsRefLikeType || type.TypeKind == TypeKind.Delegate)
            return null;
        switch (type.SpecialType)
        {
            case SpecialType.System_String:
                return new ResolvedFormatter(utf8 ? $"{Core}Utf8StringFormatter" : $"{Core}StringFormatter", true,
                    utf8 ? WireEncoding.Utf8 : WireEncoding.String, OpensBodies: false,
                    Elements: ImmutableArray<ITypeSymbol>.Empty);
            case SpecialType.System_Object:
                return new ResolvedFormatter($"{Core}ObjectFormatter", true, WireEncoding.Object);
        }

        if (type.IsTupleType && type.TupleUnderlyingType is { } underlying)
            type = underlying;

        // Registrations of this very definition first (a library can override how a raw struct is written).
        var definition = type.OriginalDefinition;
        var factories = new List<FactoryRegistration>();
        if (FirstMatch(_rules.ForDefinition(definition), type, utf8, factories, out bool opens, out var elements,
                out string encoding) is { } registered)
            return Registered(registered, factories, opens, elements, encoding);
        if (_raw.IsRawUnmanaged(type))
            return new ResolvedFormatter($"{Core}UnmanagedFormatter<{TypeNames.Global(type)}>", true,
                RawLayout.Describe(type), OpensBodies: false, Elements: ImmutableArray<ITypeSymbol>.Empty);

        // Checked before the open registrations: walking the base types and interfaces of a type is the most
        // expensive part of a model build.
        if (type.IsNinoType())
            return _visibility.IsGeneratedHere(type)
                ? new ResolvedFormatter(GeneratedFormatter(type), false, WireEncoding.Type(type), Body: type)
                : CachedFormatter(type);

        return Registered(FirstMatch(_rules.Open, type, utf8, factories, out opens, out elements, out encoding),
            factories, opens, elements, encoding);
    }

    private static ResolvedFormatter? Registered(string? code, List<FactoryRegistration>? factories, bool opens,
        ImmutableArray<ITypeSymbol> elements, string encoding) =>
        code == null
            ? null
            : new ResolvedFormatter(code, true, encoding, factories is { Count: > 0 }
                ? new EquatableArray<FactoryRegistration>(factories.Distinct())
                : EquatableArray<FactoryRegistration>.Empty, opens, Elements: elements);

    /// <summary>
    /// The first rule that binds; adds the creators it needs to <paramref name="factories"/>. <paramref name="opens"/>:
    /// whether the bound formatter may open class bodies (see <see cref="ResolvedFormatter.OpensBodies"/>);
    /// <paramref name="elements"/>: see <see cref="ResolvedFormatter.Elements"/>; <paramref name="encoding"/>: see
    /// <see cref="ResolvedFormatter.Encoding"/>.
    /// </summary>
    private string? FirstMatch(IReadOnlyList<FormatterRule> rules, ITypeSymbol type, bool utf8,
        List<FactoryRegistration> factories, out bool opens, out ImmutableArray<ITypeSymbol> elements,
        out string encoding)
    {
        foreach (var rule in rules)
        {
            if (Bind(rule, type, utf8, factories, out opens, out elements, out encoding) is { } code)
                return code;
        }

        opens = true;
        elements = default;
        encoding = "";
        return null;
    }

    /// <summary>
    /// Binds the formatter's type parameters for <paramref name="target"/> (see <c>NinoFormatterAttribute</c>), with
    /// the binding the runtime uses too (<see cref="NinoFormatterBinding"/>).
    /// </summary>
    private string? Bind(FormatterRule rule, ITypeSymbol target, bool utf8, List<FactoryRegistration> factories,
        out bool opens, out ImmutableArray<ITypeSymbol> elements, out string encoding)
    {
        var parameters = new Parameters(this, rule.Formatter.TypeParameters, utf8);
        opens = true;
        elements = default;
        encoding = "";
        if (!NinoFormatterBinding.Bind<ITypeSymbol, string, SymbolTypeSystem, Parameters>(
                new SymbolTypeSystem(_compilation), parameters, rule.Pattern, target, out var types,
                out var formatters))
            return null;
        string code = Code(rule, types, formatters, parameters);
        factories.AddRange(parameters.Created);
        // Nino.Core's formatters open no class body themselves (object is ObjectFormatter's, resolved above), only
        // through their element formatters; any other library's formatter may.
        bool core = rule.Formatter.ContainingAssembly?.Name == NinoNames.CoreAssembly;
        opens = !core || parameters.ElementsOpenBodies[0];
        if (core)
            elements = parameters.ElementTypes.ToImmutableArray();
        encoding = (core ? WireEncoding.OfCore(rule.Formatter.Name, types, formatters.Select(f =>
            f == null ? null : parameters.Encodings[f]).ToArray(), _compilation) : null) ?? WireEncoding.Formatter(code);
        return code;
    }

    /// <summary>The bound formatter as C# source; adds the creators of its <c>[NinoCreated]</c> types.</summary>
    private string Code(FormatterRule rule, ITypeSymbol[] types, string[] formatters, Parameters parameters)
    {
        if (types.Length == 0)
            return rule.Prefix;
        var args = new string[types.Length];
        for (int i = 0; i < types.Length; i++)
        {
            if (types[i] is { } bound)
            {
                args[i] = TypeNames.Global(bound);
                if (IsCreated(rule.Formatter.TypeParameters[i]) && Factory(bound) is { } factory)
                    parameters.Created.Add(factory);
            }
            else
            {
                args[i] = formatters[i];
            }
        }

        return $"{rule.Prefix}<{string.Join(", ", args)}>";
    }

    /// <summary>A formatter's type parameters, for <see cref="NinoFormatterBinding"/>.</summary>
    private readonly struct Parameters : INinoFormatterParameters<ITypeSymbol, string>
    {
        private readonly FormatterResolver _resolver;
        private readonly ImmutableArray<ITypeParameterSymbol> _parameters;
        private readonly bool _utf8;

        public Parameters(FormatterResolver resolver, ImmutableArray<ITypeParameterSymbol> parameters, bool utf8)
        {
            _resolver = resolver;
            _parameters = parameters;
            _utf8 = utf8;
        }

        /// <summary>Creators the bound formatter needs (its element formatters' and its own [NinoCreated] types').</summary>
        public List<FactoryRegistration> Created { get; } = new();

        /// <summary>Whether an element formatter bound so far may open class bodies (one element: a mutable flag).</summary>
        public bool[] ElementsOpenBodies { get; } = new bool[1];

        /// <summary>The element types bound so far, written through their formatters.</summary>
        public List<ITypeSymbol> ElementTypes { get; } = new();

        /// <summary>The encodings of the element formatters bound so far, by formatter.</summary>
        public Dictionary<string, string> Encodings { get; } = new();

        public int Count => _parameters.Length;

        public IEnumerable<ITypeSymbol> Constraints(int index) => _parameters[index].ConstraintTypes;

        // Null when the parameter is no element formatter (the shared interface is nullable-oblivious).
        public ITypeSymbol FormatterElement(int index) =>
            FormatterRules.FormatterElement(_parameters[index], _resolver._symbols)!;

        public bool ResolveElement(ITypeSymbol element, out string formatter)
        {
            if (_resolver.Resolve(element, _utf8) is not { } resolved)
            {
                formatter = null!;
                return false;
            }

            formatter = resolved.Code;
            Encodings[formatter] = resolved.Encoding;
            Created.AddRange(resolved.Factories);
            ElementTypes.Add(element);
            ElementsOpenBodies[0] |= resolved.OpensBodies;
            return true;
        }

        public bool Satisfies(int index, ITypeSymbol bound, ITypeSymbol[] types) =>
            _resolver.Satisfies(_parameters[index], bound, types);
    }

    /// <summary>
    /// Whether <paramref name="parameter"/> is marked <c>[NinoCreated]</c>: the formatter creates its instances through
    /// <c>NinoActivator&lt;T&gt;</c>.
    /// </summary>
    private bool IsCreated(ITypeParameterSymbol parameter) => NinoSymbols.Has(parameter, _symbols.Created);

    /// <summary>
    /// How generated code creates <paramref name="type"/> for <c>NinoActivator&lt;T&gt;</c>, or null for a type it cannot
    /// name (open types): <c>T.Factory()</c> for a public static <c>[NinoFactory]</c> method declared on the
    /// type and callable without arguments, else <c>new T()</c>, which fills in optional constructor parameters.
    /// </summary>
    private FactoryRegistration? Factory(ITypeSymbol type)
    {
        if (type is not INamedTypeSymbol named || TypeNames.ContainsTypeParameter(named))
            return null;
        // A plain List<T> is created directly, with its capacity (ListAccess.Create in Nino.Core).
        _list ??= _compilation.GetTypeByMetadataName("System.Collections.Generic.List`1");
        if (SymbolEqualityComparer.Default.Equals(named.OriginalDefinition, _list))
            return null;
        string name = TypeNames.Global(named);
        string creator = _symbols.FindFactory(named) is { } factory
            ? $"static () => {name}.{factory.Name}()"
            : $"static () => new {name}()";
        return new FactoryRegistration(name, creator, TypeNames.ComputeId(TypeNames.RuntimeName(named)));
    }

    private INamedTypeSymbol? _list;

    /// <summary>
    /// What <c>[NinoCreated]</c> requires (see <c>NinoCreatedAttribute</c>): a non-abstract class with a public static
    /// <c>[NinoFactory]</c> factory or a public constructor callable without arguments.
    /// </summary>
    private bool IsCreatable(ITypeSymbol type) =>
        type is INamedTypeSymbol { TypeKind: TypeKind.Class, IsAbstract: false, IsStatic: false } named &&
        (_symbols.FindFactory(named) != null || named.InstanceConstructors.Any(c =>
            c.DeclaredAccessibility == Accessibility.Public && NinoSymbols.CallableWithoutArguments(c) &&
            (NinoSymbols.SetsRequiredMembers(c) || !NinoSymbols.HasRequiredMembers(named))));

    private bool Satisfies(ITypeParameterSymbol parameter, ITypeSymbol type, ITypeSymbol[] types)
    {
        if (parameter.HasReferenceTypeConstraint && !type.IsReferenceType)
            return false;
        if (parameter.HasValueTypeConstraint &&
            (!type.IsValueType || type.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T))
            return false;
        // `unmanaged` is C#'s; [NinoRaw] means "copied as raw memory", which is stricter.
        if (parameter.HasUnmanagedTypeConstraint && !type.IsUnmanagedType)
            return false;
        if (NinoSymbols.Has(parameter, _symbols.Raw) && !_raw.IsRawUnmanaged(type))
            return false;
        if (parameter.HasConstructorConstraint && !HasPublicDefaultConstructor(type))
            return false;
        if (IsCreated(parameter) && !IsCreatable(type))
            return false;
        foreach (var constraint in parameter.ConstraintTypes)
        {
            if (!NinoFormatterBinding.UnifyWithSupertype<ITypeSymbol, SymbolTypeSystem>(
                    new SymbolTypeSystem(_compilation), constraint, type, types))
                return false;
        }

        return true;
    }

    private static bool HasPublicDefaultConstructor(ITypeSymbol type) => type switch
    {
        { IsValueType: true } => true,
        INamedTypeSymbol { TypeKind: TypeKind.Class, IsAbstract: false } named => named.InstanceConstructors.Any(c =>
            c.Parameters.Length == 0 && c.DeclaredAccessibility == Accessibility.Public),
        _ => false,
    };
}
