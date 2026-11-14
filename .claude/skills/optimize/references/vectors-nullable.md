# Nullable slots (.NET)

Part of the `optimize` skill ([vectors.md](vectors.md) holds the general loop rules): the vector loops over nullable
slots, from the presence-flag scan shared with strided bools to the nullable bool reads and writes. Read it before
changing `NinoNullable`-style raw readers or writers, their flag validator or their width dispatch.

Platform scope: a bullet that names a GitHub runner run (or "runners") was measured on every runner CPU. The others
were timed on a local Apple M4 (Arm64, default JIT) or, where they say so, on an emulated x64 AVX2 guest; their x64
evidence is listings only (Crossgen2, cross-JIT), and the rules lists mark them "M4". Measure on native x64 before
carrying such a threshold or verdict there (`benchmark`).

Contents: [Rules](#rules), [Layout and presence flags](#layout-and-presence-flags), [Fresh reads](#fresh-reads),
[Writes](#writes), [Nullable bool reads](#nullable-bool-reads).

## Rules

- Check the nullable slot layout before adding type-specific SIMD: the Raw path already bulk-copies matching unmanaged
  values (M4).
- Scan raw presence flags in 128-byte iterations of four-vector groups above a 128-item gate, through one inlined scan
  compiled with `AggressiveOptimization` and specialized for 512/256/128-bit vectors selected outside the loop (M4
  timings; x64 listings only).
- Share that validator with strided bools, bounded at the last flag; keep its scalar tail with the early exit; unroll
  two- and three-element scans; keep the indexed scalar conversion (M4).
- Fuse the flag scan and copy for fresh raw nullable arrays of at least 128 slots with strides of 8 or 16, inline the
  fresh entry into `ReadNew`, broadcast the AVX-512 mask with a lane shuffle (M4; native x64 and AVX-512 unmeasured).
- Write nullable bools without the converter: lane masks from presence bits in 512/256/128-bit groups, 64-bit groups
  for four to seven slots on Arm64, one word store a slot for one to three, an overlapping final vector; keep the bulk
  clear for converted writes (M4; native AVX-512 unmeasured).
- Read nullable bools through the checked primitive up to 1,024 elements, and from eight slots through the slot-mask
  vectors (64-bit groups for four to seven, a scalar helper for one) with the same slot policies at 512/256/128 bits;
  keep the per-vector read (M4; native x64 unmeasured).
- Give x64 and x86 the four-slot groups Arm64 takes as `Vector64`, as a 64-bit word (`BoolSlotsWord`: the presence
  bit spread over its slot by `(bits << 16) - bits`, no 64-bit multiplication for 32-bit x86), through the same
  generic group loops: 4 to 7 slots, and every count on a host without vectors. Before, 4-7 values went through the
  general per-element loop on x64 (`InitBlock` plus `NinoLayout.Write` each) and short reads through the converter
  (unmeasured; the netstandard builds keep their loops, where Mono turns unaligned 64-bit loads into memcpy calls).

- Write raw `T?` runs (strides 2, 4, 8, 16, 24) with absent values zeroed in the same single pass as the padding,
  as wide as the masked copy: AVX-512 test-into-mask plus masked AND, Arm64 structure loads; never a second pass
  over the elements. Runner A/B 43264755384 showed the first form of this fix 2x slower than the masked copy (Writes).

## Layout and presence flags

- Check the nullable slot layout before adding type-specific SIMD. The shared Raw path already bulk-copies matching
  unmanaged values and uses the shared padding mask. On M4, byte/sbyte, short/ushort/char, int/uint/float,
  long/ulong/double, four enum widths and four custom index structs all took Raw with strides 2/4/8/16. A packed
  five-byte value also took Raw, with stride 6. These 21 nullable cases, including bool, round-tripped from offset one
  and reused their destinations with 0 B measured allocation. Equal value widths alone do not prove equal nullable
  slot offsets or padding. Presence checks and value conversion have separate SIMD gates.
- Batch raw nullable presence checks in 128-byte iterations (two groups of four vector loads) for large payloads with
  strides of 2, 4, 8 or 16, behind a 128-item gate, and keep the existing dispatch; the scalar scan stays for short
  inputs, unsupported strides and the original diagnostic. M4 timings; x64 listings only.
  - Batch raw nullable presence checks in 64-byte vector groups for large payloads with strides of 2, 4, 8 or 16.
    Unlike strided pair fields, each nullable flag is followed by a full slot, so whole-vector loads stay within the
    validated payload. Use the scalar scan for short inputs, unsupported strides, and the original invalid-flag
    diagnostic. The first one-vector loop regressed both 64-item and cache-sized integer arrays. The 64-byte loop with
    a 128-item gate improved the unaligned 4,095- and 4,096-item reuse cases in all four local default-JIT rounds
    against both baselines, at 0 B allocated. Short inputs varied. The vector helper adds 320 IL bytes, 272 Arm64
    FullOpts bytes, and 204 x64 cross-JIT bytes.
  - Process 128 bytes per full iteration with two groups of four vector loads. Compared with the 64-byte loop, all
    four unaligned `int?[]` reuse rounds improved against both baselines on each side of the 64 KiB working set. Time
    fell by 19–47% at 4,095 items and 20–39% at 4,096 items versus the faster baseline in each round. Shorter samples
    varied and allocation stayed at 0 B. The helper grew from 320 to 493 IL bytes, 272 to 336 Arm64 FullOpts bytes,
    and 204 to 295 x64 cross-JIT bytes. The wire samples and scalar fallback matched.
  - Keep the 128-byte loop as the common path. A 256-byte loop improved 16,384-item reads but varied near 64 KiB.
    Gating that loop at 64 KiB of input still slowed 128-, 4,095-, and 4,096-item reads against both baselines in all
    four rounds, despite those cases taking the 128-byte loop. The gated helper and its inline group reader went from
    493 to 566 IL bytes, 336 to 464 Arm64 FullOpts bytes, and 295 to 483 x64 cross-JIT bytes. A separate four-round
    comparison reproduced the 128-byte loop's gain over the 64-byte loop for 4,096 and 16,384 items. If revisiting the
    wider loop, isolate its machine-code layout from the common path and measure both again.
  - The out-of-line 256-byte variant kept the Arm64 FullOpts instructions of the 128-byte helper identical and kept
    its x64 cross-JIT size at 295 bytes. It still slowed 4,095-item reads against both baselines in all four local
    rounds, and the 16,384-item timing changed direction. The flag entry and new wide path added 174 IL bytes
    including shared helpers, 356 Arm64 FullOpts bytes, and 303 x64 cross-JIT bytes. Keep the existing dispatch.
- Share the 128-, 64-, and 16-byte loops through one inlined scan and pass constant offsets to the four-load helper. A
  pre-offset reference introduced an address add on Arm64. The helper calls survived in instrumented Tier 0 and slowed
  the scan. Compile the out-of-line validator with `AggressiveOptimization` so it starts with both helpers inlined.
  The original loop with the same attribute confirmed that tiering accounts for most of the gain. On Apple Silicon
  with 128-byte cache lines, pinning the wire start at offsets 0, 63, and 127 gave 43–66% less time across four rounds
  on each side of the 64 KiB working set. Allocation remained zero. The first 128-item read median rose from 1.62 to
  1.74 ms. With all widths optimized from entry, 64-byte groups generally trailed 128, and 256 did not consistently
  beat 128 at those offsets. Use line size as a benchmark input; keep the batch width separate from native SIMD width.
  The validator and helpers shrank from 493 to 354 IL bytes, with 336 Arm64 FullOpts bytes on both sides. x64
  cross-JIT went from 295 to 301 bytes with the optimization attribute removed for the listing, since crossgen2
  skipped the annotated method.
- Keep the indexed scalar nullable conversion read. A separate scalar conversion probe replaced the indexed nullable
  read with two cursors. It removed the stride shift and reduced the `bool?` helper from 212 to 196 Arm64 FullOpts
  bytes and 261 to 247 x64 bytes, but short reuse readings often regressed. The wire and generated files matched.
- Specialize the same scan for native 128-, 256-, and 512-bit vectors through inlined load, OR, and mask operations.
  Select the width outside the loop with JIT-foldable acceleration checks and the existing `Wide512` policy. Keep the
  128-byte scan batch independent of vector width. On Arm64 the 128-bit specialization stayed at 336 FullOpts bytes
  with no repeatable local timing change. x64 cross-JIT emitted XMM, YMM, and ZMM variants of 296, 277, and 271 bytes,
  respectively. Native x64 timings are still needed. A direct probe validated all widths for the four strides,
  including unaligned loads and invalid flags in tails. Spell out the 512-to-256-to-128 selection. The hardware gates
  fold and the remaining x64 v4 policy branch changes direction without growing the entry. Arm64 measured on par, at
  152 FullOpts bytes, and x64 cross-JIT entries stayed at 84/84/112 bytes for v2/v3/v4. Use one 128-bit pattern for
  the repeated mask. Passing two scalar words instead grew the Arm64 scan from 336 to 368 bytes and lost to the faster
  baseline in all four 4,095-item reads.
- Share the three-width validator with strided bools. Pass a byte bound that ends at the last flag for a field inside
  a record; only nullable payloads guarantee the entire final stride is readable. Preserve the scalar diagnostic and
  unsupported-stride fallback. In four local Arm64 rounds, reused `List<(byte, bool)>` took 30–35% less time at 128
  items and 90–95% less at 16,384 against the faster duplicate baseline, at 0 B allocated. Nullable timings varied.
  The strided bool entry grew from 68 to 160 Arm64 FullOpts bytes and 25 to 91 x64 cross-JIT bytes. All three vector
  widths passed direct last-flag and full-slot probes.
- Keep the scalar tail for the shared validator. An overlapping load ending on a whole stride preserved mask phase,
  and a strided field ending at its last flag still needed one scalar check. Folding the tail vector into the
  accumulator, then trying a separate tail check with an exact-end fast return, both lost in public zero-offset
  `long?[]` reuse at 4,096 items. The separate tail check improved the direct 128-item stride-two bool check in all
  four M4 rounds at both offsets. It increased the validator from 217 to 256 IL bytes and the Arm64 128-bit instance
  from 328 to 380 B. The 256- and 512-bit x64 cross listings grew from 410 to 512 B and 437 to 543 B in MinOpts. The
  x64 128-bit listings used different optimization tiers. All 6,144 width/stride/offset cases and their invalid-tail
  mutations passed, with matching wire hashes and zero steady allocation. Both candidates were withdrawn.
- Keep the early exit in the scalar tail. OR-ing the flags into one scalar mask removes the per-flag check but left
  four M4 default-JIT pairs mixed across strides 2, 4, 8 and 16. The 4,095-item stride-eight case at offset one took
  longer against the paired baseline in all four rounds, but identical-binary controls also varied. Wire, round trips,
  invalid-tail and truncation results matched at zero steady allocation. The branchless tail moved the validator from
  217 to 225 IL bytes and from 336 to 328 Arm64 FullOpts bytes. Emulated x64 grew from 288 to 294 B for 128-bit
  vectors and from 266 to 286 B for 256-bit vectors. An overlapping load ending exactly at the byte bound also lacked
  a stable nullable win and is valid only for whole slots, not the shared strided bool bound.
- Unroll two- and three-element presence scans in the shared validator. Keep the OR reduction and validate every flag
  before writing any slots. Four M4 default-JIT pairs at offsets zero and one improved nullable bool array medians by
  19–20% and nullable Int32 list medians by 14–18%. Four- and 128-element bool arrays and 16-element lists stayed
  within 2.2% by median. The singleton bypasses this validator and its timings varied. Eighteen wire comparisons
  matched and timed reuse allocated 0 B. The validator grew from 46 to 92 IL bytes, 152 to 200 Arm64 FullOpts bytes
  and 88 to 132 emulated x64 FullOpts bytes. Test the final flag at an unaligned offset and assert that the reuse
  destination is still untouched when validation rejects it.

## Fresh reads

- Fuse the flag scan and copy for fresh raw nullable arrays of at least 128 slots with strides of eight or sixteen
  bytes. The destination is unpublished until all flags pass. Reuse the width-specific loads, masks and CPU policy.
  Four-vector groups emit paired loads and stores on Arm64. The single-vector candidate lost on long inputs, and the
  short-stride candidates slowed reuse. Keep those reads on the existing path. The final version's default-JIT
  cache-boundary timings varied alongside A/A. The aligned 4,092 Int32 and 2,044 Int64 cases took less time in all
  four no-GC controls at the same allocations. All 72 wire samples and four generated files matched. The Arm64 entry
  shrank from 528 to 296 B, with a 388 B fresh helper and a 172 B fused loop. Emulated x64 AVX2 emitted a 191 B fused
  loop, and the 512-bit variant passed software checks. Native x64 throughput remains unmeasured (`16dc6de8`).
- Broadcast a repeated 128-bit mask into the AVX-512 register with a lane shuffle. The low lane is initialized and the
  shuffle selects it four times. The isolated x64 copy-and-check loop went from 257 to 220 B and dropped its ZMM stack
  load. Broadcasting from memory saved less (238 B), and reducing halves to 128 bits took 242 B. The 256-bit
  alternatives grew, so retain its existing mask construction. Arm64 caller and loop sizes matched, with no stable
  timing or allocation change. All 135 software width/stride/offset cases and end-to-end wire outputs matched. AVX-512
  hardware execution and throughput remain unmeasured (`3ab4e4e9`).
- Inline the fresh read entry into `ReadNew` and specialize the stride from the raw slot size. Check SIMD support
  there so the scalar build skips the dispatch. The Arm64 entry and helper went from 388 + 124 B to 432 B, or 332 B
  with intrinsics disabled. Emulated AVX2 went from 276 + 78 B to 303 B. The fused loops kept their sizes. The helper
  added seven IL bytes and the caller stayed at 47. Four default-JIT M4 comparisons favored the 129-item Int32 fresh
  case at offset seven, while long, cold and reused cases varied with A/A. Wire, generated source and typical
  allocation matched. All three targeted suites passed (`b7fb6874`).

## Writes

- Clear the absent values of raw `T?` runs in the copy's one pass, at the masked copy's widths, and read the listing
  for the uops a vector costs. Why: the first form (a751e6c2, bb63a255) made `NinoSerializeNullableInts`, `...Longs`
  and `...Decimals` about 2x slower on every runner CPU (A/B 43264755384: 0.29-0.65 of the base's speed) though it
  meant to add one AND. Its listings showed why: `CopyLanes` stopped at 32-byte vectors with no unroll and spent
  six vector uops a vector (`vpand`, `vpcmpeqq`, `vshufpd`, a `vpternlogd` NOT, a `vpternlogd` AND, a zeroing
  `vxorps`) where the masked copy spends one per 64 bytes; on Arm64 a `tbl` plus `cmtst` plus two ANDs per 16 bytes
  with every constant reloaded (`ldr q, [@RWD]`) inside the loop and no `ldp`/`stp`; and `decimal?` (stride 24) took
  the masked copy and then a scalar second pass (`ClearAbsent`: a flag load, a branch and a 16-byte store per element).
  Now: 64-byte vectors where `NinoSimd.Prefer512`, four a step, the present lanes as a mask register
  (`ConditionalSelect(~Equals(flags & mask, 0), value & keep, 0)` compiles to `vpandd`, `vptestmq`,
  `vpandq {k}{z}`; an `AndNot` of the compare compiled to `vpmovm2q`/`vpternlogd` instead); for 16 and 24 a byte
  shuffle that puts the flag in every byte of its element (`vpshufb` with index 0 per 16-byte lane for 16;
  `vpermb`/`vpermt2b` over the three vectors of a 24-byte period with AVX-512 VBMI, the qword `vpermq`/`vpermt2q`
  without), so the lanes are tested as they are: three ops a vector instead of four (b0df4364, local QUICK base 14a2d2df /
  7843090a / b0df4364: `...Decimals` 447 / 559 / 429 ns, `...Longs` 163 / 299 / 266, `...Ints` 90 / 145 / 143; stride 8
  is unchanged and still trails the masked copy by its two extra ops); 32-byte the same with AVX-512VL (`vpshufb` for 16 with
  AVX2 alone), a compare and `vpandn` without. On Arm64 (.NET 9+) `Load2xVector128AndUnzip` / `Load3xVector128AndUnzip` put one part of
  every element in a register (the flags in the first), so a step of 32 bytes (48 for 24) is `ld2`, one `cmtst`,
  two ANDs (three for 24), `st2`. Local QUICK run (Xeon Emerald Rapids, shared machine; base 14a2d2df / main /
  this): `NinoSerializeNullableDecimals` 412 / 1,276 / 430 ns, `...Longs` 157 / 379 / 259, `...Ints` 85 / 165 / 147.
  What remains on x64: unaligned stores (the lanes must start at whole elements, so the destination after a 4-byte
  count is never aligned and every 64-byte store splits a cache line; aligned, stride 8 took 109 ns for 8,000 bytes
  against 131-134, the masked copy 77) and the extra uops (port 5 takes `vptestmq` and the shuffle for 16 and 24).
  Measured and not kept (kernel harness, Xeon Emerald Rapids, diagnosis only): loading the vector 8 bytes earlier
  instead of the shuffle (a `vpternlogq`, `vptestmq`, the masked AND) is slower (the extra load splits more lines
  than the shuffle costs); destination-aligned 64-byte stores with the flags from a second load 7 bytes earlier and a
  `vpcmpud` threshold compare into k is slower (stride 8 145 ns against 111-134, 16 238-316 against 214-295: the
  second split load costs more than the aligned store saves); element-aligned results realigned to the destination
  with `vpermt2d` gain nothing (117-136 against 110-137) and need a gap/overrun fix at both ends; a masked AND
  without the folded memory operand gains nothing. Not worth trying: a keep mask from `vpsllq`/`vpsraq` costs the
  same uops and reads a flag byte above 1 as absent where the copy reads it as present; masked stores
  (`vmovdqu8 {k}`) cannot skip absent lanes, the destination holds garbage that must become zeros. The JIT does
  not fold `Equals(x & m, 0)` into a two-operand `vptestmq x, m`. Arm64 timing: runners only (listings under qemu).
- Do not scan raw `T?` runs for dirty absent values and padding to take `NinoUnsafe.Copy` when clean (MemoryPack's
  `SafeNullable` is a plain memcpy because its memory is always clean): a read-only AVX-512 pass of the same
  test-and-masked-AND plus an XOR and OR into an accumulator, then the copy, took longer than the one-pass copy on a
  Xeon (Emerald Rapids), clean data: `NinoSerializeNullableLongs` 336 against 283 ns, `...Ints` 154 against 139 (four
  alternated in-process rounds). The pass costs as much as the copy's own ALU work. What bounds the one pass there is
  three 512-bit ALU uops a vector on ports 0 and 5 (`vpandd`, `vptestmq`, `vpandq {k}{z}`) against the copy's one
  store a cycle; .NET 10 exposes no intrinsic for the two-operand `vptestmq x, m` that would make it two, and the
  JIT folds the AND into it in no form (`~Equals(x & m, 0)`, swapped `ConditionalSelect` arms, `CompareNotEqual`,
  `GreaterThan`, `BlendVariable`, the mask in a register or a parameter: always `vpandq` then `vptestmq k, z, z`).
  The test can be one uop without it: load the run again 7 bytes earlier (15 for a 16-byte stride, whose flag-only
  word takes a threshold of 0), so each lane's top byte is its element's flag, and compare unsigned against 2^56
  (`Vector512.GreaterThanOrEqual`, `vpcmpuq` into k). Not landed: it measured 3-8 % faster than the three-uop pass
  (`int?` 1,000: 110-114 against 113-123 ns; `long?` 218-230 against 219-233; kernel harness, Xeon Emerald Rapids,
  destination 4 bytes past a 64-byte boundary as after a count, memcpy 65 and 106-114 ns), and slower with an aligned
  destination (110 against 89 ns): its second load splits cache lines too, so loads bound it. What does bound the
  one pass is the destination: 64-byte aligned, the three-uop pass took 89 and 175 ns against 113-123 and 219-233,
  but after a 4-byte count the elements never start on a line, and realigning in registers costs the uop it saves
  (`valignq`; the realignment bullet above).
- Do not change raw `T?[]`'s wire to a presence bitmap for speed: in L1-sized runs it barely moves. Kernel harness,
  Xeon Emerald Rapids, 1,000 values, every fifth null, against the slot wire (each slot whole, absent and padding
  zero): a bitmap then every value (absent zero; flags and values gathered by `vpermt2q`/`vpermt2d`, the test's mask
  the bitmap byte) wrote `long?` in 228-237 ns against 242-247 and `int?` in 102-110 against 124, and read them in
  203-220 against 193-215 and 99-106 against 92-105; a bitmap then present values only (`CompressStore`, or a
  256-entry permute table) wrote in 223-262 and 106-120 ns and read 30 % slower (257-273, 128-135: `ExpandLoad`
  per group), and `vpcompress` to memory is microcoded on Zen 4. memcpy of the slots (MemoryPack's SafeNullable)
  took 117-125 and 64-79 ns. The bytes halve (`long?` 16 KB to 8.2 KB or 6.6 KB) but the time is the work per
  element: on Intel the compares and cross-lane permutes run only on port 5 (`long?` needs two of them a vector in
  every form), and the slots after a count are never aligned. A bitmap is worth considering for size alone (the
  owner's call, a wire-format change); for Nino class elements and nullable managed structs, whose bodies vary in
  length, the presence byte costs a byte and a branch beside the body and stays.
- Keep the bulk clear for converted nullable writes. Per-slot clearing wrote each slot once but slowed 64 mixed and
  16,384 all-null booleans in four paired runs. Wire bytes matched and Arm64 FullOpts grew from 1,256 to 1,404 bytes.
- On Arm64, bypass that converter for nullable bools with the probed two-byte flag/value layout. Expand each 0/1
  presence bit into a lane mask, clear the null slots and end the final vector at the last complete slot. Use 64-bit
  vectors for four to seven elements and 128-bit vectors thereafter. Check the minimum count before hardware and
  layout tests so the single-item control stays flat. Compile the bounded vector loop optimized from its first call.
  The M4 default-JIT 128-item mixed median went from 1.78 us to 38 ns; 16,896 mixed items went from 25.9 to 0.59 us.
  The baseline was still instrumented Tier 0 at 128 items. Offsets zero and one, dirty null slots, and wire output
  matched at 0 B. Write grew from 197 to 346 IL bytes before its shared helpers; Arm64 FullOpts went from 1,256 to
  1,424 bytes plus the 92-byte 128-bit kernel. The x64 FullOpts fallback stayed at 998 bytes. Keep 128/256-bit x64
  candidates out: the shift variant still slowed the emulated 129-item probe by over 30%. The 512-bit cross listing
  showed ZMM loads and stores but did not run on hardware. Both modern suites, the scalar fallback and netstandard
  builds passed (`cc716ba8`).
- Reuse that write loop for 512/256/128-bit groups on modern .NET. Select the largest complete group through NinoSimd
  and require `Prefer512` (it implies AVX-512BW) for the 16-bit shifts. Four-to-seven-element groups take Arm64's
  `Vector64` or, elsewhere, the `ulong` word (`BoolSlotsWord`). The width adapters contain the shifts and mask; the traversal and overlapping tail stay shared. Portable
  tests directly exercised 64/128/256/512-bit kernels with dirty null slots, offsets 0/1/7 and sentinels. The x64
  guest selected AVX2 or SSE2 and matched the scalar wire bytes. Offline x64 emitted XMM/YMM/ZMM bodies of 78/79/99 B;
  only the audit copy omitted AggressiveOptimization for ReadyToRun. Native AVX-512 execution and throughput remain
  unmeasured. Write IL grew from 499 to 616 B, while the shared loop/group stayed 44/48 B. Arm64 bool entry/kernel
  sizes stayed 1,560/92 B and the raw int entry stayed 1,288 B. The AVX2 guest entry grew from 1,093 to 1,205 B plus a
  79 B kernel. Four M4 A/B rounds were noisy with 0 B allocation. .NET 8/10 passed 45 tests each, scalar passed 36 and
  both netstandard variants passed 32 each (c2c7f291).
- Keep the overlapping final vector. Replacing it with a 64-bit group and up to three scalar slots slowed the
  16,897-item control by a 12% median. Two-vector unrolling had no repeatable gain and slowed 15,873 items by a 4%
  median. Advancing two byrefs with a signed countdown generated post-index loads and stores, but four default-JIT M4
  pairs were broadly flat at short sizes and around a 64 KiB input/output footprint. The cursor version grew the
  shared loop from 44 to 81 IL bytes and kept 92 Arm64 FullOpts bytes; emulated x64 remained on the 998-byte fallback.
  All ten wire cases matched at 0 B.
- Keep the shared loop for four to seven slots. A dedicated one-or-two-group helper removed the loop and shrank Arm64
  FullOpts from 92 to 76 bytes, but the default JIT used 112-byte Tier 0 code and medians slowed by 10–22% in four M4
  pairs. Requesting optimization from the first call brought the medians within 1%. Requesting inlining still slowed
  the short cases by 11–24% and grew the Arm64 FullOpts entry from 1,512 to 1,544 bytes. The short helper added 33 IL
  bytes beside the shared 44-byte loop. The x64 fallback stayed at 1,091 bytes in all three variants. At offsets zero
  and one, all 26 wire cases matched for each variant and steady allocation was 0 B. Timing around the 64 KiB
  input/output footprint varied. Retain the shared optimized loop.
- Write one to three bool slots with a single little-endian word store per element. Use typed nullable access and zero
  the entire slot when absent. Four M4 default-JIT pairs improved the present singleton by a 36% median and three
  present values by 23%. Mixed two- and three-item medians improved by 8% and 7%. The null singleton and vector
  controls were broadly flat. The emulated x64 singleton improved 23%; other guest timings were noisy. Nine wire
  comparisons matched at 0 B. Write grew from 346 to 499 IL bytes, from 1,424 to 1,512 Arm64 FullOpts bytes and from
  998 to 1,091 emulated x64 FullOpts bytes. Dirty nulls, disabled intrinsics and portable builds passed (`0e424b38`).

## Nullable bool reads

- Read short nullable bool blocks with the existing checked bool primitive after validating every presence flag. The
  cursor-only trial had no repeatable gain. Inlining the checked conversion for every size slowed the above-64-KiB
  default JIT case despite a smaller OSR loop. Keep blocks beyond 1,024 elements on the original loop. Four M4 pairs
  improved mixed 16–1,024-element reads by roughly 10–11%, with zero allocation and matching bytes. The 1,025-element
  control was flat, the cache-boundary cases varied, and emulated x64 timings were inconclusive. The entry and short
  helper moved from 163 to 224 plus 76 IL bytes, 212 to 276 plus 132 Arm64 FullOpts bytes, and 141 to 180 plus 83
  emulated x64 FullOpts bytes. Strict bool, fallback and compatibility checks passed (`18396f99`).
- Reuse the Arm64 nullable bool slot mask for long reads into disjoint storage. Validate presence bytes first as
  before. Check each masked vector for invalid live values before storing it and use the scalar loop for a rejected
  group to preserve the first exception and earlier writes. Keep dirty null value bytes out of the destination and
  keep overlap and small blocks on their prior paths. Four M4 pairs with default JIT moved 1,025 mixed items from 9.53
  us to 154 ns and 16,897 mixed items from 12.8 to 1.19 us. Short controls were flat, all eight wire comparisons
  matched, and reuse allocated 0 B. Entry IL grew from 224 to 356 bytes with 121 bytes in the two new methods. Arm64
  FullOpts grew from 276 to 356 bytes plus a 252-byte vector loop; the emulated x64 fallback stayed at 180 bytes.
  Dirty-null, invalid-tail and scalar tests passed (`5a9438b9`).
- Start that reader at one full 128-bit group (eight bool slots). Return through the short scalar loop before the ISA
  and layout checks for one to seven elements. Moving the vector test ahead of the old scalar test without that early
  return slowed the singleton. Four M4 pairs with the early return improved the 8-, 9- and 16-item mixed cases by
  medians of 42%, 39% and 57%; 1–7 items, the former threshold's upper neighbor and the cache-boundary control stayed
  broadly flat. Twelve wire cases matched at 0 B. Entry IL fell from 356 to 348 bytes, Arm64 FullOpts moved from 356
  to 364 bytes and the emulated x64 fallback from 180 to 185 bytes. The 252-byte vector loop was unchanged. Test dirty
  null and invalid tail slots at both 9 and 1,031 elements (`c8950716`).
- Handle four to seven elements in the existing short branch using the 64-bit mask and one or two groups. Sharing the
  128-bit loop through a generic read kernel slowed 16,897 items by about 23%. Keep the 128-bit loop and its dispatch
  separate. Four M4 pairs improved mixed 4-, 5- and 7-element medians by 27%, 31% and 35%; one and three elements and
  the 64 KiB control stayed broadly flat. The 128-element control had a 3% slower median. Ten wire cases matched at 0
  B. Entry IL grew from 348 to 369 bytes, and Arm64 FullOpts from 364 to 440 bytes plus a 216-byte short helper. The
  252-byte long loop and 185-byte emulated x64 fallback were unchanged in size. Check the invalid tail in a
  five-element overlapping-vector group (`9bc09098`).
- Check one bool slot with a single scalar helper after the length proof. Validate presence before an active value and
  ignore dirty null value bytes. Inlining the singleton check into the entry slowed the 128-element vector control by
  6%; outlining it brought the follow-up 4- and 128-element differences to about 2%. Four M4 pairs reduced absent and
  present singleton medians by 28% and 32%. Eight wire cases matched at 0 B. Entry IL grew from 369 to 424 bytes plus
  a 56-byte helper. The singleton probe's Arm64 FullOpts entry grew from 600 to 656 bytes plus 128; emulated x64 from
  185 to 220 plus 70 bytes. Both runtime suites and scalar validation passed (`4ee7e0c8`).
- Use the same slot policies for 512/256/128-bit reads and share the checked group with the short 64-bit path. Check
  all presence flags before writing and fall back to scalar order inside an invalid-value group. Keep the existing
  overlap gate. Portable tests covered every invalid-value position around two group boundaries and all four widths,
  including ignored null payloads and partial destination updates. Overlap tests covered both copy directions; the x64
  guest checked invalid presence and values under AVX2, 128-bit and scalar execution. .NET 8/10 passed 62 tests each,
  scalar passed 53 and both netstandard variants passed 49 each. Arm64 entry/kernel stayed 492/252 B. ReadPrechecked
  IL grew from 424 to 496 B; the shared loop/group grew from 36/85 to 44/99 B and replaced the separate 85 B short
  group. Offline x64 128/256/512 kernels were 180/181/207 B, with AggressiveOptimization omitted only in the audit
  copy. The AVX2 guest entry grew from 220 to 334 B plus its 181 B kernel. Four M4 A/B rounds and first-use reads
  varied alongside A/A at 0 B steady allocation. Native AVX-512 throughput remains unmeasured (786c2172).
- Keep the per-vector read until a paired variant preserves cold latency. Sharing the invalid-value exit reduced Arm64
  FullOpts from 252 to 188 B but consistently slowed 1,025-slot reuse. An Arm64-only 16-slot batch reused the 128-bit
  mask and emitted paired loads and stores with one invalid-value reduction. Dispatch inside the existing vector
  method kept `ReadPrechecked` at 496 IL bytes, raised the single-vector method from 44 to 95 IL bytes and added 150
  IL bytes for the batch and its group helper. Arm64 machine code was 292 B for the single-vector method and 300 B for
  the batch. Four warm rounds favored the batch at 1,025 slots across offsets 0/1/7, with 0 B allocated. First reads
  in randomized fresh processes regressed at 9 and 1,025 slots relative to two identical baseline controls. Withdraw
  the batch despite the warm gain. The extracted x64 audit kernels stayed at 178/181/207 B for 128/256/512 bits with
  the Arm64 guard folded. All 27 wire outputs matched. The candidate passed the .NET 8/10, scalar, and netstandard
  2.0/2.1 targeted suites, including first-invalid-value order across the two halves and overlapping tails.
- Do not chase 64-byte-aligned stores for the raw `T?` run on x64, and model an Arm64 kernel with llvm-mca before
  writing it. What bounds the copy, measured for `long?[1000]` (16,000 bytes; local Xeon with AVX-512, decisions
  only; runner numbers from run 43573480826):
  - x64: the kernel (vpshufb, vptestmq, masked vpandq) is 180 ns when source and destination are both 64-byte
    aligned, 193 ns with split loads only, 245 ns with split stores only (the destination is 4 bytes into the buffer,
    after the count, so every 64-byte store crosses a cache line) and 270 ns with both. Three kernels that store
    aligned all lost the gain elsewhere: two more shifted loads for the presence (340 ns: every extra 64-byte load
    splits too), cleaning as before and moving the result to the destination's phase with vpermt2d (255 to 277 ns: a
    third port-5 operation a vector), and loading at the destination's phase with one vpermt2d gathering each
    dword's flag (230 ns with split loads, 187 ns without: four operations, because the JIT does not fold
    `Equals(x & mask, Zero)` into a two-operand vptestm and emits the AND on its own). Source and destination are
    64-byte aligned relative to each other in one case of eight, and Nino controls neither address.
  - Arm64: one 16-byte vector is one element of a 16-byte stride, and its three operations (tbl, cmtst, and) wait
    for the two vector pipes of a Neoverse core: llvm-mca (`-mcpu=neoverse-n2`) gives 4.0 cycles for two elements,
    the structure loads (ld2, st2) 3.5 and a 32-byte copy 1.0, and the runner agreed (the tbl kernel against the
    structure loads: 1.00 in every case over 8 rounds; `long?[1000]` 752 ns against MemoryPack's block copy of
    SafeNullable at 268 ns). Words in general-purpose registers with a conditional select model at 1.9 cycles, but
    the JIT compiles a ternary in a loop to a branch per element, and its branchless form (cmp, cset, zero-extend,
    neg, and) models at 4.5 cycles on Neoverse-N2 and 9.0 on Apple M1 against the vector kernel's 4.0 and 2.5.
  A run that must clear each element cannot reach a block copy; `NinoNullable<T>` (memory that is already the
  wire) is the form that does: its `long` and `int` arrays wrote 1.1 to 1.8 times as fast as MemoryPack's
  SafeNullable on all four runner CPUs.
- Never build a value by writing part of a local and reading the whole back (`T present = default;
  Unsafe.As<T, byte>(ref present) = 1; field = present;`): the load is not covered by one store, so it waits for
  both to retire (a store-forwarding stall for every value). `NinoNullable<T>`'s constructor did, and its single
  values and non-block arrays took 1.3 to 10 times as long as `T?` on the runners; keep such a constant in a
  `static readonly` field per `T`, and fill arrays with stores into the destination, never through a local.
