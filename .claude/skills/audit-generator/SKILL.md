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
- Walk an assembly's types only through `AssemblyTypes.Of`.
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
Delay formatting an alias declaration until the occurrence count pays for it. Compute its length
from the alias and original name first. Four cold replays allocated 45–58 KB less, wall time
stayed within 0.3%, and all 577 outputs matched (`abc295cc`).
Share the same lease with block namespace indentation. Four cold replays saved 12–35 KB with
the test project's default language version (five block namespace files), and 4.80–4.95 MB with
C# 9 generator parse options (577 block namespace files). Both output sets matched byte for byte
and cold wall time had no repeatable change. The replay checks generated output. A separate
C# 9 consumer compiled and round-tripped with matching wire bytes (`b066e8db`).
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
Write a single indented line directly instead of allocating a closure for `CodeWriter.Indented`.
Four cold replays allocated 111–184 KB less with wall time within 0.7%. All 577 files matched,
the 572 models were reused on tracked edits, and the collection probes passed (`cb904042`).
Emit common write and inherited member blocks directly where the delegate only wraps a synchronous
call. Four cold replays saved 48–72 KB. The generated files were byte-identical, incremental reuse
and collection probes passed, and cold timing showed no repeatable change (`68aa8513`).
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
Pass registry entries and pointer fields as their existing equatable arrays. The registry emitter
can enumerate twice without copying the entries into a list. Replaying the real project registry
saved 16.7–16.9 KB per emission in both load orders. Cold and edit timing varied. All 577 outputs
matched, tracked edits reused all 572 models, and collection and targeted tests passed
(`de92db3d`).
Indent each registry case with a value-type scope to avoid its capturing action. Registry
emission saved about 55 KB per pass in both load orders. The 577 generated files matched,
all 572 models were reused on tracked edits, and collection and 58 targeted tests passed
(`3380bf7f`).
Insert registration statements directly into their ID cases in the existing order. This removes
the per-registration tuple arrays and the iterator chains in both registry emitters. The
isolated registry replay allocated about 35 KB less in both load orders. The 577 outputs matched,
incremental reuse and collection checks passed, and cold and edit times varied (`9ef11af1`).
Index registry buckets by their dense masked IDs and enumerate them in ascending order.
This avoids grouping, sorting bucket keys, and creating per-bucket delegates. Isolated
registry emission saved about 17.6 KB in both load orders. Cold timings stayed within 1%,
the 577 generated files matched, incremental reuse and collection checks passed, and
73 targeted tests passed (`5071d778`).
Keep the registry preamble writer separate unless its size is known cheaply. The real
project preamble was about 10 KB and a lease with a 256-character request grew beyond
the 8,192-character cap, allocating 288 bytes more per isolated emission. Counting the
exact length first saved about 12.5 KB, but repeated the attribute format in sizing code,
and emitter timings changed direction with the load order. Both emitted the same registry
bytes. Keep the size estimates in the probe.
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
Use the existing location and forwarder counts to estimate indentation in the
large interceptor namespace. This needs no extra scan and saved about 265 KB in both
isolated load orders. Eight method-body edits saved 256–277 KB with variable timing.
All 577 generated files matched, model reuse and collection checks passed, and 78
targeted tests passed on each of .NET 8 and 10 (`ce66cee9`).
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
Compare base-member prefixes by index and check for type parameters without collecting containing-type arguments.
Share the type-parameter predicate with the rest of the generator. On the local mixed nested-base probe,
`SharedInstance` went from 109 bytes per check to zero, with byte-identical formatter output on the unit test project.
Append runtime namespaces and containing-type arguments directly to the type name's builder. Preserve unescaped
namespace names and outermost-first argument order. The local nested-name probe went from 604 to 436 bytes per
render, and the unit test project's 628 generated files remained byte-identical.
Append formatter name stems directly, doubling literal underscores while appending each namespace or type name.
The local mixed-name probe went from about 619 to 403 bytes per render with unchanged hint names and source.
Build prefetch member lists only for eligible formatter models, after confirming a property is reference-typed.
Skip the prefetch metadata in analyzer models and allocate the small member list only on the first match.
Recognize shared immutable arrays before walking their elements. The local 128-string equality probe went
from about 75 ns to 2 ns for shared arrays and stayed on par for distinct arrays with equal contents.
Use a cheap bucket key for emitted formatters, followed by full model equality. Include a member type
to distinguish common same-name edits. The local 97-member cache-hit probe avoided a full model hash
and the generated files of the unit test project stayed byte-identical.

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
