# Emitting text without waste

Part of the `audit-generator` skill: how the emitters build the formatter, layout, dispatch, registry and usages files
- the bounded scratch builder, alias rewriting, direct emission instead of callbacks, and sizing. Read it before
changing an emitter under `src/Nino.Generator/Generation` (except the interceptor file, `emit-interceptors.md`),
`CodeWriter`, `TextScratch` or alias rewriting, or before retrying an allocation trick there. What the text says is
`audit-generated-code`; measurement terms are defined in [measuring.md](measuring.md); every entry kept its output
byte-identical.

## Contents

- [The bounded scratch builder and aliases](#the-bounded-scratch-builder-and-aliases)
- [Formatter bodies, layouts and fixed formatters](#formatter-bodies-layouts-and-fixed-formatters)
- [Dispatch](#dispatch)
- [Registry and usages](#registry-and-usages)

## The bounded scratch builder and aliases

- Measure name rendering allocations before keeping a reusable builder. Keep `FormatterRef`'s bounded thread-local
  character buffer and hand immutable strings to models. Extending the buffer to general type naming saved allocations
  but slowed the local default-JIT hot rebuild samples. Delay constraint builders and registration lists until their
  path needs them. Keep traversal sets separate per root.
- Reuse common alias declarations and rewrite alias numbers only when their order changes; keep formatter alias
  ordering and renumbering together, separate from the self, Core and raw type aliases. A file-scoped body already
  has its final indentation, so concatenate it with the preamble and alias block directly.
  - Do not carry original indices and build a remap: it saved about 48 KB per replay of the unit test formatters, but
    throughput followed load order and the Arm64 alias method grew from 3,060 to 3,480 bytes.
- Bypass sorting and renumbering when a formatter has a single alias candidate. In a unit test project snapshot, 122
  files declared one formatter alias and 52 needed renumbering. Keeping the multi-alias order unchanged saved about
  307 KB of cold allocations in the real project replay. All 577 replayed files matched byte for byte, and the cold
  and method-body edit timings were flat.
  - Keep the simpler multi-alias path: checking whether the names are already ordered and precomputing the regex
    remap saved another 32 KB in cold replay (the remap is now a rename of the chosen ranges), but the emitter-only timing changed direction with the loaded variant
    order (all 577 files still matched). The candidate kept 310 IL bytes in the original helper and added a 177-byte
    renumbering helper (383 bytes combined before). Revisit only when a representative workload shows a larger
    benefit.
- Choose a file's aliases in one pass and copy its body once: `WithAliases` records each alias as ranges of the body
  string (a name is matched only outside the ranges of the names tried before it, where it would still be found had
  those been replaced) and `GeneratedCode.File` writes the ranges as their aliases while it indents, into a buffer
  sized to the written length; `global::NinoGen.` is removed in the writer's own buffer (`CodeWriter.Finish(omitted)`)
  before the body becomes a string. The rewrite per alias it replaced copied the body once per alias that paid. On the
  596-file unit test compilation (Roslyn 4.12 build, six alternated rounds of seven runs) it allocated 36.1 MB cold
  and 32.3 MB per call-file edit against 45.3 and 38.2 MB for the rewrite per alias, with every file byte-identical
  on both Roslyn builds and time on par (cold medians 259 against 281 ms, edits 320 against 349 ms). The base with
  streamed namespace aliases and a preindented interceptor path allocated 33.9 and 29.5 MB (321 and 368 ms): the
  difference is the body string of the interceptor and usage files, which the streamed path never made; it is the
  price of one mechanism.
- Lease one alias rewrite builder per generator load context, retaining at most 8,192 characters of capacity. The
  single slot saved 3.32-3.42 MB on each of four real unit-test project cold replays, close to the earlier
  thread-local experiment without storage per worker. Cold wall time stayed within 1%, and cache hits and edit times
  had no repeatable change. All 577 generated files matched byte for byte, all 572 models were reused on tracked
  edits, and the project-close and edit probes collected old Roslyn objects and output text.
- Do not reuse the formatter's own writer for alias replacement: it increased cold allocations by 0.38-0.46 MB in the
  four rounds, because a writer that already grew into multiple chunks can allocate again when cleared.
- Keep oversized rewrites out of the slot. Growing a leased buffer beyond the cap discards it and makes the next small
  rewrite allocate again. Bypassing the lease saved another 0.60-0.71 MB per cold replay across four rounds, with cold
  wall time within 0.6% and all outputs identical (`0febf582`).
- Lease the bounded builder before writing the formatter body and release it as soon as the aliases are chosen, so
  the file borrows the same slot and no fresh writer buffer is created per file. Four cold replays
  allocated 4.72-4.82 MB less and wall time stayed within 0.6%. All 577 outputs matched, tracked edits reused all 572
  models, and the collection probes and 71 targeted tests passed (`2616fc2c`).
- Share the same lease with block namespace indentation. Four cold replays saved 12-35 KB with the test project's
  default language version (five block namespace files), and 4.80-4.95 MB with C# 9 generator parse options (577 block
  namespace files). Both output sets matched byte for byte and cold wall time had no repeatable change. The replay
  checks generated output; a separate C# 9 consumer compiled and round-tripped with matching wire bytes (`b066e8db`).
- Size a large file's buffer to its written length (aliases and indentation counted), not a guess: the reserve of the
  body plus one quarter cost about 0.4-0.5 MB per replay over the written size, nearly all of it the 594 KB
  interceptor file.
- Delay formatting an alias declaration until the occurrence count pays for it, computing its length from the alias
  and original name first. Four cold replays allocated 45-58 KB less, wall time stayed within 0.3%, and all 577
  outputs matched (`abc295cc`).
- Prepend the inlining aliases to the formatter's existing declaration list before rewriting its body; this removes
  the one-use concatenation iterator and keeps the same declaration order. The 572-model emitter replay saved
  40.7-41.1 KB in both load orders, and the four cold replays saved 25-140 KB. Cold, cached and body-edit timings
  showed no repeatable change. The emitter went from 227 to 224 IL bytes, 1,100 to 1,104 Arm64 Tier 0 bytes and 396 to
  412 x64 cross-JIT bytes. All 577 files matched on both Roslyn builds, all 572 models were reused on edits, and
  closed-project objects collected.
- Write file-scoped aliases directly into the leased prefix builder before the final four-string concatenation,
  avoiding the separate joined declarations string. This saved 364-376 KB per cold unit-test project replay, with wall
  time within 1% and all 577 files identical. Four follow-up rounds with an identical-binary A/A control found no
  repeatable edit timing shift. Each tracked edit reused all 572 models and re-emitted only the block-namespace
  interceptor file (`0abfc154`). The step before it joined the declarations before concatenating, with fixed-arity
  concatenation: it saved about 229 KB per cold run with all 577 files identical, model-edit timings varying in both
  directions, and the emitter helper grew from 304 to 319 IL bytes.
  - Do not use the variadic concatenation: it saved about 590 KB but repeatedly produced slower model-edit samples.
- Keep the lazy formatter-candidate list for now given the small allocation difference and the extra collector state:
  the singleton probe held the first name in a local and allocated a list only on the second, saving about 16 KB per
  pass over 451 alias-eligible models in both load orders. Whole-project wall time showed no repeatable change. All
  577 outputs matched, incremental edits reused all 572 models, and old compiler objects and output collected.
- Keep the current declaration list instead of adding a remove-and-reinsert step for one alias. Reusing the main
  declaration list for a single formatter alias, then moving the declaration to the end, saved 12 KB per 572-model
  emission in both load orders, but whole-project cold, cached and edit timings did not show a repeatable change.
  `WithAliases` grew from 496 to 550 IL bytes, 2,356 to 2,608 instrumented Arm64 Tier 0 bytes and 1,105 to 1,240 x64
  cross-JIT bytes. Both Roslyn builds matched all 577 files, reused all 572 models on edits, and released
  closed-project objects.
- Do not defer the declaration list until the first profitable formatter alias: it avoids the empty list without
  reordering any declarations, but the allocation savings did not warrant the additional code. Across 577 real models,
  173 emitted formatter aliases and the trial saved 27,920 bytes per emission pass. Four cold runs saved 25-177 KB and
  reopens saved 12-27 KB, but whole-project and isolated timing varied. WithAliases fell from 496 to 486 IL bytes,
  while the alias write/rewrite helpers grew from 333 to 453 IL bytes, excluding the multi-alias path. Arm64 FullOpts
  grew from 1,572 to 2,144 bytes at the entry and 872 to 1,396 bytes across those helpers; offline x64 grew from 1,105
  to 1,205 bytes and 591 to 968 bytes, respectively. All 582 generated files, tracked reuse and collection matched,
  and both Roslyn builds passed 81 tests.
- Append the final requested blank line to `CodeWriter` before materializing its text: this saves a second full string
  copy without predicting output size. The isolated registry saved 20.1 KB and four cold project replays saved 461-660
  KB. Cold and edit timing varied. All 577 files matched, incremental model reuse and collection checks passed, and 88
  targeted tests passed (`a411f88c`).
- Write a single indented line directly instead of allocating a closure for `CodeWriter.Indented`. Four cold replays
  allocated 111-184 KB less with wall time within 0.7%. All 577 files matched, the 572 models were reused on tracked
  edits, and the collection probes passed (`cb904042`).
- Inspect trimmed line boundaries through a span in `CodeWriter.Line`, and keep writing the original text so the
  output's whitespace stays identical. The unit test project replay saved about 15 KB of cold allocations and matched
  all 577 files. Timing was flat and the helper gained 11 IL bytes.
  - Keep the simple split in `CodeWriter.Lines` for the registry and pool hook templates: scanning ranges saved only
    another 21 KB cold, matched the same 577 files and did not improve elapsed time, while the method grew from 62 to
    73 IL bytes, 260 to 320 Arm64 bytes and 205 to 268 x64 cross-JIT bytes. A later slice trial found the same
    (about 15.5 KB, no whole-project gain: [emit-interceptors.md](emit-interceptors.md), Writing the body).

## Formatter bodies, layouts and fixed formatters

- Emit common write and inherited member blocks directly where the delegate only wraps a synchronous call. Four cold
  replays saved 48-72 KB. The generated files were byte-identical, incremental reuse and collection probes passed, and
  cold timing showed no repeatable change (`68aa8513`).
- Apply the same rule to raw body, fixed-size and block formatter emission. A replay of the three affected models from
  the unit test project allocated a median 728 B less per group in both load orders. The full-project cold, cached and
  body-edit timings showed no repeatable gain. `EmitFixed` grew from 389 to 443 IL bytes, 1,444 to 1,532 Arm64 Tier 0
  bytes and 673 to 718 x64 cross-JIT bytes. Both Roslyn builds matched all 577 files, reused 572 models on edits, and
  released the old compilation, tree, symbol and generated text after close.
- Traverse raw member groups as ranges of the existing model array. Continue from the first group after emitting its
  region prefix, so a mixed body needs neither copied groups nor a second grouping pass.
- Accumulate the symbolic part of each raw offset as its emitted text; keep the constant separate and prepend it only
  when needed. The actual unit test formatter replay saved about 12 KB per pass, with identical source. Reuse the same
  offset formatting in fixed layouts to avoid copying the symbolic terms even for constant offsets.
- Emit constructor arguments from the member names the construction planner already matched. Walk the existing member
  array for direct initializers and accessor assignments, opening the initializer at its first entry. Scan short
  argument lists and keep the set lookup for wide constructors.
  - The 33 recreating models from the unit test project had at most three constructor arguments. Four warmed A/B
    pairs of their formatter emission saved about 23 KB per 33 models at identical output, with mixed elapsed time.
    Both Roslyn builds reproduced all 583 real-project files byte for byte, and a 12-argument fixture exercised the
    set lookup and matched its initializer and wire output.
  - The emitter fell from 771 to 765 IL bytes plus a 49-byte argument helper. Arm64 FullOpts fell from 2,536 to 2,108
    bytes plus the 292-byte helper. The x64 cross listing went from 1,718 to 1,698 bytes plus the 158-byte helper. The
    prior listings had separate closure methods for the member scans and argument projection.
  - The randomized project replay found similar cold, cached, and method-edit times to both identical baseline
    controls, with about 33–35 KB less allocation at the cold median. Both generator builds reused 578 tracked models
    and passed the collection probes for old compiler objects and source text.
- Use the same direct block emission for recursive collection formatters. The isolated nesting emitter saved 184 B
  with one formatter and 1,304 B with eight. All 582 files matched on both Roslyn versions across fresh, body-edit and
  model-edit replays. Nesting stayed cached on edits, closed-project objects collected, and 63 targeted tests passed
  on each of .NET 8 and .NET 10. Whole-project timings showed no repeatable improvement (`37c6743b`).
- Write local reader and writer copy prologues and write-back lines directly around the body. Constant lines and
  removal of the nested closures saved 214-317 KB in four cold replays. All 577 outputs matched, the 572 models were
  reused on tracked edits, and the collection probes passed. Cold and edit timings showed no repeatable change
  (`fe87801f`).
- Use a value-type scope for synchronous formatter body-mode changes: it preserves restoration while avoiding both the
  callback and a new mode object. Re-emitting the 572 models saved about 73 KB per pass in both load orders.
  Whole-project cold and edit timings varied, all 577 outputs matched, and tracked edits reused every model.
  Collection and 97 targeted tests passed (`5c1fc60b`).
- Append the partial accessor suffix before materializing the block file, saving about 45 KB per real-project
  formatter replay in both load orders. Renting the existing scratch buffer only for the partial emitter added about
  28 KB to the same replay, so keep the accessor writer separate. Whole-project timings and allocations varied. All
  582 files matched on both Roslyn builds, tracked edits reused the models, and collection and targeted tests passed
  (`dcd6f0c6`).
- Emit the partial accessor directly into the block file buffer and leave room for the generated pool hooks. Direct
  emission with the ordinary reserve added about 20 KB per formatter replay; allowing 3 KiB for pooled tails instead
  saved 50 KB in both load orders and 42-77 KB across four cold project replays. All 582 files matched on both Roslyn
  builds, tracked edits reused the models, and collection and targeted tests passed (`28e5b679`).
- Append the layout attribute to the existing bounded scratch buffer after materializing its member text. This avoids
  interpolated header fragments while preserving escaping, invariant ids and declaration order. The 572-model
  formatter replay saved 273 KB in both load orders; four cold project replays saved 255-418 KB. Cold, cached and
  method-body edit timings had no repeatable gain. `LayoutEmitter.Emit` went from 255 to 286 IL bytes, 1,500 to 1,436
  Arm64 Tier 0 bytes, and 601 to 668 x64 cross-JIT bytes. Both Roslyn builds matched all 577 generated files, reused
  all 572 models on edits, and released the closed-project objects.
- Append the members to the same builder and escape only their range, sharing the member writer with the analyzer's
  unescaped baseline representation. This saved another 184 KB per 572-model replay in both load orders and 82-301 KB
  across four cold replays. Whole-project timings varied. `Emit` moved from 286 to 333 IL bytes and the member writer
  from 354 to 313. Their combined Arm64 Tier 0 listings were 3,376 and 3,308 bytes, and x64 cross-JIT listings were
  1,455 and 1,470. Both Roslyn builds matched all 577 outputs, reused all models on edits, released the old project
  objects and passed 83 focused tests on each runtime.
- Append ranges between `global::` qualifiers instead of allocating plain type names for the member writer. This saved
  55 KB per 572-model replay in both load orders. Whole-project timing and allocation varied. `AppendMembers` moved
  from 313 to 311 IL bytes plus the 65-byte helper, from 1,720 to 1,676 instrumented Arm64 Tier 0 bytes plus 296, and
  from 707 to 659 x64 cross-JIT bytes plus 146. All 577 outputs matched on both Roslyn builds, all 572 models were
  reused on edits, and the collection checks passed.
- Keep literal escaping on the completed member range. Escaping the original fragments instead removed the two builder
  scans but saved no allocations in the 572-model replay. Timing reversed with load order, and the whole-project
  rounds were noisy. The affected IL rose from 709 to 792 bytes, Arm64 cold listings from 3,576 to 4,052 bytes and x64
  cross-JIT from 1,568 to 1,763 bytes. Both Roslyn builds matched all 577 outputs and retained model reuse and
  collection.
- Return empty member layout text directly and use the same bounded scratch for nonempty layouts. Four cold replays
  allocated 181-313 KB less with no repeatable timing change. The 577 output files matched, all 572 models were reused
  on the tracked edits, and the collection probes passed (`3db04963`).
- Use the known leaf layout for portability and raw descriptions before allocating measurement lists or a builder, and
  leave types that the canonical view handles differently, such as `Int128`, on the existing description path. The
  ten-leaf probe reduced allocation from 648 to 40 B per type and was about five times faster in both load orders.
  Four cold project runs with duplicate baselines saved about 0.54-0.68 MB against the paired baseline. Whole-project
  timings varied. The two entry methods grew from 200 to 304 IL bytes, 1,316 to 1,672 Arm64 Tier 0 bytes, and 552 to
  723 x64 cross-JIT bytes. Both Roslyn builds matched all 577 files, incremental reuse, and collection.
- Store only the fixed-layout offset strings in member order, and skip them when the fixed writer emits one raw body.
  Three actual fixed models (one raw body) saved 280,000 bytes per 1,000 isolated passes in each M4 round, with mixed
  timing. Whole-project cold starts, reopens and body edits had no stable gain; a follow-up alternating run did not
  reproduce the generic-edit slowdown. All 582 generated files matched across four stages, all 577 models were reused
  after edits, and the closed compilation and previous tree collected. FixedLayout moved from 266 to 281 IL bytes,
  1,184 to 1,088 Arm64 FullOpts bytes and 728 to 745 offline x64 bytes. Both Roslyn builds passed 81 targeted tests
  (`f8eb5377`).
- Count eligible models before changing fixed-layout emission for allocation savings; keep the simpler production
  shape until a representative project with many fixed-size models shows a larger effect. An exact-length offset array
  saved 192 bytes per replay of the three fixed-size formatters in the unit-test project. The emitter timing changed
  direction when the load order was reversed and `FixedLayout` grew from 266 to 287 IL bytes. Four whole-project cold
  replays had no repeatable change beyond noise. All 577 generated files matched, tracked edits reused all 572 models,
  and the old compilation objects and generated text collected.
- Allocate exact-length raw block offset arrays and omit header storage for non-keyed runs. The emitter-only replay of
  572 real models saved about 32 KB in both load orders. Cold and edit timings varied, all 577 generated files
  matched, and incremental reuse, collection probes, and 69 targeted tests passed (`f19ec5b7`).
- Keep the raw-run description as a readonly value: this removes one object per run and saved 17-18 KB in the
  emitter-only replay in both load orders. The 577 outputs matched, and tracked edits, collection probes and 69
  targeted tests passed (`8a3b47aa`).
- Keep offset construction uniform until a keyed workload shows a measurable benefit. Skipping the separate
  length-offset expression when a keyed header packs both fields did not reduce allocation reproducibly: the project
  had 50 packed header stores, and the two emitter load orders each differed by 216 bytes in opposite directions. The
  577 outputs, incremental reuse and collection checks matched.

## Dispatch

- Find dispatch ID collisions with a set of seen IDs and allocate a collision set only on demand; keep the original
  order and omit every entry with a colliding ID. This saved about 100 KB per dispatch emission on the unit test
  project with identical output.
  - Do not stream the generated checks instead of materializing their list: it enlarged the Arm64 emitter method from
    328 to 1,044 bytes and gave mixed timings.
- Visit base classes and interfaces separately in dispatch, preserving their order without boxing either array for
  `Concat`. The isolated dispatch replay saved a median 43 KB in both load orders, with no repeatable timing gain.
  Both Roslyn builds emitted identical files, dispatch stayed cached on body edits, old project objects collected, and
  70 targeted tests passed on each of .NET 8 and .NET 10 (`284d0c63`).
- Write exact dispatch checks directly from the known subtype list. The dispatch replay saved a median 68 KB per
  emission in both load orders, and four cold project replays saved 39-122 KB. Timings varied. All 582 outputs matched
  on both Roslyn builds. Body edits reused the models and dispatch output, closed-project objects collected, and 70
  targeted tests passed on each of .NET 8 and .NET 10 (`7fed214a`).
- Reuse the sorted closed instances and their declaration ordinals to emit the read and write selections directly.
  This saved a median 47 KB per dispatch emission in both load orders on the 31 closed classes in the unit test
  project. Whole-project cold and edit timings showed no repeatable gain. All 582 outputs matched on both Roslyn
  builds, dispatch stayed cached on body edits, old project objects collected, and both targeted suites passed
  (`bf5f8ae7`).
- Skip the filtered list and collision set when a dispatch bucket has at most one candidate, checking the singleton
  against the base ID before reusing its list. This saved about 21 KB per dispatch replay in both load orders.
  Whole-project timings and allocations varied. All 582 files matched on both Roslyn builds, dispatch stayed cached on
  body edits, project objects collected, and 70 targeted tests passed on each of .NET 8 and .NET 10 (`167d32ce`).
- Reuse the original bucket for two to four candidates when their IDs are distinct from each other and from the base
  ID. The unit project had 27 such small emitted dispatches. An isolated replay saved about 7.4 KB in both load
  orders, with no repeatable timing gain. The filter and helper together grew from 213 to 300 IL bytes, 684 to 892
  Arm64 FullOpts bytes, and 493 to 686 x64 cross-JIT bytes. Ordered buckets through five candidates matched the
  collision rules, and both Roslyn builds matched all 577 generated files and kept dispatch cached and old project
  objects collectible.
- Keep list-backed dispatch buckets for emission. Storing the first candidate directly and allocating its list only on
  the second saved about 3.9 KB per isolated unit-project emission in both load orders, but the wider dictionary value
  and the count/index checks increased the four sampled methods and new bucket members by 163 IL bytes, and the four
  machine-code listings grew by 556 Arm64 FullOpts bytes and 421 x64 cross-JIT bytes. Whole-project allocation and
  timing showed no repeatable improvement. Both Roslyn builds matched all 577 generated files, kept dispatch cached,
  and released old project objects.
- Measure the cost of indexing dispatch bases before adding a filter for unused edges: the same fixture had 22 unused
  ancestor edges across 14 names and no unused closed-generic edges, compared with 232 dispatch bases.

## Registry and usages

- Pass registry entries and pointer fields as their existing equatable arrays; the registry emitter can enumerate
  twice without copying the entries into a list. Replaying the real project registry saved 16.7-16.9 KB per emission
  in both load orders. Cold and edit timing varied. All 577 outputs matched, tracked edits reused all 572 models, and
  collection and targeted tests passed (`de92db3d`).
- Pass the existing equatable arrays to the usage emitter, enumerate their struct enumerators and reuse the layout
  names for both passes. On the real unit test input this removed about 2.4 KiB of allocation per emission in all four
  isolated local rounds. IL went from 713 to 634 bytes, Arm64 instrumented Tier 0 from 3,864 to 3,188 bytes, and x64
  offline code from 1,695 to 1,649 bytes. The 236,789-byte usages file and all other outputs matched. Incremental hits
  and collection were preserved, and timings varied in both directions.
- Allocate metadata function-pointer registration lists only when a module contributes an entry, and skip sorting
  empty results. The 216-reference Nino.UnitTests replay saved 53,568 bytes per complete scan in four M4 comparisons.
  Scan, cold generation and edit timing showed no repeatable change beyond the controls. The retained change grew Scan
  from 237 to 254 IL bytes, from 7,072 to 7,488 Arm64 Tier 1 bytes and from 581 to 619 offline x64 bytes. The Arm64
  caller shrank from 3,128 to 2,632 bytes. Outputs matched on Roslyn 4.3, 4.8 and 4.12, edits reused all 216 reference
  results, collection checks passed, and three targeted configurations passed 41 tests each (`c00db141`).
- Do not check the value-type base before scanning fields in that scan: it made two warmed comparisons about 22%
  slower.
- Indent each registry case with a value-type scope to avoid its capturing action. Registry emission saved about 55 KB
  per pass in both load orders. The 577 generated files matched, all 572 models were reused on tracked edits, and
  collection and 58 targeted tests passed (`3380bf7f`).
- Emit the registry switches directly from their bucket lists, removing the capturing callback and boxed enumerator
  per switch. The 64-switch registry replay saved about 11 KB in both load orders. All 582 files matched on both
  Roslyn builds, body edits reused the registry entries, old project objects collected, and both targeted suites
  passed (`cdf0e8b2`).
- Append numeric case labels directly to the code writer buffer. The registry replay saved about 40 KB per emission in
  both load orders and four cold project replays saved 78-131 KB, with no repeatable timing change. Both Roslyn builds
  emitted the same 582 files, registry entries stayed cached on body edits, project objects collected, and both
  targeted suites passed (`3687fb79`).
- Append bucket numbers directly in both the outer switch labels and method headers. The 64-bucket registry replay
  saved about 17 KB in both load orders. Whole-project time and allocation varied. All 582 files matched on both
  Roslyn builds, registry entries stayed cached on body edits, project objects collected, and both targeted suites
  passed (`5309a6e5`).
- Insert registration statements directly into their ID cases in the existing order, removing the per-registration
  tuple arrays and the iterator chains in both registry emitters. The isolated registry replay allocated about 35 KB
  less in both load orders. The 577 outputs matched, incremental reuse and collection checks passed, and cold and edit
  times varied (`9ef11af1`).
- Index registry buckets by their dense masked IDs and enumerate them in ascending order, avoiding grouping, sorting
  bucket keys, and per-bucket delegates. Isolated registry emission saved about 17.6 KB in both load orders. Cold
  timings stayed within 1%, the 577 generated files matched, incremental reuse and collection checks passed, and 73
  targeted tests passed (`5071d778`).
- Count cases before filling each bucket to allocate its backing array at the final size. On the real project, the
  64-bucket registry replay saved about 16 KB in both load orders, and four cold runs saved 11-43 KB without a
  repeatable timing change. All 582 files matched, registry entries stayed cached on body edits, old project objects
  collected, and 69 targeted tests passed on each of .NET 8 and .NET 10 (`ed1ac897`).
- Store the sorted cases in one array grouped by cumulative bucket offsets. The registry replay saved another 4 KB by
  removing the per-bucket objects and array headers. The dispatch and switch emitters together shrank by 60 Arm64
  FullOpts bytes and 375 x64 cross-JIT bytes. Whole-project timing and allocation varied. Both Roslyn builds emitted
  identical files, incremental and collection checks passed, and both targeted suites passed (`91612f39`).
- Sort the cases once by masked bucket and then by signed ID, and walk the contiguous groups directly to avoid the
  second case array and offset table.
  Isolated registry replay saved 12,032 B in both load orders and four cold project runs saved about 20-50 KB. Timings
  varied. The ordering and dispatch methods plus comparer went from 515 to 499 IL bytes, 2,956 to 2,320 Arm64 FullOpts
  bytes, and 1,279 to 900 x64 cross-JIT bytes. Both Roslyn builds matched all 577 generated files, kept registry
  entries cached, and released old project objects.
- Size the local case dictionary during the existing generic-registration preamble scan: count both IDs when a type
  has a distinct wire name and skip open generics. This saved 52,240 B per isolated registry emission and 11-84 KB in
  four cold runs. Timing gains varied with load order. `EmitRegistry` grew from 924 to 970 IL bytes, 2,924 to 2,928
  Arm64 FullOpts bytes, and 1,791 to 1,880 x64 cross-JIT bytes. Both Roslyn builds kept all 577 outputs, incremental
  reuse, and collection.
- Seed the usages case dictionary with the sum of the formatter, factory, and closed-generic counts, avoiding a second
  scan for the rare alternate IDs. The isolated usage replay saved about 41.7 KB in both load orders and four cold
  project runs saved about 26-52 KB. Timing varied. `EmitUsages` grew from 638 to 661 IL bytes, 2,972 to 3,028 Arm64
  FullOpts bytes, and 1,695 to 1,746 x64 cross-JIT bytes. Both Roslyn builds kept all 577 outputs, reuse, and
  collection.
- Format concrete registration statements with their final ID argument directly: reuse the statement for the normal ID
  and format an alternate only for a distinct name ID. This saved 247 KB per isolated registry emission, 59 KB per
  usage emission, and 291-328 KB across four cold project replays. Whole-project timings varied. `AddStatements` and
  its new helper together fell from 454 to 420 IL bytes, 976 to 888 Arm64 FullOpts bytes, and 837 to 788 x64 cross-JIT
  bytes. Both Roslyn builds matched all 577 outputs, incremental reuse, and collection.
- Emit the call to the same-namespace usages registry without a generated namespace prefix; otherwise that single
  prefix makes alias processing copy the whole registry body. The isolated replay saved 244,288 B in both load orders
  and four cold project runs saved 215-255 KB. Timings varied. `EmitRegistry` kept its 970 IL, 2,928 Arm64 FullOpts,
  and 1,880 x64 cross-JIT bytes. Both Roslyn builds matched all 577 files and preserved incremental reuse and
  collection.
- Keep the registry preamble writer separate from the lease unless its size is known cheaply, and keep the size
  estimates in the probe. The real project preamble was about 10 KB and a lease with a 256-character request grew
  beyond the 8,192-character cap, allocating 288 bytes more per isolated emission. Counting the exact length first
  saved about 12.5 KB, but repeated the attribute format in sizing code, and emitter timings changed direction with
  the load order. Both emitted the same registry bytes.
- Start the separate preamble writer at 256 characters. Isolated replays with zero or one generic registration saved
  7,680 B and eight saved 6,000 B in both load orders. The full unit project added 288 B to isolated emission. Neither
  its four cold replays nor the isolated timing controls showed a repeatable change. The emitter kept 970 IL and 1,880
  x64 cross-JIT bytes, and Arm64 FullOpts went from 2,928 to 2,920. Both Roslyn builds matched all 577 files, reuse,
  and collection.
