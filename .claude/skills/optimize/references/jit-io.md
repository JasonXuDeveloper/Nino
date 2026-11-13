# The JIT: Reader, Writer, buffer writers, streams and sequences

Part of the `optimize` skill, split from [jit.md](jit.md), whose general rules (the inlining budget, the cursor in
registers) apply here. These are the measured experiments on Nino's own I/O primitives: the reader's and writer's
cursor helpers, `NinoArrayBufferWriter` and its short copies, stream reads, and `ReadOnlySequence` input and output.
"Arm64 only; x64 unmeasured" marks a bullet timed only on the local Arm64 host (Apple M4): its x64 sizes come from
cross-JIT, emulated, guest or offline listings and are code-size evidence, not throughput.

Rules in short:

- Advance the reader once per region and share its constructors; keep `Writer.PatchLength` relative to the buffer
  start; keep `ReadSlice`'s cursor update; build raw spans from `ReadBlock` on byref targets only.
- In `NinoArrayBufferWriter` copy the prefix in `Grow` with `CopyToNew`, return the rented array from it, keep the
  shared capacity check, the array-backed `WrittenSpan` and the current empty `ToArray`.
- Short copies: one move for one byte, two overlapping words for two or three, the shared head move for exactly 16.
- Streams: delay the prefix copy, keep the growth read one call, measure immediate and suspended completions apart.
- Sequences: copy what spans segments once, out of line, to the stack up to 1,024 bytes; keep the explicit `TryGet`
  loops, per-segment reservations, `NinoUnsafe.Copy` per segment (the span move where one overlaps), and local
  validation; read two-segment byte arrays and split
  headers on their own cold paths.

## Reader and Writer

- Advance the parent cursor once for a plain `Reader.ReadRegion` on .NET 8+, share the child constructor across the
  normal and deferred-section paths, and keep the consumed header visible if section validation throws. Why: a split
  returning a reader through a no-inline slow path grew the Arm64 Tier 1 keyed caller from 1,616 to 1,764 bytes and
  slowed the ordered 700-item case from about 4 to 24 microseconds. Evidence: sharing the constructor took the caller
  to 1,496 Arm64 bytes and 1,435 x64 cross-JIT bytes (from 1,525), though `ReadRegion` grew from 74 to 143 IL bytes;
  it improved both ordered array sizes straddling 64 KiB in all four local rounds at zero steady allocation; the
  ordered 16-item timing overlapped A/A; wire and generated files matched. Arm64 only; x64 unmeasured.
  Keep its `#if` body on the fields too: one body for both targets through cursor primitives (a sub-reader and a move
  given a ref and the bytes left, each target using the one it holds) changed what the keyed callers inline. `ReadRegion`
  fell from 143 to 115 IL bytes but brought `SubAt` (28), `MoveTo` (20) and the `Avail` / `Cur` getters, and
  `ProfileV2Formatter.ReadBody` (Workloads) left `ListFormatter<string>.Deserialize` as a call instead of inlining it
  (2,082 to 1,961 listing lines, `DOTNET_TieredCompilation=0` `asm.py` dumps, AVX-512 and `DOTNET_EnableAVX512=0` alike,
  every other method identical; Emerald Rapids, round of the core dedupe).
- Keep `Writer.PatchLength` relative to the writer's buffer start, and inspect the Arm64 pair load before retrying
  this arithmetic rewrite. Why: measuring the distance from the length slot or from the payload start split the Arm64
  pair load of the start and cursor into two separate loads. Evidence: both variants kept the keyed caller at 804
  Arm64 Tier 1 bytes while x64 cross-JIT fell from 541 to 529 and 533 bytes, respectively; the helper grew from 18 to
  28 and 36 IL bytes. Both slowed the paired 16- and 700-item ASCII writes in four local rounds (with some 700-item A/A
  noise), and the 1,100-item payload-relative variant was slower than both controls in each round. The same generated
  consumer matched wire bytes at offsets 0, 1 and 7 and stayed at zero steady allocation. Arm64 only; x64 unmeasured.
- Keep the existing custom-formatter `ReadSlice` cursor update. Why: folding header and payload advancement into one
  update helped only once the candidate reached Tier 1. Evidence: the method grew from 43 to 122 IL bytes while
  standalone x64 cross-JIT shrank from 120 to 114 bytes; a 64-byte slice through a byref parent reduced its Arm64 Tier 1
  caller from 184 to 156 bytes and improved local timing, but empty-slice runs often stayed in Tier 0 for the
  candidate, with repeated regressions even after longer warmup. Four slice checks matched and measured reads stayed
  at zero allocation. Arm64 only; x64 unmeasured.
