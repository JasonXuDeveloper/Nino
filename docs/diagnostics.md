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
| [NINO017](#nino017) | Error | Invalid `[NinoRefDeserialization]` method |
| [NINO018](#nino018) | Error | `[NinoPooled]` on a type that cannot be pooled 🔧 |
| [NINO019](#nino019) | Error | `[NinoPooled]` class does not implement its pool hooks 🔧 |

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

### NINO005

A `[NinoConstructor]` constructor or factory method has a parameter (or a name passed to the attribute) that
matches no serialized member.

### NINO006

The type is re-created through a constructor, and this read-only member (its own or inherited) is not one of its
parameters: its serialized value is read and dropped. Reported on the member, once per type that
re-creates it. Add it to the constructor, make it settable, or `[NinoIgnore]` it.
🔧 Add `[NinoIgnore]`.

## Members

### NINO007

Nino has no formatter for the member type (delegates, `Task`, pointers...). The member is skipped. Mark it
`[NinoIgnore]` or give it a custom formatter. 🔧 Add `[NinoIgnore]`.

### NINO008

`[NinoCustomFormatter(typeof(F))]` requires `F` to be either a struct implementing `INinoFormatter<T>` (called directly,
like the built-in formatters) or a non-abstract class deriving from `NinoFormatter<T>` with a public parameterless
constructor (one shared instance), where `T` is exactly the member
type.

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

## Features

### NINO014

Keyed members (`keyed: true`) are identified by a 32-bit hash of their name, inherited members included; two names
collide. Rename one of them.

### NINO015

Reference tracking applies to classes; on a struct (including one implementing a tracked interface) it is ignored.
🔧 Remove `trackReferences`.

### NINO016

On a class that is re-created from constructor arguments or read-only members (records with positional
parameters, for example), a reference to the instance cannot be resolved before it exists. Tracking is inherited, so
this also applies to subtypes of a tracked type. Make the members assignable.

### NINO017

A `[NinoRefDeserialization]` factory must be `public static`, parameterless and return the containing type.

## Pooling

### NINO018

`[NinoPooled]` makes Nino deserialize into reused instances, so the type must be a sealed class whose members are
all assignable: not a struct, an abstract or non-sealed class, or a type re-created from its constructor (such as a
positional record). 🔧 Make the class sealed (offered for non-abstract classes and records).

### NINO019

A `[NinoPooled]` class must implement the two partial methods the generator declares:
`private static partial IEnumerable<T> OnNinoPoolWarmup()`, called once before Nino first deserializes the type (the instances it returns go straight into the pool), and
`private partial void OnNinoPoolReturn()`, called by `ReturnToPool()` to release what must not outlive the instance's use.
Empty bodies are fine. 🔧 Implement pool hooks (adds empty bodies for the missing ones).
