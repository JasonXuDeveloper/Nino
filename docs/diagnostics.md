# Nino diagnostics

All diagnostics are reported by the analyzer (`NinoAnalyzer`) as you type; the source generator itself reports
nothing. Fixes marked 🔧 are offered as code fixes in the IDE (Visual Studio, Rider, VS Code) and can be applied
to a whole document, project or solution at once.

Severities can be changed per project in `.editorconfig`, e.g. `dotnet_diagnostic.NINO006.severity = none`.

| Id | Severity | Summary |
|---|---|---|
| [NINO001](#nino001) | Error | The type cannot be named by generated code 🔧 |
| [NINO002](#nino002) | Error | Type with non-public members (or `[NinoPooled]`) must be partial 🔧 |
| [NINO003](#nino003) | Warning | A subtype of a NinoType does not inherit its attribute 🔧 |
| [NINO004](#nino004) | Error | No usable constructor |
| [NINO005](#nino005) | Error | A `[NinoConstructor]` parameter matches no member |
| [NINO006](#nino006) | Warning | Read-only member is not restored 🔧 |
| [NINO007](#nino007) | Warning | Member type cannot be serialized 🔧 |
| [NINO008](#nino008) | Error | Custom formatter does not match the member type |
| [NINO009](#nino009) | Error | Non-public `[NinoMember]` in a type that does not allow non-public members 🔧 |
| [NINO010](#nino010) | Warning | Redundant `[NinoMember]` 🔧 |
| [NINO011](#nino011) | Warning | Redundant `[NinoIgnore]` 🔧 |
| [NINO012](#nino012) | Error | Member has both `[NinoMember]` and `[NinoIgnore]` 🔧 |
| [NINO013](#nino013) | Error | Duplicate `[NinoMember]` index 🔧 |
| [NINO014](#nino014) | Error | Keyed members hash to the same key |
| [NINO015](#nino015) | Warning | `trackReferences` on a struct 🔧 |
| [NINO016](#nino016) | Error | `trackReferences` on a type re-created from its constructor |
| [NINO017](#nino017) | Error | Invalid `[NinoFactory]` method |
| [NINO018](#nino018) | Error | `[NinoPooled]` on a type that cannot be pooled 🔧 |
| [NINO019](#nino019) | Error | `[NinoPooled]` class does not implement its pool hooks 🔧 |
| [NINO020](#nino020) | Error | Invalid `[assembly: NinoFormatter]` registration |
| [NINO021](#nino021) | Error | `[NinoConstructor]` is not accessible to generated code |
| [NINO022](#nino022) | Warning | A member hidden with `new` cannot be reached by generated code |
| [NINO023](#nino023) | Error | A struct serializes no member |
| [NINO024](#nino024) | Warning | `[NinoFormerName]` on a member of a type that is not keyed |
| [NINO025](#nino025) | Warning | `[NinoFactory]` on a type created through `[NinoConstructor]` |
| [NINO026](#nino026) | Error | More than one `[NinoFormerName]` on a type |
| [NINO027](#nino027) | Warning | `[NinoFormerName]` on a generic type written with its type id |

Besides these, `NinoSuppressor` hides the compiler's "field is never used / never assigned" (CS0169, CS0414,
CS0649) and the IDE's "remove unused private member" / "make field readonly" (IDE0051, IDE0052, IDE0044) for
private members that Nino serializes: on .NET 8+ generated code reaches them through `[UnsafeAccessor]`, which the
compiler cannot see.

## The type

### NINO001

Generated formatters live outside the type, so the type and every type containing it must be `public` or
`internal` (`protected internal` also works), and it cannot be a `static` class or a `ref struct`.
🔧 Make the type public (and any `private`/`protected` containing type).

### NINO002

Before .NET 8 there is no `[UnsafeAccessor]` (and generic types never use it), so non-public members are reached
through an accessor generated inside the type. `[NinoPooled]` classes get generated members as well. Either way the
type and every type containing it must be `partial`. 🔧 Make them partial.

### NINO003

The base type or interface is a NinoType with `allowInheritance: false`, so this subtype is not serialized as
itself. 🔧 Add `[NinoType]`.

## Construction

### NINO004

The deserializer needs a way to create the type: an accessible parameterless constructor, a constructor whose
parameters all match serialized members (by name, ignoring case), or one marked `[NinoConstructor]`.

With C# 11 `required` members, a constructor that is not `[SetsRequiredMembers]` is only usable when every
required member is serialized, assignable by generated code (public or internal) and not one of its
parameters: the object initializer sets them.

### NINO005

A `[NinoConstructor]` constructor or factory method has a parameter (or a name passed to the attribute) that
matches no serialized member.

### NINO006

The type is re-created through a constructor, and this read-only member (its own or inherited) is not one of its
parameters: its serialized value is read and dropped. Reported on the member, once per type that
re-creates it. Add it to the constructor, make it settable, or `[NinoIgnore]` it.
🔧 Add `[NinoIgnore]`.

### NINO021

The constructor or factory method marked `[NinoConstructor]` is called by generated code outside the type, so it
must be `public` or `internal` (`protected internal` also works); a `private` or `protected` one cannot be called.
The type is not constructed by the generated formatter: deserializing it throws `NotSupportedException`.

### NINO025

A type with a `[NinoConstructor]` (a constructor or a static factory method) is re-created through it on every read, so its
`[NinoFactory]` method is never called. Remove one of the two attributes:
`[NinoFactory]` to keep re-creating instances from the constructor, or `[NinoConstructor]` to create instances with
the factory and fill them in place.

## Members

### NINO007

Nino has no formatter for the member type (delegates, `Task`, pointers...). The member is skipped. Mark it
`[NinoIgnore]` or give it a custom formatter. 🔧 Add `[NinoIgnore]`.

User collections end up here when Nino cannot create or fill them: an abstract collection, one without a public
constructor callable without arguments (and no public static `[NinoFactory]` factory), one whose C# 11
`required` members that constructor does not set, a struct implementing `ICollection<T>`, a non-generic collection
(`ArrayList`) or a type implementing `ICollection<T>` for two different `T`.

### NINO008

`[NinoCustomFormatter(typeof(F))]` requires `F` to be a struct implementing `INinoFormatter<T>`, where `T` is exactly the
member type. Generated code calls it as `default(F)`, inlined like the built-in formatters, so it has no instance:
keep any state it needs in static fields.

### NINO009

A non-public (private, protected, internal...) member marked `[NinoMember]` is only serialized when the type sets
`containNonPublicMembers: true`, with or without `autoCollect`. 🔧 Remove `[NinoMember]`, or set
`containNonPublicMembers: true` on the type.

### NINO010

With `autoCollect: true` (the default) every public member is serialized; `[NinoMember]` changes nothing.
(On a non-public member that is not collected, NINO009 is reported instead.) 🔧 Remove it.

### NINO011

With `autoCollect: false` only `[NinoMember]` members are serialized; `[NinoIgnore]` changes nothing. 🔧 Remove it.

### NINO012

A member cannot be both included and ignored. 🔧 Remove `[NinoMember]`, `[NinoIgnore]`, or both.

### NINO013

Indices define the order of members on the wire and must be unique within a type. Reported once per duplicated
index. 🔧 Use the next free index.

### NINO022

A non-public base member is hidden by a member of the same name (`new`) declared by a subtype. Both are
serialized, but generated code can only reach the hidden one through `[UnsafeAccessor]` (.NET 8+, non-generic
types): the partial accessor class inside the type would name the hiding member, and cannot reach a `protected`
base member through a cast. The hidden member is skipped. Rename one of the members, or make it public.

### NINO023

A positional `[NinoType]` struct has no header, so one without serialized members (every member `[NinoIgnore]`d
or skipped) is written as zero bytes. On its own it round-trips, but a list, array or dictionary of it cannot be
read back: collection counts are checked against the bytes left, so that hostile data cannot trigger huge
allocations, and every element must take at least one byte. Serialize a member, or mark the struct
`NinoFormat.VersionTolerant` or `NinoFormat.Keyed` (each instance then writes its 4-byte length). A struct without any instance field is
written as its raw memory (1 byte) and is not reported.

## Features

### NINO014

Keyed members (`format: NinoFormat.Keyed`) are identified by a 32-bit hash of their name, inherited members included; two names
collide. Rename one of them. A member's `[NinoFormerName]`s are keys too: a former name that is another member's
current or former name (or hashes like one) would make old data ambiguous, and is reported on the member that
claims it second. A former name equal to the member's own name is ignored.

### NINO024

Member-level `[NinoFormerName]` lets keyed types read data written before a member was renamed. Positional and
version-tolerant types write no member names (members are matched by position), so there it has no effect. Remove
it, or give the type `format: NinoFormat.Keyed`.

### NINO026

A type's `[NinoFormerName]` gives the earlier name its type id is computed from, so a type takes one. With two, the
generator would use the first and `NinoTypeId.Of` would throw `AmbiguousMatchException`. Keep the name the type had
when the data you still read was written. (Members of keyed types take several, one per earlier name.)

### NINO027

A type id is computed from the type's name, and `[NinoFormerName]` replaces that name. On a generic type, every
instantiation then takes the same id: `Box<int>` and `Box<string>` collide once both are written with their type id,
which happens when the type is written through a base class, an interface or `object`. Generic structs, and sealed
generic classes with no `[NinoType]` base class or interface, are not written that way, so the warning leaves them
alone. Remove the former name, or keep
former names on non-generic types.

### NINO015

Reference tracking applies to classes; on a struct (including one implementing a tracked interface) it is ignored.
🔧 Remove `trackReferences`.

### NINO016

On a class that is re-created from constructor arguments or read-only members (records with positional
parameters, for example), a reference to the instance cannot be resolved before it exists. Tracking is inherited, so
this also applies to subtypes of a tracked type. Make the members assignable.

### NINO017

A `[NinoFactory]` factory must be `public static`, non-generic, callable without arguments (parameterless, or every
parameter optional, like ET's `static T Create(bool isFromPool = false)`) and return the containing type.

## Pooling

### NINO018

`[NinoPooled]` makes Nino deserialize into reused instances, so the type must be a sealed class whose members are
all assignable: not a struct, an abstract or non-sealed class, or a type re-created from its constructor (such as a
positional record). 🔧 Make the class sealed (offered for non-abstract classes and records).

### NINO019

A `[NinoPooled]` class must implement the two partial methods the generator declares:
`private static partial IEnumerable<T> OnNinoPoolWarmup()`, called once before Nino first deserializes the type (the instances it returns go straight into the pool), and
`private partial void OnNinoPoolReturn()`, called by `ReturnToPool()` to release what must not outlive the instance's use.
`OnNinoPoolReturn` may be empty; `OnNinoPoolWarmup` may return `null` or an empty sequence to start with an empty pool. 🔧 Implement pool hooks (adds an empty `OnNinoPoolReturn` and a warmup that returns `Array.Empty<T>()`).

## Formatter registrations

### NINO020

An `[assembly: NinoFormatter(typeof(F))]` registration Nino cannot use, so it is ignored. `F` must be a struct
(not nested in a generic type) implementing `INinoFormatter<P>` exactly once, passed as itself (`typeof(F)`) or, if
it is generic, as its open generic definition (`typeof(F<,>)`). Every type parameter of `F` must be inferable: it occurs in `P`, in a type constraint of an
inferable parameter (`where TList : List<T>`), or is constrained to `INinoFormatter<E>` for an inferable `E`.
`targets` only apply when `P` is a bare type parameter.
