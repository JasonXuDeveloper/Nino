# Bounds checks and vector loops (.NET)

Part of the `optimize` skill: removing bounds checks, and the vector loops on x64 (AVX-512, AVX2, SSSE3), Arm64
(tbl, ldp/stp) and the netstandard builds, with the per-CPU decisions (Ice Lake, Zen, Neoverse) and the kernels
measured and not taken. Read it before writing or changing a loop over elements, a block copy, a permute or a check
of bytes in a block.

- Keep shared managed SIMD capabilities and CPU policy in `Nino.Core/Internal/Simd/NinoSimd.cs`. At kernels with
  equivalent implementations, check 512, then 256, then 128, then scalar. Use inlinable capability checks so the
  JIT can discard unsupported widths. Keep each kernel’s length, alignment, exact ISA requirements and tail beside
  its loop. Unity and WebGL register function pointers through `NinoKernels`; `Internal/Simd/NinoKernelHook.cs`
  owns their dispatch. Generated formatters call Core entry points and leave ISA selection to the runtime.
  Keep the complete text SIMD entry and loop in `NinoSimd.Text.cs` or `NinoSimd.Text.Legacy.cs`,
  so the exact ISA guard can fold in Tier 0. Leave string headers, scalar handling and the legacy native
  hook ordering in `NinoStrings`. Moving the guarded methods preserved the sampled Arm64 code sizes
  and the aligned fallback timings (`2a596daf`).

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
  Compare the loop entry as well as its exit condition. Replacing the dual-cursor countdown with
  an equality check against the last valid destination byref removed the decrement, but both loops
  cloned the first element's field reads on Arm64 scalar Tier 1. In baseline, countdown, and
  end-byref order, `ReadEach` was 459/493/503 IL bytes, 164/204/220 Arm64 scalar Tier 1 bytes,
  and 135/106/122 x64 scalar FullOpts cross-JIT bytes. With SIMD those machine-code sizes were
  232/244/260 on Arm64 and 180/190/209 in the x64 listing. Four default-tiering process-per-build
  rounds through the generated array interceptor at offset 1 showed no repeatable gain at
  1, 65, 1,640 or 1,700 elements. Identical binaries varied by up to 32% on the 65-element SIMD
  case. Reads allocated 0 B and hashes matched at offsets 0, 1 and 7. The end-byref candidate
  passed 21 targeted tests on .NET 10. Keep both patches and try a pretested loop with the same
  cursor steps to isolate the first-iteration cloning.
  Testing `remaining > 0` before the body and removing the separate empty-tail guard kept a single
  field-read body in Arm64 scalar Tier 1. Compared with the original indexed loop, IL went from
  459 to 492 bytes, Arm64 scalar Tier 1 from 164 to 140, and the x64 scalar FullOpts cross listing
  from 135 to 108. With SIMD, the listings grew from 232 to 248 and from 180 to 191 bytes.
  Scalar timing stayed within 5% in all four default-tiering rounds. At 1,700 items with SIMD,
  all four initial rounds favored the pretested loop, but an alternating replay with an identical
  baseline control showed up to 40% A/A variation. Keep the patch as a code-generation result
  and measure the tail separately before attributing a throughput gain. Wire bytes matched at
  offsets 0, 1 and 7, reads allocated 0 B, and the block, versioning, reference, and deferred-body
  checks passed on the tested .NET targets.
  Keep the original index as the pretested loop condition while advancing both byrefs. This
  removes the remaining-count subtraction from the SIMD tail entry and still avoids first-iteration
  cloning on Arm64 scalar Tier 1. Against the indexed-address baseline, IL was 459 versus 484 bytes,
  Arm64 Tier 1 was 164 versus 136 bytes without SIMD and 232 versus 244 with SIMD, and the x64
  FullOpts cross listings were 135 versus 106 and 180 versus 191. The 7-element case stayed below
  the SIMD entry threshold. At 65 and 1,700 elements, extending each timed batch and alternating
  with a byte-identical control reduced the worst A/A spread from about 42% in the short 65-element
  batches to 12.2% at 65 and 6.4% at 1,700. The candidate did not show a repeatable throughput
  gain. Keep the patch for code-generation comparisons. Both sides allocated 0 B per read and
  produced identical wire bytes at offsets 0, 1 and 7. The candidate passed 22 targeted tests
  on each of .NET 8 and .NET 10.
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
  Use the same proof for the shared nullable array/list reader. Every caller checks the
  whole wire stride and opens a destination of that exact count. Reuse the byte count for
  advancing and copying, and keep the presence-flag checks. The helper went from 169 to
  163 IL bytes. For `int?` and `bool?`, Arm64 FullOpts went from 192/248 to 148/212
  bytes, and x64 cross-JIT from 405/283 to 389/261. Local reuse stayed at 0 B with no
  repeatable timing change against the duplicate baseline, including the 64 KiB boundary.
  Wire, truncation and invalid-flag checks matched at aligned and unaligned offsets.
  Batch raw nullable presence checks in 64-byte vector groups for large payloads with
  strides of 2, 4, 8 or 16. Unlike strided pair fields, each nullable flag is followed
  by a full slot, so whole-vector loads stay within the validated payload. Use the
  scalar scan for short inputs, unsupported strides, and the original invalid-flag
  diagnostic. The first one-vector loop regressed both 64-item and cache-sized
  integer arrays. The 64-byte loop with a 128-item gate improved the unaligned
  4,095- and 4,096-item reuse cases in all four local default-JIT rounds against
  both baselines, at 0 B allocated. Short inputs varied. The vector helper adds
  320 IL bytes, 272 Arm64 FullOpts bytes, and 204 x64 cross-JIT bytes.
  Process 128 bytes per full iteration with two groups of four vector loads.
  Compared with the 64-byte loop, all four unaligned `int?[]` reuse rounds
  improved against both baselines on each side of the 64 KiB working set.
  Time fell by 19–47% at 4,095 items and 20–39% at 4,096 items versus
  the faster baseline in each round. Shorter
  samples varied and allocation stayed at 0 B. The helper grew from 320 to
  493 IL bytes, 272 to 336 Arm64 FullOpts bytes, and 204 to 295 x64 cross-JIT
  bytes. The wire samples and scalar fallback matched.
  Keep the 128-byte loop as the common path. A 256-byte loop improved
  16,384-item reads but varied near 64 KiB. Gating that loop at 64 KiB of
  input still slowed 128-, 4,095-, and 4,096-item reads against both baselines
  in all four rounds, despite those cases taking the 128-byte loop. The gated
  helper and its inline group reader went from 493 to 566 IL bytes, 336 to 464
  Arm64 FullOpts bytes, and 295 to 483 x64 cross-JIT bytes. A separate four-round
  comparison reproduced the 128-byte loop's gain over the 64-byte loop for
  4,096 and 16,384 items. If revisiting the wider loop, isolate its machine-code
  layout from the common path and measure both again.
  The out-of-line 256-byte variant kept the Arm64 FullOpts instructions of
  the 128-byte helper identical and kept its x64 cross-JIT size at 295 bytes.
  It still slowed 4,095-item reads against both baselines in all four local
  rounds, and the 16,384-item timing changed direction. The flag entry and
  new wide path added 174 IL bytes including shared helpers, 356 Arm64
  FullOpts bytes, and 303 x64 cross-JIT bytes. Keep the existing dispatch.
  Share the 128-, 64-, and 16-byte loops through one inlined scan and pass
  constant offsets to the four-load helper. A pre-offset reference introduced
  an address add on Arm64. The helper calls survived in instrumented Tier 0
  and slowed the scan. Compile the out-of-line validator with
  `AggressiveOptimization` so it starts with both helpers inlined. The original
  loop with the same attribute confirmed that tiering accounts for most of
  the gain. On Apple Silicon with 128-byte cache lines, pinning the wire start
  at offsets 0, 63, and 127 gave 43–66% less time across four rounds on each
  side of the 64 KiB working set. Allocation remained zero. The first 128-item
  read median rose from 1.62 to 1.74 ms. With all widths optimized from entry,
  64-byte groups generally trailed 128, and 256 did not consistently beat 128
  at those offsets. Use line size as a benchmark input; keep the batch width
  separate from native SIMD width. The validator and helpers shrank from
  493 to 354 IL bytes, with 336 Arm64 FullOpts bytes on both sides. x64
  cross-JIT went from 295 to 301 bytes with the optimization attribute
  removed for the listing, since crossgen2 skipped the annotated method.
  A separate scalar conversion probe replaced the indexed nullable read with
  two cursors. It removed the stride shift and reduced the `bool?` helper
  from 212 to 196 Arm64 FullOpts bytes and 261 to 247 x64 bytes, but short
  reuse readings often regressed. The wire and generated files matched.
  Specialize the same scan for native 128-, 256-, and 512-bit vectors through
  inlined load, OR, and mask operations. Select the width outside the loop
  with JIT-foldable acceleration checks and the existing `Wide512` policy.
  Keep the 128-byte scan batch independent of vector width. On Arm64 the
  128-bit specialization stayed at 336 FullOpts bytes with no repeatable
  local timing change. x64 cross-JIT
  emitted XMM, YMM, and ZMM variants of 296, 277, and 271 bytes, respectively.
  Native x64 timings are still needed. A direct probe validated all widths
  for the four strides, including unaligned loads and invalid flags in tails.
  Spell out the 512-to-256-to-128 selection. The hardware gates fold and the
  remaining x64 v4 policy branch changes direction without growing the entry.
  Arm64 measured on par, at 152 FullOpts bytes, and x64 cross-JIT entries stayed
  at 84/84/112 bytes for v2/v3/v4. Use one 128-bit pattern for the repeated
  mask. Passing two scalar words instead grew the Arm64 scan from 336 to 368
  bytes and lost to the faster baseline in all four 4,095-item reads.
  Share the three-width validator with strided bools. Pass a byte bound
  that ends at the last flag for a field inside a record; only nullable
  payloads guarantee the entire final stride is readable. Preserve the
  scalar diagnostic and unsupported-stride fallback. In four local
  Arm64 rounds, reused `List<(byte, bool)>` took 30–35% less time at 128
  items and 90–95% less at 16,384 against the faster duplicate baseline,
  at 0 B allocated. Nullable timings varied. The strided bool entry grew
  from 68 to 160 Arm64 FullOpts bytes and 25 to 91 x64 cross-JIT bytes.
  All three vector widths passed direct last-flag and full-slot probes.
  Keep the scalar tail for the shared validator. An overlapping load ending on a whole stride
  preserved mask phase, and a strided field ending at its last flag still needed one scalar check.
  Folding the tail vector into the accumulator, then trying a separate tail check with an exact-end
  fast return, both lost in public zero-offset `long?[]` reuse at 4,096 items. The separate tail
  check improved the direct 128-item stride-two bool check in all four M4 rounds at both offsets.
  It increased the validator from 217 to 256 IL bytes and the Arm64 128-bit instance from 328 to
  380 B. The 256- and 512-bit x64 cross listings grew from 410 to 512 B and 437 to 543 B in MinOpts.
  The x64 128-bit listings used different optimization tiers. All 6,144 width/stride/offset cases
  and their invalid-tail mutations passed, with matching wire hashes and zero steady allocation.
  Both candidates were withdrawn.
  Keep the bulk clear for converted nullable writes. Per-slot clearing
  wrote each slot once but slowed 64 mixed and 16,384 all-null booleans in
  four paired runs. Wire bytes matched and Arm64 FullOpts grew from 1,256
  to 1,404 bytes.
  On Arm64, bypass that converter for nullable bools with the probed two-byte
  flag/value layout. Expand each 0/1 presence bit into a lane mask, clear the
  null slots and end the final vector at the last complete slot. Use 64-bit
  vectors for four to seven elements and 128-bit vectors thereafter. Check the
  minimum count before hardware and layout tests so the single-item control
  stays flat. Compile the bounded vector loop optimized from its first call.
  The M4 default-JIT 128-item mixed median went from 1.78 us to 38 ns; 16,896
  mixed items went from 25.9 to 0.59 us. The baseline was still instrumented
  Tier 0 at 128 items. Offsets zero and one, dirty null slots, and wire output
  matched at 0 B. Write grew from 197 to 346 IL bytes before its shared helpers;
  Arm64 FullOpts went from 1,256 to 1,424 bytes plus the 92-byte 128-bit kernel.
  The x64 FullOpts fallback stayed at 998 bytes. Keep 128/256-bit x64 candidates
  out: the shift variant still slowed the emulated 129-item probe by over 30%.
  The 512-bit cross listing showed ZMM loads and stores but did not run on
  hardware. Both modern suites, the scalar fallback and netstandard builds
  passed (`cc716ba8`).
  Keep the overlapping final vector. Replacing it with a 64-bit group and up to
  three scalar slots slowed the 16,897-item control by a 12% median. Two-vector
  unrolling had no repeatable gain and slowed 15,873 items by a 4% median.
  Advancing two byrefs with a signed countdown generated post-index loads and
  stores, but four default-JIT M4 pairs were broadly flat at short sizes and
  around a 64 KiB input/output footprint. The cursor version grew the shared
  loop from 44 to 81 IL bytes and kept 92 Arm64 FullOpts bytes; emulated x64
  remained on the 998-byte fallback. All ten wire cases matched at 0 B.
  Keep the shared loop for four to seven slots. A dedicated one-or-two-group
  helper removed the loop and shrank Arm64 FullOpts from 92 to 76 bytes, but the
  default JIT used 112-byte Tier 0 code and medians slowed by 10–22% in four
  M4 pairs. Requesting optimization from the first call brought the medians
  within 1%. Requesting inlining still slowed the short cases by 11–24% and
  grew the Arm64 FullOpts entry from 1,512 to 1,544 bytes. The short helper
  added 33 IL bytes beside the shared 44-byte loop. The x64 fallback stayed
  at 1,091 bytes in all three variants. At offsets zero and one, all 26 wire
  cases matched for each variant and steady allocation was 0 B. Timing around
  the 64 KiB input/output footprint varied. Retain the shared optimized loop.
  Write one to three bool slots with a single little-endian word store per
  element. Use typed nullable access and zero the entire slot when absent.
  Four M4 default-JIT pairs improved the present singleton by a 36% median and
  three present values by 23%. Mixed two- and three-item medians improved
  by 8% and 7%. The null singleton and vector controls were broadly flat.
  The emulated x64 singleton improved 23%; other guest timings were noisy.
  Nine wire comparisons matched at 0 B. Write grew from 346 to 499 IL bytes,
  from 1,424 to 1,512 Arm64 FullOpts bytes and from 998 to 1,091 emulated x64
  FullOpts bytes. Dirty nulls, disabled intrinsics and portable builds passed
  (`0e424b38`).
