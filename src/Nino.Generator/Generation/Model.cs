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

    /// <summary>
    /// <c>[UnsafeAccessor]</c> declared in the formatter (.NET 8+; generic types .NET 9+, see
    /// <c>ModelBuilder.UnsafeAccessorReaches</c>).
    /// </summary>
    UnsafeAccessor,

    /// <summary>Accessor class generated inside the (partial) declaring type.</summary>
    Partial,

    /// <summary>
    /// <c>((Declaring)value).Member</c>: a public member of a base class that a derived member of the same name hides
    /// (<c>new</c>). Never part of an object initializer, which would name the derived member.
    /// </summary>
    Cast,
}

/// <summary>How a member written raw is read (<c>RawLayout.ReadOf</c>).</summary>
internal enum RawRead
{
    /// <summary>As its bytes: every bit pattern is a value.</summary>
    Plain,

    /// <summary>A <c>bool</c>: its byte is checked to be 0 or 1.</summary>
    Bool,

    /// <summary>
    /// A type that may hold values no constructor makes, checked in <c>NinoSettings.Untrusted</c> mode
    /// (<c>ReadChecked</c>): a struct (not a primitive or enum, whose sizes are constants), or a <c>DateTime</c>,
    /// <c>decimal</c>, <c>DateOnly</c> or <c>TimeOnly</c>.
    /// </summary>
    Checked,
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

    /// <summary>How the member is read.</summary>
    public RawRead Read { get; init; }

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
    FormatterRef Formatter,
    // Which formatter it is (FormatterTraits): strings are read and written directly, flat ones on local copies.
    FormatterTraits Traits,
    // How the member is written, for its published layout (ResolvedFormatter.Encoding, WireEncoding).
    string Encoding,
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
    // The formatter with the generated formatters it is composed of replaced by their shallow twins (see
    // TypeFacts.ShallowTwin), for shallow bodies; the formatter itself when none has one.
    FormatterRef ShallowFormatter,
    // Whether a new instance (TypeModel.FreshRead) holds null in this member, which a Nino.Core formatter then reads
    // from a null local rather than from the member, and a string is read without (no reuse of an equal string to
    // check, and the reader's address not taken by Reader.ReadStringOver): a field or auto-property of a reference
    // type without an initializer, in a type whose constructors set nothing.
    bool Fresh,
    // Whether the member's Nino.Core formatter reads its elements in a loop inlined into the body (any collection
    // but strings, raw arrays and lists, and dictionaries of raw keys and raw or string values; see
    // FormatterTraits.Flat).
    bool ReadsLoop)
{
    /// <summary>Whether the member is a string, which generated code reads and writes through the reader and writer.</summary>
    public bool IsString => (Traits & (FormatterTraits.String | FormatterTraits.Utf8String)) != 0;
}

internal sealed record ConstructionModel(
    // Re-create the instance on every read (constructor arguments, init-only or readonly members).
    bool Recreate,
    // "new global::Ns.T" or "global::Ns.T.Factory"; empty when the type cannot be constructed.
    string Creator,
    // Member names passed as constructor arguments, in order.
    EquatableArray<string> Arguments,
    // [NinoFactory] factory method used when the target is null (in-place mode only).
    string Factory);

/// <summary>
/// For a <c>[NinoType]</c> collection: the formatter of its elements, written before its members.
/// </summary>
/// <param name="Formatter">The formatter of the collection's elements.</param>
/// <param name="ShallowFormatter"><paramref name="Formatter"/> with its element formatters' shallow twins.</param>
/// <param name="Encoding">How it writes them, for the published layout (WireEncoding).</param>
internal sealed record CollectionModel(FormatterRef Formatter, FormatterRef ShallowFormatter, string Encoding);

/// <summary>What a type shares with its base classes and the types of its hierarchy.</summary>
internal sealed record InheritanceModel(
    // Whether static WriteMembers/ReadMembers are emitted so derived types can reuse them (classes whose
    // members are all directly accessible).
    bool EmitMembers,
    // Formatter whose WriteMembers/ReadMembers cover the first BaseMemberCount members, or "".
    string BaseFormatter,
    int BaseMemberCount,
    // Whether the first members are a shared generic base's, written here instead of through its WriteMembers.
    bool SharedBase,
    // The topmost base class of this assembly that dispatches subtypes (and its formatter), or "": a dispatching
    // type below it uses its dispatch.
    string DispatchRoot,
    string DispatchRootFormatter,
    // The [NinoType] base classes (nearest first) and interfaces (global names), for the subtype dispatch.
    EquatableArray<string> NinoBaseClasses,
    EquatableArray<string> NinoInterfaces,
    // The closed generic ones among them that generated code can name, with their definitions: a generic base of this
    // assembly dispatches to this type when its type arguments are these (DispatchEmitter).
    EquatableArray<GenericAncestor> NinoGenericAncestors);

