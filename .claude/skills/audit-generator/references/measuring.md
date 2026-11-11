# Measuring the generator

Part of the `audit-generator` skill: how the allocation and timing evidence in the other references was gathered,
what each phrase in it means, and what counts as a verdict. Read it before measuring a generator change, before
reading or writing an entry in `pipeline-and-binding.md`, `model-allocations.md`, `emit-text.md` or
`emit-interceptors.md`, and before claiming that a generator change made builds or the IDE faster.

## The replay harness and its vocabulary

- Replay the real project, not only `GeneratorBenchmark`. The entries are measured on the compilation of
  tests/Nino.UnitTests rebuilt from its `csc` line (SKILL.md, Measure): 122 syntax trees, 216 metadata references,
  about 2,400 top-level calls, 548-910 types depending on the probe, 572-579 models and 577-584 generated files (the
  counts grew as the test project grew, so each entry keeps the counts it was measured at; one run wrote 582 files of
  3,241,440 bytes, another 3,240,332 bytes). Why: `GeneratorBenchmark`'s comment edit lands in the file of every
  model, and its synthetic calls bind in microseconds, so it hides what a real edit costs.
- Run every candidate through the same four stages: cold (fresh driver), cached (same trees), a method-body edit (the
  collection fixture or the generic polymorphism fixture) and a model edit. Why: a change can win cold and lose on the
  edit the IDE pays on every keystroke.
- Prove the output with "all N files matched": hash every generated source of base and head in every stage, on every
  generator build the change touches (Roslyn 4.3 for the pinned 4.3.0 reference host, 4.8 and 4.12; "all three
  generator builds").
- Prove the caching with "N models reused": the tracked edits must rebuild no model (temporary counters once confirmed
  that both edits and the cached run called `ModelBuilder.Build` zero times), and the stage named in the entry
  (dispatch, registry, nesting, selection) must stay cached on body edits.
- Prove nothing leaks with the collection probes: after the project closes (the driver dropped), and after an edit
  replaces a tree, the old compilation, syntax tree, symbols and generated text must be collectable (weak references
  and a full GC).
- Measure an isolated replay of the one stage or emitter the change touches (for example "the 572-model emitter
  replay", "the 850-type replay", "the 512-pass candidate probe") to see its allocation, then the whole project (cold,
  reopen and edit runs) to see whether it matters.
- Measure emission allocation on the first generator pass, since later passes can reuse cached formatter source.
- Read "M4 pairs" as paired base/head runs on a local Arm64 machine with the default JIT (tiered), and "four rounds"
  as four such pairs. Local numbers decide what to keep; they are never published (root `AGENTS.md`, Numbers).
- Record the code size with every kept or withdrawn change: IL bytes, the Arm64 listing (FullOpts, Tier 0,
  instrumented Tier 0 or Tier 1, as the entry says) and an x64 listing (offline/cross-JIT, MinOpts or ReadyToRun) of
  the methods the change touched, plus any new helper ("349 + 73"). Why: a saving that doubles a method's machine code
  is not free (`optimize`, `audit-generated-code`).
- Run the targeted test suites (the generator, incremental and analyzer tests touched) on both Roslyn builds and on
  .NET 8 and .NET 10, and record the counts ("38 targeted tests passed per runtime").

## What counts as a verdict

- Measure end-to-end runs with tracking disabled, and use tracked times only to rank stages. Why: Roslyn's
  `RecordStepTree` took seconds on a cached run of the real unit test compilation whose generator stages and output
  callbacks took under 2 ms, so tracked driver wall time is no verdict (cached tracking paid `RecordStepTree` while
  the stages did no work). The verdict is a separate untracked A/B with the same compiler arguments.
- Pair every candidate with an identical-binary (A/A) control and run the isolated replay in both load orders. Why: on
  the local machine timings routinely reversed with the load order, and several first slowdowns (the generic fixture
  edit after the short-circuiting callback scan, the reopen after direct member candidates, a cold slowdown after the
  interceptor forwarder reserve, the generic-edit after fixed-layout offsets) did not reproduce against an
  identical-build control.
- Report allocation and latency separately, and do not claim a build-time gain from an isolated allocation or timing
  win. Most entries saved bytes with whole-project timings "within the controls" or "varied"; say so (`57d3197e`).
- Keep an allocation saving whose whole-project time stayed within the A/A spread only when it adds no complexity
  (SKILL.md, Measure; the owner's call in the review of codex/nino-audit); state which criterion a change met. Three
  mechanisms kept that way were later removed with time on par (the streamed namespace aliases, the second
  interceptor path, `EmittedFormatters`' generations: `emit-interceptors.md`, Removed).
- Measure the active-project edit with the prior compilation and driver alive, and measure reopening separately after
  dropping both and collecting. Why: a strong recent-project list kept closed projects' trees alive and masked the
  cost of reopening (`pipeline-and-binding.md`, weak keys).
- Time the suspected step directly before attributing an edit slowdown to it: model dependency migration ran once per
  edit on the collection fixture and took 0.02-0.03 ms.
- Time closed-call binding separately from identity, dependencies, usage and interception. Binding dominated the
  instrumented project even when machine load varied.
- Check the runtime and tier before predicting an allocation from IL: the LINQ predicate in member deduplication
  allocated on .NET 8 (1,122 B per type) but the .NET 10 default-tiering probe already allocated the same 786 B on
  both builds, although the two allocation calls were still present in the baseline Arm64 FullOpts listing
  (`model-allocations.md`, overridden properties).
- Count the inputs a change serves before generalizing it (ties among sort keys, fixed-size models, keyed headers,
  small dispatch buckets): several changes saved little because the unit-test project had few such inputs (one tie
  among 517 registrations; three fixed-size formatters; 50 packed header stores).
