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
  `NormalizeWhitespace()` in transforms; one `StringBuilder` per output, one file per model, deterministic and
  culture-invariant output, unique hint names.
- Allocate a callback list only after accepting the first callback of its kind. Track the four kinds
  accepted at each hierarchy level in a local mask and keep static before instance deserializing callbacks.
  Over 15 callback-bearing declarations from the real unit test project, the isolated build saved
  about 218 B per model in all four M4 pairs and took less time in three. Whole-project timing and
  allocation varied, including a second four-round reopen replay. All 583 files matched across four
  stages, 578 models were reused after each edit and closed-project references collected. Build shrank
  from 856 to 832 IL bytes, 3,864 to 3,752 Arm64 FullOpts bytes and 2,565 to 2,493 offline x64 bytes.
  Both Roslyn builds passed 59 targeted tests (`57d3197e`).
- Return the shared empty sequence before creating a callback-mark iterator for a method with no attributes.
  Keep attribute order in the nonempty iterator and retain no symbols in the shared result.
  Scanning 3,403 real-project methods saved 185,120 B per pass in four isolated M4 pairs.
  Isolated medians improved against the paired baseline, but one identical-binary control was an
  outlier. Cold, reopen and body-edit timings and allocations did not establish a whole-project gain.
  All 583 generated files matched across four stages, 578 models were reused after either edit,
  and the old compilation, tree, symbol and output collected. Factory plus MoveNext IL grew
  from 183 to 192 B, Arm64 FullOpts from 344 to 472 B and offline x64 from 386 to 420 B.
  Both Roslyn builds passed 59 targeted tests (`dca6dba7`).
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
- Reuse call bindings across identical reparses only with matching parse options, reference identities, and
  source checksums. Require the same compilation options instance: the compiler server's resolver can throw
  from value equality. For a single directive-free method-body edit, bind that body again and map calls outside it
  back to their previous positions. Ask Roslyn for current interceptor locations even before the edit position,
  because the file checksum changed. Compare each edited output with a baseline that bound the calls normally.
- Carry type-model dependencies to replacement trees only for those same identical reparses or ordinary
  method-body edits. Constructor bodies, initializers and accessors still affect the model and must invalidate
  it. In the real unit test project, editing a method in the generic polymorphism fixture rebuilt 80 models
  even though all 80 were structurally unchanged.
- Let live compilations own model-cache state through weak keys. Keep symbols in per-compilation tables and
  release old tree mappings after migrating call dependencies. Measure active-project edits with the prior
  compilation and driver alive, then measure reopening separately after dropping both and collecting.
  A strong recent-project list kept closed projects' trees alive and masked the cost of reopening.
  Improve reopening by measuring the cold binding and model stages rather than extending their lifetime.
  In the real unit test project, cold call binding took roughly half a second and model construction another
  130 ms. Skipping identity and dependency work for calls without output reduced allocations but lost on
  default-JIT reopening. Scan interceptor feature names and metadata-name arity directly without split arrays.
- Enumerate constructor and named attribute arguments directly in the model dependency scan. Preserve
  argument order and keep the same per-compilation ownership. Avoiding the LINQ projection and concatenation
  saved 42,832 B per pass over 910 declarations in four default-JIT M4 pairs, with isolated medians down
  11–25%. Whole-project latency and allocations varied, including reopening with an identical-binary control.
  The scan grew from 163 to 188 IL bytes but shrank from 704 to 612 Arm64 FullOpts bytes and 574 to 553
  emulated x64 FullOpts bytes. All 582 files (3,241,440 bytes) matched across the cold, cached and edited runs,
  all 577 models were reused after either edit, and the closed project inputs and generated text collected.
- Materialize a single declaring tree directly in the dependency node. Keep distinct-tree collection
  for types with multiple declarations, including partials declared more than once in one file.
  The fast path covered 907 of 910 declarations in the unit-test project. Four isolated default-JIT
  M4 pairs each saved 261,216 B per pass, with elapsed time varying in both directions. Whole-project
  reopen runs saved 81,160–109,136 B with mixed timing. `Direct` grew from 362 to 405 IL bytes,
  1,512 to 1,632 Arm64 FullOpts bytes and 1,218 to 1,285 emulated x64 FullOpts bytes.
  All 582 generated files matched byte for byte, the edit probes reused all 577 models, and
  old project inputs and generated text collected.
- Defer the traversal's tree set until it sees a second distinct tree. The dependency walk
  still visits the same symbols and returns the same ordered trees. In the unit-test project,
  894 of 910 declarations depended on a single tree. Four isolated M4 default-JIT pairs saved
  207,520 B per pass and reduced median time by 6–44%. Cold project runs saved 646–842 KB and
  reopening saved 441–476 KB, with mixed latency. `Dependencies` grew from 494 to 602 IL bytes,
  2,272 to 2,716 Arm64 FullOpts bytes and 1,972 to 2,346 emulated x64 FullOpts bytes. All 582
  generated files matched, both edit probes reused all 577 models, and closed-project collection passed.
  Keep the set construction in the traversal. Outlining only the two initial insertions reduced the
  Arm64 FullOpts entry from 2,716 to 2,600 bytes but added a 152-byte helper; emulated x64 went from
  2,346 to 2,275 plus 96 bytes, and IL from 602 to 588 plus 22 bytes. After measuring the
  single-tree and multi-tree groups in separate warmed processes, the single-tree group still took
  3–11% longer than baseline over four M4 pairs, against -0.2% to +7% for an identical-binary
  control. The multi-tree group improved against the first baseline but varied against the control.
  Isolated allocations, 582 generated files, model reuse and collection matched. The candidate was withdrawn.
  Keep the work stack for the symbol traversal. A local for one pending symbol, backed by a stack on
  branches, saved 17,576 B per isolated 910-declaration pass. The dependency order matched, but three
  of four warmed default-JIT M4 pairs took 3–23% longer, and project reopen allocation and timing
  varied. The traversal moved from 602 to 632 IL bytes, 2,716 to 2,708 Arm64 FullOpts bytes, and
  2,346 to 2,678 emulated x64 FullOpts bytes. The 582 generated files, incremental model reuse,
  and collection checks matched. The candidate was withdrawn.
  Keep the current original-definition check. Reading `OriginalDefinition` once and adding a
  reference-equality shortcut moved the traversal from 602 to 607 IL bytes, 2,716 to 2,660 Arm64
  FullOpts bytes and 2,346 to 2,338 emulated x64 FullOpts bytes. Four isolated M4 pairs split
  between faster and slower, with equal allocations. Reading once without the shortcut shrank
  the same listings to 601, 2,652 and 2,329 bytes, but three of four isolated pairs and three
  of four cold project pairs took longer. Both variants kept the dependency order, all 582 files,
  all 577 reused models, and the collection checks. Both candidates were withdrawn.
  The local feature lookup went from 280 B per call to zero, with identical real-project output.
- Skip binding explicit type arguments that name a method's own lexical type parameter from inside its body.
  Keep qualified names, contextual keywords and inferred calls on Roslyn's binding path. The real unit test
  project lost 47 binding candidates, all checked against baseline semantic results, with identical output.
  A follow-up filter for single-argument `Serialize` with a cast or a typed `default` removed only three more
  candidates in that project, so keep the shared binding path. Time closed-call binding separately from identity,
  dependencies, usage and interception. Binding dominated the instrumented project even when machine load varied.
- Pass the current syntax context and cancellation token to static callbacks in the call cache, and build a model
  directly on a miss. Capturing callbacks allocate before the lookup, even when the lookup hits. Keep the sweep's
  temporary key list lazy as well, so saving a call does not allocate a closure for a sweep that may never run.
- Use a short-circuiting symbol scan when the model only needs to know whether a serialization callback exists.
  Keep the ordered callback iterator for building calls and diagnostics. On 548 types from the unit test project,
  the per-type probe went from 402 to 64 B and found the same callbacks. Roslyn 4.8 and 4.12 each matched all
  577 files byte for byte on cold and edited runs. Both edits reused all 572 models on Roslyn 4.12, and the
  closed compilation, tree, symbol and generated text collected. Cold and reopen timings varied, and the first
  generic fixture edit slowdown did not reproduce in a four-round follow-up with an identical-build control.
