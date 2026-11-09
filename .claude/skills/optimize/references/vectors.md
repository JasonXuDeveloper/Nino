# Bounds checks and vector loops (.NET)

Part of the `optimize` skill: removing bounds checks, and the vector loops on x64 (AVX-512, AVX2, SSSE3), Arm64
(tbl, ldp/stp) and the netstandard builds, with the per-CPU decisions (Ice Lake, Zen, Neoverse) and the kernels
measured and not taken. Read it before writing or changing a loop over elements, a block copy, a permute or a check
of bytes in a block.

## Bounds checks

- Index an array or span on a hot path through a ref to its first element and `Unsafe.Add`, never `a[i]` or
  `span[i]`: take `ref MemoryMarshal.GetReference(span)` or `ref NinoUnsafe.GetArrayDataReference(array)` once (the
  wrapper covers netstandard2.0, which lacks `MemoryMarshal.GetArrayDataReference`), check the length once against
  what the loop will touch, then walk with `Unsafe.Add(ref first, i)` or a bumped cursor. Why: the JIT removes a
  check only when it can prove `i < Length` (a `for` over `0..Length` of the same local); anything else (two arrays,
  an index from the data, a count read earlier, `i + 1`) leaves a compare and a `CORINFO_HELP_RNGCHKFAIL` call per
  access, and the throw block also costs the method its inlining budget. On IL2CPP the gain is larger: it emits its
  own check on every array access unless the project turns it off, and `Unsafe.Add` has none.
- The one check you keep is what makes it safe: on a read path it is the length check against the bytes left
  (`audit-security`), and every `Unsafe.Add` must stay inside what that check covered. Never `ref array[0]` (throws
  on an empty array, and is a bounds check itself); `GetReference` of an empty span is a valid ref that must not be
  read. `ref span.GetPinnableReference()` is the same as `GetReference` but returns a null ref for an empty span:
  use it only with `fixed`.
- Find the checks left: `CORINFO_HELP_RNGCHKFAIL` in the Tier1 listing (`benchmarks/asm.py dump --asm FILE`),
  counted per method; a hot method with one in its loop is a finding, one on a cold path out of line is not.
- Validate a pooled reference array's exact element type with a writable span before obtaining a byref for
  stores. The shared pool checks the returned array's length, and its API expects clients to return their own
  rentals. In the dictionary refill probe, a checked span or its first ref removed the array-store helper and
  helped 17 string-key pairs, but the larger cases stayed on par and the formatter's machine code grew.
- Go, Rust and TypeScript: `optimize-runtimes`, references/blocks.md.

## Vector loops

- Validate four priority-queue children against their shared parent in one signed vector compare.
  Reuse the odd-lane unzip for interleaved raw pairs and keep the scalar tail for incomplete groups.
  The local 8,193-pair heap adoption probe cut read time by 54–58%, with unchanged bytes and allocation.
  Store each validated vector group directly into the empty backing array, publishing its size only after
  every group and the scalar tail pass. The enqueue fallback overwrites unpublished raw slots. Keep a
  single bulk copy without SIMD; scalar per-pair stores regressed. Fusing the vector stores saved another
  24–25% on the local 8,193-pair probe.
  Advance the source, destination and parent priority cursors by their strides across vector groups.
  This removed indexed address arithmetic and saved a further 22–24% in the same local large-heap probe.
- Keep the dual-cursor scalar block-read tail as a candidate while diagnosing JIT code generation.
  On the local `BlockWaypoint` probe, two bumped cursors removed the two loop multiplications but grew
  `ReadEach` by 33 IL bytes and did not consistently improve the direct or public read paths.
  Moving the same two-cursor loop to a no-inline tail helper improved 1,025-element reads with SIMD
  enabled by 6–11% across four local rounds, but the one-element cases paid for the new call.
  Keep the long-run candidate and measure a short-tail route separately.
  A single-element early return inside `ReadEach` sped up that isolated case, but erased the long-run gain.
  Moving it to `ReadUnchecked` grew the inlined rejecting caller from 176 to 304 Arm64 Tier 1 bytes and
  showed no consistent gain at the public entry. Check the generated interceptor path before tuning further.
  The same probe rebuilt with the generated `NinoDirect.Deserialize` interceptor also showed no repeatable
  1,025-element gain for either helper placement. Warm the generated caller before measuring so that the
  Tier 1 listings are available. In the Arm64 replay, both cursors stayed in registers with no hot-loop
  calls or spills, and the public caller stayed at 644 bytes. Splitting the loop changed 240 bytes of
  `ReadEach` into 152 bytes plus a 136-byte tail, with two stride multiplications at the tail boundary.
  The caller-side single-item dispatch enlarged `ReadRejecting` from 176 to 304 bytes even on the long case.
  All variants allocated 0 B per read and timings varied across rounds. Compare short-tail call placement
  while keeping the cursor loop fixed. Dispatching the actual remainder after SIMD directly to `ReadOne`
  when it is one increased `ReadEach` from 459 to 481 IL bytes and from 240 to 272 Arm64 Tier 1 bytes.
  The x64 FullOpts cross listing grew from 240 to 260 bytes. With 64 items and a one-byte unaligned
  start, the direct tail was consistently slower with hardware intrinsics enabled and within noise
  with them disabled. The 1,025-item result was within noise with either setting. The probe verified
  equal wire bytes and 0 B per read. Keep the dual-cursor helper available and isolate the SIMD-side
  dispatch from the pure scalar path before testing another tail shape. A gate on
  `Vector128.IsHardwareAccelerated` folded away on the SIMD path and kept its Arm64 Tier 1 instructions
  identical, although the 64-item timing difference persisted. With hardware intrinsics disabled, cursor
  stepping removed the loop multiplications and reduced Tier 1 from 172 to 148 Arm64 bytes and the x64
  FullOpts cross listing from 140 to 113 bytes. Reads of 1,025 items and the 1,640/1,700-item pair
  straddling a 64 KiB source-plus-destination budget were within 0.5% across four rounds. The two sides
  emitted identical wire bytes and allocations matched. Keep this as evidence of smaller scalar machine
  code. The extra branch and duplicate loop raised `ReadEach` from 459 to 503 IL bytes. A byte-identical
  A/A control reproduced a timing gap across load contexts. With a process per build and offsets 0, 1,
  and 7, the 64-item SIMD result showed no repeatable regression across four rounds per offset. Use the
  separate-process protocol from `benchmark` for this case.
  Stepping only the source byref in the shared tail removed its 15-byte stride multiply on Arm64
  Tier 1 while leaving the 24-byte destination multiply. This kept the SIMD dispatch and avoided
  duplicating the tail. `ReadEach` grew from 459 to 470 IL bytes. Arm64 Tier 1 went from 240 to 244
  bytes with SIMD and from 172 to 160 without it. The x64 FullOpts cross listing went from 186 to
  192 bytes with SIMD. At a one-byte offset, four default-tiering process-per-build A/B rounds
  showed no repeatable gain with SIMD, and scalar reads of 1,025, 1,640 and 1,700 items differed by
  at most 0.22%. All timed reads allocated 0 B. The full-value zero stores before the field writes
  remained in the loop. Check those stores independently of cursor stepping in the next variant,
  preserving zero padding and bool validation.
