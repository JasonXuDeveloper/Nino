---
name: audit-generator
description: "Audit the Nino source generator itself (src/Nino.Generator and its Roslyn 4.8/4.12 builds) - incremental pipeline and caching, cost per keystroke in the IDE, equatable symbol-free models, the model's rules (TypeModel.Need, DispatchesSubtypes, IsDispatchRoot), every shape of type users can declare, interceptors, registration without module initializers, re-entrant registries - and its compile-time benchmark. Use when changing the generator, adding a way to declare a type, when a type shape does not compile, or when builds or the IDE are slow. Triggers: src/Nino.Generator, NinoGenerator.cs, ModelBuilder, TypeModel, IncrementalGeneratorTests, ShapeMatrixTests, GeneratorBenchmark, interceptors, ForAttributeWithMetadataName, slow build, slow IDE, 源生成器, 生成器, 增量, 编译慢."
metadata:
  internal: true
---

# Generator audit

Files: `src/Nino.Generator` (netstandard2.0, loaded by every Roslyn host; `src/Nino.Generator/AGENTS.md` has the
map), built twice: `src/Nino.Generator.Roslyn4.12` and `.Roslyn4.8` (the .NET 8 SDK). The text it writes is
`audit-generated-code`; its diagnostics are `audit-analyzer`.

## Correct for every shape

- A generator bug shows up as code that does not compile for one shape of type nobody tested (#203: every generic
  abstract class or interface). Take a name the model may leave empty (`BaseFormatter`, `DispatchRootFormatter`,
  `Construction.Creator`, ...) through `TypeModel.Need`, never by interpolating it directly. Decide what a type gets
  from the model's rules (`DispatchesSubtypes`, `IsDispatchRoot`, ...), not from a new check next to them.
- A feature that adds a way to declare a type (a kind, an attribute option, a place) adds a dimension to
  `tests/Nino.UnitTests/Corner/ShapeMatrixTests.cs`, so it is compiled and round-tripped with every other one.
- Generated registration code may call back into Nino.Core (`NinoPolymorphic.TryRegister(Type)`), which asks every
  registry again: anything a registry calls must be re-entrant (`NinoRegistry.RegisterType` guards it per thread).
- Register every closed type a public `NinoSerializer` / `NinoDeserializer` method names, by type argument or by
  `typeof` argument: a new such method goes into `NinoGenerator.NamesType` (the syntax filter) and is covered by
  `IncrementalGeneratorTests.EveryCallThatNamesATypeRegistersIt` and a case in `tests/Nino.AotTests`. Native AOT
  cannot close a generic formatter at run time, and the JIT hides the gap: `GetFixedSize<Pair<int, short>>()` and
  `SerializeBoxed(v, typeof(Pair<int, long>))` threw "no formatter" only after `PublishAot` until the filter matched
  more than `Serialize` and `Deserialize`. A generic `[NinoType]` is never of fixed size (`TypeFacts.IsFixedSize`):
  `GetFixedSize` of one is -1 on every runtime.
- Register by id exactly the closed subtypes the analyzer searches: `ClosedGenerics` takes a slot's generic subtypes
  from `TypeFacts.GenericSubtypesOf` (this compilation and every referenced assembly that is the slot's or references
  it, nested in generic types too, the containing types' parameters bound like the subtype's own). Its own pass saw
  only this compilation and the slot's assembly and skipped nested types, so `XListShelf<int>` of a plugin, and
  `SlotOuter<SlotArg>.Inner`, were checked by NINO029 but unreadable from their ids
  (`ClosedSubtypesFromAnotherReferencedAssemblyAreRegistered`, `ClosedSubtypesOfAGenericSlotAreReadableFromTheirIdsAlone`,
  round 121).
- Never pack a fact per type parameter into a bit of an `int`: C# masks a shift count to five bits, so `1 << 32` is
  bit 0 and a formatter's 33rd parameter took the first one's marks without a word (`[NinoRaw]` on `T33` of a
  34-parameter formatter registered `T1` as raw). Keep one value per position (`NinoParameterMarks.Register(typeof(F<>),
  marks...)`, `FormatterRules.OwnMarks`; `NinoParameterMarksTests.MarksOfParametersPastTheThirtySecondAreTheirOwn`).
