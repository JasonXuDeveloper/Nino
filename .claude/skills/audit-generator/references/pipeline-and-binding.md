# Pipeline, binding and model reuse

Part of the `audit-generator` skill: what keeps the generator cheap on every keystroke beyond the rules in SKILL.md
- how calls are bound and reused, how type models carry their dependencies across edits, who owns the model cache,
how inherited types are discovered, and what the incremental stages that select, sort and filter may cost. Read it
before changing `ModelCache`, call binding or identity, the dependency scan, inherited-type discovery, the emission
cache or a pipeline stage's projection. Measurement terms ("all 577 files matched", "M4 pairs", "collection probes")
are defined in [measuring.md](measuring.md).

## Contents

- [Call binding](#call-binding)
- [Model dependencies and reuse across edits](#model-dependencies-and-reuse-across-edits)
- [Inherited-type discovery](#inherited-type-discovery)
- [Stages, projections and the emission cache](#stages-projections-and-the-emission-cache)

## Call binding

- Keep `ForAttributeWithMetadataName` for attribute discovery. The local general syntax-provider experiment reused
  bindings on warm runs, but did not improve cold startup and duplicated Roslyn's alias and partial-declaration
  matching.
- Reuse call bindings across identical reparses only with matching parse options, reference identities and source
  checksums, and require the same compilation options instance: the compiler server's resolver can throw from value
  equality. For a single directive-free method-body edit, bind that body again and map calls outside it back to their
  previous positions. Ask Roslyn for current interceptor locations even before the edit position, because the file
  checksum changed. Compare each edited output with a baseline that bound the calls normally.
- Skip binding explicit type arguments that name a method's own lexical type parameter from inside its body, and keep
  qualified names, contextual keywords and inferred calls on Roslyn's binding path. The real unit test project lost 47
  binding candidates, all checked against baseline semantic results, with identical output.
- Do not add a filter for single-argument `Serialize` with a cast or a typed `default`: it removed only three more
  candidates in that project, so keep the shared binding path (time binding on its own, `measuring.md`).
- Pass the current syntax context and cancellation token to static callbacks in the call cache, and build a model
  directly on a miss. Capturing callbacks allocate before the lookup, even when the lookup hits. Keep the sweep's
  temporary key list lazy as well, so saving a call does not allocate a closure for a sweep that may never run.
- Do not memoize method display strings in call identity until an alternative also holds the warmed edit timing; check
  sparse rebinding first. In a 2,416-call replay with 14 method definitions, sharing display strings within the weak
  compilation state and storing the method and type as separate strings saved 881,360 B per identity pass. Bypassing
  the memo for ordinary method-body edits kept this saving and reduced cold project allocation by 565-880 KB and
  reopen allocation by 778-820 KB across four M4 pairs. Whole-project timings varied with load. The independent warmed
  generic-fixture edit replay ranged from 14.5% faster to 34.7% slower after the bypass, with the slower pair's
  identical-binary spread at 8.4%. The bypass version grew `CallIdentity` from 34 to 131 IL bytes, 524 to 1,572 Arm64
  Tier 1 bytes, and 89 to 315 offline x64 bytes. All files matched on Roslyn 4.3, 4.8 and 4.12, both edits reused all
  579 models, and the old compiler objects and generated text collected. Keep the existing identity path.
- Scan interceptor feature names and metadata-name arity directly, without split arrays. The local feature lookup went
  from 280 B per call to zero, with identical real-project output.
- Share top-level usage registrations by bound type within `TypeFacts`, and keep binding and interceptor locations per
  call. Repeated calls in the unit test project rebuild the same closed-generic, layout and nesting registrations
  without that cache.

## Model dependencies and reuse across edits

- Carry type-model dependencies to replacement trees only for identical reparses (as above) or ordinary method-body
  edits. Constructor bodies, initializers and accessors still affect the model and must invalidate it. In the real
  unit test project, editing a method in the generic polymorphism fixture rebuilt 80 models even though all 80 were
  structurally unchanged.
- Let live compilations own model-cache state through weak keys: keep symbols in per-compilation tables and release
  old tree mappings after migrating call dependencies. A strong recent-project list kept closed projects' trees alive
  and masked the cost of reopening (how to measure both: `measuring.md`).
- Improve reopening by measuring the cold binding and model stages rather than extending their lifetime. In the real
  unit test project, cold call binding took roughly half a second and model construction another 130 ms.
- Do not skip identity and dependency work for calls without output: it reduced allocations but lost on default-JIT
  reopening.
- Enumerate constructor and named attribute arguments directly in the model dependency scan, preserving argument order
  and the same per-compilation ownership. Avoiding the LINQ projection and concatenation saved 42,832 B per pass over
  910 declarations in four default-JIT M4 pairs, with isolated medians down 11-25%. Whole-project latency and
  allocations varied, including reopening with an identical-binary control. The scan grew from 163 to 188 IL bytes but
  shrank from 704 to 612 Arm64 FullOpts bytes and 574 to 553 emulated x64 FullOpts bytes. All 582 files (3,241,440
  bytes) matched across the cold, cached and edited runs, all 577 models were reused after either edit, and the closed
  project inputs and generated text collected.
- Materialize a single declaring tree directly in the dependency node, and keep distinct-tree collection for types
  with multiple declarations, including partials declared more than once in one file. The fast path covered 907 of 910
  declarations in the unit-test project. Four isolated default-JIT M4 pairs each saved 261,216 B per pass, with
  elapsed time varying in both directions. Whole-project reopen runs saved 81,160-109,136 B with mixed timing.
  `Direct` grew from 362 to 405 IL bytes, 1,512 to 1,632 Arm64 FullOpts bytes and 1,218 to 1,285 emulated x64 FullOpts
  bytes. All 582 generated files matched byte for byte, the edit probes reused all 577 models, and old project inputs
  and generated text collected.
- Defer the traversal's tree set until it sees a second distinct tree; the dependency walk still visits the same
  symbols and returns the same ordered trees. In the unit-test project, 894 of 910 declarations depended on a single
  tree. Four isolated M4 default-JIT pairs saved 207,520 B per pass and reduced median time by 6-44%. Cold project
  runs saved 646-842 KB and reopening saved 441-476 KB, with mixed latency. `Dependencies` grew from 494 to 602 IL
  bytes, 2,272 to 2,716 Arm64 FullOpts bytes and 1,972 to 2,346 emulated x64 FullOpts bytes. All 582 generated files
  matched, both edit probes reused all 577 models, and closed-project collection passed.
- Keep the set construction in the traversal (do not outline the two initial insertions). Outlining reduced the Arm64
  FullOpts entry from 2,716 to 2,600 bytes but added a 152-byte helper; emulated x64 went from 2,346 to 2,275 plus 96
  bytes, and IL from 602 to 588 plus 22 bytes. Measured in separate warmed processes, the single-tree group still took
  3-11% longer than baseline over four M4 pairs, against -0.2% to +7% for an identical-binary control; the multi-tree
  group improved against the first baseline but varied against the control. Isolated allocations, 582 generated files,
  model reuse and collection matched. The candidate was withdrawn.
- Keep the work stack for the symbol traversal (do not hold one pending symbol in a local backed by a stack on
  branches). It saved 17,576 B per isolated 910-declaration pass and the dependency order matched, but three of four
  warmed default-JIT M4 pairs took 3-23% longer, and project reopen allocation and timing varied. The traversal moved
  from 602 to 632 IL bytes, 2,716 to 2,708 Arm64 FullOpts bytes, and 2,346 to 2,678 emulated x64 FullOpts bytes. The
  582 generated files, incremental model reuse and collection checks matched. The candidate was withdrawn.
- Keep the current original-definition check. Reading `OriginalDefinition` once and adding a reference-equality
  shortcut moved the traversal from 602 to 607 IL bytes, 2,716 to 2,660 Arm64 FullOpts bytes and 2,346 to 2,338
  emulated x64 FullOpts bytes; four isolated M4 pairs split between faster and slower, with equal allocations. Reading
  once without the shortcut shrank the same listings to 601, 2,652 and 2,329 bytes, but three of four isolated pairs
  and three of four cold project pairs took longer. Both variants kept the dependency order, all 582 files, all 577
  reused models and the collection checks. Both candidates were withdrawn.
- Leave the cache-hit state machine unchanged. The cache-hit profile put state acquisition near 1-2 ms, key rendering
  near 0.2 ms and dependency lookup near 0.1 ms per edit, with no rebuilt models.

## Inherited-type discovery

- Create inherited-type cache entries only after an assembly passes the Nino-reference filter. Keeping plain symbols
  in the temporary list avoided entries for unrelated references and saved about 33 KB of cold allocations in the unit
  test project replay. All 577 files matched. `ReferencedNinoTypes` dropped from 596 to 576 IL bytes and the previous
  compiler objects still collected after an edit and after the driver was released.
- Do not stash reference-name sets in the temporary list: it increased cold allocations by about 31 KB.
- Keep the shared `HashSet.Overlaps` operation. Explicitly enumerating the candidate set kept warmed allocations at
  the same 5,272 B per scan, slightly improved the isolated scan but left full replay timings flat and grew the Arm64
  Tier 1 body from 10,004 to 15,380 bytes. Both variants produced the same 577 files.
- Use the source module's referenced assembly symbols for inherited-type discovery instead of rebuilding a list
  through per-reference symbol lookups. The ordinary, extern-aliased, duplicate alias and linked-module probes
  returned equal symbol sets. The isolated scan improved in all four local rounds and fell from 576 to 478 IL bytes,
  with all 577 replayed files unchanged.
- Keep the existing per-assembly name collection. Replacing the cold LINQ name collection with a lazy set also matched
  all 577 files, but did not move replay time or allocations beyond noise; the entry and new collector totaled 524 IL
  bytes versus 478 before.
- For source declarations, collect unmarked candidates in the same pass that records marked names, and check base
  names by index to avoid the predicate allocation in each fixed-point pass. The real project replay saved 66 KB cold
  and 48 KB on method-body edits with the same 577 generated files. Timings were flat and the entry plus helper used
  296 IL bytes instead of 318.
- Seed that search with marked declarations by name alone: leave their base lists empty and allocate the unmarked
  declaration list only on the first usable base name. The 577-file replay saved about 7 KB cold and 29 KB on
  method-body edits. Output, cache hits and compiler-object collection matched, and the apparent model-edit slowdown
  did not persist after reversing the run order.
- Do not reuse only the last semantic model in candidate resolution: it did not reduce replay allocations.
- Keep the current file-path lookup until a larger project shows a stronger benefit. Filtering it to candidate paths
  saved about 19 KB on a method-body edit, without changing timings, incremental hits or any of the 577 files, but its
  iterator grew from 469 to 565 IL bytes, 1,388 to 1,864 Arm64 bytes and 1,183 to 1,381 x64 ReadyToRun bytes.
- Index inherited candidates by unique syntax-tree path during binding, so sorted candidates can reuse one current
  semantic model. Keep the compilation-ordered lookup when paths repeat, and keep all trees and models local to the
  iterator. A 512-pass replay of the real candidate set dropped from about 14 to 8.1 ms and 7.56 to 5.55 MB per batch.
  Eight whole-project body edits were 0.8-8.8% faster and saved 4-12 KB each. Cold timings varied. The 582 generated
  files matched in all four states, 577 models were reused and the collection probes passed. The fast iterator moved
  from 460 to 586 IL bytes, 1,444 to 1,432 Arm64 FullOpts bytes and 1,565 to 1,518 cross-JIT x64 MinOpts bytes. The
  356 IL-byte duplicate path and both Roslyn targets were covered by the duplicate-path regression (`acd8a7e4`).
- Keep the existing index growth until a capacity hint also improves lookup time. On the same 512-pass candidate
  probe, pre-sizing from the C# tree count reduced allocations from 5.55 to 2.27 MB but increased time in all four
  rounds. Keeping the current syntax root in the iterator did not recover throughput. Adding 50% capacity headroom
  allocated 3.22 MB and was still slower than the baseline in all four isolated pairs. For the size-only trial,
  iterator IL grew from 586 to 624 B, Arm64 FullOpts from 1,432 to 1,516 B and cross-JIT x64 MinOpts from 1,518 to
  1,694 B. The root and size-only trials kept all 582 files byte-identical, with the same model reuse and recovery.
  Both targeted Roslyn suites passed on the headroom trial.
- Keep the inherited-name fixed-point loop for sparse matches. Compaction into a live tail preserved scan order, but a
  probe derived from 700 real declarations with 26 matches took longer in all four rounds and allocated the same 15.65
  MB per batch. Using only the result of `HashSet.Add` to suppress a redundant pass also gave no repeatable gain.
  Compaction increased `Find` from 258 to 306 IL bytes; both trials grew the Arm64 listing from 1,252 to 1,272 B. The
  x64 cross JIT did not print the method. Both trials preserved candidate order, 582 generated files, 577-model reuse
  and collection. The stopping-condition trial passed 48 tests on each Roslyn target. Neither change was retained.
- Allocate a singleton base-name array directly and keep the existing declaration record. Of 704 real declarations,
  139 unmarked declarations had one base and ten had multiple bases. Keeping the first name in the record saved 11,128
  B per pass; the owned singleton array saved 12,232 B per pass and kept the record's size and equality. Its direct
  replay improved by about 20-40% in four M4 pairs. Whole-project cold and edit timings varied, while method-body
  edits saved about 4.8-18.3 KB per run. `From` went from 137 IL bytes to 74 plus the 122-byte `BaseNames` helper.
  Arm64 Tier 1 went from 2,232 to 1,608 plus 1,540 bytes. Offline x64 went from 464 to 253 plus 372 bytes. The ordered
  declaration digest, 583-584 generated files, 579-model reuse and collection matched across all three generator
  builds. Modern and legacy targeted tests passed (`2153c835`).

## Stages, projections and the emission cache

- Defer rendering a formatter sort key until two type names compare equal, and keep stable ordering for equal keys.
  The unit test replay had one tie among 517 registrations. Stage allocation fell from 273,920 to 72,904 bytes in four
  local rounds, and all 577 generated files matched. Stage and cold replay times varied, including after the
  scheduling control. The selection stage stayed cached on edits. Check tie-heavy inputs before generalizing this
  comparison.
- Project an optional usage registration directly and discard nulls before merging; keep the sort keys and the
  position of distinct filtering. The singleton arrays in `FormatterRegistrations` cost 73,200 bytes in the local unit
  test replay. The measured stage went from 347,120 to 273,920 bytes in four cold rounds with the same 517 results.
  The per-usage projection shrank from 27 to 7 IL bytes and from 172 to 52 Arm64 Tier 0 bytes (x64 cross JIT, 135 to
  43). The 577 generated files matched, method edits kept the stage cached and reopened projects released their
  previous compiler objects.
- Measure the state-table cost before adding a filter step to a projection, and compare skipping the allocation inside
  the existing projection first. A separate Roslyn `Where` before creating dispatch entries allocated more on all four
  local cold runs against their paired baselines, even though it projected fewer entries; the identical-binary
  controls also varied. All 577 outputs were byte-identical and closing and editing the project still released old
  objects.
- Return null for nonparticipants in the existing dispatch projection and filter the collected values before sorting.
  On the 572 models from the unit test project, the isolated projection created 469 entries and allocated 54,352 bytes
  instead of 70,840 across all four rounds. Timing varied in both directions in the isolated and full-project probes.
  The 577 generated files matched, the dispatch stage stayed cached on both edits, and closed projects released their
  Roslyn objects.
- Recognize shared immutable arrays before walking their elements in model equality. The local 128-string equality
  probe went from about 75 ns to 2 ns for shared arrays and stayed on par for distinct arrays with equal contents.
- Use a cheap bucket key for emitted formatters, followed by full model equality, and include a member type to
  distinguish common same-name edits. The local 97-member cache-hit probe avoided a full model hash and the generated
  files of the unit test project stayed byte-identical.
- Keep the by-value formatter text lookup weak: Roslyn can move an equal model to a new slot after a declaration is
  inserted, so reuse its immutable source text while a consumer holds it, then let the model and text collect. An
  index bound by entry count alone still held closed projects' generated texts in the real project GC probe.
- Store a weak reference directly in a one-entry emission bucket; allocate a list only for hash collisions and compare
  complete models before reusing text. The real unit test project reopened with 45-60 KB less allocation in four local
  rounds, without a repeatable timing change. All 577 generated files matched byte for byte, all 572 models were
  reused on tracked edits, and the old compilation, tree, symbol and generated text were collected after project
  closure.
