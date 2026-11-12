# The JIT: collection formatters

Part of the `optimize` skill, split from [jit.md](jit.md), whose general rules (the inlining budget, the cursor in
registers, reading a repeating shape into an existing collection with a loop of its own) apply here. These are the
measured experiments on the collection formatters of Nino.Core and Nino.Immutable: dictionaries, hash sets, queues,
stacks, sorted collections, priority queues, lookups, linked lists, the shared enumerable loop, and reading into an
existing dictionary. How the raw paths reach a collection's fields is `runtime-internals`. "Arm64 only; x64
unmeasured" marks a bullet timed only on the local Arm64 host (Apple M4): its x64 sizes come from cross-JIT, emulated
or offline listings and are code-size evidence, not throughput.

Rules in short:

- Return right after the zero count of an empty collection; do not add a second empty test where the shared loop
  already has one.
- Write fixed-stride runs (queue runs, heap nodes, sorted-set nodes, linked-list rings, lookup groups) with a reference
  and a count into one reservation, gated by a measured size threshold, with the small path kept as it was.
- Keep a single loop rather than a separate single-item arm that duplicates the inlined formatter.
- In a dictionary refill, skip the seen-key set only for small, bounded cases; copy the `Reader` with struct
  assignments; keep ordered-key probing and ordinal-only loops out until they win at both tiers.

## Empty collections and shared helpers

- Write the zero count of an empty raw dictionary without entering its out-of-line pair loop, keeping the reservation
  shared with non-empty dictionaries. Evidence: the Arm64 probe saved about 7–9 ns on empty dictionaries at both output
  offsets, with the non-empty controls on par. Arm64 only; x64 unmeasured.
- Skip entry-view lookup after the same zero reservation for raw hash sets. Evidence: the Arm64 probe saved 5–7 ns,
  with dense and sparse non-empty sets on par. Arm64 only; x64 unmeasured.
- Return once for an empty hash set read after creating or recycling its destination. Why: it keeps reuse and comparer
  setup intact and removes the repeated zero tests before capacity and fill. Evidence: the local Arm64 probe saved 6–8
  ns on empty reads with unchanged allocations; the formatter shrank from 424 to 416 IL bytes. Arm64 only; x64
  unmeasured.
- Share the actual presizing clamp in `NinoElements.Capacity` and keep the cheap eligibility tests at the hash set and
  dictionary callers. Why: a shared inline branch shortened the two callers by 10 IL bytes but added a 14-byte helper,
  for nothing measurable. Evidence: the Arm64 listings kept their sizes, only the x64 set listing shrank (6 bytes), and
  the local A/B showed no repeatable throughput gain or allocation change.
- Keep the element loop inside `NinoElements.Write`, never in a helper of its own, and call `Write` from the queue's
  variable-size path: `BlockStride` is a constant of each instantiation, so the block check folds away. Why: where an
  element writes a collection of its own type (a tree), the JIT did not inline the helper, and every level paid one
  more call. Evidence: e4064404's `WriteItems` showed in the Tier1 listings of `SelfTree` as a call from both `Write`
  and `ListFormatter.Serialize`; the runner A/B of the rc.10 review head against main measured SelfTree serialize at
  0.84 to 0.86x on EPYC 9V74 (both jobs) and about 0.9x on other x64 CPUs; with the loop back in `Write`, asm.py
  showed no method worse than main. The queue's empty-tail test went with the helper (a local guard was on par).
- Keep the existing count reservation in the shared enumerable loop until a broader win is measured. Why: writing a
  known count before enumeration avoids carrying its patch position but does not remove a store (`ReserveInt32` itself
  writes no value). Evidence: four default-JIT M4 pairs favored small offset-one integer collections, while larger,
  unknown-count and reference-element cases were mixed; restricting eager counts to the existing local-writer type rule
  did not broaden the win. Allocation stayed at 32 B for the counted wrapper and 48 B for the iterator. Wire and round
  trips matched at offsets zero and one; the first candidate passed 124/123 targeted .NET 10/8 tests including a
  wrong-count probe. Shared loop IL grew from 93 to 109 B, or 125 B with the type gate; the integer helper grew from
  1,116 to 1,220/1,240 Arm64 FullOpts bytes and 890 to 1,074/1,057 emulated x64 bytes. Both candidates were withdrawn.
  Arm64 only; x64 unmeasured.

## Queues, stacks and sorted lists

