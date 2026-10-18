using System.Linq;
using Nino.Generator.Common;

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

    /// <summary>
    /// <c>((Declaring)value).Member</c>: a public member of a base class that a derived member of the same name hides
    /// (<c>new</c>). Never part of an object initializer, which would name the derived member.
    /// </summary>
    Cast,
}

/// <summary>
/// Size of a member written raw (as part of an unmanaged block). <see cref="Bytes"/> is the size when it is a
/// compile-time constant (primitives, enums), else 0; <see cref="Expression"/> is the size as C# source either way.
/// <see cref="Layout"/> is the bytes in the canonical layout, as the member's published layout shows them
/// (<c>RawLayout.Describe</c>).
/// </summary>
internal sealed record RawSize(int Bytes, string Expression)
{
    public string Layout { get; init; } = "raw";

    /// <summary>Whether the size is a constant; otherwise the member is a struct (<c>NinoUnsafe.SizeOf&lt;T&gt;()</c>).</summary>
    public bool IsConstant => Bytes != 0;

    public static RawSize Constant(int bytes) =>
        new(bytes, bytes.ToString(System.Globalization.CultureInfo.InvariantCulture));
}

internal sealed record MemberModel(
    // Unique among the type's members, names its locals and accessors: the member's name, unless a more derived
    // member of the same name is serialized too (`new`), then "Name__N" with N the number of such levels.
    string Name,
    // The member's name in C#.
    string SourceName,
    // Id of the member in keyed bodies: the hash of its name.
    int Key,
    // Keys of its [NinoFormerName]s (keyed bodies only), also accepted when reading.
    EquatableArray<int> FormerKeys,
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
    int TypeId,
    // Whether the member's formatter may write or read a class body (see ResolvedFormatter.OpensBodies).
    bool OpensBodies,
    // The formatter with the generated formatters it is composed of replaced by their __Shallow twins (see
    // TypeFacts.HasShallow), for shallow bodies; the formatter itself when none has one.
    string ShallowFormatter,
    // Whether a new instance (TypeModel.FreshRead) holds null in this member, which a Nino.Core formatter then reads
    // from a null local rather than from the member: a field or auto-property of a reference type without an
    // initializer, in a type whose constructors set nothing.
    bool Fresh,
    // Whether the member's Nino.Core formatter reads its elements in a loop inlined into the body (any collection
    // but strings, raw arrays and lists, and dictionaries of raw keys and raw or string values; see
    // TypeNames.IsFlat).
    bool ReadsLoop);

