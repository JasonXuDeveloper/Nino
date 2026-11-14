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
        Kind = TypeRules.Kind(type);
        Options = attr;
        Generic = TypeNames.AllTypeArguments(type).Count != 0;
        Tracked = TypeRules.IsTracked(type);
        Format = attr.Format;
        GeneratedHere = facts.Visibility.IsGeneratedHere(type);
        RawStruct = facts.Raw.IsRawStruct(type);
        HasCallbacks = Kind != FormatterKind.Polymorphic && !RawStruct &&
            Callbacks.HasInHierarchy(type, facts.Symbols);
        if (Kind == FormatterKind.Polymorphic)
            return;
        if (Kind != FormatterKind.Struct)
            Collection = facts.Resolver.ResolveCollection(type);
        MembersOpenBodies = facts.Members.Of(type, attr).Any(member =>
            facts.Symbols.HasCustomFormatter(member) ||
            MemberCollector.MemberType(member) is { } memberType &&
            facts.Resolver.Resolve(memberType, facts.Symbols.WritesUtf8(member)) is { OpensBodies: true });
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

    /// <summary>The format it is written in (its <c>[NinoType]</c>'s, its own or inherited).</summary>
    public NinoFormat Format { get; }

    /// <summary>Version tolerant: <see cref="NinoFormat.VersionTolerant"/>.</summary>
    public bool Tolerant => Format == NinoFormat.VersionTolerant;

    public bool Keyed => Format == NinoFormat.Keyed;

    /// <summary>Whether its formatter is generated in this compilation.</summary>
    public bool GeneratedHere { get; }

    /// <summary>An unmanaged struct written as its raw memory.</summary>
    public bool RawStruct { get; }

    /// <summary>
    /// Whether its formatter calls serialization callbacks (<see cref="Callbacks"/>): a method of it or of a
    /// <c>[NinoType]</c> base class is marked as one, and it is a class or struct not copied as raw memory. Its values
    /// are then always written and read by its body, which calls them: it is not of fixed size
    /// (<see cref="TypeFacts.IsFixedSize"/>), so no block or <c>WriteFixed</c> writes them around it.
    /// </summary>
    public bool HasCallbacks { get; }

    /// <summary>The formatter of its elements, for a <c>[NinoType]</c> class that is a collection; null otherwise.</summary>
    public ResolvedFormatter? Collection { get; }

    /// <summary>
    /// Whether a member of a class or struct is written by a formatter that may open a class body (a custom
    /// formatter may, see <see cref="ResolvedFormatter.OpensBodies"/>; so may a <c>[NinoType]</c> member, a struct
    /// included); false for abstract classes and interfaces.
    /// </summary>
    public bool MembersOpenBodies { get; }

    /// <summary>
    /// Whether its body may hold class bodies (<c>NinoBodyTraits.Nests</c>, <see cref="DepthModel.Nests"/>): any type
    /// but a class or struct whose members and elements open none.
    /// </summary>
    public bool Nests => Kind == FormatterKind.Polymorphic || Collection is { OpensBodies: true } || MembersOpenBodies;
}

/// <summary>
/// The rules that decide what a type's formatter has, in one place: <see cref="TypeModel"/> applies them to its
/// values, and <see cref="TypeFacts"/> and <see cref="ModelBuilder"/> to the <see cref="TypeShape"/> of a type they
/// only see as a symbol.
/// </summary>
internal static class TypeRules
{
    /// <summary>The kind of the type's formatter.</summary>
    public static FormatterKind Kind(INamedTypeSymbol type) =>
        type.TypeKind == TypeKind.Interface || type.IsAbstract
            ? FormatterKind.Polymorphic
            : type.IsValueType
                ? FormatterKind.Struct
                : type.IsSealed
                    ? FormatterKind.SealedClass
                    : FormatterKind.Class;

    /// <summary>Whether a type of <paramref name="kind"/> may have subtypes: an interface, an abstract or unsealed class.</summary>
    public static bool HasSubtypes(FormatterKind kind) => kind is FormatterKind.Polymorphic or FormatterKind.Class;

    /// <summary>Whether a type of <paramref name="kind"/> can be instantiated: not an interface or abstract class.</summary>
    public static bool IsConcrete(FormatterKind kind) => kind != FormatterKind.Polymorphic;

    /// <summary>Whether the type, a base class or an interface asks for reference tracking.</summary>
    public static bool IsTracked(INamedTypeSymbol type) =>
        type.GetNinoTypeOptions() is { TrackReferences: true } ||
        type.NinoAncestors().Any(static a =>
            a.GetDirectNinoTypeAttribute() is { } attribute && NinoTypeOptions.From(attribute).TrackReferences);

    /// <summary>
    /// Whether the formatter dispatches its subtypes through generated code (<see cref="TypeModel.DispatchesSubtypes"/>).
    /// </summary>
    public static bool DispatchesSubtypes(FormatterKind kind, bool trackReferences) =>
        !trackReferences && HasSubtypes(kind);

    /// <summary>Whether the formatter has a <c>__Top</c> twin (<see cref="TypeModel.HasTop"/>).</summary>
    public static bool HasTop(FormatterKind kind, bool generic, bool trackReferences, bool rawStruct, bool nests) =>
        !generic && !trackReferences && !rawStruct && !nests && kind == FormatterKind.SealedClass;

    /// <inheritdoc cref="HasTop(FormatterKind, bool, bool, bool, bool)"/>
    public static bool HasTop(TypeShape s) => HasTop(s.Kind, s.Generic, s.TrackReferences, s.RawStruct, s.Nests);
}