- Process automatically collected members in declaration order. Allocate a per-level candidate list only for
  explicit indices, which still sort by index and name. In a 548-type probe, skipping only the sort cut allocation
  from 1,925 to 1,332 B per type. Sending automatic candidates directly through the existing deduplication rules
  cut it to 1,106 B with identical member order. Four cold project replays saved 73–115 KB per run. Reopening
  saved 0.54–0.82 MB against the paired baseline across eight rounds. The initial reopen slowdown did not
  reproduce in the four rounds with an identical-build control. Both Roslyn builds matched all 577 files,
  the Roslyn 4.12 edits reused all 572 models, and the project-close and edit probes collected old objects.
- Allocate the ancestor list and visited set only when member collection finds a serializable base or interface.
  Collect the declaring type separately, after its ancestors. In the 548-type probe this reduced allocation
  from 1,106 to 912 B per type with identical member order. Traversing and collecting in the same recursion
  reduced it further to 888 B, but reopening the real project took longer than the paired baseline in four
  rounds. Keep the lazy list for now and retain the direct traversal as an experiment. With the lazy list,
  cold, edit and reopen timings overlapped the identical-build controls. All 577 generated files matched on
  Roslyn 4.8 and 4.12, 572 models were reused after both edits, and the close and edit collection checks passed.
- Delay scanning constructor and factory parameter names until member collection encounters a get-only
  property. Allocate the case-insensitive set on the first name. In the 548-type probe, allocating the
  set lazily alone went from 912 to 850 B per type. Delaying the scan reduced it to 786 B and kept
  member order identical. It saved 114–177 KB per cold project replay. Cold, cached, edit and reopen
  timings varied, so keep the allocation result separate from whole-project latency.
- Walk overridden properties directly during member deduplication. The former LINQ predicate bound
  a delegate and created an iterator even when the member had no overridden property. On .NET 8
  the 548-type probe went from 1,122 to 789 B per type and from 399–412 to 352–359 ns. The
  .NET 10 default-tiering probe already allocated the same 786 B on both builds. The two
  allocation calls were still present in the baseline Arm64 FullOpts listing, so check the
  runtime and tier before predicting the allocation cost from IL. Real-project generated files
  and incremental reuse matched.
- Pass the visibility context directly when checking whether a derived member hides a base member.
  Check the override chain by symbol equality without a captured predicate. Across 233 inherited
  member probes from the unit test project, both .NET 8 and .NET 10 went from 38.04 to 1.92 B per
  check with identical decisions. The .NET 10 project reopened with 0.17–0.32 MB less allocation
  across four rounds. Whole-project latency varied within the identical-build spread.
- Walk an assembly's types only through `AssemblyTypes.Of`.
- Build a compact element encoding array directly from the bound formatter arguments. Keep
  order and duplicates. Replaying 850 real project types saved 144–159 KB per pass in both
  load orders and matched all resolver results. Timing varied with load order; three of four
  cold runs allocated less and one slightly more. Combined IL moved from 2,459 to 2,521 bytes,
  Arm64 Tier 1 from 17,792 to 17,440 bytes, and x64 cross-JIT from 4,343 to 4,330.
  Both Roslyn builds matched all 577 files, reused all 572 models on edits and passed the
  collection checks. Each runtime passed 88 focused tests.
  Pass the argument count and trailing traits instead of building a traits array.
  The same 850-type replay saved 285 KB in both load orders, with identical resolution.
  Four cold replays saved 579–831 KB and their timings varied. `Bind` moved from 339
  to 351 IL bytes, 10,980 to 10,616 Arm64 Tier 1 bytes, and 1,003 to 1,011 x64 cross-JIT
  bytes. Both Roslyn builds kept all 577 files, 572 model hits, and collection checks;
  both runtimes passed 69 focused tests.
  Keep the parameter adapter a struct and share its mutable body flag with a lazy factory
  list. Making the adapter itself a class added 23 KB per 850-type replay. The small shared
  state saved 75 KB in both load orders and 316–343 KB in four cold project rounds. The two
  measured methods went from 503 to 541 IL bytes, 17,524 to 17,952 Arm64 Tier 1 bytes and
  1,356 to 1,283 x64 cross-JIT bytes. Resolver results, all 577 generated files, 572 model
  hits and collection checks matched. Both runtimes passed 81 focused tests.
  Keep the eager element list pending a change that avoids it on successful single-element
  bindings too. Moving the list to the shared state and creating it on first use saved 37 KB
  per 850-type replay in both load orders, but did not reliably improve whole-project latency.
  The binder, element resolver and accessor grew from 548 to 591 IL bytes, 17,760 to 18,884
  Arm64 Tier 1 bytes and 1,288 to 1,323 x64 cross-JIT bytes. Both Roslyn builds kept all 577
  generated files, 572 model hits and collection checks. Each runtime passed 81 focused tests.
  A separate first-element slot also avoided lists on successful singleton bindings. It
  saved 56 KB per 850-type replay in both clean load orders and 94–168 KB in the four
  cold runs. Timing reversed with load order and varied under load. The three affected
  methods grew from 548 to 655 IL bytes, 18,528 to 19,572 Arm64 Tier 1 bytes and 1,288
  to 1,409 x64 cross-JIT bytes. Both Roslyn builds matched all 577 files and retained
  incremental reuse and collection. Keep the list until a more direct representation
  removes the allocation without adding two collection states.
  Delay the shared binder's formatter and resolution arrays until the pattern unifies.
  The runtime array rule's rank mismatch allocated 80 B instead of 152 B per call;
  successful matches allocated the same. The 850-type generator replay saved 7.8 KB in
  both orders. Whole-project and runtime timings varied. Binder IL was 406 to 414 bytes;
  the runtime specialization was 7,388 to 7,376 Arm64 Tier 1 bytes and 1,044 to 1,094 x64
  cross-JIT bytes. Both Roslyn builds matched 577 files, model reuse and collection.
  Runtime wire output matched, each runtime passed 93 focused tests, and both portable
  builds passed.
  Use the non-null formatter slot as the resolved flag. Both adapters produce reference
  types and only succeed with a non-null result. This removed 32 B per successful
  runtime array binding and saved 74 KB per 850-type generator replay in both load
  orders. Runtime misses were unchanged. Timings and whole-project allocation varied.
  Binder IL was 414 to 422 bytes, Arm64 Tier 1 was 7,488 to 7,380, and x64 cross-JIT
  was 1,094 to 960. Wire bytes and all 577 generated files matched, incremental reuse
  and collection passed, and both runtimes passed 101 focused tests.
  Match parameterless formatter rules directly using shared empty outputs. Keeping
  transactional unification cloned an empty array; direct matching cannot write a slot
  and removed that last allocation. The runtime Uri rule went from 72 B on success
  and 48 B on rejection to zero, and both cases beat both controls in four local rounds.
  Generic array rule timings varied at the same allocations. Binder IL was 422 to 453
  bytes, the generic Arm64 Tier 1 listing 7,484 to 7,944, and x64 cross-JIT 960 to 1,051.
  Runtime wire bytes, 577 generated files, all 572 model hits and collection matched.
  Match the first generic pattern into its fresh type slots as well. The caller discards
  them on rejection. Keep transactional unification for constraint inference on existing
  slots. The 850-type generator replay saved 113 KB in both load orders, and four cold
  runs saved 207–284 KB. The runtime array rule saved 40 B on success and rejection;
  rejected rules beat both controls in all four rounds, while success timing varied.
  IL and x64 cross-JIT stayed at 453 and 1,051 bytes; Arm64 Tier 1 fell from 7,936 to
  7,108. Wire bytes, 577 outputs, model reuse and collection matched.
  Filter interface definitions before cloning the constraint trial slots, then match into
  the disposable clone. Keep ambiguity detection and commit only the sole binding. The
  850-type replay saved 118 KB in both orders and four cold runs saved 87–153 KB.
  Runtime match, rejection and ambiguity probes all allocated less. Timings were noisy
  and some runtime samples overlapped scratch cleanup. IL stayed at 323 bytes, Arm64
  Tier 1 was 6,144 to 6,108 bytes and x64 cross-JIT 819 to 782. Wire bytes and all 577
  outputs matched, incremental reuse and collection passed, as did focused tests.
  Do not prefilter definitions through the argument-reading API. Moving that
  prefilter before transactional matching saved 68 KB per 850-type replay and
  1.23–1.34 MB in cold project runs. The runtime base constraint miss allocated
  80 B less but was slower than both controls in all four rounds. IL rose from 323
  to 341 bytes, Arm64 Tier 1 from 5,192 to 5,688 and x64 cross-JIT from 782 to 872.
  The definition-only check still retrieves generic arguments via `IsConstructed`.
  Both Roslyn builds kept all 577 outputs, model reuse and collection. Runtime wire
  and binding checks matched, and the focused suites and portable builds passed.
  Use a definition-only API for the prefilter and supertype scan. This saved
  520–535 KB per 850-type replay and 1.78–1.91 MB in four cold project runs.
  Runtime base-constraint rejection saved 112 B, and success saved about 288 B.
  Timings varied without the repeatable miss regression of the argument-reading
  version. IL was 323 to 325 bytes, Arm64 Tier 1 5,192 to 3,752 and x64 cross-JIT
  782 to 836. Runtime bytes, 577 generated files, reuse and collection matched.
  Keep the definition prefilter separate from the recursive matcher. Passing a
  flag to skip the already proven top-level checks kept the argument reads and
  did not save runtime allocations or consistently improve timings. The four
  default-JIT replays kept all 577 files byte-identical, and both Roslyn builds
  kept model reuse and collection. For `UnifyWithSupertype` and `Match` together,
  IL grew from 612 to 630 bytes, Arm64 Tier 1 from 5,596 to 6,516 bytes, and x64
  cross-JIT from 1,483 to 1,948 bytes. Try avoiding the actual argument read or
  moving the specialized match out of the shared recursion before retrying.
  Keep the compact array for now. Looking up elements on demand and building the array
  only for tuples saved 18 KB per 850-type pass in both load orders, but cold allocation
  varied around baseline. The lookup repeated the scan for pair encodings and grew the
  combined IL from 2,533 to 2,673 bytes and the Arm64 Tier 1 methods from 16,988 to 22,148.
  The x64 cross-JIT listing for binding and formatting, excluding the shared array builder,
  moved from 4,076 to 4,330 bytes. Both Roslyn builds matched all 577 files, reused the
  models on edits and passed the collection checks.
  Flatten the last element of a long value tuple in the binder-owned array. On the real 8-,
  9- and 15-element shapes the isolated helper saved 88 B per call and was faster in both
  load orders. Whole-project allocation and timing stayed within noise. `Flat` moved from
  53 to 43 IL bytes, 676 to 292 Arm64 Tier 1 bytes and 134 to 114 x64 cross-JIT bytes.
  Both Roslyn builds kept all 577 files and all 572 model hits and passed collection checks.
  The two runtimes each passed 46 focused tests.
  Keep the simple flattening helper. Appending the rest as a character range into the emitter's
  leased builder saved 285 B per full encoding in the isolated long-tuple replay, with better
  timing in both load orders. Whole-project allocation and timing did not improve repeatably.
  The two affected methods grew from 2,116 to 2,206 IL bytes, 5,136 to 5,772 Arm64 Tier 1 bytes
  and 3,179 to 3,381 x64 cross-JIT bytes. The candidate also made the model depend on the emitter's
  buffer pool. Both Roslyn builds matched all 577 files and kept incremental reuse and collection.