- Count zeroing stores as well as bytes before specializing a padded scalar read. The `BlockWaypoint`
  15-byte wire / 24-byte memory probe used the validated map to narrow its two zero stores from 24
  bytes to 10, including the bool byte overwritten by the formatter. `ReadOne` grew from 37 to 77 IL
  bytes and `ReadEach` stayed at 459. Arm64 Tier 1 stayed at 240 bytes with SIMD and 172 without it,
  and the x64 FullOpts SIMD cross listing went from 186 to 185. Four separate-process A/B rounds
  at offset 1 allocated 0 B per read. The 1,700-item scalar case stayed within 0.11% and with SIMD
  it improved by 0.48–3.14%, within the local noise for this case. Sizes 1 and 65 varied in both
  directions. The probe compared wire bytes at offsets 0, 1 and 7, checked zero padding on reused
  arrays prefilled with 0xCD, and rejected truncated input and a final bool of 2, with SIMD on and
  off. Keep the layout-specific variant as a probe. Reducing the number of stores would require
  combining padding zeroing with a field store while preserving field validation.
  A little-endian, exact-map `BlockWaypoint` probe fused the padding into the short and bool stores
  and paired the floats in one 8-byte move. The JIT emitted four stores instead of seven.
  With intrinsics off, 65 items read 37.4–37.9% faster and 1,700 items 33.0–34.8% faster across
  four process-per-build rounds at offset 1. With SIMD on, small cases varied and 1,700 items
  were slower in three rounds. Allocation stayed at 0 B. `ReadOne` grew from 37 to 134 IL
  bytes while `ReadEach` Arm64 Tier 1 shrank from 172 to 148 bytes without SIMD and 240 to 220
  with it. The x64 FullOpts cross listing shrank from 140 to 107 and 186 to 153 bytes respectively.
  The same byte, padding, truncation and invalid-bool checks passed. Keep the probe for the scalar
  path. A general implementation needs an explicit generated-formatter guarantee before replacing
  its field reads. An exact byte map alone does not preserve a custom formatter's validation.
  Gating the same probe on `!Vector128.IsHardwareAccelerated` restored the original SIMD Arm64
  Tier 1 instruction stream after normalizing call addresses. The x64 FullOpts cross listing
  returned to 186 bytes with SIMD and stayed at 107 without it. `ReadOne` reached 141 IL bytes.
  Four default-tiering A/B rounds at offset 1 retained 37.5–39.7% faster scalar reads at 65 items
  and 34.3–35.0% at 1,700, with 0 B allocated. One item varied, including a 24% scalar regression
  in one round. SIMD timings varied in both directions. Repeat the byte, dirty-padding, truncation
  and invalid-bool checks on both paths before trying a general implementation.
- Skip the bool prepass when the read plan can only call `ReadBlock`, which already validates each
  bool. Keep it for whole-block copies and vector paths that do not validate. Keep the decision in
  a read-only nested plan so writes do not initialize the read masks. At offset 1, four local
  `BlockWaypoint` A/B rounds with intrinsics disabled reduced time by 32–37% for one item, 7–13%
  for 65 and about 7.3% for 1,700. SIMD timings varied and allocation stayed at 0 B. The Arm64
  scalar `ReadRejecting` Tier 1 shrank from 176 to 116 bytes, while the x64 FullOpts cross listing
  grew from 385 to 443 bytes with the static field load. `ReadUnchecked` grew from 54 to 61 IL
  bytes. The wire bytes and rejection checks matched, and the targeted .NET 8/10 tests passed
  with hardware intrinsics both on and off (`2916df8b`).
  Combining the bool presence and copy flags in one static field kept the x64 FullOpts caller at
  443 bytes. Reusing the active group thresholds instead removed the extra class-init guard and
  reduced the x64 caller to 360 bytes. Arm64 Tier 1 stayed at 116 bytes without SIMD and grew
  from 176 to 184 with SIMD. The caller/helper IL changed from 61/49 to 54/64 bytes. At offset 1
  across four local rounds, scalar reads of 65 and 1,700 items stayed within 0.25%, with 0 B
  allocated. The SIMD one-item case improved in three rounds but slowed in one. The 1,700-item
  SIMD case varied from 3.4% faster to 13.2% slower. Wire bytes, dirty padding and invalid data
  checks matched. In six further rounds, an identical-binary A/A control for 1,700 items ranged
  from 3.7% faster to 5.5% slower, while A/B ranged from 4.9% faster to 9.8% slower. The single-item
  A/B improved in all six rounds, by 23.7–36.0%. The Arm64 caller gained a compare and a branch
  that skip the prepass below eight items for this plan. Keep the short-input improvement and
  report the long SIMD case separately. The .NET 8 and 10 targeted suites passed with intrinsics
  on and off (`9d51e10a`).
