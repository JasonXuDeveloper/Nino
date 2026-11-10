# Prefetch (every runtime)

Part of the `optimize` skill, for every runtime: the design (decided at generation time, eligible element kinds,
per platform), and every attempt measured in .NET, Go and Rust with why it did or did not land. Read it before
touching a collection loop's prefetch, `NinoPrefetchLargeCollections`, `PREFETCH_BYTES`, or before trying
prefetch again anywhere.

- Skip the near run when the formatter prefetches only objects. Even after folding a generated
  `PrefetchMembers` that returns zero, Arm64 still loaded up to 16 references in that loop.
  Distinguish object-only formatters at generation time and keep the old interface for legacy
  output and for actual member prefetch. On the M4 scalar probe, `Ahead` took 7.23–9.08 ns
  versus 11.59–12.50 ns across four paired runs after tier promotion, at zero steady allocation.
  Shared IL grew from 103 to 123 B, while object-only FullOpts fell from 216 to 152 B on Arm64
  and 114 to 68 B on x64 cross-JIT. Whole-array timings varied in both directions across
  repeated series, and short arrays below the threshold never took this loop. Keep the entrance
  gate placebo as a separate control (`44302f34`).

- Check the wire count before sampling the destination of a prefetched read. Keep the length test on
  the actual span since the top-level call receives the array by reference. On a long M4 destination
  with a mismatched count, the helper took 2.55–6.35 ns versus 9.09–21.57 ns at offsets zero and one
  across four pairs. Array and list IL stayed at 71 and 90 B. The array's Arm64 Tier 1 listing shrank
  from 720 to 704 B. The x64 cross-JIT printed the unchanged 163 B caller but did not list the helper.
  Matching round trips and the steady allocation measurements were unchanged (`cc1fc233`).
- Keep an off control even for long, scattered objects with no member prefetch. After removing the
  empty near run, M4's default-JIT probe still took longer with prefetch enabled in all four rounds
  on the offset-zero 299,630-object shuffled graph, for both writes and reuse reads. The sampler
  accepted the shuffled inputs and rejected the ordered ones. Wire bytes and allocation matched.
  Arm64 uses demand loads here, so the cost of prefetching cannot be inferred from x64's hint or
  from the smaller helper listing. Keep this workload separate from short collections that never
  pass the gate.

- Sweep the distance separately from the 16-element batch. On M4, one or four batches ahead and one
  load per four objects at the current distance did not beat the existing path across both
  28- and 64-byte object estimates and both operations. Use the off control for each operation.
  The matched generated formatters, wire bytes and steady allocations stayed equal. `Ahead` kept
  104 Arm64 and 68 cross-JIT x64 FullOpts bytes across all distances. Its shared IL was 123 B
  at baseline, 140 B for the distance candidates and 150 B for sparse reads.
- Treat short address deltas as a locality signal, and validate the policy separately for reading
  and writing. An Arm64 absolute-delta gate rejected reverse allocation order in all four M4
  rounds and still accepted all shuffled samples. It sped scalar reuse reads by 11–35% at both
  offsets but slowed scalar and wide writes in every round. The shared rule was withdrawn.
  The sampler moved from 76 to 116 IL bytes, 132 to 128 Arm64 FullOpts bytes and 120 to 113 x64
  cross-JIT bytes. Wire hashes and steady allocations matched. Buffer offsets zero and one
  are separate controls, not evidence of the object's alignment. The measured machine reports
  a 128-byte cache line, independent of the 64 KiB L1D working-set estimate.
  Restrict the bidirectional sample to Arm64 reuse reads of object-only formatters estimated at
  32 bytes or less. The 28- and 32-byte reverse probes improved in 15 of 16 paired measurements
  and tied in the other. The shuffled controls varied in both directions, including identical
  baseline-binary repetitions. Across 48 case/round comparisons, including the off control,
  the wire hashes matched and steady allocation was zero. Keep the shared sampler inlined into
  separate out-of-line entries so each direction rule is constant. The forward sampler's FullOpts
  moved from 132 to 128 B on Arm64 and 120 to 109 B on x64 cross-JIT. The reverse entry took 124 B
  on Arm64; the shared sampler and entries took 112 + 8 + 8 IL bytes against 76 B before.
  Arm64 array read helper sizes matched at 764 and 756 B for the two shapes (`a07f1ce0`).
  Stop the bidirectional sample after eight local steps. The remaining entries cannot make the sample
  scattered, while a positive result must still check all 17 entries for nulls. An early exit in both
  directions slowed the mixed forward sample in three of four rounds, so preserve forward counting.
  The final candidate cut ordered and reverse bidirectional sample times by 16–48% and 25–41%,
  respectively, across four M4 pairs. Shuffled and late-null timing varied. Decisions, wire bytes,
  and zero steady allocations matched. Forward FullOpts instructions were identical on Arm64 and
  cross-JIT x64. The reverse entry stayed at 124 B on Arm64 and grew from 111 to 115 B in the cross
  listing. Shared sampler IL grew from 112 to 137 B (`501adee6`).

