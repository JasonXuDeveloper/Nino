# The JIT: inlining budget, registers, folding

Part of the `optimize` skill. The rules about what RyuJIT inlines, keeps in registers and folds, each with the
evidence that set it. Read it before changing a read or write path of Nino.Core, Nino.Immutable or the emitted code,
and when `asm.py compare` lists methods the change did not touch.

## Inlining budget

- Use the length of the public `BitArray` byte view when reserving its wire payload.
  The view is already sized for the bits, so slicing it again adds an unnecessary check.
  Keep the final-byte mask and verify dirty padding on unaligned output. The formatter
  went from 86 to 77 IL bytes and from 409 to 369 x64 ReadyToRun bytes. Arm64 timing
  varied around 4 KiB after changing run order, with identical bytes and zero allocations.
  An explicit nonempty guard let x64 fold the final-byte index check without using a byref.
  The standalone mask was 38 x64 bytes with the guard, 44 at baseline, and 62 with a guarded byref.
  Both guarded forms had the same 712-byte Arm64 Tier 1 caller versus 716 at baseline. Neither
  consistently improved the local timings. Keep the existing mask and its bounds check.

- Build the raw span from `ReadBlock` on the byref reader targets. This gives the span and
  block reads one bounds check and cursor advance in the source. The standalone IL fell from
  29 to 14 bytes and the x64 ReadyToRun body from 57 to 49 bytes, with the same Arm64 Tier 1
  caller size. Keep the portable slice. Switching it to the byref form regressed the local
  1-byte and 32 KiB probes. Byte output, failure cursor and steady-state allocations matched.

- Keep the span copy in `NinoArrayBufferWriter.Grow`. Switching to `CopyToNew` regressed
  the 1 KiB growth case in four local rounds. Applying it only from 64 KiB preserved the
  smaller path but did not produce a repeatable gain across 64 KiB and adjacent inputs.
  The threshold variant grew from 148 to 195 IL bytes and from 233 to 325 x64 ReadyToRun
  bytes. Prefix bytes and allocations matched.
- Keep the existing empty handling in `NinoArrayBufferWriter.ToArray`. An early return
  accelerated the empty result but slowed the 65-byte export in all four local rounds.
  Checking for zero inside the short copy branch removed that pattern, but gave no stable
  empty-case gain. Exported bytes and allocations matched for zero through 32 KiB.
  The early return grew from 63 to 72 IL bytes and from 548 to 556 Arm64 Tier 1 bytes.
  The short-branch form used 66 IL bytes and the same Arm64 and x64 sizes as the baseline.

- Keep the shared span read in `MemoryFormatter.Deserialize`. Sending a fresh array directly
  to the reader instead of obtaining its span through `Memory<T>` did not change allocations.
  The inline variant grew the Arm64 Tier 1 caller from 524 to 556 bytes. Moving the fresh path
  out of line reduced the entry from 118 to 54 IL bytes but added an 88-byte helper and slowed
  the single-int fresh case and the 8,192-int reuse case in every local round. The variants
  preserved the wire bytes and read an unaligned source into the same sliced destination.

- Write canonical arrays directly into the span of a generic buffer writer. Reuse
  `NinoLayout.CopyToWire` for the disjoint case and preserve overlap and padding semantics.
  Plain span copying regressed the 8 KiB byte case in four local rounds. The existing kernel
  improved it in each rerun and reduced the 8,192-element int path to two active 32 KiB
  buffers instead of three. Both builds allocated 0 B after warmup. The raw array dispatch
  remains out of line from the concrete buffer writer path.
  The boxed buffer writer overload already uses the same path. Removing the repeated formatter
  lookup improved short boxed arrays locally, both when dispatch moved into the public method and
  when the existing helper received the lookup result. Both versions produced slower 8,192-item
  external writer samples in three of four rounds. Moving dispatch into the caller grew its
  Arm64 Tier 1 listing from 668 to 1,880 bytes, while passing the result kept it at 668 bytes.
  Keep the isolated alternatives for future measurement.