- Reuse the validated local bool byte in raw block reads. After the check rejects values above
  one, its representation is already canonical. Arm64 removed the compare against zero and
  `cset`: scalar `ReadEach` Tier 1 shrank from 172 to 164 bytes. The x64 FullOpts cross listing
  went from 140 to 135 without SIMD and 186 to 180 with it. `ReadBool` stayed at 22 IL bytes.
  Four Arm64 A/B rounds with SIMD enabled and offset 1 were 1.6–3.0% faster for one item and
  1.6–4.6% faster for 65. The 1,700-item and tiny scalar results varied. Scalar results at 65
  and 1,700 stayed within 0.3%. Allocation stayed at 0 B. Wire, dirty-padding, truncation and
  invalid-bool probes and targeted .NET 8/10 tests passed (`5518250c`).
  Share the canonical validation with `Reader.ReadBool` after its bounds-checked byte load.
  With individual `BlockWaypoint` reads into a reused array, the generated body went from 244
  to 236 Arm64 Tier 1 bytes and from 162 to 156 x64 FullOpts cross-JIT bytes. The reader entry's
  IL went from 22 to 12 bytes before inlining the shared validator. Four alternating rounds and
  an identical-binary A/A control showed variable timing at offset 1, with 0 B per read. The
  1,700-item case spans about 66 KB of input and output. Wire and rejection checks matched at
  offsets 0, 1 and 7, and .NET 8 and 10 targeted tests passed (`95caf340`).
- Measure one-item block writes through the public serializer before choosing the branch placement.
  In the `BlockWaypoint[]` probe, bypassing `WriteEach` from `WriteAll` improved the single-item
  median in all eight default-tiering rounds, by 12–40%. Larger inputs varied, including large
  differences in the identical-binary A/A control. Wire bytes matched at offsets 0, 1 and 7 for
  zero, one, two, 65 and 1,700 items, and timed writes allocated 0 B. `WriteAll` grew from 32 to
  44 IL bytes and the Arm64 Tier 1 caller from 484 to 548. The x64 cross listing showed the same
  caller and larger-input loop sizes, but the array formatter reached only MinOpts there.
  Moving the condition inside `WriteEach` kept the Arm64 caller at 484 bytes, enlarged its
  65-item loop from 196 to 260 bytes, and lost the repeatable single-item gain. A no-inline
  single-item helper limited the Arm64 caller to 520 bytes, but had variable timings. Keep
  the inline outer candidate for comparison. Four mixed-profile rounds with five one-item writes
  per eight calls improved by 2.6–10.6%. The profile containing one 1,700-item write per eight
  varied from flat to 33% slower. A byte-identical A/A control was 4–10% apart on that profile.
  The large-weighted mix varied in both directions. Allocations stayed at 0 B and the wire
  comparison passed. Use `crossgen2 -O` to get an optimized offline x64 listing. With this
  specialization, `ArrayFormatter.Serialize` grew from 2,117 to 3,232 bytes and `WriteAll`
  from 356 to 1,241 bytes. The added branch inlined layout initialization, padding clearing,
  and layout conversion paths that were absent from the baseline caller. Keep the prototype
  separate until the scalar store can be inlined without pulling those paths into the caller.
  A primitive-only layout bypass cut the candidate's offline x64 `WriteAll` from 1,241 to 556
  bytes and `ArrayFormatter.Serialize` from 3,232 to 2,459. Explicit type comparisons grew
  `NinoLayout<T>.Write` from 30 to 383 IL bytes. `Type.IsPrimitive` with both native pointer types
  excluded produced the same code sizes with 109 IL bytes. Both candidates kept the Arm64
  mixed-profile caller and loop at 548 and 196 Tier 1 bytes, and matched wire output and
  0 B allocations. The explicit comparisons kept the small mix gain and had mixed large-run
  results. The property version was 7–17% faster in the mixed profile but 9–12% slower across
  all four large-profile rounds. Keep both as probes and check code layout under the
  large-run profile before adding a broad primitive branch to the shared layout writer.
  Marking only `WriteConverted` no-inline removed its two conversion bodies from offline x64
  `WriteAll`, reducing that listing from 1,241 to 1,159 bytes, but the inlined array formatter
  grew from 3,232 to 3,431. Arm64 Tier 1 still emitted the 548-byte caller and 196-byte loop.
  The small-weighted profile remained faster in all four rounds versus the production baseline,
  with variable mixed and large profiles. Wire bytes matched at offsets 0, 1 and 7, and
  allocations stayed at 0 B. Isolate the padding paths next if revisiting this shape, since
  they still expand inside the x64 caller after removing the conversion bodies.
  Moving only padding clearing behind a no-inline call reduced the outer candidate's offline
  x64 `WriteAll` from 1,241 to 720 bytes and `ArrayFormatter.Serialize` from 3,232 to 2,807.
  `NinoLayout<T>.Write` grew from 30 to 36 IL bytes plus an 8-byte helper. The non-padded
  `BlockWaypoint` Arm64 caller and loop stayed at 548 and 196 bytes. A tuple containing
  `PaddedPlain` from the padding tests used the helper once per element, enlarging Arm64
  `WriteEach` from 72 to 112 bytes plus the 28-byte helper. Against the same outer branch
  with inline padding, the padded large-weighted workload slowed by 43–48% in every round.
  Both probes wrote identical bytes at offsets 0, 1 and 7 and allocated 0 B. Keep per-value
  padding stores inline on the ordinary tuple path. Any further isolation must be local to
  the one-item specialization.
  For a 15-byte block with an identity-mapped 14-byte prefix, the one-item branch can use
  two overlapping 8-byte stores and one mapped final byte. The validated map keeps the
  source and destination accesses within the value and the block. Restrict the probe to
  64-bit runtimes. It kept the Arm64 multi-item loop at 196 bytes and reduced the outer
  candidate's caller from 548 to 520. Offline x64 `WriteAll` went from 1,241 to 406 bytes
  and `ArrayFormatter.Serialize` from 3,232 to 2,217. `WriteAll` grew from 32 to 114 IL
  bytes versus production. The small mix improved in three of four rounds, with variable
  larger mixes, byte-identical output at offsets 0, 1 and 7, and 0 B allocations. Keep the
  shape-specific probe for comparison with a general contiguous-prefix plan.
  Generalize this to an 8- to 16-byte prefix and one mapped final byte. The 13- and 15-byte
  qualifying shapes improved 8.9–13.1% and 4.5–11.2% in four small-weighted Arm64 rounds.
  The 11-byte nonqualifying control kept its caller and loop sizes and had no consistent
  slowdown. Larger profiles varied. The general `WriteAll` is 121 IL bytes. Arm64 stayed
  at 520 bytes for the mixed-profile caller and 196 for the 15-byte loop, while optimized
  offline x64 used 440 bytes in `WriteAll` and 2,257 in `ArrayFormatter.Serialize`. All
  probes allocated 0 B and matched wire bytes at offsets 0, 1 and 7. Targeted .NET 8/10
  tests passed with and without hardware intrinsics (`d2688fac`).
  Keep the two explicit word stores for the variable prefix length. Replacing them with
  `Unsafe.CopyBlockUnaligned` reduced `WriteAll` from 121 to 89 IL bytes, with the same Arm64
  Tier 1 sizes for both shapes. Offline x64 shrank from 440 to 434 bytes in `WriteAll`, and
  from 2,257 to 2,245 in the array formatter, but introduced a `CORINFO_HELP_MEMCPY` call in
  the single-item path. Four local rounds for each shape had no stable timing gain, 0 B
  allocations, and byte-identical writes at offsets 0, 1 and 7.
  Express those stores through the existing `NinoUnsafe` unaligned helpers. `WriteAll`
  shrank from 121 to 112 IL bytes without changing the measured Arm64 Tier 1 or offline
  x64 machine-code sizes or introducing a copy helper call. Four rounds for each shape
  had variable timings and 0 B allocation. Wire bytes matched at offsets 0, 1 and 7, and
  targeted .NET 8/10 tests passed with hardware intrinsics on and off (`fe927629`).