- Build the raw span from `ReadBlock` on the byref reader targets, and keep the portable slice. Why: the span and block
  reads then share one bounds check and cursor advance in the source; the byref form regressed the portable target.
  Evidence: the standalone IL fell from 29 to 14 bytes and the x64 ReadyToRun body from 57 to 49 bytes, with the same
  Arm64 Tier 1 caller size; switching the portable slice to the byref form regressed the local 1-byte and 32 KiB
  probes. Byte output, failure cursor and steady-state allocations matched.

## NinoArrayBufferWriter and short copies

- Copy the prefix in `NinoArrayBufferWriter.Grow` with `CopyToNew` (d12d9c16), as every payload copy
  ([vectors-copies.md](vectors-copies.md#rules)): the span move is Memmove, glibc's rep movsb on AMD runners. An
  earlier M4-only trial had kept the span move: `CopyToNew` regressed the 1 KiB growth case in
  four local rounds; applying it only from 64 KiB preserved the smaller path but did not produce a repeatable gain
  across 64 KiB and adjacent inputs. Time growth on Arm64 runners before keeping it there. Evidence: the threshold variant grew from 148 to 195 IL bytes and from 233 to 325
  x64 ReadyToRun bytes. Prefix bytes and allocations matched.
- Return the rented array from `Grow` to the reserve callers. Why: it removes the direct reserve wrapper and the
  writer's buffer reload while keeping the growth boundary out of line. Evidence: the static writer wrapper plus growth
  body went from 512 to 496 Arm64 Tier 1 bytes and from 387 to 373 bytes in the x64 FullOpts cross listing.
  Alternating direct and writer probes from 16 bytes through 33 KiB showed no repeatable timing gain against the
  identical-binary control. Wire bytes, pool ownership checks and allocations matched (`a76fff7c`). Arm64 only; x64
  unmeasured.
- Keep the shared capacity check of `GetSpan` and `GetMemory` in production, and check the span return registers
  before sharing the buffer and index between them; keep the split shape as a probe. Why: an inline helper removed
  both field reloads but spilled the span length in Arm64 Tier 1; a separate out-of-line growth branch removed the
  spill but bought no time. Evidence: the split reduced the `GetSpan` entry from 172 to 124 Arm64 Tier 1 bytes and
  from 168 to 124 x64 FullOpts cross-JIT bytes, excluding the new helper; four alternating runs with a byte-identical
  control showed no repeatable gain. Both candidates matched the written bytes and allocated 0 B without growth.
  Arm64 only; x64 unmeasured.
- Keep the array-backed constructor for `NinoArrayBufferWriter.WrittenSpan`. Why: creating the span from an array-data
  reference removed the bounds comparison but left an implicit null-check load on Arm64, and consistently slowed the
  one-item serialize-and-consume loop; using the full array span's reference instead also regressed the direct
  64-item view probe in all four local default-JIT rounds against both baselines. Cache-sized inputs varied. Evidence:
  getter IL was 19/23/28 bytes for the baseline and the two candidates; the consumer was 132/64/80 Arm64 FullOpts
  bytes and 66/35/51 x64 cross-JIT bytes. Wire samples matched at both offsets and steady allocations were zero.
  Arm64 only; x64 unmeasured.
- Keep the existing empty handling in `NinoArrayBufferWriter.ToArray`. Why: an early return accelerated the empty
  result but slowed the 65-byte export in all four local rounds; checking for zero inside the short copy branch removed
  that pattern but gave no stable empty-case gain. Evidence: the early return grew from 63 to 72 IL bytes and from 548
  to 556 Arm64 Tier 1 bytes; the short-branch form used 66 IL bytes and the same Arm64 and x64 sizes as the baseline.
  Exported bytes and allocations matched for zero through 32 KiB.
- Use one byte move for a one-byte copy and two overlapping short-word moves for two or three bytes, and keep that
  shared overlapping path rather than exact-width moves. Evidence: `CopyUpTo15` shrank from 172 to 171 IL bytes, the
  dynamic Arm64 caller grew from 380 to 384 bytes and the x64 cross listing for `ToArray` from 508 to 512; four public
  export rounds improved by 6–9% at 1–3 bytes with unchanged allocation, direct-copy samples varied, all 80 export
  patterns and the 0–64 byte offset checks matched, and both targeted suites and netstandard builds passed
  (`15a5281d`). One word for exactly two bytes and a word plus a byte for exactly three grew `CopyUpTo15` by one IL
  byte and the dynamic Arm64 and x64 callers by 12 bytes each, and did not improve either size against the
  identical-binary control in four public export rounds (the 0–64 byte and patterned export probes matched, both
  targeted suites and netstandard builds passed); the exact-width patch is kept as a probe. Arm64 only; x64
  unmeasured.
- Copy exactly 16 bytes with the shared head move and run the tail only above that size, sharing the store rather than
  returning early. Why: an early return duplicated the head store and grew the dynamic Arm64 caller by 32 bytes.
  Evidence: sharing the store grew it by 12 bytes and `Copy16To32` by two IL bytes, with unchanged x64 cross listings;
  the 16-byte public export took 4–7% less time in four rounds at the same 40 B allocation; neighboring sizes and
  direct timings varied. Alignment, export patterns, targeted tests and netstandard builds passed (`57ca2ebb`). Arm64
  only; x64 unmeasured.

## Streams

- Delay copying the prefix of a full synchronous stream buffer until the next read returns bytes, and keep the
  ownership and EOF paths out of the common read loop. Why: a non-seekable stream ending at a buffer boundary needs the
  EOF read but no prefix copy. Evidence: the 64 KiB wire case improved in each of four local rounds, with the adjacent
  64 KiB plus one byte case within noise and zero steady-state allocations on both sides; the 4 KiB timing was noisy.
- Keep the growth read as one call into the next rental. Why: a separate minimum-sized probe rental avoided the 128 KiB
  rental at 64 KiB EOF but increased the non-seekable stream's read calls from six to ten at that size and from seven
  to twelve at 96 KiB. Evidence: it also grew the full-buffer helper from 88 to 129 IL bytes and had no repeatable
  steady-state gain at 64 KiB.
- Keep the existing stream path for exact hidden-buffer `MemoryStream` instances. Why: a single bounded read did not
  improve throughput consistently. Evidence: placed in the synchronous rental path it left the exposed-buffer branch
  intact but grew `ReadToEnd` from 91 to 174 IL bytes, plus a 44-byte helper; the 64 KiB hidden-buffer case had more
  slower than faster rounds with either placement, with zero steady-state allocations.
- Measure immediate and suspended async completions separately; both async variants below stay experiments. Why: each
  helped one kind and cost the other. Evidence: retaining both rentals across the await raised `MoveNext` from 376 to
  510 IL bytes and added 8 B per operation in the suspended 4 KiB probe; testing for immediate EOF in a non-async
  helper kept the suspended 4 KiB allocation flat and limited `MoveNext` to 387 IL bytes, and improved immediate 64 KiB
  reads in four local rounds but regressed immediate 4 KiB reads.

## ReadOnlySequence input and output

Why multi-segment input is copied rather than read in place is in [jit.md](jit.md), "Registers". The single-segment
`ReadOnlySequence<T>` write through the memory entry is in [jit-arrays.md](jit-arrays.md).

- Compute the segmented length once and pass it to the pooled copy, keep the bounds check there (before converting the
  length to an int), and share the copy out of line across direct and regular callers. Why: its bounded spans must
  consume exactly the reported length, so a custom segment's inconsistent `RunningIndex` cannot expose unfilled
  scratch bytes.
- Copy to the stack up to 1,024 bytes, measure both sides of the cutoff, and keep the fixed buffer out of inline
  callers. Evidence: raising the stack cutoff from 512 to 1,024 bytes avoided the pool round-trip for 516-, 804- and
  1,024-byte messages in the local probe.
- Keep the shared copy helper; the byref destination cursor stays a read-path experiment. Why: a byref destination
  cursor and an exact-length destination span folded the second length check in Arm64 FullOpts, but only reads gained.
  Evidence: the first warmup did not reach Tier 1; with longer warmup the 128 by 512-byte read improved in four local
  rounds, while the write had mixed results; allocations stayed at zero and wire bytes matched. The helper grew from
  76 to 110 IL bytes, 728 to 736 Arm64 Tier 1 bytes, and 207 to 233 x64 ReadyToRun bytes. Isolating scratch reads and
  switching to `CopyToNew` or `CopyBlockUnaligned` did not reproduce the read throughput improvement: both scratch
  helpers used 114 IL bytes and 238 x64 ReadyToRun bytes, versus 76 IL and 207 x64 bytes for the shared helper, and
  the Arm64 Tier 1 sizes were 736 and 724 bytes versus 732. Wire bytes and steady-state allocations matched; the
  read-only alternatives remain in the experiment worktree.
- Keep the explicit `TryGet` loop of the shared copy, stop at the last segment, and test the segment position before
  `TryGet` in the loop condition. Why: replacing the loop with `foreach` made two 32-byte segments slower on both reads
  and writes in two local rounds, even though Arm64 machine code shrank from 708 to 684 bytes (the JIT already shared
  the `CopyTo` and `Slice` bounds check); dropping the position check slowed the two nonempty 32-byte segments.
  Evidence: moving the position check into the condition kept nonempty timings within A/A and improved the
  interleaved-empty short writes; Arm64 Tier 1 fell from 732 to 700 bytes and offline x64 from 649 to 603, with 2
  more IL bytes. Six wire and round-trip checks per candidate passed at zero steady allocation. Arm64 only; x64
  unmeasured.
- Skip copying and slicing for an empty span, after obtaining the span from the segment, and keep the last-segment
  test and the final remaining-length validation. Evidence: two 32-byte segments interleaved with empty segments wrote
  in about 28 ns instead of 31 ns in the local default-JIT probe; ordinary inputs and the shared read path overlapped
  A/A. The helper grew from 76 to 85 IL bytes, 728 to 732 Arm64 Tier 1 bytes, and 641 to 649 offline x64 bytes. All
  six wire cases matched at an unaligned output offset, at zero allocation. Arm64 only; x64 unmeasured.
- Do not probe array identity and offsets to coalesce two adjacent byte segments on every write; the patch is kept for
  experiments. Why: two 32-byte adjacent segments went from about 24 ns to 27–35 ns in four local rounds, disjoint
  short inputs also slowed, and inputs plus output around 64 KiB varied across A/B and A/A. Evidence: `WriteSegments`
  grew from 118 to 138 IL bytes, 352 to 432 Arm64 FullOpts bytes and 312 to 369 offline x64 bytes; the probe helper
  added 169 IL, 1,184 Arm64 and 1,052 offline x64 bytes. All six wire cases matched at an unaligned output offset with
  no steady allocation. Arm64 only; x64 unmeasured.
- Reserve the header and payload together when writing a multi-segment byte sequence with the built-in formatter,
  reuse `NinoSequences.CopySegments` to check the declared length while copying into the reservation, and match the
  exact formatter type so the JIT removes the generic segment loop. Evidence: the Arm64 entry shrank from 3,020 to 352
  bytes and calls the existing 708-byte copy helper.
- In `CopySegments` use `TryGet` as the sole loop guard (it already handles the end position). Since 5d9090e6 each
  segment is copied with `NinoUnsafe.Copy` after an explicit length check (ArgumentException for a segment longer than
  the rest), the span move only for a segment overlapping the destination; the M4-only trial below had kept two span
  operations (Arm64 folded the slice check after `CopyTo`), so time it on Arm64 runners (`ExternalBufferTest.*SegmentedInts`). Why: a separate two-segment branch slowed the
  512-byte write by 22–28% on M4. Evidence: the simpler guard was broadly on par across four default-JIT pairs,
  including two, four and sixteen segments and the cache-boundary controls; all nine wire comparisons matched at 0 B.
  `CopySegments` moved from 87 to 78 IL bytes, 680 to 712 Arm64 FullOpts bytes and 202 to 198 offline x64 bytes. The
  .NET 8/10 suites and both netstandard builds passed (`fc95d7ea`). Arm64 only; x64 unmeasured.
- Keep per-segment reservations in `WriteSegments`, the general fixed-width (fixed-stride) sequence writer. Why:
  reserving all segments at once and checking the declared element count removed repeated capacity checks but slowed
  short writes. Evidence: the first trial slowed both the two- and 32-segment small-int writes in all four Arm64
  rounds, with longer writes mixed, identical bytes and zero steady-state allocations; `WriteSegments` grew from 118 to
  246 IL bytes and from 1,524 to 1,592 Arm64 Tier 1 bytes. Another pre-reservation trial reserved only when the
  destination already had room (so a potentially aliased pooled array is not released before the segments are read),
  and its bounded loop checked the remaining count and rejected inconsistent lengths before publishing output. It
  saved 7–17% at 8,192 ints across sixteen segments and offset seven, but slowed the 128-int case by 17–38% in four M4
  pairs. A 1,024-element gate and an outlined copy restored the short case and gave mixed long results; keeping the
  gated loop in the main method recovered a 7–14% gain on sixteen segments, while the short case slowed 11–21% again
  despite skipping that loop; padded and threshold-sized timings varied. `WriteSegments` used 118 IL bytes before
  these trials, 249 for the initial version, 167 plus a 97-byte helper for the outlined version, and 260 for the gated
  in-method version, whose Tier 1 body grew from 1,540 to 2,304 bytes on Arm64 and from 1,508 to 2,164 in the x64
  guest. All wire comparisons and zero steady allocation matched; the outlined candidate passed four targeted
  configurations, including the inconsistent-length cases, and the real generated files matched. None of the source
  candidates was retained. Arm64 only; x64 unmeasured.
- Read a complete built-in byte array directly from two segments after validating the header and actual length, and
  check segment adjacency before entering the direct path. Keep the copy through scratch when reusing an array that
  aliases input, or when replacing an array with an element or array recycler registered: recyclers can mutate the
  source before it is copied. Why: probing both spans on three-segment input regressed the fallback. Evidence: passing
  the checked start position cut 12 IL bytes from `TryReadByteArray` but grew its offline x64 from 1,688 to 1,711
  bytes without a repeatable timing gain; checking adjacency inside that helper grew it to 212 IL bytes, 1,944 Arm64
  Tier 1 bytes and 1,757 offline x64 bytes, and the extra call slowed three-segment input in all four paired rounds.
  Both candidate patches passed six wire and round-trip comparisons. Arm64 only; x64 unmeasured.
- Keep split headers in a separate cold method that joins only the four header bytes, and assemble the split length
  directly from the two spans. Why: joining them inside the ordinary two-segment reader regressed complete-header
  inputs in the local A/B. Evidence: the 2+2 split of a 64-byte payload read in 21.31–23.71 ns across four warmed
  rounds, from 23.26–27.28 ns; complete headers and near-L1 payloads overlapped A/A. The helper grew from 193 to 277 IL
  bytes and 1,412 to 1,476 Arm64 Tier 1 bytes, but offline x64 fell from 1,345 to 1,268. All seven unaligned wire and
  round-trip probes passed at zero steady allocation. Arm64 only; x64 unmeasured.
- Keep the bounded loads and the switch in the split header reader; the reverse-loop patch is retained. Why: smaller
  forms were slower. Evidence: byref halfword loads reduced Arm64 Tier 1 from 1,476 to 1,400 bytes and offline x64 from
  1,268 to 1,182, but the 2+2 case was slower against its paired baseline in three of four rounds. Switching on the
  actual left span length kept IL at 277 bytes and reduced native code to 1,464 Arm64 and 1,240 x64 bytes, but timings
  varied and the 1+3 case was slower; both variants passed five wire and round-trip probes, with zero steady
  allocation. Two reverse byte loops assembled the split word without a switch and shrank IL from 277 to 223 bytes,
  Arm64 Tier 1 from 1,476 to 1,392 bytes and offline x64 from 1,268 to 1,170, yet the 3+1 split slowed in four paired
  rounds (24.0–25.0 ns for the candidate); all five wire probes matched at zero allocation. Arm64 only; x64
  unmeasured.
- Keep the segment and count validation local and share only payload copying and replacement. Why: sharing both
  validations through inlined helpers still slowed split-header reuse across both loading orders.
- Keep the regular and direct sequence readers' stack and pool scopes around their own span calls, and share only the
  segment copy, rent and return primitives. Why: reusing the stream reader adapters inside a shared sequence scope made
  the 32-segment probe slower in both entry points across four rounds.
- Keep `Span.CopyTo` for direct two-segment payloads even when disjointness is known. Why: `NinoUnsafe.Copy` made all
  measured sizes slower across both load orders. Evidence: it grew the Arm64 two-segment method from 1,796 to 2,196
  bytes.