- Delay copying the prefix of a full synchronous stream buffer until the next read returns bytes.
  A non-seekable stream ending at a buffer boundary needs the EOF read but no prefix copy.
  The 64 KiB wire case improved in each of four local rounds, with the adjacent 64 KiB plus
  one byte case within noise and zero steady-state allocations on both sides. The 4 KiB timing
  was noisy. Keep the ownership and EOF paths out of the common read loop.
  A separate minimum-sized probe rental avoided the 128 KiB rental at 64 KiB EOF, but increased
  the non-seekable stream's read calls from six to ten at that size and from seven to twelve at
  96 KiB. It also grew the full-buffer helper from 88 to 129 IL bytes and had no repeatable
  steady-state gain at 64 KiB. Keep the growth read as one call into the next rental.
  A single bounded read for exact hidden-buffer `MemoryStream` instances did not improve throughput
  consistently. The variant in the synchronous rental path left the exposed-buffer branch intact but
  grew `ReadToEnd` from 91 to 174 IL bytes, plus a 44-byte helper. The 64 KiB hidden-buffer case
  had more slower than faster rounds with either placement, with zero steady-state allocations.
  Keep the existing stream path.
  In async reads, retaining both rentals across the await raised `MoveNext` from 376 to 510 IL bytes
  and added 8 B per operation in the suspended 4 KiB probe. Testing for immediate EOF in a non-async
  helper kept the suspended 4 KiB allocation flat and limited `MoveNext` to 387 IL bytes. It improved
  immediate 64 KiB reads in four local rounds but regressed immediate 4 KiB reads, so keep both
  variants as experiments. Measure immediate and suspended completions separately.

- Use a direct lookup before constructing a cached value when the target has no stateful factory overload.
  On a miss, pass the value to `GetOrAdd` and return the value that wins publication. The portable layout cache's
  factory already computed outside the dictionary lock. The new hit path removed the closure and allocation calls
  in both Arm64 and x64 listings. Use the stateful factory overload where the target supports it.
- Reserve the output array and its offset when a BCL copy API accepts those directly. The portable
  `BitArray.CopyTo` path writes into the output instead of renting and copying a temporary. For 24 KiB of
  bits the active payload footprint falls from about 72 KiB to 48 KiB, with the same padding on the wire
  (a0ee2414).
- Keep per-segment reservations for the general fixed-width sequence writer. Reserving all
  segments at once and checking the declared element count removed repeated capacity checks,
  but slowed both the two- and 32-segment small-int writes in all four Arm64 rounds.
  The longer writes were mixed, with identical bytes and zero steady-state allocations.
  `WriteSegments` grew from 118 to 246 IL bytes and from 1,524 to 1,592 Arm64 Tier 1 bytes.
- Reserve the header and payload together when writing a multi-segment byte sequence with the built-in
  formatter. Reuse `NinoSequences.CopySegments` to check the declared length while copying into the reservation.
  Match the exact formatter type so the JIT removes the generic segment loop. The Arm64 entry shrank from
  3,020 to 352 bytes and calls the existing 708-byte copy helper.
- Decode a portable `BigInteger` payload of at most four bytes as a signed integer after the length check.
  Sign-extend the last byte of a three-byte payload and use the existing unaligned reads for two and four bytes.
  This removes a 32-byte temporary allocation per short value on CoreCLR running the netstandard2.0 build.
- Rent a character buffer for portable `StringBuilder` writes of 4,096 or more characters. Use the existing
  UTF-16 reservation and copy only the builder's length. A 1,024-character cutoff regressed multi-chunk
  builders, while 4,096-character single- and multi-chunk cases improved without a steady-state allocation.
  An empty pool bucket still allocates on the first rent.
  Replacing the pinned UTF-16 reader append with the span overload kept bytes, truncated-input
  errors and zero allocations in the reuse probe. It reduced the Arm64 Tier 1 formatter from
  1,280 to 1,220 bytes but increased x64 ReadyToRun from 388 to 413 bytes. No timing advantage
  persisted across the measured lengths. Keep the shared pointer append for now.
  Append a single Latin-1 character directly instead of sizing the builder and widening a
  chunk. The one-character reuse probe fell from about 30 ns to 6 ns in four local rounds,
  with zero allocations and identical bytes. Longer inputs had no stable regression.
  The helper went from 97 to 111 IL bytes and 421 to 450 x64 ReadyToRun bytes.
  For two to sixteen Latin-1 characters, widen into one small stack buffer and append once.
  Keep the stack allocation out of line from the single-character and chunk paths. Local
  Arm64 reuse probes for 2, 8 and 16 characters improved in all four rounds, at zero
  allocations. The 17-character control was mixed. Output bytes matched, including
  unaligned input, and a test covers embedded nulls and high Latin-1 characters.
  Extend the one-append approach to 256 characters through a separate bounded stack helper.
  Move the existing chunk loop out of line as well. Keeping it in the dispatcher regressed
  the 16,384-character reuse probe even when the short helper was unchanged. With the loop
  isolated, 64 and 256 characters improved in every local round, and 257 and 16,384 recovered
  and improved too. Tier 1 was present on both sides, with the probe caller shrinking from
  2,004 to 1,336 Arm64 bytes at 16,384 characters. The dispatcher fell from 125 to 54 IL bytes,
  with separate 26- and 97-byte helpers. Wire bytes and steady-state allocations matched.
  Pre-sizing the cleared builder before the chunk loop saved 16 B when growing from a small
  capacity, but regressed 1,024 characters. Restricting it to insufficient capacity at 4,096 or
  more characters improved growing builders at 4,096 and 16,384 in all four local rounds, yet the
  4,096-character reuse control slowed in each round. The guarded helper grew from 97 to 122
  IL bytes, 928 to 992 Arm64 FullOpts bytes and 421 to 472 x64 ReadyToRun bytes. Wire bytes
  matched. Keep the existing chunk growth for now.
