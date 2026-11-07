---
name: audit-analyzer
description: "Audit Nino's diagnostics (NINO rules in src/Nino.Generator/Analysis), the suppressor and the code fixes - missing rules for declarations that cannot work or that cost, false positives, messages that name the member and the fix, one diagnostic per mistake, a code fix wherever the fix is mechanical, tests per rule, the analyzer's build cost, and the docs that list the rules. Use when adding or changing a rule or code fix, when users hit a runtime error a rule could have caught, or when a rule fires wrongly. Triggers: NinoAnalyzer, NinoDiagnostics, NinoSuppressor, NinoCodeFixProvider, NINO0*, AnalyzerReleases, AnalyzerTests, CodeFixTests, docs/diagnostics.md, false positive, analyzer warning, 分析器, 诊断, 误报, 代码修复."
metadata:
  internal: true
---

# Analyzer audit

Files: `src/Nino.Generator/Analysis` (`NinoAnalyzer.cs`, `NinoDiagnostics.cs`, `NinoSuppressor.cs`),
`src/Nino.Generator.CodeFixes/NinoCodeFixProvider.cs`, `AnalyzerReleases.Shipped.md` / `Unshipped.md`; the list users
read is `docs/diagnostics.md` and the site's analyzers page. nino-gen's NINOGEN rules belong to `audit-runtimes`.

## Coverage

