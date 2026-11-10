# The JIT: inlining budget, registers, folding

Part of the `optimize` skill. The rules about what RyuJIT inlines, keeps in registers and folds, each with the
evidence that set it. Read it before changing a read or write path of Nino.Core, Nino.Immutable or the emitted code,
and when `asm.py compare` lists methods the change did not touch.

## Inlining budget

- Advance the parent cursor once for a plain `Reader.ReadRegion` on .NET 8+.
  Share the child constructor across the normal and deferred-section paths.
  Keep the consumed header visible if section validation throws. A split
  returning a reader through a no-inline slow path grew the Arm64 Tier 1
  keyed caller from 1,616 to 1,764 bytes and slowed the ordered 700-item
  case from about 4 to 24 microseconds. Sharing the constructor took
  the caller to 1,496 Arm64 bytes and 1,435 x64 cross-JIT bytes (from
  1,525), though `ReadRegion` grew from 74 to 143 IL bytes. The shared
  variant improved both ordered array sizes straddling 64 KiB in all four
  local rounds at zero steady allocation. The ordered 16-item timing
  overlapped A/A. Wire and generated files matched.
- Keep `Writer.PatchLength` relative to the writer's buffer start. Measuring the
  distance from the length slot or payload start instead split the Arm64 pair
  load of the start and cursor into two separate loads. Both variants kept the
  keyed caller at 804 Arm64 Tier 1 bytes, while x64 cross-JIT fell from 541 to
  529 and 533 bytes, respectively. The helper grew from 18 to 28 and 36 IL
  bytes. Both variants slowed the paired 16- and 700-item ASCII writes in four
  local rounds, with some 700-item A/A noise. The 1,100-item payload-relative
  variant was also slower than both controls in each round. The same generated
  consumer matched wire bytes at offsets 0, 1 and 7 and stayed at zero steady
  allocation. Inspect the pair load before retrying this arithmetic rewrite.
- Reuse the constructor-validated `ArraySegment<T>` range when writing value types on .NET 8+.
  Reference types still take `AsSpan` to preserve its covariance check. A one-byte slice at
  offset one beat the paired baseline in all eight local rounds and the identical-binary
  control in six. Long copies on either side of a 64 KiB working set varied. The formatter IL
  rose from 33 to 90 bytes; the Arm64 Tier 1 caller shrank from 964 to 932 bytes and the
  concrete x64 cross-JIT write entry from 2,279 to 2,180. Six wire checks and all 577 generated
  files matched, the covariant array still threw, and steady-state write allocation was zero.
  Keep the existing byte copy dispatch for one-element segments. A separate reservation
  grew the formatter from 90 to 146 IL bytes and the Arm64 Tier 1 caller from 948 to
  1,088 bytes. Specializing after the shared reservation raised `WriteSpan` from 63 to
  115 IL bytes and kept the caller near baseline at 956 bytes. The x64 cross-JIT entry
  was 2,180 bytes at baseline, 2,177 with the separate path and 2,122 with the shared
  path. Neither consistently beat the identical-binary control for one byte. Seven wire
  checks matched for both versions and steady-state allocation stayed at zero.
- Keep the existing reservation for canonical span writes. Passing the 64-bit payload
  size from `Writer.Write` to a reservation overload removed the separate 32-bit shift
  from the no-padding copy path, but kept the Arm64 FullOpts callers at 1,896 bytes.
  The x64 cross-JIT callers shrank by 14 bytes and the changed Writer methods grew
  from 124 to 134 IL bytes. Four local default-JIT rounds with duplicate baselines
  showed no repeatable gain for short or cache-sized inputs, at 0 B allocated.
- Keep the existing custom-formatter `ReadSlice` cursor update. Folding header and payload
  advancement into one update grew the method from 43 to 122 IL bytes while standalone x64
  cross-JIT shrank from 120 to 114 bytes. A 64-byte slice through a byref parent reduced its
  Arm64 Tier 1 caller from 184 to 156 bytes and improved local timing. Empty-slice runs often
  stayed in Tier 0 for the candidate, with repeated regressions even after longer warmup.
  Four slice checks matched and measured reads stayed at zero allocation.
- Use the length of the public `BitArray` byte view when reserving its wire payload.
  The view is already sized for the bits, so slicing it again adds an unnecessary check.
  Keep the final-byte mask and verify dirty padding on unaligned output. The formatter
  went from 86 to 77 IL bytes and from 409 to 369 x64 ReadyToRun bytes. Arm64 timing
  varied around 4 KiB after changing run order, with identical bytes and zero allocations.
  An explicit nonempty guard let x64 fold the final-byte index check without using a byref.
  The standalone mask was 38 x64 bytes with the guard, 44 at baseline, and 62 with a guarded byref.
  Both guarded forms had the same 712-byte Arm64 Tier 1 caller versus 716 at baseline. Neither
  consistently improved the local timings. Keep the existing mask and its bounds check.
  Handle empty and one-byte writes together. Store the single byte with its mask applied, and let
  the existing span copy handle larger payloads. A one-byte-only gate moved the empty timings in
  the wrong direction in the initial probe. The combined gate beat the identical-binary control
  on empty and seven-bit values in four local rounds at 0 B per write. The 62 and 66 KiB working
  sets showed no repeatable timing change. Formatter IL grew from 77 to 114 bytes and the Arm64
  Tier 1 caller from 868 to 896 bytes. The x64 cross-JIT caller stayed at 224 bytes, while the
  standalone Tier 0 formatter grew from 274 to 372 bytes. Generated files and wire bytes matched.
  Skip the span copy for empty and one-byte reads. Keep the multi-byte test first and share the
  existing cleanup, including the length setter that invalidates enumerators on reuse. Masking
  inside a new single-byte branch slowed three fresh seven-bit rounds. A separate reuse branch
  also made the seven-bit samples less consistent. The shared path beat the empty reuse control
  in all four local rounds at 0 B per read, with no repeatable longer-input timing change.
  Read IL grew from 63 to 98 bytes, Arm64 Tier 1 from 728 to 752 bytes and x64 offline from
  211 to 272 bytes. The round trips, dirty tails and generated files matched.
  Use one unaligned load and store for the validated two-byte read too. The
  15-bit case at offset 7 beat both baseline and identical-binary control in
  four local rounds for fresh and reused values. Neighboring 7- and 17-bit
  reads and working sets around 64 KiB were noisier. Read IL grew from 98 to
  132 bytes, Arm64 Tier 1 from 752 to 776 bytes and x64 cross-JIT from 272 to
  320 bytes. Allocation stayed at 0 bytes on reuse and 64 bytes for fresh
  reads. Keep the shared tail cleanup and enumerator invalidation.
  Use the out-of-line large-block kernel for disjoint reuse above 64 bytes.
  Keep the span move for overlapping input and fresh storage. Inlining the
  general copy brought all short-copy cases into the reader and took the
  x64 cross listing to 713 bytes. Calling the existing large helper kept the
  final listing at 440 bytes (baseline 320), with IL at 189 bytes (from 132)
  and Arm64 Tier 1 at 888 bytes (from 772 with a short reuse profile).
  At working sets of about 62 and 66 KiB, four paired local reuse rounds beat
  both controls at zero allocation. Short and fresh results varied. Wire
  checks, including the 64-byte boundary, and all 577 generated files matched.
  Share the short-payload gate when adding a two-byte case. On .NET 10 a
  single unaligned masked store consistently improved 15-bit writes in four
  local A/B rounds. A separate gate was less convincing at 66 KiB, while the
  shared gate showed no repeatable change at 62 or 66 KiB. Formatter IL grew
  from 114 to 157 bytes, the Arm64 Tier 1 caller from 896 to 928 bytes, and the
  x64 cross-JIT caller stayed at 224 bytes. Wire bytes and steady-state
  allocations matched.
  Use the shared copy kernel for payloads above two bytes and keep the tail
  mask. Copying a shorter prefix and writing the masked tail separately, with
  either one or two copy sites, gave no repeatable gain around 64 KiB. With
  the shared kernel, aligned and partial-byte counts at 62 and 66 KiB beat
  both controls in four local rounds at zero allocation. The short cases
  varied. IL moved from 157 to 161 bytes and the Arm64 Tier 1 caller from
  928 to 1,220 bytes. The x64 cross-JIT formatter grew from 471 to 694 bytes,
  with no x64 timing. An Arm64-only guard was retained by the offline compiler
  and grew that listing further, to 758 bytes. Seven wire comparisons and
  577 generated files matched.
  Leave the size split in the copy kernel. Calling its existing large helper
  explicitly while retaining the kernel for small writes kept the Arm64
  Tier 1 caller at 1,220 bytes and took x64 cross-JIT from 694 to 698 bytes.
  Using the span move for payloads up to 64 bytes instead shrank those
  listings to 972 and 507 bytes but slowed the 17-bit and 512-bit writes in
  all four local rounds. Formatter IL was 161 bytes at baseline, 177 with
  the explicit split and 181 with the span move. Both variants matched
  all seven wire comparisons and stayed at zero steady allocation.

