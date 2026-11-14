# Nino diagnostics

All diagnostics are reported by the analyzer (`NinoAnalyzer`) as you type; the source generator itself reports
nothing. Fixes marked 🔧 are offered as code fixes in the IDE (Visual Studio, Rider, VS Code) and can be applied
to a whole document, project or solution at once. The tools that generate other languages report their own
([below](#code-generation-for-other-languages)).

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
| [NINO008](#nino008) | Error | `[NinoMemberFormatter]` does not match the member type |
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
| [NINO024](#nino024) | Warning | `[NinoFormerName]` on a member of a type that is not keyed 🔧 |
| [NINO025](#nino025) | Warning | `[NinoFactory]` on a type created through `[NinoConstructor]` 🔧 |
| [NINO026](#nino026) | Error | More than one `[NinoFormerName]` on a type |
| [NINO027](#nino027) | Warning | `[NinoFormerName]` on a generic type written with its type id |
| [NINO028](#nino028) | Warning | `[NinoMember]` on a member that is never serialized 🔧 |
| [NINO029](#nino029) | Error | `Vector<T>` cannot be serialized |
| [NINO030](#nino030) | Warning | `[NinoUtf8]` has no effect 🔧 |
| [NINO031](#nino031) | Info | `[NinoMemberFormatter(typeof(Utf8StringFormatter))]`: use `[NinoUtf8]` 🔧 |
| [NINO032](#nino032) | Error | `WEAK_VERSION_TOLERANCE` does not make types version tolerant 🔧 |
| [NINO033](#nino033) | Warning | `[NinoUtf8]`, `[NinoMemberFormatter]` or `[NinoIgnore]` on an interface member has no effect 🔧 |
| [NINO034](#nino034) | Warning | A layout change breaks data written before or after it (layout baseline) 🔧 |
| [NINO035](#nino035) | Warning | A layout change loses data without an error (layout baseline) 🔧 |
| [NINO036](#nino036) | Info | A layout change is compatible, with a condition (layout baseline) 🔧 |
| [NINO037](#nino037) | Warning | The layout baseline cannot be read |
| [NINO038](#nino038) | Error | A serialization callback has the wrong signature |
| [NINO039](#nino039) | Error | A serialization callback is not accessible to generated code 🔧 |
| [NINO040](#nino040) | Warning | A serialization callback on a type that is not a `[NinoType]` is never called 🔧 |
| [NINO041](#nino041) | Error | Two methods for one serialization callback |
| [NINO042](#nino042) | Error | A serialization callback that runs on the value is static |
| [NINO043](#nino043) | Error | Instance `[NinoOnDeserializing]` on a type created from its members |
| [NINO044](#nino044) | Error | A serialization callback on a struct copied as raw memory |

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

Where generated code cannot reach a non-public member through `[UnsafeAccessor]`, it reaches it through an accessor
generated inside the type: before .NET 8 (Unity, `netstandard`), for generic types before .NET 9
(the compilation defines no `NET9_0_OR_GREATER`), and on .NET 9+ for a member a generic type inherits from a generic
base whose type arguments are not the type's own first type parameters in order (`class D<T> : Base<List<T>>`,
`class D<T> : Base<int>`; `class D<T, U> : Base<T>` is fine), since the runtime matches the accessor against the
member's declaration. `[NinoPooled]` classes get generated members as well. Either way the type and every type
containing it must be `partial`. 🔧 Make them partial.

### NINO003

The base type or interface is a NinoType with `inheritedBySubtypes: false`, so this subtype gets no formatter of its
own: writing one (through the base, an interface or `object`) throws `InvalidOperationException`. 🔧 Add `[NinoType]`.

## Construction

### NINO004

The deserializer needs a way to create the type: an accessible parameterless constructor, a constructor whose
parameters all match serialized members (by name, ignoring case), or one marked `[NinoConstructor]`.

With C# 11 `required` members, a constructor that is not `[SetsRequiredMembers]` is only usable when every
required member is serialized, assignable by generated code (public or internal) and not one of its
parameters: the object initializer sets them.

### NINO005

A `[NinoConstructor]` constructor or factory method has a parameter (or a name passed to the attribute) that
matches no serialized member, so the type cannot be created when read: deserializing it throws
`NotSupportedException`. Name the parameter after a member (any case), or list the members in
`[NinoConstructor(nameof(A), ...)]`.

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
the factory and fill them in place. 🔧 Remove `[NinoFactory]` (what reading does now).

## Members

### NINO007

Nino has no formatter for the member type (delegates, `Task`, pointers...). The member is skipped. Mark it
`[NinoIgnore]` or give it a custom formatter. 🔧 Add `[NinoIgnore]`. A member of a base type is reported once, by the
type that declares it, not again by each subtype; a subtype reports it only when the declaring type is not built in
this compilation (a closed generic base, a base in another assembly). NINO029 follows the same rule.

User collections end up here when Nino cannot create or fill them: an abstract collection, one without a public
constructor callable without arguments (and no public static `[NinoFactory]` factory), one whose C# 11
`required` members that constructor does not set, a struct implementing `ICollection<T>`, a non-generic collection
(`ArrayList`) or a type implementing `ICollection<T>` for two different `T`.

It is also reported for a generic `[NinoType]` closed over a type Nino has no formatter for, where a member of the
generic type uses that type parameter (`Box<Action>`, also inside `List<Box<Action>>`, `Box<Action>[]` or another
generic). That member is not skipped: it is written, and null works, but writing any `Box<Action>` throws
`InvalidOperationException` ("no formatter for System.Action"). Only a type argument the closed type's formatter
writes is reported: one that a serialized member writes (its own or a `[NinoType]` base's, directly, in an array, a
collection, a dictionary, a tuple or `Nullable`, or through another generic `[NinoType]` that writes it), the elements
of a collection class, or what a generic subtype writes of it. A slot (an interface, an abstract or unsealed class)
holds the generic subtypes whose base matches its argument, at any depth of inheritance, and they write the part of
the argument they match: with `class TagShelf<U> : Shelf<Tag<U>> { public U Value; }`, a `Shelf<Tag<Action>>` is
reported though `Tag<T>` writes no `T`, and a `Shelf<Box<Action>>` is not when no subtype of that shape writes it.
The argument must be one the subtype can give its base: with `class Fixed<U> : Shelf<KeyValuePair<int, U[]>>` a
`Shelf<KeyValuePair<long, Action[]>>` is not reported, nor with `class Same<U> : Shelf<KeyValuePair<U, U>>` a
`Shelf<KeyValuePair<int, Action>>`. These are the closed subtypes the generated registry maps from their type ids. A
type parameter used only by `[NinoIgnore]` members, methods, constraints or a nested generic that never writes it is
not (`Tag<Action>` of `class Tag<T> { int X; }`, nor `Outer<Action>` of `class Outer<T> { Tag<T> Inner; }`, nor
anything inside it, `Tag<Box<Action>>`).

Types in referenced assemblies are read from their symbols, with two limits. A compilation sees only the public and
protected members of a referenced class, so a private serialized member is read from the layout Nino publishes for
the type (`[assembly: NinoTypeLayout]`): every type argument such a member's type names counts as written, and every
type argument of a referenced generic class without a layout does. Subtypes are searched in the compilation and in
every referenced assembly that is the base's or references it; a subtype declared in an assembly that references this
one, and a subtype's type parameter its base does not name (`class S<U, W> : Shelf<U>` writing `W`), cannot be seen
from the slot, and are not reported there. A subtype that holds its base over its own type parameter matches
arguments nested without end (`Tag<Tag<...>>`): beyond a few levels, every type inside the argument counts. A generic
that is not a `[NinoType]` writes every type argument: Nino.Core's formatters (collections, dictionaries, `Nullable`,
tuples, `KeyValuePair`) write them all, and refuse any other such generic with `Vector<T>` in an argument.

### NINO008

`[NinoMemberFormatter(typeof(F))]` requires `F` to be a struct implementing `INinoFormatter<T>`, where `T` is exactly the
member type. Generated code calls it as `default(F)`, inlined like the built-in formatters, so it has no instance:
keep any state it needs in static fields.

### NINO009

A non-public (private, protected, internal...) member marked `[NinoMember]` is only serialized when the type sets
`includeNonPublicMembers: true`, with or without `autoCollect`. 🔧 Remove `[NinoMember]`, or set
`includeNonPublicMembers: true` on the type.

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
serialized, but generated code can only reach the hidden one through `[UnsafeAccessor]` (.NET 8+; generic types
.NET 9+, as NINO002 describes): the partial accessor class inside the type would name the hiding member, and cannot reach a `protected`
base member through a cast. The hidden member is skipped. Rename one of the members, or make it public.

### NINO023

A positional `[NinoType]` struct has no header, so one without serialized members (every member `[NinoIgnore]`d
or skipped) is written as zero bytes. On its own it round-trips, but a list, array or dictionary of it cannot be
read back: collection counts are checked against the bytes left, so that hostile data cannot trigger huge
allocations, and every element must take at least one byte. Serialize a member, or mark the struct
`NinoFormat.VersionTolerant` or `NinoFormat.Keyed` (each instance then writes its 4-byte length). A struct without any instance field is
written as its raw memory (1 byte) and is not reported.

### NINO028

`[NinoMember]` on a member Nino never serializes, whatever the type's options: a static field or property, a
constant, an indexer, a fixed-size buffer, a property without a getter or implemented explicitly for an interface (for a `[NinoType]` interface, its member is
written in its place, with the interface member's attributes: put `[NinoMember]` there),
a property without a setter that no constructor takes (`int Sum => A + B;`, or `int Id { get; }` without a
constructor parameter `id`), or a backing field (`[field: NinoMember]`; mark the property). Nothing is written for
it. This is the only diagnostic about that `[NinoMember]` (not NINO009, NINO010 or NINO013). To serialize a get-only
auto-property, give a constructor a parameter of its name; otherwise 🔧 remove `[NinoMember]`.

### NINO029

A member, a `NinoSerializer`/`NinoDeserializer` call, or Nino.Core's own formatters and `Writer`/`Reader` methods used
by hand (`new UnmanagedFormatter<Vector<int>>()`, `default(...)`, a field, property or local of that type, which a
formatter struct needs no constructor call to fill, `[NinoMemberFormatter(typeof(...))]`, `writer.Write(vector)`,
`reader.Read(out Vector<int> v)`) need `System.Numerics.Vector<T>`: the type is one or holds
one (as an element, a type argument, a base type or a field of a plain struct), and `Vector<T>` is 16, 32 or 64 bytes
by the CPU, so Nino serializes no such type; the runtime throws `NotSupportedException` for it. A generic
`[NinoType]` counts when its formatter writes the type argument, by the same rule as NINO007's type arguments:
`Box<Vector<int>>` of `class Box<T> { public T Value; }` is reported, and so is `ListBox<Vector<short>>` of
`class ListBox<T> : Box<List<T>>`, and `Shelf<Tag<Vector<int>>>` when a subtype `class TagShelf<U> : Shelf<Tag<U>>`
writes `U`; `Outer<Vector<int>>` of `class Outer<T> { Tag<T> Inner; }` is not when `Tag<T>` writes no `T`. The message reads: "... cannot be serialized: '{type}' is a Vector<T> (or holds
'...'), and Vector<T> is 16, 32 or 64 bytes by the CPU; store a fixed number of lanes (an array, a fixed buffer,
Vector128<T> or Vector256<T>) and view them as Vector<T> in [NinoIgnore] members". A formatter of your
own that writes the lanes its way is never reported, nor a Nino.Core collection formatter over it. Store a fixed
number of lanes and expose the vectors through `[NinoIgnore]` members: [pitfalls.md, "`Vector<T>` is not
serializable"](pitfalls.md#vectort-is-not-serializable) has why and the example.
There is no code fix: the stored form is yours to choose.

### NINO030

`[NinoUtf8]` changes nothing on this member, for one of three reasons, which the message names:

- the member's type holds no string the built-in formatters write ("its type 'int' holds no string"), or holds them
  only inside a `[NinoType]`, `object` or type parameter, whose own formatter writes them by its own members'
  attributes ("it does not reach the strings a [NinoType], object or type parameter in its type 'X' writes");
- a `[NinoMemberFormatter]` writes the member, which decides the encoding itself;
- the member is never serialized: it is `[NinoIgnore]`, not public in a type without `includeNonPublicMembers`,
  get-only with no constructor parameter, static, an explicit interface implementation (a `[NinoType]` interface's
  member is written in its place, with the interface member's attributes), or lacks `[NinoMember]` in an
  `autoCollect: false` type.

`[NinoUtf8]` reaches every string the built-in formatters write in the member's type: the member itself, the elements
of arrays and collections, dictionary keys and values, tuple items. The bytes are the same without it. Reported once,
by the type that declares the member; a member with another finding (NINO008, NINO009, ...) is left to that one,
whose fix may change what the attribute does. On an interface member it is [NINO033](#nino033). 🔧 Remove it.

### NINO031

`[NinoMemberFormatter(typeof(Utf8StringFormatter))]` on a string member: `Utf8StringFormatter` is what generated code
uses for `[NinoUtf8]`, and the generator reads this attribute as `[NinoUtf8]` (the same bytes, the same layout,
`utf8`, and the same code). `[NinoUtf8]` says it directly and also reaches the strings inside collections.
🔧 Use `[NinoUtf8]` (with `[NinoUtf8]` already on the member, the formatter attribute is removed). On a member that is
not a string, the formatter cannot write it: NINO008.

### NINO033

`[NinoUtf8]`, `[NinoMemberFormatter]` or `[NinoIgnore]` on a member of an interface. A type implementing the
interface writes its own member, with that member's attributes, so these never reach the bytes: the string stays
UTF-16, the formatter is not called, and the "ignored" member is written. Only the interface member's
`[NinoMember(index)]` counts: it places the member in the implementing types. The exception is a type that implements
the member explicitly (`int IFoo.X { get; set; }`) or takes its default implementation: the interface's member is
written there, with the interface member's attributes, so they take effect and NINO033 is not reported when a type of
the compilation does so. A member with another finding (NINO008, NINO011) is left to that one. 🔧 Move the attribute
to the members implementing it implicitly in the solution's source (a member that already has one keeps its own; a
record's parameter gets `[property: ...]`), or remove it when nothing implements the member; when the solution has an
explicit implementation, the fix copies it to the implicit ones and keeps it on the interface.

## Features

### NINO014

Keyed members (`format: NinoFormat.Keyed`) are identified by a 32-bit hash of their name, inherited members included; two names
collide. Rename one of them. A member's `[NinoFormerName]`s are keys too: a former name that is another member's
current or former name (or hashes like one) would make old data ambiguous, and is reported on the member that
claims it second. A former name equal to the member's own name is ignored.

### NINO024

Member-level `[NinoFormerName]` lets keyed types read data written before a member was renamed. Positional and
version-tolerant types write no member names (members are matched by position), so there it has no effect. Remove
it, or give the type `format: NinoFormat.Keyed`. 🔧 Remove the member's `[NinoFormerName]`s.

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

### NINO032

The project defines `WEAK_VERSION_TOLERANCE`, which until 4.0 made every positional `[NinoType]` of its assembly
version tolerant; it no longer does ("`WEAK_VERSION_TOLERANCE` does not make NinoType 'Player' version tolerant (it
did before 4.0)"). The type is now written positionally, without the body length, so it can no longer read the data it
wrote as version tolerant. Reported on each positional type's own `[NinoType]`; a type that takes its `[NinoType]`
from another type of the project is covered by that type's fix. 🔧 Add `format: NinoFormat.VersionTolerant` (fix all:
every type of the document, project or solution), which generates exactly the code the symbol did, so the bytes stay.
A type that takes its `[NinoType]` from a type in another assembly is reported on the type: 🔧 declare
`[NinoType(..., format: NinoFormat.VersionTolerant)]` on it with the options it took (the message spells them out).
Then remove the symbol from the project. Keyed and version-tolerant types are not affected.

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

## The layout baseline

NINO034 to NINO037 are reported only in an assembly that has a layout baseline: `[assembly: NinoLayouts(@"...")]`,
usually in a file of its own, `NinoLayouts.cs`. It holds, as a small TOML document, the wire layout (the
`[NinoTypeLayout]` the generator publishes, [wire-format spec §8](spec/wire-format.md#8-type-layout-metadata-and-its-grammar))
of every version-tolerant and keyed `[NinoType]` of the assembly and of every type their members reach (positional
types and polymorphic subtypes included): the types whose bytes outlive a build. A positional type nothing stored
reaches is left out; its bytes are meant for one build. The analyzer compares each of those types with its section
as you type and reports a change on the member or type that made it, with the readers it breaks: this build reading
data written before the change, and builds before the change reading data written after it (clients not updated yet,
old nodes in a rolling deployment).

```csharp
// NinoLayouts.cs
[assembly: Nino.Core.NinoLayouts(@"
['MyGame.Save.PlayerData']
format = 'Keyed'
header = 'Presence'
id = 0x3DEFB1B5
deferrable = true
members = [
  { name = 'Health', type = 'int', encoding = 'raw(4)', key = 0x99A16761 },
  { name = 'Name', type = 'string', encoding = 'string', key = 0x3085B3E2, former = [0x93A29D95] },
]
")]
```

- One table per type, named by its runtime-form name (`Ns.Outer+Inner`, `Ns.Box<T>`), sorted; `trackReferences`
  only when true, `raw` (the raw encoding) only for a type written as raw memory, `elements` only for a `[NinoType]`
  collection; the members in the order they are written, one a line. Strings are TOML literal strings (single
  quotes), so nothing in it needs escaping in a C# verbatim string.
- `NinoLayoutsAttribute` is `[Conditional("NINO_LAYOUTS")]`: the compiler checks it and the analyzer reads it from
  source, but it is never written to the assembly.
- Create it with the refactoring **Create the layout baseline (NinoLayouts.cs)**, offered on a `[NinoType]`
  declaration, or with `nino-gen layouts --assembly <dll> --out NinoLayouts.cs` ([codegen.md](codegen.md#layout-baselines));
  write it again (accepting every change) with the refactoring **Write the layout baseline again** or the same
  command. `nino-gen layouts ... --check` runs the same rules on a built assembly, for CI without an IDE, and exits
  with 1 on NINO034 or NINO035.
- To fail a build on them, raise their severity: `dotnet_diagnostic.NINO034.severity = error`.
- What a layout does not show is not checked: enum values renumbered (an enum is `raw(4)`), what a custom formatter
  writes, and whether old data still holds instances of a polymorphic subtype that was removed.

| Change | This build reads old data | Old builds read new data | Reported |
|---|---|---|---|
| A type removed (no type has its name or its type id) | breaks | breaks | NINO034 on its section 🔧 accept |
| Type id changed (renamed or moved) where the id is written: a `TypeId` header, or a subtype | breaks | breaks | NINO034 🔧 `[NinoFormerName("Old.Name")]` |
| `format` or `trackReferences` changed | breaks | breaks | NINO034 🔧 accept |
| `sealed` added or removed (header `Presence` ↔ `TypeId`) | breaks | breaks | NINO034 🔧 restore it |
| A raw struct's size or fields changed | breaks | breaks | NINO034 🔧 accept |
| `deferrable` changed | breaks only for data nested deeper than 128 bodies | same | NINO036 🔧 accept |
| Positional (reached by stored data): any member written differently | breaks | breaks | NINO034 🔧 accept |
| Version tolerant: members appended | compatible | compatible | none |
| Version tolerant: a member removed, moved, inserted or written differently | breaks | breaks | NINO034 🔧 move an inserted member to the end |
| Keyed: a member added | compatible | compatible | none |
| Keyed: a member removed | its value in old data is skipped | old builds read their default | NINO036 🔧 accept |
| Keyed: renamed with `[NinoFormerName]` | compatible | old builds do not know the new key | NINO036 (deploy readers first) 🔧 accept |
| Keyed: renamed without `[NinoFormerName]` | value silently lost | value silently lost | NINO035 🔧 `[NinoFormerName("Old")]` |
| Keyed: the same key written differently | breaks | breaks | NINO034 🔧 accept |

A member written as `type(T)` (a `[NinoType]`) is compared by `T`'s name, and a raw struct with a section of its own
by its type: what changed inside them is reported once, on their own section. A type renamed or moved is matched to
its old section by its type id (a `[NinoFormerName]` kept it), else by its simple name when one type of that name went
and one came. Accepting a change (🔧 **Accept the change into the layout baseline**, on every finding; Fix All
accepts them all at once) rewrites that type's section in the baseline, so the change shows in the review of the pull
request.

### NINO034

A change to a type in the layout baseline that makes data unreadable on one side of it, or both: the message names the
member (or the type), what changed, and which readers break. Undo the change the way the fix says (a former name, `sealed`
back, the member moved to the end), or, when old data or old builds no longer matter, accept it into the baseline. Also
reported on the section of a type that is no longer a `[NinoType]` of the assembly (removed, or renamed or moved without a
match). 🔧 Accept the change; and, as the finding says, add `[NinoFormerName("Old.Name")]` to a renamed or moved type,
make the class sealed again or not sealed, or move a member inserted into a version-tolerant type to the end of the
type (offered when the type collects its members in declaration order and is declared in one place).

### NINO035

A keyed member that looks renamed without `[NinoFormerName]`: a member gone and a new one written the same way (at the
same place, or the only such one). Its key is the hash of its name, so the new key reads nothing from old data (the
member keeps its default) and old builds skip it: data is lost on both sides, without an error. 🔧 Add
`[NinoFormerName("Old")]` (old data is then read; old builds still do not know the new key: NINO036), or accept the
change.

### NINO036

A compatible change with a condition: a keyed member removed (its value in old data is skipped, and old builds read
their default for it), a keyed member renamed with `[NinoFormerName]` (this build reads old data, but builds before it
do not know the new key: in a rolling deployment, deploy the builds that read the new name before any that writes it),
or `deferrable` changed (data nested deeper than 128 bodies of the type is laid out differently). 🔧 Accept the change.

### NINO037

The string of `[assembly: NinoLayouts]` is not a baseline Nino can read (the message gives the reason and the line), so
nothing is checked. Correct the line, or write the baseline again (refactoring, or `nino-gen layouts`).

## Serialization callbacks

NINO038 to NINO044 check the methods marked `[NinoOnSerializing]`, `[NinoOnSerialized]`, `[NinoOnDeserializing]` and
`[NinoOnDeserialized]`, which the generated formatter calls directly around writing and reading a value
([design](v4-design/runtime.md#serialization-callbacks)). A method a rule reports is never called; the generator leaves it out.

### NINO038

A callback is a non-generic method that takes no parameters and returns `void`: the message names what is wrong (it
takes parameters, returns a value, is generic, or is not an ordinary method). Move the work into such a method.

### NINO039

The formatter is a struct of its own (in the `NinoGen` namespace), outside the type, so it calls the method like any
other code of the assembly: it must be `public`, `internal` or `protected internal`. A `private` or `protected` callback
cannot be called. 🔧 Make it internal (not offered for an override, a partial method or an explicit interface
implementation). A callback a base class in another assembly declares `internal` is reported on the subtype whose
formatter cannot call it: make it `public` there.

### NINO040

The attribute is on a method of a class or struct that is not a `[NinoType]` (and takes no `[NinoType]` from a base
class or interface), or of an interface: Nino only calls the callbacks of the type it writes and of its `[NinoType]`
base classes. Mark the type `[NinoType]`, or remove the attribute. 🔧 Remove the attribute (it has no effect).

### NINO041

A type marks two methods with the same callback attribute. Nino calls one method per callback and type (the first
declared), so merge them into one. Base classes may declare their own: those are called too, base classes first.

### NINO042

`[NinoOnSerializing]`, `[NinoOnSerialized]` and `[NinoOnDeserialized]` run on the value written or read, so they must
be instance methods. Only `[NinoOnDeserializing]` may be static: it runs before the members are read, and a static one
also before the instance exists.

### NINO043

An instance `[NinoOnDeserializing]` runs on the instance the members are read into. A type Nino creates from the
members it reads (a constructor that takes them, as records with a primary constructor; init-only or readonly
members) has no instance until they are read. Make the method static, or do the work in `[NinoOnDeserialized]`, which
runs on the new instance. Reported on the method when the type declares it, else on the type created that way.

### NINO044

A positional `[NinoType]` struct of unmanaged members only is written as its raw memory, and an array or list of it as
one copy: no code of the type runs, so its callbacks would never be called. Making it run code would change its
bytes, so Nino does not: call the work from a callback of the type that holds the struct, or give the struct a format
that is written member by member (`NinoFormat.VersionTolerant`), which changes its bytes (a layout change).

## Code generation for other languages

`nino-gen` (the Nino.Codegen.Tool command, and the Nino.Codegen build package, which runs it after each compile) reports
in MSBuild's message format, outside the analyzer:

- `NINOGEN001`: error. The arguments are invalid (an unknown option, an option of another language, a value it does
  not take) or the assembly cannot be read; the tool exits with code 1 and the build fails.
- `NINOGEN002`: warning. A type is left out because the target language cannot express it (in Go and Rust, a generic
  type that holds itself with other type arguments; in TypeScript, a `DateTimeOffset` member; ...), with the reason;
  see [What Is Not Generated](https://nino.xgamedev.net/en/docs/v4/advanced/codegen#not-generated).
- `NINOGEN003`: warning, from the build. The project sets Nino.TypeScript's `NinoTypeScript*` properties: without a
  `<NinoCodegen>` item they stand for `<NinoCodegen Include="typescript" ... />`, next to one they are ignored. Declare
  the item instead ([Migrating from Nino.TypeScript](https://nino.xgamedev.net/en/docs/v4/advanced/typescript#from-ninotypescript)).
- `NINOGEN004`: warning, from the build. The project references Nino.Codegen but declares no `<NinoCodegen>` item, so
  nothing is written; add one, or set `NinoCodegenEnabled` to `false`.

Nino.TypeScript's `NINOTS001` and `NINOTS002` (4.0 release candidates up to rc.7) are `NINOGEN001` and `NINOGEN002`.