- Prefetch member references only through fields or bodyless auto-property getters that cannot dispatch to an
  override. A backing field alone also describes a custom getter using `field`; executing either getter during
  prefetch would add a user-visible access before the formatter reads the member.
  Classify bodyless source getters from their accessor syntax after excluding abstract, extern, partial and
  overridable members. Avoid rescanning the containing type for an associated field on every property.

- Compare a default-on policy with both the unchanged default and a length gate that no input can pass.
  In the local benchmark project, enabling every eligible type changed 66 files and added 14,704 generated
  bytes. The gate alone grew the Arm64 Tier 1 non-generic and polymorphic array benchmark callers from
  2,664 to 3,412 bytes and from 1,184 to 1,716 bytes. The x64 cross-JIT listings also grew with the gate.
  Actual prefetch consistently improved the million scattered items versus the gate, while ordered shapes
  sometimes took longer. All six outputs matched byte for byte, and timed writes allocated nothing.
  Keep this broad policy out of the default and evaluate eligibility with short, ordered and shuffled
  inputs of the same type. Compare native code as well as IL, since the benchmark callers had the same IL size.
  A sealed-only policy requiring an eligible reference member changed 12 benchmark sources. It preserved
  the write gains on a million scattered items. Reuse reads kept their array and wire bytes with no steady-state
  allocation. The million ordered items were 11–33% slower than the gate-only variant across four local rounds.
  The sampler returned false for the ordered graph in every process. Compare the ordinary writer reached after
  sampling with the one used below the length threshold, since neither case runs the prefetch loop.
  Share the ordinary buffer-writer path after the sampler declines. The two direct overloads each used
  seven fewer IL bytes and the benchmark Tier 1 entry shrank from 508 to 352 bytes on Arm64 and from
  474 to 301 bytes in the x64 cross JIT. Ordered writes used less thread CPU in four supplementary local
  rounds, but identical-binary variation was large and the short and shuffled cases moved in both
  directions. Treat throughput as unproven. The generated source and all four wire samples matched.

- Confirm prefetch throughput in separate processes with one copy of the object graph per process. In the
  local 131,089- and 262,161-object probes, the apparent benefit of processing whole runs instead of
  checking each element flipped with assembly load order. The isolated ABBA rerun showed about 1–2%
  median improvement, within noise. Keep the batching variants in the experiment worktree.

