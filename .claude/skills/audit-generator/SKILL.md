---
name: audit-generator
description: "Audit the Nino source generator itself (src/Nino.Generator and its Roslyn 4.8/4.12 builds) - incremental pipeline and caching, cost per keystroke in the IDE, equatable symbol-free models, the model's rules (TypeModel.Need, DispatchesSubtypes, IsDispatchRoot), every shape of type users can declare, interceptors, registration without module initializers, re-entrant registries, what building models and emitting text allocate - and its compile-time benchmark. Use when changing the generator, adding a way to declare a type, when a type shape does not compile, or when builds or the IDE are slow. Triggers: src/Nino.Generator, NinoGenerator.cs, ModelBuilder, TypeModel, IncrementalGeneratorTests, ShapeMatrixTests, GeneratorBenchmark, interceptors, ForAttributeWithMetadataName, ModelCache, CodeWriter, TextScratch, generator allocations, slow build, slow IDE, 源生成器, 生成器, 增量, 编译慢, 内存分配."
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
  `tests/Nino.UnitTests/Corner/Modeling/ShapeMatrixTests.cs`, so it is compiled and round-tripped with every other one.
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
  `NormalizeWhitespace()` in transforms; build each output in the one leased scratch builder (`TextScratch`, capped
  at 8,192 characters, [emit-text.md](references/emit-text.md)), never a builder per fragment; one file per model, deterministic and
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

### Pipeline, binding and model reuse

The rules below are one line each; the reasons, the numbers and the trials that lost are in
[references/pipeline-and-binding.md](references/pipeline-and-binding.md): read it before changing `ModelCache`, call
binding or identity, the dependency scan, inherited-type discovery, the emission cache or a stage's projection.

- Keep `ForAttributeWithMetadataName` for attribute discovery.
- Reuse call bindings across reparses only with matching parse options, reference identities, source checksums and
  the same compilation options instance; on a method-body edit rebind that body only.
- Skip binding explicit type arguments naming the method's own type parameter; bind the rest as Roslyn does.
- Pass context and token to static callbacks in the call cache; keep the sweep's key list lazy.
- Do not memoize method display strings in call identity until the warmed edit timing holds.
- Carry model dependencies to replacement trees only for identical reparses and ordinary method-body edits.
- Let live compilations own model-cache state through weak keys; never keep a strong recent-project list.
- Enumerate attribute arguments directly, keep a single declaring tree inline, defer the tree set to a second one.
- Keep the dependency traversal's set construction, work stack and original-definition check as they are.
- Discover inherited types from the source module's referenced assemblies, after the Nino-reference filter.
- Index inherited candidates by unique tree path; keep the fixed-point loop and the existing index growth.
- Defer formatter sort keys to ties; return null from a projection instead of adding a `Where` stage.
- Compare emitted formatters by a cheap bucket key, then full equality; keep one index of weak entries, the latest per
  key, swept when it doubles, each tied to its text by a `ConditionalWeakTable` (a strong index held closed projects'
  generated texts).

### Building models

Rules for what building a model may allocate; reasons, numbers and the trials that lost are in
[references/model-allocations.md](references/model-allocations.md): read it before changing `ModelBuilder`,
`MemberCollector`, the construction planner, `TypeFacts`' registration walks, the formatter-rule binder or a type
name renderer.

- Allocate a list or set on the first item that needs it (callbacks, members, ancestors, parameter names, required
  members, registrations, prefetch members), never up front.
