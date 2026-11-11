# Building models without waste

Part of the `audit-generator` skill: what building a type model may allocate - serialization callbacks, member
collection, construction plans, registrations and layouts, the formatter-rule binder, `EquatableArray` and the
rendering of type and formatter names. Read it before changing `ModelBuilder`, `MemberCollector`, the construction
planner, `TypeFacts`' registration walks, the formatter-rule binder (shared with Nino.Core's runtime rules) or a type
name renderer, or before retrying an allocation trick there. Measurement terms are defined in
[measuring.md](measuring.md); every entry kept its output byte-identical.

## Contents

- [Serialization callbacks](#serialization-callbacks)
- [Members and construction](#members-and-construction)
- [Registrations and layouts](#registrations-and-layouts)
- [The formatter-rule binder](#the-formatter-rule-binder)
- [EquatableArray](#equatablearray)
- [Type and formatter names](#type-and-formatter-names)

## Serialization callbacks

- Allocate a callback list only after accepting the first callback of its kind. Track the four kinds accepted at each
  hierarchy level in a local mask and keep static before instance deserializing callbacks. Over 15 callback-bearing
  declarations from the real unit test project, the isolated build saved about 218 B per model in all four M4 pairs
  and took less time in three. Whole-project timing and allocation varied, including a second four-round reopen
  replay. All 583 files matched across four stages, 578 models were reused after each edit and closed-project
  references collected. Build shrank from 856 to 832 IL bytes, 3,864 to 3,752 Arm64 FullOpts bytes and 2,565 to 2,493
  offline x64 bytes. Both Roslyn builds passed 59 targeted tests (`57d3197e`).
- Copy a singleton deserializing callback list directly into its equatable array, and preserve static-before-instance
  ordering for multiple callbacks. Five of the real project's 15 callback models had one such call; bypassing both
  filters and concatenation saved 1,000 B per isolated pass over those 15 models in four M4 pairs. Isolated and
  whole-project timings varied alongside A/A. Build grew from 832 to 851 IL bytes, 3,836 to 3,856 Arm64 FullOpts bytes
  and 2,545 to 2,570 offline x64 bytes. All 583 files matched across four stages, 578 models were reused after either
  edit, collection passed and both Roslyn builds passed 59 targeted tests (`3e226e45`).
- Return the shared empty sequence before creating a callback-mark iterator for a method with no attributes; keep
  attribute order in the nonempty iterator and retain no symbols in the shared result. Scanning 3,403 real-project
  methods saved 185,120 B per pass in four isolated M4 pairs. Isolated medians improved against the paired baseline,
  but one identical-binary control was an outlier. Cold, reopen and body-edit timings and allocations did not
  establish a whole-project gain. All 583 generated files matched across four stages, 578 models were reused after
  either edit, and the old compilation, tree, symbol and output collected. Factory plus MoveNext IL grew from 183 to
  192 B, Arm64 FullOpts from 344 to 472 B and offline x64 from 386 to 420 B. Both Roslyn builds passed 59 targeted
  tests (`dca6dba7`).
- Do not replace the callback hierarchy lookups with nested attribute loops without a whole-project gain; keep the
  existing lookup and record both trials rather than claiming the isolated gain as build speed. Moving attributes
  outside the callback-kind loop removed repeated GetAttributes calls but slowed the isolated scan in three of four M4
  pairs. Hoisting AttributeClass once per attribute then improved the 911-type isolated scan by 2-13% in all four
  pairs, with equal allocations, but cold project timings were slightly higher in every pair and reopen and body-edit
  timings varied. Neither trial established an end-to-end gain. The revised Declares grew from 106 to 141 IL bytes,
  264 to 368 Arm64 FullOpts bytes and 250 to 406 offline x64 bytes. Answers and all 583 files matched, 578 models were
  reused, GC probes passed and both Roslyn suites passed 59 targeted tests.
- Use a short-circuiting symbol scan when the model only needs to know whether a serialization callback exists, and
  keep the ordered callback iterator for building calls and diagnostics. On 548 types from the unit test project, the
  per-type probe went from 402 to 64 B and found the same callbacks. Roslyn 4.8 and 4.12 each matched all 577 files
  byte for byte on cold and edited runs. Both edits reused all 572 models on Roslyn 4.12, and the closed compilation,
  tree, symbol and generated text collected. Cold and reopen timings varied, and the first generic fixture edit
  slowdown did not reproduce in a four-round follow-up with an identical-build control.

## Members and construction

- Process automatically collected members in declaration order, and allocate a per-level candidate list only for
  explicit indices, which still sort by index and name. In a 548-type probe, skipping only the sort cut allocation
  from 1,925 to 1,332 B per type. Sending automatic candidates directly through the existing deduplication rules cut
  it to 1,106 B with identical member order. Four cold project replays saved 73-115 KB per run. Reopening saved
  0.54-0.82 MB against the paired baseline across eight rounds. The initial reopen slowdown did not reproduce in the
  four rounds with an identical-build control. Both Roslyn builds matched all 577 files, the Roslyn 4.12 edits reused
  all 572 models, and the project-close and edit probes collected old objects.
- Allocate the ancestor list and visited set only when member collection finds a serializable base or interface, and
  collect the declaring type separately, after its ancestors. In the 548-type probe this reduced allocation from 1,106
  to 912 B per type with identical member order. With the lazy list, cold, edit and reopen timings overlapped the
  identical-build controls. All 577 generated files matched on Roslyn 4.8 and 4.12, 572 models were reused after both
  edits, and the close and edit collection checks passed.
- Do not traverse and collect in the same recursion yet (kept as an experiment): it reduced allocation further to 888
  B per type, but reopening the real project took longer than the paired baseline in four rounds.
- Delay scanning constructor and factory parameter names until member collection encounters a get-only property, and
  allocate the case-insensitive set on the first name. In the 548-type probe, allocating the set lazily alone went
  from 912 to 850 B per type. Delaying the scan reduced it to 786 B and kept member order identical. It saved 114-177
  KB per cold project replay. Cold, cached, edit and reopen timings varied, so keep the allocation result separate
  from whole-project latency.
- Walk overridden properties directly during member deduplication. The former LINQ predicate bound a delegate and
  created an iterator even when the member had no overridden property. On .NET 8 the 548-type probe went from 1,122 to
  789 B per type and from 399-412 to 352-359 ns. The .NET 10 default-tiering probe already allocated the same 786 B on
  both builds (why the runtime and tier matter: `measuring.md`). Real-project generated files and incremental reuse
  matched.
- Pass the visibility context directly when checking whether a derived member hides a base member, and check the
  override chain by symbol equality without a captured predicate. Across 233 inherited member probes from the unit
  test project, both .NET 8 and .NET 10 went from 38.04 to 1.92 B per check with identical decisions. The .NET 10
  project reopened with 0.17-0.32 MB less allocation across four rounds. Whole-project latency varied within the
  identical-build spread.
- Walk Roslyn's constructor and member arrays directly when choosing a construction plan. Preserve marked instance
  constructors before marked static factories, then check visibility before choosing an unmarked constructor, and
  avoid materializing factory and visible-constructor lists for ordinary parameterless types. The 507 actual project
  plans had identical creator, ordered arguments, factory and recreation fields. Their warmed isolated replay
  allocated about 329-361 KB less per full traversal, with variable elapsed time. Randomized real-project cold medians
  saved about 648-650 KB against two identical baseline controls, with cold elapsed time within 2%. All 583 generated
  files matched on both Roslyn builds across cold, cached and method-edit phases. Both builds reused 578 models and
  collected old compiler objects and generated text. The planner went from 1,003 to 1,002 IL bytes and 3,900 to 3,784
  Arm64 FullOpts bytes. The x64 cross listing grew from 2,372 to 2,527 bytes.
- Read required-member flags through typed Roslyn properties (the pinned 4.3.0 reference already exposes both flags),
  and allocate the result list only after finding a required member. The 507-plan replay retained identical ordered
  construction fields and saved 58,800 bytes per traversal. Four warmed pairs took 5-11% less time. Whole-project cold
  allocation medians fell by 35-44 KB. Process timings varied widely, including the identical-binary controls. The
  scan and flag predicate went from 102/82 to 94/72 IL bytes, 368/392 to 384/236 Arm64 FullOpts bytes, and 243/167 to
  248/154 x64 cross-listing bytes. A real Roslyn 4.3.0 host compiled the required field and property fixture with
  matching output. All three generator builds matched the real-project source and passed the collection probes.
- Share factory validation flags between discovery and the analyzer, and format NINO017 messages only where
  diagnostics use them. The 32 real factory-type probes matched their selections and saved 1,080 bytes per traversal
  in four pairs. Their warmed time fell by 4-11%. A method violating all five checks kept the same diagnostic text and
  ordering. The lookup and its validation callee went from 93/137 to 88/82 IL bytes, 264/824 to 260/280 Arm64 FullOpts
  bytes, and 178/599 to 176/207 x64 cross-listing bytes. Whole-project allocation stayed within the identical-baseline
  spread. All three generator builds matched real-project source and passed the collection probes.
- Skip type parameter collection, projection and constraint rendering for non-generic models; Roslyn's `IsGenericType`
  includes parameters on containing types. The real project had 495 non-generic models and eight arity-zero models
  that inherited parameters. Four default-JIT model-rebuild comparisons saved 38-67 KB per traversal and three took
  less time. Whole-project timings varied alongside identical binaries, and temporary counters confirmed that both
  edits and the cached run called `ModelBuilder.Build` zero times. All three generator builds emitted identical files
  across four phases and passed both collection probes. `Build` went from 1,115 to 1,140 IL bytes, 3,716 to 3,636
  Arm64 FullOpts bytes, and 3,288 to 3,364 offline x64 bytes. The pinned Roslyn 4.3 host compiled nested types with
  inherited constraints (`d1a40bd0`).
- Build prefetch member lists only for eligible formatter models, after confirming a property is reference-typed. Skip
  the prefetch metadata in analyzer models and allocate the small member list only on the first match.

## Registrations and layouts

- Collect factory registrations only for members and collection bodies that have them: enumerate the equatable array
  directly, skip the helper call for an empty array, and preserve sorting and deduplication for nonempty
  registrations. The 578-model replay had 85 registrations in 42 models. Four comparisons saved 112-137 KB per
  traversal; warmed times varied. The 122-tree project's cold median saved 387-400 KB against two identical baseline
  binaries. Collection-file edit time returned to the control range on the final variant. The three Roslyn builds
  matched every generated file across four phases, reused 578 models on edits, and passed the collection probes.
  `Build` went from 1,140 to 1,142 IL bytes, 3,636 to 3,524 Arm64 bytes, and 3,364 to 3,281 offline x64 bytes. The
  uncommon collection helper adds 58 IL, 288 Arm64 and 198 offline x64 bytes (`eea79b60`).
- Skip sorting empty closed registrations and enumerate nesting names in their existing order through one iterator.
  Only 43 of the 578 real-project models named closed types and three named nesting formatters. Four warmed model
  traversals saved 153-163 KB each, while whole-project cold and edit measurements stayed in the baseline spread. All
  three Roslyn builds matched generated source across the four phases and kept 578-model reuse and collection.
  `BuildRegistrations` went from 570 to 482 IL bytes, 2,500 to 2,100 Arm64 FullOpts bytes, and 1,861 to 1,526 offline
  x64 bytes. The iterator entry and `MoveNext` add 29/291 IL, 120/636 Arm64 and 98/397 offline x64 bytes (`e1fb67e9`).
- Key nesting lookup by the qualified name already used by formatter references, and extract the declaration name only
  on a match. The initial tuple variant avoided the concatenation but enlarged each entry; keeping the qualified name
  as the key retains the original value shape. The 11-name, 578-model replay saved about 2.84 MB per traversal, and
  the real project's cold allocation median fell by 2.30-2.32 MB against identical baselines. No stable whole-project
  time change was measured. The search went from 303 to 312 IL bytes, 1,264 to 1,196 Arm64 FullOpts bytes and 614 to
  640 offline x64 bytes. The three generator builds matched all generated files, reused every model on edits and
  passed the collection probes (`95a3102a`).
- Filter nesting lookup inputs by the shared generated suffix before stacking them; keep exact lookup and traversal
  order for candidates, and allocate sorted results on the first match. The real-project search replay kept all hits
  and reduced 96 traversals from 23.82 to 2.65 MB allocated. Four local pairs took 69-85% less time. Cold project
  medians saved 578-595 KB with variable timing. Short-warmup whole-model replays slowed, while the 2.7-second warmup
  gave three faster and one slower pair. The search grew from 312 to 413 IL bytes, 1,196 to 1,472 Arm64 bytes and 640
  to 858 offline x64 bytes, plus the small filter helper. All generator builds matched emitted files, 578-model reuse
  and collection (`207c1206`).
- Walk closed type arguments directly from Roslyn's immutable arrays, recursing through containing types first to keep
  the flattened traversal order without its list or interface boxing. The 418-root real-project replay found the same
  58 registrations in order and saved 22-38 KB per traversal in four comparisons. Whole-project cold, cached and edit
  timings varied within the control spread. `Walk` went from 394 IL bytes to 349 + 73, 1,284 Arm64 bytes to 1,108 +
  252, and 870 offline x64 bytes to 765 + 186, including its new argument helper. All three generator builds matched
  emitted source, 578-model reuse and collection (`ebf033ad`).
- Track only generic nodes in closed registration traversal: a non-generic leaf has no registrations or children, and
  arrays preserve the element's depth. Keep the first generic node in local state and allocate a set for the second
  distinct node. The 418-root replay preserved all 58 ordered registrations and saved 65-75 KB per pass. Four timings
  were below their paired baseline averages, with substantial A/A variation. Cold and edited real-project times
  remained in the control range. `Collect` kept 58 IL bytes and went from 284 to 220 Arm64 bytes and from 166 to 152
  offline x64 bytes. The new visited helper adds 98 IL, 324 Arm64 and 237 offline x64 bytes. All three builds matched
  generated files, edit reuse and collection (`2592b038`).
- In layout discovery, skip tracking non-generic leaves and unwrap arrays at the element's depth; for generic nodes,
  keep deduplication and argument traversal before asking Roslyn if the type is unmanaged. Moving that check earlier
  raised whole-project cold allocation by about 0.4 MB. The final order saved 18,128 bytes per 2,619-root replay and
  52-61 KB at cold start across two identical baseline binaries. Timings varied with the controls. The 83 ordered
  registrations, generated files, 578-model reuse and collection probes matched across all three Roslyn builds.
  `Collect` and `Walk` went from 94 + 234 IL bytes to 14 + 258 plus a 30-byte predicate. Arm64 FullOpts emitted 868 +
  136 bytes with `Collect` inlined, down from 384 + 788, and offline x64 emitted 26 + 574 + 79 bytes, down from 239 +
  533 (`87064687`).

## The formatter-rule binder

The binder that matches `[NinoFormatter]` rules is shared by the generator and Nino.Core's runtime rules, so each
entry here was measured on both: the 850-type generator replay (in both load orders) and runtime probes (wire bytes
and
focused tests on each runtime).

- Build a compact element encoding array directly from the bound formatter arguments, keeping order and duplicates.
  Replaying 850 real project types saved 144-159 KB per pass in both load orders and matched all resolver results.
  Timing varied with load order; three of four cold runs allocated less and one slightly more. Combined IL moved from
  2,459 to 2,521 bytes, Arm64 Tier 1 from 17,792 to 17,440 bytes, and x64 cross-JIT from 4,343 to 4,330. Both Roslyn
  builds matched all 577 files, reused all 572 models on edits and passed the collection checks. Each runtime passed
  88 focused tests.
- Pass the argument count and trailing traits instead of building a traits array. The same 850-type replay saved 285
  KB in both load orders, with identical resolution. Four cold replays saved 579-831 KB and their timings varied.
  `Bind` moved from 339 to 351 IL bytes, 10,980 to 10,616 Arm64 Tier 1 bytes, and 1,003 to 1,011 x64 cross-JIT bytes.
  Both Roslyn builds kept all 577 files, 572 model hits, and collection checks; both runtimes passed 69 focused tests.
- Keep the parameter adapter a struct and share its mutable body flag with a lazy factory list. Making the adapter
  itself a class added 23 KB per 850-type replay. The small shared state saved 75 KB in both load orders and 316-343
  KB in four cold project rounds. The two measured methods went from 503 to 541 IL bytes, 17,524 to 17,952 Arm64 Tier
  1 bytes and 1,356 to 1,283 x64 cross-JIT bytes. Resolver results, all 577 generated files, 572 model hits and
  collection checks matched. Both runtimes passed 81 focused tests.
- Keep the eager element list pending a change that avoids it on successful single-element bindings too. Moving the
  list to the shared state and creating it on first use saved 37 KB per 850-type replay in both load orders, but did
  not reliably improve whole-project latency; the binder, element resolver and accessor grew from 548 to 591 IL bytes,
  17,760 to 18,884 Arm64 Tier 1 bytes and 1,288 to 1,323 x64 cross-JIT bytes. Both Roslyn builds kept all 577
  generated files, 572 model hits and collection checks. Each runtime passed 81 focused tests.
- Do not add a separate first-element slot either: keep the list until a more direct representation removes the
  allocation without adding two collection states. The slot also avoided lists on successful singleton bindings and
  saved 56 KB per 850-type replay in both clean load orders and 94-168 KB in the four cold runs, but timing reversed
  with load order and varied under load. The three affected methods grew from 548 to 655 IL bytes, 18,528 to 19,572
  Arm64 Tier 1 bytes and 1,288 to 1,409 x64 cross-JIT bytes. Both Roslyn builds matched all 577 files and retained
  incremental reuse and collection.
- Delay the shared binder's formatter and resolution arrays until the pattern unifies. The runtime array rule's rank
  mismatch allocated 80 B instead of 152 B per call; successful matches allocated the same. The 850-type generator
  replay saved 7.8 KB in both orders. Whole-project and runtime timings varied. Binder IL was 406 to 414 bytes; the
  runtime specialization was 7,388 to 7,376 Arm64 Tier 1 bytes and 1,044 to 1,094 x64 cross-JIT bytes. Both Roslyn
  builds matched 577 files, model reuse and collection. Runtime wire output matched, each runtime passed 93 focused
  tests, and both portable builds passed.
- Use the non-null formatter slot as the resolved flag: both adapters produce reference types and only succeed with a
  non-null result. This removed 32 B per successful runtime array binding and saved 74 KB per 850-type generator
  replay in both load orders. Runtime misses were unchanged. Timings and whole-project allocation varied. Binder IL
  was 414 to 422 bytes, Arm64 Tier 1 was 7,488 to 7,380, and x64 cross-JIT was 1,094 to 960. Wire bytes and all 577
  generated files matched, incremental reuse and collection passed, and both runtimes passed 101 focused tests.
- Match parameterless formatter rules directly using shared empty outputs. Keeping transactional unification cloned an
  empty array; direct matching cannot write a slot and removed that last allocation. The runtime Uri rule went from 72
  B on success and 48 B on rejection to zero, and both cases beat both controls in four local rounds. Generic array
  rule timings varied at the same allocations. Binder IL was 422 to 453 bytes, the generic Arm64 Tier 1 listing 7,484
  to 7,944, and x64 cross-JIT 960 to 1,051. Runtime wire bytes, 577 generated files, all 572 model hits and collection
  matched.
- Match the first generic pattern into its fresh type slots as well (the caller discards them on rejection), and keep
  transactional unification for constraint inference on existing slots. The 850-type generator replay saved 113 KB in
  both load orders, and four cold runs saved 207-284 KB. The runtime array rule saved 40 B on success and rejection;
  rejected rules beat both controls in all four rounds, while success timing varied. IL and x64 cross-JIT stayed at
  453 and 1,051 bytes; Arm64 Tier 1 fell from 7,936 to 7,108. Wire bytes, 577 outputs, model reuse and collection
  matched.
- Filter interface definitions before cloning the constraint trial slots, then match into the disposable clone; keep
  ambiguity detection and commit only the sole binding. The 850-type replay saved 118 KB in both orders and four cold
  runs saved 87-153 KB. Runtime match, rejection and ambiguity probes all allocated less. Timings were noisy and some
  runtime samples overlapped scratch cleanup. IL stayed at 323 bytes, Arm64 Tier 1 was 6,144 to 6,108 bytes and x64
  cross-JIT 819 to 782. Wire bytes and all 577 outputs matched, incremental reuse and collection passed, as did
  focused tests.
- Do not prefilter definitions through the argument-reading API. Moving that prefilter before transactional matching
  saved 68 KB per 850-type replay and 1.23-1.34 MB in cold project runs, but the runtime base constraint miss
  allocated 80 B less and was slower than both controls in all four rounds. IL rose from 323 to 341 bytes, Arm64 Tier
  1 from 5,192 to 5,688 and x64 cross-JIT from 782 to 872. The definition-only check still retrieves generic arguments
  via `IsConstructed`. Both Roslyn builds kept all 577 outputs, model reuse and collection. Runtime wire and binding
  checks matched, and the focused suites and portable builds passed.
- Use a definition-only API for the prefilter and supertype scan. This saved 520-535 KB per 850-type replay and
  1.78-1.91 MB in four cold project runs. Runtime base-constraint rejection saved 112 B, and success saved about 288
  B. Timings varied without the repeatable miss regression of the argument-reading version. IL was 323 to 325 bytes,
  Arm64 Tier 1 5,192 to 3,752 and x64 cross-JIT 782 to 836. Runtime bytes, 577 generated files, reuse and collection
  matched.
- Keep the definition prefilter separate from the recursive matcher. Passing a flag to skip the already proven
  top-level checks kept the argument reads and did not save runtime allocations or consistently improve timings. The
  four default-JIT replays kept all 577 files byte-identical, and both Roslyn builds kept model reuse and collection.
  For `UnifyWithSupertype` and `Match` together, IL grew from 612 to 630 bytes, Arm64 Tier 1 from 5,596 to 6,516
  bytes, and x64 cross-JIT from 1,483 to 1,948 bytes. Try avoiding the actual argument read or moving the specialized
  match out of the shared recursion before retrying.
- Keep the compact element array for now (do not look elements up on demand). Looking up elements on demand and
  building the array only for tuples saved 18 KB per 850-type pass in both load orders, but cold allocation varied
  around baseline. The lookup repeated the scan for pair encodings and grew the combined IL from 2,533 to 2,673 bytes
  and the Arm64 Tier 1 methods from 16,988 to 22,148. The x64 cross-JIT listing for binding and formatting, excluding
  the shared array builder, moved from 4,076 to 4,330 bytes. Both Roslyn builds matched all 577 files, reused the
  models on edits and passed the collection checks.
- Flatten the last element of a long value tuple in the binder-owned array. On the real 8-, 9- and 15-element shapes
  the isolated helper saved 88 B per call and was faster in both load orders. Whole-project allocation and timing
  stayed within noise. `Flat` moved from 53 to 43 IL bytes, 676 to 292 Arm64 Tier 1 bytes and 134 to 114 x64 cross-JIT
  bytes. Both Roslyn builds kept all 577 files and all 572 model hits and passed collection checks. The two runtimes
  each passed 46 focused tests.
- Keep the simple flattening helper: do not append the rest as a character range into the emitter's leased builder,
  which made the model depend on the emitter's buffer pool. It saved 285 B per full encoding in the isolated
  long-tuple replay, with better timing in both load orders, but whole-project allocation and timing did not improve
  repeatably. The two affected methods grew from 2,116 to 2,206 IL bytes, 5,136 to 5,772 Arm64 Tier 1 bytes and 3,179
  to 3,381 x64 cross-JIT bytes. Both Roslyn builds matched all 577 files and kept incremental reuse and collection.

## EquatableArray

- Pass ordered enumerables directly into `EquatableArray<T>` when the constructor owns materialization. Removing the
  extra `ToArray` from project facts and layout names reduced the two methods by 15 IL bytes and kept all 577 replayed
  files, incremental hits and collection checks unchanged. The whole-project timing and allocation differences were
  within noise.
- Use the value-type enumerator when directly iterating an `EquatableArray<T>`; keep the enumerable interfaces for
  LINQ and preserve the array's value equality and ownership. The 577-file project replay saved about 18 KB of cold
  allocations with identical output. `LayoutEmitter.Members` fell from 320 to 310 IL bytes, from 1,472 to 1,364 Arm64
  bytes, and from 735 to 695 x64 ReadyToRun bytes.
- Filter accessor members in the loop to avoid boxing the array and allocating a LINQ iterator. Replaying 577
  formatters saved 112-113 KB per pass in both load orders, and cold project replays saved 43-130 KB. Timing varied
  with load order. All 582 outputs matched on both Roslyn builds, tracked body edits reused all 577 models, and
  closed-project objects collected. The .NET 8 and .NET 10 targeted suites passed (`0ccae04c`).

## Type and formatter names

- Compare base-member prefixes by index and check for type parameters without collecting containing-type arguments;
  share the type-parameter predicate with the rest of the generator. On the local mixed nested-base probe,
  `SharedInstance` went from 109 bytes per check to zero, with byte-identical formatter output on the unit test
  project.
- Check `Arity` at the current level while walking generic arguments, so a membership query can stop at its first
  match without a list or closure, and materialize outermost-first arguments in one list only when the caller needs
  them. Repeated `IsGenericType` gates walked the containing types again and slowed all four isolated rounds. On 131
  nested types and 421 queries from the unit test project, the retained version cut each 128-pass batch from
  11,975,680 to 679,936 allocated bytes. Whole-project timings and allocations varied under machine load. All 582
  files matched across cold, cached and edited runs, 577 models were reused and the close and edited-tree collection
  probes passed. `Mentions` moved from 120 to 116 IL bytes, 348 to 408 Arm64 FullOpts bytes and 300 to 405 cross-JIT
  x64 bytes. The 4.8 and 4.12 targeted suites passed (`15a4a3c1`).
- Append runtime namespaces and containing-type arguments directly to the type name's builder, preserving unescaped
  namespace names and outermost-first argument order. The local nested-name probe went from 604 to 436 bytes per
  render, and the unit test project's 628 generated files remained byte-identical.
- Append formatter name stems directly, doubling literal underscores while appending each namespace or type name. The
  local mixed-name probe went from about 619 to 403 bytes per render with unchanged hint names and source.
- Share the existing bounded text scratch slot (`TextScratch`, `emit-text.md`) with fully qualified type-name
  rendering and return it in a finally block, keeping only the final name string. Four real project cold replays saved
  3.0-3.2 MB, reopens saved 2.6-2.7 MB, and method-body edits saved 103-115 KB. Isolated rendering of actual project
  symbols saved about 119 B per call with unchanged timing. All 582 files matched byte for byte across cold, cached
  and edited runs, 577 models were reused, and the closed-project and previous-tree collection probes passed. The
  render method grew from 78 to 93 IL bytes, 328 to 376 Arm64 bytes and 258 to 285 cross-JIT x64 bytes. The Roslyn 4.8
  and 4.12 targeted suites passed (`461354bb`).
- Reuse the same slot for runtime names and ordinary formatter stems, but keep the nesting formatter's construction
  separate: the combined pooled method made the isolated nesting path slower in most rounds. On actual project
  symbols, runtime and formatter rendering saved about 263 and 338 B per call and median times fell about 6% and 12%,
  respectively. Nesting stayed on par. Four reopens saved 0.85-0.99 MB and four body edits saved 9-18 KB without a
  stable end-to-end time change. All 582 files matched and 577 models were reused across both edits. The GC probes and
  both Roslyn test sets passed. The runtime name's IL grew from 18 to 33 B and the formatter name's from 22 to 43 B.
  Their Arm64 listings grew from 136 to 184 B and 172 to 288 B, and their x64 cross-JIT listings from 106 to 120 B and
  124 to 200 B, respectively (`8b6fd14d`).
- Render an open generic name into one bounded scratch buffer: append each containing type and its arity directly,
  then retain only the final string. Over 119 generic declarations from the unit-test project, four warmed M4 pairs
  saved about 450 B per name and reduced isolated median time by 19-27%. Whole-project timings varied alongside
  identical-binary controls. The two body edits saved 29-53 KB per run. All 583 generated files matched across four
  stages, all 578 models were reused after either edit, and old compilation, syntax tree, symbol and output references
  collected. `OpenName` went from 129 to 58 IL bytes plus an 85-byte helper, 508 to 336 + 644 Arm64 FullOpts bytes,
  and 315 to 149 + 205 offline x64 bytes.
- Earlier trials on the open generic name builder, both withdrawn because neither held the project edit timing: a
  single pooled builder reduced a 96-pass batch over 910 actual symbols from 40,816,128 to 9,633,024 allocated bytes
  and was faster in four isolated rounds, and method-body edits saved 28-50 KB but often took longer, including in an
  A/A/B replay; appending directly to the formatter registration signature without pooling also failed to show an edit
  timing gain. The pooled method and helper took 33 + 109 IL bytes, 184 + 600 Arm64 bytes and 120 + 454 x64 cross-JIT
  bytes, versus 129, 508 and 410 B before. Both trials preserved all 582 generated files, 577-model reuse and
  collection of the closed compilation and previous tree. The direct-append trial passed 47 targeted tests each on
  Roslyn 4.8 and 4.12.