- Build the raw span from `ReadBlock` on the byref reader targets. This gives the span and
  block reads one bounds check and cursor advance in the source. The standalone IL fell from
  29 to 14 bytes and the x64 ReadyToRun body from 57 to 49 bytes, with the same Arm64 Tier 1
  caller size. Keep the portable slice. Switching it to the byref form regressed the local
  1-byte and 32 KiB probes. Byte output, failure cursor and steady-state allocations matched.

- Reuse the checked wide byte count in raw array reads. Arm64 Tier 1 no longer shifts the
  element count again for the copy, reducing the sampled fresh and reuse callers from 1,056
  to 1,044 bytes and 1,088 to 1,072. The x64 FullOpts cross listing grew by 4 bytes.
  Four rounds with byte-identical controls showed no repeatable throughput gain. The
  `int[]` probe crossed a 64 KiB input-plus-output budget and matched wire bytes at offsets
  0, 1 and 7. Reuse allocated 0 B and fresh allocation was unchanged (`30cd066b`).

- Use the overlap-safe span move for unpadded raw array writes to caller-provided buffers.
  Keep overlap detection for layouts that fuse padding clearing into the nonoverlapping copy.
  The `int[]` write shrank from 800 to 444 Arm64 Tier 1 bytes and from 640 to 296 in the x64
  FullOpts cross listing. At 8,449 items, four local rounds improved by 19–30%, with the
  identical-binary control within 9%. Short inputs and the 7,935-item replay varied.
  Wire bytes matched at offsets 0, 1 and 7, and writes allocated 0 B (`c311e969`).
  Slice the payload once and use its checked length for the source span. Combining the
  destination slices alone grew Arm64 Tier 1 from 444 to 452 bytes. Sharing the source
  size removed the duplicate multiplication and copy length check, cutting Tier 1 to 384
  bytes and the x64 FullOpts cross listing from 296 to 240. Four local rounds and a
  byte-identical control showed no repeatable timing gain. Wire bytes matched, writes
  allocated 0 B, and targeted tests and both netstandard builds passed (`6590c2a2`).
  Use the same span move and padding clear for one padded element. This skips both the
  overlap check and the general mask loop. A separate value-write branch grew Arm64 Tier 1
  by about 60 bytes. Reusing the existing branch grew it from 564 to 572, and the x64 cross
  listing from 424 to 432. The 24-byte padded struct probe improved by 10–20% for one element
  in four local rounds, with identical-binary controls within 9%. Multi-element timings
  varied. Dirty padding still serialized as zero at offsets 0, 1 and 7 (`23574bb0`).
  Passing the already checked payload size into `NinoMask.Copy` removed its checked
  multiplication but moved that calculation into other callers. The sampled padded array
  writer and mask body together went from 1,260 to 1,252 Arm64 Tier 1 bytes and from 748 to
  724 x64 cross-JIT bytes. Neither caller-provided buffers nor `NinoArrayBufferWriter`
  showed a repeatable timing gain with identical-binary controls. Keep the original mask
  entry. The probe matched wire bytes and dirty-padding output at offsets 0, 1 and 7.

- Keep the span copy in `NinoArrayBufferWriter.Grow`. Switching to `CopyToNew` regressed
  the 1 KiB growth case in four local rounds. Applying it only from 64 KiB preserved the
  smaller path but did not produce a repeatable gain across 64 KiB and adjacent inputs.
  The threshold variant grew from 148 to 195 IL bytes and from 233 to 325 x64 ReadyToRun
  bytes. Prefix bytes and allocations matched.
  Return the rented array from `Grow` to the reserve callers. This removes the direct reserve
  wrapper and the writer's buffer reload while keeping the growth boundary out of line.
  The static writer wrapper plus growth body went from 512 to 496 Arm64 Tier 1 bytes and
  from 387 to 373 bytes in the x64 FullOpts cross listing. Alternating direct and writer probes
  from 16 bytes through 33 KiB showed no repeatable timing gain with the identical-binary
  control. Wire bytes, pool ownership checks, and allocations matched (`a76fff7c`).
  Check the span return registers before sharing the buffer and index with `GetSpan` and
  `GetMemory`. An inline helper removed both field reloads but spilled the span length in
  Arm64 Tier 1. A separate out-of-line growth branch removed the spill and reduced the
  `GetSpan` entry from 172 to 124 Arm64 Tier 1 bytes and from 168 to 124 x64 FullOpts cross-JIT
  bytes, excluding the new helper. Four alternating runs with a byte-identical control
  showed no repeatable gain. Both candidates matched the written bytes and allocated 0 B
  without growth. Keep the split shape as a probe and the shared capacity check in production.
- Keep the array-backed constructor for `NinoArrayBufferWriter.WrittenSpan`.
  Creating the span from an array-data reference removed the bounds comparison
  but left an implicit null-check load on Arm64. Using the full array span's
  reference instead also regressed the direct 64-item view probe in all four
  local default-JIT rounds against both baselines. The first form consistently
  slowed the one-item serialize-and-consume loop; cache-sized inputs varied.
  Getter IL was 19/23/28 bytes for the baseline and two candidates. The consumer
  was 132/64/80 Arm64 FullOpts bytes and 66/35/51 x64 cross-JIT bytes.
  Wire samples matched at both offsets and steady allocations were zero.
- Keep the existing empty handling in `NinoArrayBufferWriter.ToArray`. An early return
  accelerated the empty result but slowed the 65-byte export in all four local rounds.
  Checking for zero inside the short copy branch removed that pattern, but gave no stable
  empty-case gain. Exported bytes and allocations matched for zero through 32 KiB.
  The early return grew from 63 to 72 IL bytes and from 548 to 556 Arm64 Tier 1 bytes.
  The short-branch form used 66 IL bytes and the same Arm64 and x64 sizes as the baseline.

- Use one byte move for a one-byte copy and two overlapping short-word moves for two
  or three bytes. `CopyUpTo15` shrank from 172 to 171 IL bytes, and the dynamic Arm64
  caller grew from 380 to 384 bytes. The x64 cross listing for `ToArray` grew from 508
  to 512. Four public export rounds improved by 6–9% at 1–3 bytes with unchanged
  allocation. Direct-copy samples varied. All 80 export patterns and the 0–64 byte
  offset checks matched, and both targeted suites and netstandard builds passed (`15a5281d`).
  Keep the shared overlapping short-word path. Using one word for exactly two bytes
  and a word plus a byte for exactly three bytes grew `CopyUpTo15` by one IL byte
  and the dynamic Arm64 and x64 callers by 12 bytes each. Four public export rounds
  did not improve at either size against the identical-binary control. The 0–64
  byte and patterned export probes matched, and both targeted suites and netstandard
  builds passed. Keep the exact-width patch as a probe.
  Copy exactly 16 bytes with the shared head move and run the tail only above that size.
  An early return duplicated the head store and grew the dynamic Arm64 caller by 32
  bytes. Sharing the store grew it by 12 bytes and `Copy16To32` by two IL bytes,
  with unchanged x64 cross listings. The 16-byte public export took 4–7% less time
  in four rounds, at the same 40 B allocation. Neighboring sizes and direct timings
  varied. Alignment, export patterns, targeted tests and netstandard builds passed
  (`57ca2ebb`).

- Keep the shared span read in `MemoryFormatter.Deserialize`. Sending a fresh array directly
  to the reader instead of obtaining its span through `Memory<T>` did not change allocations.
  The inline variant grew the Arm64 Tier 1 caller from 524 to 556 bytes. Moving the fresh path
  out of line reduced the entry from 118 to 54 IL bytes but added an 88-byte helper and slowed
  the single-int fresh case and the 8,192-int reuse case in every local round. The variants
  preserved the wire bytes and read an unaligned source into the same sliced destination.