- **Code that must run for every value of a type goes into its body, and the paths that skip the body are turned off
  by the type's rules.** `WriteBody`/`ReadBody` (and their `__Top`, `__Shallow` and new-instance copies, which the
  emitter writes from the same contents) are what every value reaches: top level, member, element, a subtype in a
  base slot (the dispatch calls the subtype's body), a deferred body. Only `WriteFixed`/blocks and `WriteRawBody`
  skip it: the serialization callbacks set `TypeShape.HasCallbacks`, which `TypeFacts.IsFixedSize` and
  `TypeModel.RawBody` read, so holders, interceptors and the type agree; a raw struct cannot be taken off its path
  without new bytes, so the analyzer forbids it there (NINO044). A feature that is not a way to declare a type but must
  hold for every shape rides on an existing `ShapeMatrixTests` dimension (callbacks on the `middle` shapes) rather than
  doubling the rows (round 130, `CallbackTests`, `ShapeMatrixTests`).
- Registration works without module initializers (HybridCLR, `platforms`); interceptors are always on with Roslyn
  4.8+ and the regular entry points must agree with them (the `-p:NinoRegularCalls=true` run of `verify`).
- Partial, nested, generic, record, struct, abstract and interface types, types in other assemblies, and every
  accessibility combination are handled or reported (NINO diagnostics, `audit-analyzer`).
- An interface member a type implements explicitly (or by its default implementation) is collected as the interface's
  member (`MemberCollector.FindImplementation` finds no field or property of its name), so every rule that matches a
  type's fields to its members maps an explicit implementation's backing field to that interface member
  (`MemberCollector.StoredMember`: `RawRules.IsRawStruct`, `TypeFacts.IsBlock`; without it an explicit struct was not
  raw and wrote other bytes than the implicit one), and facts about how the member is stored (`StartsDefault`,
  `IsAutoProperty`) are read from `FindImplementationForInterfaceMember`, never from the interface's declaration
  (round 129, `InterfaceMemberAttributeTests`). Two interfaces declaring a member of the same name, both implemented
  explicitly, still write only the first (`names` in `Collect`).
- A generic type's formatter takes the formatter of each type parameter (`__F_T`), so every name of a generated
  generic formatter (base formatters, member formatters, registrations, the hot-update resolver) carries formatters
  of the other assembly or of type parameters in scope. Shapes covered: bases closed over another parameter, a
  collection of one or a tuple (`CornerGenericTests.GenericSubtypesOfBasesClosedOverOtherArguments`), a type holding
  itself over a bigger argument (`Expanding<T>` holding `Expanding<List<T>>`), generics crossing a hot-update
  assembly both ways (`HotUpdateAssemblyTests.LoadedGenericsAcrossTheAotBoundary`), nested generics, constraints
  naming other parameters, records, keyed and tracked generics. Identifiers with two consecutive underscores are
  the implementation's (C# spec), so a user type parameter named `__F_T` clashing with one is not a bug.

## Fast on every keystroke

- `ForAttributeWithMetadataName` or a syntax-only predicate first; no `SemanticModel` in predicates.
- A transform reruns for every match on every compilation change: it reads declarations only, and returns an
  equatable, symbol-free model (`EquatableArray`, records of strings and ints). Facts that need method bodies or the
  whole compilation are computed in stages that feed implementation output only.
- No `CompilationProvider` in a per-item pipeline, and no `Combine` with a whole-compilation value: `Select` the
  narrow fact first. `Collect()` only small models into an output that needs all of them.
- No display strings for comparisons (`SymbolEqualityComparer`, metadata names), no `ToFullString()` /
  `NormalizeWhitespace()` in transforms; one `StringBuilder` per output, one file per model, deterministic and
  culture-invariant output, unique hint names.
- Bind as few nodes as syntax allows. A syntax provider's semantic transform reruns for every match on every
  compilation change, and `GetSymbolInfo` of a call binds its statement: binding the top-level calls was most of the
  generator's time on tests/Nino.UnitTests (about 2,400 calls; local trace, round 128), more than building the models.
  Skipping identifier receivers that are neither `NinoSerializer`/`NinoDeserializer` nor a `using` alias of one
  (alias names from syntax, per compilation) measured within the noise on `GeneratorBenchmark --calls` (a call in a
  one-statement method binds in microseconds) and was not kept (round 128). Look a transform's result up before
  binding anything: a call is reused unbound while its tree is the same instance and no tree's declarations changed
  (`ModelCache` epochs: `SyntaxTree.IsEquivalentTo(topLevel: true)`, which ignores method bodies and non-constant
  field initializers), which only holds for a result read from declarations; models are not (`ConstructsDefaults`,
  `StartsDefault` and `IsAutoProperty` read constructor bodies, initializers and accessors) and keep the tree check.
  Chained comment edits on tests/Nino.UnitTests took 150 ms instead of 520 ms, TopLevelCall 2 ms instead of 290 ms
  (round 129). Measure edits there, not on `GeneratorBenchmark` alone: its comment edit lands in the file of every
  model, which rebuilds them all.
- Walk an assembly's types only through `AssemblyTypes.Of`.
- Do not cache `FormatterRules` per referenced assembly: its 5-6 ms an edit on tests/Nino.UnitTests is the project's
  own `[assembly: NinoFormatter]` registrations (binding the new source assembly's attributes, then `Read`), which hold
  that compilation's symbols and cannot outlive it; every reference together read in 25 ms once. A per-
  `IAssemblySymbol` cache (`ConditionalWeakTable`) left the output byte-identical and chained edits within the noise
  (90-94 ms against 80-87 ms) and was not kept (round 129).
- `cancellationToken.ThrowIfCancellationRequested()` in loops over members and types.
- `WithTrackingName` on every stage; `tests/Nino.UnitTests/Generator/IncrementalGeneratorTests.cs` asserts that
  edits elsewhere and in method bodies hit the cache for every tracked stage (a rerun that returns an equal model is
  not a hit: count reruns).
- Implementation output for what user code never names; source output for what it calls.
- No diagnostics from the generator: the analyzer reports through the same checks (`NinoSymbols`, the model's rules),
  so "skipped by the generator" is exactly "reported by the analyzer".

## Measure

Measure name rendering allocations before keeping a reusable builder. Formatter names can reuse a bounded
thread-local character buffer because their recursive rendering appends to the same builder and retains no model
or symbol.

`benchmarks/Nino.GeneratorBenchmark` (cold run, an edit in a method body, an edit elsewhere, compile with and without
the analyzer); compare base and head back to back, keep a change only if time and allocations both improve beyond
the error. `--generics` adds generic slots, subtypes and closed members, which make the analyzer run
`WrittenParameters` and its subtype scan; `--layouts` makes the classes version-tolerant and adds a layout baseline of
every type, so the analyzer checks each against its section (NINO034-NINO037); pass `--generator <base
Nino.Generator.dll>` for the base side, and `--runs <n>` for a median of warm runs. On runners: runtime-ab's
`generator` lines (`benchmark`). Collect garbage before each timed step (the tool does): without it the analyzers'
time of the same code read 80 or 330 ms run by run, the previous step's garbage collected inside it. The analyzers'
own time (Roslyn's telemetry) also holds the binding an analyzer is the first to ask for; the analysis wall time
holds all of it: read both (`audit-analyzer`, the layout baseline).
On a real project: `ReportAnalyzer` (set in tests/Nino.UnitTests) prints the generator's and the analyzer's time per
compilation in a `dotnet build -v:d` log; to see the stages, rebuild the compilation from that log's `csc` line
(`CSharpCommandLineParser`, with its analyzer config files as an `AnalyzerConfigOptionsProvider`), run the driver with
`trackIncrementalGeneratorSteps: true` and sum each `TrackedSteps` entry's `ElapsedTime`, parsing the files again for
every run (`ModelCache` reuses models by tree instance), and hash the generated sources to prove base and head write the
same text. Untracked time there is Roslyn's own (declaring the compilation's symbols, binding), which the first stage
to ask pays. The Debug and Release builds of the generator take the same time on that compilation (binding
dominates; round 128). A new stage gets input in the incremental tests, or its caching assertion checks nothing.

## Look for

- Checks scattered next to the model's rules; the same symbol walk in two places (`NinoSymbols`, `TypeFacts`,
  `MemberCollector`); a model that carries symbols or syntax.
- Shapes `ShapeMatrixTests` does not cover yet; attributes or options that overlap (two ways to say one thing).
- Whatever users still write by hand that the generator could write (registration, formatter wiring, pool hooks).

- The code standard (`audit-code`: DRY, decoupled, clear) applies to these files, and redundancy in them
  (duplicated logic, dead code, two ways to do one job) is part of this audit; the sweep across layers is
  `audit-redundancy`.

## Done means

A caching assertion and a generated-output test for each new form, a ShapeMatrix dimension for each new way to
declare, the analyzer reports what the generator skips, GeneratorBenchmark shows no regression, and `feature`'s
checklist.
