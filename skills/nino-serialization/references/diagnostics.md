# Nino diagnostics

Reported by the analyzer as you type (the generator itself reports nothing). 🔧: a code fix exists in Visual Studio,
Rider and VS Code. Severity can be changed in `.editorconfig` (`dotnet_diagnostic.NINO006.severity = none`), but a
warning here usually means data is dropped. Full text: https://nino.xgamedev.net/en/docs/v4/analyzers.md

| Id | Severity | Meaning | Fix |
| --- | --- | --- | --- |
| NINO001 | Error | The type (or a containing type) is not `public`/`internal`, or is a `static` class or `ref struct` | Make it public or internal 🔧 |
| NINO002 | Error | Non-public members (or `[NinoPooled]`) where generated code cannot use `[UnsafeAccessor]` (before .NET 8, generic types before .NET 9, Unity) | Make the type and every containing type `partial` 🔧 |
| NINO003 | Warning | A subtype of a `[NinoType(allowInheritance: false)]` base is not serialized as itself | Add `[NinoType]` to the subtype 🔧 |
| NINO004 | Error | No usable constructor | Add a parameterless one, one whose parameters match members by name, or `[NinoConstructor]` |
| NINO005 | Error | A `[NinoConstructor]` parameter or name matches no serialized member | Fix the names |
| NINO006 | Warning | A read-only member of a re-created type is not a constructor parameter: read and dropped | Add it to the constructor, make it settable, or `[NinoIgnore]` 🔧 |
| NINO007 | Warning | No formatter for the member type (delegate, `Task`, pointer, `Type`, an uncreatable collection): skipped | `[NinoIgnore]` 🔧, or a custom formatter |
| NINO008 | Error | `[NinoMemberFormatter]` is not a struct implementing `INinoFormatter<T>` for exactly the member type | Fix the formatter |
| NINO009 | Error | Non-public `[NinoMember]` without `includeNonPublicMembers: true` | Remove it, or set the option 🔧 |
| NINO010 | Warning | `[NinoMember]` with `autoCollect: true` changes nothing | Remove it 🔧 |
| NINO011 | Warning | `[NinoIgnore]` with `autoCollect: false` changes nothing | Remove it 🔧 |
| NINO012 | Error | Both `[NinoMember]` and `[NinoIgnore]` | Remove one 🔧 |
| NINO013 | Error | Duplicate `[NinoMember]` index | Use the next free index 🔧 |
| NINO014 | Error | Two keyed member names (or former names) hash to the same key | Rename one |
| NINO015 | Warning | `trackReferences` on a struct is ignored | Remove it 🔧 |
| NINO016 | Error | `trackReferences` on a type re-created from its constructor (positional record, init-only members) | Make the members assignable |
| NINO017 | Error | Invalid `[NinoFactory]`: must be public, static, non-generic, callable without arguments, return the type | Fix the method |
| NINO018 | Error | `[NinoPooled]` on a struct, a non-sealed class or a re-created type | Make it a sealed class with assignable members 🔧 |
| NINO019 | Error | `[NinoPooled]` class lacks `OnNinoPoolWarmup` / `OnNinoPoolReturn` | Implement both partial methods 🔧 |
| NINO020 | Error | Unusable `[assembly: NinoFormatter]` (not a struct, nested in a generic type, uninferable type parameter, ...) | Fix the registration |
| NINO021 | Error | `[NinoConstructor]` is `private`/`protected` | Make it public or internal |
| NINO022 | Warning | A non-public base member hidden with `new` cannot be reached | Rename one, or make it public |
| NINO023 | Error | A struct serializes no member (zero bytes; its collections cannot be read back) | Serialize a member, or make it `VersionTolerant`/`Keyed` |
| NINO024 | Warning | Member `[NinoFormerName]` on a type that is not keyed has no effect | Remove it, or use `NinoFormat.Keyed` |
| NINO025 | Warning | `[NinoFactory]` on a type created through `[NinoConstructor]` is never called | Remove one of the two |
| NINO026 | Error | More than one `[NinoFormerName]` on a type | Keep the name the stored data used |
| NINO027 | Warning | `[NinoFormerName]` on a generic type written with its type id: all instantiations share one id | Remove it, or use it on non-generic types only |

`NinoSuppressor` also hides CS0169/CS0414/CS0649 and IDE0051/IDE0052/IDE0044 for private members Nino serializes.

## nino-gen

| Id | Severity | Meaning |
| --- | --- | --- |
| NINOGEN001 | Error | Invalid arguments or `<NinoCodegen>` metadata, or the assembly cannot be read; exit code 1, the build fails |
| NINOGEN002 | Warning | A type is left out because the target language cannot express it (the reason is given), with the types that hold it |
| NINOGEN003 | Warning | Old Nino.TypeScript `NinoTypeScript*` properties: declare a `<NinoCodegen Include="typescript" />` item instead |
| NINOGEN004 | Warning | Nino.Codegen is referenced but no `<NinoCodegen>` item is declared: add one, or set `NinoCodegenEnabled` to `false` |

## Runtime errors people hit

- `Nino: no formatter for T`: mark `T` `[NinoType]`, declare a formatter, and install `Nino` in the project declaring
  it (in Unity, reference `Nino.Core`). Under Native AOT, name the exact closed type in code the generator sees.
- A type id that is not a registered subtype: load the assembly first, rename with `[NinoFormerName]`, or
  `NinoPolymorphic.Register<Closed<T>>()` for a closed generic only read. On write: the runtime type is not a
  `[NinoType]` (base with `allowInheritance: false`, or no generator in its assembly).
- A cycle while serializing: `trackReferences: true` on the types of the cycle. Nested deeper than `MaxDepth`: give
  the recursive type a parameterless constructor and settable members so it can be deferred.
- Old data unreadable or wrong values: the type is positional and its members changed; see the formats table.