/// <summary>How the type's bodies nest, and what that lets its formatter skip (see <c>NinoDepth</c>).</summary>
internal sealed record DepthModel(
    // A class whose body can be read after its instance is in its slot (see NinoDepth): created without its
    // members (not by a constructor taking them) and not compared by value.
    bool Deferrable,
    // Whether a class body may hold class bodies (see NinoBody.Nests): false lets it skip the depth count.
    bool Nests,
    // How deep below its own check a value can check the depth (TypeFacts.DepthBound): -1 if it opens no class
    // body, TypeFacts.Unbounded when not known.
    int Bound,
    // Whether the formatter has a __Shallow twin (TypeFacts.HasShallow; a sealed class whose body nests nothing uses
    // its __Top twin as that, TypeFacts.ShallowTwin).
    bool Shallow);

/// <summary>The generated partial of the type itself (accessor class, pool hooks).</summary>
internal sealed record PartialModel(
    // Namespace and containing declarations of the partial; empty when neither is needed.
    string Namespace,
    EquatableArray<string> Declarations)
{
    public static readonly PartialModel None = new("", EquatableArray<string>.Empty);
}

/// <summary>What the type's formatter needs registered with the assembly's (NinoGen.Usages).</summary>
internal sealed record RegistrationModel(
    // (type, formatter) pairs registered for AOT platforms and top-level calls.
    EquatableArray<FormatterRegistration> Formatters,
    // Creators of the collection types the members' formatters create (NinoActivator<T>), registered like them.
    EquatableArray<FactoryRegistration> Factories,
    // Closed generic [NinoType]s the type names (member types, base types), registered by type id.
    EquatableArray<TypeRegistration> GenericTypes,
    // Unmanaged structs whose layout the runtime measures (LayoutTypes), for the registry's [DynamicDependency].
    EquatableArray<string> LayoutTypes);

