# Copies (.NET)

Part of the `optimize` skill ([vectors.md](vectors.md) holds the general loop rules): block copies of raw arrays,
lists, `Memory<T>`, `BitArray`, sequences and byte writers, and the masked copy of padded structs (`NinoMask`).

Platform scope: a bullet that names a GitHub runner run (or "runners") was measured on every runner CPU. The others
were timed on a local Apple M4 (Arm64, default JIT) or, where they say so, on an emulated x64 AVX2 guest; their x64
evidence is listings only (Crossgen2, cross-JIT), and the rules lists mark them "M4". Measure on native x64 before
carrying such a threshold or verdict there (`benchmark`).

Contents: [Rules](#rules), [Short copies on Arm64](#short-copies-on-arm64), [Long copies and
writers](#long-copies-and-writers), [Sequences](#sequences), [Masked copy](#masked-copy).

## Rules

- On Arm64 copy 65-128 bytes as a fixed head and tail of two constant 64-byte `CopyBlockUnaligned` calls
  (`NinoUnsafe.CopyArm64Short`), tested with the one predicate `NinoUnsafe.IsArm64Short(size)` (its range constants
  are the only place the band is written) at the specific caller (raw array reads and fresh writes, one-byte lists,
  byte `Memory<T>`, `BitArray`, long Latin-1 strings), never in a shared dispatcher (M4 only). The reads started at
  81 bytes until the review round that unified the six call sites (each had its own range and gate); 65-80-byte
  reads on Arm64 are unmeasured since.
- Copy every payload of data-dependent size with `NinoUnsafe.Copy`, `CopyOver64` or `CopyToNew`, never a BCL span
  copy, `Buffer.Memmove`/`BlockCopy`/`MemoryCopy`, `BuffersExtensions.Write` or a variable-size
  `Unsafe.CopyBlockUnaligned` (Memmove: glibc's rep movsb on AMD lost to the loop 2-30 times up to 16 KB, on GitHub's
  EPYC runners);
  keep the BCL move only where source and destination may overlap (Nino's copy is no memmove above 64 bytes; test
  `Overlaps` first), for at most 64 bytes where a measured caller chose it (`ListFormatter` reads), and where a BCL type
  hides its storage (`StringBuilder.CopyTo` before .NET 8, `BitArray.CopyTo` before .NET 10).
- Keep the existing copies for raw arrays of zero to two elements, the overlap-safe span copy where a caller-owned
  buffer overlaps the source (skip ClearPadding without padding) and the raw list writer (M4).
- Copy long blocks per vendor and per destination; Ice Lake under Linux takes Memmove from 2,112 bytes to 32 KB
  (runners).
- Skip the copy of an empty growing writer; copy byte writer results up to 64 bytes inline (M4).
- Keep per-segment reservations and the per-segment Writer for `ReadOnlySequence` writes (M4).
- In the masked copy: full words in the scalar tail with the indexed loop and the predictable wrap branch; paired
  loads for one-, two- and three-word periods; 512/256/128-bit adapters; head and tail for one to two vectors; the
  final store owning a one-vector period; the inclusive multi-vector bound and the phase check kept; mask tables built
  by doubling with the modulo loop (M4 timings; x64 listings).

## Short copies on Arm64

- Keep the 81–128-byte Arm64 shortcut at the checked raw array reader. An inline branch and a separate helper in the
  shared copy dispatcher both slowed the 1 KiB direct control. Extracting the existing loop's vector group still
  affected that control, even after moving the branch. Leave the shared loop intact and express the short head and
  tail as two constant 64-byte `CopyBlockUnaligned` calls. The Arm64 JIT emitted paired 128-bit loads and stores in 64
  B. Four M4 rounds reduced reuse time by 39–45% and fresh reads by 27–41% at the covered sizes. Both offsets,
  surrounding sizes, wire bytes, sentinels and allocations were checked. Array deserialize IL grew from 102 to 133 B
  and its Arm64 caller from 564 to 608 B. The x64 cross caller stayed 195 B and shared copy listings stayed 580/1,096
  B on Arm64/x64. Both .NET suites, the no-intrinsics case and the netstandard builds passed (`894d6936`).
- Reuse the same short kernel for one-byte raw lists. Keep the array helper and guard unchanged. Loading both blocks
  before the stores or sharing an inline guard changed the array control. For a list with overlapping backing storage,
  use span copy in the outlined list entry. The final M4 byte-list probe improved reuse by 24–32% and fresh reads by
  12–18%, with matched bytes and allocations at offsets zero and one. Int32 timing varied even in A/A and its Arm64
  caller kept 564 B. List deserialize IL moved from 162 to 205 B. The byte-list caller moved from 948 to 992 B on
  Arm64 and stayed 205 B on x64 cross-JIT MinOpts. The list helper was 92 Arm64 bytes. Boundary, overlapping-buffer
  and no-intrinsics cases passed (`9ce599ed`).
- Select the short Arm64 copy inside the existing raw span branch for byte-sized `Memory<T>`. Checking it in the
  formatter duplicated raw dispatch and moved even the `int` control. The final M4 default-JIT probe reduced 81–128
  byte reuse medians by 23–27% across the tested sizes and buffer offsets. A separate per-size A/B with repeated
  same-binary controls put the 129-byte overhead below 5%; near a 64 KiB input-plus-output footprint, the controls
  were noisy. Reuse allocated 0 B, fresh allocations and wire hashes matched, and an overlapping slice kept the next
  field readable. The formatter and shared span reader moved from 119/255 to 120/296 IL bytes. Arm64 FullOpts moved
  from 1,296 to 1,340 bytes for `byte` and stayed at 1,256 bytes for `int`. x64 cross-JIT printed MinOpts, where the
  byte formatter moved from 475 to 480 bytes and emitted no short-copy call. Keep that listing separate from x64
  throughput (`7f4103cf`).
- Use the fixed Arm64 head and tail for fresh one-byte raw array writes at 65–128 bytes. The new output is distinct
  and the four-byte header deliberately offsets the destination. Starting at 81 bytes left an 80-byte slowdown.
  Putting the range check in the shared length-prefixed method also slowed the 64-byte Int32 control. Dispatch in the
  explicit allocating array formatter instead. Four M4 default-JIT A/B rounds with repeated-binary controls lowered
  65, 80, 96 and 128 byte medians by 28–37%; the 32, 64 and 1,024 byte controls and both sides of the 64 KiB
  input-plus-output footprint showed no meaningful regression. Byte output and allocation matched. Formatter IL grew
  from 46 to 110 B; Arm64 FullOpts went from 532 to 588 B and uses the existing 64 B helper. The public caller stayed
  at 660 B on Arm64, and x64 cross-JIT caller/formatter stayed at 501/419 B. The boundary wire checks and both
  frameworks passed (`7939e974`).
- Keep the existing allocating raw-array copy for zero to two elements. A fixed-size singleton copy improved that case
  but slowed empty byte arrays in the mixed-size replay. Extending the branch to zero through two fixed-size copies
  improved isolated short cases. It also made eight-element Int32 writes consistently slower in the randomized mixed
  replay, beyond two identical baseline controls. Isolating each length with longer warmup removed much of the general
  slowdown. Moving the short path out of line then slowed isolated Int32 singletons. Measure both mixed and isolated
  workloads and inspect Tier 0 and Tier 1 before interpreting a short-array result. The inline branch grew the byte
  formatter from 110 to 222 IL bytes, from 544 to 636 Arm64 FullOpts bytes and from 416 to 507 x64 composite bytes.
  The outlined entry and helper were 137 plus 86 IL bytes and 471 plus 71 x64 bytes. All 19 serialized outputs and
  allocation counts matched. First-use differences were comparable to the randomized baseline controls. Withdraw all
  three branches.
- Keep the overlap-safe span copy for raw arrays written to a caller-owned buffer that overlaps them (since e7f15a27 a
  disjoint one takes `CopyToWire`, [jit-arrays.md](jit-arrays.md#writing-into-an-external-buffer-writer); the M4
  result below is why `ExternalBufferTest` needs Arm64 runner lines). Adding the fixed Arm64 head and
  tail behind an overlap test slowed 65–128-byte writes by 7–21%. Replacing the checked destination slice with a
  prefix check plus byref span construction also slowed 64–1,024 bytes. Instead skip the Tier 0 ClearPadding call when
  the layout has no padding. Outlining the remaining padded path slowed a single padded item by about 4%, so keep that
  path inline. Four default-JIT M4 pairs with mixed offsets showed a 3–7% drop at 64, 65 and 80 bytes and a 5% drop
  for 16 ints. A longer cache-boundary retest varied in both directions, including repeated-binary controls. Wire
  bytes matched at 0 B steady allocation. IL grew from 156 to 167 B, Arm64 Tier 0 grew from 696 to 732 B and stopped
  executing the 296 B padding helper on byte arrays, Arm64 FullOpts stayed at 240 B, and x64 cross-JIT MinOpts grew
  from 706 to 748 B. Both modern frameworks and both netstandard builds passed (`85c0f052`).
- Keep the current raw list writer dispatch. Reusing the overlap-aware Arm64 short copy improved 65–128-byte byte-list
  writes, but the first inline dispatch slowed the 32- and 64-byte controls by about 22%. Moving the long path out of
  line, reserving once, and sharing the existing small-copy split still slowed 32 bytes. The last writer variant also
  slowed 16 Int32 items by about 7%. Trying only the allocating sized path improved 80–128 bytes by about 12–14%, but
  the 32- and 64-byte controls slowed by 19% and 16%. Sized writer IL grew from 55 to 128 bytes, Arm64 FullOpts
  SerializeSized from 764 to 812, and emulated x64 from 592 to 590. Output and allocation matched in all probes. Do
  not move the short array copy into the list writer without a dispatch that preserves the small-list controls.
- Refill a matching-length `BitArray` through its public byte span after validating the payload (cb6e950e). Setting
  its length to the same value clears the unused bits and invalidates existing enumerators. A single copy body for
  fresh and reused instances avoided duplicating the Arm64 copy code.
  - Reuse the fixed Arm64 head and tail for a disjoint 65–128-byte refill, behind the existing overlap check. Four
    default-JIT M4 pairs gave 15–26% lower medians at the tested sizes and offsets. The fresh and 64 KiB controls had
    no stable change. Fresh allocation matched and reuse allocated 0 B. All ten wire comparisons matched; the
    overlapping-buffer tests covered both endpoints. ReadBits grew from 189 to 234 IL bytes and 616 to 660 Arm64
    FullOpts bytes. Linux x64 offline ReadyToRun stayed at 440 B; it was not timed (`6defca8c`).
  - Fill a new BitArray of more than 64 bytes with the same copy (9eec0589): its zeroed storage cannot overlap the
    input, and the span move it took is Memmove (rep movsb on AMD runners). Not timed yet; `BitArrays` round-trips
    200,005 bits through the vector loop.
  - Use the same kernel on writes after the 64-byte split, and mask the final byte as before. Adding a separate
    unsigned range check before the shared copy slowed the 129-byte control. Dispatching larger Arm64 writes directly
    to CopyOver64 removed the repeatable regression. Four M4 pairs gave 21–37% lower medians for 65–128 bytes with
    mixed offsets and tail masks; the 96-byte follow-up also improved in all four pairs. Small and cache-boundary
    controls varied. All 12 wire cases matched with 0 B allocation. Serialize grew from 161 to 210 IL bytes and 828 to
    872 Arm64 FullOpts bytes; its caller stayed 196 B and offline x64 stayed 694 B. Dirty-tail and unaligned writer
    tests passed (`054e6900`).
  - Share the ReadyToRun-friendly Arm64 gate between the two BitArray copy paths. The getter adds 15 IL bytes and each
    caller saves 7. Arm64 FullOpts retained 872/660 bytes and offline x64 retained 694/440 bytes. Four M4 pairs showed
    no slowdown in the 96- and 129-byte writes, with zero allocation and identical wire bytes. Applying the gate to
    string and raw-array callers preserved sampled FullOpts sizes but moved default-JIT controls: 23-character Latin-1
    reuse slowed until both string call sites were restored, and fresh 96-byte array writes slowed by about 5%. Leave
    those gates local (`78f93168`).

## Long copies and writers

- Copy long blocks per vendor and per destination: past 32 KB a 32-byte vector loop beats Memmove into a new zeroed
  array on Intel (Xeon 8370C, 160,000 bytes: 3.18 µs instead of 3.51) and loses there on Zen 5 (EPYC 9V45: 1.81
  against 1.67), while on AMD it beats Memmove into a new uninitialized array (9V45 and 9V74: 0.93 to 0.95 times the
  time). `NinoUnsafe.CopyOver64` (zeroed or existing memory) takes it on Intel only, `CopyToNew` on AMD only. From
  2,112 bytes to 32 KB, Ice Lake under Linux takes Memmove instead of the 64-byte loop (`NinoUnsafe.IceLakeMemmove`):
  glibc runs it as one rep movsb from an aligned destination (FSRM), while the loop's loads split two lines unless
  source and destination sit alike in a line. Xeon 8370C (run 41188054612, n=12): 1,000 `KeyValuePair<int, int>`
  serialize 151 -> 128 ns (median 0.846; head 60 or 128 ns by process, base 106, 151 or 181), a `Queue<int>` of 1,000
  94 -> 74 (0.783; head 43 or 74, base 61 or 94), TupleList (not a copy) 1.015; the floor line drew no 8370C.
  Confirmed with a floor (run 41203402942, n=24, base rounds against each other as the floor): `KeyValuePair<int,
  int>` 0.844 (head 128 ns in 22 of 24 rounds; base 105, 151 or 182; floor median 1.006), `Queue<int>` 0.690 (head 74
  or 43, base 93-109 or 58; floor 0.999). Not on other CPUs: AMD's rep movsb lost to the loop 2-30 times up to 16 KB,
  and other C runtimes' memmove was not measured.
- Skip the copy when a growing byte writer is empty, otherwise copy its bounded written span
  (`NinoArrayBufferWriter.Grow`). The local Arm64 probe lowered growth latency with the same allocations, at the cost
  of a larger native helper.
- Copy byte writer results up to 64 bytes with the existing inline copy on 64-bit targets (88315b4c). Keep larger
  copies on `CopyToNew`; an extra out-of-line allocation-and-copy helper lost on the larger local cases. The
  short-string caller listing stayed identical while the copy helper grew.

## Sequences

- Keep per-segment reservations for general fixed-stride ReadOnlySequence writes. One counted reservation followed by
  bounded segment writes saved about 10% on the 2- and 4-segment Int32 probes but slowed a 16-segment small input.
  Outlining and forcing FullOpts made the 2- and 4-segment probes much faster against Tier 0 but almost doubled the
  16-segment case: the original reached PGO Tier 1 (1,544 bytes), while the outlined loop could not. After restoring
  normal tiering, the 4-segment 32-element probe improved by 4–6%, but 2 segments, 16 segments and the cases
  straddling a 64 KiB input/output footprint were flat or noisy. The final trial moved from 118 IL bytes to 137 plus
  121; Arm64 FullOpts from 2,432 to 1,304 plus 1,108 bytes; emulated x64 FullOpts from 2,855 to 1,397 plus 1,041
  bytes. All eight wire comparisons matched at zero steady allocation. Do not trade the mature PGO loop for this
  general reservation split.
- Keep the per-segment Writer local for variable-width value formatters as well. Inlining their element loops into the
  sequence writer helped tiny Int32 nullable segments, but the all-length version slowed 4,096 items in eight segments
  by about 43%. Sharing the element loop also moved the string control. Limiting the byref loop to up to four items
  and selecting it in an outlined method recovered the long cases, and four pairs improved 2 and 16 short segments by
  medians of 11% and 27%. Four eight-item segments still varied, with a 33% slower median and wide same-binary
  controls. On that path instrumented Arm64 Tier 0 grew from 392 to 664 bytes. For 16 segments, Tier 1 moved from
  1,512 to 1,000 plus 1,132 bytes; the emulated x64 FullOpts path grew from 5,852 to 2,919 plus 5,576, excluding the
  unchanged per-segment callee. Shared IL grew from 118 to 160 plus 122 bytes. All 11 wire comparisons matched at 0 B.
  Keep this experiment out of the general path until it avoids perturbing the fallback.

## Masked copy

- Copy whole words in the final partial period of the scalar mask loop, leaving fewer than eight bytes for its byte
  tail. For a padded 12-byte element with SIMD disabled, longer alternating batches improved the 3-element case by
  11–23% and the 17-element case by 11–12%. The byte-identical controls varied by at most 9% for those cases. Two
  elements and SIMD timings had no repeatable gain. `NinoMask.Copy` grew from 259 to 318 IL bytes and by 56 bytes in
  the Arm64 scalar Tier 1 and x64 cross-JIT listings. Dirty-padding wire checks matched at offsets 0, 1 and 7, and
  targeted tests passed (`51e30efb`).
- Share the full-word scalar loop across complete and partial periods. Advancing and wrapping the mask word index
  reduced `Copy` from 318 to 226 IL bytes and by 88 bytes in the Arm64 scalar Tier 1 and x64 cross-JIT listings. For
  12-byte elements with SIMD disabled, both runs straddling 64 KiB of input, output and mask improved by 8–13%, with
  A/A within 5%. Short and SIMD timings varied. Dirty-padding bytes matched at offsets 0, 1 and 7, and targeted and
  scalar tests passed (`a7927d44`).
- Keep the predictable scalar mask wrap branch. Expressing the wrap as a ternary still emitted a branch and did not
  improve timing. A sign-mask expression removed the branch and cut the scalar body from 344 to 336 Arm64 Tier 1 bytes
  and from 284 to 280 in the x64 cross listing, but serialized 2,665 and 2,821 padded 12-byte elements in about three
  times the baseline time with SIMD disabled. Its loop carried an extra arithmetic dependency into the next mask load.
  Four A/A rounds stayed within 6% for those cases. Wire and dirty-padding checks matched, and targeted tests passed.
  Keep both as probes.
- Check both address updates and the loop counter before replacing the scalar mask index with two byref cursors. Arm64
  used post-index loads and stores, but `Copy` grew from 226 to 250 IL bytes and 344 to 348 Arm64 Tier 1 bytes.
  Counting the remaining bytes simplified the tail and used 245 IL bytes, 356 Arm64 bytes and 280 x64 cross-JIT bytes
  versus 284 at baseline. Neither variant improved short or 64 KiB boundary workloads repeatably, and the countdown
  was slower in all four default-SIMD 17-element rounds. Keep the indexed loop. Wire and dirty-padding checks and
  targeted tests passed.
- Unroll the three-word scalar mask period and load all three words before storing. Arm64 paired the first two loads
  and stores as `ldp` and `stp`, using eight fewer bytes than the interleaved variant. Compared with the shared loop,
  IL grew from 226 to 383 bytes, Arm64 scalar Tier 1 from 344 to 432, and the x64 cross listing from 284 to 380. With
  SIMD disabled, the 2,665- and 2,821-element 12-byte probes took 38–56% less time and the 1,420-element 24-byte probe
  44–55% less in four rounds. Default SIMD timings varied. Dirty-padding checks matched, and both targeted suites,
  scalar tests and netstandard builds passed (`7a3cf2c6`).
  These unrolls (and the two- and one-word ones below) run only where no vector mask does: .NET 8+ without SIMD and
  the netstandard builds without accelerated `Vector<byte>` or a registered kernel for the run (IL2CPP, Unity's Mono,
  .NET Framework); keep them as that platform's fast path, never merged into the shared word loop, until a Mono or
  IL2CPP measurement says otherwise (every number here is .NET 10 with SIMD disabled; `NinoMask.Copy` says so).
- Keep indexed addressing for the three-word period. Bumping both byrefs preserved `ldp` and `stp` but left two
  address increments, grew IL from 383 to 399 bytes and Arm64 scalar Tier 1 from 432 to 440, and left the x64 cross
  listing at 380. Moving the paired operations last did not fold the increments and made the 2,821-item scalar case
  63–140% slower across four rounds. The ordinary cursor variant had no repeatable large-input gain. Keep both patches
  as probes. Wire and dirty-padding checks matched, and targeted and scalar tests and netstandard builds passed.
- Keep the three-word path inside `Copy` for now. Outlining only that loop increased the default Arm64 Tier 1 entry
  from 748 to 764 bytes and slowed the 17-element SIMD case by 13–23% in four rounds. Outlining the entire scalar path
  reduced the default entry to 564 bytes, but the scalar entry plus helper grew from 432 to 492 bytes and the x64
  cross listing from 380 to 424 bytes. The apparent 2,665-element SIMD gain did not repeat with an identical-binary
  control. Keep both patches as probes. Wire and dirty-padding checks matched, and the scalar outline passed targeted
  tests.
- Use the same paired shape for the two-word period. With SIMD disabled, the padded 16-byte probe took 23–29% less
  time at 17 elements and 37–67% less across the two sizes straddling 64 KiB. Default-SIMD and existing three-word
  timings varied on replay. `Copy` grew from 383 to 500 IL bytes, 432 to 492 Arm64 scalar Tier 1 bytes, and 380 to 444
  in the x64 cross listing. Dirty-padding bytes matched, and targeted and scalar tests and both netstandard builds
  passed (`f3d6bd81`).
- Reuse the paired loop for one-word periods by applying its mask to both words. A separate one-word loop saved 16–26%
  on the large scalar probes but added another body. Reusing the pair saved 39–48% across the same two sizes and
  11–18% at 17 elements, with no repeatable slowdown on the 16-byte or SIMD probes. `Copy` grew from 500 to 508 IL
  bytes, from 500 to 516 Arm64 scalar Tier 1 bytes and from 444 to 460 in the x64 cross listing. Dirty-padding checks
  and targeted tests passed (`4c8a1e11`).
- Handle one to two native vectors in the masked copy with the head and, when distinct, the tail. The tail starts
  within the first mask period, so it can load the mask at that offset directly. On M4, four default-JIT pairs and a
  repeated series with an identical-binary control improved the 16- and 24-byte writes of padded 8-byte items by about
  3–5% by median. The 128-item and near-64 KiB input/output controls varied. The generic method grew from 687 to 758
  IL bytes, 424 to 456 Arm64 FullOpts bytes, and 292 to 324 emulated x64 AVX2 bytes. Dirty-padding output matched in
  39 M4 cases and five emulated x64 cases, with zero steady-state allocation. The 512-bit specialization passed 57
  software checks for dirty padding and destination bounds. Native AVX-512 throughput was unmeasured.
- Select fixed 512-, 256-, and 128-bit adapters for the managed masked copy in that order. Keep the existing 512-bit
  CPU policy. Repeat the mask for the widest eligible adapter and retain a phase period per width, so shorter inputs
  can use the same table at a narrower width. The netstandard path keeps its existing native hook and numeric-vector
  adapter. The shared copy loop passed 12,042 forced-width, stride and offset cases. All 48 public wire outputs
  matched with zero steady allocation. An apparent two-element slowdown did not reproduce with isolated longer warmup
  and identical-binary controls. Arm64 throughput and first use were broadly flat. Copy grew from 508 to 559 IL bytes
  while its Arm64 listing fell from 460 to 412 bytes. The loop stayed at 761 IL and 456 Arm64 bytes. The x64 audit
  emitted 325/318/366-byte kernels at the corresponding 128/256/512-bit ceilings. Native x64 throughput remains
  unmeasured.
- Let the final store own the vector at the end of a one-vector mask period. Skipping the tail with an extra branch
  slowed a 48-byte block with a 12-byte stride by about 8%, even when the branch was limited to one-vector periods.
  Make the aligned body's bound exclusive instead, including its Arm64 paired loop. Four M4 pairs improved the 48-byte
  aligned kernel by 4–8%, with an identical-binary control near flat by median. The public writer, the 12-byte stride
  and the 64 KiB boundary had mixed timing. IL grew from 758 to 761 bytes, with Arm64 and emulated AVX2 FullOpts
  unchanged at 456 and 324 bytes. Wire bytes and destination sentinels matched across 128-bit, emulated AVX2 and
  software 512-bit probes.
- Keep the inclusive loop when a mask spans several vectors. Making it exclusive took 27–33% less time for an aligned
  192-byte kernel probe but increased the aligned 48-byte time by 8% by median across four M4 pairs. Using the tail's
  absolute offset in the first period did not fix the 48-byte result and increased the aligned 60-byte time by 15%.
  Keeping the inclusive bound within the first period removed the repeatable 48-byte penalty, but the 192-byte public
  writer and 64 KiB boundary gains did not repeat. The three candidates used 761, 774 and 777 IL bytes against 761,
  with Arm64 FullOpts at 456, 472 and 460 bytes against 456 and emulated AVX2 at 324, 334 and 333 against 324. All
  matched dirty-padding wire bytes and steady allocations. The candidates were withdrawn. The retry against `5069bda0`
  confirmed the short aligned penalty. The exclusive bound slowed three of four 48-byte direct comparisons by 7–9%,
  with A/A within 2%. Keeping the inclusive branch opcode by comparing against `end - 1` moved the penalty to the
  unaligned 96-byte case (11–16% slower in three pairs). Public writes and first use varied. The exclusive-bound audit
  kept 761 IL, 456 Arm64 and 325/318/366 offline x64 bytes at the three width ceilings. It matched all 57 wire
  outputs, 12,042 software cases and the five targeted configurations. Withdraw both retries.
- Keep the initial phase check on the multi-vector loop for now. Its condition cannot fire because the initial offset
  is at most one vector and the period is strictly wider. Removing it nevertheless changed Arm64 register choice and
  loop alignment. Four longer M4 default-JIT pairs slowed the aligned 192-byte kernel by 31–68%, versus -12% to +24%
  for the identical-binary control. The unaligned and near-64 KiB cases varied. The change removed 13 IL bytes and 7
  bytes from the emulated AVX2 FullOpts listing, with Arm64 still at 456 bytes. All 54 local wire cases and three
  emulated AVX2 cases matched, including the software 512-bit path and destination sentinels. Steady-state allocation
  was zero. The candidate was withdrawn.
- Build repeated mask tables by doubling a filled prefix. The last copy can be partial, and each earlier copy
  preserves the mask phase. Four M4 default-JIT pairs reduced warmed `NinoMask.Create` medians by 36–51% for 8-, 12-
  and 24-byte masks, with equal allocations. The one-byte repeat and first public write varied within their
  identical-binary controls. `Repeat` grew from 57 to 104 IL bytes, 244 to 396 Arm64 FullOpts bytes, and 129 to 200
  emulated x64 bytes. Checks matched all 1,536 repeated tables across 8-, 16-, 32- and 64-byte widths and all 21
  padded wire outputs at offsets zero, one and seven.
- Keep the temporary byte table for scalar words for now. Filling the final `ulong[]` through its byte view saved
  32–48 B for 8–24-byte masks and 544 B for a 65-byte mask, but four longer M4 pairs without intrinsics slowed 12-byte
  construction by 23–27%. Inlining the shared span filler did not remove that regression. Replacing the span copies
  with disjoint block copies made the 8-, 12- and 24-byte scalar cases slower in all four pairs despite faster SIMD
  construction in several cases. The first public write saved 48 B and had mixed timing. The span and block variants
  used 340 and 347 IL bytes versus 340, and the constructor and emitted helpers went from 1,276 to 948 and 832 Arm64
  bytes and from 820 to 651 and 613 emulated x64 bytes. All 192 mask sizes and 21 wire outputs matched. The candidates
  were withdrawn.
- Keep the modulo loop for the table period. A lowest-set-bit formula and a doubling loop both sped up repeated
  12-byte masks but slowed the 65-byte mask at a 64-byte width by 5–25% across four M4 pairs per variant. Keeping the
  additions and replacing modulo with a bit test still slowed that case by 13–16%, versus 1–6% for the
  identical-binary control. IL was 104 bytes at baseline and 110, 104 and 106 in the trials. Arm64 FullOpts fell from
  396 to 344, 356 and 352 bytes, and emulated x64 went from 200 to 201, 193 and 192. Allocations, 4,096 repeated
  tables and 21 padded wire outputs matched. First-write timings varied. The candidates were withdrawn.