- Keep the 81–128-byte Arm64 shortcut at the checked raw array reader. An inline branch and a
  separate helper in the shared copy dispatcher both slowed the 1 KiB direct control. Extracting
  the existing loop's vector group still affected that control, even after moving the branch.
  Leave the shared loop intact and express the short head and tail as two constant 64-byte
  `CopyBlockUnaligned` calls. The Arm64 JIT emitted paired 128-bit loads and stores in 64 B.
  Four M4 rounds reduced reuse time by 39–45% and fresh reads by 27–41% at the covered sizes.
  Both offsets, surrounding sizes, wire bytes, sentinels and allocations were checked.
  Array deserialize IL grew from 102 to 133 B and its Arm64 caller from 564 to 608 B.
  The x64 cross caller stayed 195 B and shared copy listings stayed 580/1,096 B on Arm64/x64.
  Both .NET suites, the no-intrinsics case and the netstandard builds passed (`894d6936`).
  Reuse the same short kernel for one-byte raw lists. Keep the array helper and guard
  unchanged. Loading both blocks before the stores or sharing an inline guard changed the
  array control. For a list with overlapping backing storage, use span copy in the outlined
  list entry. The final M4 byte-list probe improved reuse by 24–32% and fresh reads by
  12–18%, with matched bytes and allocations at offsets zero and one. Int32 timing varied
  even in A/A and its Arm64 caller kept 564 B. List deserialize IL moved from 162 to 205 B.
  The byte-list caller moved from 948 to 992 B on Arm64 and stayed 205 B on x64 cross-JIT
  MinOpts. The list helper was 92 Arm64 bytes. Boundary, overlapping-buffer and no-intrinsics
  cases passed (`9ce599ed`).

- Select the short Arm64 copy inside the existing raw span branch for byte-sized `Memory<T>`.
  Checking it in the formatter duplicated raw dispatch and moved even the `int` control.
  The final M4 default-JIT probe reduced 81–128 byte reuse medians by 23–27% across the tested
  sizes and buffer offsets. A separate per-size A/B with repeated same-binary controls put the
  129-byte overhead below 5%; near a 64 KiB input-plus-output footprint, the controls were noisy.
  Reuse allocated 0 B, fresh allocations and wire hashes matched, and an overlapping slice kept the
  next field readable. The formatter and shared span reader moved from 119/255 to 120/296 IL bytes.
  Arm64 FullOpts moved from 1,296 to 1,340 bytes for `byte` and stayed at 1,256 bytes for `int`.
  x64 cross-JIT printed MinOpts, where the byte formatter moved from 475 to 480 bytes and emitted
  no short-copy call. Keep that listing separate from x64 throughput (`7f4103cf`).