- Write canonical arrays directly into the span of a generic buffer writer. Reuse
  `NinoLayout.CopyToWire` for the disjoint case and preserve overlap and padding semantics.
  Plain span copying regressed the 8 KiB byte case in four local rounds. The existing kernel
  improved it in each rerun and reduced the 8,192-element int path to two active 32 KiB
  buffers instead of three. Both builds allocated 0 B after warmup. The raw array dispatch
  remains out of line from the concrete buffer writer path.
  The boxed buffer writer overload already uses the same path. Removing the repeated formatter
  lookup improved short boxed arrays locally, both when dispatch moved into the public method and
  when the existing helper received the lookup result. Both versions produced slower 8,192-item
  external writer samples in three of four rounds. Moving dispatch into the caller grew its
  Arm64 Tier 1 listing from 668 to 1,880 bytes, while passing the result kept it at 668 bytes.
  Keep the isolated alternatives for future measurement.

- Delay copying the prefix of a full synchronous stream buffer until the next read returns bytes.
  A non-seekable stream ending at a buffer boundary needs the EOF read but no prefix copy.
  The 64 KiB wire case improved in each of four local rounds, with the adjacent 64 KiB plus
  one byte case within noise and zero steady-state allocations on both sides. The 4 KiB timing
  was noisy. Keep the ownership and EOF paths out of the common read loop.
  A separate minimum-sized probe rental avoided the 128 KiB rental at 64 KiB EOF, but increased
  the non-seekable stream's read calls from six to ten at that size and from seven to twelve at
  96 KiB. It also grew the full-buffer helper from 88 to 129 IL bytes and had no repeatable
  steady-state gain at 64 KiB. Keep the growth read as one call into the next rental.
  A single bounded read for exact hidden-buffer `MemoryStream` instances did not improve throughput
  consistently. The variant in the synchronous rental path left the exposed-buffer branch intact but
  grew `ReadToEnd` from 91 to 174 IL bytes, plus a 44-byte helper. The 64 KiB hidden-buffer case
  had more slower than faster rounds with either placement, with zero steady-state allocations.
  Keep the existing stream path.
  In async reads, retaining both rentals across the await raised `MoveNext` from 376 to 510 IL bytes
  and added 8 B per operation in the suspended 4 KiB probe. Testing for immediate EOF in a non-async
  helper kept the suspended 4 KiB allocation flat and limited `MoveNext` to 387 IL bytes. It improved
  immediate 64 KiB reads in four local rounds but regressed immediate 4 KiB reads, so keep both
  variants as experiments. Measure immediate and suspended completions separately.

- Use a direct lookup before constructing a cached value when the target has no stateful factory overload.
  On a miss, pass the value to `GetOrAdd` and return the value that wins publication. The portable layout cache's
  factory already computed outside the dictionary lock. The new hit path removed the closure and allocation calls
  in both Arm64 and x64 listings. Use the stateful factory overload where the target supports it.
- Reserve the output array and its offset when a BCL copy API accepts those directly. The portable
  `BitArray.CopyTo` path writes into the output instead of renting and copying a temporary. For 24 KiB of
  bits the active payload footprint falls from about 72 KiB to 48 KiB, with the same padding on the wire
  (a0ee2414).
- Keep per-segment reservations for the general fixed-width sequence writer. Reserving all
  segments at once and checking the declared element count removed repeated capacity checks,
  but slowed both the two- and 32-segment small-int writes in all four Arm64 rounds.
  The longer writes were mixed, with identical bytes and zero steady-state allocations.
  `WriteSegments` grew from 118 to 246 IL bytes and from 1,524 to 1,592 Arm64 Tier 1 bytes.
- Reserve the header and payload together when writing a multi-segment byte sequence with the built-in
  formatter. Reuse `NinoSequences.CopySegments` to check the declared length while copying into the reservation.
  Match the exact formatter type so the JIT removes the generic segment loop. The Arm64 entry shrank from
  3,020 to 352 bytes and calls the existing 708-byte copy helper.
  Use TryGet as the sole loop guard; it already handles the end position. Arm64
  also folds the destination slice check after CopyTo, so keep the two span
  operations. A separate two-segment branch slowed the 512-byte write by 22–28%
  on M4. The simpler guard was broadly on par across four default-JIT pairs,
  including two, four and sixteen segments and the cache-boundary controls.
  All nine wire comparisons matched at 0 B. CopySegments moved from 87 to 78 IL
  bytes, 680 to 712 Arm64 FullOpts bytes and 202 to 198 offline x64 bytes.
  The .NET 8/10 suites and both netstandard builds passed (`fc95d7ea`).
- Decode a portable `BigInteger` payload of at most four bytes as a signed integer after the length check.
  Sign-extend the last byte of a three-byte payload and use the existing unaligned reads for two and four bytes.
  This removes a 32-byte temporary allocation per short value on CoreCLR running the netstandard2.0 build.
  Keep the BCL write path on modern .NET. A direct store for one to four bytes
  improved those cases after tier promotion, but slowed wider writes. Branching
  before reservation instead of sharing the reservation did not fix the wider
  cases. In the same public-entry probe, Arm64 Tier 1 for a two-byte value went
  from 1,728 bytes to 1,052 and 1,152, respectively. Formatter IL went from 36
  to 96 and 103 bytes, and x64 cross-JIT formatter code from 204 to 271 and
  386. The x64 caller stayed at 157 bytes. Six wire comparisons at offsets
  0, 1 and 7 matched for both variants, with zero steady allocation.
- Rent a character buffer for portable `StringBuilder` writes of 4,096 or more characters. Use the existing
  UTF-16 reservation and copy only the builder's length. A 1,024-character cutoff regressed multi-chunk
  builders, while 4,096-character single- and multi-chunk cases improved without a steady-state allocation.
  An empty pool bucket still allocates on the first rent.
  Write one UTF-16 builder character directly on modern .NET, with the
  existing short chunk loop out of line. Keeping the loop inline enlarged
  the Arm64 Tier 1 caller from 1,552 to 1,636 bytes and did not reliably
  improve the single-character case. Outlining it took the caller to 988
  bytes plus an 812-byte helper and beat both controls on one character in
  four rounds. The x64 cross-JIT formatter moved from 921 to 573 bytes plus
  a 459-byte helper. Copy dispatch IL went from 108 to 52 bytes plus an
  82-byte helper. Other sizes and repeated multi-chunk rounds were noisy.
  Wire bytes and steady-state allocation matched.
  Pack a two-character payload in the short helper. A second inline length
  check slowed three-character writes, and sharing the inline check with the
  single-character path moved its timing in the wrong direction. Outlining
  kept the two-character Arm64 caller near baseline (848 to 852 bytes),
  while the helper grew from 816 to 1,084 Tier 1 bytes and from 459 to 522
  x64 cross-JIT bytes. Dispatch IL was 52 to 53 bytes and helper IL 82 to 111.
  Four cross-chunk CPU-time pairs beat both controls, while wall time did so
  in three rounds. Single-chunk results overlapped A/A. Eight wire samples
  and 577 generated files matched, with zero steady write allocation.
  Replacing the pinned UTF-16 reader append with the span overload kept bytes, truncated-input
  errors and zero allocations in the reuse probe. It reduced the Arm64 Tier 1 formatter from
  1,280 to 1,220 bytes but increased x64 ReadyToRun from 388 to 413 bytes. No timing advantage
  persisted across the measured lengths. Keep the shared pointer append for now.
  Append a single Latin-1 character directly instead of sizing the builder and widening a
  chunk. The one-character reuse probe fell from about 30 ns to 6 ns in four local rounds,
  with zero allocations and identical bytes. Longer inputs had no stable regression.
  The helper went from 97 to 111 IL bytes and 421 to 450 x64 ReadyToRun bytes.
  For two to sixteen Latin-1 characters, widen into one small stack buffer and append once.
  Keep the stack allocation out of line from the single-character and chunk paths. Local
  Arm64 reuse probes for 2, 8 and 16 characters improved in all four rounds, at zero
  allocations. The 17-character control was mixed. Output bytes matched, including
  unaligned input, and a test covers embedded nulls and high Latin-1 characters.
  Extend the one-append approach to 256 characters through a separate bounded stack helper.
  Move the existing chunk loop out of line as well. Keeping it in the dispatcher regressed
  the 16,384-character reuse probe even when the short helper was unchanged. With the loop
  isolated, 64 and 256 characters improved in every local round, and 257 and 16,384 recovered
  and improved too. Tier 1 was present on both sides, with the probe caller shrinking from
  2,004 to 1,336 Arm64 bytes at 16,384 characters. The dispatcher fell from 125 to 54 IL bytes,
  with separate 26- and 97-byte helpers. Wire bytes and steady-state allocations matched.
  Pre-sizing the cleared builder before the chunk loop saved 16 B when growing from a small
  capacity, but regressed 1,024 characters. Restricting it to insufficient capacity at 4,096 or
  more characters improved growing builders at 4,096 and 16,384 in all four local rounds, yet the
  4,096-character reuse control slowed in each round. The guarded helper grew from 97 to 122
  IL bytes, 928 to 992 Arm64 FullOpts bytes and 421 to 472 x64 ReadyToRun bytes. Wire bytes
  matched. Keep the existing chunk growth for now.