- Write the zero count of an empty raw dictionary without entering its out-of-line pair loop. Keep the
  reservation shared with non-empty dictionaries. The Arm64 probe saved about 7–9 ns on empty dictionaries
  at both output offsets, with the non-empty controls on par.
  Skip entry-view lookup after the same zero reservation for raw hash sets. The Arm64 probe saved 5–7 ns,
  with dense and sparse non-empty sets on par.
  Return once for an empty hash set read after creating or recycling its destination. This keeps reuse and
  comparer setup intact and removes the repeated zero tests before capacity and fill. The local Arm64 probe
  saved 6–8 ns on empty reads with unchanged allocations; the formatter shrank from 424 to 416 IL bytes.
- Share the actual presizing clamp in `NinoElements.Capacity` and keep the cheap eligibility tests at the hash set
  and dictionary callers. A shared inline branch shortened the two callers by 10 IL bytes but added a 14-byte
  helper. The Arm64 listings kept their sizes, only the x64 set listing shrank (6 bytes), and the local A/B showed
  no repeatable throughput gain or allocation change.
- Leave the empty-tail test inside `NinoElements.WriteItems` for variable-size queue writes. Adding a
  `first != count` guard at the queue saved work for a one-string queue in three of four local rounds, but
  the 16-string case varied widely and the 1,024-string case stayed on par. The extra branch grew the Arm64
  caller by 16 bytes and the x64 queue formatter by 12 bytes. The raw block path keeps its existing guard.
- Keep the portable stack's reverse-then-write path. Walking the rented snapshot backwards for variable-size
  reference elements saved about a fifth of the time for 512 and 2,048 strings in the forced fallback, but
  grew the raw Tier 0 helper from 356 to 408 Arm64 bytes. The 64-integer case slowed by 2–4% across four
  local rounds even after a value-type gate restored its FullOpts listing to 1,572 bytes.
  A separate reverse-walk helper restored the raw Tier 0 helper to 356 bytes. Dispatching to it from
  `Serialize` added 40 IL bytes there and grew the normal-layout Arm64 probe from 4,240 to 4,624 bytes.
  The 64-integer normal-layout control slowed in every round, so keep that dispatch out of the hot entry.
  Replacing the reverse-walk span indexer with `Unsafe.Add` removed its range failure in both listings
  (Arm64 1,172 to 1,164 bytes, x64 920 to 858). The one-string forced-fallback case slowed in all four
  local rounds, so the bounds-check removal alone does not justify reviving that candidate.
- Walk the checked key and value arrays with `Unsafe.Add` in the variable-size `SortedList` write loop.
  This removed range failures and saved 196 Arm64 bytes for `int` keys and string values. Single-pair writes
  slowed in four local rounds. Keep the one loop: a separate single-pair arm duplicated the inlined formatter,
  growing Arm64 from 1,828 to 2,772 bytes and x64 from 1,584 to 2,770.
- Keep the current seeded string hash loop. An unaligned UTF-16 word load and a byref tail preserved the
  reference hash and removed range failures (Arm64 588 to 428 bytes, x64 611 to 550), but four-character
  inputs slowed by 6–8% across four local default-tiering rounds and 15-character inputs also slowed.
- Reserve the raw `PriorityQueue` count and pairs together inside its existing by-value writer helper.
  Keep the public writer's dispatch intact: putting the raw gate in `Serialize` slowed the reference-element
  loop, while moving it into the helper restored the original Arm64 and x64 listings for that loop.
  The local 257-integer-pair case cut write time by 56–61%, with the enumerator preserving heap order.
  For reads, checking the whole pair block and walking its byref removed both per-pair bounds checks.
  Using `Peek` and advancing on success also avoided the reader copy, but neither variant consistently
  improved throughput: the `Enqueue` call remained. Keep the experiment isolated and evaluate heap
  construction separately from byte loading.
  Adopting an already ordered integer heap through checked .NET 10 fields cut the local 8,193-pair reuse
  time by about 74%, with zero steady-state allocation. Keep the public enqueue fallback for any wire
  order or comparer that does not match the checked heap (`runtime-internals`).
  Reuse that checked node array for writes, copying the live count in heap order. The local 8,193-pair
  integer case cut write time by about 90% against the counted enumerator path.