- Write the two fixed-stride queue runs with a reference and count. Why: the validated ring already bounds both runs,
  and their shared reservation covers both destinations. Evidence: four local default-JIT rounds with a duplicate
  baseline showed a gain for one integer and for 64 contiguous or wrapped integers, with zero allocations; wrapped
  `DateTimeOffset` and the integer working sets immediately below and above 64 KiB were on par. The queue formatter
  grew from 205 to 209 IL bytes; the integer caller fell from 3,024 to 3,004 bytes on Arm64 FullOpts and from 2,535 to
  2,445 bytes on x64 cross-JIT; the `DateTimeOffset` caller grew on Arm64 (2,236 to 2,284) and shrank on x64 (1,969 to
  1,934). The wire and 577 generated files matched. Arm64 only; x64 unmeasured.
- Keep the portable stack's reverse-then-write path, and keep any reverse-walk dispatch out of the hot `Serialize`
  entry. Why: walking the rented snapshot backwards for variable-size reference elements saved about a fifth of the
  time for 512 and 2,048 strings in the forced fallback, but grew the raw Tier 0 helper from 356 to 408 Arm64 bytes,
  and the 64-integer case slowed by 2–4% across four local rounds even after a value-type gate restored its FullOpts
  listing to 1,572 bytes. Evidence: a separate reverse-walk helper restored the raw Tier 0 helper to 356 bytes, but
  dispatching to it from `Serialize` added 40 IL bytes there, grew the normal-layout Arm64 probe from 4,240 to 4,624
  bytes, and slowed the 64-integer normal-layout control in every round. Replacing the reverse-walk span indexer with
  `Unsafe.Add` removed its range failure in both listings (Arm64 1,172 to 1,164 bytes, x64 920 to 858), but the
  one-string forced-fallback case slowed in all four local rounds, so the bounds-check removal alone does not justify
  reviving that candidate. Arm64 only; x64 unmeasured.
- Walk the checked key and value arrays with `Unsafe.Add` in the variable-size `SortedList` write loop, and keep the one
  loop. Why: it removed range failures and saved 196 Arm64 bytes for `int` keys and string values; single-pair writes
  slowed in four local rounds, and a separate single-pair arm duplicated the inlined formatter. Evidence: that arm grew
  Arm64 from 1,828 to 2,772 bytes and x64 from 1,584 to 2,770. The packed `SortedList<uint, uint>` SIMD entry is in
  [jit.md](jit.md), "Inlining budget" (keep singleton stores in the entry).

## PriorityQueue

- Reserve the raw `PriorityQueue` count and pairs together inside its existing by-value writer helper, and keep the
  public writer's dispatch intact. Why: putting the raw gate in `Serialize` slowed the reference-element loop, while
  moving it into the helper restored the original Arm64 and x64 listings for that loop. Evidence: the local
  257-integer-pair case cut write time by 56–61%, with the enumerator preserving heap order.
- Keep the read experiment isolated, and evaluate heap construction separately from byte loading. Why: checking the
  whole pair block and walking its byref removed both per-pair bounds checks, and using `Peek` and advancing on success
  also avoided the reader copy, but neither variant consistently improved throughput: the `Enqueue` call remained.
- Adopt an already ordered integer heap through checked .NET 10 fields, and keep the public enqueue fallback for any
  wire order or comparer that does not match the checked heap (`runtime-internals`). Evidence: the local 8,193-pair
  reuse time fell by about 74%, with zero steady-state allocation.
- Reuse that checked node array for writes, copying the live count in heap order. Evidence: the local 8,193-pair
  integer case cut write time by about 90% against the counted enumerator path.
- Use the shared tuple block plan to prove identity layout for arbitrary raw element and priority types, and for padded
  or reordered tuples write directly from the typed heap with a constrained `WriteFixed` call. Why: a shared `WriteAll`
  trial was faster on large mixed pairs, but its interface tail allocated up to 48 B per short write before tier
  promotion. Evidence: the retained variant used 0 B in the timed intervals and cut elapsed time in all four M4 rounds
  for 257 and 4,097 pairs of custom index/uint, custom index/ushort and byte/long at both offsets; the int/int control
  at 4,097 pairs was effectively unchanged in the aligned runs. The heap helper grew from 92 to 228 IL bytes, while its
  int/int Arm64 Tier 1 instance moved from 296 to 292 B; the x64 cross instance grew from 222 to 732 B and the
  corresponding raw caller shrank from 643 to 364 B (`ef8e4195`). Arm64 only; x64 unmeasured.