- Write the zero count of an empty raw dictionary without entering its out-of-line pair loop. Keep the
  reservation shared with non-empty dictionaries. The Arm64 probe saved about 7–9 ns on empty dictionaries
  at both output offsets, with the non-empty controls on par.
  Skip entry-view lookup after the same zero reservation for raw hash sets. The Arm64 probe saved 5–7 ns,
  with dense and sparse non-empty sets on par.
  Return once for an empty hash set read after creating or recycling its destination. This keeps reuse and
  comparer setup intact and removes the repeated zero tests before capacity and fill. The local Arm64 probe
  saved 6–8 ns on empty reads with unchanged allocations; the formatter shrank from 424 to 416 IL bytes.
- Share the actual presizing clamp in `NinoElements.Capacity` and keep the cheap eligibility tests at the hash set
  and dictionary callers. A shared inline branch shortened the two callers by 10 IL bytes but added a 14-byte
  helper. The Arm64 listings kept their sizes, only the x64 set listing shrank (6 bytes), and the local A/B showed
  no repeatable throughput gain or allocation change.
- Leave the empty-tail test inside `NinoElements.WriteItems` for variable-size queue writes. Adding a
  `first != count` guard at the queue saved work for a one-string queue in three of four local rounds, but
  the 16-string case varied widely and the 1,024-string case stayed on par. The extra branch grew the Arm64
  caller by 16 bytes and the x64 queue formatter by 12 bytes. The raw block path keeps its existing guard.
- Write the two fixed-stride queue runs with a reference and count. The validated ring already bounds both
  runs, and their shared reservation covers both destinations. Four local default-JIT rounds with a duplicate
  baseline showed a gain for one integer and for 64 contiguous or wrapped integers, with zero allocations.
  Wrapped `DateTimeOffset` and the integer working sets immediately below and above 64 KiB were on par.
  The queue formatter grew from 205 to 209 IL bytes. The integer caller fell from 3,024 to 3,004 bytes on
  Arm64 FullOpts and from 2,535 to 2,445 bytes on x64 cross-JIT. The `DateTimeOffset` caller grew on Arm64
  (2,236 to 2,284) and shrank on x64 (1,969 to 1,934). The wire and 577 generated files matched.
- Keep the portable stack's reverse-then-write path. Walking the rented snapshot backwards for variable-size
  reference elements saved about a fifth of the time for 512 and 2,048 strings in the forced fallback, but
  grew the raw Tier 0 helper from 356 to 408 Arm64 bytes. The 64-integer case slowed by 2–4% across four
  local rounds even after a value-type gate restored its FullOpts listing to 1,572 bytes.
  A separate reverse-walk helper restored the raw Tier 0 helper to 356 bytes. Dispatching to it from
  `Serialize` added 40 IL bytes there and grew the normal-layout Arm64 probe from 4,240 to 4,624 bytes.
  The 64-integer normal-layout control slowed in every round, so keep that dispatch out of the hot entry.
  Replacing the reverse-walk span indexer with `Unsafe.Add` removed its range failure in both listings
  (Arm64 1,172 to 1,164 bytes, x64 920 to 858). The one-string forced-fallback case slowed in all four
  local rounds, so the bounds-check removal alone does not justify reviving that candidate.
- Walk the checked key and value arrays with `Unsafe.Add` in the variable-size `SortedList` write loop.
  This removed range failures and saved 196 Arm64 bytes for `int` keys and string values. Single-pair writes
  slowed in four local rounds. Keep the one loop: a separate single-pair arm duplicated the inlined formatter,
  growing Arm64 from 1,828 to 2,772 bytes and x64 from 1,584 to 2,770.
- Keep the current seeded string hash loop. An unaligned UTF-16 word load and a byref tail preserved the
  reference hash and removed range failures (Arm64 588 to 428 bytes, x64 611 to 550), but four-character
  inputs slowed by 6–8% across four local default-tiering rounds and 15-character inputs also slowed.
- Reserve the raw `PriorityQueue` count and pairs together inside its existing by-value writer helper.
  Keep the public writer's dispatch intact: putting the raw gate in `Serialize` slowed the reference-element
  loop, while moving it into the helper restored the original Arm64 and x64 listings for that loop.
  The local 257-integer-pair case cut write time by 56–61%, with the enumerator preserving heap order.
  For reads, checking the whole pair block and walking its byref removed both per-pair bounds checks.
  Using `Peek` and advancing on success also avoided the reader copy, but neither variant consistently
  improved throughput: the `Enqueue` call remained. Keep the experiment isolated and evaluate heap
  construction separately from byte loading.
  Adopting an already ordered integer heap through checked .NET 10 fields cut the local 8,193-pair reuse
  time by about 74%, with zero steady-state allocation. Keep the public enqueue fallback for any wire
  order or comparer that does not match the checked heap (`runtime-internals`).
  Reuse that checked node array for writes, copying the live count in heap order. The local 8,193-pair
  integer case cut write time by about 90% against the counted enumerator path.
  Use the shared tuple block plan to prove identity layout for arbitrary raw element and priority
  types. For padded or reordered tuples, write directly from the typed heap with a constrained
  `WriteFixed` call. A shared `WriteAll` trial was faster on large mixed pairs, but its interface
  tail allocated up to 48 B per short write before tier promotion. The retained variant used 0 B
  in the timed intervals and cut elapsed time in all four M4 rounds for 257 and 4,097 pairs of
  custom index/uint, custom index/ushort and byte/long at both offsets. The int/int control at
  4,097 pairs was effectively unchanged in the aligned runs. The heap helper grew from 92 to
  228 IL bytes, while its int/int Arm64 Tier 1 instance moved from 296 to 292 B. The x64 cross
  instance grew from 222 to 732 B and the corresponding raw caller shrank from 643 to 364 B.
  x64 throughput remains unmeasured (`ef8e4195`).
- Batch writes at the typed lookup boundary. Trying an array branch in the public grouping formatter
  removed its enumerator allocation but slowed short foreign groupings even after outlining. Dispatch
  once from the lookup formatter, write the existing group array and reuse the span writer for each
  group. Return after the zero count to avoid both dispatch and enumeration on empty lookups.
  Four local M4 pairs at offsets zero and one reduced the 257- and 8,193-element int and string lookup
  timings with byte-identical outputs and 1,064 B fewer allocations per write. Foreign short-input
  timings varied. The lookup entry grew from 75 to 99 IL bytes. For int elements it grew from 1,896
  to 2,168 B on Arm64 Tier 1 and from 1,417 to 1,670 B on x64 cross-JIT. The x64 listing does not
  establish throughput (`45570cc1`).
- Write packed sorted-set nodes in order into the reserved output for sets of 32 or more items.
  Keep the existing layout probe and bound the span by the count. Retain the array traversal for
  references and other layouts. Sharing the span traversal with reference sets or adding the gate
  to the shared copied-pair writer hurt the controls. Warm both sides of the count threshold
  before measuring short fallbacks. Four M4 rounds on `SortedSet<int>` at offsets zero and one
  improved the 32–8,193 item writes by 10–30% at zero steady allocation, with matching bytes.
  Reference set results varied and the dictionary candidate was withdrawn. The dispatch grew from
  48 to 81 IL bytes and the packed helper added 67 B of IL. The Arm64 packed helper and recursive
  copy were 408 and 268 B; the x64 cross JIT emitted a 221 B MinOpts helper and no span recursion
  listing. The measured caller stayed at 208 B on Arm64 and 170 B on cross-JIT x64 (`04894a90`).
  Route compact sorted dictionary pairs from their own dispatch into the same tree writer.
  Require an identity block map and a pair of at most eight bytes. Larger pairs and short
  fallbacks were sensitive to the count gate. For a single packed pair, write the root item
  directly instead of entering the recursive traversal. The final M4 probe improved
  `SortedDictionary<int,int>` writes at all sampled sizes from 1 to 8,193 and at both offsets
  in all four rounds. Larger, padded and reference pair controls varied. Wire bytes and zero
  steady allocation matched. The dictionary dispatch grew from 76 to 114 IL bytes and the
  new helper took 42 IL bytes, 380 Arm64 FullOpts bytes and 298 x64 cross-JIT bytes.
  The outer measured caller stayed at 208 and 170 B, respectively (`cccbcc28`).