- Skip the seen-key set when the stale dictionary pass removes every entry. Preserve removal and recycling
  order. Keep the empty test in the stale pass rather than the refill loop. The latter placement regressed
  same-key reuse in the local A/B, while the stale-pass version saved 64 bytes per empty refill.
  A shared empty set improved partial removal but made the 16-entry empty refill 5–8% slower across four
  local rounds, including two with reversed loading order.
  Splitting the empty and partial removal loops grew the Arm64 helper from 620 to 924 bytes. The local
  partial-removal timing stayed within the noise band, so keep the shared loop.

- A branch whose condition is a method call (`RawFormatter.Is`, `NinoBlockCollections.FixedSize`, ...) is compiled and
  inlined even for the types where it is dead, and uses up the budget there. Keep rare or large paths (vector loops,
  element by element loops, string allocation, recycling) out of line (`NoInlining`), with only the common path inline.
  This holds for a test that folds away too: callers' budgets count an inlinee's whole IL, so a branch for one platform
  goes in the out-of-line part, never in the inline one. Evidence: `Is64Bit || IsX86 || UnalignedWide32` written in
  `NinoUnsafe.Copy` changed the x64 code of an ImmutableSortedDictionary write (`NinoBlockCollections.WriteAll` inlined,
  `Copy` left a call) in an `asm.py` dump with `DOTNET_TieredCompilation=0`; moved into `CopyOver64`, all 990 methods
  were identical to main (`CopyBlock` keeps Arm64's branch out of `Copy` for the same reason). The root's own IL counts
  the other way: the JIT sizes a method's whole inlining budget from its IL size (estimate 60 + 3 per IL byte, times
  10), dead branches included, so a folded test in a root that is otherwise one call (an interceptor) raises the budget
  and changes what is inlined under it. Never add an option to the default path as a run-time branch, not even a `static
  readonly` one: decide it at generation time (an MSBuild property the generator reads, `NinoRegularCalls`,
  `NinoPrefetchLargeCollections`). Evidence: an interceptor `NinoPrefetched.Enabled ? SerializePrefetched(...) :
  Serialize(...)`, folded at Tier1, made PolymorphicClassArray's `Serialize(in PolymorphicClassBase[], IBufferWriter)`
  1,060 bytes instead of main's 770 (single-block inlinees 142 to 155; `DOTNET_TieredPGO=0` dumps), while
  NonGenericBase's 1,112 methods stayed identical.
- Add nothing to a method whose inlinee sits at the edge of the JIT's inlining budget, not even code that is dead for
  the instantiation at hand: the budget is spent by IL size, before branches fold. `NinoBlockCollections.Lanes16` (three
  inlined copies in `Lanes`) became a call per group on Arm64 when `Lanes` gained a fused bool check (accumulators by
  reference, two groups a step, or groups returned by value), every lane path 1.4 to 5 times as slow on Neoverse-N2
  (TupleList and `HashSet<int>` writes included, run 41035440735), while x64 inlined everything. Put the new path in a
  method of its own and pick it by a `static readonly` (`LanesChecked`, AVX2 only), and check the Tier1 Arm64 listing
  under qemu (`platforms`, with tiering on: `DOTNET_TieredCompilation=0` listings inlined what Tier1 did not) for `blr`
  in such a loop. Two groups a step (a smaller loop share for one-vector groups) measured nothing on AVX2 Zen and lost 3
  to 8 % on `HashSet<int>` writes (run 41052335826): not landed.