- Use the fixed Arm64 head and tail for fresh one-byte raw array writes at 65–128 bytes.
  The new output is distinct and the four-byte header deliberately offsets the destination.
  Starting at 81 bytes left an 80-byte slowdown. Putting the range check in the shared
  length-prefixed method also slowed the 64-byte Int32 control. Dispatch in the explicit
  allocating array formatter instead. Four M4 default-JIT A/B rounds with repeated-binary
  controls lowered 65, 80, 96 and 128 byte medians by 28–37%; the 32, 64 and 1,024 byte
  controls and both sides of the 64 KiB input-plus-output footprint showed no meaningful
  regression. Byte output and allocation matched. Formatter IL grew from 46 to 110 B;
  Arm64 FullOpts went from 532 to 588 B and uses the existing 64 B helper. The public caller
  stayed at 660 B on Arm64, and x64 cross-JIT caller/formatter stayed at 501/419 B.
  The boundary wire checks and both frameworks passed (`7939e974`).
- Keep the overlap-safe span copy for raw arrays written to a caller-owned buffer.
  Adding the fixed Arm64 head and tail behind an overlap test slowed 65–128-byte writes
  by 7–21%. Replacing the checked destination slice with a prefix check plus byref span
  construction also slowed 64–1,024 bytes. Instead skip the Tier 0 ClearPadding call
  when the layout has no padding. Outlining the remaining padded path slowed a single
  padded item by about 4%, so keep that path inline. Four default-JIT M4 pairs with mixed
  offsets showed a 3–7% drop at 64, 65 and 80 bytes and a 5% drop for 16 ints. A longer
  cache-boundary retest varied in both directions, including repeated-binary controls.
  Wire bytes matched at 0 B steady allocation. IL grew from 156 to 167 B, Arm64 Tier 0
  grew from 696 to 732 B and stopped executing the 296 B padding helper on byte arrays,
  Arm64 FullOpts stayed at 240 B, and x64 cross-JIT MinOpts grew from 706 to 748 B.
  Both modern frameworks and both netstandard builds passed (`85c0f052`).
- Keep the current raw list writer dispatch. Reusing the overlap-aware Arm64 short
  copy improved 65–128-byte byte-list writes, but the first inline dispatch slowed
  the 32- and 64-byte controls by about 22%. Moving the long path out of line,
  reserving once, and sharing the existing small-copy split still slowed 32 bytes.
  The last writer variant also slowed 16 Int32 items by about 7%.
  Trying only the allocating sized path improved 80–128 bytes by about 12–14%,
  but the 32- and 64-byte controls slowed by 19% and 16%. Sized writer IL grew from
  55 to 128 bytes, Arm64 FullOpts SerializeSized from 764 to 812, and emulated x64
  from 592 to 590. Output and allocation matched in all probes. Do not move the
  short array copy into the list writer without a dispatch that preserves the
  small-list controls.
- Read short nullable bool blocks with the existing checked bool primitive after
  validating every presence flag. The cursor-only trial had no repeatable gain.
  Inlining the checked conversion for every size slowed the above-64-KiB default
  JIT case despite a smaller OSR loop. Keep blocks beyond 1,024 elements on the
  original loop. Four M4 pairs improved mixed 16–1,024-element reads by roughly
  10–11%, with zero allocation and matching bytes. The 1,025-element control was
  flat, the cache-boundary cases varied, and emulated x64 timings were inconclusive.
  The entry and short helper moved from 163 to 224 plus 76 IL bytes, 212 to 276
  plus 132 Arm64 FullOpts bytes, and 141 to 180 plus 83 emulated x64 FullOpts bytes.
  Strict bool, fallback and compatibility checks passed (`18396f99`).
- Reuse the Arm64 nullable bool slot mask for long reads into disjoint storage.
  Validate presence bytes first as before. Check each masked vector for invalid
  live values before storing it and use the scalar loop for a rejected group to
  preserve the first exception and earlier writes. Keep dirty null value bytes
  out of the destination and keep overlap and small blocks on their prior paths.
  Four M4 pairs with default JIT moved 1,025 mixed items from 9.53 us to 154 ns
  and 16,897 mixed items from 12.8 to 1.19 us. Short controls were flat, all eight
  wire comparisons matched, and reuse allocated 0 B. Entry IL grew from 224 to
  356 bytes with 121 bytes in the two new methods. Arm64 FullOpts grew from
  276 to 356 bytes plus a 252-byte vector loop; the emulated x64 fallback stayed
  at 180 bytes. Dirty-null, invalid-tail and scalar tests passed (`5a9438b9`).
  Start that reader at one full 128-bit group (eight bool slots). Return
  through the short scalar loop before the ISA and layout checks for one to
  seven elements. Moving the vector test ahead of the old scalar test without
  that early return slowed the singleton. Four M4 pairs with the early return
  improved the 8-, 9- and 16-item mixed cases by medians of 42%, 39% and 57%;
  1–7 items, the former threshold's upper neighbor and the cache-boundary
  control stayed broadly flat. Twelve wire cases matched at 0 B. Entry IL
  fell from 356 to 348 bytes, Arm64 FullOpts moved from 356 to 364 bytes and
  the emulated x64 fallback from 180 to 185 bytes. The 252-byte vector loop
  was unchanged. Test dirty null and invalid tail slots at both 9 and 1,031
  elements (`c8950716`).
  Handle four to seven elements in the existing short branch using the 64-bit
  mask and one or two groups. Sharing the 128-bit loop through a generic
  read kernel slowed 16,897 items by about 23%. Keep the 128-bit loop and
  its dispatch separate. Four M4 pairs improved mixed 4-, 5- and 7-element
  medians by 27%, 31% and 35%; one and three elements and the 64 KiB control
  stayed broadly flat. The 128-element control had a 3% slower median.
  Ten wire cases matched at 0 B. Entry IL grew from 348 to 369 bytes, and
  Arm64 FullOpts from 364 to 440 bytes plus a 216-byte short helper. The
  252-byte long loop and 185-byte emulated x64 fallback were unchanged in
  size. Check the invalid tail in a five-element overlapping-vector group
  (`9bc09098`).
  Check one bool slot with a single scalar helper after the length proof.
  Validate presence before an active value and ignore dirty null value bytes.
  Inlining the singleton check into the entry slowed the 128-element vector
  control by 6%; outlining it brought the follow-up 4- and 128-element
  differences to about 2%. Four M4 pairs reduced absent and present singleton
  medians by 28% and 32%. Eight wire cases matched at 0 B. Entry IL grew from
  369 to 424 bytes plus a 56-byte helper. The singleton probe's Arm64 FullOpts
  entry grew from 600 to 656 bytes plus 128; emulated x64 from 185 to 220
  plus 70 bytes. Both runtime suites and scalar validation passed (`4ee7e0c8`).
- Unroll two- and three-element presence scans in the shared validator. Keep
  the OR reduction and validate every flag before writing any slots. Four M4
  default-JIT pairs at offsets zero and one improved nullable bool array medians
  by 19–20% and nullable Int32 list medians by 14–18%. Four- and 128-element
  bool arrays and 16-element lists stayed within 2.2% by median. The singleton
  bypasses this validator and its timings varied. Eighteen wire comparisons
  matched and timed reuse allocated 0 B. The validator grew from 46 to 92 IL
  bytes, 152 to 200 Arm64 FullOpts bytes and 88 to 132 emulated x64 FullOpts
  bytes. Test the final flag at an unaligned offset and assert that the reuse
  destination is still untouched when validation rejects it.
- Keep non-ASCII UTF-8 reuse separate from the short ASCII reader. Decoding to a bounded stack
  buffer removed allocations for unchanged CJK and emoji but penalized changed values that had
  to copy to a string. Restricting the trial to short headers avoided changing the fresh parser.
  Re-encoding to a byte buffer helped unchanged CJK but slowed emoji. Restricting that to exactly
  three bytes per UTF-16 unit and checking the first and last bytes removed the emoji regression,
  but changed ASCII or same-width CJK still regressed even after reusing the checked short header
  on a miss. Unchanged eight- to 42-character CJK reads went from 40–112 B allocated to zero.
  The final reader grew from 178 to 316 IL bytes plus a 58 B helper, and from 324 to 552 Arm64
  bytes plus a 408 B helper. The x64 cross JIT only emitted the helper at 470 B in MinOpts.
  Wire bytes and strict decoding tests matched, with 31 targeted tests passing on both .NET 8
  and 10. The candidates were withdrawn.