- Every declaration the generator cannot serialize gets a rule (Error when it cannot work, Warning when it works but
  costs or is likely wrong), so a user never meets "no formatter" at run time for something the compiler could see.
  That includes what generated code writes but cannot: a generic `[NinoType]` closed over a type without a formatter
  (`Box<Action>`, anywhere in a member's type) is NINO007 with "writing a value of it throws", because its formatter
  takes `CachedFormatter<Action>`; the generator keeps the member, so the bytes stay (the owner's call, round 101).
  Only type arguments the closed type's formatter writes count (`WrittenParameters`, below): `Tag<Action>` and
  `Outer<Action>` of `Outer<T> { Tag<T> Inner; }` write fine.
- **A type whose bytes would differ by host is an Error wherever Nino would write it**: NINO029 for `Vector<T>` on the
  member (alone, element, type argument a generic `[NinoType]`'s formatter writes, base type or `IEnumerable<T>` of a
  collection, field of a plain struct at any depth) and on the `NinoSerializer`/`NinoDeserializer` call (an operation
  action, rejected by method name first).
- **Formatters and generic methods used by hand are searched like members**: Nino.Core's formatters and
  `Writer`/`Reader`/`NinoDirect` generic methods (`new UnmanagedFormatter<Vector<int>>()`, `default(...)`,
  `[NinoMemberFormatter(typeof(UnmanagedFormatter<Vector<int>>))]`, `writer.Write(v)`, `reader.Read(out Vector<int>
  v)`, inferred type arguments included) throw the same `NotSupportedException`, so they get NINO029 too
  (`RawRules.HostSizedVectorWrittenBy`: the invocation action rejects a non-generic method by name first, the creation
  and default-value action a type outside Nino.Core's assembly first). A type parameter constrained to a formatter
  (`ArrayFormatter<T, TFormatter>`, `NinoDirect.Serialize<T, TFormatter>`) writes the others' values: only a Nino.Core
  formatter there is searched, so the user's own `Vector<T>` formatter, a collection formatter over it and unrelated
  generics that take a `Vector<T>` fire nothing (`NINO029_FormattersMadeByHand`, `NINO029_NotForOtherFormattersByHand`). Search
  the declared type of every field, property and local too, not only `new` and `default`: a formatter struct is ready
  without a constructor call, so `static UnmanagedFormatter<Vector<int>> F;` used as `F.Serialize(...)` threw with no
  diagnostic (round 121). Report the storage once: a `new`/`default` stored straight into a variable of its own type
  is left to the variable, and a serialized `[NinoType]` member to the member rule
  (`NINO029_FormattersHeldInFieldsPropertiesAndLocals`, `NINO029_NotForFormattersHeldOverOtherTypes`).
- **One predicate decides host-sized types everywhere**: the analyzer, the generator's resolver,
  `RawRules.IsRawUnmanaged`, the call transform (no registration, no interceptor) and, mirrored, Nino.Core
  (`RawRules.HostSizedVectorIn` / `NinoReflection.HostSizedVectorIn`).
- **NINO029 fires only where Nino writes the bytes, and alone**: never where Nino writes nothing (`[NinoIgnore]`,
  get-only properties, ref structs, which stay NINO007) or the user writes the bytes (`[NinoMemberFormatter]`), and a
  struct of such members gets NINO029 alone, not NINO023 too (`AnalyzerTests.NINO029_*`).
- **Walk everything a formatter would write, base types included**: a type holding the vector only through its base
  (`class Lanes : List<Vector<int>>`) got NINO007 (no formatter) instead of NINO029, the same mistake under a rule
  that names neither cause nor fix; a base without a vector (`class Ints : List<int>`) fires neither
  (`NINO029_InBaseTypesAndCollections`, `NINO029_NotForBaseTypesWithoutVectors`).
- **Any walk over symbols guards types that hold themselves** (`struct Node { List<Node> C; }`, `class Tree :
  List<Tree>`): without the guard the first predicate overflowed the stack and took the compiler down.
- **Search each type once per query, never once per path**: keep a set of every type met to the end; a vector found
  returns through every step, so a type met again is null. Forgetting nulls found under a cut made a ring of
  collection classes (`C0 : List<C1>` ... `Cn : List<C0>`) exponential, 18 classes two minutes in the IDE
  (`NINO029_TypesHoldingEachOtherAreSearchedOnce`, round 104).
- Decide what a closed generic `[NinoType]` writes from its definition, never by building its closed member types
  and never by "the parameter appears in a member type": `WrittenParameters` computes, per definition, what its
  formatter writes of each type argument (a serialized member from `MemberCache`, not one a `[NinoMemberFormatter]`
  writes; a class's own `IEnumerable<T>` elements; the generic subtypes of a slot) as a least fixpoint over every
  definition reachable from it, and NINO029 and NINO007 search only the values it finds (`ValuesOf`). Closed member
  types grow without end for a type holding itself over a bigger argument (`Expanding<T>` holding
  `Expanding<List<T>>`: a stack overflow), and "T is mentioned" reported `Outer<Vector<int>>` of `Outer<T> { Tag<T>
  Inner; }` though nothing writes it, and missed `Bag<T> : List<T>` (round 106, `NINO029_NotForTypeArgumentsNeverWritten`,
  `NINO029_TypeArgumentsWritten`, `NINO007_TypeArgumentsTheFormatterWrites`; a ring of 40 generics decides at once).
- **What a definition writes of an argument is a set of patterns, not a bit**: a subtype `S<U> : Shelf<Tag<U>>` that
  writes `U` writes the `X` of a `Shelf<Tag<X>>`, which `Tag<T>`'s own rules never write; a bit per parameter missed
  it (round 119). Its pattern is the base's argument with the subtype's pattern in place of `U` (`Tag<Whole>`), passed
  on through bases between them, interfaces, types nested in generics and generics that pass the argument to the slot
  (`NINO029_PartsOfArgumentsSubtypesWrite`, `NINO007_PartsOfArgumentsSubtypesWrite`); an argument of another shape, or
  a part no subtype writes, is not searched (`NINO029_NotForPartsSubtypesNeverWrite`). Match a subtype's pattern only
  where the argument can be what it gives its base, as registration unifies it (`ClosedGenerics`): its fixed types
  equal and a parameter named twice one type, within an argument or across (`Pair<U, U>`, `Twin<U, U>`); without it
  `Shelf<KeyValuePair<long, Vector<int>[]>>` was reported for `Fixed<U> : Shelf<KeyValuePair<int, U[]>>`
  (`NINO029_OnlyArgumentsASubtypeCanBe`, round 121). Inside a definition a part naming a type parameter may be any
  type: the check is left out there, which only widens.
- **Bound the patterns and widen, never drop**: a subtype holding its base over its own parameter matches
  `Tag<Tag<...>>` at every depth, so a pattern deeper than `MaxDepth`, or more than `MaxPatterns` for one argument,
  becomes `Inside` (every type inside the argument counts). Widening may report what nothing writes; dropping would
  miss a vector, which NINO029 must never do.
- **A referenced class hides its private members**: a compilation imports only the public and protected members of a
  referenced class (`MetadataImportOptions.Public`, the compiler's default), so `Hidden<T> { [NinoMember] private T
  _v; }` from a package looked like it wrote no `T`. Read the members its symbols lack from its published
  `[assembly: NinoTypeLayout]` (every parameter their type names counts, as `Inside`); a referenced class without a
  layout counts all its parameters (`NINO029_AcrossAssemblies`). Structs import their private fields.
- **Search subtypes wherever the compilation can see them**: this compilation and every referenced assembly that is the
  base's or references it (`TypeFacts.GenericSubtypesOf`), nested in generic types too, indexed by ancestor once per
  compilation (a pass over every generic definition per slot was quadratic) and, for referenced assemblies, cached
  across edits. A subtype in an assembly
  that references this one, and a subtype's parameter its base does not name (`S<U, W> : Shelf<U>` writing `W`), are
  invisible from the slot: documented, not reported.
- **A generic that is not a `[NinoType]` writes every type argument**: Nino.Core's formatters (collections,
  dictionaries, `Nullable`, tuples, `KeyValuePair`) write them all, and `NinoReflection.HostSizedVectorIn` refuses any
  other generic with a `Vector<T>` in an argument at run time, so the analyzer mirrors it; narrowing it would need the
  runtime check narrowed in the same change.
- An attribute that cannot do what it says is reported, not left to a rule whose message would be wrong:
  `[NinoMember]` on a static, constant, indexer, get-only computed property or backing field was NINO010 ("collected
  already") with autoCollect and nothing without it, while nothing was written; NINO028 now reports it alone, with
  the reason (`MemberCollector.NotSerializable`, the one test that decides membership too).
- Every pattern that works but costs (a member that defeats raw copying, a format that adds bytes nobody needs, a
  pooled type without its hooks) gets a Warning that names the cheaper form.
- Mistakes from the FAQ and the pitfalls pages (`docs/pitfalls.md`, the site's FAQ) that no rule catches are findings.

## Principles

- **The analyzer and the generator share one decision**: what the generator skips is exactly what the analyzer
  reports (the same model rules and `NinoSymbols` helpers); a rule never re-derives it.
- **One mistake, one diagnostic**: when two rules fire on the same code, the one whose fix removes the other's case
  reports it.
- **Messages** name the type or member, what happens (skipped, throws when written, never called) and what to write
  instead; placeholders are all filled; attributes are spelled `[NinoMember]`, never `NinoMemberAttribute`. A title
  or message may change (round 101 reworded NINO003, 005, 007, 010-012 and 017); the id never does, and the title
  in `AnalyzerReleases.Shipped.md` follows the descriptor (`ReleaseTrackingMatchesTheDescriptors`).
- **A code fix for every rule whose fix is mechanical and always correct**; otherwise the message says what to do.
  Removing an attribute that has no effect (NINO010, 011, 024, 025) is always correct; removing one that changes the
  bytes (NINO027's former name changes the type id) or choosing between two (NINO026) is not.
- **Ids are stable**; a removed id is never reused; new ids are taken at landing (two branches can pick the same).
- **Cost**: rules run on every build and keystroke. `EnableConcurrentExecution`, no analysis of generated code,
  symbols resolved once per compilation, cheapest rejection first (syntax kind and names before the semantic
  model), no walk that grows with the compilation per callback; measure with GeneratorBenchmark's analyze case.
- The suppressor hides only compiler warnings Nino's generated code makes wrong (documented in the analyzers page).

- The code standard (`audit-code`: DRY, decoupled, clear) applies to these files, and redundancy in them
  (duplicated logic, dead code, two ways to do one job) is part of this audit; the sweep across layers is
  `audit-redundancy`.

- **A rule that replaces a removed option keeps the bytes, proven on the generated code**: build the unit tests
  with `-p:EmitCompilerGeneratedFiles=true -p:CompilerGeneratedFilesOutputPath=<dir>` before and after applying the fix
  to every type and `diff -r` the trees (only interceptor locations may differ, as sources move). For
  `WEAK_VERSION_TOLERANCE` → NINO032 (r128) the diff found the case per-type marks missed, a type taking its
  `[NinoType]` from another assembly, which the fix now covers by declaring the options it took.

## Tests: every rule, every edge

`tests/Nino.UnitTests/Generator/AnalyzerTests.cs` and `CodeFixTests.cs` (locations as markup, `{|#0:Name|}` and
`.WithLocation(0)`, never line and column spans):
- each NINO id appears in a test that expects it and one that expects it not to fire; each code fix has a case per
  form it rewrites, its output compiles and reports nothing, and applying it twice changes nothing;
- edge cases where they apply: generic, nested and partial types, records and structs, inheritance across
  assemblies, properties with and without setters, `init`, constructors and factories, conditional compilation;
- exemplary code (samples in tests, docs fences) reports nothing;
- a rule that depends on a preprocessor symbol sets it with a solution transform
  (`WithProjectParseOptions(...WithPreprocessorSymbols(...))`, `CodeFixTests.WeakToleranceTest`); one about a type
  from another assembly references a built one (`Nino.UnitTests.Subset`) through `AdditionalReferences`.

## Done means

Rule and fix with tests, `AnalyzerReleases.Unshipped.md`, `docs/diagnostics.md` and the site's analyzers page in both
languages (a row of nino.docs' `src/data/analyzer-rules.json`, `audit-docs`; docs to-dos for the docs pass), the public skill's diagnostics section, a changelog entry.