- Share top-level usage registrations by bound type within `TypeFacts`. Keep binding and interceptor locations per
  call. Repeated calls in the unit test project rebuild the same closed-generic, layout and nesting registrations
  without that cache.
- Pass ordered enumerables directly into `EquatableArray<T>` when the constructor owns materialization.
  Removing the extra `ToArray` from project facts and layout names reduced the two methods by 15 IL
  bytes and kept all 577 replayed files, incremental hits and collection checks unchanged.
  The whole-project timing and allocation differences were within noise.
- Use the value-type enumerator when directly iterating an `EquatableArray<T>`. Keep the enumerable
  interfaces for LINQ and preserve the array's value equality and ownership. The 577-file project replay
  saved about 18 KB of cold allocations with identical output. `LayoutEmitter.Members` fell from
  320 to 310 IL bytes, from 1,472 to 1,364 Arm64 bytes, and from 735 to 695 x64 ReadyToRun bytes.
  Filter accessor members in the loop to avoid boxing the array and allocating a LINQ iterator.
  Replaying 577 formatters saved 112–113 KB per pass in both load orders, and cold project replays
  saved 43–130 KB. Timing varied with load order. All 582 outputs matched on both Roslyn builds,
  tracked body edits reused all 577 models, and closed-project objects collected. The .NET 8
  and .NET 10 targeted suites passed (`0ccae04c`).
  Visit base classes and interfaces separately in dispatch, preserving their order without
  boxing either array for `Concat`. The isolated dispatch replay saved a median 43 KB in
  both load orders, with no repeatable timing gain. Both Roslyn builds emitted identical
  files, dispatch stayed cached on body edits, old project objects collected, and 70 targeted
  tests passed on each of .NET 8 and .NET 10 (`284d0c63`).
- Count eligible models before changing fixed-layout emission for allocation savings. An exact-length
  offset array saved 192 bytes per replay of the three fixed-size formatters in the unit-test project.
  The emitter timing changed direction when the load order was reversed and `FixedLayout` grew from
  266 to 287 IL bytes. Four whole-project cold replays had no repeatable change beyond noise.
  All 577 generated files matched, tracked edits reused all 572 models, and the old compilation
  objects and generated text collected. Keep the simpler production shape until a representative
  project with many fixed-size models shows a larger effect.
- Allocate exact-length raw block offset arrays and omit header storage for non-keyed runs.
  The emitter-only replay of 572 real models saved about 32 KB in both load orders.
  Cold and edit timings varied, all 577 generated files matched, and incremental reuse,
  collection probes, and 69 targeted tests passed (`f19ec5b7`).
  Keep the raw-run description as a readonly value. This removes one object per run and
  saved 17–18 KB in the emitter-only replay in both load orders. The 577 outputs matched,
  and tracked edits, collection probes and 69 targeted tests passed (`8a3b47aa`).
  Skipping the separate length-offset expression when a keyed header packs both fields
  did not reduce allocation reproducibly. The project had 50 packed header stores, and
  the two emitter load orders each differed by 216 bytes in opposite directions. The 577
  outputs, incremental reuse and collection checks matched. Keep offset construction
  uniform until a keyed workload shows a measurable benefit.
- Create inherited-type cache entries only after an assembly passes the Nino-reference filter.
  Keeping plain symbols in the temporary list avoided entries for unrelated references and saved
  about 33 KB of cold allocations in the unit test project replay. All 577 files matched.
  `ReferencedNinoTypes` dropped from 596 to 576 IL bytes and the previous compiler objects
  still collected after an edit and after the driver was released.
  Stashing reference-name sets in the temporary list increased cold allocations by about 31 KB.
  Explicitly enumerating the candidate set instead of calling `HashSet.Overlaps` kept warmed
  allocations at the same 5,272 B per scan. It slightly improved the isolated scan but left full
  replay timings flat and grew the Arm64 Tier 1 body from 10,004 to 15,380 bytes. Both variants
  produced the same 577 files. Keep the shared overlap operation.
  Use the source module's referenced assembly symbols for inherited-type discovery instead of
  rebuilding a list through per-reference symbol lookups. The ordinary, extern-aliased, duplicate
  alias and linked-module probes returned equal symbol sets. The isolated scan improved in all four
  local rounds and fell from 576 to 478 IL bytes, with all 577 replayed files unchanged.
  Replacing the cold per-assembly LINQ name collection with a lazy set also matched all 577 files,
  but did not move replay time or allocations beyond noise. The entry and new collector totaled
  524 IL bytes versus 478 before. Keep the existing collection for now.
  For source declarations, collect unmarked candidates in the same pass that records marked names.
  Check base names by index to avoid the predicate allocation in each fixed-point pass. The real
  project replay saved 66 KB cold and 48 KB on method-body edits with the same 577 generated files.
  Timings were flat and the entry plus helper used 296 IL bytes instead of 318.
  Marked declarations seed that search by name alone. Leave their base lists empty and allocate
  the unmarked declaration list only on the first usable base name. The 577-file replay saved
  about 7 KB cold and 29 KB on method-body edits. Output, cache hits and compiler-object collection
  matched, and the apparent model-edit slowdown did not persist after reversing the run order.
  Reusing only the last semantic model in candidate resolution did not reduce replay allocations.
  Filtering the file-path lookup to candidate paths saved about 19 KB on a method-body edit,
  without changing timings, incremental hits or any of the 577 files. Its iterator grew from
  469 to 565 IL bytes, 1,388 to 1,864 Arm64 bytes and 1,183 to 1,381 x64 ReadyToRun bytes.
  Keep the current lookup until a larger project shows a stronger benefit.
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