- Give an out-of-line kernel one out-of-line entry with its loops inlined into it, and keep that entry at Tier1:
  a size dispatch (`NinoUnsafe.CopyOver64`) that called the vector loop (`CopyVectors`) out of line made two calls per
  copy of 129 bytes to 32 KB, three through `CopyToNew`, where rc.7's inlined `Copy` made one: 100 12-byte structs
  serialized in 0.98-1.27 times rc.7's time (.NET A/B run 41413072278). The loops are now `AggressiveInlining` into
  `CopyOver64Inline`, which `CopyOver64` and `CopyToNew` both inline. Never mark such an entry `AggressiveOptimization`
  to keep the loops' layout: fully optimized code folds no `static readonly` field (it tested `NinoUnsafe`'s class init
  and loaded `AmdCopy` and `IceLakeMemmove` per copy) and the class-init call made it save four registers in the prolog
  of every copy, the 65-128 byte ones included; that form measured Inventory serialize 1.114 times main on EPYC 7763
  (run 41455371503). With one call per copy at Tier1, 100 12-byte structs serialized in 0.85 of rc.7's time on EPYC
  7763 and on par on Neoverse-N2, but still 1.07-1.09 on Xeon 6973P and 8573C (one job each, run 41534216283; the same
  loop as rc.7, not reproduced on a local AVX-512 Xeon). Round 126 tested that gap and did not close it: 1.077-1.084 on
  three Xeon 8573C jobs, 1.045-1.069 on 6975P, 1.03-1.06 on 6973P, 1.007-1.031 on 8370C, MemoryPack's identical copy
  0.98-1.04 in the same jobs; unchanged with
  `DOTNET_JitAlignLoops=0` (1.084, 1.069) and `DOTNET_TieredPGO=0` (1.061) in the quick jobs, and checking the
  64-byte path first in `CopyOver64Inline` was no faster locally on Emerald Rapids (.NET A/B runs 41621699299,
  41659495441, 41665524359, 41671303527). The gap was in main before round 123's copy changes (main 1.118 on 8573C,
  run 41413072278) and head against main is on par there. Round 127 found the source: where the writer's buffer lands
  in the page, not Nino's code. Judge such a gap by the destination's page offset before touching the path: print it
  per process (`(addr) & 4095` in the benchmark's Setup, A/B only) and run the same probe code in both builds. The
  pooled 320 KB buffer (ArrayPool, LOH) starts at page offset 1352 in every rc.7 process and 1944 in every main
  process, fixed per build by what `SimpleTest`'s static constructor rents first; on Xeon 8573C the case took 11.95-12.12
  ns at destination offset 1356 for every source offset seen and 12.84-13.15 ns at 1948 (20 ABAB rounds a job, run
  41806557589; 6975P 9.45-10.01 against 10.05-10.54). The machine code of the case is the same in both builds but the
  copy target; main's and rc.7's copy shapes measured on par inside one binary (8573C 12.03 against 12.05, run
  41687210928), and one probe calling `NinoSerializer.Serialize` into its own buffer measured 1.004 (8573C) and 1.008
  (6975P) of rc.7 across the two builds' processes, where `NinoSerializeSimpleStructs` measured 1.083 and 1.044 in the
  same jobs (run 41794983604); a null change measured 0.996 (run 41679647486). Within rc.7 alone the writer object's
  placement moved the case from 9.2-9.6 to 10.7-12.0 ns on 6973P (run 41802774827), more than the gap. A local copy
  table on Emerald Rapids took 14.6 to 41.8 ns for 1,200 bytes by (dst - src) mod 4096. A 200-byte or an 85 KB array
  allocated before the writer did not move the buffer. Nino cannot choose where a caller's pooled array lands, so this
  gap is not closable in Nino's code. Check the Tier1 listing on x64 and with the Arm64 cross JIT (`benchmarks/cross-jit.sh`): one
  method, a two-instruction prolog.
- Inlining a small out-of-line method that the JIT left as a call can be a wash: `NinoLookup.Add` inlined into the
  `ILookup` read (one call per grouping instead of two) measured 1.002 and 1.003 times main on EPYC 7763 and
  Neoverse-N2 (run 41455371503); not landed. Forcing `NinoDictionary.Refill` (the merge into an existing dictionary)
  inline, as the JIT chose for rc.7, grew every read into an existing dictionary (PooledSimpleClass `__ReadBody` 2,731
  to 3,514 bytes, 127 to 189 stack accesses) and made `NinoDeserializePooled` no faster in an in-process timing loop
  (`benchmark`); not landed.

## Registers

- Never pass `ref reader` or `ref writer` to a method that is not inlined, even in a dead branch: the address keeps
  the struct in memory for the whole method. Pass it by value and return it (`reader = ReadEach(values, reader)`).
  An instance method of `Reader` or `Writer` left out of line passes `this` the same way: `Reader.ReadString(current)`
  calls `ReadStringOver` with the reader's address whenever `current` may be non-null, so generated code reads a new
  instance's strings with `ReadString()` (`MemberModel.Fresh`): SimpleClass's `__ReadTop` went from 108 stack
  accesses to 10, TolerantClass's from 174 to 10. Look for `Reader:...:this` and `Writer:...:this` calls in a listing.
  The same rule keeps multi-segment input a copy: a `ReadOnlySequence<byte>` read across segments in place would need
  every bounds check to refill the reader out of line instead of throwing (the reader's address in every method that
  reads, or a refill inlined into every check), and regions, sections and deferrals address the data by absolute
  position. So the sequence overloads copy data that spans segments once, out of line (to the stack up to 1,024 bytes,
  else a pooled array: `NinoSequences`), and read one segment in place (Nino `d275a71e`). Compute the segmented
  length once and pass it to the pooled copy. Keep the bounds check there, before converting the length to an int.
  Share the copy out of line across direct and regular callers. Its bounded spans must consume exactly the
  reported length, so a custom segment's inconsistent `RunningIndex` cannot expose unfilled scratch bytes.
  A byref destination cursor and an exact-length destination span folded the second length
  check in Arm64 FullOpts. The first warmup did not reach Tier 1. With longer warmup, the 128 by
  512-byte read improved in four local rounds, but the write had mixed results. Allocations stayed
  at zero and wire bytes matched. The helper grew from 76 to 110 IL bytes, 728 to 736 Arm64 Tier 1
  bytes, and 207 to 233 x64 ReadyToRun bytes. Keep this variant as a read-path experiment.
  Isolating scratch reads and switching to `CopyToNew` or `CopyBlockUnaligned` did not reproduce
  the read throughput improvement. Both scratch helpers used 114 IL bytes and 238 x64 ReadyToRun
  bytes, versus 76 IL and 207 x64 bytes for the shared helper. The Arm64 Tier 1 sizes were 736
  and 724 bytes versus 732. Wire bytes and steady-state allocations matched. Keep the shared copy
  while the read-only alternatives remain in the experiment worktree.
  Keep the explicit `TryGet` loop and stop at the last segment. Replacing it with `foreach` made two
  32-byte segments slower on both reads and writes in two local rounds, even though Arm64 machine code shrank
  from 708 to 684 bytes. The JIT already shared the `CopyTo` and `Slice` bounds check.
  Do not probe array identity and offsets to coalesce two adjacent byte segments on every write.
  Two 32-byte adjacent segments went from about 24 ns to 27–35 ns in four local rounds.
  Disjoint short inputs also slowed, and inputs plus output around 64 KiB varied across A/B
  and A/A. `WriteSegments` grew from 118 to 138 IL bytes, 352 to 432 Arm64 FullOpts bytes
  and 312 to 369 offline x64 bytes. The probe helper added 169 IL, 1,184 Arm64 and 1,052
  offline x64 bytes. All six wire cases matched at an unaligned output offset with no
  steady allocation. Keep the patch for experiments.
  Raising the stack cutoff from 512 to 1,024 bytes avoided the pool round-trip for 516-, 804- and 1,024-byte
  messages in the local probe. Measure both sides of the cutoff and keep the fixed buffer out of inline callers.
  Read a complete built-in byte array directly from two segments after validating the header and actual length.
  Check segment adjacency before entering the direct path. Probing both spans on three-segment input regressed
  the fallback. Keep the copy through scratch when reusing an array that aliases input, or when replacing
  an array with an element or array recycler registered. Recyclers can mutate the source before it is copied.
  Keep split headers in a separate cold method that joins only the four header bytes. Joining them inside
  the ordinary two-segment reader regressed complete-header inputs in the local A/B.
  Sharing both segment and count validation through inlined helpers still slowed split-header reuse
  across both loading orders. Keep those checks local and share payload copying and replacement.
  Keep the regular and direct sequence readers' stack and pool scopes around their own span calls.
  Reusing the stream reader adapters inside a shared sequence scope made the 32-segment probe slower
  in both entry points across four rounds. Share the segment copy, rent and return primitives.
  Keep `Span.CopyTo` for direct two-segment payloads even when disjointness is known. Using
  `NinoUnsafe.Copy` made all measured sizes slower across both load orders and grew the Arm64
  two-segment method from 1,796 to 2,196 bytes.
- Mark every small helper a read or write path calls `AggressiveInlining`, even one whose arguments are constants
  and that looks certain to fold: in a large method the JIT's inliner gives up on it. `NinoLanes.Paired`, called
  with constants in the AVX2 group loop, stayed a call per load and spilled the vectors around it: AVX2 writes took
  twice as long as SSSE3's. Look for such calls in the Tier1 listing of every ISA a path takes (`--asm`, with
  `DOTNET_EnableAVX512=0` and `DOTNET_EnableAVX2=0` too).