- Recheck fixed-stride array reads before bypassing the second bounds check. The length check
  already proves the block fits before allocation, so an array-only probe advanced the reader
  without repeating the comparison. The span-read IL grew from 252 to 255 bytes plus a helper.
  Arm64 Tier 1 caller code fell from 644 to 620 bytes, and offline optimized x64 from 1,071 to
  1,017. At offset 1, the 65-item SIMD case was 1.8–5.9% slower in four follow-up rounds,
  while the byte-identical A/A control ranged from 2.8% faster to 9.8% slower. With intrinsics
  disabled it stayed within 1%. The one-item and 1,700-item results varied. Wire, dirty-padding,
  truncation and invalid-bool checks passed at offsets 0, 1 and 7, with 0 B allocated per read.
  Pin the probe arrays and select actual input and output alignments before comparing SIMD
  timing. `Lanes` matched after normalizing addresses at both output alignments. At output
  alignment zero and input offset one, the four rounds improved 4.2–9.7%; the other
  offsets and output alignment eight were mixed. The original slowdown did not reproduce.
  Retain the array-only path. All Core targets built and 106/107 targeted tests passed
  on .NET 8/10 with and without hardware intrinsics (`b9f1a4be`).
  A list opened at the exact validated count can use the same path. The reused-list Arm64
  hot branch already folded the duplicate comparison and the Tier 1 caller stayed at
  1,216 bytes. Offline x64 shrank from 1,446 to 1,392, with unchanged formatter IL.
  Pinned-alignment A/B and byte-identical A/A both varied across four rounds, with 0 B
  allocated. Wire output matched at offsets 0, 1 and 7, and 111/112 targeted tests passed
  on .NET 8/10 with and without hardware intrinsics (`d3a36d9a`).
  Apply the same rule to `Memory<T>` after the exact-length check and the growing fallback.
  In the aligned reused-memory probe the Arm64 Tier 1 entry went from 828 to 812 bytes
  and the formatter from 768 to 756. Offline x64 formatter code went from 1,015 to 975,
  and formatter IL stayed at 83 bytes. Four A/B and A/A rounds varied, with 0 B allocated
  and byte-identical writes at offsets 0, 1 and 7. The 98/99 targeted tests passed on
  .NET 8/10 with hardware intrinsics on and off (`c82748e0`).
  For queue and stack backing-array reads, reuse both the capacity proof and the fixed-stride
  length validation. Removing only the payload comparison repeatedly slowed 65 items in
  the first aligned probe. Creating the span from its validated ref and length brought
  both 65-item timings back within the variable A/A range. Arm64 Tier 1 callers shrank
  by 44 bytes each and offline x64 `ReadInto` went from 780 to 686 bytes. IL went from
  127 to 130 bytes. Wire bytes matched at offsets 0, 1 and 7, allocations were 0 B,
  and 111/112 targeted tests passed on .NET 8/10 with intrinsics on and off (`ef43854c`).
- Keep empty fixed-stride writes on the shared counted reservation. The Arm64 caller already
  combines the element-byte multiplication and length addition in one `umaddl`. Emitting
  `WriteLength(0)` before the reservation grew `WriteSpan` from 63 to 69 IL bytes, the Arm64
  Tier 1 caller from 524 to 608 bytes, and the offline x64 array formatter from 2,257 to
  2,345. Four empty-weighted A/B rounds varied from 16.7% faster to 0.9% slower. The other
  profiles varied in both directions. All wire outputs matched at offsets 0, 1 and 7 and
  all timed writes allocated 0 B. Avoid duplicating the reservation path for an empty array.
- Zip raw `SortedList<int, int>` key and value vectors straight into the counted reservation. Use Arm64
  `zip1/zip2` or SSE2 unpack on x64 and x86, and keep the scalar tail. The one-source permute kernel cannot
  take the two arrays directly. The local Arm64 2,047- and 8,193-pair probes cut elapsed time by 56–70%.
  Keep the dual-source loop in `NinoBlockCollections.InterleaveInt32` and call it only for at least one vector.
  Extracting it cut seven-pair write time by about 30% locally while the larger cases remained broadly on par.
  On netstandard, register the same four-byte lane operation through `NinoKernels.Interleave32`. Gate on raw
  layout and no padding, call once for full vectors, and leave the remaining pairs to the existing scalar loop.
  Check unaligned output and the untouched bytes beyond the vector prefix in the Burst kernel tests.
  For a generic 16/32/64-bit interleave, fold the lane-width gate before calling a generic helper and
  precompute the pair stride: `i * size * 2` became two shifts for 32-bit lanes. Skipping Tier 0 in the
  generic helper recovered short 32-bit throughput locally, but the 2,047-pair control slowed by 4–9%.
  Keep the candidate isolated while tuning dispatch; the 16- and 64-bit large cases improved consistently.
  Resolve zip operations with static lane policies so the shared loop carries no width switch. This reduced
  its IL from 440 to 193 bytes and kept the 32-bit Arm64 and Windows x64 FullOpts sizes at 128 and 109 bytes.
  The local seven-pair 32-bit case still took 3–6% longer; 8,193 short pairs and 2,049 long pairs improved
  by roughly 79–81% and 29–34%, respectively.