internal sealed record ConstructionModel(
    // Re-create the instance on every read (constructor arguments, init-only or readonly members).
    bool Recreate,
    // "new global::Ns.T" or "global::Ns.T.Factory"; empty when the type cannot be constructed.
    string Creator,
    // Member names passed as constructor arguments, in order.
    EquatableArray<string> Arguments,
    // [NinoFactory] factory method used when the target is null (in-place mode only).
    string Factory);

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
    // NinoFormat.VersionTolerant, or WEAK_VERSION_TOLERANCE (which also sets it on keyed types: see Tolerant).
    bool VersionTolerant,
    // Members written as (key, length, value): see NinoFormat.Keyed.
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
    // Creators of the collection types the members' formatters create (NinoActivator<T>), registered like them.
    EquatableArray<FactoryRegistration> UsedFactories,
    // Closed generic [NinoType]s the type names (member types, base types), registered by type id.
    EquatableArray<ClosedTypeRegistration> UsedGenericTypes,
    // The topmost base class of this assembly that dispatches subtypes (and its formatter), or "": a dispatching
    // type below it uses its dispatch.
    string DispatchRoot,
    string DispatchRootFormatter,
    // The [NinoType] base classes (nearest first) and interfaces (global names), for the subtype dispatch.
    EquatableArray<string> NinoBaseClasses,
    EquatableArray<string> NinoInterfaces,
    // [NinoPooled]: instances are taken from NinoPool<T> before being created, and the partial adds the pool hooks.
    bool Pooled,
    // Namespace and containing declarations of the generated partial (accessor class, pool hooks); empty when
    // neither is needed.
    string PartialNamespace,
    EquatableArray<string> PartialDeclarations,
    // A class whose body can be read after its instance is in its slot (see NinoDepth): created without its
    // members (not by a constructor taking them) and not compared by value.
    bool Deferrable,
    // Whether a class body may hold class bodies (see NinoBody.Nests): false lets it skip the depth count.
    bool Nests,
    // Whether every (non-null) value is written in the same number of bytes (TypeFacts.IsFixedSize): the formatter
    // implements INinoFixedSizeFormatter.
    bool FixedSize,
    // How deep below its own check a value can check the depth (TypeFacts.DepthBound): -1 if it opens no class
    // body, TypeFacts.Unbounded when not known.
    int DepthBound,
    // Whether the formatter has a __Shallow twin (TypeFacts.HasShallow).
    bool Shallow,
    // CollectionFormatter with its element formatters' __Shallow twins.
    string ShallowCollectionFormatter,
    // Whether ReadBody reads a new instance (created by `new T()`, not taken from a pool or a factory) apart from
    // an existing one: its members are known to be null there (MemberModel.Fresh), so their formatters' reuse
    // paths fold away. Only for types whose members read no loop in the body (MemberModel.ReadsLoop): a second copy
    // of a loop saves little per object and the JIT may allocate its registers worse there. Sealed classes only: those
    // are what collections hold many new instances of, and the second copy costs compile time for every type.
    bool FreshRead,
    // Unmanaged structs whose layout the runtime measures (LayoutTypes), for the registry's [DynamicDependency].
    EquatableArray<string> LayoutTypes,
    // A raw struct's bytes in the canonical layout, its published layout (LayoutEmitter, RawLayout.Describe); empty
    // for any other type, whose layout is its members.
    string RawLayout)
{
    public bool IsGeneric => TypeParameters.Length != 0;

    /// <summary>
    /// Version-tolerant positional body: the members of the whole hierarchy in one length-prefixed region, so members
    /// appended to the most derived class are skipped by older readers and missing ones are left to newer readers.
    /// </summary>
    public bool Tolerant => VersionTolerant && !Keyed;

    /// <summary>
    /// Whether the formatter dispatches its subtypes through generated code (<c>__WriteSubtype</c> and
    /// <c>__ReadSubtype</c>, emitted with the registry) before falling back to <c>NinoPolymorphic</c>.
    /// </summary>
    public bool DispatchesSubtypes =>
        !IsGeneric && !TrackReferences && Kind is FormatterKind.Polymorphic or FormatterKind.Class;

    /// <summary>Whether this type's formatter holds the dispatch of its hierarchy (emitted with the registry).</summary>
    public bool IsDispatchRoot => DispatchesSubtypes && DispatchRoot.Length == 0;

    /// <summary>Whether bases in this assembly dispatch to this type directly.</summary>
    public bool IsDispatchTarget =>
        !IsGeneric && !TrackReferences && Kind is FormatterKind.Class or FormatterKind.SealedClass;

    /// <summary>
    /// Whether the body is compiled on its own (it calls formatters): see <c>BodyEmitter</c>.
    /// </summary>
    public bool OutOfLineBody => Members.Any(static x => x.Raw == null) || CollectionFormatter.Length != 0;

    /// <summary>
    /// Whether the formatter has a <c>__Top</c> twin for the intercepted top-level calls, whose body is inlined into
    /// the caller: the caller then owns the reader or writer, which the JIT keeps in registers (passed to the
    /// out-of-line body, its address keeps every field in memory, zeroed in the prolog and reloaded after each
    /// store). Only for sealed classes whose small body (at most two members that call formatters) opens no class
    /// body (one that does passes the reader to that body's call anyway): a large body inlined into every call site
    /// would use up the JIT's inlining budget there.
    /// </summary>
    public bool TopInline => HasTop && InlineBody;

    /// <summary>
    /// Whether the body is small enough to be inlined wherever the formatter is used (at most two members that call
    /// formatters, of at most eight), for a class whose body opens no class body.
    /// </summary>
    public bool InlineBody => !IsGeneric && !TrackReferences && !RawStruct && !Nests &&
                              Kind is FormatterKind.Class or FormatterKind.SealedClass && !Keyed && !Tolerant &&
                              BaseFormatter.Length == 0 && CollectionFormatter.Length == 0 &&
                              Members.Length <= 8 && Members.Count(static x => x.Raw == null) is 1 or 2;

    /// <summary>
    /// Whether the formatter has a <c>__Top</c> twin: sealed classes whose body opens no class body. With no body
    /// open around it (the calls that create their reader or writer), such a body is never deferred and counts no
    /// depth, so the twin skips the depth check and calls the body directly (inlined if <see cref="TopInline"/>,
    /// else the out-of-line body), also for each element of a built-in collection of it.
    /// </summary>
    public bool HasTop => !IsGeneric && !TrackReferences && !RawStruct && !Nests && Kind == FormatterKind.SealedClass;

    /// <summary>
    /// Whether the <c>__Shallow</c> twin has bodies of its own: a member (or the elements of a collection) is
    /// written by a formatter with twins. Otherwise the twin calls the body, which then has no depth to skip.
    /// </summary>
    public bool ShallowBodies => Shallow && !RawStruct &&
                                 (ShallowCollectionFormatter != CollectionFormatter ||
                                  Members.Any(static x => x.ShallowFormatter != x.Formatter));

    /// <summary>
    /// Whether a shallow body writes the base class's members itself rather than through its <c>WriteMembers</c>,
    /// whose formatters count the depth: one of them has a twin.
    /// </summary>
    public bool ShallowInlinesBase => BaseFormatter.Length != 0 &&
                                      Members.Take(BaseMemberCount).Any(static x => x.ShallowFormatter != x.Formatter);

    /// <summary>
    /// Whether <c>Serialize</c>/<c>Deserialize</c> choose the <c>__Shallow</c> twin when the depth leaves room for
    /// the whole nesting bound (<c>NinoObject.SerializeSealed</c> with a bound): a sealed class whose body counts the
    /// depth (<see cref="Nests"/>), which the twin then skips with every check inside. A body that does not nest
    /// checks only its own depth, which the twin cannot skip.
    /// </summary>
    public bool BoundedEntry => Shallow && Kind == FormatterKind.SealedClass && Nests && !TrackReferences;

    /// <summary>
    /// Whether the body of a class is one block of raw members (positional, not tracked, not a collection): the
    /// formatter implements <c>INinoRawBodyFormatter</c>, so the type id or null flag in front of the body is written
    /// into the same reservation (one capacity check instead of two).
    /// </summary>
    public bool RawBody => Kind is FormatterKind.Class or FormatterKind.SealedClass && !RawStruct && !VersionTolerant &&
                           !Keyed && !TrackReferences && CollectionFormatter.Length == 0 && Members.Length != 0 &&
                           Members.All(static x => x.Raw != null);

    /// <summary>"&lt;T1, T2&gt;", or "" for non-generic types.</summary>
    public string TypeParameterList => IsGeneric ? "<" + string.Join(", ", TypeParameters) + ">" : "";
}