Defer rendering a formatter sort key until two type names compare equal. The unit test replay
had one tie among 517 registrations. Stage allocation fell from 273,920 to 72,904 bytes in
four local rounds, and all 577 generated files matched. Keep stable ordering for equal keys
and check tie-heavy inputs before generalizing this comparison. Stage and cold replay times
varied, including after the scheduling control. The selection stage stayed cached on edits.

Project an optional usage registration directly and discard nulls before merging. The
singleton arrays in `FormatterRegistrations` cost 73,200 bytes in the local unit test replay.
The measured stage went from 347,120 to 273,920 bytes in four cold rounds with the same 517
results. The per-usage projection shrank from 27 to 7 IL bytes and from 172 to 52 Arm64 Tier 0
bytes (x64 cross JIT, 135 to 43). The 577 generated files matched, method edits kept the stage
cached and reopened projects released their previous compiler objects. Keep the sort keys and
the position of distinct filtering.

Pass the existing equatable arrays to the usage emitter. Enumerate their struct enumerators and
reuse the layout names for both passes. On the real unit test input this removed about 2.4 KiB
of allocation per emission in all four isolated local rounds. IL went from 713 to 634 bytes,
Arm64 instrumented Tier 0 from 3,864 to 3,188 bytes, and x64 offline code from 1,695 to 1,649 bytes.
The 236,789-byte usages file and all other outputs matched. Incremental hits and collection were
preserved, and timings varied in both directions.

Measure the state-table cost before adding a filter step to a projection. A separate Roslyn
`Where` before creating dispatch entries allocated more on all four local cold runs against their
paired baselines, even though it projected fewer entries. The identical-binary controls also varied.
All 577 outputs were byte-identical and closing and editing the project still released old objects.
Compare skipping the allocation inside the existing projection before keeping the extra step.
Return null for nonparticipants in the existing dispatch projection and filter the collected values
before sorting. On the 572 models from the unit test project, the isolated projection created 469
entries and allocated 54,352 bytes instead of 70,840 across all four rounds. Timing varied in
both directions in the isolated and full-project probes. The 577 generated files matched, the
dispatch stage stayed cached on both edits, and closed projects released their Roslyn objects.

