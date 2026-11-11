# Checks of values in a block (.NET)

Part of the `optimize` skill ([vectors.md](vectors.md) holds the general loop rules): checking the bools and value
ranges of a block in vector loops, and where the bool check sits in a block read. Nullable presence flags are
[vectors-nullable.md](vectors-nullable.md).

Platform scope: a bullet that names a GitHub runner run (or "runners") was measured on every runner CPU. The others
were timed on a local Apple M4 (Arm64, default JIT) or, where they say so, on an emulated x64 AVX2 guest; their x64
evidence is listings only (Crossgen2, cross-JIT), and the rules lists mark them "M4". Measure on native x64 before
carrying such a threshold or verdict there (`benchmark`).

Contents: [Rules](#rules), [Bools](#bools), [Bools in block reads](#bools-in-block-reads), [Value
ranges](#value-ranges).

## Rules

- Check a block read's bools in the pass that moves the values on x64; Neoverse-N2 keeps the strided pass (runners).
- Check bytes at a fixed stride eight at a time into four accumulators (runners).
- Check a one-byte contiguous bool block directly; never reuse the strided flag kernel for contiguous bools; fuse
  validation with the copy for fresh top-level bool arrays above 4 KiB (M4; native x64 unmeasured).
- Skip the bool prepass when the read plan only calls `ReadBlock`; reuse the validated bool byte; share the canonical
  validation with `Reader.ReadBool` (M4).
- Check value ranges on x64 with subtract-and-OR under the cost rule, folding bools into a wider pass's lane
  (runners). Keep the periodic scan with its period in a local, its tables taken by `GetArrayDataReference` and the
  Arm64 accumulator reduced straight to zero (M4; x64 listings).
- Fuse a single range pass with the copy into a fresh canonical array under the shared 4 KiB gate, and fold a
  sign-only pass into a preceding pass of the same lane width (M4; x64 listings).

## Bools

- Check a block read's bools in the pass that moves the values, not in a strided pass before it: OR the stored vectors
  (read back from the store buffer) into one accumulator per vector and mask them once after the last group
  (`NinoBlockCollections.LanesChecked`, AVX2 lane groups; AVX-512 groups OR their loads, `ReadPlan.Checks`). 1,000
  `Waypoint`s deserialized in 0.922 times the time on EPYC 7763 (1,360 to 1,240 ns, floor 1.000) and 0.965 on 9V74 VMs
  without AVX-512, writes unchanged (.NET A/B run 41079230729). On Arm64 the same check in `Lanes` cost the inlining
  of `Lanes16` (`jit.md`, "Inlining budget"), so Neoverse-N2 keeps the strided pass (0.967 measured with the check,
  run 41052335826).
- Check bytes spread at a fixed stride (the bools of a block, `NinoChecks.Strided`) eight at a time into four
  accumulators, OR-ed once at the end: one accumulator is a chain of dependent ORs, one cycle per byte. Evidence:
  1,000 bytes 15 apart in 155 ns instead of 535 on a 2.1 GHz AVX-512 Xeon (four a step: 195); on GitHub's runners
  1,000 `Waypoint`s (a bool at offset 14 of 15) deserialized in 0.81 to 0.93 times the time on every CPU (EPYC 7763,
  9V45, 9V74, Xeon 8573C and 8370C, Neoverse-N2). Rejected: a vector check with a periodic mask of the bool positions,
  211 ns with 64-byte vectors and 325 with 32-byte ones against the scalar 155.
- Check a one-byte contiguous bool block directly. Its first byte is also its first possible invalid byte, so it can
  skip the runtime's search without changing the diagnostic. Keep the runtime search for every other length. Eight
  randomized first-read runs per binary, with two identical baseline controls, found a repeatable singleton gain on
  the M4. Warm singleton results favored the fast path at offsets 0 and 7 and varied at offset 1. A 32,800-element
  regression in the unpinned fixture did not reproduce with fixed cache-line positions and randomized A/A/B. The
  checker grew from 33 to 51 IL bytes, 96 to 144 Arm64 FullOpts bytes, and 54 to 77 x64 ReadyToRun bytes across
  SSE4.2, AVX2, and AVX-512 listings. All 24 serialized outputs matched and reuse allocated 0 B.
- Do not reuse the strided flag kernel for contiguous bools without checking first use. A stride-one mask and a
  256-byte gate improved warm 1,025-element reads at all three offsets but added about 0.8 ms to first reads of 1,025
  and 32,752 elements. The shared validator grew from 217 to 244 IL bytes. The x64 audit added 24 bytes at each vector
  width, with `AggressiveOptimization` omitted for cross compilation. Withdraw the long scan.
- Fuse validation with copying for fresh top-level bool arrays above the measured 4 KiB gate. Reuse the nullable
  copy's 128/256/512-bit kernel and supply the contiguous mask. Retry the exact-byte check before reporting invalid
  input. Outlining the large fresh path kept short reuse within A/A variation; applying the gate to the shared ref
  reader had slowed singleton reuse and first reads. In four isolated M4 comparisons, fresh 32,752 and 32,800 byte
  payloads took 40–52% less time at equal allocations, with mixed 4,097-byte results. The Arm64 fresh entry grew from
  432 to 488 B plus the 440 B outlined entry and 172 B copy loop. The AVX2 guest and 128-bit fallback matched bytes;
  the 512-bit kernel passed software checks. Native x64 throughput remains unmeasured (`2b415f63`).

## Bools in block reads

- Skip the bool prepass when the read plan can only call `ReadBlock`, which already validates each bool. Keep it for
  whole-block copies and vector paths that do not validate. Keep the decision in a read-only nested plan so writes do
  not initialize the read masks. At offset 1, four local `BlockWaypoint` A/B rounds with intrinsics disabled reduced
  time by 32–37% for one item, 7–13% for 65 and about 7.3% for 1,700. SIMD timings varied and allocation stayed at 0
  B. The Arm64 scalar `ReadRejecting` Tier 1 shrank from 176 to 116 bytes, while the x64 FullOpts cross listing grew
  from 385 to 443 bytes with the static field load. `ReadUnchecked` grew from 54 to 61 IL bytes. The wire bytes and
  rejection checks matched, and the targeted .NET 8/10 tests passed with hardware intrinsics both on and off
  (`2916df8b`).
  - Combining the bool presence and copy flags in one static field kept the x64 FullOpts caller at 443 bytes. Reusing
    the active group thresholds instead removed the extra class-init guard and reduced the x64 caller to 360 bytes.
    Arm64 Tier 1 stayed at 116 bytes without SIMD and grew from 176 to 184 with SIMD. The caller/helper IL changed
    from 61/49 to 54/64 bytes. At offset 1 across four local rounds, scalar reads of 65 and 1,700 items stayed within
    0.25%, with 0 B allocated. The SIMD one-item case improved in three rounds but slowed in one. The 1,700-item SIMD
    case varied from 3.4% faster to 13.2% slower. Wire bytes, dirty padding and invalid data checks matched. In six
    further rounds, an identical-binary A/A control for 1,700 items ranged from 3.7% faster to 5.5% slower, while A/B
    ranged from 4.9% faster to 9.8% slower. The single-item A/B improved in all six rounds, by 23.7–36.0%. The Arm64
    caller gained a compare and a branch that skip the prepass below eight items for this plan. Keep the short-input
    improvement and report the long SIMD case separately. The .NET 8 and 10 targeted suites passed with intrinsics on
    and off (`9d51e10a`).
- Reuse the validated local bool byte in raw block reads. After the check rejects values above one, its representation
  is already canonical. Arm64 removed the compare against zero and `cset`: scalar `ReadEach` Tier 1 shrank from 172 to
  164 bytes. The x64 FullOpts cross listing went from 140 to 135 without SIMD and 186 to 180 with it. `ReadBool`
  stayed at 22 IL bytes. Four Arm64 A/B rounds with SIMD enabled and offset 1 were 1.6–3.0% faster for one item and
  1.6–4.6% faster for 65. The 1,700-item and tiny scalar results varied. Scalar results at 65 and 1,700 stayed within
  0.3%. Allocation stayed at 0 B. Wire, dirty-padding, truncation and invalid-bool probes and targeted .NET 8/10 tests
  passed (`5518250c`).
  - Share the canonical validation with `Reader.ReadBool` after its bounds-checked byte load. With individual
    `BlockWaypoint` reads into a reused array, the generated body went from 244 to 236 Arm64 Tier 1 bytes and from 162
    to 156 x64 FullOpts cross-JIT bytes. The reader entry's IL went from 22 to 12 bytes before inlining the shared
    validator. Four alternating rounds and an identical-binary A/A control showed variable timing at offset 1, with 0
    B per read. The 1,700-item case spans about 66 KB of input and output. Wire and rejection checks matched at
    offsets 0, 1 and 7, and .NET 8 and 10 targeted tests passed (`95caf340`).

## Value ranges

- On x64, check ranges of values in a run (Untrusted mode's bool, DateTime, decimal, DateOnly and TimeOnly fields,
  `NinoValueChecks`) with subtraction and OR. Per stride, tables of masks and bounds repeat over lcm(stride, vector
  width), each vector ORs `bound - (v & mask)` into an accumulator (bytes: `v & 0xFE`) and only its sign bits are
  tested at the end. Use the vectors only when they cost less than the branch-free scalar loop (`2 * passes * stride
  <= fields * width`). Evidence (local Xeon, AVX-512, 1,000 values): unsigned `GreaterThan` compares were slower than
  scalar (a bool/DateTime/int/double struct 1,616 ns against 1,526; a 48-byte struct of every kind 9,849 against
  5,658); subtract and OR with the cost rule took 1,535 and 5,644 against main's 3,806 and 12,040 (main branched per
  field), DateTime[] 382 against 1,914; on runners the Untrusted list case (1,000 structs with a DateTime and a bool)
  took 0.47-0.69 times main's time on every CPU. Put a bool in a wider pass's lane where that lane tests only against
  a bound of zero and the bool is not its top byte (`Pass.Joins`: `0 - (v & mask)` has its sign set exactly when a
  masked bit below bit 63 is set), so passes, not fields, set the cost: the Untrusted list's struct (a bool at offset
  20, in the DateTime pass's 8-byte lanes) went from two scalar loops to one vector pass: the Untrusted list case
  11-20 % faster on x64 runners with the over-read fix included (round 129, dotnet-ab runs 42341861781 and
  42365821207). Arm64's 16-byte vectors keep it scalar by the cost rule (48 > 32), on par there. Tried and not kept:
  the loop over whole periods with the tables' offsets from an inner loop instead of the phase wrap, 5-10 % on the
  check alone, inside the noise of the case.
