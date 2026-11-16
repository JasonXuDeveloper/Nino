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
    FormatterTraits Traits = FormatterTraits.None, string? Slot = null);

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

    /// <summary>The nesting formatters named so far (<see cref="GeneratedNestingFormatter"/>), by fully qualified name.</summary>
    private readonly ConcurrentDictionary<string, (ITypeSymbol Type, bool Utf8)> _nesting = new();

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

    /// <summary>
    /// The generated formatter of <paramref name="type"/>: for a generic type, its type arguments and then the
    /// formatter of each (<see cref="TypeNames.FormatterParameter"/>), resolved here as for a member of that type, or
    /// <c>CachedFormatter&lt;T&gt;</c> where there is none (the runtime reports it when a value is written).
    /// </summary>
    public FormatterRef GeneratedFormatter(INamedTypeSymbol type)
    {
        var arguments = TypeNames.AllTypeArguments(type);
        return FormatterRef.Generic(TypeNames.Gen + TypeNames.FormatterName(type.OriginalDefinition),
            arguments.Select(FormatterRef.Of)
                .Concat(arguments.Select(a => Resolve(a, false)?.Code ?? Cached(a))), generated: true);
    }

    /// <summary>
    /// Returns the formatter for <paramref name="type"/>, or null if Nino cannot serialize it at all.
    /// A type's type parameters are the formatter parameters its generated formatter declares
    /// (<see cref="TypeNames.FormatterParameter"/>); a method's (and <c>[NinoType]</c>s generated elsewhere) are
    /// resolved at runtime through <c>CachedFormatter&lt;T&gt;</c>.
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
        // A collection of itself (class Tree : List<Tree>) reaches its own type again: its nesting formatter.
        var resolving = t_resolving ??= new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default);
        if (!resolving.Add(type))
            return GeneratedNestingFormatter(type, utf8);
        try
        {
            // Vector<T>, alone or inside: no formatter (NINO029 names it where it is used).
            formatter = _raw.HostSizedVectorIn(type) == null ? ResolveCore(type, utf8) : null;
        }
        finally
        {
            resolving.Remove(type);
        }

        memo[type] = formatter;
        return formatter;
    }

    /// <summary>
    /// The formatter of <paramref name="type"/> where its own formatter reaches it again (<c>class Tree :
    /// List&lt;Tree&gt;</c>, <c>class Map : Dictionary&lt;string, Map&gt;</c>, no <c>[NinoType]</c> body in between): a
    /// struct generated for it (<see cref="NestingFormattersNamedIn"/>) that counts the level (<c>EnterStruct</c>, as Nino.Core's
    /// <c>NestingFormatter</c> does for the runtime's formatters) and calls that formatter, which names the struct
    /// again for the next level. A type argument cannot name the formatter it is part of, and a named struct can:
    /// every level is a direct call on every runtime, where <c>CachedFormatter&lt;T&gt;</c> made one through the
    /// shared generic code's dictionary and a virtual one, and kept the element checks of the collection's formatter
    /// from folding. <c>CachedFormatter&lt;T&gt;</c> stays for types generated code cannot name in a struct of its own
    /// (open, inaccessible) and for value types, which the runtime does not count either.
    /// </summary>
    private ResolvedFormatter GeneratedNestingFormatter(ITypeSymbol type, bool utf8)
    {
        if (type is not INamedTypeSymbol { TypeKind: TypeKind.Class } named || TypeNames.ContainsTypeParameter(type) ||
            !_visibility.CanName(type))
            return CachedFormatter(type);
        string global = TypeNames.Global(named);
        string qualifiedName = TypeNames.Gen +
            TypeNames.NestingFormatterName(named, TypeNames.ComputeId(utf8 ? global + " utf8" : global));
        _nesting.TryAdd(qualifiedName, (type, utf8));
        return CachedFormatter(type) with
        {
            Code = FormatterRef.Named(qualifiedName), Traits = FormatterTraits.None,
        };
    }

    public bool HasNestingFormatters => !_nesting.IsEmpty;

    /// <summary>
    /// The nesting formatters (<see cref="GeneratedNestingFormatter"/>) that <paramref name="code"/> (generated code naming
    /// formatters) names, with the ones their formatters name in turn, sorted by name: what the assembly declares
    /// for it.
    /// </summary>
    public EquatableArray<NestingModel> NestingFormattersNamedIn(IEnumerable<string> code)
    {
        if (!HasNestingFormatters)
            return EquatableArray<NestingModel>.Empty;
        Stack<string>? pending = null;
        foreach (string text in code)
        {
            if (NamesNestingFormatter(text))
                (pending ??= new Stack<string>()).Push(text);
        }
        if (pending == null)
            return EquatableArray<NestingModel>.Empty;

        SortedDictionary<string, NestingModel>? found = null;
        while (pending.Count != 0)
        {
            string text = pending.Pop();
            foreach (var entry in _nesting)
            {
                if (found?.ContainsKey(entry.Key) == true ||
                    text.IndexOf(entry.Key, System.StringComparison.Ordinal) < 0)
                    continue;
                // Null when no rule binds the type after all (its elements did): written as its formatter would be.
                string body = (Resolve(entry.Value.Type, entry.Value.Utf8)?.Code ?? Cached(entry.Value.Type)).ToString();
                string name = entry.Key.Substring(TypeNames.Gen.Length);
                (found ??= new SortedDictionary<string, NestingModel>(System.StringComparer.Ordinal))
                    .Add(entry.Key, new NestingModel(name, TypeNames.Global(entry.Value.Type), body));
                if (NamesNestingFormatter(body))
                    pending.Push(body);
            }
        }

        return found == null ? EquatableArray<NestingModel>.Empty : new EquatableArray<NestingModel>(found.Values);
    }

    private static bool NamesNestingFormatter(string text) =>
        text.IndexOf(TypeNames.NestingFormatterSuffix, System.StringComparison.Ordinal) >= 0;

    /// <summary>
    /// The collection formatter of a <c>[NinoType]</c> class that is also a collection (derived from
    /// <c>List&lt;T&gt;</c>, implementing <c>ICollection&lt;T&gt;</c>...), used for its elements; null if none.
    /// </summary>
    public ResolvedFormatter? ResolveCollection(INamedTypeSymbol type) => FirstMatch(_rules.Open, type, false);

    private ResolvedFormatter? ResolveCore(ITypeSymbol type, bool utf8)
    {
        switch (type)
        {
            // The formatter the generated formatter in scope takes for it; a method's type parameter at run time.
            case ITypeParameterSymbol p:
                return TypeNames.HasFormatterParameter(p)
                    ? CachedFormatter(type) with
                    {
                        Code = new FormatterRef(TypeNames.FormatterParameter(p.Name),
                            EquatableArray<FormatterRef>.Empty, false, true),
                    }
                    : CachedFormatter(type);
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
        var factories = parameters.Created is not { } created
            ? EquatableArray<FactoryRegistration>.Empty
            : new EquatableArray<FactoryRegistration>(created.Distinct());
        // Nino.Core's formatters open no class body themselves (object is ObjectFormatter's, resolved above), only
        // through their element formatters; any other library's formatter may.
        if (!FormatterTraitsOf.IsCore(rule.Formatter))
            return new ResolvedFormatter(code, true, WireEncoding.Formatter(code.ToString()), factories);
        string encoding = WireEncoding.OfCore(rule.Formatter.Name, types,
            ElementEncodings(formatters, parameters.Encodings, parameters.Slots,
                WireEncoding.SequenceElement(rule.Formatter.Name)), _compilation) ??
                          WireEncoding.Formatter(code.ToString());
        int count = formatters.Length;
        var traits = FormatterTraitsOf.Of(rule.Formatter.Name, count,
            count > 0 && formatters[count - 1] is { } last ? parameters.Traits[last] : FormatterTraits.None,
            count > 1 && formatters[count - 2] is { } previous ? parameters.Traits[previous] : FormatterTraits.None);
        return new ResolvedFormatter(code, true, encoding, factories, parameters.ElementsOpenBodies,
            Elements: parameters.ElementTypes.ToImmutableArray(), Traits: traits,
            Slot: WireEncoding.SlotOf(rule.Formatter.Name, types, _compilation));
    }

    /// <summary>
    /// The encodings of the element formatters, in order; the one at <paramref name="sequenceElement"/> (the element
    /// of a sequence, -1 for none) as its slot where it has one (<see cref="WireEncoding.SlotOf"/>).
    /// </summary>
    private static string[] ElementEncodings(FormatterRef[] formatters, Dictionary<FormatterRef, string> encodings,
        Dictionary<FormatterRef, string> slots, int sequenceElement)
    {
        int count = 0;
        foreach (var formatter in formatters)
            if (formatter != null)
                count++;
        if (count == 0)
            return System.Array.Empty<string>();

        var elements = new string[count];
        int index = 0;
        foreach (var formatter in formatters)
            if (formatter != null)
            {
                elements[index] = index == sequenceElement && slots.TryGetValue(formatter, out var slot)
                    ? slot
                    : encodings[formatter];
                index++;
            }

        return elements;
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
                    parameters.AddFactory(factory);
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

        // The binding engine copies Parameters. These two mutable results are shared across those copies.
        private readonly BindingState _state = new();

        private sealed class BindingState
        {
            public List<FactoryRegistration>? Created;
            public bool OpensBodies;
        }

        /// <summary>Creators the bound formatter needs (its element formatters' and its own [NinoCreated] types').</summary>
        public List<FactoryRegistration>? Created => _state.Created;

        public void AddFactory(FactoryRegistration factory) => (_state.Created ??= new()).Add(factory);

        /// <summary>Whether an element formatter bound so far may open class bodies.</summary>
        public bool ElementsOpenBodies => _state.OpensBodies;

        /// <summary>The element types bound so far, written through their formatters.</summary>
        public List<ITypeSymbol> ElementTypes { get; } = new();

        /// <summary>The encodings of the element formatters bound so far, by formatter.</summary>
        public Dictionary<FormatterRef, string> Encodings { get; } = new();

        /// <summary>The slot encodings of the element formatters bound so far that have one, by formatter.</summary>
        public Dictionary<FormatterRef, string> Slots { get; } = new();

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
            if (resolved.Slot != null)
                Slots[formatter] = resolved.Slot;
            Traits[formatter] = resolved.Traits;
            if (resolved.Factories.Length != 0)
                (_state.Created ??= new()).AddRange(resolved.Factories);
            ElementTypes.Add(element);
            _state.OpensBodies |= resolved.OpensBodies;
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
        if (!NinoSymbols.MeetsCSharpConstraints(parameter, type))
            return false;
        // `unmanaged` is C#'s; [NinoRaw] means "copied as raw memory", which is stricter.
        if (NinoSymbols.Has(parameter, _symbols.Raw) && !_raw.IsRawUnmanaged(type))
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
}