- Decide whether a collection loop prefetches when the code is generated, never per element at run time (the owner's
  design). Eligible: elements of a reference type in a contiguous container (array, `List<T>`, a span of references),
  since each object and each of its reference members (strings, children) is a separate cache miss. Not eligible:
  structs, unmanaged and block elements (contiguous; the hardware prefetcher covers them) and linked containers
  (LinkedList, trees, SortedSet nodes: the next address is known only after loading the node). The decision and a
  per-type threshold, a cache budget divided by the element's estimated bytes (object size plus a cost per reference
  member), are `static readonly` values the JIT folds: an ineligible type's listing stays identical to main, an
  eligible one adds one length compare (`cmp r14d, <const>; jg`) and a cold call. Enter the long loop through the
  formatter type (the generated forwarder calls `NinoPrefetched.Write<T, Self>`), never through a method keyed on the
  element type: with T = __Canon that is a runtime generic lookup in every caller. Two stages for elements with
  reference members: at element i prefetch element i+2D, at i+D prefetch its members' targets. Why: a prefetch on
  every element cost 14-15 % on 1,000 cached objects, and a large-collection branch that needed a runtime generic
  lookup spilled the caller (Shapes 1 to 7 stack accesses). Platforms: .NET x64 `Sse.Prefetch0`; .NET Arm64 has no
  scalar prefetch (only SVE's), a load of the object's first byte summed into a sink stands in; Unity through a
  `NinoKernels` hook to Burst's `Common.Prefetch`, which exists only with the scripting define
  `UNITY_BURST_EXPERIMENTAL_PREFETCH_INTRINSIC`; Rust `_mm_prefetch` on x86 and `asm!("prfm pldl1keep, ...")` on
  aarch64, both in `src/simd.rs`; Go only through an assembly stub, one call per batch; wasm and TypeScript have
  none. .NET, measured and not landed (land/prefetch 10c2f5b, land/prefetch-llc cdd36a9 and 9e28b7d against
  5d474be, `ScatteredTest`; test branch runs 40666897058 and 40691513998): how the long loop takes the writer or
  reader decides the cost for every caller, and none of the three ways was free. By reference, the address of the
  caller's writer is taken where `NinoElements.Write` is inlined, so the caller's whole loop keeps it in memory
  (FixedClasses 6 to 66 stack accesses). By value and returned, x64 copies it into the outgoing argument area of the
  cold block (+125 bytes, +5 stack accesses, hot loop unchanged; fixed-size classes, whose bodies inline with every
  writer field in a register, spilled the element pointer, so they were excluded at generation time), but where
  nothing folds (Debug, Tier0) the argument and the result are two more writers in every collection loop's frame: a
  read of 400 nested `SelfArrays` needed 1,459 KB of stack instead of 1,219 KB, and
  `SelfNestingCollectionTests.EveryCollectionKindThrowsPastTheLimit` threw on Windows' 1 MB stack. Through a copy
  passed by reference (the copy's address taken, not the caller's), Debug needed 541-758 KB, but the copy holds GC
  references and is zeroed in the prolog of every caller: CommonTypes `NinoSerializeTree` (short child lists, one
  Write per node) 3-4 % slower on EPYC 7763, 9V45 and 9V74 and Neoverse-N2 (floors 0.99-1.015). A `default(TFormatter)
  is INinoPrefetchFormatter` test in the loop's own IL changed what the JIT inlined into polymorphic arrays
  (`NinoSerializePolymorphicClassArray` 20-28 % slower on EPYC 7763 and 9V45); `Takes` inlined folds it as well. The
  gate at the whole last-level cache (sysfs index*/size, GetLogicalProcessorInformationEx, 32 MB default) left 1M
  shapes of 24 bytes out on EPYC (32 MB L3) and Neoverse-N2, which a fixed budget made 1.9-2.4 times as fast; at a
  quarter of the cache: 1M shuffled `ScatteredItem`s serialized 3.0-3.3 times as fast on EPYC (7763, 9V45, 9V74),
  2.0 on Xeon 8573C (one job) and 2.4 on Neoverse-N2 (the Arm64 load), read into existing objects 2.6-5.4 times on
  EPYC and 1.18 on Xeon, unchanged on N2; scattered shapes 1.8-2.4 times on EPYC, +10 % on N2, 6.5 % slower on Xeon
  8573C (one job, no floor on that CPU; CommonTypes Shapes, which does not prefetch, moved 7 % there too). But the
  same shapes in allocation order (where the hardware prefetcher already works) were 8 % slower on EPYC 9V74 (15 %
  faster on 7763), and Players and PolymorphicClassArray 2 % slower on 7763. A second attempt (land/prefetch2 against
  4beee30, `ScatteredTest` plus every class with arrays or lists of classes; test branch runs 40717864662,
  40738931705, 40765079561, per-job medians against main-against-itself floors) passed neither the writer nor the
  reader: a `NoInlining` helper keyed on the formatter, `Ahead<TFormatter>(ref object, int)`, prefetched objects 32-47
  ahead and the reference members of the next 16 (`INinoPrefetchFormatter.PrefetchMembers`, emitted for non-generic
  classes of varying size), called once per 16 elements; the gate, a quarter of the last-level cache over
  `PrefetchBytes`, and the platform test fold into one `static readonly` threshold, and a sample of the first 17
  addresses (8 or more of the 16 deltas in (0, 1024]) leaves objects in allocation order alone. That fixed EPYC 9V74
  (shapes in allocation order 0.996-1.005, from 0.92) and kept the gains: 1M shuffled `ScatteredItem`s serialized in
  0.49-0.50 of the time on EPYC 7763, 0.39-0.40 on 9V45, 0.44-0.48 on 9V74 and 0.21 on Neoverse-N2, read into existing
  objects in 0.44-0.47, 0.38-0.41, 0.25-0.32 and 0.30, scattered shapes 0.55-0.66 on EPYC (N2 on par); Xeon 6973P-C,
  whose cache holds the set, on par. It did not land. In the shared loops (`NinoElements.Write`/`ReadEach`, a length
  test before a second, chunked loop), every short nested collection paid: Orders 3.3-6.5 % slower on EPYC 7763, 9V45,
  9V74 and Xeon 8573C and 6973P-C, Tree 1.3-2.2 %, Shapes 5 % on Xeon 8573C, and in Debug, where nothing folds, each nesting
  level's frame grew 144 bytes (the deep-nesting test's stack 1,471 KB instead of 1,351; `Type.IsAssignableFrom`
  instead of `is` was worse, 1,516 KB). A separate copy of the loop behind the test only moved the cost. So the second
  variant prefetched only top-level calls: the interceptors send `Serialize`/`Deserialize(data, ref value)` of an
  array or `List<T>` of an eligible class to `NinoDirect.SerializePrefetched`/`DeserializePrefetched`, whose formatter
  (`INinoPrefetchTopFormatter<T>`) tests `value != null && length > threshold` inline and calls the out-of-line loop
  only past it; nested collections, `NinoRegularCalls` and every other method compile exactly as main (`asm.py
  compare`, Debug frames identical). Inlining more than that test into the top-level call cost the JIT's budget
  elsewhere (PooledGraph `LeavesInPlace` 20 to 36 stack accesses). Still not landed: the 33-80 bytes the test adds
  before the loop (sizes 940 to 1,001, 1,316 to 1,386, 1,955 to 2,019; stack accesses unchanged) moved the loop (0xC4
  to 0xE5; the JIT does not align loops that make calls), and on EPYC 7763 `NinoSerializeNonGenericBase` was 2.0-6.6 %
  slower in 6 of 6 jobs and `PolymorphicClassArray` 0.6-5.8 % (floor 0.987-1.021), `HotShapes` (8,192 shapes, never
  prefetched) on EPYC 9V45 -0.5 to +13 % (floor 1.009-1.013), while the same cases were on par or up to 3 % faster on 9V45 and N2:
  the cost is where the code lands, not the work. Controls in one run against main 7d4d6b0 (test branch run
  40829244512, floor runs 40798121552 and 40829186191) proved it: a placebo with variant 2's exact code but a run-time
  threshold no collection reaches and an empty `Ahead` (case sizes identical, 1,001 and 1,001; HotShapes 1,386 and
  1,381) was as slow as variant 2 on EPYC 7763, NonGenericBase 0.98-1.07 (median 1.04) against 1.04-1.08,
  PolymorphicClassArray 1.03-1.07 against 0.97-1.04, while a null change (one more benchmark case, so other generated
  code moves) stayed in the floor: 0.98-1.01 and 0.98-1.00 (floor 0.98-1.01 and 0.99-1.02, 10 jobs). So it is not
  random placement: the length test's bytes before the loop cost those cases 3-7 % on 7763 every time, the scattered
  gains held in the same run (items 0.50-0.51, read into 0.47-0.51, shapes 0.63-0.66 on 7763; items 0.21 on N2), and
  it did not land. Any test in the top-level path costs this, so it landed as the owner's opt-in, the MSBuild
  property `NinoPrefetchLargeCollections` (unset, the generated code is main's byte for byte; a run-time setting
  could not do that, see `jit.md`, "Inlining budget", on the root's budget). Test branch run 40916374747
  against main 9d71c04, per-job medians: unset 0.98-1.03 on EPYC 7763, 9V74 and N2, as the floor (0.97-1.03); set,
  items written 2.0x (7763), 2.1-2.6x (9V74, 9V45), 4.8x (N2), read into 2.0-3.6x, shapes 1.5-1.75x (N2 1.02), with
  NonGenericBase 0.94-0.95 on 7763 and 9V74, the cost the property's docs name.
  Go, measured and not landed: a stub `prefetch8` (eight PREFETCHT0 / PRFM PLDL1KEEP a
  call) in `WriteSlice` above a per-type threshold, objects 16 elements ahead and their strings 8 ahead, wrote
  1,000,000 shuffled records 2.2-3.1 times as fast on EPYC 7763, 9V45 and 9V74 and 4-5.5 times on Neoverse-N2, but
  13-38 % slower on Xeon 6973P-C, whose L3 holds the 100 MB set (its base was already 2-3 times as fast: nothing to
  hide, only the extra loads and calls), and moved short-slice cases 1.5-4.7 % on x64 (test branch 4e2099b, 42ae966
  against c44171f). Keep only the length compare inline: a plan lookup that does not inline (cost 147) in every
  `WriteSlice` cost 1-3.8 % on SimpleClass. A third round gated it on the last-level cache (sysfs index*/size,
  sysctl hw.l3cachesize, 32 MB default; on when length times the object plus 64 bytes a string exceeds it; the long
  path a `//go:noinline` function behind one compare, inlining as main) and still did not land (test branch
  dbd9b65 against 2958379): EPYC 0.32-0.46 and Neoverse-N2 0.18 on scattered-1m, but Xeon Platinum 8573C 1.29 with
  a base of 57 ms (cache-resident speed: the gate did not turn it off, so the VM's sysfs size or the per-string
  estimate is wrong there; log what sysfs reports on each runner before trying again), and
  `Serialize/simple-class-100` 0.8 % slower on Neoverse-N2 (n=10, floor 0.998-1.003) for the one compare.
  Rust, landed (869408d): `NinoWrite::PREFETCH_BYTES` (bytes read through pointers, summed by the derive and nino-gen) is a
  const, so `write_many` of every other type folds the test away; a slice of 512 or more whose estimated data
  outgrows the last-level cache (sysfs, 32 MiB default) takes the prefetching loop, the size test `#[cold]` out of
  line. 1,000,000 shuffled records took 67-78 % less time on EPYC 7763, 9V45, 9V74 and Neoverse-N2, 39 % on Xeon
  8370C, on par on Xeon 8573C (its cache holds the set). Gate on the cache, not a fixed budget: prefetching every
  list over 1 MiB (b93fe69) had the same gains but moved cached records-1000 1.5-3 % on EPYC and Neoverse-N2; gated,
  records-1000 stayed within a null change's spread measured in the same run (`benchmark`). Putting the eligible
  loop out of line at a 64-byte boundary (8dc3b04) did not help: +2.7 % on 7763 and N2.