- Keep the existing periodic scan. For strides that divide the vector width, the mask and bound can be loaded once,
  and the overlapping final vector has the same mask phase. An inline version regressed nonuniform layouts. An
  outlined scan with a 256-byte gate preserved the periodic loop, but its 496-byte instrumented Arm64 Tier 0 loop was
  slow before reaching the 156-byte Tier 1 body. Compiling the loop optimized from entry improved warm DateOnly reads,
  while the independent 1,025-item first-read repeat was about 9–11% slower than both identical baseline controls.
  Withdraw the four variants. All 63 wire outputs matched at zero steady read allocation. The x64 Crossgen2 audit
  rooted the same algorithm at 128/256/512 bits. The existing `Vector<T>` scan emitted XMM/YMM/ZMM operations with
  code sizes of 188/190/212 bytes for ulong lanes. The separate fixed-mask loop emitted 116/113/141 bytes. The 512-bit
  listing required the wider vector-width setting. These listings provide width and code-size evidence, with
  throughput measured on Arm64 only.
- Keep the period in a local and reuse the next vector's phase for the overlapping tail. Back up by less than one
  vector and wrap a negative phase once. This removes the period load inside the Arm64 loop and the signed divide in
  the tail. The loop went from 255 to 280 IL bytes and from 272 to 224 Arm64 FullOpts bytes. The offline x64 ulong
  full-range loops went from 215/216/247 to 230/231/263 B at 128/256/512 bits, without `idiv`. Eight DateOnly values
  at offset seven improved in all four isolated M4 comparisons; short mixed and near-64-KiB controls varied alongside
  identical binaries. All 96 wire samples matched and warmed reads allocated 0 B. The guarded-page suite and
  1,500-input fuzz runs passed with SIMD on and off (`fa18a12c`).