- Split a checked, ordered raw `SortedList<int, int>` input directly into its two arrays on .NET 8+.
  Keep the order check before publishing the arrays and use the same wire block for out-of-order insertion.
  Put the layout and comparer checks in an out-of-line helper so short reads keep the original rented-pair
  method's IL. The local Arm64 2,047- and 8,193-pair reuse probes cut elapsed time by roughly 35–45%.
- Compare long Latin-1 input to an existing string by widening bytes and comparing UTF-16 lanes. Start with the last
  complete vector to reject tail edits early. Keep this out of the short-string comparison, and place boundary tests
  at the end of a guarded page to catch a load past either end of the input.
- Continue Latin-1 narrowing at the prefix written by `Ascii.FromUtf16`, overlapping the final vector if needed.
  When the first unwritten character exceeds Latin-1, take the UTF-16 path immediately.
- Use the counted byte length to place long UTF-8 payloads at their final offset. When strict transcoding stops at an
  unpaired surrogate, continue the replacement encoder at the consumed char and written byte offsets, which differ
  after a non-ASCII prefix. Keep the replacement work out of line.
- Run the 128-bit Latin-1 widening loop over full vectors, then write the final vector once.
  This removes the per-vector tail choice while preserving the overlapping tail and the AVX2
  path. The Arm64 builder probe improved at 256 and 16,384 characters in most rounds. The 31
  character recheck was flat within the A/A variation. A separate short-vector branch regressed
  the 16,384-character control, so keep the shared loop. Wire bytes and allocations matched.
  For x64 listings of this vector method, compile Nino.Core and System.Private.CoreLib in the
  same Crossgen2 composite version bubble. A single-assembly compile skips the method because
  its vector call requires a compatible ABI. The retained loop reduced the offline listing
  from 648 to 539 bytes with the baseline ISA and from 609 to 493 with AVX2. Replacing the
  tuple widening call with explicit lower and upper operations removed seven helper IL bytes
  but left the Arm64 Tier 1 and both x64 listing sizes unchanged. It slowed the 256-character
  local probe in all four rounds, so keep the tuple form.
- Apply the same full-vector and overlapping-tail loop to 128-bit Latin-1 narrowing, checking each
  vector before writing it so UTF-16 fallback still works. The unaligned Arm64 writer probe improved
  at 64, 4,096 and 32,768 Latin-1 characters in all four rounds, with identical wire bytes and
  zero allocations. The x64 composite listing left the vector helper out of line, so include its
  body when comparing offline size and do not infer x64 throughput.
  A direct unsigned vector comparison emitted `cmhi` on Arm64 and slowed the 64-character
  probe. An Arm64-only maximum reduction emitted `umaxv` and shrank the hot writer from 836
  to 824 bytes, but the ASCII control was slower in all four reversed-order rounds. Its x64
  composite helper also grew from 280 to 415 bytes. The variants matched the wire bytes and
  allocated nothing after warmup. Keep the mask-based range check.
- Copy large UTF-16 `StringBuilder` values directly into the reserved span with `CopyTo` (1b2e3404).
  `GetChunks` can allocate a chunk index, even for a short but unusually fragmented builder. Keep the modern
  small-write loop where it measured faster, and advance the reader by the exact byte count already checked.
- Refill a matching-length `BitArray` through its public byte span after validating the payload (cb6e950e).
  Setting its length to the same value clears the unused bits and invalidates existing enumerators. A single
  copy body for fresh and reused instances avoided duplicating the Arm64 copy code.
- Skip the copy when a growing byte writer is empty, otherwise copy its bounded written span
  (`NinoArrayBufferWriter.Grow`). The local Arm64 probe lowered growth latency with the same allocations,
  at the cost of a larger native helper.
- Copy byte writer results up to 64 bytes with the existing inline copy on 64-bit targets (88315b4c).
  Keep larger copies on `CopyToNew`; an extra out-of-line allocation-and-copy helper lost on the larger
  local cases. The short-string caller listing stayed identical while the copy helper grew.

- Bump `ref` cursors and count down (`at = ref Unsafe.Add(ref at, step)`, `while (--n != 0)`), never
  `(nuint)i * size` per iteration; take everything that does not change per iteration (masks, indices) out of the
  loop into locals. Scalar tails too: `NinoBlockCollections.WriteEach`'s per-value loop with an index scaled twice per
  value wrote 1,000 dictionary entries in 658 ns without hardware intrinsics, with cursors 393 (main's per-entry
  loop: 399).
- On Arm64 (and anywhere without AVX-512) `Vector512<T>` and `Vector256<T>` are structs of 16-byte vectors: passed
  into an inlined loop body they are copied through the stack every iteration. Take their `Vector128` parts out once,
  before the loop, and pass those.
- ARM64 is a load/store architecture: what x64 does in one instruction on memory is a load, an operation and a store
  there, and 64-bit constants and addresses take several instructions. Keep hot state in registers.
- Arm64 `tbl` with two to four table registers needs consecutive registers: build the tuples from loads made once per
  iteration, before any lookup (`(w0, w1)`, `(w1, w2)`), never from per-lookup loads, or the JIT adds `mov`s. Each table
  register is a micro-op on Neoverse cores: give each lookup only the registers its bytes come from, and store only the
  bytes the values fill (`NinoBlockCollections.WireTables64`); check each shape, as types that compile to the same code
  can differ by the data (`(byte, int)` took twice as long as `(short, int)`).
- Load and store a `Vector<T>` in the netstandard builds with `Unsafe.As<byte, Vector<T>>(ref at)` (plain `ldobj`
  / `stobj`), never `Unsafe.ReadUnaligned` / `WriteUnaligned`: classic Mono compiles the `unaligned.` prefix on a
  struct wider than its inline-copy limit to a call to memcpy through a stack slot (`mini_emit_memory_load` in
  mono/mini/memory-access.c), and plain ones to `movups`. RyuJIT never assumes a vector aligned, so its code is the
  same. Evidence: `NinoMask.CopyVectors` on Mono 6.8 serialized 1,000 padded structs in 36 to 40 µs with
  `ReadUnaligned`, 8.4 to 9.2 with `Unsafe.As` (11.8 to 13.4 with ulongs); its .NET 10 listings (netstandard2.0 and
  net10.0 builds) were identical. Which `Vector<T>` operations Mono accelerates at all is `platforms`.