- Separate the small raw fallback's typed local from the large raw entry. A size check in the same method does not
  stop Tier 0 from reserving a stack slot for a large `T` on an untaken branch. Keep a single final load within each
  path and compare trusted packed and wide reads as well as untrusted allocations.

- Read a collection whose shape repeats into its existing instance with a loop of its own over the data, as the fresh
  read does: `Reader.RefillStringPairs` (raw keys and strings into an existing `Dictionary`, the position and the end in
  registers, the value read over the current one with the compare its only call) instead of the general merge, whose
  value formatter read through the caller's reader copy in memory and called `ReadStringOver` and then `SameLatin1`
  per value. In-process timing loop (`benchmark`): a pooled class holding only a 10-entry `Dictionary<int, string>`
  139-162 ns against main's 183-213 and rc.7's 178-198; and store a value read over the current one back only when it
  differs (no write barrier per unchanged string): 123-156 ns. On runners `NinoDeserializePooled` took 0.83-0.92 of
  main's time on every CPU (.NET A/B run 41576130603, 12 rounds a job) and 0.96-1.00 of rc.7's on x64, 1.04 on
  Neoverse-N2, where neither the string compare below nor `CopyPairs` out of line moved it (1.005, 1.007 against a
  1.000 floor, run 41593418245; `CopyPairs` out of line made `NinoNodePooled` 1.033).