- Take the validation table bases with `GetArrayDataReference`. Each pass allocates period plus vector width for both
  tables. The indexed bases retained two length checks in the Arm64 listing. Removing them shrank the scan from 280 to
  278 IL bytes and from 224 to 200 Arm64 bytes. The x64 ulong full-range listings shrank from 230/231/263 to
  207/208/240 B at 128/256/512 bits. Four default-JIT short and large comparisons varied alongside two identical
  baselines, with no repeatable throughput change. All 96 wire samples matched at 0 B steady allocation, and all three
  targeted test runs passed (`44016216`).
- Reduce the Arm64 accumulator directly to zero. The unsigned comparisons already fill the entire failing lane, so a
  separate sign mask repeats work. The scan grew from 278 to 285 IL bytes and shrank from 200 to 192 Arm64 bytes,
  without the 16-byte mask constant. Offline x64 instructions matched at 128/256/512 bits. Four default-JIT
  comparisons and isolated reruns showed no repeatable throughput change on either side of 64 KiB. All 120 wire
  samples matched at 0 B steady allocation. The guarded tests and two 1,500-input fuzz runs passed (`de631c97`).
- Fuse a single vector range pass with the copy into a fresh canonical unmanaged array. Keep the scalar diagnostic
  path on rejection and publish only after success. Share the 4 KiB byte gate with the fresh bool read. The vector
  loop's copy mode folds away for check-only calls, retaining the 192 B Arm64 and 207/208/240 B x64 ulong FullOpts
  loops. The copy loop is 204 B on Arm64 and 222/225/262 B offline x64 at 128/256/512 bits. Four focused M4 pairs
  improved fresh DateTime, DateOnly and a 12-byte mixed record at equal allocation. DateTime near 64 KiB took 9–29%
  less time across offsets zero and seven. Reuse varied. All 120 wire samples and three generated files matched; the
  guarded and stale-memory probes passed (`a5812f25`).
- Fold a sign-only pass into a preceding pass of the same lane width after the original density check. Reopening
  vector eligibility for formerly scalar TimeOnly layouts regressed first use. On x64, bound the low bits and OR the
  masked value to preserve its sign test. On Arm64, use the native unsigned `cmhi` for every wider lane and keep a run
  of exactly one vector scalar. The latter avoids building a plan on the first short read. In four M4 pairs at offsets
  0/1/7, 1,025 DateOnly items and the mixed 12-byte record improved by 34% and 37% at the median. Isolated
  two-baseline controls reproduced the long wins. The three-DateTime first read fell from about 3.0 to 1.9 ms, while
  the longer cold controls varied. All 72 wire outputs matched with 0 B steady allocation. The Arm64 dispatch and loop
  stayed at 240 and 272 bytes. x64 fused uint loops were 215/216/247 bytes at 128/256/512-bit ceilings.
