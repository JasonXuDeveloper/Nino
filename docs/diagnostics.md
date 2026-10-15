# Nino diagnostics

All diagnostics are reported by the analyzer (`NinoAnalyzer`) as you type; the source generator itself reports
nothing. Fixes marked 🔧 are offered as code fixes in the IDE (Visual Studio, Rider, VS Code) and can be applied
to a whole document, project or solution at once.

Severities can be changed per project in `.editorconfig`, e.g. `dotnet_diagnostic.NINO014.severity = none`.

| Id | Severity | Summary |
|---|---|---|
| [NINO001](#nino001) | Error | No usable constructor, or a `[NinoConstructor]` parameter that matches no member |
| [NINO002](#nino002) | Error | The type cannot be named by generated code |
| [NINO003](#nino003) | Warning | A subtype of a NinoType does not inherit its attribute |
| [NINO004](#nino004) | Warning | Redundant `[NinoMember]` |
| [NINO005](#nino005) | Warning | Redundant `[NinoIgnore]` |
| [NINO006](#nino006) | Error | Member has both `[NinoMember]` and `[NinoIgnore]` |
| [NINO007](#nino007) | Error | Private `[NinoMember]` in a type that does not allow non-public members |
| NINO008 | — | Retired in v4: nested types may contain non-public members |
| [NINO009](#nino009) | Error | Duplicate `[NinoMember]` index |
| [NINO010](#nino010) | Error | Custom formatter does not match the member type |
| [NINO011](#nino011) | Warning | Member type cannot be serialized |
| [NINO012](#nino012) | Error | Invalid `[NinoRefDeserialization]` method |
| [NINO013](#nino013) | Error | Type with non-public members must be partial |
| [NINO014](#nino014) | Warning | Read-only member is not restored |
| [NINO015](#nino015) | Error | Keyed members hash to the same key |
| [NINO016](#nino016) | Warning / Error | `trackReferences` cannot apply |

Besides these, `NinoSuppressor` hides the compiler's "field is never used / never assigned" (CS0169, CS0414,
CS0649) and the IDE's "remove unused private member" / "make field readonly" (IDE0051, IDE0052, IDE0044) for
private members that Nino serializes: on .NET 8+ generated code reaches them through `[UnsafeAccessor]`, which the
compiler cannot see.

## NINO001

The deserializer needs a way to create the type: an accessible parameterless constructor, a constructor whose
parameters all match serialized members (by name, ignoring case), or one marked `[NinoConstructor]`. Also reported
when a `[NinoConstructor]` parameter (or a name passed to it) matches no serialized member.

## NINO002

Generated formatters live outside the type, so the type and every type containing it must be `public` or
`internal`, and it cannot be a `static` class. 🔧 Make the type public (and any `private`/`protected` containing
type).

## NINO003

The base type or interface is a NinoType with `allowInheritance: false`, so this subtype is not serialized as
itself. Add `[NinoType]`. 🔧 Add `[NinoType]`.

## NINO004

With `autoCollect: true` (the default) every public member is serialized; `[NinoMember]` changes nothing.
🔧 Remove it.

## NINO005

With `autoCollect: false` only `[NinoMember]` members are serialized; `[NinoIgnore]` changes nothing. 🔧 Remove it.

## NINO006

🔧 Remove `[NinoMember]`, `[NinoIgnore]`, or both.

## NINO007

🔧 Remove `[NinoMember]`, or set `containNonPublicMembers: true` on the type.

## NINO009

Indices define the order of members on the wire and must be unique within a type. 🔧 Use the next free index.

## NINO010

`[NinoCustomFormatter(typeof(F))]` requires `F` to derive from `NinoFormatter<T>`, where `T` is the member type.

## NINO011

Nino has no formatter for the member type (delegates, `Task`, pointers...). The member is skipped. Mark it
`[NinoIgnore]` or give it a custom formatter. 🔧 Add `[NinoIgnore]`.

## NINO012

A `[NinoRefDeserialization]` factory must be `public static`, parameterless and return the containing type.

## NINO013

Before .NET 8 there is no `[UnsafeAccessor]`, so non-public members are reached through an accessor class
generated inside the type: the type and every type containing it must be `partial`. 🔧 Make them partial.

## NINO014

The type is re-created through a constructor, and this read-only member is not one of its parameters: its
serialized value is read and dropped. Add it to the constructor, make it settable, or `[NinoIgnore]` it.
🔧 Add `[NinoIgnore]`.

## NINO015

Keyed members are identified by a 32-bit hash of their name; two names collide. Rename one of them.

## NINO016

Reference tracking applies to classes. On a struct it is ignored (warning; 🔧 remove `trackReferences`). On a
class that is re-created from constructor arguments, a reference to the instance cannot be resolved before it
exists (error): make its members assignable.