- Measure the short Latin-1 dispatcher and neighbouring sizes when widening the batch.
  Reusing the long comparison kernels inline improved 31–63-character M4 reuse, but the offset-zero
  64-character control slowed in four pairs. Outlining a 32–63-character helper with 512-, 256- and
  128-bit dispatch retained a 1–66% gain at 63 characters across both offsets, but 31 characters and
  the offset-zero 64-character control still slowed in all four pairs. The short comparator grew from
  277 to 292 IL bytes plus a 48 B helper. Arm64 Tier 1 `ReadStringOver` grew from 1,224 to 1,276 B.
  The new helper was 148 B on Arm64 and 254 B on cross-JIT x64, where the public Tier 1 entry stayed
  at 159 B. All wire bytes, mismatch probes and steady allocations matched, and the eight targeted
  .NET 10 tests passed. Both candidates were withdrawn.
  For Arm64, keep the 24–63-character 16-byte widening batch in a fully optimized out-of-line
  helper. Reuse the long 128-bit kernel's difference operation, accumulating until the final
  test. In the M4 default-JIT probe that alternated offsets 0 and 7 inside the timed loop,
  24, 32 and 47 characters improved by 13–26% across four pairs, and 63 characters by
  20–53% with noisier repeated-binary controls. Separate offset-7 processes also improved
  those sizes. A probe timing offsets successively in one process gave conflicting results
  at the last offset. Keep 15, 23, 64 and 65 as controls. Wire bytes matched, all mismatch
  positions were detected, and unchanged reads allocated 0 B. The short comparator grew
  from 277 to 299 IL bytes plus a 57 B helper. Arm64 FullOpts reader grew from 1,208 to
  1,240 B plus a 148 B helper. Offline x64 ReadyToRun grew from 978 to 1,038 B and retained
  the hardware test as a call. It does not measure the optimized x64 JIT or throughput
  (`573a443f`).
  Check the x64 baseline ISA before the Arm64-only path when ReadyToRun leaves the foreign
  ISA check as a call. The SSE2 guard let .NET 10 Crossgen2 remove both calls on Linux x64
  and return the reader from 1,038 to 978 bytes. Normalized Linux Arm64 ReadyToRun stayed
  identical at 1,220 bytes, and M4 FullOpts reader/helper stayed at 1,240/148 bytes. Short
  comparator IL grew from 299 to 309 bytes. Four mixed-offset M4 default-JIT pairs at each
  of eight sizes had median differences within 3%, identical wire bytes, and 0 B steady
  allocation. x64 throughput was not measured (`6ff3bdf2`).
- Share long Latin-1 equality's tail-first loop across the three native SIMD
  widths, and compile the entry with `AggressiveOptimization`. Without it,
  the generic loop slowed 65- and 256-character reuse in all four rounds.
  With the same optimization setting, the old and shared loops measured
  similarly. Against default JIT baselines, 65-character reuse fell from
  about 76 to 48–51 ns, and two cases straddling 64 KiB fell from roughly
  9–10.5 to 0.8–1.2 µs at 0 B per reuse. First read rose from 0.75 to
  0.98 ms at the median. Arm64 FullOpts fell from 184 to 180 bytes.
  x64 cross-JIT compare loops measured 129/135/175 bytes and emitted
  XMM/YMM/ZMM instructions. The direct probe checked equal inputs and
  every mismatch position at all three widths, including non-Latin-1 chars.
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
  Reuse the shape validation for 2D, 3D and 4D fixed-stride array reads, updating the
  generator template and checked-in output together. In the aligned 2D probe the
  formatter went from 181 to 182 IL bytes, 1,176 to 1,164 Arm64 Tier 1 bytes, and 973
  to 926 offline x64 bytes. Four A/B and A/A rounds varied, all writes matched at
  offsets 0, 1 and 7, and reads allocated 0 B. The 87 targeted tests passed on each
  of .NET 8 and 10 with intrinsics on and off (`1c1e62df`).
  For a staged multidimensional array of reference-containing elements, use span copy
  to batch write barriers. Keep the unmanaged loop. Applying span copy to both cases
  repeatedly slowed the 5,000-item unmanaged fixture. The reference-only variant
  emitted `BulkMoveWithWriteBarrierInternal`, shrank its Arm64 FullOpts `Fill` from
  204 to 184 bytes, and grew offline x64 from 71 to 116. Shared IL grew from 34 to
  65 bytes. The unmanaged machine-code sizes matched. Large reference timings improved
  in three of four follow-up rounds, but A/A varied too. Allocations and wire bytes
  matched, and 75 targeted tests passed on each of .NET 8 and 10 with intrinsics on
  and off (`c144df2a`).
  Skip staging for a new heavy multidimensional array when its full allocation fits the remaining
  payload budget. Read directly into its zeroed storage and publish it only after decoding succeeds.
  Keep the growing path for sparse payloads outside the budget. Moving both cases into a no-inline
  helper kept the main 2D reader at 135 IL bytes plus a 111-byte helper, from 182 IL bytes in the
  baseline and 238 in the inline candidate. For the reference fixture, Arm64 FullOpts was
  656 to 504 + 432 bytes and offline x64 was 528 to 421 + 409. The checked-in generated C# grew
  from 6,457 to 7,531 bytes across all three ranks. At 5,000 elements the reference fixture
  allocated 5,266,744 to 2,666,720 B per read. Three of four default-JIT Arm64 rounds beat the
  baseline and all four beat the identical-build control. The sparse fixture kept its allocation
  and its timing spread overlapped the A/A spread. Both fixtures matched wire bytes and round
  tripped at offsets 0, 1 and 7. The truncated heavy payload test verifies that the old array
  stays published.
  When the caller already holds a Reader by reference, keep it by reference across the
  multidimensional heavy helper boundary. This removed the helper call's copy and write-back.
  For the reference fixture, 2D caller plus helper went from 936 to 888 Arm64 FullOpts bytes
  and 830 to 736 offline x64 bytes. Arm64 Tier 0 fell from 1,040 to 976 bytes. The sparse
  fixture went from 980 to 932 and 1,552 to
  1,446 respectively. Combined IL fell from 246 to 241 bytes and the three-rank source from
  7,531 to 7,420 bytes. Allocations and wire bytes matched at offsets 0, 1 and 7, and four
  default-JIT rounds per case showed timing differences within the A/A spread.
  Precomputing the sparse initial capacity skipped a second budget calculation but added
  a branch and argument to the shared growing helper. Its Arm64 helper pair grew from
  1,108 to 1,152 bytes without a repeatable gain. Passing only the shape by reference saved
  24 Arm64 bytes across the caller and helper, compared with 48 for passing only the reader.
  Pass the validated shape by readonly reference as well. It already lives in a stack slot
  for the shape reader. Arm64 FullOpts drops the 20-byte copy before the heavy helper and
  shrinks the 2D caller plus helper by another 24 bytes in both fixtures. Offline x64 drops
  21 bytes for the reference fixture and 12 for the sparse fixture. Arm64 Tier 0 combined
  size stayed at 976 bytes. Combined IL grew by 1 byte and three-rank generated source by
  18 bytes. Four local default-JIT rounds showed faster 5,000-element reference reads in
  several rounds, but the magnitudes varied and the short cases overlapped A/A. Allocations
  matched, and wire comparisons and round trips passed at offsets 0, 1 and 7.
- Pass the generated rank to the shared multidimensional header writer. Reserve the header once
  and write each length at its fixed offset. The old Arm64 listing called `Array.GetLength` from
  the runtime-rank loop. Four local default-JIT rounds on one-element integer arrays improved
  for ranks two through four, with zero allocations. The 64-element and near-64-KiB input-plus-output
  cases overlapped A/A after a longer warmup. All 45 wire cases matched at offsets 0, 1 and 7.
  The Arm64 2D Tier 1 write entry shrank from 1,356 to 1,164 bytes. In offline x64 the 2D formatter
  grew from 1,295 to 1,384 bytes while inlining away its call to the 199-byte shared helper.
- Take the first element of a freshly allocated, non-empty heavy multidimensional array
  through the array data reference on modern .NET. The allocation fixes the element type
  and lower bounds, so there is no multidimensional index to validate. Keep typed indexing
  on the older targets. The 2D helper shrank from 117 to 115 IL bytes, 396 to 380 Arm64
  FullOpts bytes and 376 to 347 offline x64 bytes for the reference fixture. The three-rank
  generated source grew by 550 bytes. Four local rounds overlapped A/A with identical allocations
  and wire bytes at offsets 0, 1 and 7. The write path's two length uses were already one
  load in Arm64 Tier 1.
- Pass the generated rank into the multidimensional shape match on reuse. The baseline
  loaded the runtime rank in its shared helper. The Arm64 Tier 1 2D formatter shrank
  from 832 to 696 bytes and its public read entry from 816 to 680 bytes. The offline x64
  2D formatter went from 676 to 682 bytes while inlining away the call to a 190-byte
  helper. The shared helper shrank from 85 to 78 IL bytes, and each generated reader gained
  one IL byte. Four local rounds from empty to near 64 KiB of input plus output overlapped
  A/A with zero allocations. All 45 wire and same-shape reuse checks matched at offsets
  0, 1 and 7. The last-dimension mismatch replaced the array at each rank.