- Walk a checked linked-list ring in pairs for raw writes at 32 or more items. Bound the writes by
  the captured count, check the ring and version at the end, and keep padding clearing. Compile
  the new helper with `AggressiveOptimization` and keep the original small-list writer inlined.
  A one-node loop lost on large int lists. The two-node trial without early optimization also
  lost, and merely moving the branch out of the old writer still hurt small lists. With both
  inlining choices the M4 int probe improved 127-item writes by 5–31% across all pairs, while
  the 31-item zero-offset control added under 1.1 ns in three rounds and 8.2 ns in one. Larger
  results varied. Wire bytes, including an odd tail and padding, and zero steady allocations
  matched. The original writer kept 195 IL bytes. The ring helper added 145 IL bytes and was
  316 B on Arm64 and 240 B in the x64 cross listing for int (`b099fc74`).
  Keep the checked linked-list builder's current read loop. Separating the first node removed
  the null predecessor tests without a repeatable end-to-end gain. A second trial consumed
  raw elements from the collection entry's already checked payload, then advanced the reader
  once after publishing the ring. It still allocated 48 B per int or long node and timings
  changed direction across the four M4 pairs at offsets zero and one. The int Arm64 entry was
  436 B before and 120 B plus a 288 B raw helper after. The x64 cross entry was 517 to 538 B
  in MinOpts and the raw helper was 284 B in FullOpts. Entry IL was 172 to 177 B with a
  104 B helper. Wire bytes and allocation matched and the .NET 8/10 targeted suites passed.
  Both trials were withdrawn.
- Skip the seen-key set when the stale dictionary pass removes every entry. Preserve removal and recycling
  order. Keep the empty test in the stale pass rather than the refill loop. The latter placement regressed
  same-key reuse in the local A/B, while the stale-pass version saved 64 bytes per empty refill.
  A shared empty set improved partial removal but made the 16-entry empty refill 5–8% slower across four
  local rounds, including two with reversed loading order.
  Splitting the empty and partial removal loops grew the Arm64 helper from 620 to 924 bytes. The local
  partial-removal timing stayed within the noise band, so keep the shared loop.
  Skip the set when one key survives in a dictionary of at most 16 entries on
  modern .NET and neither element type needs recycling. Scan with the
  dictionary's comparer and retain the existing path for larger dictionaries.
  An unbounded scan slowed the 2,048-entry unaligned case by 25–45%. With the
  bound and an outlined scan, the large control varied alongside A/A, and the
  two-entry unaligned case improved by 12–61% across four M4 pairs. Eligible
  cases allocated 32 instead of 208 B. RemoveStale and its helper used 126
  plus 80 IL bytes versus 84, 972 plus 672 Arm64 FullOpts bytes versus 692,
  and 810 plus 605 emulated x64 bytes versus 560. All 54 wire cases matched.

- A branch whose condition is a method call (`RawFormatter.Is`, `NinoBlockCollections.FixedSize`, ...) is compiled and
  inlined even for the types where it is dead, and uses up the budget there. Keep rare or large paths (vector loops,
  element by element loops, string allocation, recycling) out of line (`NoInlining`), with only the common path inline.
  Check that the outlined path is cold for each common input size. In the packed 32-bit
  `SortedList<uint, uint>` write probe, outlining the generic scalar fallback shrank the Arm64 Tier 1
  SIMD entry from 1,268 to 100 bytes, but moved singleton writes across a call boundary and slowed them
  in all four local runs. Keeping the two unaligned singleton stores in the entry took 128 bytes.
  Compare the caller and helper on Arm64 and x64, and measure singleton, short and vector-sized inputs
  separately. A smaller hot entry does not show that the outlined call is cheap.
  This holds for a test that folds away too: callers' budgets count an inlinee's whole IL, so a branch for one platform
  goes in the out-of-line part, never in the inline one. Evidence: `Is64Bit || IsX86 || UnalignedWide32` written in
  `NinoUnsafe.Copy` changed the x64 code of an ImmutableSortedDictionary write (`NinoBlockCollections.WriteAll` inlined,
  `Copy` left a call) in an `asm.py` dump with `DOTNET_TieredCompilation=0`; moved into `CopyOver64`, all 990 methods
  were identical to main (`CopyBlock` keeps Arm64's branch out of `Copy` for the same reason). The root's own IL counts
  the other way: the JIT sizes a method's whole inlining budget from its IL size (estimate 60 + 3 per IL byte, times
  10), dead branches included, so a folded test in a root that is otherwise one call (an interceptor) raises the budget
  and changes what is inlined under it. Never add an option to the default path as a run-time branch, not even a `static
  readonly` one: decide it at generation time (an MSBuild property the generator reads, `NinoRegularCalls`,
  `NinoPrefetchLargeCollections`). Evidence: an interceptor `NinoPrefetched.Enabled ? SerializePrefetched(...) :
  Serialize(...)`, folded at Tier1, made PolymorphicClassArray's `Serialize(in PolymorphicClassBase[], IBufferWriter)`
  1,060 bytes instead of main's 770 (single-block inlinees 142 to 155; `DOTNET_TieredPGO=0` dumps), while
  NonGenericBase's 1,112 methods stayed identical.
- Add nothing to a method whose inlinee sits at the edge of the JIT's inlining budget, not even code that is dead for
  the instantiation at hand: the budget is spent by IL size, before branches fold. `NinoBlockCollections.Lanes16` (three
  inlined copies in `Lanes`) became a call per group on Arm64 when `Lanes` gained a fused bool check (accumulators by
  reference, two groups a step, or groups returned by value), every lane path 1.4 to 5 times as slow on Neoverse-N2
  (TupleList and `HashSet<int>` writes included, run 41035440735), while x64 inlined everything. Put the new path in a
  method of its own and pick it by a `static readonly` (`LanesChecked`, AVX2 only), and check the Tier1 Arm64 listing
  under qemu (`platforms`, with tiering on: `DOTNET_TieredCompilation=0` listings inlined what Tier1 did not) for `blr`
  in such a loop. Two groups a step (a smaller loop share for one-vector groups) measured nothing on AVX2 Zen and lost 3
  to 8 % on `HashSet<int>` writes (run 41052335826): not landed.

