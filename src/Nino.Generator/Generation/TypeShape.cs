using System.Linq;
using Microsoft.CodeAnalysis;
using Nino.Generator.Common;

namespace Nino.Generator.Generation;

/// <summary>
/// What a <c>[NinoType]</c> is, as every rule about it reads it: its kind, format, tracking, raw layout, collection
/// formatter and whether its body may open a class body. Computed once per type and compilation from its symbol
/// (<see cref="TypeFacts.Shape"/>), so the model of the type (<see cref="ModelBuilder"/>), the types that hold it
/// (<see cref="TypeFacts.ShallowTwin"/>, <see cref="TypeFacts.IsFixedSize"/>, <see cref="TypeFacts.TypeBound"/>)
/// and its subtypes (<c>ModelBuilder.DispatchRoot</c>) decide from the same facts, through the rules of
/// <see cref="TypeRules"/>. Holds symbols: lives as long as its <see cref="TypeFacts"/>, never in a model.
/// </summary>
internal sealed class TypeShape
{
    public TypeShape(INamedTypeSymbol type, NinoTypeOptions attr, TypeFacts facts)
    {
        Kind = type.TypeKind == TypeKind.Interface || type.IsAbstract
            ? FormatterKind.Polymorphic
            : type.IsValueType
                ? FormatterKind.Struct
                : type.IsSealed
                    ? FormatterKind.SealedClass
                    : FormatterKind.Class;
        Options = attr;
        Generic = TypeNames.AllTypeArguments(type).Count != 0;
        Tracked = TypeRules.IsTracked(type);
        Tolerant = facts.IsTolerant(attr);
        GeneratedHere = facts.Visibility.IsGeneratedHere(type);
        RawStruct = facts.Raw.IsRawStruct(type);
        if (Kind is not (FormatterKind.Class or FormatterKind.SealedClass))
            return;
        Collection = facts.Resolver.ResolveCollection(type);
        MembersOpenBodies = facts.Members.Of(type, attr).Any(member =>
            NinoSymbols.Has(member, facts.Symbols.CustomFormatter) ||
            MemberCollector.MemberType(member) is { } memberType &&
            facts.Resolver.Resolve(memberType, NinoSymbols.Has(member, facts.Symbols.Utf8)) is { OpensBodies: true });
    }

    public FormatterKind Kind { get; }

    /// <summary>The type's <c>[NinoType]</c> options (its own or inherited).</summary>
    public NinoTypeOptions Options { get; }

    /// <summary>Whether it has type arguments (its own or a containing type's).</summary>
    public bool Generic { get; }

    /// <summary>Whether it, a base class or an interface asks for reference tracking (<see cref="TypeRules.IsTracked"/>).</summary>
    public bool Tracked { get; }

    /// <summary>Whether objects of it are written with a reference header: tracked, and not a struct.</summary>
    public bool TrackReferences => Tracked && Kind != FormatterKind.Struct;

    /// <summary>Version tolerant: so marked, or WEAK_VERSION_TOLERANCE (<see cref="TypeFacts.IsTolerant"/>).</summary>
    public bool Tolerant { get; }

    public bool Keyed => Options.Keyed;

    /// <summary>Whether its formatter is generated in this compilation.</summary>
    public bool GeneratedHere { get; }

    /// <summary>An unmanaged struct written as its raw memory.</summary>
    public bool RawStruct { get; }

    /// <summary>The formatter of its elements, for a <c>[NinoType]</c> class that is a collection; null otherwise.</summary>
    public ResolvedFormatter? Collection { get; }

    /// <summary>
    /// Whether a member of a class is written by a formatter that may open a class body (a custom formatter may,
    /// see <see cref="ResolvedFormatter.OpensBodies"/>); false for structs, abstract classes and interfaces.
    /// </summary>
    public bool MembersOpenBodies { get; }

    /// <summary>
    /// Whether its body may hold class bodies (<c>NinoBody.Nests</c>, <see cref="TypeModel.Nests"/>): any type but a
    /// class whose members and elements open none.
    /// </summary>
    public bool Nests => Kind is not (FormatterKind.Class or FormatterKind.SealedClass) ||
                         Collection is { OpensBodies: true } || MembersOpenBodies;
}

/// <summary>
/// The rules that decide what a type's formatter has, in one place: <see cref="TypeModel"/> applies them to its
/// values, and <see cref="TypeFacts"/> and <see cref="ModelBuilder"/> to the <see cref="TypeShape"/> of a type they
/// only see as a symbol.
/// </summary>
internal static class TypeRules
{
    /// <summary>Whether the type, a base class or an interface asks for reference tracking.</summary>
    public static bool IsTracked(INamedTypeSymbol type) =>
        type.GetNinoTypeOptions() is { TrackReferences: true } ||
        type.NinoAncestors().Any(static a =>
            a.GetDirectNinoTypeAttribute() is { } attribute && NinoTypeOptions.From(attribute).TrackReferences);

    /// <summary>
    /// Whether the formatter dispatches its subtypes through generated code (<see cref="TypeModel.DispatchesSubtypes"/>).
    /// </summary>
    public static bool DispatchesSubtypes(FormatterKind kind, bool trackReferences) =>
        !trackReferences && kind is FormatterKind.Polymorphic or FormatterKind.Class;

    /// <summary>Whether the formatter has a <c>__Top</c> twin (<see cref="TypeModel.HasTop"/>).</summary>
    public static bool HasTop(FormatterKind kind, bool generic, bool trackReferences, bool rawStruct, bool nests) =>
        !generic && !trackReferences && !rawStruct && !nests && kind == FormatterKind.SealedClass;

    /// <inheritdoc cref="HasTop(FormatterKind, bool, bool, bool, bool)"/>
    public static bool HasTop(TypeShape s) => HasTop(s.Kind, s.Generic, s.TrackReferences, s.RawStruct, s.Nests);
}