- Compare an unchanged short string read into an existing object inside the out-of-line `ReadStringOver`, never in
  the inlined `Reader.ReadString(current)` nor in a loop: `NinoStrings.Same` (one-byte Latin-1 header, length, the chars
  widened eight at a time and compared) before `NinoStrings.Read` saves `SameLatin1`'s call (`Ascii.Equals`); 1,000
  objects with a short name read into existing ones took 6.9-7.5 us against main's 11.1-11.5 and rc.7's 9.9-11.4,
  `NinoDeserializePooled` 223-271 ns against main's 251-268 and rc.7's 224-273 (in-process timing, `benchmark`); on
  runners 1,000,000 shuffled items read into existing objects in 0.53-0.86 of main's time and 0.56-0.95 of rc.7's on
  every CPU (runs 41534216283, 41576130603). The
  same test inline in `ReadString(current)` pushed callers past the inlining budget (the `Lazy<string>` read left
  `Reader.Skip` a call, 7 stack accesses to 22) and read the pooled class in 277-295 ns; inlined into
  `RefillStringPairs`' loop it made that loop slower (a pooled class with only the dictionary 179-206 ns against
  139-162, `NinoDeserializePooled` 275-307); added before `RefillStringPairs` existed, the dictionary merge's general
  loop, which reads each value through `ReadStringOver`, read the pooled class in 284-293 ns against 256-265 without it
  (round 123).

- Store nothing a read into an existing object leaves unchanged: generated code stores a string member only when
  `ReadString(current)` returned another string (fields and auto-properties, `MemberModel.PlainStore`; a setter with a
  body is still called). Each store into an existing (often gen2) object is a write barrier call, and on Neoverse-N2 it
  was the last part of `NinoDeserializePooled`'s gap: 1.04 times rc.7 before, 0.926 times the head without it on that
  CPU (.NET A/B run 41616438646, 20 rounds, MemoryPack 1.000), 0.967 times rc.7 after (Xeon 8573C 0.871, 6975P 0.861;
  run 41621699299). The same run priced `ReadBool`'s 0-or-1 check there: 1.001, nothing to win by weakening it.

## Tests that must fold

- Write a platform test that must fold as `A || B || ...` at each site, each operand a branch-free property (a
  constant or one `static readonly` field), never as one property whose body has `||`: the inliner gives a property
  with several returns a temporary, so the branch folds only after struct promotion was decided. Evidence: with
  `NinoUnsafe`'s 32-bit gate as one property (`Is64Bit || X86Base.IsSupported || WideAccess.Unaligned`), x64 read a
  24-byte struct with three `vmovsd` pairs instead of `vmovdqu` + `mov` and a 16-byte one with six `mov`s instead of
  two `vmovups`; written `Is64Bit || IsX86 || Unsafe.SizeOf<T>() <= 4 || UnalignedWide32` at each site, the x64
  listing was identical to main. A `|` of a constant and a field does not fold either: R2R x86 kept the field load
  and its class-init helper call.
- A JIT-time check made of several helper calls is one `static readonly` field instead (`NinoElements.Traits`,
  `NinoBlockPlan<T, TFormatter>.Size`): one load, nothing to inline.
