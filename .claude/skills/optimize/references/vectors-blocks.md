# Blocks of padded structs (.NET)

Part of the `optimize` skill ([vectors.md](vectors.md) holds the general loop rules): moving blocks of padded structs
and runtime entries with permutes and lane groups, the per-CPU gates (Ice Lake), when an array may be allocated
uninitialized, reservations, and the scalar block read and write paths measured on `BlockWaypoint`.

Platform scope: a bullet that names a GitHub runner run (or "runners") was measured on every runner CPU. The others
were timed on a local Apple M4 (Arm64, default JIT) or, where they say so, on an emulated x64 AVX2 guest; their x64
evidence is listings only (Crossgen2, cross-JIT), and the rules lists mark them "M4". Measure on native x64 before
carrying such a threshold or verdict there (`benchmark`).

Contents: [Rules](#rules), [Permutes and lane groups](#permutes-and-lane-groups), [Per-CPU gates and Ice
Lake](#per-cpu-gates-and-ice-lake), [Reservations and allocation](#reservations-and-allocation), [Scalar block
reads](#scalar-block-reads), [Block writes](#block-writes).

## Rules

- Write a runtime's strided entries through the block path (runners).
- On Arm64 move values over 8 bytes in partial lane groups on the 16-byte grid; read values over 16 bytes in groups
  that fill whole memory vectors and check their bools in those loads (runners).
- Gate an instruction choice per CPU by CPUID family and model in a `static readonly`, never by an ISA flag or a
  feature bit a newer CPU merely adds (runners).
- On Ice Lake mask padded raw structs with 32-byte vectors, keep the 64-byte write groups and load them from the line
  below each group; research the microarchitecture before probing a gap, and do not retry the listed Ice Lake attempts
  (runners).
- Allocate uninitialized only where every byte, padding included, is written; add nothing to `ArrayFormatter` or
  `ListFormatter.Deserialize` (runners).
- Reserve a fixed-size queue's count and segments, and a raw sorted list's count and pairs, together; patch an exact
  concurrent queue's count from the items written; skip the initial clear for large canonical raw results; keep empty
  fixed-stride writes on the shared reservation.
- Treat the `BlockWaypoint` scalar read-tail variants (dual cursors, fewer zero stores, exact-map stores) as
  code-generation results: none gained repeatably with SIMD on (M4).
- Specialize the one-item block write only through an 8- to 16-byte prefix of two word stores plus one mapped byte;
  keep the write countdown (M4 timings; offline x64 listings).

## Permutes and lane groups

- Write a runtime's strided entries (a `HashSet<T>`'s or `Dictionary<K, V>`'s entry array) through the block path
  (`SetEntryBlock`, `MapEntryBlock`: a block map of the key and value at their offsets in the entry), never one
  `Write` per entry: the permutes pack them like any padded struct. Evidence (GitHub runners, runs 40842440261 and
  40865180098, n=12-48 a CPU, floors 0.99-1.01): 1,000-entry `HashSet<int>` serialize 0.34-0.49 times the time,
  `Dictionary<int, int>` 0.29-0.56, the polymorphic class array holding both 0.69-0.84.
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
- Read values over 16 bytes whose blocks never fill whole vectors in four of memory in groups that fill whole memory
  vectors (`ReadPlan.Group`: 8 24-byte values in three vectors, each one `vpermt2b` of two 64-byte loads, aligned
  stores), and check their bools in those loads (`ReadPlan.Checks`: OR `load & 0xFE-at-bools` into one accumulator,
  test once, the strided pass only to name the bad one). Evidence (GitHub runners, run 40935400095, n=12): 1,000
  `Waypoint`s deserialize in 0.77-0.85 times the time on EPYC 7763 and 9V74, Xeon 8370C and Neoverse-N2. Rejected: one
  64-byte load permuted into 128 bytes of memory per four values (`vpermb` twice, overlapping stores), 1.3 to 1.4
  times the time of lane groups on a local AVX-512 Xeon. Not built: the same fused check in Arm64's lane groups, whose
  windows overlap per output vector (an AND and ORR per window against the strided pass's one load per bool).
- Rust's fixed-size blocks: `optimize-runtimes`, references/blocks.md.

## Per-CPU gates and Ice Lake

- Gate an instruction choice per CPU where the runners disagree, by CPUID at startup in a `static readonly` (the JIT
  folds it), never by a `Vector512.IsHardwareAccelerated`-style ISA flag alone: AVX-512 VBMI is on Ice Lake, Sapphire
  Rapids and Zen 4/5 alike. `NinoBlockCollections.WritePairs` (four 24-byte values per `vpermt2b` of 128 bytes of
  memory into 64 of blocks) wrote 1,000 `Waypoint`s in 0.64 times the time of 32-byte lane groups on Xeon Platinum
  8573C, 0.74 on EPYC 9V45 and 0.97 on 9V74, but 1.44 times on Xeon Platinum 8370C (Ice Lake): `NinoSimd.Prefer512`
  takes AMD and every Intel CPU but the Sunny Cove generation, by family 6 and model (`IsIceLake`: 0x6A, 0x6C, 0x7D,
  0x7E, 0x9D, 0x8C, 0x8D, 0xA7). Never gate on a feature bit a newer CPU merely adds: Azure's Xeon Platinum 8573C
  (Emerald Rapids) hides AVX512-FP16 and AMX from its guests (lscpu in the job log), so the first gate, on FP16 (CPUID
  7.0 EDX bit 23), turned the 64-byte masks off there and wrote 1,000 padded 8-byte structs in 92 ns instead of 60
  (.NET A/B run 41020623184); gated by model, 8573C wrote them in 0.80 times the time of the FP16 gate and 1,000
  `Waypoint`s in 0.81 (pair permutes, 336 to 259 ns), the 8370C unchanged (run 41079230729). Include `Xeon` in the A/B
  line's CPU regex for any AVX-512 path, or Ice Lake goes unmeasured.
- On Ice Lake, mask padded raw structs with 32-byte vectors (`NinoSimd.Prefer512` gates `NinoMask`'s 64-byte masks as
  it gates the pair permutes): GitHub's Xeon Platinum 8370C wrote 1,000 8-byte structs with a padding byte in 116 ns
  with 64-byte vectors and 83 with 32-byte ones (MemoryPack 88). Its 64-byte kernels on 8,000 bytes (aligned stores
  145 ns, a zero-masking load `Avx512BW.MaskLoad` instead of the `vpand` 147, unaligned 167-287) ran no faster than
  32-byte ones (144) and slower than Memmove (122) (`MaskKernelProbe`, run 40935400095): a zero-masking load buys
  nothing there, nor locally on Emerald Rapids (1.06 against 1.07 of Memmove). Keep the 64-byte write groups
  (`Permute64x2`, vpermt2d) on Ice Lake: 32-byte ones (vpermt2d ymm, AVX-512VL) took 0.76-0.79 of their time in a
  native-buffer probe there (`TuplePackProbe`, run 41159851472: 167-173 ns against 220-226), yet in the benchmark
  1,000 `(int, long)` serialized 4.5 % slower on the 8370C (234 to 245 ns, n=12, every round above 1.0; run
  41164356389). Ice Lake's VM there pays heavily for loads across cache lines (a 32-byte copy loop of 16,000 bytes
  from 8 bytes past a line: 430 ns, aligned 222, Memmove 160; 64-byte: 377 and 190): a probe kernel with fixed
  alignments does not predict the case, whose list and buffer land where the process puts them. MemoryPack's copy
  there is bimodal by alignment (117 or 250 ns in the probe, 112 in the case).
- Before probing kernels for a gap on one CPU, research that microarchitecture (Intel's optimization manual,
  uops.info, Agner Fog, Travis Downs, Chips and Cheese) and read the code both sides run, IL and asm, instruction by
  instruction: a probe then confirms a cost model instead of searching (the owner: "you keep guessing"). The Ice Lake
  TupleList gap (round 103): MemoryPack writes the raw 16-byte `(int, long)`s with one `CopyBlockUnaligned`, which
  .NET hands to glibc past 2 KB and glibc runs as one `rep movsb` past 2,112 bytes on FSRM CPUs (destination aligned,
  so its time depends on the source's offset to it: 117 or 247 ns); Nino packs 16 to 12 bytes with 4 loads, 3
  `vpermt2d` and 3 destination-aligned 64-byte stores per 16 values, and its loads split two lines unless the list's
  group starts on one: the 234 / 162 ns modes. On a Xeon 8573C (run 41180010041, `TupleCostProbe`): 188 split 64-byte
  stores 142 ns against 59 aligned, 250 split loads 97 against 80, 188 `vpermt2d` 59, the clock 4.0 GHz scalar and 3.1
  under zmm code; Nino's own write 74 to 144 ns by the list's place (identical code both sides). On Ice Lake (Xeon
  8370C) Nino's write groups therefore load from the line below each group (`NinoBlockCollections.WriteAligned`,
  tables per place in the line, a second `vpermt2d` for the vector of blocks that spans three lines): 1,000 `(int,
  long)` serialized in 0.917 of the time (235 to 221 ns, every round 0.85-0.96, n=24, run 41181295487) and the
  `Dictionary<int, int>` entries in 0.809 (250 to 200 ns); other CPUs fold the gate (listings identical to main). Less
  than the 0.65 the model gave: the rest is the stores and the clock. Confirmed against main before it with a floor
  (run 41203402942, two 8370C jobs, n=24, base round against base round in the same jobs as the floor, `benchmark`):
  TupleList 0.925 (235 to 222 ns, every round 0.84-0.97; floor 1.001, 0.93-1.08), the dictionary 0.797 (every round
  below 0.93; floor 0.994), MemoryPack (same code both sides) 0.998.
- Do not retry the remaining Ice Lake TupleList gap (Nino 222 ns, MemoryPack 114 on the 8370C) with these; each loses
  by the model, so none was built (round 104). The gap is the store path: 12 bytes a value (the wire format) are at
  least 188 64-byte lines, and the 8370C writes lines with vector stores at about 0.76 ns each (16,000 bytes copied
  with aligned 64-byte vectors in 190 ns, r102) but with `rep movsb` at about 0.46 (MemoryPack's 16,000 bytes in
  112-117): 188 lines cost about 143 ns before any permute. 256-bit aligned groups (vpermt2d ymm): `vpermt2d zmm` and
  512-bit loads and stores are light AVX-512, which costs Ice Lake server parts at most about 200 MHz all-core (Intel
  in golang.org/issue/43925; Phoronix measured about 175 MHz on a Xeon 8380 under heavy AVX-512), so 256-bit code wins
  back at most about 6 %, while it needs 375 stores and about 525 permutes against 188 and 235, and the 8370C's
  32-byte aligned copy already lost to the 64-byte one (222 against 190 ns); locally (Emerald Rapids,
  `TupleGroupProbe`) they took 1.7-1.9 times the time at every source shift. Non-temporal stores: they evict the
  reused buffer and run at memory bandwidth (188 64-byte ones 757 ns locally against 68 cached). `rep movsb` with
  packing, either way round (pack into scratch then `rep movsb` it, or `rep movsb` the raw values then compact): two
  passes, at least the current pass plus about 85 ns.

## Reservations and allocation

- Reserve a fixed-size queue's count and both ring segments together. Keep the variable-size element loop separate so
  it does not repeat the fixed-size check for each segment. A stack's backing span can use the existing counted span
  writer.
- Reserve a raw sorted list's count and pairs together, then walk its checked key and value arrays by reference. Use
  `WriteValueIn` for both fields so the raw path retains the padding masks.
- For an exact concurrent queue with variable-size elements, write its snapshot enumerator and patch the count from
  the items written. Reading `Count` separately can observe a different snapshot. Retain bulk writes for fixed-size
  elements and the existing snapshot semantics for derived types, stacks and bags. A pooled chunk loop and direct raw
  snapshot writes both saved the snapshot array but regressed several fixed queue sizes in a local A/B. Compare
  fixed-size cases separately before replacing their bulk path.
- Allocate a new array uninitialized only where the operation writes every byte of it, padding included
  (`NinoElements.CopiedWhole`), or it hands stale heap memory to `MemoryMarshal.AsBytes` or a raw re-serializer: block
  reads write a padded value's padding as zero (`NinoBlockCollections.ReadOne` clears a value before `ReadBlock`;
  AVX-512's `vpermb`/`vpermt2b` read only an index's low bits, so their padding bytes are masked with a `vpand` per
  vector, `ReadPlan.Keeps`, `KeepMemory64`; pshufb, tbl and Arm64's lookups write 0 for index 0x80 by themselves), and
  the tests read into 0xFF-filled and recycled 0xCC memory and compare every byte
  (`BlockStructTests.ReadsWriteEveryByte`; without the masks they fail on AVX-512). Values read a field at a time
  (Untrusted mode, `ReadSize` -1) keep a zeroed array. Evidence: rejected first with the padding unwritten (Waypoints
  0.68-0.76 times main's time); landed with it written, together with the read groups above (run 40935400095). Where
  the read can still reject the data after the allocation (a bool of a block: `NinoBlockPlan<T, TFormatter>.Rejects`),
  `NinoBlockCollections.ReadAll` reads them out of line under a try/finally that clears the values when it throws
  (`ReadRejecting`; `audit-security`). Never allocate those arrays zeroed instead: 1,000 Waypoints read into a new
  array took 1.15 to 1.27 times as long (A/B run 41110873514, every CPU). Add nothing to `ArrayFormatter` or
  `ListFormatter.Deserialize`, not even a test that folds in Release: they recurse through collections nested in
  themselves, and Debug builds (CI's Windows unit tests, 1 MB stacks) compile every branch and keep a temp per call. A
  test of a static field and an out-of-line call there grew the two frames from 672 + 448 to 768 + 576 bytes
  (`DOTNET_JitDisasm` under Debug, `sub rsp`) and 400 nested `List<SelfArrays[]>` ran out of stack on Windows
  (`SelfNestingCollectionTests`, main e2e28fe7). In Release the same test through an inlined method returning a
  constant, though it folded, made `ArrayFormatter<int, ...>` stop inlining `NinoElements.Read` into the
  ImmutableHashSet, ImmutableDictionary, ReadOnlyCollection and Memory<int> reads (1 stack access to 15, `asm.py`).
- Skip the initial clear for large canonical raw serialization results (ad425fcc). The layout write fills the entire
  array and clears padding. Keep the small direct allocation and use the shared 6 KiB cutoff.
- Keep empty fixed-stride writes on the shared counted reservation. The Arm64 caller already combines the element-byte
  multiplication and length addition in one `umaddl`. Emitting `WriteLength(0)` before the reservation grew
  `WriteSpan` from 63 to 69 IL bytes, the Arm64 Tier 1 caller from 524 to 608 bytes, and the offline x64 array
  formatter from 2,257 to 2,345. Four empty-weighted A/B rounds varied from 16.7% faster to 0.9% slower. The other
  profiles varied in both directions. All wire outputs matched at offsets 0, 1 and 7 and all timed writes allocated 0
  B. Avoid duplicating the reservation path for an empty array.

## Scalar block reads

- Keep the `BlockWaypoint` scalar block-read tail (`ReadEach`, `ReadOne`) cursor variants as code-generation probes,
  not throughput wins: stepping byrefs removed stride multiplies and shrank the scalar listings, but no variant gained
  repeatably with SIMD, and byte-identical A/A controls varied by up to 32-42% on short batches. Measure the case with
  the separate-process protocol (`benchmark`). M4 timings; x64 cross listings only.
  - Keep the dual-cursor scalar block-read tail as a candidate while diagnosing JIT code generation. On the local
    `BlockWaypoint` probe, two bumped cursors removed the two loop multiplications but grew `ReadEach` by 33 IL bytes
    and did not consistently improve the direct or public read paths. Moving the same two-cursor loop to a no-inline
    tail helper improved 1,025-element reads with SIMD enabled by 6–11% across four local rounds, but the one-element
    cases paid for the new call. Keep the long-run candidate and measure a short-tail route separately.
  - A single-element early return inside `ReadEach` sped up that isolated case, but erased the long-run gain. Moving
    it to `ReadUnchecked` grew the inlined rejecting caller from 176 to 304 Arm64 Tier 1 bytes and showed no
    consistent gain at the public entry. Check the generated interceptor path before tuning further.
  - The same probe rebuilt with the generated `NinoDirect.Deserialize` interceptor also showed no repeatable
    1,025-element gain for either helper placement. Warm the generated caller before measuring so that the Tier 1
    listings are available. In the Arm64 replay, both cursors stayed in registers with no hot-loop calls or spills,
    and the public caller stayed at 644 bytes. Splitting the loop changed 240 bytes of `ReadEach` into 152 bytes plus
    a 136-byte tail, with two stride multiplications at the tail boundary. The caller-side single-item dispatch
    enlarged `ReadRejecting` from 176 to 304 bytes even on the long case. All variants allocated 0 B per read and
    timings varied across rounds. Compare short-tail call placement while keeping the cursor loop fixed. Dispatching
    the actual remainder after SIMD directly to `ReadOne` when it is one increased `ReadEach` from 459 to 481 IL bytes
    and from 240 to 272 Arm64 Tier 1 bytes. The x64 FullOpts cross listing grew from 240 to 260 bytes. With 64 items
    and a one-byte unaligned start, the direct tail was consistently slower with hardware intrinsics enabled and
    within noise with them disabled. The 1,025-item result was within noise with either setting. The probe verified
    equal wire bytes and 0 B per read. Keep the dual-cursor helper available and isolate the SIMD-side dispatch from
    the pure scalar path before testing another tail shape. A gate on `Vector128.IsHardwareAccelerated` folded away on
    the SIMD path and kept its Arm64 Tier 1 instructions identical, although the 64-item timing difference persisted.
    With hardware intrinsics disabled, cursor stepping removed the loop multiplications and reduced Tier 1 from 172 to
    148 Arm64 bytes and the x64 FullOpts cross listing from 140 to 113 bytes. Reads of 1,025 items and the
    1,640/1,700-item pair straddling a 64 KiB source-plus-destination budget were within 0.5% across four rounds. The
    two sides emitted identical wire bytes and allocations matched. Keep this as evidence of smaller scalar machine
    code. The extra branch and duplicate loop raised `ReadEach` from 459 to 503 IL bytes. A byte-identical A/A control
    reproduced a timing gap across load contexts. With a process per build and offsets 0, 1, and 7, the 64-item SIMD
    result showed no repeatable regression across four rounds per offset. Use the separate-process protocol from
    `benchmark` for this case.
  - Stepping only the source byref in the shared tail removed its 15-byte stride multiply on Arm64 Tier 1 while
    leaving the 24-byte destination multiply. This kept the SIMD dispatch and avoided duplicating the tail. `ReadEach`
    grew from 459 to 470 IL bytes. Arm64 Tier 1 went from 240 to 244 bytes with SIMD and from 172 to 160 without it.
    The x64 FullOpts cross listing went from 186 to 192 bytes with SIMD. At a one-byte offset, four default-tiering
    process-per-build A/B rounds showed no repeatable gain with SIMD, and scalar reads of 1,025, 1,640 and 1,700 items
    differed by at most 0.22%. All timed reads allocated 0 B. The full-value zero stores before the field writes
    remained in the loop. Check those stores independently of cursor stepping in the next variant, preserving zero
    padding and bool validation.
  - Compare the loop entry as well as its exit condition. Replacing the dual-cursor countdown with an equality check
    against the last valid destination byref removed the decrement, but both loops cloned the first element's field
    reads on Arm64 scalar Tier 1. In baseline, countdown, and end-byref order, `ReadEach` was 459/493/503 IL bytes,
    164/204/220 Arm64 scalar Tier 1 bytes, and 135/106/122 x64 scalar FullOpts cross-JIT bytes. With SIMD those
    machine-code sizes were 232/244/260 on Arm64 and 180/190/209 in the x64 listing. Four default-tiering
    process-per-build rounds through the generated array interceptor at offset 1 showed no repeatable gain at 1, 65,
    1,640 or 1,700 elements. Identical binaries varied by up to 32% on the 65-element SIMD case. Reads allocated 0 B
    and hashes matched at offsets 0, 1 and 7. The end-byref candidate passed 21 targeted tests on .NET 10. Keep both
    patches and try a pretested loop with the same cursor steps to isolate the first-iteration cloning.
  - Testing `remaining > 0` before the body and removing the separate empty-tail guard kept a single field-read body
    in Arm64 scalar Tier 1. Compared with the original indexed loop, IL went from 459 to 492 bytes, Arm64 scalar Tier
    1 from 164 to 140, and the x64 scalar FullOpts cross listing from 135 to 108. With SIMD, the listings grew from
    232 to 248 and from 180 to 191 bytes. Scalar timing stayed within 5% in all four default-tiering rounds. At 1,700
    items with SIMD, all four initial rounds favored the pretested loop, but an alternating replay with an identical
    baseline control showed up to 40% A/A variation. Keep the patch as a code-generation result and measure the tail
    separately before attributing a throughput gain. Wire bytes matched at offsets 0, 1 and 7, reads allocated 0 B,
    and the block, versioning, reference, and deferred-body checks passed on the tested .NET targets.
  - Keep the original index as the pretested loop condition while advancing both byrefs. This removes the
    remaining-count subtraction from the SIMD tail entry and still avoids first-iteration cloning on Arm64 scalar Tier
    1. Against the indexed-address baseline, IL was 459 versus 484 bytes, Arm64 Tier 1 was 164 versus 136 bytes
    without SIMD and 232 versus 244 with SIMD, and the x64 FullOpts cross listings were 135 versus 106 and 180 versus
    191. The 7-element case stayed below the SIMD entry threshold. At 65 and 1,700 elements, extending each timed
    batch and alternating with a byte-identical control reduced the worst A/A spread from about 42% in the short
    65-element batches to 12.2% at 65 and 6.4% at 1,700. The candidate did not show a repeatable throughput gain. Keep
    the patch for code-generation comparisons. Both sides allocated 0 B per read and produced identical wire bytes at
    offsets 0, 1 and 7. The candidate passed 22 targeted tests on each of .NET 8 and .NET 10.
- Count zeroing stores as well as bytes before specializing a padded scalar read. The `BlockWaypoint` 15-byte wire /
  24-byte memory probe used the validated map to narrow its two zero stores from 24 bytes to 10, including the bool
  byte overwritten by the formatter. `ReadOne` grew from 37 to 77 IL bytes and `ReadEach` stayed at 459. Arm64 Tier 1
  stayed at 240 bytes with SIMD and 172 without it, and the x64 FullOpts SIMD cross listing went from 186 to 185. Four
  separate-process A/B rounds at offset 1 allocated 0 B per read. The 1,700-item scalar case stayed within 0.11% and
  with SIMD it improved by 0.48–3.14%, within the local noise for this case. Sizes 1 and 65 varied in both directions.
  The probe compared wire bytes at offsets 0, 1 and 7, checked zero padding on reused arrays prefilled with 0xCD, and
  rejected truncated input and a final bool of 2, with SIMD on and off. Keep the layout-specific variant as a probe.
  Reducing the number of stores would require combining padding zeroing with a field store while preserving field
  validation.
  - A little-endian, exact-map `BlockWaypoint` probe fused the padding into the short and bool stores and paired the
    floats in one 8-byte move. The JIT emitted four stores instead of seven. With intrinsics off, 65 items read
    37.4–37.9% faster and 1,700 items 33.0–34.8% faster across four process-per-build rounds at offset 1. With SIMD
    on, small cases varied and 1,700 items were slower in three rounds. Allocation stayed at 0 B. `ReadOne` grew from
    37 to 134 IL bytes while `ReadEach` Arm64 Tier 1 shrank from 172 to 148 bytes without SIMD and 240 to 220 with it.
    The x64 FullOpts cross listing shrank from 140 to 107 and 186 to 153 bytes respectively. The same byte, padding,
    truncation and invalid-bool checks passed. Keep the probe for the scalar path. A general implementation needs an
    explicit generated-formatter guarantee before replacing its field reads. An exact byte map alone does not preserve
    a custom formatter's validation.
  - Gating the same probe on `!Vector128.IsHardwareAccelerated` restored the original SIMD Arm64 Tier 1 instruction
    stream after normalizing call addresses. The x64 FullOpts cross listing returned to 186 bytes with SIMD and stayed
    at 107 without it. `ReadOne` reached 141 IL bytes. Four default-tiering A/B rounds at offset 1 retained 37.5–39.7%
    faster scalar reads at 65 items and 34.3–35.0% at 1,700, with 0 B allocated. One item varied, including a 24%
    scalar regression in one round. SIMD timings varied in both directions. Repeat the byte, dirty-padding, truncation
    and invalid-bool checks on both paths before trying a general implementation.

## Block writes

- Specialize the one-item block write in `WriteAll` only through a 64-bit 8- to 16-byte contiguous prefix written as
  two word stores plus one mapped final byte (`d2688fac`, `fe927629`); keep per-value padding stores inline on the
  ordinary tuple path, and any further isolation local to the one-item specialization. M4 timings; offline x64
  listings only.
  - Measure one-item block writes through the public serializer before choosing the branch placement. In the
    `BlockWaypoint[]` probe, bypassing `WriteEach` from `WriteAll` improved the single-item median in all eight
    default-tiering rounds, by 12–40%. Larger inputs varied, including large differences in the identical-binary A/A
    control. Wire bytes matched at offsets 0, 1 and 7 for zero, one, two, 65 and 1,700 items, and timed writes
    allocated 0 B. `WriteAll` grew from 32 to 44 IL bytes and the Arm64 Tier 1 caller from 484 to 548. The x64 cross
    listing showed the same caller and larger-input loop sizes, but the array formatter reached only MinOpts there.
    Moving the condition inside `WriteEach` kept the Arm64 caller at 484 bytes, enlarged its 65-item loop from 196 to
    260 bytes, and lost the repeatable single-item gain. A no-inline single-item helper limited the Arm64 caller to
    520 bytes, but had variable timings. Keep the inline outer candidate for comparison. Four mixed-profile rounds
    with five one-item writes per eight calls improved by 2.6–10.6%. The profile containing one 1,700-item write per
    eight varied from flat to 33% slower. A byte-identical A/A control was 4–10% apart on that profile. The
    large-weighted mix varied in both directions. Allocations stayed at 0 B and the wire comparison passed. Use
    `crossgen2 -O` to get an optimized offline x64 listing. With this specialization, `ArrayFormatter.Serialize` grew
    from 2,117 to 3,232 bytes and `WriteAll` from 356 to 1,241 bytes. The added branch inlined layout initialization,
    padding clearing, and layout conversion paths that were absent from the baseline caller. Keep the prototype
    separate until the scalar store can be inlined without pulling those paths into the caller.
  - A primitive-only layout bypass cut the candidate's offline x64 `WriteAll` from 1,241 to 556 bytes and
    `ArrayFormatter.Serialize` from 3,232 to 2,459. Explicit type comparisons grew `NinoLayout<T>.Write` from 30 to
    383 IL bytes. `Type.IsPrimitive` with both native pointer types excluded produced the same code sizes with 109 IL
    bytes. Both candidates kept the Arm64 mixed-profile caller and loop at 548 and 196 Tier 1 bytes, and matched wire
    output and 0 B allocations. The explicit comparisons kept the small mix gain and had mixed large-run results. The
    property version was 7–17% faster in the mixed profile but 9–12% slower across all four large-profile rounds. Keep
    both as probes and check code layout under the large-run profile before adding a broad primitive branch to the
    shared layout writer.
  - Marking only `WriteConverted` no-inline removed its two conversion bodies from offline x64 `WriteAll`, reducing
    that listing from 1,241 to 1,159 bytes, but the inlined array formatter grew from 3,232 to 3,431. Arm64 Tier 1
    still emitted the 548-byte caller and 196-byte loop. The small-weighted profile remained faster in all four rounds
    versus the production baseline, with variable mixed and large profiles. Wire bytes matched at offsets 0, 1 and 7,
    and allocations stayed at 0 B. Isolate the padding paths next if revisiting this shape, since they still expand
    inside the x64 caller after removing the conversion bodies.
  - Moving only padding clearing behind a no-inline call reduced the outer candidate's offline x64 `WriteAll` from
    1,241 to 720 bytes and `ArrayFormatter.Serialize` from 3,232 to 2,807. `NinoLayout<T>.Write` grew from 30 to 36 IL
    bytes plus an 8-byte helper. The non-padded `BlockWaypoint` Arm64 caller and loop stayed at 548 and 196 bytes. A
    tuple containing `PaddedPlain` from the padding tests used the helper once per element, enlarging Arm64
    `WriteEach` from 72 to 112 bytes plus the 28-byte helper. Against the same outer branch with inline padding, the
    padded large-weighted workload slowed by 43–48% in every round. Both probes wrote identical bytes at offsets 0, 1
    and 7 and allocated 0 B. Keep per-value padding stores inline on the ordinary tuple path. Any further isolation
    must be local to the one-item specialization.
  - For a 15-byte block with an identity-mapped 14-byte prefix, the one-item branch can use two overlapping 8-byte
    stores and one mapped final byte. The validated map keeps the source and destination accesses within the value and
    the block. Restrict the probe to 64-bit runtimes. It kept the Arm64 multi-item loop at 196 bytes and reduced the
    outer candidate's caller from 548 to 520. Offline x64 `WriteAll` went from 1,241 to 406 bytes and
    `ArrayFormatter.Serialize` from 3,232 to 2,217. `WriteAll` grew from 32 to 114 IL bytes versus production. The
    small mix improved in three of four rounds, with variable larger mixes, byte-identical output at offsets 0, 1 and
    7, and 0 B allocations. Keep the shape-specific probe for comparison with a general contiguous-prefix plan.
  - Generalize this to an 8- to 16-byte prefix and one mapped final byte. The 13- and 15-byte qualifying shapes
    improved 8.9–13.1% and 4.5–11.2% in four small-weighted Arm64 rounds. The 11-byte nonqualifying control kept its
    caller and loop sizes and had no consistent slowdown. Larger profiles varied. The general `WriteAll` is 121 IL
    bytes. Arm64 stayed at 520 bytes for the mixed-profile caller and 196 for the 15-byte loop, while optimized
    offline x64 used 440 bytes in `WriteAll` and 2,257 in `ArrayFormatter.Serialize`. All probes allocated 0 B and
    matched wire bytes at offsets 0, 1 and 7. Targeted .NET 8/10 tests passed with and without hardware intrinsics
    (`d2688fac`).
  - Keep the two explicit word stores for the variable prefix length. Replacing them with `Unsafe.CopyBlockUnaligned`
    reduced `WriteAll` from 121 to 89 IL bytes, with the same Arm64 Tier 1 sizes for both shapes. Offline x64 shrank
    from 440 to 434 bytes in `WriteAll`, and from 2,257 to 2,245 in the array formatter, but introduced a
    `CORINFO_HELP_MEMCPY` call in the single-item path. Four local rounds for each shape had no stable timing gain, 0
    B allocations, and byte-identical writes at offsets 0, 1 and 7.
  - Express those stores through the existing `NinoUnsafe` unaligned helpers. `WriteAll` shrank from 121 to 112 IL
    bytes without changing the measured Arm64 Tier 1 or offline x64 machine-code sizes or introducing a copy helper
    call. Four rounds for each shape had variable timings and 0 B allocation. Wire bytes matched at offsets 0, 1 and
    7, and targeted .NET 8/10 tests passed with hardware intrinsics on and off (`fe927629`).
- Keep the write countdown when Tier 1 already emits a single cursor loop. On `BlockWaypoint[]`, a pretested index
  with the same byrefs reduced `WriteEach` from 424 to 414 IL bytes and reduced Arm64 Tier 0 by 60 bytes. Scalar Tier
  1 stayed at 92 bytes and SIMD Tier 1 grew from 196 to 200. The sampled x64 FullOpts cross listings went from 244 to
  245 and stayed at 276 bytes for the second compilation. Small and mixed timings showed no repeatable gain. A large
  SIMD mix initially favored the candidate, but four replay rounds with a byte-identical control did not reproduce it.
  Wire bytes matched at offsets 0, 1 and 7, writes allocated 0 B, and 22 targeted tests passed on each of .NET 8 and
  .NET 10.