- Give an out-of-line kernel one out-of-line entry with its loops inlined into it, and keep that entry at Tier1:
  a size dispatch (`NinoUnsafe.CopyOver64`) that called the vector loop (`CopyVectors`) out of line made two calls per
  copy of 129 bytes to 32 KB, three through `CopyToNew`, where rc.7's inlined `Copy` made one: 100 12-byte structs
  serialized in 0.98-1.27 times rc.7's time (.NET A/B run 41413072278). The loops are now `AggressiveInlining` into
  `CopyOver64Inline`, which `CopyOver64` and `CopyToNew` both inline. Never mark such an entry `AggressiveOptimization`
  to keep the loops' layout: fully optimized code folds no `static readonly` field (it tested `NinoUnsafe`'s class init
  and loaded `AmdCopy` and `IceLakeMemmove` per copy) and the class-init call made it save four registers in the prolog
  of every copy, the 65-128 byte ones included; that form measured Inventory serialize 1.114 times main on EPYC 7763
  (run 41455371503). With one call per copy at Tier1, 100 12-byte structs serialized in 0.85 of rc.7's time on EPYC
  7763 and on par on Neoverse-N2, but still 1.07-1.09 on Xeon 6973P and 8573C (one job each, run 41534216283; the same
  loop as rc.7, not reproduced on a local AVX-512 Xeon). Round 126 tested that gap and did not close it: 1.077-1.084 on
  three Xeon 8573C jobs, 1.045-1.069 on 6975P, 1.03-1.06 on 6973P, 1.007-1.031 on 8370C, MemoryPack's identical copy
  0.98-1.04 in the same jobs; unchanged with
  `DOTNET_JitAlignLoops=0` (1.084, 1.069) and `DOTNET_TieredPGO=0` (1.061) in the quick jobs, and checking the
  64-byte path first in `CopyOver64Inline` was no faster locally on Emerald Rapids (.NET A/B runs 41621699299,
  41659495441, 41665524359, 41671303527). The gap was in main before round 123's copy changes (main 1.118 on 8573C,
  run 41413072278) and head against main is on par there. Round 127 found the source: where the writer's buffer lands
  in the page, not Nino's code. Judge such a gap by the destination's page offset before touching the path: print it
  per process (`(addr) & 4095` in the benchmark's Setup, A/B only) and run the same probe code in both builds. The
  pooled 320 KB buffer (ArrayPool, LOH) starts at page offset 1352 in every rc.7 process and 1944 in every main
  process, fixed per build by what `SimpleTest`'s static constructor rents first; on Xeon 8573C the case took 11.95-12.12
  ns at destination offset 1356 for every source offset seen and 12.84-13.15 ns at 1948 (20 ABAB rounds a job, run
  41806557589; 6975P 9.45-10.01 against 10.05-10.54). The machine code of the case is the same in both builds but the
  copy target; main's and rc.7's copy shapes measured on par inside one binary (8573C 12.03 against 12.05, run
  41687210928), and one probe calling `NinoSerializer.Serialize` into its own buffer measured 1.004 (8573C) and 1.008
  (6975P) of rc.7 across the two builds' processes, where `NinoSerializeSimpleStructs` measured 1.083 and 1.044 in the
  same jobs (run 41794983604); a null change measured 0.996 (run 41679647486). Within rc.7 alone the writer object's
  placement moved the case from 9.2-9.6 to 10.7-12.0 ns on 6973P (run 41802774827), more than the gap. A local copy
  table on Emerald Rapids took 14.6 to 41.8 ns for 1,200 bytes by (dst - src) mod 4096. A 200-byte or an 85 KB array
  allocated before the writer did not move the buffer. Nino cannot choose where a caller's pooled array lands, so this
  gap is not closable in Nino's code. Check the Tier1 listing on x64 and with the Arm64 cross JIT (`benchmarks/cross-jit.sh`): one
  method, a two-instruction prolog.
- Inlining a small out-of-line method that the JIT left as a call can be a wash: `NinoLookup.Add` inlined into the
  `ILookup` read (one call per grouping instead of two) measured 1.002 and 1.003 times main on EPYC 7763 and
  Neoverse-N2 (run 41455371503); not landed. Forcing `NinoDictionary.Refill` (the merge into an existing dictionary)
  inline, as the JIT chose for rc.7, grew every read into an existing dictionary (PooledSimpleClass `__ReadBody` 2,731
  to 3,514 bytes, 127 to 189 stack accesses) and made `NinoDeserializePooled` no faster in an in-process timing loop
  (`benchmark`); not landed.

## Registers

- Never pass `ref reader` or `ref writer` to a method that is not inlined, even in a dead branch: the address keeps
  the struct in memory for the whole method. Pass it by value and return it (`reader = ReadEach(values, reader)`).
  An instance method of `Reader` or `Writer` left out of line passes `this` the same way: `Reader.ReadString(current)`
  calls `ReadStringOver` with the reader's address whenever `current` may be non-null, so generated code reads a new
  instance's strings with `ReadString()` (`MemberModel.Fresh`): SimpleClass's `__ReadTop` went from 108 stack
  accesses to 10, TolerantClass's from 174 to 10. Look for `Reader:...:this` and `Writer:...:this` calls in a listing.
  The same rule keeps multi-segment input a copy: a `ReadOnlySequence<byte>` read across segments in place would need
  every bounds check to refill the reader out of line instead of throwing (the reader's address in every method that
  reads, or a refill inlined into every check), and regions, sections and deferrals address the data by absolute
  position. So the sequence overloads copy data that spans segments once, out of line (to the stack up to 1,024 bytes,
  else a pooled array: `NinoSequences`), and read one segment in place (Nino `d275a71e`). Compute the segmented
  length once and pass it to the pooled copy. Keep the bounds check there, before converting the length to an int.
  Share the copy out of line across direct and regular callers. Its bounded spans must consume exactly the
  reported length, so a custom segment's inconsistent `RunningIndex` cannot expose unfilled scratch bytes.
  A byref destination cursor and an exact-length destination span folded the second length
  check in Arm64 FullOpts. The first warmup did not reach Tier 1. With longer warmup, the 128 by
  512-byte read improved in four local rounds, but the write had mixed results. Allocations stayed
  at zero and wire bytes matched. The helper grew from 76 to 110 IL bytes, 728 to 736 Arm64 Tier 1
  bytes, and 207 to 233 x64 ReadyToRun bytes. Keep this variant as a read-path experiment.
  Isolating scratch reads and switching to `CopyToNew` or `CopyBlockUnaligned` did not reproduce
  the read throughput improvement. Both scratch helpers used 114 IL bytes and 238 x64 ReadyToRun
  bytes, versus 76 IL and 207 x64 bytes for the shared helper. The Arm64 Tier 1 sizes were 736
  and 724 bytes versus 732. Wire bytes and steady-state allocations matched. Keep the shared copy
  while the read-only alternatives remain in the experiment worktree.
  Keep the explicit `TryGet` loop and stop at the last segment. Replacing it with `foreach` made two
  32-byte segments slower on both reads and writes in two local rounds, even though Arm64 machine code shrank
  from 708 to 684 bytes. The JIT already shared the `CopyTo` and `Slice` bounds check.
  Do not probe array identity and offsets to coalesce two adjacent byte segments on every write.
  Two 32-byte adjacent segments went from about 24 ns to 27–35 ns in four local rounds.
  Disjoint short inputs also slowed, and inputs plus output around 64 KiB varied across A/B
  and A/A. `WriteSegments` grew from 118 to 138 IL bytes, 352 to 432 Arm64 FullOpts bytes
  and 312 to 369 offline x64 bytes. The probe helper added 169 IL, 1,184 Arm64 and 1,052
  offline x64 bytes. All six wire cases matched at an unaligned output offset with no
  steady allocation. Keep the patch for experiments.
  Skip copying and slicing for an empty span, after obtaining the span from the
  segment. Keep the last-segment test and final remaining-length validation.
  Two 32-byte segments interleaved with empty segments wrote in about 28 ns
  instead of 31 ns in the local default-JIT probe. Ordinary inputs and the
  shared read path overlapped A/A. The helper grew from 76 to 85 IL bytes,
  728 to 732 Arm64 Tier 1 bytes, and 641 to 649 offline x64 bytes.
  All six wire cases matched at an unaligned output offset, at zero allocation.
  Test the segment position before `TryGet` in the loop condition. Dropping the
  position check slowed the two nonempty 32-byte segments. Moving it into the
  condition kept nonempty timings within A/A and improved the interleaved-empty
  short writes. Arm64 Tier 1 fell from 732 to 700 bytes and offline x64 from
  649 to 603, with 2 more IL bytes. Six wire and round-trip checks per candidate
  passed at zero steady allocation.
  Raising the stack cutoff from 512 to 1,024 bytes avoided the pool round-trip for 516-, 804- and 1,024-byte
  messages in the local probe. Measure both sides of the cutoff and keep the fixed buffer out of inline callers.
  Read a complete built-in byte array directly from two segments after validating the header and actual length.
  Check segment adjacency before entering the direct path. Probing both spans on three-segment input regressed
  the fallback. Keep the copy through scratch when reusing an array that aliases input, or when replacing
  an array with an element or array recycler registered. Recyclers can mutate the source before it is copied.
  Passing the checked start position cut 12 IL bytes from `TryReadByteArray`
  but grew its offline x64 from 1,688 to 1,711 bytes without a repeatable timing
  gain. Checking adjacency inside that helper grew it to 212 IL bytes, 1,944
  Arm64 Tier 1 bytes and 1,757 offline x64 bytes. The extra call slowed
  three-segment input in all four paired rounds. Both candidate patches passed
  six wire and round-trip comparisons.
  Keep split headers in a separate cold method that joins only the four header bytes. Joining them inside
  the ordinary two-segment reader regressed complete-header inputs in the local A/B.
  Assemble the split length directly from the two spans. The 2+2 split of a
  64-byte payload read in 21.31–23.71 ns across four warmed rounds, from
  23.26–27.28 ns. Complete headers and near-L1 payloads overlapped A/A.
  The helper grew from 193 to 277 IL bytes and 1,412 to 1,476 Arm64 Tier 1
  bytes, but offline x64 fell from 1,345 to 1,268. All seven unaligned
  wire and round-trip probes passed at zero steady allocation.
  Keep the bounded loads in the split header reader. Byref halfword loads
  reduced Arm64 Tier 1 from 1,476 to 1,400 bytes and offline x64 from
  1,268 to 1,182, but the 2+2 case was slower against its paired baseline
  in three of four rounds. Switching on the actual left span length kept
  IL at 277 bytes and reduced native code to 1,464 Arm64 and 1,240 x64
  bytes, but timings varied and the 1+3 case was slower. Both variants
  passed five wire and round-trip probes, with zero steady allocation.
  Two reverse byte loops assembled the split word without a switch and
  shrank IL from 277 to 223 bytes, Arm64 Tier 1 from 1,476 to 1,392 bytes,
  and offline x64 from 1,268 to 1,170. The 3+1 split nevertheless slowed
  in four paired rounds (24.0–25.0 ns for the candidate). Keep the switch
  and retain the loop patch. All five wire probes matched at zero allocation.
  Sharing both segment and count validation through inlined helpers still slowed split-header reuse
  across both loading orders. Keep those checks local and share payload copying and replacement.
  Keep the regular and direct sequence readers' stack and pool scopes around their own span calls.
  Reusing the stream reader adapters inside a shared sequence scope made the 32-segment probe slower
  in both entry points across four rounds. Share the segment copy, rent and return primitives.
  Keep `Span.CopyTo` for direct two-segment payloads even when disjointness is known. Using
  `NinoUnsafe.Copy` made all measured sizes slower across both load orders and grew the Arm64
  two-segment method from 1,796 to 2,196 bytes.
- Mark every small helper a read or write path calls `AggressiveInlining`, even one whose arguments are constants
  and that looks certain to fold: in a large method the JIT's inliner gives up on it. `NinoLanes.Paired`, called
  with constants in the AVX2 group loop, stayed a call per load and spilled the vectors around it: AVX2 writes took
  twice as long as SSSE3's. Look for such calls in the Tier1 listing of every ISA a path takes (`--asm`, with
  `DOTNET_EnableAVX512=0` and `DOTNET_EnableAVX2=0` too).