- Decide "copy memory" against "convert" by a `static readonly` computed before Tier1 (`NinoLayout<T>.IsCanonical`,
  `CopiesAsIs`, `HasPadding`, `Size`), so the common platform (64-bit CoreCLR) pays nothing and every other one adapts
  to it (the owner's rule: 32-bit adapts to 64-bit, never the reverse). Prove it with JitDisasm of a scratch program
  that calls the array, list and value paths in a loop until Tier1 (`DOTNET_JitDisasm='P:* Nino*:* NinoGen*:*'`): no
  `NinoLayout` field or call is left (`NinoLayout<T>.Size` compiles to `mov eax, 32`, `CopyToWire` to a tail jump
  into `NinoMask.Copy`), for `Vector<int>[]` and raw structs holding tuples alike (round of the Vector<T> layout).

## Locals, listing size and arithmetic

- Scope a captured local inside the branch that needs its closure. The netstandard2.0 `Lazy<T>` formatter
  (a792e149) previously allocated before the presence check. Moving the local into the present branch
  removed that null-path allocation on Arm64 and x64 listings. A late helper with a large generic local
  would move that value onto the stack, and changing the Lazy publication mode lost present throughput.
- Pass a present nullable's raw value to a readonly-ref layout writer through
  `Nullable.GetValueRefOrDefaultRef` on .NET 8 and later (546d8265). Taking a value copy first zeroed and copied
  a stack temporary in the local Arm64 listing. The 128-byte probe's frame went from 336 to 208 bytes.
- Past the JIT's tracking limit, locals are zeroed in the prolog: rare paths out of line also keep the local count
  down.
- A smaller listing is not a faster one: measure. Reading TimeZoneInfo's two strings through one out-of-line helper
  with two `out` strings cut the listing but put both on the stack: 7.5 to 12.8 % slower on runners. A constant
  parameter at each call site (`share: false`) let the inliner drop the dead branch per caller instead, with the
  listing as before and the string classes 17 to 21 % faster.
- Move a loop into Nino.Core from generated code as an `AggressiveInlining` helper with its own accumulator and a
  pointer that steps down a count, the shape it had, not as a second loop of the caller: the JIT then compiles the
  caller as before. Round 129 moved `PrefetchMembers`' run loop into `NinoPrefetch.Ahead`: written in `Ahead` with an
  index (`for (int i = 0; i < near; i++)`), it became `movsxd` plus a scaled load, and sharing `Ahead`'s `sum` added a
  dead `xor eax, eax` and 8 bytes of loop alignment (157/141 bytes instead of 144/128); as `Members<TFormatter>(ref
  object first, int count)`, the FullOpts listings were byte for byte main's (`DOTNET_TieredCompilation=0
  DOTNET_JitDisasm=Ahead` over PrefetchTests on both sides; `DOTNET_JitStdOutFile` appends, delete it between runs).
- Replace `%` by the runtime's fast modulo (multiply-high, `HashTables.Bucket`) on x64 only: on Arm64 `udiv`/`msub`
  is cheap and the multiply sequence lost. ConcurrentDictionary's build with fast modulo read 18-23 % faster on EPYC
  7763, on par on Zen 4 (9V45, 9V74), and 1.1 % slower on Neoverse-N2 in two rounds (3d4baf2).

## Memory layout across threads

- Keep a field that a hot path reads off the cache line of anything another thread writes by putting it in a non-generic
  struct with `LayoutKind.Explicit`, 64 bytes of padding on each side (`Size = 64 + fields + 64`), held as a field,
  never as an explicit-layout base class or padding fields of the class itself. The runtime orders a class's fields as
  it likes and packs a derived class's fields right after the base's last byte, and objects are only 8-byte aligned,
  while a struct moves as one block with its padding. `NinoPool<T>` had the base-class form: CoreCLR put the derived
  `_count`, which batch transfers write, at offset 124, on `Filled`'s line (64) whenever the object's fields started on
  a 64-byte boundary, one placement in eight (field offsets read with `ldflda`; locally, 4 threads, the 7 of 40 runs
  placed so took a median 1134 ms against the other 33 runs' 940 ms, though the struct form's own spread was as wide);
  `NinoPoolLine` and `NinoPoolThreadLine` are the struct form. Generic classes cannot take an explicit layout either;
  the struct field works there. Read and write such a field through a local (`count = s.Line.Count` ... `s.Line.Count =
  count + 1`), not with `s.Line.Count++` in an expression: that took the field's address and loaded it a second time in
  `TryTake` and `Add` (one `add` and one load more per call); with the local the listing is main's, one `add` fewer.

## Startup: code run once per type

- **A Nino.Core method that generated code calls once per type (a registration) builds what it needs itself and calls
  only methods that are not generic over the formatter**, rather than calling another per-formatter generic helper.
  Why: for a class `T` its code is shared over `T` (`__Canon`) but compiled per struct formatter, and stays Tier0 (it
  runs once): a call from it to another method generic over the formatter is a second method compiled per type and a
  generic-dictionary lookup at run time, where the registry's exact code resolved it while compiling. Evidence (round
  129, `NinoTypes`, 160 types in fresh processes, local, direction only): `NinoTypes.Register<T, F>` calling
  `NinoFormatterCache<T>.TryRegister<F>()` registered them in a median 46.7 ms against main's 43.6 ms (two calls in the
  registry); building `new StructFormatter<T, F>()` itself and calling the non-generic
  `NinoFormatterCache<T>.TryRegister(NinoFormatterBase<T>)` took 34.3 ms against 37.4 ms, 2,277 methods jitted against
  2,396. Count with `DOTNET_JitStdOutFile=f DOTNET_JitDisasmSummary=1` (one line per compiled method, with its code
  size); time `NinoPolymorphic.TryRegister(typeof(X))` over many types, which registers without compiling formatters.