- Read the trailing dimensions of 3D and 4D headers with one checked reservation after
  the existing first-length check. A null header still consumes only one length. Keep
  the single checked read for 2D. On the local 4D one-element fixture, four default-JIT
  rounds improved by 9–13% with zero allocation. A longer warmup put 2D 32–128 element
  results near the A/A spread. The split helper grew from 316 to 348 IL bytes but shrank
  from 632 to 620 Arm64 FullOpts bytes and 579 to 552 offline x64 bytes. Grouping all
  ranks made the listing smaller, but the first 2D timing series favored the split for
  short inputs. Wire comparisons matched at offsets 0, 1 and 7. Every cut in the header
  threw before replacing the target, and the four-byte null sentinel decoded at every rank.
- Test the sign and bit 30 of the OR of multidimensional lengths before the exact
  per-dimension bounds check. Arm64 uses an immediate `tst` on the common path. Keep
  the exact checks and error messages in a cold helper for negative or large dimensions.
  Comparing the OR directly to the runtime maximum slowed the one-item 2D fixture by
  about 7% in four rounds. The bit-test variant instead reduced time from 3.48–3.49 to
  3.22–3.25 ns with no allocation change. The hot method went from 348 to 286 IL bytes,
  620 to 572 Arm64 FullOpts bytes and 552 to 499 offline x64 bytes. The cold helper adds
  65 IL, 132 Arm64 and 63 x64 bytes. Wire bytes matched at offsets 0, 1 and 7, and a
  valid empty array with a dimension near the runtime limit passed the exact check.
- Multiply only the dimensions present in the wire header. Check each real prefix
  before the next multiplication, even for trailing-zero shapes. The former shared
  helper multiplied by the implicit dimensions of length one and checked each product.
  Four default-JIT rounds of 3D single-element reuse went from 3.97 to 3.73 ns with
  zero allocations. The 2D single-element case held at 3.25 ns and larger cases overlapped
  A/A. The helper grew from 286 to 313 IL bytes, 572 to 600 Arm64 FullOpts bytes, and
  499 to 535 offline x64 bytes. All 45 wire and reuse probes passed at offsets 0, 1 and 7.
- Keep the separate unsigned product bound before multiplying by the minimum wire size.
  Combining the high word of the total with the low-word byte product removed one comparison,
  but four default-JIT A/B rounds showed no repeatable benefit. IL grew from 313 to 322 bytes
  and offline x64 from 535 to 542, while Arm64 FullOpts fell from 600 to 592. Bounding the
  count by remaining bytes first instead of `uint.MaxValue` used 318 IL bytes, 600 Arm64 bytes
  and 532 offline x64 bytes. That form repeatedly slowed the single-item 2D read from around
  3.25 to 3.72 ns. Both candidates matched the baseline's 45 wire and reuse probes at offsets
  0, 1 and 7. Keep the alternatives as separate experiments for subsequent JIT diagnosis.
  Dividing the remaining byte count by the minimum wire size also passed all 45 probes.
  It used 306 IL, 596 Arm64 FullOpts and 530 offline x64 bytes, saving 7, 4 and 5 bytes
  respectively. The dynamic divisor still emitted `udiv` on Arm64 and `div` on x64.
  Four Arm64 rounds were flat for 2D and 3D single-item reuse and about 3% faster for 4D.
  Larger cases varied and x64 throughput was not measured. Retain the multiply-and-check
  baseline while the quotient variant remains an experiment.
- Advance past a complete multidimensional header once when the first length is nonnegative
  and collection lengths are uncapped. Use the original first-length check and tail read for
  truncated, null and capped inputs. Four local default-JIT rounds of 4D one-element reuse
  went from 5.89–5.95 to 5.63 ns and 2D from 3.23–3.25 to 3.18 ns. Allocation stayed at zero.
  Larger results varied. The helper grew from 313 to 366 IL bytes, 600 to 612 Arm64 FullOpts
  bytes and 535 to 549 offline x64 bytes. All 45 wire and reuse probes and 18 header probes
  passed at offsets 0, 1 and 7.
- Keep dimensions in locals until multidimensional shape validation succeeds, then
  assign the out fields once. Arm64 FullOpts no longer cleared and reloaded the output
  during parsing. An object initializer used 334 IL bytes and direct field writes used
  313, down from 366. Both emitted 592 Arm64 FullOpts bytes and 550 offline x64 bytes,
  from 612 and 549. The direct variant reduced Arm64 Tier 1 from 640 to 616 bytes
  with the public entry unchanged. Default-JIT timings overlapped A/A and allocations
  matched. The 45 wire and 18 header probes passed at offsets 0, 1 and 7.
- Keep the multidimensional header fallback inside the shape reader while its caller
  keeps the Reader in registers. Moving the tail or just the first length to a
  no-inline byref helper shrank the Arm64 Tier 1 shape reader from 616 to 504 or
  568 bytes, but grew the read entry from 676 to 756 or 804 bytes and introduced
  stack loads and stores of the cursor on the fast path. The 2D single-item probe
  slowed from 3.18 to at least 4.01 ns. Copying the Reader into the cold helper
  improved on that result but still took 3.63–3.67 ns. All three forms kept the
  45 wire and reuse probes identical at offsets 0, 1 and 7. Keep their patches
  for JIT diagnosis.
  Passing only the decoded first length to the cold helper avoided the cursor
  spill and restored the 2D single-item read to 3.18 ns. Four default-JIT rounds
  showed no repeatable latency gain. Arm64 Tier 1 shrank the shape reader and
  its caller by 16 bytes each, while the cold helper added 68 Arm64 FullOpts bytes.
  Offline x64 grew from 550 to 518 + 66 bytes, and IL from 313 to 317 + 26.
  All 45 wire and 18 header probes matched. Keep the shared Reader length check
  to avoid duplicating its rules for a size tradeoff without a speed gain.
- Keep the two scalar loads for the first two multidimensional lengths. Reading
  them with one unaligned 64-bit load requires splitting the words and preserving
  native word order on big-endian targets. Four local Arm64 rounds had unchanged
  2D short reads, a 1–2% difference on 3D, and variable 64-item results at zero
  allocations. The portable candidate grew from 313 to 345 IL bytes, 592 to 612
  Arm64 FullOpts bytes and 550 to 569 offline x64 bytes. The 45 wire and reuse
  probes passed at offsets 0, 1 and 7. Keep the packed-load patch as an experiment.
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
  Keep the dual-source loop in `NinoSimd.Interleave` and call it only for at least one vector.
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
  Dispatch wider batches at the caller boundary and leave the narrow loop out of line. Putting the AVX2
  dispatch and cursor updates in the narrow method grew the x64 int listing from 109 to 216 B. Moving
  the dispatch back to the caller kept the narrow method at 109 B and the Arm64 caller and loop at
  236 B and 128 B. Require two full vectors before entering the wide path. On AVX2, unpack per lane then
  merge the 128-bit lanes. With AVX-512, use two-source permutations and the shared 512-bit CPU policy.
  The x64 guest verified AVX2 and its fallback, including unaligned writes, while the AVX-512 listing
  showed `vpermt2w/d/q`. The guest lacks AVX-512 and no native x64 throughput was measured. Four M4 A/B
  pairs gave mixed results with zero allocations and identical output around 64 KiB (`3c871bab`).
  Keep pair interleave, deinterleave, ordering and the Burst bridge together in `NinoSimd.Pairs.cs`.
  Moving the guard with its kernel preserved the sampled IL, Arm64 Tier 1 sizes, and x64 v3/v4 sizes.
  Pin both buffers to control their actual addresses around 64 KiB. Uncontrolled timings showed large
  outliers, while the fixed-alignment A/B medians stayed within 4%. Bytes matched with SIMD on and off,
  and the real project's 582 generated files were identical (`c0025a12`).
  Finish a 16- or 32-bit remainder with the last whole 128-bit vector, overlapping a
  prefix already written by the narrow or wide loop. End the vector at the last pair.
  Write a 64-bit remainder directly because it contains only one pair. The scalar cursor
  experiment was flat. Sharing a helper with the main loop left a Tier 0 call per vector
  and slowed larger cases by 9–24%; keep the main loop in place. The final mixed-offset
  M4 probe improved 15 short, 7 int and 3 long pairs by median 16%, 7% and 5%.
  Input-plus-output cases around 64 KiB were broadly flat. Wire hashes matched; 155 of
  156 final samples allocated 0 B and one large sample had a transient allocation.
  The kernel moved from 193 to 190 IL bytes plus a 64 B helper. Arm64 FullOpts fell
  from 128 to 112 B for 16/32-bit lanes and to 88 B for 64-bit lanes. x64 cross-JIT
  MinOpts grew by 30 B per lane width; no native x64 timing was measured. Both .NET
  suites, scalar fallback and the unaligned destination sentinels passed (`281f03fd`).
  On the AVX2 path, cover the remainder with one or two 128-bit zips instead of
  calling the general narrow loop. The x64 emulated guest supported AVX2 and ran
  four default-JIT pairs. Medians for 17 and 21 int pairs improved 4% and 8%, while
  exact-vector and 64 KiB controls were noisy. Wire bytes, destination sentinels
  and zero allocations matched. AVX2-disabled execution and the Arm64 112 B
  narrow listing matched. The shared dispatcher IL grew from 138 to 219 B,
  x64 guest FullOpts from 112 to 177 B, and the AVX2 main loop stayed at 79 B.
  The separate 512-to-256 half-vector trial grew x64 cross-JIT MinOpts from
  361 to 455 B per width. Retain the existing AVX-512 tail until that variant
  can be timed on supporting hardware (`85789f51`).
