using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Nino.Generator.Generation.Emit;

namespace Nino.Generator.Generation;

/// <summary>
/// A formatter as C# source. <see cref="Registered"/>: a struct formatter of the closed type that is registered as
/// its global formatter (so AOT platforms have the instantiation); false for generated, cached and custom ones.
/// </summary>
internal readonly record struct ResolvedFormatter(string Code, bool Registered);

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
    private readonly TypeFacts _facts;
    private readonly FormatterRules _rules;
    private readonly IReadOnlyList<HelperSources.Helper> _helpers;
    private readonly ConcurrentDictionary<ITypeSymbol, ResolvedFormatter?> _plain = new(SymbolEqualityComparer.Default);
    private readonly ConcurrentDictionary<ITypeSymbol, ResolvedFormatter?> _utf8 = new(SymbolEqualityComparer.Default);

    [System.ThreadStatic] private static HashSet<ITypeSymbol>? t_resolving;

    public FormatterResolver(TypeFacts facts, Compilation compilation)
    {
        _facts = facts;
        _rules = new FormatterRules(compilation, facts.Symbols);
        _helpers = HelperSources.Available(compilation);
    }

    public static string Cached(ITypeSymbol type) => $"{Core}CachedFormatter<{TypeNames.Global(type)}>";

    public static string GeneratedFormatter(INamedTypeSymbol type)
    {
        var name = TypeNames.Gen + TypeNames.FormatterName(type.OriginalDefinition);
        var args = TypeNames.AllTypeArguments(type);
        return args.Count == 0 ? name : $"{name}<{string.Join(", ", args.Select(TypeNames.Global))}>";
    }

    /// <summary>Size of a raw unmanaged type: a constant for primitives and enums, symbolic for structs.</summary>
    public static RawSize RawSizeOf(ITypeSymbol type)
    {
        var t = type is INamedTypeSymbol { TypeKind: TypeKind.Enum, EnumUnderlyingType: { } underlying }
            ? underlying
            : type;
        int bytes = t.SpecialType switch
        {
            SpecialType.System_Boolean or SpecialType.System_Byte or SpecialType.System_SByte => 1,
            SpecialType.System_Char or SpecialType.System_Int16 or SpecialType.System_UInt16 => 2,
            SpecialType.System_Int32 or SpecialType.System_UInt32 or SpecialType.System_Single => 4,
            SpecialType.System_Int64 or SpecialType.System_UInt64 or SpecialType.System_Double => 8,
            SpecialType.System_Decimal => 16,
            _ => 0,
        };
        return bytes != 0
            ? RawSize.Constant(bytes)
            : new RawSize(0, $"{Core}NinoUnsafe.SizeOf<{TypeNames.Global(type)}>()");
    }

    /// <summary>
    /// Returns the formatter for <paramref name="type"/>, or null if Nino cannot serialize it at all.
    /// Type parameters (and <c>[NinoType]</c>s generated elsewhere) are resolved at runtime through
    /// <c>CachedFormatter&lt;T&gt;</c>.
    /// </summary>
    public ResolvedFormatter? Resolve(ITypeSymbol type, bool utf8)
    {
        type = type.WithNullableAnnotation(NullableAnnotation.NotAnnotated);
        var memo = utf8 ? _utf8 : _plain;
        if (memo.TryGetValue(type, out var formatter))
            return formatter;
        // A collection of itself (class Tree : List<Tree>) reaches its own type again: resolve that at runtime.
        var resolving = t_resolving ??= new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default);
        if (!resolving.Add(type))
            return new ResolvedFormatter(Cached(type), false);
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
    public string? ResolveCollection(INamedTypeSymbol type) => FirstMatch(_rules.Open, type, false);

    private ResolvedFormatter? ResolveCore(ITypeSymbol type, bool utf8)
    {
        switch (type)
        {
            case ITypeParameterSymbol:
                return new ResolvedFormatter(Cached(type), false);
            case IArrayTypeSymbol array:
                return Registered(FirstMatch(_rules.ForArray(array.IsSZArray ? 1 : -array.Rank), type, utf8));
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
                return new ResolvedFormatter(utf8 ? $"{Core}Utf8StringFormatter" : $"{Core}StringFormatter", true);
            case SpecialType.System_Object:
                return new ResolvedFormatter($"{Core}PolymorphicFormatter<object>", true);
        }

        if (type.IsTupleType && type.TupleUnderlyingType is { } underlying)
            type = underlying;

        // Registrations of this very definition first (a library can override how a raw struct is written).
        var definition = type.OriginalDefinition;
        if (FirstMatch(_rules.ForDefinition(definition), type, utf8) is { } registered)
            return new ResolvedFormatter(registered, true);
        foreach (var helper in _helpers)
        {
            if (SymbolEqualityComparer.Default.Equals(helper.Target, definition))
                return Registered(helper.Bind(type, a => Element(a, utf8)));
        }

        if (_facts.IsRawUnmanaged(type))
            return new ResolvedFormatter($"{Core}UnmanagedFormatter<{TypeNames.Global(type)}>", true);

        // Checked before the open registrations: walking the base types and interfaces of a type is the most
        // expensive part of a model build.
        if (type.IsNinoType())
            return new ResolvedFormatter(_facts.IsGeneratedHere(type) ? GeneratedFormatter(type) : Cached(type), false);

        return Registered(FirstMatch(_rules.Open, type, utf8));
    }

    private static ResolvedFormatter? Registered(string? code) =>
        code == null ? null : new ResolvedFormatter(code, true);

    private string? Element(ITypeSymbol type, bool utf8) =>
        Resolve(type, utf8)?.Code;

    private string? FirstMatch(IReadOnlyList<FormatterRule> rules, ITypeSymbol type, bool utf8)
    {
        foreach (var rule in rules)
        {
            if (Bind(rule, type, utf8) is { } code)
                return code;
        }

        return null;
    }

    /// <summary>
    /// Binds the formatter's type parameters for <paramref name="target"/> (see <c>NinoFormatterAttribute</c>):
    /// match the pattern, infer from type constraints, resolve element formatters, then check every constraint.
    /// </summary>
    private string? Bind(FormatterRule rule, ITypeSymbol target, bool utf8)
    {
        var parameters = rule.Formatter.TypeParameters;
        var types = new ITypeSymbol?[parameters.Length];
        var formatters = new string?[parameters.Length];
        if (!TypeUnifier.Unify(rule.Pattern, target, types))
            return null;

        bool progress = true;
        while (progress)
        {
            progress = false;
            for (int i = 0; i < parameters.Length; i++)
            {
                if (types[i] is { } bound)
                {
                    foreach (var constraint in parameters[i].ConstraintTypes)
                    {
                        if (TypeUnifier.IsBound(constraint, types))
                            continue;
                        if (!TypeUnifier.UnifyWithSupertype(constraint, bound, types))
                            return null;
                        progress = true;
                    }
                }
                else if (formatters[i] == null &&
                         FormatterRules.FormatterElement(parameters[i], _facts.Symbols) is { } element &&
                         TypeUnifier.IsBound(element, types))
                {
                    var elementType = TypeUnifier.Substitute(element, types, _facts.Compilation);
                    if (elementType == null || Element(elementType, utf8) is not { } code)
                        return null;
                    formatters[i] = code;
                    progress = true;
                }
            }
        }

        if (parameters.Length == 0)
            return rule.Prefix;
        var args = new string[parameters.Length];
        for (int i = 0; i < parameters.Length; i++)
        {
            if (types[i] is { } bound)
            {
                if (!Satisfies(parameters[i], bound, types))
                    return null;
                args[i] = TypeNames.Global(bound);
            }
            else
            {
                args[i] = formatters[i]!; // every parameter is inferable (FormatterRules.Read), so it is bound
                if (args[i] == null)
                    return null;
            }
        }

        return $"{rule.Prefix}<{string.Join(", ", args)}>";
    }

    private bool Satisfies(ITypeParameterSymbol parameter, ITypeSymbol type, ITypeSymbol?[] types)
    {
        if (parameter.HasReferenceTypeConstraint && !type.IsReferenceType)
            return false;
        if (parameter.HasValueTypeConstraint &&
            (!type.IsValueType || type.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T))
            return false;
        // `unmanaged` means "copied as raw memory", which is stricter than C#'s unmanaged.
        if (parameter.HasUnmanagedTypeConstraint && !_facts.IsRawUnmanaged(type))
            return false;
        if (parameter.HasConstructorConstraint && !HasPublicDefaultConstructor(type))
            return false;
        foreach (var constraint in parameter.ConstraintTypes)
        {
            if (!TypeUnifier.UnifyWithSupertype(constraint, type, types))
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
