---
name: audit-naming
description: "Audit every name in Nino - public API, attributes and their options, settings, generated members and hint names, diagnostics' titles and messages, internal types, methods, fields, files and projects, tests and benchmarks, the runtimes' APIs in Go, Rust and TypeScript, docs headings and anchors, examples, and the Chinese terms - for ambiguity, wrong meaning, convention and consistency. Use in every audit round and on every new public name before it lands. Report first; public renames are the owner's call. Triggers: a new or renamed type, member, attribute, option, file, rule, page or term, \"is this name right\", 命名, 改名, 名字, 术语."
metadata:
  internal: true
---

# Naming audit

A name is the first doc a reader sees.

| Layer | Where |
|---|---|
| Public API | `src/Nino.Core` public types and members, attributes and their parameters, `NinoSettings`, exceptions' messages |
| Generated | members, types and hint names the generator writes; nino-gen's generated names per language |
| Diagnostics | NINO / NINOGEN titles, messages and code-fix titles |
| Internal code | internal and private types, methods, fields, locals, files, folders, projects |
| Runtimes | Go, Rust and TypeScript public APIs, following each language's conventions (Go `MixedCaps`, Rust `snake_case` and crate naming, TypeScript `camelCase`) while naming the same concept the same way |
| Tests and benchmarks | test names say what is checked; benchmark classes and methods as the reports show them |
| Docs | page titles, headings, anchors, file names, prose naming an API exactly as the code does |
| Skills | skill names and descriptions (agents pick a skill by them) |
| Chinese | one term per concept across pages and the public skill |

## For every name, ask

- **Ambiguity**: one word for two things (format, layout, raw, block, type id, version, key), or two names for one?
- **Behavior**: does it say its cost, whether it allocates, whether it throws? `Try` returns false, `Get` throws.
- **Convention**: does it match .NET's (and the serializers users compare with: MemoryPack, MessagePack) where the
  behavior is the same, and differ where it differs?
- **Call sites**: does it read well in the docs' examples and the tests?
- **History**: a name that no longer matches what the code does after a refactor; a file not named after the type or
  concern inside.
- **Inherited names**: a new member name must not hide one of its base's: renaming `NinoTypeLayoutAttribute.Id` to
  `TypeId` hid `System.Attribute.TypeId` (CS0114; through an `Attribute` reference `TypeId` is the attribute's
  identity object, not the id), found only at the build of the rename; the owner kept `Id` and renamed the
  constructor parameter and the layout text to `id` instead (r128 A11). Check the base's members (`Attribute`:
  `TypeId`, `Match`, `IsDefaultAttribute`) before proposing. An enum alias (two names, one value) makes
  `Enum.ToString` name either: text written from the enum spells its names out.

- **A diagnostic's title names the mistake in the user's code, in the present tense, not the project's history**: the
  history goes in the message. NINO032 was "WEAK_VERSION_TOLERANCE was removed", which says what Nino did, not what
  is wrong; it is now "WEAK_VERSION_TOLERANCE does not make types version tolerant" (the message keeps "it did before
  4.0"), in the form of "[NinoUtf8] has no effect" (r129). Its internal names followed (`WeakToleranceWithoutEffect`,
  `WeakToleranceSymbol`).
- **Names generated code adds to a user-visible type's own namespace carry the Nino prefix; names inside Nino's
  traits or interfaces do not**: Rust's inherent consts nino-gen writes (`type_consts!`) are `NINO_TYPE_ID`,
  `NINO_TYPE_NAME`, `NINO_LAYOUT`, as Go's methods are `NinoTypeID`, `NinoTypeName`, `NinoLayout`, so a user's own
  `impl` cannot collide with them; the body traits keep `TYPE_ID` beside `DEFERRABLE`, `MIN_SIZE` and `FIXED_SIZE`,
  namespaced by the trait (the derive writes only those). Until r129 the inherent id was a bare `TYPE_ID` next to
  `NINO_TYPE_NAME` (shipped in rc.8, renamed without an alias on the owner's word: "反正rc"; regenerated code compiles,
  user code naming `Cat::TYPE_ID` moves to `Cat::NINO_TYPE_ID`). TypeScript generates no such constants.
- **A test's name is what a console failure prints**: never a shared `DisplayName` or a base class's one `Test`
  method; `IssueTests` had every issue report as `Test` until r129, now a method per issue named
  `Issue<n>_<what it checks>` (`Issue157_InheritedClassesWithStructMembers`), each with its own `[Timeout(10000)]`.

## Output and applying

- A table per layer: current | problem | proposed | breaking | confidence | call site before → after, ranked by
  impact, split into clearly right and owner's call, plus a one-line "checked and fine" list.
- Internal, test, benchmark (keeping the report keys the docs site syncs) and docs names are renamed without
  approval, in their own commit. Public renames wait for the owner; then they need the changelog, the docs in both
  languages, the public skill, and a migration note.
- Rename a name no release carries without an alias: check `git tag --contains <commit that added it>` (and the
  registries: NuGet, crates.io, npm, the Go module's tags) before adding one. Aliases (Go `Deprecated:`, Rust
  `#[deprecated]`, MSBuild warnings, accepted old attribute values) only for names a release shipped. Why: rounds 101-102
  renamed unreleased Go and Rust names with aliases anyway (Go deprecated.go, Rust `NinoWrite::PREFETCH`, the derive's
  `"plain"`/`"tolerant"`), two names for one thing in a 4.0 that had not shipped; round 103 deleted them (the runtimes
  first ship in rc.8). TypeScript shipped in rc.7: its renames break, with a changelog line saying to regenerate.
- Stale names: `git grep` the old name across this repository and nino.docs; only history pages keep it.
- Prove a rename is only a rename, so it needs no A/B: map the new names back to the old ones with `sed` and `cmp`
  each changed file (runtime, tests, regenerated outputs) against `git show HEAD:<file>`; for an emitter, build the
  unit tests with `-p:EmitCompilerGeneratedFiles=true` on base and branch and `diff -r` the trees; for TypeScript,
  `node --no-lazy --print-bytecode -e 'import("./Nino.Conformance.ts")'` in `test/generated` sums to the same
  bytecode length (V8 inlines by bytecode size, and a method's name is a constant-pool entry, not bytes). Rust
  constants and file moves leave no trace in the machine code. Round 102 (Rust `INLINE_DEPTH`/`MAX_DEPTH`,
  TypeScript `beginRegion`/`exitBody`): 1,584 functions, 146,729 bytes before and after.
- Reorder parameters across the code base with a Roslyn rewriter, never `sed`: swap the declarations' pairs, then
  only the calls of methods so declared (an unrelated `(ref a, ref r)` of one type would swap silently), and run
  it once per preprocessor configuration (.NET, netstandard, `NINO_GENERATOR`, Unity): code under an inactive
  `#if` is trivia to the parser. The compiler finds the rest (a reader named `local`, `deep` or `this`); the
  emitters' text and the tests' source strings are done by hand. Round 128's reader-first formatters: 118
  declarations, 152 calls by the tool, about 40 by hand.
