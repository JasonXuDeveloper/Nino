# Emitting the interceptor file

Part of the `audit-generator` skill: how the interceptor emitter builds its one large file (on tests/Nino.UnitTests,
about 1,200 distinct calls at about 2,000 locations) - rendering calls once, writing at the final indentation,
aliases appended while writing instead of rewritten after, and sizing. Read it before changing the interceptor
emitter, its aliases or location attributes, or before retrying an allocation trick there. The bounded scratch
builder it shares is in [emit-text.md](emit-text.md); measurement terms are in [measuring.md](measuring.md); every
entry kept its output byte-identical.

## Contents

- [Rendering calls](#rendering-calls)
- [Writing the body](#writing-the-body)
- [Aliases](#aliases)

## Rendering calls

- Use the bounded scratch for interceptor signatures and forwarded arguments: append qualified types directly to the
  signature and return the buffer before rendering the target type name. The real 122-tree unit test project saved
  1.44-1.51 MB on cold runs, 1.70-1.73 MB on reopens and 14-27 KB on collection method-body edits. Cached runs
  allocated the same, and the generic method-body edit allocated 17-44 KB more. Four M4 pairs showed no stable
  end-to-end timing change. All 582 files (3,240,332 bytes) matched across four stages, 577 models were reused, and
  the closed compilation, symbols, previous tree and generated text collected. The interceptor's IL went from 465 to
  507 bytes plus a 60-byte modifier helper. Arm64 FullOpts went from 2,012 to 2,188 plus 120 bytes; x64 offline
  ReadyToRun went from 1,253 to 1,207 plus 89 bytes. Both Roslyn builds passed 67 targeted tests (`7c60ecef`).
- Render interceptor calls and nested formatter names into one string through the existing bounded character buffer.
  Four cold replays saved 1.52-1.54 MB, and eight method-body edits saved 1.55-1.56 MB. Isolated emitter replays saved
  about 630 KB and their median time fell 16-25% in both load orders. Whole-project timings varied. All 577 outputs
  matched, model reuse and collection checks passed, and 78 targeted tests passed on each of .NET 8 and 10
  (`0ff49c6a`).
- Preserve formatter reference identity through unchanged twin substitutions, allocating a replacement argument array
  only after the first changed child. This avoided about 676 KB and reduced median emitter time by 7-11% in both
  isolated load orders. The eight method-body edit samples saved 632-664 KB, with variable whole-project timing. All
  577 outputs matched, tracked model reuse and collection checks passed, and 78 targeted tests passed on each of .NET
  8 and 10 (`b467a67c`).
- Memoize rendered interceptor calls by their shape for one emission, then group by the rendered text as before:
  multiple source locations can share the same call. Both isolated load orders saved 308 KB and stayed within 3% in
  time. Method-body edits saved 295-312 KB in eight samples with variable timing. All 577 files matched, model reuse
  and collection checks passed, and 78 targeted tests passed on each of .NET 8 and 10 (`d6556222`).
- Keep the call-text dictionary scoped to emission; count the structural hash cost before caching sort keys. Reusing
  the structural key for the earlier sort saved only 2-19 KB across the eight method-body edit samples, with variable
  whole-project timing. In the tracked replay the sort stage took 2.43 to 5.00 ms cold and 1.47 to 2.05 ms on the body
  edit. The 577 files, model reuse and collection results matched.
- Bound the call-text dictionary's initial reserve to 256 entries. The unit-test project had 1,205 keys across 1,965
  locations. Isolated emission saved about 83 KB in both load orders and the eight body edits saved 77-91 KB, with
  variable timing. Both Roslyn builds emitted identical files, incremental and collection checks passed, and each
  runtime passed 47 targeted tests (`6ed42ab4`).
- Use the interceptor lookup directly when the emitter needs both group count and iteration. The 1,210-group replay
  saved about 9.7 KB in both load orders. Whole-project timing and allocation varied. Both Roslyn builds emitted the
  same 582 files, edit tracking and closed-project collection matched, and 54 targeted tests passed per runtime
  (`909cdf51`).
- Build the three interceptor name sets directly with the model array's value enumerator. The isolated allocation
  saving was about 0.3 KB, but the entry and shared helper together saved 284 Arm64 FullOpts bytes and 326 x64
  cross-JIT bytes. All 582 files matched in both Roslyn builds, incremental and collection checks passed, and each
  runtime passed 47 targeted tests (`5b2ed919`).
- Filter interceptor prefetch candidates by option, overload, and element formatter before comparing the rendered list
  type. Isolated emission saved about 11.8 KB with prefetch enabled and 16.6 KB with it disabled, with similar times
  in both load orders. Both 577-file output sets matched their baselines. The enabled replay kept all 39 prefetch
  calls and the disabled replay emitted none. Model reuse, collection probes and 68 tests on each of .NET 8 and 10
  passed (`b13fd0d2`).
- Use compile-time preambles for the fixed interceptor attribute. The 307-323-character preamble no longer needs a
  writer, saving about 9.2 KB per replay in both load orders. Both Roslyn builds emitted the same 582 files,
  incremental and collection checks passed, and 38 targeted tests passed on each runtime (`fc0b5481`).

## Writing the body

- Append interceptor signature slices, the method ordinal, and the location attribute with its optional comment
  directly to the code writer, rather than allocating substrings and the assembled line.
  - Signature slices and ordinal: in the 577-file unit test replay, cold and method-body edit runs each allocated about
    1.1 MB less with byte-identical output. The main emitter went from 870 to 813 IL bytes and added a 99-byte append
    helper.
  - The location attribute and comment: another 0.50 MB on cold and edited replays, the same output, the main emitter
    down to 764 IL bytes, and a separate 99-byte append helper.
- Estimate the interceptor body buffer from both call sites and grouped methods, capped at the previous call-only
  reservation. The replay had 1,930 locations and 1,192 methods. The estimate reduced initial capacity from 741,120 to
  628,480 characters and saved about 223 KB in each cold or edited replay, with identical output and no stable timing
  difference. (The per-forwarder reserve below refined it.)
- Write the modern interceptor body at its final namespace indentation and concatenate it with the small prefix,
  keeping the line-by-line path for legacy and multiline locations. This removed about 1.06 MB per isolated emission
  and per method-body edit. Isolated timing varied by load order. Both Roslyn 4.8 and 4.12 output sets matched all 577
  files. Tracked model reuse and collection checks passed, and .NET 8 and 10 each passed 78 targeted tests
  (`e958537d`).
- Finish a fully compacted modern interceptor body in its existing builder (`205af3eb`) instead of materializing it
  and concatenating a second full string. Require preindented output, both namespace aliases, the byte-span alias, and
  location comments without generated/Core/byte-span rewrite targets; otherwise keep the original alias and file path,
  and keep the original statement-by-statement alias fallback for unusual comments. Prepend the small existing file
  prefix only after body emission, preserving the body's write positions and exact alias order. On 122 real unit-test
  trees, four local default-JIT rounds saved about 1.17 MB per cold generation and method-body edit; cached runs were
  unchanged. Isolated emission fell from about 4.57 to 3.40 MB allocated, with timing on par. Whole-project edit
  timings varied: do not claim a build-time gain. Both Roslyn 4.8/4.12 kept all 583 files byte-identical across
  fresh/cached/two edit states, reused 578 models, and released closed compilations, trees, symbols, generated texts
  and the replaced tree. All 24 location/count edge cases matched baseline, including qualified-name comments,
  multiline comments, empty and tiny call sets. The two targeted suites passed 38 tests each. Emit grew from 1,007 to
  1,131 IL bytes, 3,304 to 3,876 Arm64 FullOpts bytes and 2,330 to 2,646 offline x64 bytes; Finish(prefix) adds 21 IL
  bytes. A prefix-first variant grew Emit to 4,020 Arm64 bytes and was slightly slower in isolated replay;
  prepend-after-emission retained the allocation benefit with on-par throughput.
- Do not attribute the interceptor allocation result to line splitting: the preliminary `CodeWriter.Lines` slice trial
  saved only about 15.5 KB cold and showed no whole-project time gain. Preserve its patch (the earlier range-scanning
  trial of `CodeWriter.Lines`: [emit-text.md](emit-text.md)).
- Use the existing location and forwarder counts to estimate indentation in the large interceptor namespace; this
  needs no extra scan and saved about 265 KB in both isolated load orders. Eight method-body edits saved 256-277 KB
  with variable timing. All 577 generated files matched, model reuse and collection checks passed, and 78 targeted
  tests passed on each of .NET 8 and 10 (`ce66cee9`).
- Count the actual modern location line lengths in the same pass that checks display paths. The isolated emitter saved
  about 67.6 KB in both load orders and the eight body edits saved 62-81 KB. Timings varied. Both Roslyn builds
  emitted the same 582 files, tracked call sites and closed-project collection matched, and 38 targeted tests passed
  per runtime (`908bda02`).
- Reserve 312 characters per forwarder in the preindented interceptor body and retain 320 for the legacy path. The
  unit-test body used 609,981 of the original 622,556 reserved characters. Both isolated load orders saved about 19.4
  KB, while the benchmark project hit the existing location cap and allocated the same. The initial cold slowdown did
  not repeat with a byte-identical control. Outputs, incremental tracking, collection and both targeted suites matched
  (`0646f4ab`).

## Aliases

- Append interceptor aliases directly for the two signature slices and the call, removing the intermediate replaced
  strings while leaving location attributes alone. The isolated emitter saved about 519 KB in both load orders with
  similar median time. The eight method-body edit samples allocated 514-536 KB less. All 577 files matched, tracked
  model reuse and collection checks passed, and 68 targeted tests passed on each of .NET 8 and 10 (`82b7bbb3`).
  - Do not stream namespace alias replacements into the whole numbered method or only its call expression: into the
    whole method it avoided about 0.52 MB of cold allocations but slowed the model-edit replay in both run orders;
    applied to only the call expression it still slowed that case, saving about 0.45 MB. Both variants emitted the
    same 577 files byte for byte. Measure against `82b7bbb3` before retrying.
- Omit generated namespace qualifiers while appending interceptor signature and call slices, and keep the final
  rewrite for qualifiers outside those slices. Isolated emission allocated about 1.28 MB less and median times fell
  5-10% in both load orders. Eight method-body edits saved 1.28-1.29 MB with variable whole-project timing. Both
  Roslyn 4.8 and 4.12 matched all 577 files, incremental reuse and collection probes passed, and 78 targeted tests
  passed on each of .NET 8 and 10 (`31b7ef5c`).
- Append the byte-span alias while writing modern interceptor slices when its existing whole-name threshold is met;
  this avoided the last full alias rewrite in the real interceptor file. Isolated emission saved about 2.52 MB. Four
  clean method-body edit replays saved 2.52-2.53 MB and ran 6-11% faster, with variable A/A and other edit timings.
  Both Roslyn 4.8 and 4.12 matched all 577 files, model reuse and collection checks passed, and 78 targeted tests
  passed on each of .NET 8 and 10 (`2e9c7b4b`).
- Keep the existing whole-name byte-span matcher until a larger or more alias-dense workload shows a benefit. Bounding
  the search to each alias slice kept output and allocations effectively identical; isolated emitter time improved in
  one load order and regressed in the other, and four cold and edit replays had no repeatable change.
- Keep the short namespace and byte-alias profitability scans separate (keep the merged loop as a probe). Combining
  them saved only 30-40 B per emitter replay, with no repeatable time or whole-project allocation gain. `Emit` grew by
  6 IL bytes and 16 x64 cross-JIT bytes while shrinking by 120 Arm64 FullOpts bytes. The 582 outputs, incremental
  tracking and collection matched on replay. Both targeted suites passed.
- Skip empty alias ranges before searching or appending. The Arm64 listing of `Append` and its separately emitted
  helper shrank from 2,268 to 1,332 bytes after the inline decisions changed. Isolated replays of both projects showed
  no reliable time or allocation gain, and other local builds disturbed the cold samples. Both Roslyn builds emitted
  identical files, tracking and collection matched, and 38 targeted tests passed per runtime in an isolated worktree
  (`bffe98b6`).
- Keep both empty-range guards. Removing the inner guard cut six IL bytes and 16 Arm64 FullOpts bytes from the alias
  helper path, but let the generated-range body inline into the x64 `AppendRange` listing, growing it from 219 to 466
  bytes. Neither project's emitter replay gained reliably against the identical-binary control. Both Roslyn builds
  still emitted the same 582 files, tracked results and collection matched, and 38 targeted tests passed on each
  runtime.
- Keep the current byte-alias match loop (keep the short-range patch as a probe). Checking the remaining slice before
  searching saved eight IL bytes but grew Arm64 `AppendRange` from 616 to 628 bytes; the x64 cross listing shrank from
  219 to 218 bytes. Neither project replay showed a repeatable gain against A/A, and allocation matched. Both Roslyn
  outputs, tracking, collection and targeted suites matched.