- Batch two quaternary parents in the Arm64 adoption loop for heaps of at least 33 pairs: keep 128-bit NEON loads and
  stores and OR the two comparison masks before one horizontal reduction. Evidence: the 257-pair M4 reads improved in
  all four rounds at offsets zero and one, and 8,193-pair reads improved in every pair; results near 64 KiB of combined
  input and output and for short heaps varied. Wire inputs matched, decoded heap order matched and steady allocation
  was zero. Adoption grew from 397 to 661 IL bytes and from 452 to 572 Arm64 FullOpts bytes; the emulated x64 listing
  stayed identical at 341 B (`d7efda09`). Arm64 only (the path is Arm64's).

## Lookups, sorted sets and linked lists

- Write a `NinoLookup` (a deserialized lookup) through the grouping path like any lookup; a NoInlining fast path that
  takes `ref writer` makes every lookup write keep the writer in memory. Evidence: 45570cc1's `WriteArray` raised the
  top-level call's Tier1 stack accesses from 75 to 127, and the runner A/B of the rc.10 review head against main
  measured Lookup serialize (a .NET `Lookup`, which never took the path) at 0.88x on x64; the writer by value still
  left 106; reverted.
- Batch writes at the typed lookup boundary: dispatch once from the lookup formatter, write the existing group array
  and reuse the span writer for each group, and return after the zero count to avoid both dispatch and enumeration on
  empty lookups. Why: an array branch in the public grouping formatter removed its enumerator allocation but slowed
  short foreign groupings even after outlining. Evidence: four local M4 pairs at offsets zero and one reduced the 257-
  and 8,193-element int and string lookup timings with byte-identical outputs and 1,064 B fewer allocations per write;
  foreign short-input timings varied. The lookup entry grew from 75 to 99 IL bytes; for int elements it grew from
  1,896 to 2,168 B on Arm64 Tier 1 and from 1,417 to 1,670 B on x64 cross-JIT (`45570cc1`). Arm64 only; x64
  unmeasured.
- Write packed sorted-set nodes in order into the reserved output for sets of 32 or more items: keep the existing layout
  probe, bound the span by the count, retain the array traversal for references and other layouts, and warm both sides
  of the count threshold before measuring short fallbacks. Why: sharing the span traversal with reference sets or
  adding the gate to the shared copied-pair writer hurt the controls. Evidence: four M4 rounds on `SortedSet<int>` at
  offsets zero and one improved the 32–8,193 item writes by 10–30% at zero steady allocation, with matching bytes;
  reference set results varied and the dictionary candidate was withdrawn. The dispatch grew from 48 to 81 IL bytes and
  the packed helper added 67 B of IL; the Arm64 packed helper and recursive copy were 408 and 268 B; the x64 cross JIT
  emitted a 221 B MinOpts helper and no span recursion listing; the measured caller stayed at 208 B on Arm64 and 170 B
  on cross-JIT x64 (`04894a90`). Arm64 only; x64 unmeasured.
- Route compact sorted dictionary pairs from their own dispatch into the same tree writer, requiring an identity block
  map and a pair of at most eight bytes, and write the root item directly for a single packed pair instead of entering
  the recursive traversal. Why: larger pairs and short fallbacks were sensitive to the count gate. Evidence: the final
  M4 probe improved `SortedDictionary<int,int>` writes at all sampled sizes from 1 to 8,193 and at both offsets in all
  four rounds; larger, padded and reference pair controls varied. Wire bytes and zero steady allocation matched. The
  dictionary dispatch grew from 76 to 114 IL bytes and the new helper took 42 IL bytes, 380 Arm64 FullOpts bytes and
  298 x64 cross-JIT bytes; the outer measured caller stayed at 208 and 170 B, respectively (`cccbcc28`). Arm64 only;
  x64 unmeasured.
- Walk a checked linked-list ring in pairs for raw writes at 32 or more items: bound the writes by the captured count,
  check the ring and version at the end, keep padding clearing, compile the new helper with `AggressiveOptimization`,
  and keep the original small-list writer inlined. Why: a one-node loop lost on large int lists, the two-node trial
  without early optimization also lost, and merely moving the branch out of the old writer still hurt small lists.
  Evidence: with both inlining choices the M4 int probe improved 127-item writes by 5–31% across all pairs, while the
  31-item zero-offset control added under 1.1 ns in three rounds and 8.2 ns in one; larger results varied. Wire bytes,
  including an odd tail and padding, and zero steady allocations matched. The original writer kept 195 IL bytes; the
  ring helper added 145 IL bytes and was 316 B on Arm64 and 240 B in the x64 cross listing for int (`b099fc74`). Arm64
  only; x64 unmeasured.
- Keep the checked linked-list builder's current read loop. Why: separating the first node removed the null
  predecessor tests without a repeatable end-to-end gain; a second trial consumed raw elements from the collection
  entry's already checked payload, then advanced the reader once after publishing the ring, but still allocated 48 B
  per int or long node and timings changed direction across the four M4 pairs at offsets zero and one. Evidence: the
  int Arm64 entry was 436 B before and 120 B plus a 288 B raw helper after; the x64 cross entry was 517 to 538 B in
  MinOpts and the raw helper was 284 B in FullOpts; entry IL was 172 to 177 B with a 104 B helper. Wire bytes and
  allocation matched and the .NET 8/10 targeted suites passed. Both trials were withdrawn. Arm64 only; x64 unmeasured.

## Dictionary refill (reading into an existing dictionary)

- Skip the seen-key set when the stale dictionary pass removes every entry, preserving removal and recycling order, and
  keep the empty test in the stale pass rather than the refill loop. Why: the refill-loop placement regressed same-key
  reuse in the local A/B, while the stale-pass version saved 64 bytes per empty refill.
- Keep one shared removal loop and no shared empty set. Why: a shared empty set improved partial removal but made the
  16-entry empty refill 5–8% slower across four local rounds, including two with reversed loading order; splitting the
  empty and partial removal loops grew the Arm64 helper from 620 to 924 bytes, with the local partial-removal timing
  within the noise band. Arm64 only; x64 unmeasured.
- Skip the set when one key survives in a dictionary of at most 16 entries on modern .NET and neither element type needs
  recycling: scan with the dictionary's comparer, outline the scan, and retain the existing path for larger
  dictionaries. Why: an unbounded scan slowed the 2,048-entry unaligned case by 25–45%. Evidence: with the bound and an
  outlined scan, the large control varied alongside A/A, and the two-entry unaligned case improved by 12–61% across four
  M4 pairs; eligible cases allocated 32 instead of 208 B. RemoveStale and its helper used 126 plus 80 IL bytes versus
  84, 972 plus 672 Arm64 FullOpts bytes versus 692, and 810 plus 605 emulated x64 bytes versus 560. All 54 wire cases
  matched. Arm64 only; x64 unmeasured.
- Extend the direct scan to two incoming keys only when the dictionary has at most four entries, sharing the
  eligibility checks with the one-key path and using the dictionary's comparer for both keys, including equivalent
  duplicates. Why: a broader scan regressed 16-entry refills. Evidence: the bounded scan allocated 64 instead of 240 B
  and took less time in 15 of 16 M4 pairs; single-key timings varied alongside same-binary controls. The stale pass grew
  from 126 to 158 IL bytes, from 972 to 1,056 Arm64 FullOpts bytes and from 810 to 898 emulated x64 bytes; the new
  two-key helper used 96 IL, 724 Arm64 and 671 x64 bytes. All 54 wire cases matched, including offsets 0, 1 and 7
  (`55550c16`). Arm64 only; x64 unmeasured.
- For three or four read keys and at most four dictionary entries, share four local cached hashes between uniqueness
  and membership checks (hash collisions must still call the dictionary's comparer), keep recycling on the established
  fallback, hold the cache in a local four-int inline array rather than `stackalloc`, and record allocation as the
  repeatable gain. Why: equality-only scans regressed four-key reordering, a default-comparer branch worsened the
  three-key integer case, and separate uniqueness hashing helped reordering but repeated hash work for removals; a
  `stackalloc` cache forced Tier0-FullOpts and raised first string-key reads from 2.1–2.7 to 3.1–3.3 ms. Evidence:
  sharing the cache removed those repetitions; integers saved 168 B on three-key removal and 232 B on four-key
  reordering, strings 176 and 256 B, respectively. The inline array restored ordinary tiering and first reads to
  2.25–2.46 ms, while leaving timing sensitive to promotion and warmup. RemoveStale grew from 158 to 205 IL bytes and
  the new helper used 320 B; for int keys the Arm64 FullOpts entry grew from 816 to 1,076 B plus a 776 B helper, and
  emulated x64 from 619 to 841 + 764 B. Duplicate keys, custom comparers, all-colliding hashes, wire bytes, recycling
  and both netstandard builds passed; .NET 10/8 passed 133/121 targeted tests (`77bfb0ea`).
- Copy the dictionary refill's `Reader` with struct assignments, in the refill, in the fresh dictionary pair loops and
  in the outer fresh dictionary copy (the pair loop still receives a local and the caller writes it back only after
  success). Why: the Arm64 JIT emitted redundant zero stores for a default local followed by field-wise `CopyTo`;
  passing and returning `Reader` by value instead grew both callers and both refill listings. Evidence: in the refill,
  Deserialize IL went from 181 to 171 B and the Arm64 caller from 1,100 to 1,088 B for string keys and 536 to 520 B for
  integer keys, emulated x64 callers from 947 to 935 and 397 to 383 B; four warmed M4 pairs did not reproduce the first
  run's large integer-key swings. In the fresh pair loops `ReadPairs` and `ReadGuarded` each lost 10 IL bytes; the
  string-key `ReadPairs` went from 1,156 to 1,080 Arm64 FullOpts bytes and 932 to 925 emulated x64 bytes, the
  integer-key guarded loop from 520 to 524 Arm64 bytes and 373 to 371 emulated x64 bytes; default-JIT timing varied and
  the 2,048-pair slowdown did not repeat when the measured batches ran without GC. In the outer copy, Read lost 10 IL
  bytes (229 to 219); the string- and integer-key Arm64 callers moved from 960 to 952 and 628 to 616 bytes, and
  emulated x64 from 820 to 807 and 448 to 436; four M4 default-JIT pairs varied alongside the identical-binary
  controls. Allocations and 30 wire comparisons matched each time (at offsets zero, one and seven for the outer copy).
  Arm64 only; x64 unmeasured.
- Keep ordered string-key probing out of the shared refill loop, and keep the existing refill until a prefix loop
  improves both tiers. Why: passing each existing key into StringFormatter until the first mismatch cut same-order
  allocation from 512 and 19,680 B to zero for 16 and 512 entries, and warmed M4 pairs improved those cases, but the
  16-entry unaligned reversed case regressed even after accounting for an identical-binary control; outlining the probe
  improved that control but slowed the custom-comparer case by 35–92%. Evidence: inline Refill grew from 298 to 469 IL
  bytes, 2,484 to 2,916 Arm64 FullOpts bytes and 2,056 to 2,649 emulated x64 bytes; the inline variant matched all 36
  wire cases, and both variants passed the changed-key and reference-comparer regressions. An outlined ordered prefix
  that stops before the first different key (leaving the ordinary merge to read that key) kept 16- and 512-entry
  same-order refills at zero allocation, with 512 entries 35–44% faster in four M4 pairs, but the 1,024-entry result
  reversed with warmup: shorter runs at offset one slowed in every pair, while longer runs at both offsets sped up by
  40–68%; the default JIT produced distinct OSR bodies (2,168 and 1,112 B). Optimizing from the first call preserved
  the 1,024-entry gain but slowed 16-entry same-order reads at both offsets. Refill and its helper used 406 plus 125 IL
  bytes versus 298, 2,636 plus 1,508 Arm64 FullOpts bytes versus 2,484, and 2,243 plus 1,370 emulated x64 bytes versus
  2,056. Both variants matched all 48 wire cases and passed targeted tests. Arm64 only; x64 unmeasured.
- Keep the comparer-based `SameKeys` loop with its rented-key indexer. Why: neither alternative gave a repeatable gain.
  Evidence: a separate ordinal string loop behind an exact default-comparer check improved the isolated 1,024-key
  comparison by less than 1% across four M4 pairs, and the 512-key timings tracked the identical-binary control; IL
  grew from 98 to 151 plus 74 B, Arm64 FullOpts from 780 to 924 plus 428 B, and emulated x64 from 662 to 792 plus 374
  B, while Refill itself kept its code size; allocation and all 36 wire comparisons matched. A byref cursor (the caller
  proves the buffer length) removed the indexer's FullOpts range failure on both architectures but did not produce
  repeatable gains in isolated or end-to-end M4 comparisons: IL grew from 98 to 111 B, Arm64 FullOpts fell from 780 to
  760 B, and emulated x64 grew from 662 to 677 B; Arm64 Tier 1 fell from 1,540 to 1,512 B and still checked the
  dictionary enumerator's entries; all 45 wire cases and allocations matched. Both candidates were withdrawn. Arm64
  only; x64 unmeasured.