internal sealed record TypeModel(
    // Fully qualified type, e.g. "global::Ns.Outer.Box<T>".
    string TypeName,
    // The type for typeof: a generic type as its open definition, e.g. "global::Ns.Outer.Box<>".
    string OpenTypeName,
    // Formatter type name without type parameters, e.g. "Ns_Outer_Box_1Formatter".
    string FormatterName,
    // Type parameter names, outermost containing type first; empty for non-generic types. The formatter also takes
    // the formatter of each (TypeParameterList).
    EquatableArray<string> TypeParameters,
    // " where T : struct" ... or "".
    string Constraints,
    FormatterKind Kind,
    // Type id, or 0 for generic types (computed at runtime).
    int TypeId,
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
    // For [NinoType] collections: the formatter of the elements; null for any other type.
    CollectionModel? Collection,
    EquatableArray<MemberModel> Members,
    ConstructionModel Construction,
    InheritanceModel Inheritance,
    DepthModel Depth,
    // [NinoPooled]: instances are taken from NinoPool<T> before being created, and the partial adds the pool hooks.
    bool Pooled,
    PartialModel Partial,
    RegistrationModel Registrations,
    // Whether every (non-null) value is written in the same number of bytes (TypeFacts.IsFixedSize): the formatter
    // implements INinoFixedSizeFormatter.
    bool FixedSize,
    // Whether the formatter implements INinoBlockFormatter (TypeFacts.IsBlock): a struct of fixed size whose
    // collections are read and written as blocks.
    bool Block,
    // Whether ReadBody reads a new instance (created by `new T()`, not taken from a pool or a factory) apart from
    // an existing one: its members are known to be null there (MemberModel.Fresh), so their formatters' reuse
    // paths fold away. Only for types whose members read no loop in the body (MemberModel.ReadsLoop): a second copy
    // of a loop saves little per object and the JIT may allocate its registers worse there. Sealed classes only: those
    // are what collections hold many new instances of, and the second copy costs compile time for every type.
    bool FreshRead,
    // Whether the layout publishes Deferrable (NinoSymbols.LayoutDeferrable: the referenced Nino.Core can hold it).
    bool PublishesDeferrable,
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
    public bool DispatchesSubtypes => TypeRules.DispatchesSubtypes(Kind, TrackReferences);

    /// <summary>
    /// <paramref name="value"/>, one of the names the model leaves empty where it does not apply (a base, collection or
    /// dispatch root formatter, a constructor). Emitting an empty one is a bug in the rule that chose the branch: it throws, and
    /// <c>NinoGenerator</c> reports it as a build error naming the type, rather than writing code that does not compile.
    /// </summary>
    public string Need(string value, string name) =>
        value.Length != 0 ? value : throw new System.InvalidOperationException($"{name} of {TypeName} is empty");

    /// <inheritdoc cref="Need(string, string)"/>
    public T Need<T>(T? value, string name) where T : class =>
        value ?? throw new System.InvalidOperationException($"{name} of {TypeName} is empty");

    /// <summary>Whether the type is a <c>[NinoType]</c> collection, its elements written before its members.</summary>
    public bool IsCollection => Collection != null;

    /// <summary>Whether this type's formatter holds the dispatch of its hierarchy (emitted with the registry).</summary>
    public bool IsDispatchRoot => DispatchesSubtypes && Inheritance.DispatchRoot.Length == 0;

    /// <summary>Whether bases in this assembly dispatch to this type directly.</summary>
    public bool IsDispatchTarget =>
        !IsGeneric && !TrackReferences && Kind is FormatterKind.Class or FormatterKind.SealedClass;

    /// <summary>
    /// Whether the body is compiled on its own (it calls formatters): see <c>BodyEmitter</c>.
    /// </summary>
    public bool OutOfLineBody => Members.Any(static x => x.Raw == null) || IsCollection;

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
    /// formatters, of at most eight), for a class whose body opens no class body. A subtype writing a base's members
    /// (through its WriteMembers, or itself for a <see cref="InheritanceModel.SharedBase"/>) keeps its body out of line: inlined into a
    /// dispatch of many subtypes, the bodies would use up its inlining budget.
    /// </summary>
    public bool InlineBody => !IsGeneric && !TrackReferences && !RawStruct && !Depth.Nests &&
                              Kind is FormatterKind.Class or FormatterKind.SealedClass && !Keyed && !Tolerant &&
                              Inheritance.BaseFormatter.Length == 0 && !Inheritance.SharedBase && !IsCollection &&
                              Members.Length <= 8 && Members.Count(static x => x.Raw == null) is 1 or 2;

    /// <summary>
    /// Whether the formatter has a <c>__Top</c> twin: sealed classes whose body opens no class body. With no body
    /// open around it (the calls that create their reader or writer), such a body is never deferred and counts no
    /// depth, so the twin skips the depth check and calls the body directly (inlined if <see cref="TopInline"/>,
    /// else the out-of-line body), also for each element of a built-in collection of it.
    /// </summary>
    public bool HasTop => TypeRules.HasTop(Kind, IsGeneric, TrackReferences, RawStruct, Depth.Nests);

    /// <summary>
    /// Whether the <c>__Shallow</c> twin has bodies of its own: a member (or the elements of a collection) is
    /// written by a formatter with twins. Otherwise the twin calls the body, which then has no depth to skip.
    /// </summary>
    public bool ShallowBodies => Depth.Shallow && !RawStruct &&
                                 (Collection is { } c && c.ShallowFormatter != c.Formatter ||
                                  Members.Any(static x => x.ShallowFormatter != x.Formatter));

    /// <summary>
    /// Whether a shallow body writes the base class's members itself rather than through its <c>WriteMembers</c>,
    /// whose formatters count the depth: one of them has a twin.
    /// </summary>
    public bool ShallowInlinesBase => Inheritance.BaseFormatter.Length != 0 &&
                                      Members.Take(Inheritance.BaseMemberCount).Any(static x => x.ShallowFormatter != x.Formatter);

    /// <summary>
    /// Whether <c>Serialize</c>/<c>Deserialize</c> choose the <c>__Shallow</c> twin when the depth leaves room for
    /// the whole nesting bound (<c>NinoObject.SerializeSealed</c> with a bound): a sealed class whose body counts the
    /// depth (<see cref="DepthModel.Nests"/>), which the twin then skips with every check inside. A body that does not nest
    /// checks only its own depth, which the twin cannot skip.
    /// </summary>
    public bool BoundedEntry => Depth.Shallow && Kind == FormatterKind.SealedClass && Depth.Nests && !TrackReferences;

    /// <summary>
    /// Whether the body of a class is one block of raw members (positional, not tracked, not a collection): the
    /// formatter implements <c>INinoRawBodyFormatter</c>, so the type id or null flag in front of the body is written
    /// into the same reservation (one capacity check instead of two).
    /// </summary>
    public bool RawBody => Kind is FormatterKind.Class or FormatterKind.SealedClass && !RawStruct && !VersionTolerant &&
                           !Keyed && !TrackReferences && !IsCollection && Members.Length != 0 &&
                           Members.All(static x => x.Raw != null);

    /// <summary>
    /// Whether the out-of-line read body works on a local copy of the reader, written back at its end: through the
    /// <c>ref</c> parameter, the position is stored after every member and loaded again by the next one (a store to
    /// load forwarding on the critical path of each member, and a load, an operation and a store on ARM64), where
    /// a local the JIT keeps in registers. Only for bodies that open no class body (<see cref="DepthModel.Nests"/> false): a
    /// nested body takes the reader's address, which would keep the local in memory too.
    /// </summary>
    public bool LocalReader => OutOfLineBody && !Depth.Nests && !Keyed && !TrackReferences;

    /// <summary>
    /// Whether the out-of-line write body works on a local copy of the writer, written back at its end, for the
    /// reasons of <see cref="LocalReader"/> (the position, the buffer and its length are otherwise loaded, and the
    /// position stored, for every member). Every member is raw or written by a <see cref="FormatterTraits.Flat"/>
    /// formatter, which keeps the writer's address from calls that are not inlined, so the local stays in registers;
    /// any other member would take its address or need a copy of it around the call, which costs more than the loads
    /// and stores the local saves.
    /// </summary>
    public bool LocalWriter => LocalReader && !IsCollection &&
                               Members.All(static x => x.Raw != null || (x.Traits & FormatterTraits.Flat) != 0);

    /// <summary>
    /// Whether the <c>__Top</c> twin reads a new value with a reader of its own (<c>__ReadTop</c>, see
    /// <c>INinoTopFormatter.Read</c>), for the reasons of <see cref="LocalWriter"/>'s <c>__WriteTop</c>: the body that
    /// reads a new instance (<c>__ReadBodyInline</c>, or <c>__ReadBodyFreshInline</c> when <see cref="SplitFresh"/>) is
    /// then an inlinable method that the out-of-line one calls on its local copy. Not for pooled types, whose new
    /// value comes from the pool first.
    /// </summary>
    public bool ReadTop => HasTop && !TopInline && LocalReader && !Pooled;

    /// <summary>
    /// Whether an out-of-line body reads a new instance in a method of its own (<c>__ReadBodyFresh</c>, see
    /// <see cref="FreshRead"/>), chosen by the inlined <c>ReadBody</c>: one method holding both copies of the member
    /// reads would keep more values live at once (spilled in loops). Bodies inlined wherever the formatter is used
    /// (<see cref="InlineBody"/>) have the reuse copy only: a second copy there only adds locals for the JIT to keep
    /// apart.
    /// </summary>
    public bool SplitFresh => FreshRead && OutOfLineBody && !InlineBody;

    /// <summary>
    /// The formatter's type parameters, "&lt;T1, T2, __F_T1, __F_T2&gt;": the type's, then the formatter of each
    /// (<see cref="TypeNames.FormatterParameter"/>); "" for non-generic types.
    /// </summary>
    public string TypeParameterList => IsGeneric
        ? "<" + string.Join(", ", TypeParameters.Concat(TypeParameters.Select(TypeNames.FormatterParameter))) + ">"
        : "";

    /// <summary>The type's own type parameters, "&lt;T1, T2&gt;", or "".</summary>
    public string TypeArgumentList => IsGeneric ? "<" + string.Join(", ", TypeParameters) + ">" : "";

    /// <summary>The number of the formatter's type parameters (<see cref="TypeParameterList"/>).</summary>
    public int FormatterArity => 2 * TypeParameters.Length;

    /// <summary>The constraints of the formatter's own type parameters, then of their formatters.</summary>
    public string FormatterConstraints => Constraints + string.Concat(TypeParameters.Select(static p =>
        $" where {TypeNames.FormatterParameter(p)} : struct, {TypeNames.Core}INinoFormatter<{p}>"));

    /// <summary>The formatter as its own code names it: its name with its type parameters.</summary>
    public string Self => SelfOf(FormatterName, TypeParameterList);

    /// <summary>A formatter as its own code names it (<see cref="Self"/>, <see cref="DispatchEntry.Self"/>).</summary>
    public static string SelfOf(string formatterName, string typeParameterList) => formatterName + typeParameterList;
}