- Separate the small raw fallback's typed local from the large raw entry. A size check in the same method does not
  stop Tier 0 from reserving a stack slot for a large `T` on an untaken branch. Keep a single final load within each
  path and compare trusted packed and wide reads as well as untrusted allocations.
  Check the frozen untrusted setting before the layout branch and return directly from the existing
  validated reader in that mode. Combining both fallbacks into a typed return added a full stack slot
  and a second copy to the trusted Arm64 caller. A result local with a single return kept both copies.
  Testing the setting first restored the 156-byte trusted caller. Untrusted reads of 1 and 4 KiB
  saved 1,048 and 4,120 allocated bytes at offsets 1 and 7 and beat the identical-binary control in
  all four local rounds at each offset. The x64 offline entry retained both settings branches and
  tail-jumped into the validated reader with no large stack slot. Keep the byref layout fallback
  for trusted types that cannot be loaded directly.

- Read a collection whose shape repeats into its existing instance with a loop of its own over the data, as the fresh
  read does: `Reader.RefillStringPairs` (raw keys and strings into an existing `Dictionary`, the position and the end in
  registers, the value read over the current one with the compare its only call) instead of the general merge, whose
  value formatter read through the caller's reader copy in memory and called `ReadStringOver` and then `SameLatin1`
  per value. In-process timing loop (`benchmark`): a pooled class holding only a 10-entry `Dictionary<int, string>`
  139-162 ns against main's 183-213 and rc.7's 178-198; and store a value read over the current one back only when it
  differs (no write barrier per unchanged string): 123-156 ns. On runners `NinoDeserializePooled` took 0.83-0.92 of
  main's time on every CPU (.NET A/B run 41576130603, 12 rounds a job) and 0.96-1.00 of rc.7's on x64, 1.04 on
  Neoverse-N2, where neither the string compare below nor `CopyPairs` out of line moved it (1.005, 1.007 against a
  1.000 floor, run 41593418245; `CopyPairs` out of line made `NinoNodePooled` 1.033).
- Copy the dictionary refill's Reader with struct assignments. The Arm64 JIT emitted redundant zero stores
  for a default local followed by field-wise CopyTo. Assignments reduced Deserialize IL from 181 to 171 B
  and the Arm64 caller from 1,100 to 1,088 B for string keys and 536 to 520 B for integer keys.
  Emulated x64 callers shrank from 947 to 935 and 397 to 383 B. Passing and returning Reader by
  value instead grew both callers and both refill listings. Four warmed M4 pairs did not reproduce the
  first run's large integer-key swings. Allocations and 30 wire comparisons matched.
  Use the same assignments for the local Reader in fresh dictionary pair loops.
  ReadPairs and ReadGuarded each lost 10 IL bytes. The string-key ReadPairs went
  from 1,156 to 1,080 Arm64 FullOpts bytes and 932 to 925 emulated x64 bytes.
  The integer-key guarded loop moved from 520 to 524 Arm64 bytes and 373 to 371
  emulated x64 bytes. Default-JIT timing varied; the 2,048-pair slowdown did not
  repeat when the measured batches ran without GC. Allocations and all 30 wire
  comparisons matched.
  Use assignments for the outer fresh dictionary copy as well. The pair loop
  still receives a local and the caller only writes it back after success.
  Read lost 10 IL bytes (229 to 219). The string- and integer-key Arm64 callers
  moved from 960 to 952 and 628 to 616 bytes, and emulated x64 from 820 to 807
  and 448 to 436. Four M4 default-JIT pairs varied alongside the identical-binary
  controls. Allocations and 30 wire cases matched at offsets zero, one and seven.
  Keep ordered string-key probing out of the shared refill loop for now. Passing
  each existing key into StringFormatter until the first mismatch cut same-order
  allocation from 512 and 19,680 B to zero for 16 and 512 entries. Warmed M4 pairs
  improved those cases, but the 16-entry unaligned reversed case regressed even
  after accounting for an identical-binary control. Outlining the probe improved
  that control but slowed the custom-comparer case by 35–92%. Inline Refill grew
  from 298 to 469 IL bytes, 2,484 to 2,916 Arm64 FullOpts bytes and 2,056 to
  2,649 emulated x64 bytes. The inline variant matched all 36 wire cases, and
  both variants passed the changed-key and reference-comparer regressions.
  An outlined ordered prefix can stop before the first different key, then
  leave the ordinary merge to read that key. This kept 16- and 512-entry
  same-order refills at zero allocation, with 512 entries 35–44% faster in
  four M4 pairs. The 1,024-entry result reversed with warmup: shorter runs at
  offset one slowed in every pair, while longer runs at both offsets sped up
  by 40–68%. The default JIT produced distinct OSR bodies (2,168 and 1,112 B).
  Optimizing from the first call preserved the 1,024-entry gain but slowed
  16-entry same-order reads at both offsets. Refill and its helper used
  406 plus 125 IL bytes versus 298, 2,636 plus 1,508 Arm64 FullOpts bytes
  versus 2,484, and 2,243 plus 1,370 emulated x64 bytes versus 2,056.
  Both variants matched all 48 wire cases and passed targeted tests.
  Keep the existing refill until the prefix loop can improve both tiers.
  Keep the comparer-based SameKeys loop. A separate ordinal string loop behind
  an exact default-comparer check did not improve refill timing repeatably. The
  isolated 1,024-key comparison improved by less than 1% across four M4 pairs,
  and the 512-key timings tracked the identical-binary control. IL grew from
  98 to 151 plus 74 B. Arm64 FullOpts grew from 780 to 924 plus 428 B, and
  emulated x64 from 662 to 792 plus 374 B. Refill itself kept its code size.
  Allocation and all 36 wire comparisons matched. The candidate was withdrawn.
  Keep the rented-key indexer in SameKeys. The caller proves the buffer length,
  and a byref cursor removes its FullOpts range failure on both architectures.
  It did not produce repeatable gains in isolated or end-to-end M4 comparisons.
  IL grew from 98 to 111 B, Arm64 FullOpts fell from 780 to 760 B, and emulated
  x64 grew from 662 to 677 B. Arm64 Tier 1 fell from 1,540 to 1,512 B and still
  checked the dictionary enumerator's entries. All 45 wire cases and allocations
  matched. The candidate was withdrawn.
