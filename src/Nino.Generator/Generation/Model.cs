using Nino.Generator.DTOs;

namespace Nino.Generator.Generation;

// Everything in this file is an immutable value: the incremental pipeline compares these to decide what to
// re-emit, so they must never hold symbols, syntax nodes or the compilation.

internal enum FormatterKind
{
    /// <summary>Value type: body only.</summary>
    Struct,

    /// <summary>Sealed class: 1-byte null flag, then body.</summary>
    SealedClass,

    /// <summary>Non-sealed instantiable class: type id, then body (subtypes dispatched through the registry).</summary>
    Class,

    /// <summary>Abstract class or interface: always dispatched through the registry.</summary>
    Polymorphic,
}

internal enum MemberAccess
{
    /// <summary><c>value.Member</c>.</summary>
    Direct,

    /// <summary><c>[UnsafeAccessor]</c> declared in the formatter (.NET 8+, non-generic declaring type).</summary>
    UnsafeAccessor,

    /// <summary>Accessor class generated inside the (partial) declaring type.</summary>
    Partial,
}

/// <summary>
/// Size of a member written raw (as part of an unmanaged block). <see cref="Bytes"/> is the size when it is a
/// compile-time constant (primitives, enums), else 0; <see cref="Expression"/> is the size as C# source either way.
/// </summary>
internal sealed record RawSize(int Bytes, string Expression)
{
    /// <summary>Whether the size is a constant; otherwise the member is a struct (<c>NinoUnsafe.SizeOf&lt;T&gt;()</c>).</summary>
    public bool IsConstant => Bytes != 0;

    public static RawSize Constant(int bytes) =>
        new(bytes, bytes.ToString(System.Globalization.CultureInfo.InvariantCulture));
}

internal sealed record MemberModel(
    string Name,
    // Id of the member in keyed bodies: the hash of its name.
    int Key,
    string TypeName,
    string Formatter,
    // Set when the member is written raw as part of an unmanaged block, otherwise null.
    RawSize? Raw,
    bool IsProperty,
    MemberAccess Access,
    // Type that declares the member (global-qualified), used by accessors.
    string DeclaringType,
    bool DeclaringTypeIsValueType,
    // Whether the member can only be assigned while constructing the instance (readonly field, init-only).
    bool ConstructionOnly,
    // Whether the member is read-only for us even at construction (no way to assign it): read and dropped.
    bool Unassignable,
    // Runtime type id of the member type when its built-in formatter is registered for top-level use, else 0.
    int TypeId);

internal sealed record ConstructionModel(
    // Re-create the instance on every read (constructor arguments, init-only or readonly members).
    bool Recreate,
    // "new global::Ns.T" or "global::Ns.T.Factory"; empty when the type cannot be constructed.
    string Creator,
    // Member names passed as constructor arguments, in order.
    EquatableArray<string> Arguments,
    // [NinoRefDeserialization] factory method used when the target is null (in-place mode only).
    string RefFactory);

internal sealed record TypeModel(
    // Fully qualified type, e.g. "global::Ns.Outer.Box<T>".
    string TypeName,
    // Formatter type name without type parameters, e.g. "Ns_Outer_Box_1Formatter".
    string FormatterName,
    // Type parameter names, outermost containing type first; empty for non-generic types.
    EquatableArray<string> TypeParameters,
    // " where T : struct" ... or "".
    string Constraints,
    FormatterKind Kind,
    // Type id, or 0 for generic types (computed at runtime).
    int TypeId,
    string IdName,
    // Hash of the runtime name ignoring [NinoFormerName]: the key a by-type lookup computes without reflection.
    int NameId,
    bool VersionTolerant,
    // Members written as (key, length, value): see NinoTypeAttribute.Keyed.
    bool Keyed,
    // Objects written with a reference header (null, back reference or new): see trackReferences.
    bool TrackReferences,
    // Unmanaged struct written as its raw memory.
    bool RawStruct,
    // For [NinoType] collections: the formatter of the elements, written before the members.
    string CollectionFormatter,
    // Whether static WriteMembers/ReadMembers are emitted so derived types can reuse them (classes whose
    // members are all directly accessible).
    bool EmitMembers,
    // Formatter whose WriteMembers/ReadMembers cover the first BaseMemberCount members, or "".
    string BaseFormatter,
    int BaseMemberCount,
    EquatableArray<MemberModel> Members,
    ConstructionModel Construction,
    // (type, formatter) pairs registered for AOT platforms and top-level calls.
    EquatableArray<FormatterRegistration> UsedFormatters,
    // Creators of the collection types the members' formatters create (NinoFactory<T>), registered like them.
    EquatableArray<FactoryRegistration> UsedFactories,
    // [NinoPooled]: instances are taken from NinoPool<T> before being created, and the partial adds the pool hooks.
    bool Pooled,
    // Namespace and containing declarations of the generated partial (accessor class, pool hooks); empty when
    // neither is needed.
    string PartialNamespace,
    EquatableArray<string> PartialDeclarations)
{
    public bool IsGeneric => TypeParameters.Length != 0;

    /// <summary>"&lt;T1, T2&gt;", or "" for non-generic types.</summary>
    public string TypeParameterList => IsGeneric ? "<" + string.Join(", ", TypeParameters) + ">" : "";
}

// Id: runtime type id of the closed type (NinoTypeId), the key of lazy registration.
internal sealed record FormatterRegistration(string TypeName, string Formatter, int Id);

// A NinoFactory<T> creator: Creator is a lambda ("static () => new global::Ns.Bag()"); Id as above.
internal sealed record FactoryRegistration(string TypeName, string Creator, int Id);

// A top-level NinoSerializer/NinoDeserializer call: the formatter of its type and the creators that formatter needs.
internal sealed record UsageRegistration(FormatterRegistration Formatter, EquatableArray<FactoryRegistration> Factories);

// A generated formatter, as the registry lists it. Arity: its number of type parameters (0 if not generic).
internal sealed record RegistryEntry(string FormatterName, int Arity, int TypeId, int NameId);

// RegisteringReferences: one formatter (open name) of each referenced assembly that registers formatters.
internal sealed record GeneratorOptions(string AssemblyName, bool HasModuleInitializer, bool HasUnityEngine,
    bool HasUnityEditor, EquatableArray<string> RegisteringReferences);