## Checks of values in a block

- Check a block read's bools in the pass that moves the values, not in a strided pass before it: OR the stored vectors
  (read back from the store buffer) into one accumulator per vector and mask them once after the last group
  (`NinoBlockCollections.LanesChecked`, AVX2 lane groups; AVX-512 groups OR their loads, `ReadPlan.Checks`). 1,000
  `Waypoint`s deserialized in 0.922 times the time on EPYC 7763 (1,360 to 1,240 ns, floor 1.000) and 0.965 on 9V74 VMs
  without AVX-512, writes unchanged (.NET A/B run 41079230729). On Arm64 the same check in `Lanes` cost the inlining of
  `Lanes16` (`jit.md`, "Inlining budget"), so Neoverse-N2 keeps the strided pass (0.967 measured with the check, run 41052335826).
- Check bytes spread at a fixed stride (the bools of a block, `NinoChecks.Strided`) eight at a time into four
  accumulators, OR-ed once at the end: one accumulator is a chain of dependent ORs, one cycle per byte. Evidence:
  1,000 bytes 15 apart in 155 ns instead of 535 on a 2.1 GHz AVX-512 Xeon (four a step: 195); on GitHub's runners
  1,000 `Waypoint`s (a bool at offset 14 of 15) deserialized in 0.81 to 0.93 times the time on every CPU (EPYC 7763,
  9V45, 9V74, Xeon 8573C and 8370C, Neoverse-N2). Rejected: a vector check with a periodic mask of the bool
  positions, 211 ns with 64-byte vectors and 325 with 32-byte ones against the scalar 155.
- Check ranges of values in a run (Untrusted mode's bool, DateTime, decimal, DateOnly and TimeOnly fields,
  `NinoValueChecks`) with a subtract and an OR, never with compares: per stride, tables of masks and bounds repeat over
  lcm(stride, vector width), each vector ORs `bound - (v & mask)` into an accumulator (bytes: `v & 0xFE`) and only its
  sign bits are tested at the end. Use the vectors only when they cost less than the branch-free scalar loop
  (`2 * passes * stride <= fields * width`). Evidence (local Xeon, AVX-512, 1,000 values): unsigned `GreaterThan`
  compares were slower than scalar (a bool/DateTime/int/double struct 1,616 ns against 1,526; a 48-byte struct of
  every kind 9,849 against 5,658); subtract and OR with the cost rule took 1,535 and 5,644 against main's 3,806 and
  12,040 (main branched per field), DateTime[] 382 against 1,914; on runners the Untrusted list case (1,000 structs
  with a DateTime and a bool) took 0.47-0.69 times main's time on every CPU. Put a bool in a wider pass's lane where
  that lane tests only against a bound of zero and the bool is not its top byte (`Pass.Joins`: `0 - (v & mask)` has
  its sign set exactly when a masked bit below bit 63 is set), so passes, not fields, set the cost: the Untrusted
  list's struct (a bool at offset 20, in the DateTime pass's 8-byte lanes) went from two scalar loops to one vector
  pass: the Untrusted list case 11-20 % faster on x64 runners with the over-read fix included (round 129, dotnet-ab
  runs 42341861781 and 42365821207). Arm64's 16-byte vectors keep it scalar by the cost rule (48 > 32), on par there. Tried and not kept:
  the loop over whole periods with the tables' offsets from an inner loop instead of the phase wrap, 5-10 % on the
  check alone, inside the noise of the case.

## Blocks, permutes and copies

- Reserve a fixed-size queue's count and both ring segments together. Keep the variable-size element loop separate
  so it does not repeat the fixed-size check for each segment. A stack's backing span can use the existing
  counted span writer.
- Reserve a raw sorted list's count and pairs together, then walk its checked key and value arrays by reference.
  Use `WriteValueIn` for both fields so the raw path retains the padding masks.
- For an exact concurrent queue with variable-size elements, write its snapshot enumerator and patch the count
  from the items written. Reading `Count` separately can observe a different snapshot. Retain bulk writes for
  fixed-size elements and the existing snapshot semantics for derived types, stacks and bags.
  A pooled chunk loop and direct raw snapshot writes both saved the snapshot array but regressed several fixed
  queue sizes in a local A/B. Compare fixed-size cases separately before replacing their bulk path.

- Write a runtime's strided entries (a `HashSet<T>`'s or `Dictionary<K, V>`'s entry array) through the block path
  (`SetEntryBlock`, `MapEntryBlock`: a block map of the key and value at their offsets in the entry), never one
  `Write` per entry: the permutes pack them like any padded struct. Evidence (GitHub runners, runs 40842440261 and
  40865180098, n=12-48 a CPU, floors 0.99-1.01): 1,000-entry `HashSet<int>` serialize 0.34-0.49 times the time,
  `Dictionary<int, int>` 0.29-0.56, the polymorphic class array holding both 0.69-0.84.
- Compare bytes with chars eight at a time where no vectors are (netstandard `NinoStrings.SameBytes`): widen a
  ulong of bytes to two ulongs of chars (`x = (x | x << 16) & 0x0000FFFF0000FFFF; x = (x | x << 8) &
  0x00FF00FF00FF00FF`) on little-endian machines, with an overlapping last step, instead of one char a step.
- Gate an instruction choice per CPU where the runners disagree, by CPUID at startup in a `static readonly` (the JIT
  folds it), never by a `Vector512.IsHardwareAccelerated`-style ISA flag alone: AVX-512 VBMI is on Ice Lake, Sapphire
  Rapids and Zen 4/5 alike. `NinoBlockCollections.WritePairs` (four 24-byte values per `vpermt2b` of 128 bytes of memory
  into 64 of blocks) wrote 1,000 `Waypoint`s in 0.64 times the time of 32-byte lane groups on Xeon Platinum 8573C, 0.74
  on EPYC 9V45 and 0.97 on 9V74, but 1.44 times on Xeon Platinum 8370C (Ice Lake): `NinoUnsafe.Wide512` takes AMD and
  every Intel CPU but the Sunny Cove generation, by family 6 and model (`IsIceLake`: 0x6A, 0x6C, 0x7D, 0x7E, 0x9D, 0x8C,
  0x8D, 0xA7). Never gate on a feature bit a newer CPU merely adds: Azure's Xeon Platinum 8573C (Emerald Rapids) hides
  AVX512-FP16 and AMX from its guests (lscpu in the job log), so the first gate, on FP16 (CPUID 7.0 EDX bit 23), turned
  the 64-byte masks off there and wrote 1,000 padded 8-byte structs in 92 ns instead of 60 (.NET A/B run 41020623184);
  gated by model, 8573C wrote them in 0.80 times the time of the FP16 gate and 1,000 `Waypoint`s in 0.81 (pair permutes,
  336 to 259 ns), the 8370C unchanged (run 41079230729). Include `Xeon` in the A/B line's CPU regex for any AVX-512
  path, or Ice Lake goes unmeasured.