- Compare an unchanged short string read into an existing object inside the out-of-line `ReadStringOver`, never in
  the inlined `Reader.ReadString(current)` nor in a loop: `NinoStrings.Same` (one-byte Latin-1 header, length, the chars
  widened eight at a time and compared) before `NinoStrings.Read` saves `SameLatin1`'s call (`Ascii.Equals`); 1,000
  objects with a short name read into existing ones took 6.9-7.5 us against main's 11.1-11.5 and rc.7's 9.9-11.4,
  `NinoDeserializePooled` 223-271 ns against main's 251-268 and rc.7's 224-273 (in-process timing, `benchmark`); on
  runners 1,000,000 shuffled items read into existing objects in 0.53-0.86 of main's time and 0.56-0.95 of rc.7's on
  every CPU (runs 41534216283, 41576130603). The
  same test inline in `ReadString(current)` pushed callers past the inlining budget (the `Lazy<string>` read left
  `Reader.Skip` a call, 7 stack accesses to 22) and read the pooled class in 277-295 ns; inlined into
  `RefillStringPairs`' loop it made that loop slower (a pooled class with only the dictionary 179-206 ns against
  139-162, `NinoDeserializePooled` 275-307); added before `RefillStringPairs` existed, the dictionary merge's general
  loop, which reads each value through `ReadStringOver`, read the pooled class in 284-293 ns against 256-265 without it
  (round 123).

- Store nothing a read into an existing object leaves unchanged: generated code stores a string member only when
  `ReadString(current)` returned another string (fields and auto-properties, `MemberModel.PlainStore`; a setter with a
  body is still called). Each store into an existing (often gen2) object is a write barrier call, and on Neoverse-N2 it
  was the last part of `NinoDeserializePooled`'s gap: 1.04 times rc.7 before, 0.926 times the head without it on that
  CPU (.NET A/B run 41616438646, 20 rounds, MemoryPack 1.000), 0.967 times rc.7 after (Xeon 8573C 0.871, 6975P 0.861;
  run 41621699299). The same run priced `ReadBool`'s 0-or-1 check there: 1.001, nothing to win by weakening it.

## Tests that must fold

- Write a platform test that must fold as `A || B || ...` at each site, each operand a branch-free property (a
  constant or one `static readonly` field), never as one property whose body has `||`: the inliner gives a property
  with several returns a temporary, so the branch folds only after struct promotion was decided. Evidence: with
  `NinoUnsafe`'s 32-bit gate as one property (`Is64Bit || X86Base.IsSupported || WideAccess.Unaligned`), x64 read a
  24-byte struct with three `vmovsd` pairs instead of `vmovdqu` + `mov` and a 16-byte one with six `mov`s instead of
  two `vmovups`; written `Is64Bit || IsX86 || Unsafe.SizeOf<T>() <= 4 || UnalignedWide32` at each site, the x64
  listing was identical to main. A `|` of a constant and a field does not fold either: R2R x86 kept the field load
  and its class-init helper call.
- A JIT-time check made of several helper calls is one `static readonly` field instead (`NinoElements.Traits`,
  `NinoBlockPlan<T, TFormatter>.Size`): one load, nothing to inline.
- Decide "copy memory" against "convert" by a `static readonly` computed before Tier1 (`NinoLayout<T>.IsCanonical`,
  `CopiesAsIs`, `HasPadding`, `Size`), so the common platform (64-bit CoreCLR) pays nothing and every other one adapts
  to it (the owner's rule: 32-bit adapts to 64-bit, never the reverse). Prove it with JitDisasm of a scratch program
  that calls the array, list and value paths in a loop until Tier1 (`DOTNET_JitDisasm='P:* Nino*:* NinoGen*:*'`): no
  `NinoLayout` field or call is left (`NinoLayout<T>.Size` compiles to `mov eax, 32`, `CopyToWire` to a tail jump
  into `NinoMask.Copy`), for `Vector<int>[]` and raw structs holding tuples alike (round of the Vector<T> layout).

## Locals, listing size and arithmetic

- Scope a captured local inside the branch that needs its closure. The netstandard2.0 `Lazy<T>` formatter
  (a792e149) previously allocated before the presence check. Moving the local into the present branch
  removed that null-path allocation on Arm64 and x64 listings. A late helper with a large generic local
  would move that value onto the stack, and changing the Lazy publication mode lost present throughput.
- Pass a present nullable's raw value to a readonly-ref layout writer through
  `Nullable.GetValueRefOrDefaultRef` on .NET 8 and later (546d8265). Taking a value copy first zeroed and copied
  a stack temporary in the local Arm64 listing. The 128-byte probe's frame went from 336 to 208 bytes.
- Past the JIT's tracking limit, locals are zeroed in the prolog: rare paths out of line also keep the local count
  down.
- A smaller listing is not a faster one: measure. Reading TimeZoneInfo's two strings through one out-of-line helper
  with two `out` strings cut the listing but put both on the stack: 7.5 to 12.8 % slower on runners. A constant
  parameter at each call site (`share: false`) let the inliner drop the dead branch per caller instead, with the
  listing as before and the string classes 17 to 21 % faster.
- Move a loop into Nino.Core from generated code as an `AggressiveInlining` helper with its own accumulator and a
  pointer that steps down a count, the shape it had, not as a second loop of the caller: the JIT then compiles the
  caller as before. Round 129 moved `PrefetchMembers`' run loop into `NinoPrefetch.Ahead`: written in `Ahead` with an
  index (`for (int i = 0; i < near; i++)`), it became `movsxd` plus a scaled load, and sharing `Ahead`'s `sum` added a
  dead `xor eax, eax` and 8 bytes of loop alignment (157/141 bytes instead of 144/128); as `Members<TFormatter>(ref
  object first, int count)`, the FullOpts listings were byte for byte main's (`DOTNET_TieredCompilation=0
  DOTNET_JitDisasm=Ahead` over PrefetchTests on both sides; `DOTNET_JitStdOutFile` appends, delete it between runs).
- Replace `%` by the runtime's fast modulo (multiply-high, `HashTables.Bucket`) on x64 only: on Arm64 `udiv`/`msub`
  is cheap and the multiply sequence lost. ConcurrentDictionary's build with fast modulo read 18-23 % faster on EPYC
  7763, on par on Zen 4 (9V45, 9V74), and 1.1 % slower on Neoverse-N2 in two rounds (3d4baf2).

## Memory layout across threads

- Keep a field that a hot path reads off the cache line of anything another thread writes by putting it in a non-generic
  struct with `LayoutKind.Explicit`, 64 bytes of padding on each side (`Size = 64 + fields + 64`), held as a field,
  never as an explicit-layout base class or padding fields of the class itself. The runtime orders a class's fields as
  it likes and packs a derived class's fields right after the base's last byte, and objects are only 8-byte aligned,
  while a struct moves as one block with its padding. `NinoPool<T>` had the base-class form: CoreCLR put the derived
  `_count`, which batch transfers write, at offset 124, on `Filled`'s line (64) whenever the object's fields started on
  a 64-byte boundary, one placement in eight (field offsets read with `ldflda`; locally, 4 threads, the 7 of 40 runs
  placed so took a median 1134 ms against the other 33 runs' 940 ms, though the struct form's own spread was as wide);
  `NinoPoolLine` and `NinoPoolThreadLine` are the struct form. Generic classes cannot take an explicit layout either;
  the struct field works there. Read and write such a field through a local (`count = s.Line.Count` ... `s.Line.Count =
  count + 1`), not with `s.Line.Count++` in an expression: that took the field's address and loaded it a second time in
  `TryTake` and `Add` (one `add` and one load more per call); with the local the listing is main's, one `add` fewer.

## Startup: code run once per type

- **A Nino.Core method that generated code calls once per type (a registration) builds what it needs itself and calls
  only methods that are not generic over the formatter**, rather than calling another per-formatter generic helper.
  Why: for a class `T` its code is shared over `T` (`__Canon`) but compiled per struct formatter, and stays Tier0 (it
  runs once): a call from it to another method generic over the formatter is a second method compiled per type and a
  generic-dictionary lookup at run time, where the registry's exact code resolved it while compiling. Evidence (round
  129, `NinoTypes`, 160 types in fresh processes, local, direction only): `NinoTypes.Register<T, F>` calling
  `NinoFormatterCache<T>.TryRegister<F>()` registered them in a median 46.7 ms against main's 43.6 ms (two calls in the
  registry); building `new StructFormatter<T, F>()` itself and calling the non-generic
  `NinoFormatterCache<T>.TryRegister(NinoFormatterBase<T>)` took 34.3 ms against 37.4 ms, 2,277 methods jitted against
  2,396. Count with `DOTNET_JitStdOutFile=f DOTNET_JitDisasmSummary=1` (one line per compiled method, with its code
  size); time `NinoPolymorphic.TryRegister(typeof(X))` over many types, which registers without compiling formatters.