// Id: runtime type id of the closed type (NinoTypeId), the key of lazy registration.
internal sealed record FormatterRegistration(string TypeName, FormatterRef Formatter, int Id);

// A NinoActivator<T> creator: Creator is a lambda ("static () => new global::Ns.Bag()"); Id as above.
internal sealed record FactoryRegistration(string TypeName, string Creator, int Id);

// How a [NinoType]'s formatter is registered (RegistryEmitter.Statements): a non-generic type's by the registry, a
// closed generic one's (ClosedGenerics) by the usages. Id: wire id; NameId: hash of the runtime name (by-type
// lookups), the same unless [NinoFormerName]. Formatter: its generated formatter, or "" when generated by another
// assembly (then registered by reflection). Concrete: a type that has instances (not an interface or abstract
// class), so registered with NinoPolymorphic under its id; Tracked: trackReferences. Top: registered with its __Top
// twin (TypeModel.HasTop); Fixed: of fixed size (TypeModel.FixedSize).
internal sealed record TypeRegistration(string TypeName, string Formatter, int Id, int NameId, bool Concrete,
    bool Tracked, bool Top = false, bool Fixed = false);

// A top-level NinoSerializer/NinoDeserializer call: the formatter of its type and the creators that formatter needs
// (null when the type is a [NinoType]), and the closed generic [NinoType]s its type names.
internal sealed record UsageRegistration(FormatterRegistration? Formatter, EquatableArray<FactoryRegistration> Factories,
    EquatableArray<TypeRegistration> GenericTypes, EquatableArray<string> LayoutTypes);