- On Arm64, move values over 8 bytes in partial lane groups (`NinoLanes.Solve` with `partial`), as x64 without VBMI
  does: one at a time they took 1.44 µs for 1,000 `Waypoint`s on Neoverse-N2, in lane groups 0.43 (MemoryPack's copy
  of their memory 0.39). Take the partial groups' windows on the 16-byte grid there (the grid pass first, kept on a
  tie): two `Waypoint`s a step from `ldp` + `ldr` at 0, 16 and 32 and two 2-register lookups, against loads anywhere
  (0, 9, 25; a 2- and a 1-register lookup) at the same `Cost`, wrote 1,000 in 0.757 of the time (430 to 325 ns, floor
  1.002, .NET A/B run 41164356389; every other case on par). Measured and not taken (`WaypointPackProbe`, run
  41159851472, against the old groups on Neoverse-N2): four values a step from six grid loads with a 3-register lookup
  0.62-0.67 and the grid pair twice a step 0.61-0.66 (no clear gain over the pair's 0.66-0.70, and two groups a step
  cost `HashSet<int>` writes before), a scalar pair of 8-byte loads with the bool put in by `bfi` 1.34, a 16-byte load
  with the bool loaded into lane 14 (`ld1` lane) 1.20.
- Copy long blocks per vendor and per destination: past 32 KB a 32-byte vector loop beats Memmove into a new zeroed
  array on Intel (Xeon 8370C, 160,000 bytes: 3.18 µs instead of 3.51) and loses there on Zen 5 (EPYC 9V45: 1.81
  against 1.67), while on AMD it beats Memmove into a new uninitialized array (9V45 and 9V74: 0.93 to 0.95 times the
  time). `NinoUnsafe.CopyOver64` (zeroed or existing memory) takes it on Intel only, `CopyToNew` on AMD only.
  From 2,112 bytes to 32 KB, Ice Lake under Linux takes Memmove instead of the 64-byte loop
  (`NinoUnsafe.IceLakeMemmove`): glibc runs it as one rep movsb from an aligned destination (FSRM), while the loop's
  loads split two lines unless source and destination sit alike in a line. Xeon 8370C (run 41188054612, n=12):
  1,000 `KeyValuePair<int, int>` serialize 151 -> 128 ns (median 0.846; head 60 or 128 ns by process, base 106, 151
  or 181), a `Queue<int>` of 1,000 94 -> 74 (0.783; head 43 or 74, base 61 or 94), TupleList (not a copy) 1.015; the
  floor line drew no 8370C. Confirmed with a floor (run 41203402942, n=24, base rounds against each other as the floor):
  `KeyValuePair<int, int>` 0.844 (head 128 ns in 22 of 24 rounds; base 105, 151 or 182; floor median 1.006), `Queue<int>`
  0.690 (head 74 or 43, base 93-109 or 58; floor 0.999). Not on other CPUs: AMD's rep movsb lost to the loop 2-30 times up to 16 KB, and other C
  runtimes' memmove was not measured.
