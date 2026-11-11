# Strings (.NET)

Part of the `optimize` skill ([vectors.md](vectors.md) holds the general loop rules): the vector loops of string reads
and writes: reusing an existing string by comparison, Latin-1 widening and narrowing, and the UTF-8 and UTF-16
payloads. Inlining around `NinoStrings.Read` is `jit.md`.

Platform scope: a bullet that names a GitHub runner run (or "runners") was measured on every runner CPU. The others
were timed on a local Apple M4 (Arm64, default JIT) or, where they say so, on an emulated x64 AVX2 guest; their x64
evidence is listings only (Crossgen2, cross-JIT), and the rules lists mark them "M4". Measure on native x64 before
carrying such a threshold or verdict there (`benchmark`).

Contents: [Rules](#rules), [Short reuse](#short-reuse), [Long comparison](#long-comparison), [Widening
(reads)](#widening-reads), [Narrowing (writes)](#narrowing-writes), [UTF-8 and UTF-16](#utf-8-and-utf-16).

## Rules

- Compare Latin-1 with a full-character comparator, never `Ascii.Equals` (correctness: ASCII-only equality silently
  defeats reuse).
- In short reuse, carry a failed comparison into the fallback through the caller's local; widen one vector a block on
  x64 (AVX2, AVX-512BW); keep 24-63 chars on Arm64 in an optimized out-of-line 16-byte batch; test the x64 baseline
  ISA before an Arm64-only path for ReadyToRun; keep non-ASCII UTF-8 reuse separate (M4; native x64 unmeasured).
- Compare long Latin-1 tail first in widened UTF-16 lanes, one loop for the three widths with
  `AggressiveOptimization`; Arm64 batches two 16-char comparisons from 128 chars (M4; x64 listings).
- Widen full vectors then one final vector; keep the tuple form and the 128-bit Arm64 loop; align the middle stores of
  long Arm64 reads behind the 512-char guard; fold the widening loads into `vpmovzxbw` on AVX2 and AVX-512BW (M4 and
  AVX2 guest; native AVX-512 unmeasured).
- Narrow after `Ascii.FromUtf16`; outline the 256-bit (32-63 chars) and 512-bit (128+) writers behind exact ISA tests;
  full vectors with a checked overlapping tail; outline and eagerly optimize only the long remainder; keep the
  mask-based range check (M4 and AVX2 guest).
- Place long UTF-8 at its final offset; keep the fixed Arm64 head and tail for the 33-63-char UTF-16 fallback; copy
  large `StringBuilder` values with `CopyTo`; compare bytes with chars eight at a time where no vectors are.

## Short reuse

- Compare Latin-1 with a full-character Latin-1 comparator, never `Ascii.Equals`: ASCII-only equality rejects
  unchanged non-ASCII text and can silently defeat reuse while returning correct decoded values. `804d7185` makes
  `NinoStrings.SameLatin1` use the existing portable comparator on every target. The mixed nine-string array probe at
  1/4/8/16/31/32/63/64/65 chars replaced seven strings on its first read and allocated 456 B per steady reuse before
  the fix, and retained all strings at 0 B after it, with intrinsics on and off. Four alternating M4 default-JIT
  rounds improved the Latin-1 array median by 32%; ASCII controls showed no consistent regression. These local results
  establish the allocation fix and a candidate benefit, not published runner throughput. The helper IL is 8 to 10 B;
  Arm64 FullOpts is 48 to 40 B plus the existing 244 B comparator, offline x64 13 to 15 B. Guarded short comparisons
  cover every mismatch position including equal low bytes with different UTF-16 high bytes.
- Carry a failed short Latin-1 comparison into the fallback through the existing caller local (`6723d580`), without
  changing the default decoder. `TryReuseShortLatin1` clears a by-value reader parameter only after a complete
  same-length short comparison fails; other shapes keep the original value. The inlined ref argument does not expose
  the user field, and the managed source ref retains any aliased object. The original `NinoStrings.Read` signature and
  174 B IL remain byte-identical. Short ASCII/Latin-1 misses in four warmed M4 default-JIT A/B rounds improved roughly
  8–20% for a front edit and 20–38% for a tail edit; equal strings and 64/65-char controls were on par. Match
  allocations stayed 0 B and misses kept the original allocation. The actual `SameLatin1` call disappears from the
  short-miss trace. Reader IL grows 38 to 39 B, helper IL 49 to 57 B; Arm64 FullOpts is 1,240 to 1,244 B. Offline x64
  reader sizes are 968 to 941 B at v2, 987 to 969 B at v3, 985 to 967 B at v4; no native x64 throughput was measured.
  The pooled FullOpts comparison flags no regression among 209 methods, and all 104 generated Nino benchmark files
  remain byte-identical at 523,973 B. Changed 7–63-char reads touch at most 315 payload bytes across input, current
  chars and output, with no temporary buffer; one-char sharing uses the existing cache. Preserve the failed variants:
  directly inlining the checked decode grew the Arm64 entry to 1,764 B; outlining it kept a 1,292 B entry plus a 520 B
  helper. A folded bool on the default decoder produced a 1,208 B entry but broke fresh-formatter inlining (see
  `jit.md`); a caller-side header peek added long-string overhead, and a signed match count added work to some matched
  cases. Keep only the original bool result with local-value clearing. Tests cover all relevant header shapes, changed
  lengths, null/empty, UTF-16 forced by StringBuilder, exact next byte consumption at offsets 0/1/7, truncation,
  high-byte differences and unchanged identity. Exact-name reference tracking and deep-deferred selections add 25
  passing cases per compatibility configuration (`verify`). Warmup pitfalls belong to `benchmark`; do not count the
  early 100 ms per-case logs as steady-state results. The local Strings fuzzer processed 3,516,160 inputs with
  intrinsics and 3,545,024 without them, with no findings; its planted-bug selftest passed. The shipped netstandard
  Core DLL and related selection passed 31 cases.
- Measure the short Latin-1 dispatcher and neighbouring sizes when widening the batch. For x64 short reuse, widen one
  result vector per block: 16 source bytes to 16 UTF-16 chars with AVX2, or 32 to 32 with AVX-512BW. Share
  `SameLatin1Blocks` with the long comparators through two small policies; select 512, 256 and 128 outside the loop,
  retaining `Prefer512Int16`. Test the 32-char bound before reading the CPU preference. Keep the x64 dispatch out of
  the Arm64 24–63-char path: its normalized FullOpts reader remains 1,240 B and its medium helper 148 B. Four
  default-JIT A/B rounds with repeated-binary controls were on par across 15/16/17/23/24/31/32/33/47/63/64/65 chars,
  offsets 0 and 7, equal strings and front/tail edits; equal reads allocated 0 B. The short comparator IL grows 309 to
  338 B, plus a 47 B dispatch and 80 B of policies; the shared loop stays 67 B. Offline x64 helpers emit YMM at 78 B
  or YMM/ZMM at 205 B. Reader code stays 968 B on the 128-bit target and grows 963 to 987 B on AVX2, 959 to 985 B on
  AVX-512: coverage, not a code-size win. Native x64 throughput remains unmeasured (`ad78c40f`). Portable guarded
  tests make 20,736 comparisons at both widths, with every tail length, shifted source alignments and high/low-byte
  edits. Source plus compared chars are at most 189 payload bytes, with no table or temporary buffer, within the 64
  KiB budget. The real benchmark's 104 generated Nino files remain byte-identical at 523,973 B; `PooledSimpleClass`
  calls `ReadString(__c)` in its reuse formatter. Each filtered FullOpts comparison flags no regression among 308
  methods.
- For Arm64, keep the 24–63-character 16-byte widening batch in a fully optimized out-of-line helper. Reuse the long
  128-bit kernel's difference operation, accumulating until the final test. In the M4 default-JIT probe that
  alternated offsets 0 and 7 inside the timed loop, 24, 32 and 47 characters improved by 13–26% across four pairs, and
  63 characters by 20–53% with noisier repeated-binary controls. Separate offset-7 processes also improved those
  sizes. A probe timing offsets successively in one process gave conflicting results at the last offset. Keep 15, 23,
  64 and 65 as controls. Wire bytes matched, all mismatch positions were detected, and unchanged reads allocated 0 B.
  The short comparator grew from 277 to 299 IL bytes plus a 57 B helper. Arm64 FullOpts reader grew from 1,208 to
  1,240 B plus a 148 B helper. Offline x64 ReadyToRun grew from 978 to 1,038 B and retained the hardware test as a
  call. It does not measure the optimized x64 JIT or throughput (`573a443f`).
  - Reusing the long comparison kernels inline improved 31–63-character M4 reuse, but the offset-zero 64-character
    control slowed in four pairs. Outlining a 32–63-character helper with 512-, 256- and 128-bit dispatch retained a
    1–66% gain at 63 characters across both offsets, but 31 characters and the offset-zero 64-character control still
    slowed in all four pairs. The short comparator grew from 277 to 292 IL bytes plus a 48 B helper. Arm64 Tier 1
    `ReadStringOver` grew from 1,224 to 1,276 B. The new helper was 148 B on Arm64 and 254 B on cross-JIT x64, where
    the public Tier 1 entry stayed at 159 B. All wire bytes, mismatch probes and steady allocations matched, and the
    eight targeted .NET 10 tests passed. Both candidates were withdrawn.
- Check the x64 baseline ISA before the Arm64-only path when ReadyToRun leaves the foreign ISA check as a call. The
  SSE2 guard let .NET 10 Crossgen2 remove both calls on Linux x64 and return the reader from 1,038 to 978 bytes.
  Normalized Linux Arm64 ReadyToRun stayed identical at 1,220 bytes, and M4 FullOpts reader/helper stayed at 1,240/148
  bytes. Short comparator IL grew from 299 to 309 bytes. Four mixed-offset M4 default-JIT pairs at each of eight sizes
  had median differences within 3%, identical wire bytes, and 0 B steady allocation. x64 throughput was not measured
  (`6ff3bdf2`).
- Keep non-ASCII UTF-8 reuse separate from the short ASCII reader. Decoding to a bounded stack buffer removed
  allocations for unchanged CJK and emoji but penalized changed values that had to copy to a string. Restricting the
  trial to short headers avoided changing the fresh parser. Re-encoding to a byte buffer helped unchanged CJK but
  slowed emoji. Restricting that to exactly three bytes per UTF-16 unit and checking the first and last bytes removed
  the emoji regression, but changed ASCII or same-width CJK still regressed even after reusing the checked short
  header on a miss. Unchanged eight- to 42-character CJK reads went from 40–112 B allocated to zero. The final reader
  grew from 178 to 316 IL bytes plus a 58 B helper, and from 324 to 552 Arm64 bytes plus a 408 B helper. The x64 cross
  JIT only emitted the helper at 470 B in MinOpts. Wire bytes and strict decoding tests matched, with 31 targeted
  tests passing on both .NET 8 and 10. The candidates were withdrawn.

## Long comparison

- Compare long Latin-1 input to an existing string by widening bytes and comparing UTF-16 lanes. Start with the last
  complete vector to reject tail edits early. Keep this out of the short-string comparison, and place boundary tests
  at the end of a guarded page to catch a load past either end of the input.
  - On Arm64, batch two 16-character comparisons into one horizontal reduction for strings of at least 128 chars. Keep
    the tail-first comparison and reuse the existing widened difference operation. The 21,823- and 21,847-char reuse
    cases, which straddle 64 KiB including wire and UTF-16 input, took 14–18% less time in four M4 pairs at offsets
    zero and one, at zero steady allocation. Shorter and mismatched controls varied with A/A. The first read was 2–6%
    slower against the paired baseline and overlapped the range of identical-binary controls. The entry grew from 83
    to 108 IL bytes and 180 to 220 Arm64 FullOpts bytes, with a 93 IL/304 Arm64 byte helper. Emulated x64 kept the 126
    B and 139 B listings with AVX2 off and on. Wire bytes matched and the guarded test covers every mismatch position
    from 127 through 161 chars (`a9ed6912`).
  - Share the tail-first check and remainder loop between both Arm64 sizes and inline them in the already optimized
    entry. Inlining alone left 476 Arm64 FullOpts bytes; sharing the tail brought the original 220 + 304 B down to 328
    B. Combined IL grew from 201 to 214 B. Emulated x64 retained identical instructions at 126 and 139 B. The initial
    65-char offset-zero slowdown did not repeat with a longer warmup, and the near-64-KiB cases showed no repeatable
    change against the existing batched loop. Long-warm reuse allocated 0 B and wire bytes matched. First-use timing
    varied alongside same-binary controls. Both .NET suites passed 44 tests and the scalar suite passed 14
    (`a7849144`).
- Share long Latin-1 equality's tail-first loop across the three native SIMD widths, and compile the entry with
  `AggressiveOptimization`. Without it, the generic loop slowed 65- and 256-character reuse in all four rounds. With
  the same optimization setting, the old and shared loops measured similarly. Against default JIT baselines,
  65-character reuse fell from about 76 to 48–51 ns, and two cases straddling 64 KiB fell from roughly 9–10.5 to
  0.8–1.2 µs at 0 B per reuse. First read rose from 0.75 to 0.98 ms at the median. Arm64 FullOpts fell from 184 to 180
  bytes. x64 cross-JIT compare loops measured 129/135/175 bytes and emitted XMM/YMM/ZMM instructions. The direct probe
  checked equal inputs and every mismatch position at all three widths, including non-Latin-1 chars.

## Widening (reads)

- Run the 128-bit Latin-1 widening loop over full vectors, then write the final vector once. This removes the
  per-vector tail choice while preserving the overlapping tail and the AVX2 path. The Arm64 builder probe improved at
  256 and 16,384 characters in most rounds. The 31 character recheck was flat within the A/A variation. A separate
  short-vector branch regressed the 16,384-character control, so keep the shared loop. Wire bytes and allocations
  matched. For x64 listings of this vector method, compile Nino.Core and System.Private.CoreLib in the same Crossgen2
  composite version bubble. A single-assembly compile skips the method because its vector call requires a compatible
  ABI. The retained loop reduced the offline listing from 648 to 539 bytes with the baseline ISA and from 609 to 493
  with AVX2. Replacing the tuple widening call with explicit lower and upper operations removed seven helper IL bytes
  but left the Arm64 Tier 1 and both x64 listing sizes unchanged. It slowed the 256-character local probe in all four
  rounds, so keep the tuple form.
- Keep the existing 128-bit widening loop on Arm64 after the paired-block trials. Two indexed blocks cut loop branches
  but retained per-block address arithmetic. The outlined byref version formed paired stores and left the x64 width
  choices alone, but both versions slowed the 16-character reused builder at offset seven. Moving only the two stores
  to one destination base formed `stp` without widening the loop. That version kept the entry at 177 IL bytes, grew
  the helper from 43 to 48 IL bytes, and reduced Arm64 Tier 1 from 104 to 96 bytes. Offline x64 v2/v3/v4 moved from
  79/184/243 to 83/188/245 bytes and the 156-byte AVX-512BW helper kept its folded memory loads. Near 64 KiB of live
  input and output the kernel's timing overlapped the A/A controls. The 64-character public builder read slowed in all
  four broad sweeps at offset seven and all four isolated comparisons at offsets zero, one and seven. The direct
  kernel did not reproduce that slowdown. The builder caller and the timing wrapper kept their Tier 1 sizes. All
  variants matched the 834 wire, refill and truncation probes, allocated 0 B per timed read, and the shared-address
  version matched all 584 generated files. Retain the scratch patches when revisiting the caller timing or the x64
  AVX2 widen loads.
- Align the middle stores of long Arm64 Latin-1 reads when the UTF-16 destination starts off a 16-byte boundary. Write
  the head once and use its overlap to reach a 32-byte boundary. Keep the 512-character guard at the existing
  long-header and builder-chunk callers. Putting it in the shared entry slowed the 16-character kernel by about 17%.
  The unconditional paired-store version showed no repeatable gain when the source and destination placements were
  swept independently. With alignment, at 4,096 characters and destination offset 2 in a 128-byte region, all four M4
  pairs improved by 32–34%. A new string from offset seven improved by 16–22% at 21,844 characters and 3–21% at
  21,846, either side of a 64 KiB live input/output budget. Builder refill timings around that boundary varied and its
  steady allocation stayed zero. The isolated 31-character string comparison overlapped the controls. The 177-byte IL
  and 104-byte Arm64 Tier 1 shared entry stayed the same. The guard adds 56 IL bytes and the new loop adds 85 IL and
  120 Arm64 Tier 1 bytes. Offline x64 v2/v3/v4 kept the widening entry at 79/184/243 bytes and the v4 helper at 156.
  The 5,598 direct alignment checks and 1,638 public wire and truncation checks matched, as did 584 real-project
  generated files. The .NET 8/10, scalar and both netstandard suites passed (`585c22c6`).
- For AVX-512BW, widen two 32-byte loads directly with the ISA's conversion and align the middle stores. A 64-byte
  load and tuple widening emitted a vector copy and half extraction. The direct conversion folds both loads into
  `vpmovzxbw` and reduced the v4 helper from 177 to 158 B. Gate the outlined call with the exact ISA intrinsic, then
  apply shared CPU preference inside. An inlinable wrapper around the hardware test still grew Arm64 instrumented Tier
  0 from 692 to 824 B, while Tier 1 did not grow. The exact ISA test restored Tier 0 size and 31–127-char timings. The
  M4 and AVX2 guest verify the fallback and portable kernel's bytes, including non-aligned inputs and tails. Native
  AVX-512 throughput remains unmeasured (`663b1861`).
- Reuse the folded 32-byte widen load in the 512-bit reuse comparison. Crossgen2 v4 lowered both loads to
  memory-operand `vpmovzxbw`, reducing the compare block from 75 to 57 B and its IL from 73 to 64 B. The widening
  helper retained folded loads and fell from 158 to 156 B. Portable block tests covered every mismatch lane and the
  public wire bytes matched with SIMD on and off. M4 fallback timings varied with process profile, and native AVX-512
  timing was unavailable (`4d10592c`).
- Share the 16-byte widening load between the AVX2 short comparator and both 32-character decode and comparison
  batches. This emits two memory-operand `vpmovzxbw` instructions instead of a YMM load, copy and half extraction.
  Default-JIT x64 guest code fell from 239 to 203 bytes for the widening entry and from 139 to 122 for long
  comparison. The offline v3 entry fell from 184 to 172 bytes. Arm64 Tier 1 instructions, offline v2 instructions and
  the v4 AVX-512BW helper matched. Widening IL went from 177 to 170 bytes, with a shared 39-byte helper, and the long
  comparator adapter fell from 73 to 64 bytes. In four emulated x64 pairs with input offset seven, the 4,096-character
  builder refill was 14–18% faster and string reuse was 11–20% faster, both with zero allocation. The
  21,844/21,846-character cases also improved. Cold first calls, isolated short controls and fixed-placement Arm64
  widening were within or mixed with A/A spread. Guest timing does not establish native x64 throughput. Both guest ISA
  configurations and Arm64 matched all direct and public checks, including same-low-byte mismatches and truncation.
  All 584 generated files and five targeted test configurations matched (`def92685`).

## Narrowing (writes)

- Continue Latin-1 narrowing at the prefix written by `Ascii.FromUtf16`, overlapping the final vector if needed. When
  the first unwritten character exceeds Latin-1, take the UTF-16 path immediately. For 32–63 chars, call an outlined
  256-bit writer directly without the ASCII setup. Check the first and last 32-char windows and write the header only
  after both succeed. Inlining the wide work grew the x64 narrow entry from 208 to 363 B and regressed the 17–31-char
  controls. Moving only the gate to the narrow entry also regressed the small Arm64 controls despite an unchanged
  FullOpts code size. Dispatch from the caller and keep the narrow entry intact. After the range check, AVX2
  saturating pack needs no input masks. It reduced the new x64 helper from 167 to 142 B and the long writer from 563
  to 546 B. Four local x64 guest A/B pairs favored the wide path for most 32–63-char inputs. The 47-char ASCII case at
  offset one regressed in the guest. Arm64 controls showed no stable regression and the two 64 KiB cases were noisy.
  Wire bytes matched with AVX2 on and off, and all timed loops allocated 0 B. Do not extrapolate guest timings to
  native x64 (`2202f782`).
- For a suffix of at least 128 chars, outline the 512-bit writer and keep CPU preference inside that entry. Keep the
  AVX2 fallback loop intact. Sharing its loop with the wider path changed guest code shape and hurt some 64- and
  127-char controls; splitting out the two ends also grew the v3 caller. With the separate wide entry, the x64 v3
  caller's instructions match the baseline at 543 B and Arm64 stays at 476 B. The v4 caller is 639 B versus 561 B,
  with a 260 B helper that emits two `vpmovwb` operations per 64 chars. Verify the portable kernel directly across
  offsets and tails. The AVX2 guest and M4 exercise the fallbacks; neither executes native AVX-512. Their timed loops
  allocated 0 B, and unstable guest timings around the threshold do not establish native throughput (`f9ce316e`).
- Use the exact AVX-512BW support check at the write entry too. Arm64 instrumented Tier 0 `NarrowVector` fell from 928
  to 776 B and Tier 1 from 748 to 712 B, with the same 218 B IL. The x64 cross-JIT MinOpts listing fell from 644 to
  637 B. Four local timed pairs overlapped the controls at zero allocations, and the byte comparison passed with SIMD
  enabled and disabled (`a74d9910`).
- Apply the same full-vector and overlapping-tail loop to 128-bit Latin-1 narrowing, checking each vector before
  writing it so UTF-16 fallback still works. The unaligned Arm64 writer probe improved at 64, 4,096 and 32,768 Latin-1
  characters in all four rounds, with identical wire bytes and zero allocations. The x64 composite listing left the
  vector helper out of line, so include its body when comparing offline size and do not infer x64 throughput.
- Keep the ASCII prefix conversion in the small entry and outline its remainder. For a narrow remainder of at least
  128 chars, validate and write the first block before checking the last character, then continue from the second
  block. A tail-first vector trial hurt short writes, a scalar tail precheck hurt early non-Latin-1 input, and
  changing the shared narrow loop hurt the 65-char ASCII control. With the split entry, four M4 pairs improved that
  ASCII control by 2–11% and 257-char late-UTF-16 fallback by 43–46%. Near 64 KiB of UTF-16 input plus output, late
  fallback improved 66–70% in most pairs; A/A had large outliers. Pure Latin-1 and early fallback controls varied by
  small amounts, and first-use timing was on par. Wire matched at offsets zero and one with 0 B timed allocation.
  Narrowing IL grew from 218 to 100 + 191 B. The Arm64 long writer went from 476 to 344 + 268 B; emulated x64 went
  from 395 to 255 + 249 B without AVX2 and 546 to 255 + 396 B with it. Both .NET suites passed 44 tests, plus 14
  scalar tests (`5dc7c91e`).
- Eagerly optimize only the long 128-bit remainder loop. Applying AggressiveOptimization to the whole dispatcher
  increased first-use serialization from about 13 to 17 ms and regressed fully warmed 65-char controls. Leaving
  dispatcher tiering intact and outlining the long loop improved the early 257-char Latin-1 workload by 66–71% and
  near-64-KiB Latin-1 writes by 53–56% in four M4 pairs. After allowing background Tier 1 compilation, timings were
  mixed alongside noisy A/A controls; do not call these steady-state gains. With the registry warm, the first Latin-1
  write took about 0.61 instead of 0.42 ms, versus 4.1 ms for the whole-method trial. Wire matched and timed
  allocation stayed 0 B. IL grew from 191 to 142 + 58 B and Arm64 FullOpts from 268 to 208 + 252 B. Emulated x64 was
  249 to 175 + 202 B without AVX2 and 396 to 329 B with it. Both .NET suites passed 44 tests and scalar passed 14
  (`c7c82e56`).
- Keep the mask-based range check in the narrowing loop. A direct unsigned vector comparison emitted `cmhi` on Arm64
  and slowed the 64-character probe. An Arm64-only maximum reduction emitted `umaxv` and shrank the hot writer from
  836 to 824 bytes, but the ASCII control was slower in all four reversed-order rounds. Its x64 composite helper also
  grew from 280 to 415 bytes. The variants matched the wire bytes and allocated nothing after warmup. Keep the
  mask-based range check.

## UTF-8 and UTF-16

- Use the counted byte length to place long UTF-8 payloads at their final offset. When strict transcoding stops at an
  unpaired surrogate, continue the replacement encoder at the consumed char and written byte offsets, which differ
  after a non-ASCII prefix. Keep the replacement work out of line.
- Keep the fixed Arm64 head and tail in the 33–63-character UTF-16 fallback with a known one-byte header. Putting the
  range in the shared UTF-16 writer slowed the 65-character control, including after reusing the small-copy
  dispatcher. The one-byte branch reduced M4 default-JIT medians by 27–36% across four pairs with first/last
  non-Latin-1 characters and offsets zero/one. The 32-, 64- and 65-character and ASCII controls were within the local
  spread, as were cases around the 64 KiB working set. All 13 wire comparisons matched at 0 B allocation. WriteLong
  grew from 81 to 131 IL bytes and 256 to 336 Arm64 FullOpts bytes; the caller stayed at 612 B and the two x64
  ReadyToRun listings matched exactly. Both framework suites, scalar string checks and netstandard builds passed
  (`c3fa2df1`).
- Copy large UTF-16 `StringBuilder` values directly into the reserved span with `CopyTo` (1b2e3404). `GetChunks` can
  allocate a chunk index, even for a short but unusually fragmented builder. Keep the modern small-write loop where it
  measured faster, and advance the reader by the exact byte count already checked.
- Compare bytes with chars eight at a time where no vectors are (netstandard `NinoStrings.SameBytes`): widen a ulong
  of bytes to two ulongs of chars (`x = (x | x << 16) & 0x0000FFFF0000FFFF; x = (x | x << 8) & 0x00FF00FF00FF00FF`) on
  little-endian machines, with an overlapping last step, instead of one char a step.