// A generated formatter, as the registry lists it. Arity: its number of type parameters (0 if not generic).
// Registration: how the registry registers it (non-generic types only, else null).
internal sealed record RegistryEntry(string FormatterName, int Arity, TypeRegistration? Registration);

// A top-level NinoSerializer/NinoDeserializer call (NinoGenerator.TransformCall): what it registers, and how it is
// intercepted (Roslyn 4.8+ builds, where the project enables interceptors); either may be null.
internal sealed record TopLevelCall(UsageRegistration? Usage, InterceptedCall? Intercepted);

/// <summary>
/// One intercepted top-level call. <see cref="Signature"/> is the interceptor's parameter list and return type,
/// <see cref="Location"/> the arguments of its <c>[InterceptsLocation]</c>; it calls the <c>NinoDirect</c> method
/// <see cref="Method"/> with the type arguments <see cref="TypeName"/> and, unless empty, <see cref="Formatter"/>,
/// and the arguments <see cref="Arguments"/> (<see cref="Call"/>). Calls with the same signature and call share one
/// interceptor. <see cref="Inlinable"/>: the call creates its own reader or writer, so no class body is open around
/// <see cref="Formatter"/>: the call then uses the <c>__Top</c> twin (<see cref="TypeModel.HasTop"/>) of the
/// formatter, or of the element formatters of a built-in collection (<c>List&lt;Small&gt;</c>,
/// <c>Dictionary&lt;int, Small&gt;</c>), whose elements are read and written there too.
/// </summary>
internal sealed record InterceptedCall(string Signature, string Method, string TypeName, FormatterRef? Formatter,
    string Arguments, string Location, bool Inlinable = false, string Where = "")
{
    /// <summary>A call of the <c>NinoDirect</c> method <paramref name="method"/> with <paramref name="formatter"/>, and this call's type and arguments.</summary>
    public string CallWith(string method, FormatterRef? formatter) =>
        $"{method}<{TypeName}{(formatter != null ? ", " + formatter : "")}>({Arguments})";

    /// <summary>The <c>NinoDirect</c> call, without the class name.</summary>
    public string Call => CallWith(Method, Formatter);
}

// A type in the per-assembly subtype dispatch: a base (DispatchesSubtypes), a target (IsDispatchTarget), or both.
// TypeParameterList: a generic base's (TypeModel.TypeParameterList), "" otherwise. RawBody: the target's body is one raw
// block (TypeModel.RawBody), written with its type id in one reservation.
internal sealed record DispatchEntry(string TypeName, string FormatterName, string TypeParameterList, int TypeId,
    bool IsBase, bool IsTarget, EquatableArray<string> BaseClasses, EquatableArray<string> Interfaces,
    EquatableArray<GenericAncestor> GenericAncestors, bool RawBody)
{
    /// <inheritdoc cref="TypeModel.Self"/>
    public string Self => TypeModel.SelfOf(FormatterName, TypeParameterList);
}

// A closed generic [NinoType] ancestor (global name) and its definition (global name, as the definition's TypeModel
// names it).
internal sealed record GenericAncestor(string Type, string Definition);

// RegisteringReferences: one formatter (open name) of each referenced assembly that registers formatters.
internal sealed record GeneratorOptions(string AssemblyName, bool HasModuleInitializer, bool HasUnityEngine,
    bool HasUnityEditor, EquatableArray<string> RegisteringReferences);