Measure name rendering allocations before keeping a reusable builder. Keep `FormatterRef`'s bounded thread-local
character buffer and hand immutable strings to models. Extending the buffer to general type naming saved
allocations but slowed the local default-JIT hot rebuild samples. Delay constraint builders and registration
lists until their path needs them. Keep traversal sets separate per root.
Lease one alias rewrite builder per generator load context, retaining at most 8,192 characters of
capacity. The single slot saved 3.32–3.42 MB on each of four real unit-test project cold replays,
close to the earlier thread-local experiment without storage per worker. Cold wall time stayed
within 1%, and cache hits and edit times had no repeatable change. All 577 generated files
matched byte for byte, all 572 models were reused on tracked edits, and the project-close and
edit probes collected old Roslyn objects and output text. Reusing the formatter's own writer
instead increased cold allocations by 0.38–0.46 MB in the four rounds. A writer that already
grew into multiple chunks can allocate again when cleared for alias replacement.
Keep oversized rewrites out of the slot. Growing a leased buffer beyond the cap discards it and
makes the next small rewrite allocate again. Bypassing the lease saved another 0.60–0.71 MB
per cold replay across four rounds, with cold wall time within 0.6% and all outputs identical
(`0febf582`).
Append the layout attribute to the existing bounded scratch buffer after materializing its
member text. This avoids interpolated header fragments while preserving escaping, invariant
ids and declaration order. The 572-model formatter replay saved 273 KB in both load orders;
four cold project replays saved 255–418 KB. Cold, cached and method-body edit timings had no
repeatable gain. `LayoutEmitter.Emit` went from 255 to 286 IL bytes, 1,500 to 1,436 Arm64
Tier 0 bytes, and 601 to 668 x64 cross-JIT bytes. Both Roslyn builds matched all 577 generated
files, reused all 572 models on edits, and released the closed-project objects.
Append the members to the same builder and escape only their range. Share the member writer
with the analyzer's unescaped baseline representation. This saved another 184 KB per
572-model replay in both load orders and 82–301 KB across four cold replays. Whole-project
timings varied. `Emit` moved from 286 to 333 IL bytes and the member writer from 354 to
313. Their combined Arm64 Tier 0 listings were 3,376 and 3,308 bytes, and x64 cross-JIT
listings were 1,455 and 1,470. Both Roslyn builds matched all 577 outputs, reused all models
on edits, released the old project objects and passed 83 focused tests on each runtime.
Append ranges between `global::` qualifiers instead of allocating plain type names for the
member writer. This saved 55 KB per 572-model replay in both load orders. Whole-project
timing and allocation varied. `AppendMembers` moved from 313 to 311 IL bytes plus the
65-byte helper, from 1,720 to 1,676 instrumented Arm64 Tier 0 bytes plus 296, and from
707 to 659 x64 cross-JIT bytes plus 146. All 577 outputs matched on both Roslyn builds,
all 572 models were reused on edits, and the collection checks passed.
Keep literal escaping on the completed member range. Escaping the original fragments instead
removed the two builder scans but saved no allocations in the 572-model replay. Timing reversed
with load order, and the whole-project rounds were noisy. The affected IL rose from 709 to
792 bytes, Arm64 cold listings from 3,576 to 4,052 bytes and x64 cross-JIT from 1,568 to
1,763 bytes. Both Roslyn builds matched all 577 outputs and retained model reuse and collection.
Delay formatting an alias declaration until the occurrence count pays for it. Compute its length
from the alias and original name first. Four cold replays allocated 45–58 KB less, wall time
stayed within 0.3%, and all 577 outputs matched (`abc295cc`).
Share the same lease with block namespace indentation. Four cold replays saved 12–35 KB with
the test project's default language version (five block namespace files), and 4.80–4.95 MB with
C# 9 generator parse options (577 block namespace files). Both output sets matched byte for byte
and cold wall time had no repeatable change. The replay checks generated output. A separate
C# 9 consumer compiled and round-tripped with matching wire bytes (`b066e8db`).
Prepend the inlining aliases to the formatter's existing declaration list before rewriting its body.
This removes the one-use concatenation iterator and keeps the same declaration order. The
572-model emitter replay saved 40.7–41.1 KB in both load orders, and the four cold replays
saved 25–140 KB. Cold, cached and body-edit timings showed no repeatable change. The emitter
went from 227 to 224 IL bytes, 1,100 to 1,104 Arm64 Tier 0 bytes and 396 to 412 x64 cross-JIT
bytes. All 577 files matched on both Roslyn builds, all 572 models were reused on edits,
and closed-project objects collected.
Write file-scoped aliases directly into the leased prefix builder before the final four-string
concatenation. This avoids the separate joined declarations string and saved 364–376 KB per cold
unit-test project replay, with wall time within 1% and all 577 files identical. Four follow-up
rounds with an identical-binary A/A control found no repeatable edit timing shift. Each tracked
edit reused all 572 models and re-emitted only the block-namespace interceptor file
(`0abfc154`).
Return empty member layout text directly and use the same bounded scratch for nonempty layouts.
Four cold replays allocated 181–313 KB less with no repeatable timing change. The 577 output
files matched, all 572 models were reused on the tracked edits, and the collection probes
passed (`3db04963`).
Lease the bounded builder before writing the formatter body and release it as soon as the
immutable body string exists. This lets the alias pass borrow the same slot and avoids creating
a fresh writer buffer for each file. Four cold replays allocated 4.72–4.82 MB less and wall
time stayed within 0.6%. All 577 outputs matched, tracked edits reused all 572 models, and the
collection probes and 71 targeted tests passed (`2616fc2c`).
Append the partial accessor suffix before materializing the block file, saving about 45 KB
per real-project formatter replay in both load orders. Renting the existing scratch buffer
only for the partial emitter added about 28 KB to the same replay, so keep the accessor writer
separate. Whole-project timings and allocations varied. All 582 files matched on both Roslyn
builds, tracked edits reused the models, and collection and targeted tests passed (`dcd6f0c6`).
Emit the partial accessor directly into the block file buffer and leave room for the generated
pool hooks. Direct emission with the ordinary reserve added about 20 KB per formatter replay.
Allowing 3 KiB for pooled tails instead saved 50 KB in both load orders and 42–77 KB across
four cold project replays. All 582 files matched on both Roslyn builds, tracked edits reused
the models, and collection and targeted tests passed (`28e5b679`).
Write a single indented line directly instead of allocating a closure for `CodeWriter.Indented`.
Four cold replays allocated 111–184 KB less with wall time within 0.7%. All 577 files matched,
the 572 models were reused on tracked edits, and the collection probes passed (`cb904042`).
Emit common write and inherited member blocks directly where the delegate only wraps a synchronous
call. Four cold replays saved 48–72 KB. The generated files were byte-identical, incremental reuse
and collection probes passed, and cold timing showed no repeatable change (`68aa8513`).
Apply the same rule to raw body, fixed-size and block formatter emission. A replay of the three
affected models from the unit test project allocated a median 728 B less per group in both load
orders. The full-project cold, cached and body-edit timings showed no repeatable gain.
`EmitFixed` grew from 389 to 443 IL bytes, 1,444 to 1,532 Arm64 Tier 0 bytes and
673 to 718 x64 cross-JIT bytes. Both Roslyn builds matched all 577 files, reused 572 models
on edits, and released the old compilation, tree, symbol and generated text after close.
Use the same direct block emission for recursive collection formatters. The isolated nesting
emitter saved 184 B with one formatter and 1,304 B with eight. All 582 files matched on both
Roslyn versions across fresh, body-edit and model-edit replays. Nesting stayed cached on edits,
closed-project objects collected, and 63 targeted tests passed on each of .NET 8 and .NET 10.
Whole-project timings showed no repeatable improvement (`37c6743b`).
Write exact dispatch checks directly from the known subtype list. The dispatch replay saved a
median 68 KB per emission in both load orders, and four cold project replays saved 39–122 KB.
Timings varied. All 582 outputs matched on both Roslyn builds. Body edits reused the models and
dispatch output, closed-project objects collected, and 70 targeted tests passed on each of
.NET 8 and .NET 10 (`7fed214a`).
Reuse the sorted closed instances and their declaration ordinals to emit the read and write
selections directly. This saved a median 47 KB per dispatch emission in both load orders on
the 31 closed classes in the unit test project. Whole-project cold and edit timings showed
no repeatable gain. All 582 outputs matched on both Roslyn builds, dispatch stayed cached on
body edits, old project objects collected, and both targeted suites passed (`bf5f8ae7`).
Skip the filtered list and collision set when a dispatch bucket has at most one candidate.
Check the singleton against the base ID before reusing its list. This saved about 21 KB per
dispatch replay in both load orders. Whole-project timings and allocations varied. All 582
files matched on both Roslyn builds, dispatch stayed cached on body edits, project objects
collected, and 70 targeted tests passed on each of .NET 8 and .NET 10 (`167d32ce`).
Reuse the original bucket for two to four candidates when their IDs are distinct from
each other and from the base ID. The unit project had 27 such small emitted dispatches.
An isolated replay saved about 7.4 KB in both load orders, with no repeatable timing gain.
The filter and helper together grew from 213 to 300 IL bytes, 684 to 892 Arm64 FullOpts
bytes, and 493 to 686 x64 cross-JIT bytes. Ordered buckets through five candidates
matched the collision rules, and both Roslyn builds matched all 577 generated files
and kept dispatch cached and old project objects collectible.
Keep list-backed dispatch buckets for emission. Storing the first candidate directly
and allocating its list only on the second saved about 3.9 KB per isolated unit-project
emission in both load orders. The wider dictionary value and the count/index checks
increased the four sampled methods and new bucket members by 163 IL bytes. The four
machine-code listings grew by 556 Arm64 FullOpts bytes and 421 x64 cross-JIT bytes.
Whole-project allocation and timing showed no repeatable improvement. Both Roslyn
builds matched all 577 generated files, kept dispatch cached, and released old
project objects. The same fixture had 22 unused ancestor edges across 14 names and
no unused closed-generic edges, compared with 232 dispatch bases. Measure the
cost of indexing those bases before adding a filter for the unused edges.
Write local reader and writer copy prologues and write-back lines directly around the body.
Constant lines and removal of the nested closures saved 214–317 KB in four cold replays.
All 577 outputs matched, the 572 models were reused on tracked edits, and the collection
probes passed. Cold and edit timings showed no repeatable change (`fe87801f`).
Use a value-type scope for synchronous formatter body-mode changes. This preserves restoration
while avoiding both the callback and a new mode object. Re-emitting the 572 models saved about
73 KB per pass in both load orders. Whole-project cold and edit timings varied, all 577 outputs
matched, and tracked edits reused every model. Collection and 97 targeted tests passed
(`5c1fc60b`).
The singleton formatter-candidate probe held the first name in a local and allocated a list
only on the second. It saved about 16 KB per pass over 451 alias-eligible models in both
load orders. Whole-project wall time showed no repeatable change. All 577 outputs matched,
incremental edits reused all 572 models, and old compiler objects and output collected.
Keep the lazy list for now given the small allocation difference and the extra collector state.
Keep the declaration path separate as well. Reusing the main declaration list for a single
formatter alias, then moving the declaration to the end, saved 12 KB per 572-model emission
in both load orders. Whole-project cold, cached and edit timings did not show a repeatable
change. `WithAliases` grew from 496 to 550 IL bytes, 2,356 to 2,608 instrumented Arm64 Tier 0
bytes and 1,105 to 1,240 x64 cross-JIT bytes. Both Roslyn builds matched all 577 files, reused
all 572 models on edits, and released closed-project objects. Keep the current declaration
list instead of adding a remove-and-reinsert step for one alias.
Deferring the declaration list until the first profitable formatter alias avoids the
empty list without reordering any declarations. Keep the current implementation.
Across 577 real models, 173 emitted formatter aliases and the trial saved 27,920 bytes
per emission pass. Four cold runs saved 25–177 KB and reopens saved 12–27 KB, but
whole-project and isolated timing varied. WithAliases fell from 496 to 486 IL bytes,
while the alias write/rewrite helpers grew from 333 to 453 IL bytes, excluding the
multi-alias path. Arm64 FullOpts grew from 1,572 to 2,144 bytes at the entry and
872 to 1,396 bytes across those helpers; offline x64 grew from 1,105 to 1,205 bytes
and 591 to 968 bytes, respectively. All 582 generated files, tracked reuse and
collection matched, and both Roslyn builds passed 81 tests. The allocation savings
did not warrant the additional code.
Pass registry entries and pointer fields as their existing equatable arrays. The registry emitter
can enumerate twice without copying the entries into a list. Replaying the real project registry
saved 16.7–16.9 KB per emission in both load orders. Cold and edit timing varied. All 577 outputs
matched, tracked edits reused all 572 models, and collection and targeted tests passed
(`de92db3d`).
Indent each registry case with a value-type scope to avoid its capturing action. Registry
emission saved about 55 KB per pass in both load orders. The 577 generated files matched,
all 572 models were reused on tracked edits, and collection and 58 targeted tests passed
(`3380bf7f`).
Emit the registry switches directly from their bucket lists. This removes the capturing
callback and boxed enumerator per switch. The 64-switch registry replay saved about 11 KB
in both load orders. All 582 files matched on both Roslyn builds, body edits reused the
registry entries, old project objects collected, and both targeted suites passed (`cdf0e8b2`).
Append numeric case labels directly to the code writer buffer. The registry replay saved
about 40 KB per emission in both load orders and four cold project replays saved 78–131 KB,
with no repeatable timing change. Both Roslyn builds emitted the same 582 files, registry
entries stayed cached on body edits, project objects collected, and both targeted suites
passed (`3687fb79`).
Append bucket numbers directly in both the outer switch labels and method headers. The
64-bucket registry replay saved about 17 KB in both load orders. Whole-project time and
allocation varied. All 582 files matched on both Roslyn builds, registry entries stayed
cached on body edits, project objects collected, and both targeted suites passed (`5309a6e5`).
Insert registration statements directly into their ID cases in the existing order. This removes
the per-registration tuple arrays and the iterator chains in both registry emitters. The
isolated registry replay allocated about 35 KB less in both load orders. The 577 outputs matched,
incremental reuse and collection checks passed, and cold and edit times varied (`9ef11af1`).
Index registry buckets by their dense masked IDs and enumerate them in ascending order.
This avoids grouping, sorting bucket keys, and creating per-bucket delegates. Isolated
registry emission saved about 17.6 KB in both load orders. Cold timings stayed within 1%,
the 577 generated files matched, incremental reuse and collection checks passed, and
73 targeted tests passed (`5071d778`).
Count cases before filling each bucket to allocate its backing array at the final size.
On the real project, the 64-bucket registry replay saved about 16 KB in both load orders,
and four cold runs saved 11–43 KB without a repeatable timing change. All 582 files matched,
registry entries stayed cached on body edits, old project objects collected, and 69 targeted
tests passed on each of .NET 8 and .NET 10 (`ed1ac897`).
Store the sorted cases in one array grouped by cumulative bucket offsets. The registry
replay saved another 4 KB by removing the per-bucket objects and array headers. The dispatch
and switch emitters together shrank by 60 Arm64 FullOpts bytes and 375 x64 cross-JIT bytes.
Whole-project timing and allocation varied. Both Roslyn builds emitted identical files,
incremental and collection checks passed, and both targeted suites passed (`91612f39`).
Sort the cases once by masked bucket and then by signed ID. Walk the contiguous groups
directly to avoid the second case array and offset table. Alias counts saturate, so their
result is independent of the scan order. Isolated registry replay saved 12,032 B in both
load orders and four cold project runs saved about 20–50 KB. Timings varied. The ordering
and dispatch methods plus comparer went from 515 to 499 IL bytes, 2,956 to 2,320 Arm64
FullOpts bytes, and 1,279 to 900 x64 cross-JIT bytes. Both Roslyn builds matched all
577 generated files, kept registry entries cached, and released old project objects.
Size the local case dictionary during the existing generic-registration preamble
scan. Count both IDs when a type has a distinct wire name and skip open generics.
This saved 52,240 B per isolated registry emission and 11–84 KB in four cold runs.
Timing gains varied with load order. `EmitRegistry` grew from 924 to 970 IL bytes,
2,924 to 2,928 Arm64 FullOpts bytes, and 1,791 to 1,880 x64 cross-JIT bytes.
Both Roslyn builds kept all 577 outputs, incremental reuse, and collection.
Seed the usages case dictionary with the sum of the formatter, factory, and
closed-generic counts. This avoids a second scan for the rare alternate IDs.
The isolated usage replay saved about 41.7 KB in both load orders and four cold
project runs saved about 26–52 KB. Timing varied. `EmitUsages` grew from 638 to
661 IL bytes, 2,972 to 3,028 Arm64 FullOpts bytes, and 1,695 to 1,746 x64
cross-JIT bytes. Both Roslyn builds kept all 577 outputs, reuse, and collection.
Format concrete registration statements with their final ID argument directly.
Reuse the statement for the normal ID and format an alternate only for a distinct
name ID. This saved 247 KB per isolated registry emission, 59 KB per usage emission,
and 291–328 KB across four cold project replays. Whole-project timings varied.
`AddStatements` and its new helper together fell from 454 to 420 IL bytes,
976 to 888 Arm64 FullOpts bytes, and 837 to 788 x64 cross-JIT bytes. Both Roslyn
builds matched all 577 outputs, incremental reuse, and collection.
Emit the call to the same-namespace usages registry without a generated namespace
prefix. Otherwise that single prefix makes alias processing copy the whole registry
body. The isolated replay saved 244,288 B in both load orders and four cold project
runs saved 215–255 KB. Timings varied. `EmitRegistry` kept its 970 IL, 2,928 Arm64
FullOpts, and 1,880 x64 cross-JIT bytes. Both Roslyn builds matched all 577 files and
preserved incremental reuse and collection.

