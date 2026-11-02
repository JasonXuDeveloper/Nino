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
  generics that take a `Vector<T>` fire nothing (`NINO029_FormattersMadeByHand`, `NINO029_NotForOtherFormattersByHand`).
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
- Decide what a closed generic `[NinoType]` writes from its definition, by type parameter, never by building its
  closed member types and never by "the parameter appears in a member type": `WrittenParameters` computes, per
  definition, which parameters reach a written position (a serialized member from `MemberCache`, not one a
  `[NinoMemberFormatter]` writes; a class's own `IEnumerable<T>` elements; a generic subtype of a slot) as a least
  fixpoint over every definition reachable from it, and NINO029 and NINO007 search only the closed type's arguments
  in those positions. Closed member types grow without end for a type holding itself over a bigger argument
  (`Expanding<T>` holding `Expanding<List<T>>`: a stack overflow), and "T is mentioned" reported
  `Outer<Vector<int>>` of `Outer<T> { Tag<T> Inner; }` and `Tag<Box<Action>>` though nothing writes them, and missed
  `Bag<T> : List<T>` and `Shelf<T>` with a `ListShelf<T>` subtype (round 106,
  `NINO029_NotForTypeArgumentsNeverWritten`, `NINO029_TypeArgumentsWritten`,
  `NINO007_TypeArgumentsTheFormatterWrites`; a ring of 40 generics over growing arguments decides at once).
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

## Tests: every rule, every edge

`tests/Nino.UnitTests/Generator/AnalyzerTests.cs` and `CodeFixTests.cs` (locations as markup, `{|#0:Name|}` and
`.WithLocation(0)`, never line and column spans):
- each NINO id appears in a test that expects it and one that expects it not to fire; each code fix has a case per
  form it rewrites, its output compiles and reports nothing, and applying it twice changes nothing;
- edge cases where they apply: generic, nested and partial types, records and structs, inheritance across
  assemblies, properties with and without setters, `init`, constructors and factories, conditional compilation;
- exemplary code (samples in tests, docs fences) reports nothing.

## Done means

Rule and fix with tests, `AnalyzerReleases.Unshipped.md`, `docs/diagnostics.md` and the site's analyzers page in both
languages (a row of nino.docs' `src/data/analyzer-rules.json`, `audit-docs`; docs to-dos for the docs pass), the public skill's diagnostics section, a changelog entry.