- Walk Roslyn's immutable arrays directly; no LINQ, `Concat` or captured predicate on a per-type path.
- Skip type-parameter work for non-generic models (`IsGenericType` includes containing types' parameters).
- Track only generic nodes in registration and layout walks; ask Roslyn whether a type is unmanaged last.
- In the formatter-rule binder, match into fresh slots, prefilter by definition only, and keep the struct adapter,
  the eager element list, the compact element array and the simple tuple flattening.
- Use `EquatableArray<T>`'s value enumerator and let its constructor materialize.
- Render type and formatter names into the bounded scratch, keeping only the final string; keep nesting names apart.

### Emitting text

Rules for the formatter, layout, dispatch, registry and usages emitters; reasons, numbers and the trials that lost
are in [references/emit-text.md](references/emit-text.md): read it before changing an emitter, `CodeWriter`,
`TextScratch` or alias rewriting.

- Lease the one bounded scratch builder (8,192 characters) per load context; keep oversized text out of it.
- Write aliases, prologues, case labels and statements directly into the builder; no closures for synchronous blocks.
- Keep `CodeWriter` general: blocks only through `Open`/`Close`, a line in pieces through `BeginLine`/`EndLine`, and
  a line shape of one emitter in that emitter.
- Use value-type scopes for indentation and body modes.
- Size dictionaries and arrays from counts the emitter already has; count eligible inputs before tuning a rare case.
- Keep list-backed dispatch buckets, uniform offsets, the separate preamble writer, escaping on the finished range.
- Renumber formatter aliases only when their order changes, not at all for a single candidate; choose every alias of
  a file as ranges of one body string and write them with the file (`GeneratedCode.File`), never a rewrite per
  alias; no variadic concatenation.
- Find dispatch ID collisions with a seen set; walk raw groups as ranges; reuse the planner's constructor matches.
- Keep each subtype's `[assembly: NinoTypeLayout]` text whole, its inherited members repeated; never propose writing
  only its own members plus its base. Why: the owner ruled it out (rc.10: "不是说了不考虑这个吗"); it would change the
  published layout text every baseline and runtime compares, for about 1.2 MB of source at 1,100 types (#262).

### Emitting interceptors

Rules for the interceptor file; reasons, numbers and the trials that lost are in
[references/emit-interceptors.md](references/emit-interceptors.md): read it before changing the interceptor emitter,
its aliases or its location attributes.

- Render each call once per emission through the bounded scratch and group by its text.
- Emit the file through the general path on every build (write, `WithAliases`, `GeneratedCode.File`); no second
  path for allocation alone.
- Append signature slices, ordinals and location attributes to the writer, never as substrings.

## Measure

`benchmarks/Nino.GeneratorBenchmark` (cold run, an edit in a method body, an edit elsewhere, compile with and without
the analyzer); compare base and head back to back. Keep a change that improves time or allocations beyond the error and
costs neither; an allocation saving with time on par counts only if the code stays as simple (no second mechanism
for one job, no second emission path, no hand-rolled cache): complexity needs a time gain (the owner, in the
review of codex/nino-audit). `--generics` adds generic slots, subtypes and closed members, which make the analyzer run
`WrittenParameters` and its subtype scan; `--layouts` makes the classes version-tolerant and adds a layout baseline of
every type, so the analyzer checks each against its section (NINO034-NINO037); pass `--generator <base
Nino.Generator.dll>` for the base side, and `--runs <n>` for a median of runs after the warm-up (`--warmup`; why: `benchmark`, the generator benchmark's
warm-up). On runners: runtime-ab's
`generator` lines (`benchmark`). Collect garbage before each timed step, the edits included (the tool does): without
it the analyzers' time of the same code read 80 or 330 ms run by run, the previous step's garbage collected inside
it, and the edits after emit and the analysis read as a 2x regression that was not one (#262: at 1,100 types main's
member edit took 0.28-0.34 s and its comment edit 0.56-0.67 s, 1938da63's either 0.29-0.72 s; collected first, both
sides 0.22-0.34 s, main on par or faster). Before blaming a stage for a slower edit, track the steps
(`AddingAMemberReEmitsOnlyTheTypesThatHoldIt` is the member edit's assertion): there every collected stage stayed
Cached, six formatter files of 1,103 were emitted, and the models' rebuild (one file holds all 1,100 types, so every
model reruns at both commits) was no slower. Track with a Roslyn newer than 4.3 on large inputs: 4.3's
`RecordStepTree` took over ten minutes recording one edit at 1,100 types. The analyzers'
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

How the evidence in the references was gathered and what counts as a verdict is in
[references/measuring.md](references/measuring.md): read it before measuring a generator change, before writing an
entry into a reference, and before claiming a build or IDE speedup.

- Replay the real project through cold, cached, body-edit and model-edit runs; prove identical output, model reuse
  and collection every time.
- Judge with tracking off, against an identical-binary control, in both load orders.
- Report allocation and latency separately; never claim a build-time gain from an isolated win.
- Measure edits with the old compilation alive and reopening after it is collected.
- Record IL and the Arm64 and x64 listing sizes of every method a change touches, kept or withdrawn.

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
