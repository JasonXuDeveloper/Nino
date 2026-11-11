# Sorted lists, pairs and priority queues (.NET)

Part of the `optimize` skill ([vectors.md](vectors.md) holds the general loop rules): the vector loops of
`SortedList<TKey, TValue>` (ordered-key validation, the split into keys and values), the pair interleave
(`NinoSimd.Interleave`, `NinoSimd.Pairs.cs`) and `PriorityQueue` heap checks.

Platform scope: a bullet that names a GitHub runner run (or "runners") was measured on every runner CPU. The others
were timed on a local Apple M4 (Arm64, default JIT) or, where they say so, on an emulated x64 AVX2 guest; their x64
evidence is listings only (Crossgen2, cross-JIT), and the rules lists mark them "M4". Measure on native x64 before
carrying such a threshold or verdict there (`benchmark`).

Contents: [Rules](#rules), [Ordered keys](#ordered-keys), [Pair interleave](#pair-interleave), [Priority
queues](#priority-queues).

## Rules

- Validate packed `SortedList<int, int>` keys with two batched Arm64 vectors per reduction; keep the 128-bit dispatch
  on x64 and the indexed Arm64 reuse loop; measure reuse separately from fresh reads (M4 and AVX2 guest; native
  AVX-512 unmeasured).
- Split checked ordered raw input straight into the two arrays (AVX2/AVX-512 for wide batches; Arm64 fuses the order
  check and unzip when both arrays fit); keep the two-pass check when they grow, zeroed arrays, and the paired loads
  on Apple Arm64 (M4 and AVX2 guest).
- Zip keys and values straight into the reservation through `NinoSimd.Interleave` with static lane policies, wide
  dispatch at the caller, an overlapping final vector and a single element offset (M4 and AVX2 guest; AVX-512 listings
  only).
- Validate four priority-queue children against their parent per vector compare and store the groups straight into the
  backing array; keep the 128-bit check on x64 (M4 and AVX2 guest).

## Ordered keys

- Batch two native Arm64 vectors before reducing ordered-key comparisons (`c377dd97`). The packed `SortedList<int,
  int>` validator still unzips keys and carries the last key across groups, but tests one combined result per eight
  keys. Keep the four-key and scalar tails. Use `NinoSimd.HasArm64` for the outer loop so the x64 ReadyToRun listing
  does not retain the Arm64 batch. The validation method went from 150 to 188 IL bytes and from 172 to 268 Arm64
  FullOpts bytes; the x64 AVX2 cross listing went from 372 to 375 bytes and retained its four-key loop. Four
  default-JIT local rounds improved the isolated long-input validation at offsets 0, 1 and 7, with 0 B allocated.
  Fresh reads through a generated interceptor improved in some long cases and varied in others; allocations and all 24
  wire outputs matched. The source plus destination payloads near 64 KiB were included, without hardware cache-miss
  measurements. Duplicate keys at the batch boundary still take the existing fallback; the targeted .NET 8 and 10
  suites passed, including .NET 10 with intrinsics disabled. Do not replace the Arm64 unzip with two overlapping
  vector loads and an even-lane mask: that processes only two keys per vector and extracts the mask with shifts and a
  horizontal add. A vector-only reduction recovered part of the loss but was still slower on long input. Keep the
  256/512-bit versions as x64 candidates: crossgen emitted `vpcmpgtd` plus `vmovmskps` or `kmovw`, respectively. The
  94/103-byte loop listings are instruction evidence only; measure actual x64 throughput and alignment before changing
  dispatch.
- Keep the 128-bit dispatch after testing the two wider validators through the actual `SortedList<int, int>` entry.
  Unzipping keys and carrying the previous group was 9–11% slower at 4,080 pairs and offset seven in four AVX2 guest
  kernel rounds. Comparing two overlapping packed blocks and reducing only the key lanes was 8–37% slower. Reuse and
  fresh reads did not establish an end-to-end gain. Both retained zero steady kernel allocation and matched wire
  output. Guard-page tests covered duplicate keys at every position, with AVX2 and with the SSSE3 fallback. The second
  candidate's exact 512-bit generic loop also passed the software guard-page tests. Offline v3 listings were 115 B for
  the original vector method and 136/140 B for the candidate wide entries. The v4 candidates were 327/382 B, including
  both width branches, and emitted ZMM compares. Arm64 retained 116 B for the dispatch and 260 B for its Tier 1 vector
  loop. Native AVX-512 throughput remains unmeasured.
- Measure reuse separately from fresh reads. The Arm64 reuse kernel already validates and splits in one pass. Batching
  two of its groups grew the instantiated method from 232 to 392 bytes. Advancing the three byrefs kept it at 232
  bytes and produced a paired load, but slowed the 4,100-pair, offset-1 reuse case in all four local rounds. Both
  variants regressed at that size and offset, allocated 0 B per reuse read, and matched the baseline's 24 serialized
  outputs. Keep the indexed loop. For the separate x64 fused-split probe, reuse the unpacked keys to build their
  predecessors: use a two-source dword permute at 512 bits, a lane permute and byte align at 256 bits, or byte align
  at 128 bits. With the linux-x64 runtime pack as references, Crossgen2 emitted 150/125/101-byte methods for the three
  supported widths. An Arm64 CoreLib reference can emit calls to unsupported-intrinsic stubs instead. Run the fused
  probe natively before selecting dispatch thresholds.
- Split a checked, ordered raw `SortedList<int, int>` input directly into its two arrays on .NET 8+. For full wide
  read batches, use AVX2 lane shuffles plus 64-bit reordering or AVX-512 two-source dword permutes. Keep the exact ISA
  test in the existing out-of-line entry so Arm64 Tier 0 drops it. Let the wide helper call that entry for the
  remainder, which is below the wide threshold. Returning the processed count to the narrow entry grew its x64 listing
  to 205 B. Keeping the remainder in the wide helper brought it to 123 B against the 110 B baseline, with Arm64 Tier 0
  and Tier 1 unchanged at 488 and 120 B. The AVX2 guest matched wire bytes, including unaligned inputs and sentinels,
  and the offset-zero 4,095-pair guest control improved in all four rounds. Native x64 throughput and native AVX-512
  execution remain unmeasured. Local M4 fallback timings varied without a repeatable regression (`a07cde43`).
- Keep the order check before publishing the arrays and use the same wire block for out-of-order insertion. Fuse the
  order check and unzip on Arm64 when both arrays already fit. Write only into an empty list and publish the size
  after every key is validated. A rejected block goes through the same comparer fallback, including the indexer
  overwrite before a duplicate is reported. At offsets zero and one, four M4 A/B pairs cut 32-pair reuse latency by
  about a third and 257-, 4,095- and 4,097-pair latency by about half at zero allocation. Fresh growth retained the
  separate order check and allocations. The fused OSR loop was 240 B versus 200 plus 136 B for the two original loops.
  The caller grew from 154 to 204 IL bytes and the x64 v3 cross listing moved from 402 to 401 B. Wire, scalar fallback
  and all 582 generated files matched (`86e4efb7`).
- Share the Arm64 unzip across signed and unsigned integer lanes of 8, 16, 32 and 64 bits. Use the underlying integral
  order for enum keys with the default comparer. Keep the lane constants inside the loop and optimize from the first
  call. The first generic OSR version spilled the predecessor vector. With `AggressiveOptimization`, the sampled Arm64
  loops were 224–236 B and the x64 v3 fallback remained 401 B (`6f64a961`, `bae2d41b`).
- Extract the last lane with `GetElement` in an x64 fused check and split. Taking a byref to the predecessor for the
  scalar tail spilled the vector in every AVX2 iteration. Explicit lane extraction kept it in registers and reduced
  the generic loop from 210 to 206 IL bytes and from 221 to 204 AVX2 FullOpts bytes. Inline the ISA dispatch so the
  Arm64 caller still calls the existing loop directly. The fused candidate was withdrawn after the emulated AVX2 probe
  against `915a8c54` won at 32 pairs with offset zero in all four comparisons but lost at 4,080 pairs with offset
  seven in all four even after removing the spill. The ordered, shuffled, duplicate and truncated probes matched with
  AVX2, SSSE3 and SIMD disabled. Wire and generated source matched and steady reuse allocated 0 B. The AVX-512
  dispatcher was inspected offline only. Repeat the offset sweep on native x64 before changing the two-pass path.
- Keep custom unmanaged keys in their typed comparer. A four-byte `ReverseIndex` sorted in reverse integer order with
  the default comparer and matched the baseline bytes in 1,376 round trips. Split the staged array after typed
  validation, since comparison can run user code and the input is no longer the snapshot. Reuse `DeinterleaveInt32`
  when the shared block plan proves identity layout for two four-byte fields. An unrestricted dispatch slowed the
  4,097-pair aligned probe. Limit it to 64–2,048 pairs, where the wire, pool bucket and exact-size destination are at
  most 48 KiB plus the header. The 64-, 65- and 2,048-pair reuse probes improved in all four M4 rounds at both offsets
  with no steady allocation and identical bytes. The caller grew from 189 to 260 IL bytes and from 456 to 488 Arm64
  Tier 1 bytes. x64 cross-JIT grew from 549 to 649 B (`2d173116`).
- Keep the two-pass check when the arrays need to grow. A speculative fused growth candidate cut ordered M4 reads by
  26–51% at the same allocations and preserved wire bytes and capacity. Checking the two edges avoided extra
  allocations for descending inputs and a swap at the end, but a middle swap still cost about 2.1 KB at 257 pairs or
  32.8 KB at 4,097 pairs and slowed in all four rounds. Untrusted mode retained its original allocations. The
  candidate was withdrawn.
- Put the layout and comparer checks in an out-of-line helper so short reads keep the original rented-pair method's
  IL. The local Arm64 2,047- and 8,193-pair reuse probes cut elapsed time by roughly 35–45%.
- Validate four signed keys at a time for 32 or more pairs. Share the even-lane extraction with deinterleave, align
  the previous last key with the next three, and check the scalar tail. Keep the scalar check inlined for short inputs
  and unsupported instruction sets. Vectorizing every eligible read made the 17-pair unordered case slower. With the
  gate, that case stayed within control variation and the 3,967-, 4,223- and 8,193-pair reuse probes took less time
  across all four local rounds at zero allocation. Check duplicate keys at vector boundaries and in the tail. The
  16-pair probe occasionally stayed slow during short warmup. With a longer warmup, timings varied within the controls
  with SIMD on and off. The generated source and wire samples matched.
- Measure allocation thresholds separately before using uninitialized arrays for this split. The two-array growth
  candidate passed the order and byte checks, but 1,536 pairs crossed the 6 KiB per-array threshold and took over five
  times as long as the paired baseline in all four local rounds. Allocation size stayed at 12,400 bytes per read. The
  cross listing showed calls to the uninitialized allocation worker instead of the usual new-array helper. Larger
  samples varied. Compare a zeroed control with the same publication order and call shape. The zeroed control removed
  the threshold slowdown. At 24,576 and 32,768 pairs, the uninitialized worker also lost in all four paired local
  rounds, with unchanged output bytes and allocation size. Keep zeroed arrays for this path.
- Keep the paired vector loads and unzips for the split on Apple Arm64. The gated and compact `.NET 10` `ld2` probes
  both took longer at 4,223 pairs in four isolated local rounds. The existing Tier 1 loop already uses `ldp`, `uzp1`
  and `uzp2`. The compact replacement grew the entry from 120 to 152 Arm64 bytes and from 169 to 288 IL bytes. Compare
  the kernel alone as well as the public path, whose order check and allocation can hide the split cost.

## Pair interleave

- Zip raw `SortedList<int, int>` key and value vectors straight into the counted reservation. Use Arm64 `zip1/zip2` or
  SSE2 unpack on x64 and x86, and keep the scalar tail. The one-source permute kernel cannot take the two arrays
  directly. The local Arm64 2,047- and 8,193-pair probes cut elapsed time by 56–70%. Keep the dual-source loop in
  `NinoSimd.Interleave` and call it only for at least one vector. Extracting it cut seven-pair write time by about 30%
  locally while the larger cases remained broadly on par. On netstandard, register the same four-byte lane operation
  through `NinoKernels.Interleave32`. Gate on raw layout and no padding, call once for full vectors, and leave the
  remaining pairs to the existing scalar loop. Check unaligned output and the untouched bytes beyond the vector prefix
  in the Burst kernel tests.
- For a generic 16/32/64-bit interleave, fold the lane-width gate before calling a generic helper and precompute the
  pair stride: `i * size * 2` became two shifts for 32-bit lanes. Skipping Tier 0 in the generic helper recovered
  short 32-bit throughput locally, but the 2,047-pair control slowed by 4–9%. Keep the candidate isolated while tuning
  dispatch; the 16- and 64-bit large cases improved consistently.
- Resolve zip operations with static lane policies so the shared loop carries no width switch. This reduced its IL
  from 440 to 193 bytes and kept the 32-bit Arm64 and Windows x64 FullOpts sizes at 128 and 109 bytes. The local
  seven-pair 32-bit case still took 3–6% longer; 8,193 short pairs and 2,049 long pairs improved by roughly 79–81% and
  29–34%, respectively.
- Dispatch wider batches at the caller boundary and leave the narrow loop out of line. Putting the AVX2 dispatch and
  cursor updates in the narrow method grew the x64 int listing from 109 to 216 B. Moving the dispatch back to the
  caller kept the narrow method at 109 B and the Arm64 caller and loop at 236 B and 128 B. Require two full vectors
  before entering the wide path. On AVX2, unpack per lane then merge the 128-bit lanes. With AVX-512, use two-source
  permutations and the shared 512-bit CPU policy. The x64 guest verified AVX2 and its fallback, including unaligned
  writes, while the AVX-512 listing showed `vpermt2w/d/q`. The guest lacks AVX-512 and no native x64 throughput was
  measured. Four M4 A/B pairs gave mixed results with zero allocations and identical output around 64 KiB
  (`3c871bab`).
- Keep pair interleave, deinterleave, ordering and the Burst bridge together in `NinoSimd.Pairs.cs`. Moving the guard
  with its kernel preserved the sampled IL, Arm64 Tier 1 sizes, and x64 v3/v4 sizes. Pin both buffers to control their
  actual addresses around 64 KiB. Uncontrolled timings showed large outliers, while the fixed-alignment A/B medians
  stayed within 4%. Bytes matched with SIMD on and off, and the real project's 582 generated files were identical
  (`c0025a12`).
- Finish a 16- or 32-bit remainder with the last whole 128-bit vector, overlapping a prefix already written by the
  narrow or wide loop. End the vector at the last pair. Write a 64-bit remainder directly because it contains only one
  pair. The scalar cursor experiment was flat. Sharing a helper with the main loop left a Tier 0 call per vector and
  slowed larger cases by 9–24%; keep the main loop in place. The final mixed-offset M4 probe improved 15 short, 7 int
  and 3 long pairs by median 16%, 7% and 5%. Input-plus-output cases around 64 KiB were broadly flat. Wire hashes
  matched; 155 of 156 final samples allocated 0 B and one large sample had a transient allocation. The kernel moved
  from 193 to 190 IL bytes plus a 64 B helper. Arm64 FullOpts fell from 128 to 112 B for 16/32-bit lanes and to 88 B
  for 64-bit lanes. x64 cross-JIT MinOpts grew by 30 B per lane width; no native x64 timing was measured. Both .NET
  suites, scalar fallback and the unaligned destination sentinels passed (`281f03fd`).
- On the AVX2 path, cover the remainder with one or two 128-bit zips instead of calling the general narrow loop. The
  x64 emulated guest supported AVX2 and ran four default-JIT pairs. Medians for 17 and 21 int pairs improved 4% and
  8%, while exact-vector and 64 KiB controls were noisy. Wire bytes, destination sentinels and zero allocations
  matched. AVX2-disabled execution and the Arm64 112 B narrow listing matched. The shared dispatcher IL grew from 138
  to 219 B, x64 guest FullOpts from 112 to 177 B, and the AVX2 main loop stayed at 79 B. The separate 512-to-256
  half-vector trial grew x64 cross-JIT MinOpts from 361 to 455 B per width. Retain the existing AVX-512 tail until
  that variant can be timed on supporting hardware (`85789f51`).
- Use a single element offset in the narrow zip loop and keep the overlapping tail. Unrolling to two 128-bit groups
  slowed seven- and 33-pair offset-one writes in all four M4 pairs. The indexed loop improved 33 pairs at both offsets
  and 3,967 pairs at offset one across all four pairs. The latter took 17–29% less time, while same-binary repeats
  varied from 7% faster to 5% slower. Seven-pair and above-64-KiB timings were mixed. Bytes matched and steady
  allocation was zero. The shared IL was 190 B before, 224 B with unrolling and 214 B indexed. Arm64 FullOpts was 112,
  192 and 136 B; emulated x64 with AVX2 disabled was 107, 107 and 121 B. Keep the existing wide dispatch and use
  native x64 timing to assess throughput there (`0b90f504`).

## Priority queues

- Validate four priority-queue children against their shared parent in one signed vector compare. Reuse the odd-lane
  unzip for interleaved raw pairs and keep the scalar tail for incomplete groups. The local 8,193-pair heap adoption
  probe cut read time by 54–58%, with unchanged bytes and allocation. Store each validated vector group directly into
  the empty backing array, publishing its size only after every group and the scalar tail pass. The enqueue fallback
  overwrites unpublished raw slots. Keep a single bulk copy without SIMD; scalar per-pair stores regressed. Fusing the
  vector stores saved another 24–25% on the local 8,193-pair probe. Advance the source, destination and parent
  priority cursors by their strides across vector groups. This removed indexed address arithmetic and saved a further
  22–24% in the same local large-heap probe.
- Measure the short-call cost before widening the fused `PriorityQueue<int, int>` heap check. Eight children occupy 64
  bytes. AVX2 compares two vectors and AVX-512 compares one, masking out the even element lanes. Keep the 128-bit and
  scalar tails. Advancing the three byrefs and using `AggressiveOptimization` removed index scaling and three calls
  per group from the default AVX2 listing. Its FullOpts helper went from 138 to 107 B, while the caller grew from 341
  to 475 B against baseline `1ee1b127`. Arm64 stayed at 572 B. In the offline 512-bit probe, selecting between two
  broadcasts avoided the stack spill from composing two 256-bit halves (153 to 126 B). Both production candidates were
  withdrawn. The offset-zero 33-node read lost in all four emulated AVX2 comparisons for each variant, and the
  4,080/4,100-node cases straddling 64 KiB were mixed. Wire, generated files, rejected heap order and truncation
  matched. Steady reuse allocated 0 B. The 512-bit probe passed in software and needs native throughput evidence.