// Id: runtime type id of the closed type (NinoTypeId), the key of lazy registration.
internal sealed record FormatterRegistration(string TypeName, string Formatter, int Id);

// A NinoActivator<T> creator: Creator is a lambda ("static () => new global::Ns.Bag()"); Id as above.
internal sealed record FactoryRegistration(string TypeName, string Creator, int Id);

// A closed generic [NinoType] (ClosedGenerics). Id: wire id; NameId: hash of the runtime name (by-type lookups), the
// same unless [NinoFormerName]. Formatter: its generated formatter, or "" when generated by another assembly (then
// registered by reflection). Polymorphic: not abstract, so registered with NinoPolymorphic; Tracked: trackReferences.
internal sealed record ClosedTypeRegistration(string TypeName, string Formatter, int Id, int NameId, bool Polymorphic,
    bool Tracked);

// A top-level NinoSerializer/NinoDeserializer call: the formatter of its type and the creators that formatter needs
// (null when the type is a [NinoType]), and the closed generic [NinoType]s its type names.
internal sealed record UsageRegistration(FormatterRegistration? Formatter, EquatableArray<FactoryRegistration> Factories,
    EquatableArray<ClosedTypeRegistration> GenericTypes, EquatableArray<string> LayoutTypes);

// A generated formatter, as the registry lists it. Arity: its number of type parameters (0 if not generic).
// Registration: the statements registering the formatter (FormatterEmitter.RegisterStatements), run by the registry.
internal sealed record RegistryEntry(string FormatterName, int Arity, int TypeId, int NameId,
    EquatableArray<string> Registration);

// A type in the per-assembly subtype dispatch: a base (DispatchesSubtypes), a target (IsDispatchTarget), or both.
// RawBody: the target's body is one raw block (TypeModel.RawBody), written with its type id in one reservation.
internal sealed record DispatchEntry(string TypeName, string FormatterName, int TypeId, bool IsBase, bool IsTarget,
    EquatableArray<string> BaseClasses, EquatableArray<string> Interfaces, bool RawBody);

// RegisteringReferences: one formatter (open name) of each referenced assembly that registers formatters.
internal sealed record GeneratorOptions(string AssemblyName, bool HasModuleInitializer, bool HasUnityEngine,
    bool HasUnityEditor, EquatableArray<string> RegisteringReferences);
