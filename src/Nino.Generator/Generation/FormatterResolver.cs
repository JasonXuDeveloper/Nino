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
/// <see cref="Traits"/>: which formatter it is, for the emitters (<see cref="FormatterTraits"/>).
/// </summary>
internal readonly record struct ResolvedFormatter(FormatterRef Code, bool Registered, string Encoding,
    EquatableArray<FactoryRegistration> Factories = default, bool OpensBodies = true,
    INamedTypeSymbol? Body = null, ImmutableArray<ITypeSymbol> Elements = default,
    FormatterTraits Traits = FormatterTraits.None);

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

    public static FormatterRef Cached(ITypeSymbol type) =>
        FormatterRef.Generic(Core + "CachedFormatter", new[] { FormatterRef.Of(type) });

    private static ResolvedFormatter CachedFormatter(ITypeSymbol type) =>
        new(Cached(type), false, WireEncoding.Type(type), Traits: FormatterTraitsOf.Of("CachedFormatter"));

    public static FormatterRef GeneratedFormatter(INamedTypeSymbol type) =>
        FormatterRef.Generic(TypeNames.Gen + TypeNames.FormatterName(type.OriginalDefinition),
            TypeNames.AllTypeArguments(type).Select(FormatterRef.Of), generated: true);

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
    public ResolvedFormatter? ResolveCollection(INamedTypeSymbol type) => FirstMatch(_rules.Open, type, false);

    private ResolvedFormatter? ResolveCore(ITypeSymbol type, bool utf8)
    {
        switch (type)
        {
            case ITypeParameterSymbol:
                return CachedFormatter(type);
            case IArrayTypeSymbol array:
                return FirstMatch(_rules.ForArray(array.IsSZArray ? 1 : -array.Rank), type, utf8);
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
            {
                string name = utf8 ? "Utf8StringFormatter" : "StringFormatter";
                return new ResolvedFormatter(FormatterRef.Named(Core + name), true,
                    utf8 ? WireEncoding.Utf8 : WireEncoding.String,
                    OpensBodies: false, Elements: ImmutableArray<ITypeSymbol>.Empty,
                    Traits: FormatterTraitsOf.Of(name));
            }
            case SpecialType.System_Object:
                return new ResolvedFormatter(FormatterRef.Named(Core + "ObjectFormatter"), true, WireEncoding.Object,
                    Traits: FormatterTraitsOf.Of("ObjectFormatter"));
        }

        if (type.IsTupleType && type.TupleUnderlyingType is { } underlying)
            type = underlying;
        if (TypeNames.IsValueTuple(type.OriginalDefinition) && type.Arity == 8)
            return LongTuple(type, utf8);

        // Registrations of this very definition first (a library can override how a raw struct is written).
        if (FirstMatch(_rules.ForDefinition(type.OriginalDefinition), type, utf8) is { } registered)
            return registered;
        if (_raw.IsRawUnmanaged(type))
            return new ResolvedFormatter(
                FormatterRef.Generic(Core + "UnmanagedFormatter", new[] { FormatterRef.Of(type) }), true,
                RawLayout.Describe(type), OpensBodies: false, Elements: ImmutableArray<ITypeSymbol>.Empty,
                Traits: FormatterTraitsOf.Of("UnmanagedFormatter"));

        // Checked before the open registrations: walking the base types and interfaces of a type is the most
        // expensive part of a model build.
        if (type.IsNinoType())
            return _visibility.IsGeneratedHere(type)
                ? new ResolvedFormatter(GeneratedFormatter(type), false, WireEncoding.Type(type), Body: type,
                    Traits: FormatterTraits.Generated)
                : CachedFormatter(type);

        return FirstMatch(_rules.Open, type, utf8);
    }

    /// <summary>
    /// <c>ValueTuple&lt;T1, ..., T7, TRest&gt;</c>, a tuple of eight items or more (<c>TRest</c> holds the items after
    /// the seventh): the generated <see cref="Emit.RegistryEmitter.LongTupleFormatter"/>, which writes every item in
    /// order, as Nino.Core's formatters of shorter tuples do.
    /// </summary>
    private ResolvedFormatter? LongTuple(INamedTypeSymbol type, bool utf8)
    {
        var items = type.TypeArguments;
        var formatters = new FormatterRef[items.Length];
        var encodings = new List<string>(items.Length);
        var factories = new List<FactoryRegistration>();
        bool opensBodies = false;
        for (int i = 0; i < items.Length; i++)
        {
            if (Resolve(items[i], utf8) is not { } item)
                return null;
            formatters[i] = item.Code;
            factories.AddRange(item.Factories);
            opensBodies |= item.OpensBodies;
            // The rest is a tuple of the remaining items, written as they are: one tuple of all of them.
            encodings.Add(i == items.Length - 1 && item.Encoding.StartsWith("tuple(", System.StringComparison.Ordinal)
                ? item.Encoding.Substring(6, item.Encoding.Length - 7)
                : item.Encoding);
        }

        return new ResolvedFormatter(FormatterRef.Generic(Emit.RegistryEmitter.LongTupleFormatter,
                items.Select(FormatterRef.Of).Concat(formatters)), true,
            $"tuple({string.Join(",", encodings)})",
            factories.Count == 0
                ? EquatableArray<FactoryRegistration>.Empty
                : new EquatableArray<FactoryRegistration>(factories.Distinct()),
            opensBodies, Elements: items);
    }

    /// <summary>The formatter of the first rule that binds.</summary>
    private ResolvedFormatter? FirstMatch(IReadOnlyList<FormatterRule> rules, ITypeSymbol type, bool utf8)
    {
        foreach (var rule in rules)
        {
            if (Bind(rule, type, utf8) is { } bound)
                return bound;
        }

        return null;
    }

    /// <summary>
    /// Binds the formatter's type parameters for <paramref name="target"/> (see <c>NinoFormatterAttribute</c>), with
    /// the binding the runtime uses too (<see cref="NinoFormatterBinding"/>).
    /// </summary>
    private ResolvedFormatter? Bind(FormatterRule rule, ITypeSymbol target, bool utf8)
    {
        var parameters = new Parameters(this, rule.Formatter.TypeParameters, utf8);
        if (!NinoFormatterBinding.Bind<ITypeSymbol, FormatterRef, SymbolTypeSystem, Parameters>(
                new SymbolTypeSystem(_compilation), parameters, rule.Pattern, target, out var types,
                out var formatters))
            return null;
        var code = Code(rule, types, formatters, parameters);
        var factories = parameters.Created.Count == 0
            ? EquatableArray<FactoryRegistration>.Empty
            : new EquatableArray<FactoryRegistration>(parameters.Created.Distinct());
        // Nino.Core's formatters open no class body themselves (object is ObjectFormatter's, resolved above), only
        // through their element formatters; any other library's formatter may.
        if (!FormatterTraitsOf.IsCore(rule.Formatter))
            return new ResolvedFormatter(code, true, WireEncoding.Formatter(code.ToString()), factories);
        string encoding = WireEncoding.OfCore(rule.Formatter.Name, types,
            formatters.Select(f => f == null ? null : parameters.Encodings[f]).ToArray(), _compilation) ??
                          WireEncoding.Formatter(code.ToString());
        var traits = FormatterTraitsOf.Of(rule.Formatter.Name,
            formatters.Select(f => f == null ? FormatterTraits.None : parameters.Traits[f]).ToArray());
        return new ResolvedFormatter(code, true, encoding, factories, parameters.ElementsOpenBodies[0],
            Elements: parameters.ElementTypes.ToImmutableArray(), Traits: traits);
    }

    /// <summary>The bound formatter as C# source; adds the creators of its <c>[NinoCreated]</c> types.</summary>
    private FormatterRef Code(FormatterRule rule, ITypeSymbol[] types, FormatterRef[] formatters,
        Parameters parameters)
    {
        if (types.Length == 0)
            return FormatterRef.Named(rule.Prefix);
        var args = new FormatterRef[types.Length];
        for (int i = 0; i < types.Length; i++)
        {
            if (types[i] is { } bound)
            {
                args[i] = FormatterRef.Of(bound);
                if (IsCreated(rule.Formatter.TypeParameters[i]) && Factory(bound) is { } factory)
                    parameters.Created.Add(factory);
            }
            else
            {
                args[i] = formatters[i];
            }
        }

        return FormatterRef.Generic(rule.Prefix, args);
    }

    /// <summary>A formatter's type parameters, for <see cref="NinoFormatterBinding"/>.</summary>
    private readonly struct Parameters : INinoFormatterParameters<ITypeSymbol, FormatterRef>
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
        public Dictionary<FormatterRef, string> Encodings { get; } = new();

        /// <summary>The traits of the element formatters bound so far, by formatter.</summary>
        public Dictionary<FormatterRef, FormatterTraits> Traits { get; } = new();

        public int Count => _parameters.Length;

        public IEnumerable<ITypeSymbol> Constraints(int index) => _parameters[index].ConstraintTypes;

        // Null when the parameter is no element formatter (the shared interface is nullable-oblivious).
        public ITypeSymbol FormatterElement(int index) =>
            FormatterRules.FormatterElement(_parameters[index], _resolver._symbols)!;

        public bool ResolveElement(ITypeSymbol element, out FormatterRef formatter)
        {
            if (_resolver.Resolve(element, _utf8) is not { } resolved)
            {
                formatter = null!;
                return false;
            }

            formatter = resolved.Code;
            Encodings[formatter] = resolved.Encoding;
            Traits[formatter] = resolved.Traits;
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
