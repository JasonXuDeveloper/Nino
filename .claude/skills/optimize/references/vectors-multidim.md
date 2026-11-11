# Multidimensional arrays (.NET)

Part of the `optimize` skill ([vectors.md](vectors.md) holds the general loop rules and the length-proof reuse for
2D-4D fixed-stride reads): the heavy multidimensional read, the header writer and the header and shape checks, updated
in the generator template and the checked-in output together.

Platform scope: a bullet that names a GitHub runner run (or "runners") was measured on every runner CPU. The others
were timed on a local Apple M4 (Arm64, default JIT) or, where they say so, on an emulated x64 AVX2 guest; their x64
evidence is listings only (Crossgen2, cross-JIT), and the rules lists mark them "M4". Measure on native x64 before
carrying such a threshold or verdict there (`benchmark`).

Contents: [Rules](#rules), [Heavy reads](#heavy-reads), [Header and shape](#header-and-shape).

## Rules

- Read a new heavy multidimensional array straight into its storage when it fits the payload budget; keep the Reader
  and the validated shape by reference across the helper; span copy for reference elements; the first element through
  the array data reference on modern .NET (M4; offline x64 listings).
- Pass the generated rank to the header writer and to the reuse shape match (M4).
- Parse the header with one reservation for the trailing dimensions of 3D and 4D, an OR test of the sign and bit 30,
  products of present dimensions only, the unsigned product bound, one advance past a complete header, locals until
  valid, the fallback inside the shape reader and two scalar loads (M4; offline x64 listings).

## Heavy reads

- For a staged multidimensional array of reference-containing elements, use span copy to batch write barriers. Keep
  the unmanaged loop. Applying span copy to both cases repeatedly slowed the 5,000-item unmanaged fixture. The
  reference-only variant emitted `BulkMoveWithWriteBarrierInternal`, shrank its Arm64 FullOpts `Fill` from 204 to 184
  bytes, and grew offline x64 from 71 to 116. Shared IL grew from 34 to 65 bytes. The unmanaged machine-code sizes
  matched. Large reference timings improved in three of four follow-up rounds, but A/A varied too. Allocations and
  wire bytes matched, and 75 targeted tests passed on each of .NET 8 and 10 with intrinsics on and off (`c144df2a`).
- Skip staging for a new heavy multidimensional array when its full allocation fits the remaining payload budget. Read
  directly into its zeroed storage and publish it only after decoding succeeds. Keep the growing path for sparse
  payloads outside the budget. Moving both cases into a no-inline helper kept the main 2D reader at 135 IL bytes plus
  a 111-byte helper, from 182 IL bytes in the baseline and 238 in the inline candidate. For the reference fixture,
  Arm64 FullOpts was 656 to 504 + 432 bytes and offline x64 was 528 to 421 + 409. The checked-in generated C# grew
  from 6,457 to 7,531 bytes across all three ranks. At 5,000 elements the reference fixture allocated 5,266,744 to
  2,666,720 B per read. Three of four default-JIT Arm64 rounds beat the baseline and all four beat the identical-build
  control. The sparse fixture kept its allocation and its timing spread overlapped the A/A spread. Both fixtures
  matched wire bytes and round tripped at offsets 0, 1 and 7. The truncated heavy payload test verifies that the old
  array stays published.
- When the caller already holds a Reader by reference, keep it by reference across the multidimensional heavy helper
  boundary. This removed the helper call's copy and write-back. For the reference fixture, 2D caller plus helper went
  from 936 to 888 Arm64 FullOpts bytes and 830 to 736 offline x64 bytes. Arm64 Tier 0 fell from 1,040 to 976 bytes.
  The sparse fixture went from 980 to 932 and 1,552 to 1,446 respectively. Combined IL fell from 246 to 241 bytes and
  the three-rank source from 7,531 to 7,420 bytes. Allocations and wire bytes matched at offsets 0, 1 and 7, and four
  default-JIT rounds per case showed timing differences within the A/A spread. Precomputing the sparse initial
  capacity skipped a second budget calculation but added a branch and argument to the shared growing helper. Its Arm64
  helper pair grew from 1,108 to 1,152 bytes without a repeatable gain. Passing only the shape by reference saved 24
  Arm64 bytes across the caller and helper, compared with 48 for passing only the reader.
- Pass the validated shape by readonly reference as well. It already lives in a stack slot for the shape reader. Arm64
  FullOpts drops the 20-byte copy before the heavy helper and shrinks the 2D caller plus helper by another 24 bytes in
  both fixtures. Offline x64 drops 21 bytes for the reference fixture and 12 for the sparse fixture. Arm64 Tier 0
  combined size stayed at 976 bytes. Combined IL grew by 1 byte and three-rank generated source by 18 bytes. Four
  local default-JIT rounds showed faster 5,000-element reference reads in several rounds, but the magnitudes varied
  and the short cases overlapped A/A. Allocations matched, and wire comparisons and round trips passed at offsets 0, 1
  and 7.
- Take the first element of a freshly allocated, non-empty heavy multidimensional array through the array data
  reference on modern .NET. The allocation fixes the element type and lower bounds, so there is no multidimensional
  index to validate. Keep typed indexing on the older targets. The 2D helper shrank from 117 to 115 IL bytes, 396 to
  380 Arm64 FullOpts bytes and 376 to 347 offline x64 bytes for the reference fixture. The three-rank generated source
  grew by 550 bytes. Four local rounds overlapped A/A with identical allocations and wire bytes at offsets 0, 1 and 7.
  The write path's two length uses were already one load in Arm64 Tier 1.

## Header and shape

- Pass the generated rank to the shared multidimensional header writer. Reserve the header once and write each length
  at its fixed offset. The old Arm64 listing called `Array.GetLength` from the runtime-rank loop. Four local
  default-JIT rounds on one-element integer arrays improved for ranks two through four, with zero allocations. The
  64-element and near-64-KiB input-plus-output cases overlapped A/A after a longer warmup. All 45 wire cases matched
  at offsets 0, 1 and 7. The Arm64 2D Tier 1 write entry shrank from 1,356 to 1,164 bytes. In offline x64 the 2D
  formatter grew from 1,295 to 1,384 bytes while inlining away its call to the 199-byte shared helper.
- Pass the generated rank into the multidimensional shape match on reuse. The baseline loaded the runtime rank in its
  shared helper. The Arm64 Tier 1 2D formatter shrank from 832 to 696 bytes and its public read entry from 816 to 680
  bytes. The offline x64 2D formatter went from 676 to 682 bytes while inlining away the call to a 190-byte helper.
  The shared helper shrank from 85 to 78 IL bytes, and each generated reader gained one IL byte. Four local rounds
  from empty to near 64 KiB of input plus output overlapped A/A with zero allocations. All 45 wire and same-shape
  reuse checks matched at offsets 0, 1 and 7. The last-dimension mismatch replaced the array at each rank.
- Read the trailing dimensions of 3D and 4D headers with one checked reservation after the existing first-length
  check. A null header still consumes only one length. Keep the single checked read for 2D. On the local 4D
  one-element fixture, four default-JIT rounds improved by 9–13% with zero allocation. A longer warmup put 2D 32–128
  element results near the A/A spread. The split helper grew from 316 to 348 IL bytes but shrank from 632 to 620 Arm64
  FullOpts bytes and 579 to 552 offline x64 bytes. Grouping all ranks made the listing smaller, but the first 2D
  timing series favored the split for short inputs. Wire comparisons matched at offsets 0, 1 and 7. Every cut in the
  header threw before replacing the target, and the four-byte null sentinel decoded at every rank.
- Test the sign and bit 30 of the OR of multidimensional lengths before the exact per-dimension bounds check. Arm64
  uses an immediate `tst` on the common path. Keep the exact checks and error messages in a cold helper for negative
  or large dimensions. Comparing the OR directly to the runtime maximum slowed the one-item 2D fixture by about 7% in
  four rounds. The bit-test variant instead reduced time from 3.48–3.49 to 3.22–3.25 ns with no allocation change. The
  hot method went from 348 to 286 IL bytes, 620 to 572 Arm64 FullOpts bytes and 552 to 499 offline x64 bytes. The cold
  helper adds 65 IL, 132 Arm64 and 63 x64 bytes. Wire bytes matched at offsets 0, 1 and 7, and a valid empty array
  with a dimension near the runtime limit passed the exact check.
- Multiply only the dimensions present in the wire header. Check each real prefix before the next multiplication, even
  for trailing-zero shapes. The former shared helper multiplied by the implicit dimensions of length one and checked
  each product. Four default-JIT rounds of 3D single-element reuse went from 3.97 to 3.73 ns with zero allocations.
  The 2D single-element case held at 3.25 ns and larger cases overlapped A/A. The helper grew from 286 to 313 IL
  bytes, 572 to 600 Arm64 FullOpts bytes, and 499 to 535 offline x64 bytes. All 45 wire and reuse probes passed at
  offsets 0, 1 and 7.
- Keep the separate unsigned product bound before multiplying by the minimum wire size. Combining the high word of the
  total with the low-word byte product removed one comparison, but four default-JIT A/B rounds showed no repeatable
  benefit. IL grew from 313 to 322 bytes and offline x64 from 535 to 542, while Arm64 FullOpts fell from 600 to 592.
  Bounding the count by remaining bytes first instead of `uint.MaxValue` used 318 IL bytes, 600 Arm64 bytes and 532
  offline x64 bytes. That form repeatedly slowed the single-item 2D read from around 3.25 to 3.72 ns. Both candidates
  matched the baseline's 45 wire and reuse probes at offsets 0, 1 and 7. Keep the alternatives as separate experiments
  for subsequent JIT diagnosis. Dividing the remaining byte count by the minimum wire size also passed all 45 probes.
  It used 306 IL, 596 Arm64 FullOpts and 530 offline x64 bytes, saving 7, 4 and 5 bytes respectively. The dynamic
  divisor still emitted `udiv` on Arm64 and `div` on x64. Four Arm64 rounds were flat for 2D and 3D single-item reuse
  and about 3% faster for 4D. Larger cases varied and x64 throughput was not measured. Retain the multiply-and-check
  baseline while the quotient variant remains an experiment.
- Advance past a complete multidimensional header once when the first length is nonnegative and collection lengths are
  uncapped. Use the original first-length check and tail read for truncated, null and capped inputs. Four local
  default-JIT rounds of 4D one-element reuse went from 5.89–5.95 to 5.63 ns and 2D from 3.23–3.25 to 3.18 ns.
  Allocation stayed at zero. Larger results varied. The helper grew from 313 to 366 IL bytes, 600 to 612 Arm64
  FullOpts bytes and 535 to 549 offline x64 bytes. All 45 wire and reuse probes and 18 header probes passed at offsets
  0, 1 and 7.
- Keep dimensions in locals until multidimensional shape validation succeeds, then assign the out fields once. Arm64
  FullOpts no longer cleared and reloaded the output during parsing. An object initializer used 334 IL bytes and
  direct field writes used 313, down from 366. Both emitted 592 Arm64 FullOpts bytes and 550 offline x64 bytes, from
  612 and 549. The direct variant reduced Arm64 Tier 1 from 640 to 616 bytes with the public entry unchanged.
  Default-JIT timings overlapped A/A and allocations matched. The 45 wire and 18 header probes passed at offsets 0, 1
  and 7.
- Keep the multidimensional header fallback inside the shape reader while its caller keeps the Reader in registers.
  Moving the tail or just the first length to a no-inline byref helper shrank the Arm64 Tier 1 shape reader from 616
  to 504 or 568 bytes, but grew the read entry from 676 to 756 or 804 bytes and introduced stack loads and stores of
  the cursor on the fast path. The 2D single-item probe slowed from 3.18 to at least 4.01 ns. Copying the Reader into
  the cold helper improved on that result but still took 3.63–3.67 ns. All three forms kept the 45 wire and reuse
  probes identical at offsets 0, 1 and 7. Keep their patches for JIT diagnosis. Passing only the decoded first length
  to the cold helper avoided the cursor spill and restored the 2D single-item read to 3.18 ns. Four default-JIT rounds
  showed no repeatable latency gain. Arm64 Tier 1 shrank the shape reader and its caller by 16 bytes each, while the
  cold helper added 68 Arm64 FullOpts bytes. Offline x64 grew from 550 to 518 + 66 bytes, and IL from 313 to 317 + 26.
  All 45 wire and 18 header probes matched. Keep the shared Reader length check to avoid duplicating its rules for a
  size tradeoff without a speed gain.
- Keep the two scalar loads for the first two multidimensional lengths. Reading them with one unaligned 64-bit load
  requires splitting the words and preserving native word order on big-endian targets. Four local Arm64 rounds had
  unchanged 2D short reads, a 1–2% difference on 3D, and variable 64-item results at zero allocations. The portable
  candidate grew from 313 to 345 IL bytes, 592 to 612 Arm64 FullOpts bytes and 550 to 569 offline x64 bytes. The 45
  wire and reuse probes passed at offsets 0, 1 and 7. Keep the packed-load patch as an experiment.