- Allocate a new array uninitialized only where the operation writes every byte of it, padding included
  (`NinoElements.CopiedWhole`), or it hands stale heap memory to `MemoryMarshal.AsBytes` or a raw re-serializer: block
  reads write a padded value's padding as zero (`NinoBlockCollections.ReadOne` clears a value before `ReadBlock`;
  AVX-512's `vpermb`/`vpermt2b` read only an index's low bits, so their padding bytes are masked with a `vpand` per
  vector, `ReadPlan.Keeps`, `KeepMemory64`; pshufb, tbl and Arm64's lookups write 0 for index 0x80 by themselves), and
  the tests read into 0xFF-filled and recycled 0xCC memory and compare every byte
  (`BlockStructTests.ReadsWriteEveryByte`; without the masks they fail on AVX-512). Values read a field at a time
  (Untrusted mode, `ReadSize` -1) keep a zeroed array. Evidence: rejected first with the padding unwritten (Waypoints
  0.68-0.76 times main's time); landed with it written, together with the read groups below (run 40935400095). Where the
  read can still reject the data after the allocation (a bool of a block: `NinoBlockPlan<T, TFormatter>.Rejects`),
  `NinoBlockCollections.ReadAll` reads them out of line under a try/finally that clears the values when it throws
  (`ReadRejecting`; `audit-security`). Never allocate those arrays zeroed instead: 1,000 Waypoints read into a new array
  took 1.15 to 1.27 times as long (A/B run 41110873514, every CPU). Add nothing to `ArrayFormatter` or
  `ListFormatter.Deserialize`, not even a test that folds in Release: they recurse through collections nested in
  themselves, and Debug builds (CI's Windows unit tests, 1 MB stacks) compile every branch and keep a temp per call. A
  test of a static field and an out-of-line call there grew the two frames from 672 + 448 to 768 + 576 bytes
  (`DOTNET_JitDisasm` under Debug, `sub rsp`) and 400 nested `List<SelfArrays[]>` ran out of stack on Windows
  (`SelfNestingCollectionTests`, main e2e28fe7). In Release the same test through an inlined method returning a
  constant, though it folded, made `ArrayFormatter<int, ...>` stop inlining `NinoElements.Read` into the
  ImmutableHashSet, ImmutableDictionary, ReadOnlyCollection and Memory<int> reads (1 stack access to 15, `asm.py`).
- Skip the initial clear for large canonical raw serialization results (ad425fcc). The layout write fills the
  entire array and clears padding. Keep the small direct allocation and use the shared 6 KiB cutoff.
- Read values over 16 bytes whose blocks never fill whole vectors in four of memory in groups that fill whole memory
  vectors (`ReadPlan.Group`: 8 24-byte values in three vectors, each one `vpermt2b` of two 64-byte loads, aligned
  stores), and check their bools in those loads (`ReadPlan.Checks`: OR `load & 0xFE-at-bools` into one accumulator,
  test once, the strided pass only to name the bad one). Evidence (GitHub runners, run 40935400095, n=12): 1,000
  `Waypoint`s deserialize in 0.77-0.85 times the time on EPYC 7763 and 9V74, Xeon 8370C and Neoverse-N2. Rejected: one
  64-byte load permuted into 128 bytes of memory per four values (`vpermb` twice, overlapping stores), 1.3 to 1.4 times
  the time of lane groups on a local AVX-512 Xeon. Not built: the same fused check in Arm64's lane groups, whose
  windows overlap per output vector (an AND and ORR per window against the strided pass's one load per bool).
- On Ice Lake, mask padded raw structs with 32-byte vectors (`NinoUnsafe.Wide512` gates `NinoMask`'s 64-byte masks
  as it gates the pair permutes): GitHub's Xeon Platinum 8370C wrote 1,000 8-byte structs with a padding byte in 116
  ns with 64-byte vectors and 83 with 32-byte ones (MemoryPack 88). Its 64-byte kernels on 8,000 bytes (aligned
  stores 145 ns, a zero-masking load `Avx512BW.MaskLoad` instead of the `vpand` 147, unaligned 167-287) ran no faster
  than 32-byte ones (144) and slower than Memmove (122) (`MaskKernelProbe`, run 40935400095): a zero-masking load
  buys nothing there, nor locally on Emerald Rapids (1.06 against 1.07 of Memmove). Keep the 64-byte write groups
  (`Permute64x2`, vpermt2d) on Ice Lake: 32-byte ones (vpermt2d ymm, AVX-512VL) took 0.76-0.79 of their time in a
  native-buffer probe there (`TuplePackProbe`, run 41159851472: 167-173 ns against 220-226), yet in the benchmark
  1,000 `(int, long)` serialized 4.5 % slower on the 8370C (234 to 245 ns, n=12, every round above 1.0; run
  41164356389). Ice Lake's VM there pays heavily for loads across cache lines (a 32-byte copy loop of 16,000 bytes
  from 8 bytes past a line: 430 ns, aligned 222, Memmove 160; 64-byte: 377 and 190): a probe kernel with fixed
  alignments does not predict the case, whose list and buffer land where the process puts them. MemoryPack's copy
  there is bimodal by alignment (117 or 250 ns in the probe, 112 in the case).
- Before probing kernels for a gap on one CPU, research that microarchitecture (Intel's optimization manual, uops.info,
  Agner Fog, Travis Downs, Chips and Cheese) and read the code both sides run, IL and asm, instruction by instruction: a
  probe then confirms a cost model instead of searching (the owner: "you keep guessing"). The Ice Lake TupleList gap
  (round 103): MemoryPack writes the raw 16-byte `(int, long)`s with one `CopyBlockUnaligned`, which .NET hands to glibc
  past 2 KB and glibc runs as one `rep movsb` past 2,112 bytes on FSRM CPUs (destination aligned, so its time depends on
  the source's offset to it: 117 or 247 ns); Nino packs 16 to 12 bytes with 4 loads, 3 `vpermt2d` and 3
  destination-aligned 64-byte stores per 16 values, and its loads split two lines unless the list's group starts on one:
  the 234 / 162 ns modes. On a Xeon 8573C (run 41180010041, `TupleCostProbe`): 188 split 64-byte stores 142 ns against
  59 aligned, 250 split loads 97 against 80, 188 `vpermt2d` 59, the clock 4.0 GHz scalar and 3.1 under zmm code; Nino's
  own write 74 to 144 ns by the list's place (identical code both sides). On Ice Lake (Xeon 8370C) Nino's write groups
  therefore load from the line below each group (`NinoBlockCollections.WriteAligned`, tables per place in the line, a
  second `vpermt2d` for the vector of blocks that spans three lines): 1,000 `(int, long)` serialized in 0.917 of the
  time (235 to 221 ns, every round 0.85-0.96, n=24, run 41181295487) and the `Dictionary<int, int>` entries in 0.809
  (250 to 200 ns); other CPUs fold the gate (listings identical to main). Less than the 0.65 the model gave: the rest is
  the stores and the clock. Confirmed against main before it with a floor (run 41203402942, two 8370C jobs, n=24,
  base round against base round in the same jobs as the floor, `benchmark`): TupleList 0.925 (235 to 222 ns, every
  round 0.84-0.97; floor 1.001, 0.93-1.08), the dictionary 0.797 (every round below 0.93; floor 0.994), MemoryPack
  (same code both sides) 0.998.
- Do not retry the remaining Ice Lake TupleList gap (Nino 222 ns, MemoryPack 114 on the 8370C) with these; each loses by
  the model, so none was built (round 104). The gap is the store path: 12 bytes a value (the wire format) are at least
  188 64-byte lines, and the 8370C writes lines with vector stores at about 0.76 ns each (16,000 bytes copied with
  aligned 64-byte vectors in 190 ns, r102) but with `rep movsb` at about 0.46 (MemoryPack's 16,000 bytes in 112-117):
  188 lines cost about 143 ns before any permute. 256-bit aligned groups (vpermt2d ymm): `vpermt2d zmm` and 512-bit
  loads and stores are light AVX-512, which costs Ice Lake server parts at most about 200 MHz all-core (Intel in
  golang.org/issue/43925; Phoronix measured about 175 MHz on a Xeon 8380 under heavy AVX-512), so 256-bit code wins
  back at most about 6 %, while it needs 375 stores and about 525 permutes against 188 and 235, and the 8370C's 32-byte
  aligned copy already lost to the 64-byte one (222 against 190 ns); locally (Emerald Rapids, `TupleGroupProbe`) they
  took 1.7-1.9 times the time at every source shift. Non-temporal stores: they evict the reused buffer and run at
  memory bandwidth (188 64-byte ones 757 ns locally against 68 cached). `rep movsb` with packing, either way round
  (pack into scratch then `rep movsb` it, or `rep movsb` the raw values then compact): two passes, at least the current
  pass plus about 85 ns.
- Rust's fixed-size blocks: `optimize-runtimes`, references/blocks.md.