- Split a checked, ordered raw `SortedList<int, int>` input directly into its two arrays on .NET 8+.
  For full wide read batches, use AVX2 lane shuffles plus 64-bit reordering or AVX-512 two-source
  dword permutes. Keep the exact ISA test in the existing out-of-line entry so Arm64 Tier 0 drops it.
  Let the wide helper call that entry for the remainder, which is below the wide threshold. Returning the
  processed count to the narrow entry grew its x64 listing to 205 B. Keeping the remainder in the wide
  helper brought it to 123 B against the 110 B baseline, with Arm64 Tier 0 and Tier 1 unchanged at
  488 and 120 B. The AVX2 guest matched wire bytes, including unaligned inputs and sentinels, and the
  offset-zero 4,095-pair guest control improved in all four rounds. Native x64 throughput and native
  AVX-512 execution remain unmeasured. Local M4 fallback timings varied without a repeatable regression
  (`a07cde43`).
  Keep the order check before publishing the arrays and use the same wire block for out-of-order insertion.
  Fuse the order check and unzip on Arm64 when both arrays already fit. Write only into an empty
  list and publish the size after every key is validated. A rejected block goes through the same
  comparer fallback, including the indexer overwrite before a duplicate is reported. At offsets
  zero and one, four M4 A/B pairs cut 32-pair reuse latency by about a third and 257-, 4,095- and
  4,097-pair latency by about half at zero allocation. Fresh growth retained the separate order check
  and allocations. The fused OSR loop was 240 B versus 200 plus 136 B for the two original loops.
  The caller grew from 154 to 204 IL bytes and the x64 v3 cross listing moved from 402 to 401 B.
  Wire, scalar fallback and all 582 generated files matched (`86e4efb7`).
  Share the Arm64 unzip across signed and unsigned integer lanes of 8, 16, 32 and 64 bits. Use the
  underlying integral order for enum keys with the default comparer. Keep the lane constants inside
  the loop and optimize from the first call. The first generic OSR version spilled the predecessor
  vector. With `AggressiveOptimization`, the sampled Arm64 loops were 224–236 B and the x64 v3
  fallback remained 401 B (`6f64a961`, `bae2d41b`).
  Keep custom unmanaged keys in their typed comparer. A four-byte `ReverseIndex` sorted in reverse
  integer order with the default comparer and matched the baseline bytes in 1,376 round trips. Split
  the staged array after typed validation, since comparison can run user code and the input is no
  longer the snapshot. Reuse `DeinterleaveInt32` when the shared block plan proves identity layout
  for two four-byte fields. An unrestricted dispatch slowed the 4,097-pair aligned probe. Limit it
  to 64–2,048 pairs, where the wire, pool bucket and exact-size destination are at most 48 KiB
  plus the header. The 64-, 65- and 2,048-pair reuse probes improved in all four M4 rounds at both
  offsets with no steady allocation and identical bytes. The caller grew from 189 to 260 IL bytes
  and from 456 to 488 Arm64 Tier 1 bytes. x64 cross-JIT grew from 549 to 649 B (`2d173116`).
  Keep the two-pass check when the arrays need to grow. A speculative fused growth candidate cut ordered
  M4 reads by 26–51% at the same allocations and preserved wire bytes and capacity. Checking the two
  edges avoided extra allocations for descending inputs and a swap at the end, but a middle swap still
  cost about 2.1 KB at 257 pairs or 32.8 KB at 4,097 pairs and slowed in all four rounds. Untrusted mode
  retained its original allocations. The candidate was withdrawn.
  Put the layout and comparer checks in an out-of-line helper so short reads keep the original rented-pair
  method's IL. The local Arm64 2,047- and 8,193-pair reuse probes cut elapsed time by roughly 35–45%.
  Validate four signed keys at a time for 32 or more pairs. Share the even-lane extraction with
  deinterleave, align the previous last key with the next three, and check the scalar tail. Keep
  the scalar check inlined for short inputs and unsupported instruction sets. Vectorizing every
  eligible read made the 17-pair unordered case slower. With the gate, that case stayed within
  control variation and the 3,967-, 4,223- and 8,193-pair reuse probes took less time across all
  four local rounds at zero allocation. Check duplicate keys at vector boundaries and in the tail.
  The 16-pair probe occasionally stayed slow during short warmup. With a longer warmup, timings
  varied within the controls with SIMD on and off. The generated source and wire samples matched.
  Measure allocation thresholds separately before using uninitialized arrays for this split.
  The two-array growth candidate passed the order and byte checks, but 1,536 pairs crossed the
  6 KiB per-array threshold and took over five times as long as the paired baseline in all four
  local rounds. Allocation size stayed at 12,400 bytes per read. The cross listing showed calls
  to the uninitialized allocation worker instead of the usual new-array helper. Larger samples
  varied. Compare a zeroed control with the same publication order and call shape.
  The zeroed control removed the threshold slowdown. At 24,576 and 32,768 pairs, the uninitialized
  worker also lost in all four paired local rounds, with unchanged output bytes and allocation size.
  Keep zeroed arrays for this path.
  Keep the paired vector loads and unzips for the split on Apple Arm64. The gated and compact
  `.NET 10` `ld2` probes both took longer at 4,223 pairs in four isolated local rounds. The
  existing Tier 1 loop already uses `ldp`, `uzp1` and `uzp2`. The compact replacement grew
  the entry from 120 to 152 Arm64 bytes and from 169 to 288 IL bytes. Compare the kernel alone
  as well as the public path, whose order check and allocation can hide the split cost.
- Compare long Latin-1 input to an existing string by widening bytes and comparing UTF-16 lanes. Start with the last
  complete vector to reject tail edits early. Keep this out of the short-string comparison, and place boundary tests
  at the end of a guarded page to catch a load past either end of the input.
- Continue Latin-1 narrowing at the prefix written by `Ascii.FromUtf16`, overlapping the final vector if needed.
  When the first unwritten character exceeds Latin-1, take the UTF-16 path immediately.
  For 32–63 chars, call an outlined 256-bit writer directly without the ASCII setup. Check the first
  and last 32-char windows and write the header only after both succeed. Inlining the wide work grew the
  x64 narrow entry from 208 to 363 B and regressed the 17–31-char controls. Moving only the gate to the
  narrow entry also regressed the small Arm64 controls despite an unchanged FullOpts code size. Dispatch
  from the caller and keep the narrow entry intact. After the range check, AVX2 saturating pack needs no
  input masks. It reduced the new x64 helper from 167 to 142 B and the long writer from 563 to 546 B.
  Four local x64 guest A/B pairs favored the wide path for most 32–63-char inputs. The 47-char ASCII case
  at offset one regressed in the guest. Arm64 controls showed no stable regression and the two 64 KiB
  cases were noisy. Wire bytes matched with AVX2 on and off, and all timed loops allocated 0 B. Do not
  extrapolate guest timings to native x64 (`2202f782`).
  For a suffix of at least 128 chars, outline the 512-bit writer and keep CPU preference inside that entry.
  Keep the AVX2 fallback loop intact. Sharing its loop with the wider path changed guest code shape and hurt
  some 64- and 127-char controls; splitting out the two ends also grew the v3 caller. With the separate
  wide entry, the x64 v3 caller's instructions match the baseline at 543 B and Arm64 stays at 476 B.
  The v4 caller is 639 B versus 561 B, with a 260 B helper that emits two `vpmovwb` operations per 64 chars.
  Verify the portable kernel directly across offsets and tails. The AVX2 guest and M4 exercise the fallbacks;
  neither executes native AVX-512. Their timed loops allocated 0 B, and unstable guest timings around the
  threshold do not establish native throughput (`f9ce316e`).
  Use the exact AVX-512BW support check at the write entry too. Arm64 instrumented Tier 0
  `NarrowVector` fell from 928 to 776 B and Tier 1 from 748 to 712 B, with the same 218 B IL.
  The x64 cross-JIT MinOpts listing fell from 644 to 637 B. Four local timed pairs overlapped the
  controls at zero allocations, and the byte comparison passed with SIMD enabled and disabled
  (`a74d9910`).
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
  For AVX-512BW, widen two 32-byte loads directly with the ISA's conversion and align the middle stores.
  A 64-byte load and tuple widening emitted a vector copy and half extraction. The direct conversion folds
  both loads into `vpmovzxbw` and reduced the v4 helper from 177 to 158 B. Gate the outlined call with the
  exact ISA intrinsic, then apply shared CPU preference inside. An inlinable wrapper around the hardware
  test still grew Arm64 instrumented Tier 0 from 692 to 824 B, while Tier 1 did not grow. The exact ISA
  test restored Tier 0 size and 31–127-char timings. The M4 and AVX2 guest verify the fallback and portable
  kernel's bytes, including non-aligned inputs and tails. Native AVX-512 throughput remains unmeasured
  (`663b1861`).
  Reuse the folded 32-byte widen load in the 512-bit reuse comparison. Crossgen2 v4 lowered both loads
  to memory-operand `vpmovzxbw`, reducing the compare block from 75 to 57 B and its IL from 73 to 64 B.
  The widening helper retained folded loads and fell from 158 to 156 B. Portable block tests covered
  every mismatch lane and the public wire bytes matched with SIMD on and off. M4 fallback timings
  varied with process profile, and native AVX-512 timing was unavailable (`4d10592c`).
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
- Keep the fixed Arm64 head and tail in the 33–63-character UTF-16 fallback with
  a known one-byte header. Putting the range in the shared UTF-16 writer slowed
  the 65-character control, including after reusing the small-copy dispatcher.
  The one-byte branch reduced M4 default-JIT medians by 27–36% across four pairs
  with first/last non-Latin-1 characters and offsets zero/one. The 32-, 64- and
  65-character and ASCII controls were within the local spread, as were cases
  around the 64 KiB working set. All 13 wire comparisons matched at 0 B allocation.
  WriteLong grew from 81 to 131 IL bytes and 256 to 336 Arm64 FullOpts bytes;
  the caller stayed at 612 B and the two x64 ReadyToRun listings matched exactly.
  Both framework suites, scalar string checks and netstandard builds passed
  (`c3fa2df1`).
