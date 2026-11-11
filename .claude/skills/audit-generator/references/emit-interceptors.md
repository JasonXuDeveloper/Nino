# Emitting the interceptor file

Part of the `audit-generator` skill: how the interceptor emitter builds its one large file (on tests/Nino.UnitTests,
about 1,200 distinct calls at about 2,000 locations) - rendering calls once, appending its lines in pieces, one
emission path, and what the removed second path measured. Read it before changing the interceptor
emitter, its aliases or location attributes, or before retrying an allocation trick there. The bounded scratch
builder it shares is in [emit-text.md](emit-text.md); measurement terms are in [measuring.md](measuring.md); every
entry kept its output byte-identical.

## Contents

- [Rendering calls](#rendering-calls)
- [Writing the body](#writing-the-body)
- [Removed: the second path](#removed-the-second-path)

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
- Iterate the interceptor lookup directly, without a list of its groups. The 1,210-group replay
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
- Do not split the interceptor text into lines to save allocation: the preliminary `CodeWriter.Lines` slice trial
  saved only about 15.5 KB cold and showed no whole-project time gain. Preserve its patch (the earlier range-scanning
  trial of `CodeWriter.Lines`: [emit-text.md](emit-text.md)).
## Removed: the second path

- Emit the interceptor file through the general path only (write at no indentation, `GeneratedCode.WithAliases`,
  `GeneratedCode.File`), on every build: a second path that only saves allocation is complexity the owner's rule
  does not pay for (`measuring.md`). The removed path wrote the Roslyn 4.12 body at its final indentation, streamed
  `__C`, `__SCG`, the generated-namespace omission and `__NinoBytes` into the slices (`GeneratedCode.NamespaceAliases`,
  with its own counting thresholds, matcher, empty-range guards and profitability scans), finished the file in its
  builder, and sized it from per-location and per-forwarder estimates (`e958537d`, `205af3eb`, `82b7bbb3`,
  `31b7ef5c`, `2e9c7b4b`, `908bda02`, `0646f4ab`, `ce66cee9`). On the 596-file unit test compilation replayed with
  the Roslyn 4.12 build (three alternated rounds of seven runs, after the first), dropping the path cost 5.9 MB per
  cold run (33.9 to 39.9 MB) and 6.0 MB per edit of the call file (29.5 to 35.5 MB), and merging the streamed aliases
  into `WithAliases` another 5.4 MB and 2.7 MB; generator time stayed within the base's own spread (edit medians
  260-365 ms base, 278-321 ms head; pooled over seven rounds 332 ms base, 287 ms head). The interceptor file stayed
  byte-identical on both Roslyn builds. Choosing all aliases in one pass then took the totals to 36.1 MB cold and
  32.3 MB per edit, against 33.9 and 29.5 MB for the removed path (`emit-text.md`, one pass). Before reviving a streamed or preindented path, show a time gain on the
  replay against an identical-binary control, not an allocation saving.