Use the known leaf layout for portability and raw descriptions before allocating
measurement lists or a builder. Leave types that the canonical view handles
differently, such as `Int128`, on the existing description path. The ten-leaf
probe reduced allocation from 648 to 40 B per type and was about five times
faster in both load orders. Four cold project runs with duplicate baselines
saved about 0.54–0.68 MB against the paired baseline. Whole-project timings
varied. The two entry methods grew from 200 to 304 IL bytes, 1,316 to 1,672
Arm64 Tier 0 bytes, and 552 to 723 x64 cross-JIT bytes. Both Roslyn builds
matched all 577 files, incremental reuse, and collection.
Keep the registry preamble writer separate unless its size is known cheaply. The real
project preamble was about 10 KB and a lease with a 256-character request grew beyond
the 8,192-character cap, allocating 288 bytes more per isolated emission. Counting the
exact length first saved about 12.5 KB, but repeated the attribute format in sizing code,
and emitter timings changed direction with the load order. Both emitted the same registry
bytes. Keep the size estimates in the probe.
Start the separate preamble writer at 256 characters. Isolated replays with zero
or one generic registration saved 7,680 B and eight saved 6,000 B in both load
orders. The full unit project added 288 B to isolated emission. Neither its
four cold replays nor the isolated timing controls showed a repeatable change.
The emitter kept 970 IL and 1,880 x64 cross-JIT bytes, and Arm64 FullOpts went
from 2,928 to 2,920. Both Roslyn builds matched all 577 files, reuse, and collection.
Append the final requested blank line to `CodeWriter` before materializing its text.
This saves a second full string copy without predicting output size. The isolated registry
saved 20.1 KB and four cold project replays saved 461–660 KB. Cold and edit timing varied.
All 577 files matched, incremental model reuse and collection checks passed, and 88 targeted
tests passed (`a411f88c`).
Append interceptor aliases directly for the two signature slices and the call. This
removes the intermediate replaced strings while leaving location attributes alone.
The isolated emitter saved about 519 KB in both load orders with similar median
time. The eight method-body edit samples allocated 514–536 KB less. All 577 files
matched, tracked model reuse and collection checks passed, and 68 targeted tests
passed on each of .NET 8 and 10 (`82b7bbb3`).
Use the interceptor lookup directly when the emitter needs both group count and iteration.
The 1,210-group replay saved about 9.7 KB in both load orders. Whole-project timing and
allocation varied. Both Roslyn builds emitted the same 582 files, edit tracking and
closed-project collection matched, and 54 targeted tests passed per runtime (`909cdf51`).
Use compile-time preambles for the fixed interceptor attribute. The 307–323-character
preamble no longer needs a writer, saving about 9.2 KB per replay in both load orders.
Both Roslyn builds emitted the same 582 files, incremental and collection checks passed,
and 38 targeted tests passed on each runtime (`fc0b5481`).
Build the three interceptor name sets directly with the model array's value enumerator.
The isolated allocation saving was about 0.3 KB, but the entry and shared helper together
saved 284 Arm64 FullOpts bytes and 326 x64 cross-JIT bytes. All 582 files matched in both
Roslyn builds, incremental and collection checks passed, and each runtime passed 47
targeted tests (`5b2ed919`).
Preserve formatter reference identity through unchanged twin substitutions. Allocate
a replacement argument array only after the first changed child. This avoided about
676 KB and reduced median emitter time by 7–11% in both isolated load orders. The eight
method-body edit samples saved 632–664 KB, with variable whole-project timing. All 577
outputs matched, tracked model reuse and collection checks passed, and 78 targeted
tests passed on each of .NET 8 and 10 (`b467a67c`).
Memoize rendered interceptor calls by their shape for one emission, then group by the
rendered text as before. Multiple source locations can share the same call. Both isolated
load orders saved 308 KB and stayed within 3% in time. Method-body edits saved 295–312 KB
in eight samples with variable timing. All 577 files matched, model reuse and collection
checks passed, and 78 targeted tests passed on each of .NET 8 and 10 (`d6556222`).
Filter interceptor prefetch candidates by option, overload, and element formatter
before comparing the rendered list type. Isolated emission saved about 11.8 KB with
prefetch enabled and 16.6 KB with it disabled, with similar times in both load orders.
Both 577-file output sets matched their baselines. The enabled replay kept all 39
prefetch calls and the disabled replay emitted none. Model reuse, collection probes
and 68 tests on each of .NET 8 and 10 passed (`b13fd0d2`).
Render interceptor calls and nested formatter names into one string through the
existing bounded character buffer. Four cold replays saved 1.52–1.54 MB, and eight
method-body edits saved 1.55–1.56 MB. Isolated emitter replays saved about 630 KB
and their median time fell 16–25% in both load orders. Whole-project timings varied.
All 577 outputs matched, model reuse and collection checks passed, and 78 targeted
tests passed on each of .NET 8 and 10 (`0ff49c6a`).
Keep the call-text dictionary scoped to emission. Reusing the structural key for
the earlier sort saved only 2–19 KB across the eight method-body edit samples, with
variable whole-project timing. In the tracked replay the sort stage took 2.43 to
5.00 ms cold and 1.47 to 2.05 ms on the body edit. The 577 files, model reuse and
collection results matched. Count the structural hash cost before caching sort keys.
Bound the call-text dictionary's initial reserve to 256 entries. The unit-test project
had 1,205 keys across 1,965 locations. Isolated emission saved about 83 KB in both load
orders and the eight body edits saved 77–91 KB, with variable timing. Both Roslyn
builds emitted identical files, incremental and collection checks passed, and each
runtime passed 47 targeted tests (`6ed42ab4`).
Use the existing location and forwarder counts to estimate indentation in the
large interceptor namespace. This needs no extra scan and saved about 265 KB in both
isolated load orders. Eight method-body edits saved 256–277 KB with variable timing.
All 577 generated files matched, model reuse and collection checks passed, and 78
targeted tests passed on each of .NET 8 and 10 (`ce66cee9`).
Count the actual modern location line lengths in the same pass that checks display paths.
The isolated emitter saved about 67.6 KB in both load orders and the eight body edits
saved 62–81 KB. Timings varied. Both Roslyn builds emitted the same 582 files, tracked
call sites and closed-project collection matched, and 38 targeted tests passed per
runtime (`908bda02`).
Keep the short namespace and byte-alias profitability scans separate. Combining them
saved only 30–40 B per emitter replay, with no repeatable time or whole-project allocation
gain. `Emit` grew by 6 IL bytes and 16 x64 cross-JIT bytes while shrinking by 120 Arm64
FullOpts bytes. The 582 outputs, incremental tracking and collection matched on replay.
Both targeted suites passed. Keep the merged loop as a probe.
Reserve 312 characters per forwarder in the preindented interceptor body and retain 320
for the legacy path. The unit-test body used 609,981 of the original 622,556 reserved
characters. Both isolated load orders saved about 19.4 KB, while the benchmark project
hit the existing location cap and allocated the same. The initial cold slowdown did not
repeat with a byte-identical control. Outputs, incremental tracking, collection and both
targeted suites matched (`0646f4ab`).
Write the modern interceptor body at its final namespace indentation and concatenate it
with the small prefix. Keep the line-by-line path for legacy and multiline locations.
This removed about 1.06 MB per isolated emission and per method-body edit. Isolated timing
varied by load order. Both Roslyn 4.8 and 4.12 output sets matched all 577 files. Tracked
model reuse and collection checks passed, and .NET 8 and 10 each passed 78 targeted tests
(`e958537d`).
Omit generated namespace qualifiers while appending interceptor signature and call slices.
Keep the final rewrite for qualifiers outside those slices. Isolated emission allocated
about 1.28 MB less and median times fell 5–10% in both load orders. Eight method-body
edits saved 1.28–1.29 MB with variable whole-project timing. Both Roslyn 4.8 and 4.12
matched all 577 files, incremental reuse and collection probes passed, and 78 targeted
tests passed on each of .NET 8 and 10 (`31b7ef5c`).
Append the byte-span alias while writing modern interceptor slices when its existing
whole-name threshold is met. This avoided the last full alias rewrite in the real
interceptor file. Isolated emission saved about 2.52 MB. Four clean method-body edit
replays saved 2.52–2.53 MB and ran 6–11% faster, with variable A/A and other edit timings.
Both Roslyn 4.8 and 4.12 matched all 577 files, model reuse and collection checks passed,
and 78 targeted tests passed on each of .NET 8 and 10 (`2e9c7b4b`).
Bounding the byte-span search to each alias slice kept output and allocations effectively
identical. Isolated emitter time improved in one load order and regressed in the other.
Four cold and edit replays had no repeatable change. Keep the existing whole-name matcher
until a larger or more alias-dense workload shows a benefit.
Skip empty alias ranges before searching or appending. The Arm64 listing of `Append`
and its separately emitted helper shrank from 2,268 to 1,332 bytes after the inline
decisions changed. Isolated replays of both projects showed no reliable time or
allocation gain, and other local builds disturbed the cold samples. Both Roslyn
builds emitted identical files, tracking and collection matched, and 38 targeted
tests passed per runtime in an isolated worktree (`bffe98b6`).
Keep both empty-range guards. Removing the inner guard cut six IL bytes and 16
Arm64 FullOpts bytes from the alias helper path, but let the generated-range body
inline into the x64 `AppendRange` listing, growing it from 219 to 466 bytes.
Neither project's emitter replay gained reliably against the identical-binary
control. Both Roslyn builds still emitted the same 582 files, tracked results
and collection matched, and 38 targeted tests passed on each runtime.
Keep the current byte-alias match loop. Checking the remaining slice before searching
saved eight IL bytes but grew Arm64 `AppendRange` from 616 to 628 bytes. The x64 cross
listing shrank from 219 to 218 bytes. Neither project replay showed a repeatable
gain against A/A, and allocation matched. Both Roslyn outputs, tracking, collection
and targeted suites matched. Keep the short-range patch as a probe.
Index inherited candidates by unique syntax-tree path during binding. Sorted candidates can reuse
one current semantic model. Keep the compilation-ordered lookup when paths repeat, and keep
all trees and models local to the iterator. A 512-pass replay of the real candidate set dropped
from about 14 to 8.1 ms and 7.56 to 5.55 MB per batch. Eight whole-project body edits were
0.8–8.8% faster and saved 4–12 KB each. Cold timings varied. The 582 generated files matched
in all four states, 577 models were reused and the collection probes passed. The fast iterator
moved from 460 to 586 IL bytes, 1,444 to 1,432 Arm64 FullOpts bytes and 1,565 to 1,518
cross-JIT x64 MinOpts bytes. The 356 IL-byte duplicate path and both Roslyn targets were
covered by the duplicate-path regression (`acd8a7e4`).
Keep the inherited-name fixed-point loop for sparse matches. Compaction into a live tail
preserved scan order, but a probe derived from 700 real declarations with 26 matches
took longer in all four rounds and allocated the same 15.65 MB per batch. Using only
the result of `HashSet.Add` to suppress a redundant pass also gave no repeatable gain.
Compaction increased `Find` from 258 to 306 IL bytes; both trials grew the Arm64 listing
from 1,252 to 1,272 B. The x64 cross JIT did not print the method. Both trials preserved
candidate order, 582 generated files, 577-model reuse and collection. The stopping-condition
trial passed 48 tests on each Roslyn target. Neither change was retained.
Keep the existing index growth until a capacity hint also improves lookup time. On the same
512-pass candidate probe, pre-sizing from the C# tree count reduced allocations from 5.55
to 2.27 MB but increased time in all four rounds. Keeping the current syntax root in the
iterator did not recover throughput. Adding 50% capacity headroom allocated 3.22 MB and was
still slower than the baseline in all four isolated pairs. For the size-only trial, iterator
IL grew from 586 to 624 B, Arm64 FullOpts from 1,432 to 1,516 B and cross-JIT x64 MinOpts from
1,518 to 1,694 B. The root and size-only trials kept all 582 files byte-identical, with the
same model reuse and recovery. Both targeted Roslyn suites passed on the headroom trial.
Compare base-member prefixes by index and check for type parameters without collecting containing-type arguments.
Share the type-parameter predicate with the rest of the generator. On the local mixed nested-base probe,
`SharedInstance` went from 109 bytes per check to zero, with byte-identical formatter output on the unit test project.
Check `Arity` at the current level while walking generic arguments. A membership query can stop at its first
match without a list or closure. Materialize outermost-first arguments in one list only when the caller needs
them. Repeated `IsGenericType` gates walked the containing types again and slowed all four isolated rounds.
On 131 nested types and 421 queries from the unit test project, the retained version cut each 128-pass
batch from 11,975,680 to 679,936 allocated bytes. Whole-project timings and allocations varied under machine
load. All 582 files matched across cold, cached and edited runs, 577 models were reused and the close and
edited-tree collection probes passed. `Mentions` moved from 120 to 116 IL bytes, 348 to 408 Arm64 FullOpts
bytes and 300 to 405 cross-JIT x64 bytes. The 4.8 and 4.12 targeted suites passed (`15a4a3c1`).
Append runtime namespaces and containing-type arguments directly to the type name's builder. Preserve unescaped
namespace names and outermost-first argument order. The local nested-name probe went from 604 to 436 bytes per
render, and the unit test project's 628 generated files remained byte-identical.
Keep the current open generic name builder until a replacement holds the project edit timing.
A single pooled builder reduced a 96-pass batch over 910 actual symbols from 40,816,128 to
9,633,024 allocated bytes and was faster in four isolated rounds. Method-body edits saved
28–50 KB but often took longer, including in an A/A/B replay. Appending directly to the
formatter registration signature without pooling also failed to show an edit timing gain.
The pooled method and helper took 33 + 109 IL bytes, 184 + 600 Arm64 bytes and 120 + 454 x64
cross-JIT bytes, versus 129, 508 and 410 B before. Both trials preserved all 582 generated
files, 577-model reuse and collection of the closed compilation and previous tree. The
direct-append trial passed 47 targeted tests each on Roslyn 4.8 and 4.12. Both were withdrawn.
Append formatter name stems directly, doubling literal underscores while appending each namespace or type name.
The local mixed-name probe went from about 619 to 403 bytes per render with unchanged hint names and source.
Store only the fixed-layout offset strings in member order, and skip them when the fixed
writer emits one raw body. Three actual fixed models (one raw body) saved 280,000 bytes
per 1,000 isolated passes in each M4 round, with mixed timing. Whole-project cold starts,
reopens and body edits had no stable gain; a follow-up alternating run did not reproduce
the generic-edit slowdown. All 582 generated files matched across four stages, all 577
models were reused after edits, and the closed compilation and previous tree collected.
FixedLayout moved from 266 to 281 IL bytes, 1,184 to 1,088 Arm64 FullOpts bytes and
728 to 745 offline x64 bytes. Both Roslyn builds passed 81 targeted tests (`f8eb5377`).
Share the existing bounded text scratch slot with fully qualified type-name rendering and return it in a
finally block. Keep only the final name string. Four real project cold replays saved 3.0–3.2 MB,
reopens saved 2.6–2.7 MB, and method-body edits saved 103–115 KB. Isolated rendering of actual project
symbols saved about 119 B per call with unchanged timing. All 582 files matched byte for byte across
cold, cached and edited runs, 577 models were reused, and the closed-project and previous-tree
collection probes passed. The render method grew from 78 to 93 IL bytes, 328 to 376 Arm64 bytes and
258 to 285 cross-JIT x64 bytes. The Roslyn 4.8 and 4.12 targeted suites passed (`461354bb`).
Use the same bounded scratch for interceptor signatures and forwarded arguments. Append qualified
types directly to the signature and return the buffer before rendering the target type name.
The real 122-tree unit test project saved 1.44–1.51 MB on cold runs, 1.70–1.73 MB on reopens
and 14–27 KB on collection method-body edits. Cached runs allocated the same, and the generic
method-body edit allocated 17–44 KB more. Four M4 pairs showed no stable end-to-end timing change.
All 582 files (3,240,332 bytes) matched across four stages, 577 models were reused, and the closed
compilation, symbols, previous tree and generated text collected. The interceptor's IL went from
465 to 507 bytes plus a 60-byte modifier helper. Arm64 FullOpts went from 2,012 to 2,188 plus
120 bytes; x64 offline ReadyToRun went from 1,253 to 1,207 plus 89 bytes. Both Roslyn builds passed
67 targeted tests (`7c60ecef`).
Reuse the same slot for runtime names and ordinary formatter stems. Keep the nesting formatter's
construction separate: the combined pooled method made the isolated nesting path slower in most rounds.
On actual project symbols, runtime and formatter rendering saved about 263 and 338 B per call and
median times fell about 6% and 12%, respectively. Nesting stayed on par. Four reopens saved
0.85–0.99 MB and four body edits saved 9–18 KB without a stable end-to-end time change. All 582 files
matched and 577 models were reused across both edits. The GC probes and both Roslyn test sets passed.
The runtime name's IL grew from 18 to 33 B and the formatter name's from 22 to 43 B. Their Arm64
listings grew from 136 to 184 B and 172 to 288 B, and their x64 cross-JIT listings from 106 to 120 B
and 124 to 200 B, respectively (`8b6fd14d`).
Render an open generic name into one bounded scratch buffer. Append each containing
type and its arity directly, then retain only the final string. Over 119 generic
declarations from the unit-test project, four warmed M4 pairs saved about 450 B
per name and reduced isolated median time by 19–27%. Whole-project timings varied
alongside identical-binary controls. The two body edits saved 29–53 KB per run.
All 583 generated files matched across four stages, all 578 models were reused
after either edit, and old compilation, syntax tree, symbol and output references
collected. `OpenName` went from 129 to 58 IL bytes plus an 85-byte helper,
508 to 336 + 644 Arm64 FullOpts bytes, and 315 to 149 + 205 offline x64 bytes.
Build prefetch member lists only for eligible formatter models, after confirming a property is reference-typed.
Skip the prefetch metadata in analyzer models and allocate the small member list only on the first match.
Recognize shared immutable arrays before walking their elements. The local 128-string equality probe went
from about 75 ns to 2 ns for shared arrays and stayed on par for distinct arrays with equal contents.
Use a cheap bucket key for emitted formatters, followed by full model equality. Include a member type
to distinguish common same-name edits. The local 97-member cache-hit probe avoided a full model hash
and the generated files of the unit test project stayed byte-identical.
Store a weak reference directly in a one-entry emission bucket. Allocate a list only for hash
collisions and compare complete models before reusing text. The real unit test project reopened
with 45–60 KB less allocation in four local rounds, without a repeatable timing change.
All 577 generated files matched byte for byte, all 572 models were reused on tracked edits,
and the old compilation, tree, symbol and generated text were collected after project closure.

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
Measure end-to-end runs with tracking disabled. On the real unit test compilation, Roslyn's
`RecordStepTree` took seconds on a cached run whose generator stages and output callbacks took under 2 ms.
Use tracked times to rank stages, and a separate untracked A/B with the same compiler arguments for the verdict.
Time model dependency migration directly before attributing an edit slowdown to the scan. On the collection
fixture it ran once per edit and took 0.02–0.03 ms. Keep `ForAttributeWithMetadataName` for attribute discovery:
the local general syntax-provider experiment reused bindings on warm runs, but did not improve cold startup
and duplicated Roslyn's alias and partial-declaration matching.

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