- Copy large UTF-16 `StringBuilder` values directly into the reserved span with `CopyTo` (1b2e3404).
  `GetChunks` can allocate a chunk index, even for a short but unusually fragmented builder. Keep the modern
  small-write loop where it measured faster, and advance the reader by the exact byte count already checked.
- Refill a matching-length `BitArray` through its public byte span after validating the payload (cb6e950e).
  Setting its length to the same value clears the unused bits and invalidates existing enumerators. A single
  copy body for fresh and reused instances avoided duplicating the Arm64 copy code.
  Reuse the fixed Arm64 head and tail for a disjoint 65–128-byte refill, behind the existing
  overlap check. Four default-JIT M4 pairs gave 15–26% lower medians at the tested sizes and
  offsets. The fresh and 64 KiB controls had no stable change. Fresh allocation matched and
  reuse allocated 0 B. All ten wire comparisons matched; the overlapping-buffer tests covered
  both endpoints. ReadBits grew from 189 to 234 IL bytes and 616 to 660 Arm64 FullOpts bytes.
  Linux x64 offline ReadyToRun stayed at 440 B; it was not timed (`6defca8c`).
  Use the same kernel on writes after the 64-byte split, and mask the final byte as before.
  Adding a separate unsigned range check before the shared copy slowed the 129-byte control.
  Dispatching larger Arm64 writes directly to CopyOver64 removed the repeatable regression.
  Four M4 pairs gave 21–37% lower medians for 65–128 bytes with mixed offsets and tail masks;
  the 96-byte follow-up also improved in all four pairs. Small and cache-boundary controls
  varied. All 12 wire cases matched with 0 B allocation. Serialize grew from 161 to 210 IL
  bytes and 828 to 872 Arm64 FullOpts bytes; its caller stayed 196 B and offline x64 stayed
  694 B. Dirty-tail and unaligned writer tests passed (`054e6900`).
  Share the ReadyToRun-friendly Arm64 gate between the two BitArray copy paths.
  The getter adds 15 IL bytes and each caller saves 7. Arm64 FullOpts retained
  872/660 bytes and offline x64 retained 694/440 bytes. Four M4 pairs showed
  no slowdown in the 96- and 129-byte writes, with zero allocation and identical
  wire bytes. Applying the gate to string and raw-array callers preserved sampled
  FullOpts sizes but moved default-JIT controls: 23-character Latin-1 reuse slowed
  until both string call sites were restored, and fresh 96-byte array writes
  slowed by about 5%. Leave those gates local (`78f93168`).
- Keep per-segment reservations for general fixed-stride ReadOnlySequence writes.
  One counted reservation followed by bounded segment writes saved about 10% on the
  2- and 4-segment Int32 probes but slowed a 16-segment small input. Outlining and
  forcing FullOpts made the 2- and 4-segment probes much faster against Tier 0 but
  almost doubled the 16-segment case: the original reached PGO Tier 1 (1,544 bytes),
  while the outlined loop could not. After restoring normal tiering, the 4-segment
  32-element probe improved by 4–6%, but 2 segments, 16 segments and the cases
  straddling a 64 KiB input/output footprint were flat or noisy. The final trial
  moved from 118 IL bytes to 137 plus 121; Arm64 FullOpts from 2,432 to 1,304 plus
  1,108 bytes; emulated x64 FullOpts from 2,855 to 1,397 plus 1,041 bytes. All eight
  wire comparisons matched at zero steady allocation. Do not trade the mature
  PGO loop for this general reservation split.
  Keep the per-segment Writer local for variable-width value formatters as well.
  Inlining their element loops into the sequence writer helped tiny Int32 nullable
  segments, but the all-length version slowed 4,096 items in eight segments by
  about 43%. Sharing the element loop also moved the string control. Limiting the
  byref loop to up to four items and selecting it in an outlined method recovered
  the long cases, and four pairs improved 2 and 16 short segments by medians of
  11% and 27%. Four eight-item segments still varied, with a 33% slower median
  and wide same-binary controls. On that path instrumented Arm64 Tier 0 grew from
  392 to 664 bytes. For 16 segments, Tier 1 moved from 1,512 to 1,000 plus 1,132
  bytes; the emulated x64 FullOpts path grew from 5,852 to 2,919 plus 5,576,
  excluding the unchanged per-segment callee. Shared IL grew from 118 to 160
  plus 122 bytes. All 11 wire comparisons matched at 0 B. Keep this experiment
  out of the general path until it avoids perturbing the fallback.
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
  Copy whole words in the final partial period of the scalar mask loop, leaving fewer than
  eight bytes for its byte tail. For a padded 12-byte element with SIMD disabled, longer
  alternating batches improved the 3-element case by 11–23% and the 17-element case by
  11–12%. The byte-identical controls varied by at most 9% for those cases. Two elements
  and SIMD timings had no repeatable gain. `NinoMask.Copy` grew from 259 to 318 IL bytes and
  by 56 bytes in the Arm64 scalar Tier 1 and x64 cross-JIT listings. Dirty-padding wire
  checks matched at offsets 0, 1 and 7, and targeted tests passed (`51e30efb`).
  Share the full-word scalar loop across complete and partial periods. Advancing and wrapping
  the mask word index reduced `Copy` from 318 to 226 IL bytes and by 88 bytes in the Arm64
  scalar Tier 1 and x64 cross-JIT listings. For 12-byte elements with SIMD disabled, both
  runs straddling 64 KiB of input, output and mask improved by 8–13%, with A/A within 5%.
  Short and SIMD timings varied. Dirty-padding bytes matched at offsets 0, 1 and 7, and
  targeted and scalar tests passed (`a7927d44`).
  Keep the predictable scalar mask wrap branch. Expressing the wrap as a ternary still
  emitted a branch and did not improve timing. A sign-mask expression removed the branch
  and cut the scalar body from 344 to 336 Arm64 Tier 1 bytes and from 284 to 280 in the x64
  cross listing, but serialized 2,665 and 2,821 padded 12-byte elements in about three
  times the baseline time with SIMD disabled. Its loop carried an extra arithmetic
  dependency into the next mask load. Four A/A rounds stayed within 6% for those cases.
  Wire and dirty-padding checks matched, and targeted tests passed. Keep both as probes.
  Check both address updates and the loop counter before replacing the scalar mask index
  with two byref cursors. Arm64 used post-index loads and stores, but `Copy` grew from
  226 to 250 IL bytes and 344 to 348 Arm64 Tier 1 bytes. Counting the remaining bytes
  simplified the tail and used 245 IL bytes, 356 Arm64 bytes and 280 x64 cross-JIT bytes
  versus 284 at baseline. Neither variant improved short or 64 KiB boundary workloads
  repeatably, and the countdown was slower in all four default-SIMD 17-element rounds.
  Keep the indexed loop. Wire and dirty-padding checks and targeted tests passed.
  Unroll the three-word scalar mask period and load all three words before storing.
  Arm64 paired the first two loads and stores as `ldp` and `stp`, using eight fewer
  bytes than the interleaved variant. Compared with the shared loop, IL grew from 226 to
  383 bytes, Arm64 scalar Tier 1 from 344 to 432, and the x64 cross listing from 284 to 380.
  With SIMD disabled, the 2,665- and 2,821-element 12-byte probes took 38–56% less time
  and the 1,420-element 24-byte probe 44–55% less in four rounds. Default SIMD timings
  varied. Dirty-padding checks matched, and both targeted suites, scalar tests and
  netstandard builds passed (`7a3cf2c6`).
  Keep indexed addressing for the three-word period. Bumping both byrefs preserved
  `ldp` and `stp` but left two address increments, grew IL from 383 to 399 bytes
  and Arm64 scalar Tier 1 from 432 to 440, and left the x64 cross listing at 380.
  Moving the paired operations last did not fold the increments and made the 2,821-item
  scalar case 63–140% slower across four rounds. The ordinary cursor variant had no
  repeatable large-input gain. Keep both patches as probes. Wire and dirty-padding
  checks matched, and targeted and scalar tests and netstandard builds passed.
  Keep the three-word path inside `Copy` for now. Outlining only that loop increased
  the default Arm64 Tier 1 entry from 748 to 764 bytes and slowed the 17-element SIMD
  case by 13–23% in four rounds. Outlining the entire scalar path reduced the default
  entry to 564 bytes, but the scalar entry plus helper grew from 432 to 492 bytes and
  the x64 cross listing from 380 to 424 bytes. The apparent 2,665-element SIMD gain
  did not repeat with an identical-binary control. Keep both patches as probes.
  Wire and dirty-padding checks matched, and the scalar outline passed targeted tests.
  Use the same paired shape for the two-word period. With SIMD disabled, the padded
  16-byte probe took 23–29% less time at 17 elements and 37–67% less across the two
  sizes straddling 64 KiB. Default-SIMD and existing three-word timings varied on
  replay. `Copy` grew from 383 to 500 IL bytes, 432 to 492 Arm64 scalar Tier 1 bytes,
  and 380 to 444 in the x64 cross listing. Dirty-padding bytes matched, and targeted
  and scalar tests and both netstandard builds passed (`f3d6bd81`).
  Reuse the paired loop for one-word periods by applying its mask to both words.
  A separate one-word loop saved 16–26% on the large scalar probes but added another
  body. Reusing the pair saved 39–48% across the same two sizes and 11–18% at 17
  elements, with no repeatable slowdown on the 16-byte or SIMD probes. `Copy` grew
  from 500 to 508 IL bytes, from 500 to 516 Arm64 scalar Tier 1 bytes and from 444 to
  460 in the x64 cross listing. Dirty-padding checks and targeted tests passed (`4c8a1e11`).
  Handle one to two native vectors in the masked copy with the head and, when distinct, the
  tail. The tail starts within the first mask period, so it can load the mask at that
  offset directly. On M4, four default-JIT pairs and a repeated series with an identical-binary
  control improved the 16- and 24-byte writes of padded 8-byte items by about 3–5% by median.
  The 128-item and near-64 KiB input/output controls varied. The generic method grew from
  687 to 758 IL bytes, 424 to 456 Arm64 FullOpts bytes, and 292 to 324 emulated x64 AVX2
  bytes. Dirty-padding output matched in 39 M4 cases and five emulated x64 cases, with
  zero steady-state allocation. The 512-bit specialization passed 57 software checks for
  dirty padding and destination bounds. Native AVX-512 throughput was unmeasured.
  Let the final store own the vector at the end of a one-vector mask period. Skipping the
  tail with an extra branch slowed a 48-byte block with a 12-byte stride by about 8%, even
  when the branch was limited to one-vector periods. Make the aligned body's bound exclusive
  instead, including its Arm64 paired loop. Four M4 pairs improved the 48-byte aligned kernel
  by 4–8%, with an identical-binary control near flat by median. The public writer, the
  12-byte stride and the 64 KiB boundary had mixed timing. IL grew from 758 to 761 bytes,
  with Arm64 and emulated AVX2 FullOpts unchanged at 456 and 324 bytes. Wire bytes and
  destination sentinels matched across 128-bit, emulated AVX2 and software 512-bit probes.
  Keep the inclusive loop when a mask spans several vectors. Making it exclusive took
  27–33% less time for an aligned 192-byte kernel probe but increased the aligned 48-byte
  time by 8% by median across four M4 pairs. Using the tail's absolute offset in the first
  period did not fix the 48-byte result and increased the aligned 60-byte time by 15%.
  Keeping the inclusive bound within the first period removed the repeatable 48-byte
  penalty, but the 192-byte public writer and 64 KiB boundary gains did not repeat.
  The three candidates used 761, 774 and 777 IL bytes against 761, with Arm64 FullOpts
  at 456, 472 and 460 bytes against 456 and emulated AVX2 at 324, 334 and 333 against
  324. All matched dirty-padding wire bytes and steady allocations. The candidates were withdrawn.
  Keep the initial phase check on the multi-vector loop for now. Its condition cannot fire
  because the initial offset is at most one vector and the period is strictly wider. Removing
  it nevertheless changed Arm64 register choice and loop alignment. Four longer M4 default-JIT
  pairs slowed the aligned 192-byte kernel by 31–68%, versus -12% to +24% for the identical-binary
  control. The unaligned and near-64 KiB cases varied. The change removed 13 IL bytes and 7 bytes
  from the emulated AVX2 FullOpts listing, with Arm64 still at 456 bytes. All 54 local wire cases
  and three emulated AVX2 cases matched, including the software 512-bit path and destination
  sentinels. Steady-state allocation was zero. The candidate was withdrawn.
  Build repeated mask tables by doubling a filled prefix. The last copy can be partial,
  and each earlier copy preserves the mask phase. Four M4 default-JIT pairs reduced warmed
  `NinoMask.Create` medians by 36–51% for 8-, 12- and 24-byte masks, with equal allocations.
  The one-byte repeat and first public write varied within their identical-binary controls.
  `Repeat` grew from 57 to 104 IL bytes, 244 to 396 Arm64 FullOpts bytes, and 129 to 200
  emulated x64 bytes. Checks matched all 1,536 repeated tables across 8-, 16-, 32- and
  64-byte widths and all 21 padded wire outputs at offsets zero, one and seven.
  Keep the temporary byte table for scalar words for now. Filling the final `ulong[]`
  through its byte view saved 32–48 B for 8–24-byte masks and 544 B for a 65-byte mask,
  but four longer M4 pairs without intrinsics slowed 12-byte construction by 23–27%.
  Inlining the shared span filler did not remove that regression. Replacing the span
  copies with disjoint block copies made the 8-, 12- and 24-byte scalar cases slower
  in all four pairs despite faster SIMD construction in several cases. The first
  public write saved 48 B and had mixed timing. The span and block variants used
  340 and 347 IL bytes versus 340, and the constructor and emitted helpers went
  from 1,276 to 948 and 832 Arm64 bytes and from 820 to 651 and 613 emulated x64
  bytes. All 192 mask sizes and 21 wire outputs matched. The candidates were withdrawn.
  Keep the modulo loop for the table period. A lowest-set-bit formula and a doubling
  loop both sped up repeated 12-byte masks but slowed the 65-byte mask at a 64-byte
  width by 5–25% across four M4 pairs per variant. Keeping the additions and replacing
  modulo with a bit test still slowed that case by 13–16%, versus 1–6% for the
  identical-binary control. IL was 104 bytes at baseline and 110, 104 and 106 in
  the trials. Arm64 FullOpts fell from 396 to 344, 356 and 352 bytes, and emulated
  x64 went from 200 to 201, 193 and 192. Allocations, 4,096 repeated tables and 21
  padded wire outputs matched. First-write timings varied. The candidates were withdrawn.
  Keep the write countdown when Tier 1 already emits a single cursor loop. On `BlockWaypoint[]`,
  a pretested index with the same byrefs reduced `WriteEach` from 424 to 414 IL bytes and reduced
  Arm64 Tier 0 by 60 bytes. Scalar Tier 1 stayed at 92 bytes and SIMD Tier 1 grew from 196 to
  200. The sampled x64 FullOpts cross listings went from 244 to 245 and stayed at 276 bytes
  for the second compilation. Small and mixed timings showed no repeatable gain. A large SIMD
  mix initially favored the candidate, but four replay rounds with a byte-identical control
  did not reproduce it. Wire bytes matched at offsets 0, 1 and 7, writes allocated 0 B,
  and 22 targeted tests passed on each of .NET 8 and .NET 10.
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
  on EPYC 9V45 and 0.97 on 9V74, but 1.44 times on Xeon Platinum 8370C (Ice Lake): `NinoSimd.Prefer512` takes AMD and
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
- On Ice Lake, mask padded raw structs with 32-byte vectors (`NinoSimd.Prefer512` gates `NinoMask`'s 64-byte masks
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
