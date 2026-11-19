---
name: optimize
description: "Nino's .NET and Unity optimization techniques, measured and kept, and how to read the machine code - the JIT's inlining budget, keeping the reader and writer in registers, direct calls instead of virtual dispatch, bounds checks, vector loops on x64 AVX-512/AVX2/SSSE3 and Arm64 tbl/ldp/stp, Unity's Burst kernels and IL2CPP's generated C++, the prefetch design of every runtime, asm.py and JitDisasm; Go, Rust and TypeScript are optimize-runtimes. Use when writing or reviewing a .NET hot path, before committing a change to a read or write path, when a .NET or Unity case got slower than main (compare the listings), or when a new trick is found (record it here). Triggers: optimize, hot path, inlining, AggressiveInlining, NoInlining, bounds check, RNGCHKFAIL, Unsafe.Add, GetArrayDataReference, Vector128/256/512, AVX-512, Avx2, AdvSimd, Ice Lake, vpermb, tbl, shuffle, permute, spills, asm.py, JitDisasm, Tier1, listing, virtual dispatch, megamorphic, prefetch, Burst kernel, IL2CPP C++, 变慢, 回归, 虚调用, 优化技巧, 性能优化, 边界检查, 机器码, 汇编, 内联, 向量化."
metadata:
  internal: true
---
# Optimization techniques

The tricks that made Nino fast, the ones that made it slower, and how to see which you have. This skill owns the
techniques for .NET and Unity and the prefetch design of every runtime; how to measure is `benchmark`, where the next
win is `audit-perf`, the platforms each technique must hold on are `platforms`, and the Go, Rust and TypeScript
techniques are `optimize-runtimes`.

## Before committing a read or write path

- **Isolate before trading a small case for a big one.** When a representative workload gets faster and a narrow
  small-input case slower, keep the small-input path as it was and move the change into the larger loop or its
  dispatch, then retest both; record a tradeoff that remains next to its evidence. Why: one inlining budget covers
  everything a top-level call reaches, so the slowdown usually sits in the caller, not in the loop's extra
  instructions (compare Tier0, Tier1 and the caller first).
- **Build a fast path for the oldest framework whose APIs it needs, and let a newer one swap only the instruction
  it adds** (the owner's rule). The kernel sits under `#if NET8_0_OR_GREATER`; inside it, a small helper takes the
  newer instruction under `#if NET10_0_OR_GREATER` with the net8.0 form beside it, one implementation either way.
  Why: net8.0 users get the gain, and dropping a framework later (net8.0 once net12.0 ships) is deleting the `#if`
  lines, not merging two kernels. Code that needs a newer API for the whole path stays under that framework's `#if`
  (`CollectionsMarshal.AsBytes(BitArray)`, `PriorityQueue`'s internals). Evidence: `NinoMask.CopyCleared` was
  .NET 10 only for GFNI's `vgf2p8mulb`, so net8.0 cleared a raw struct's absent `T?` values element by element
  (1,000 structs of an int, a long? and a double?: 3.4 µs); with `Multiply` (GFNI on .NET 10, else `vpsubb` and
  `vpandq`) the net8.0 build writes them in 1.25 µs, MemoryPack 1.23-1.27 (local, quick job, #301).
- **Diagnose a flat or failed first result before shelving a sound candidate.** When an experiment removes real
  work and the first A/B shows nothing, find where the cost moved (caller and loop listings: cursor register
  pressure, spills, frame size, helper inlining, tier promotion) and try a materially different variant; a build or
  test infrastructure failure is repaired and rerun, never counted as the candidate's result. Keep the candidate in a
  reproducible patch or experiment commit meanwhile, change one code-generation factor at a time (helper boundary,
  where the cursor is initialized, short-tail dispatch), compare the listings on Arm64 and x64, and remeasure short
  and representative inputs under default tiering. Inconclusive: record the evidence, keep the candidate for the
  next round (the `BlockWaypoint` example in [vectors-blocks.md](references/vectors-blocks.md#scalar-block-reads)).

A change to a read or write path (Nino.Core, Nino.Immutable, the code the generator emits) can slow cases it does not
touch: everything a top-level call reaches is inlined into one method, under one inlining budget. Before committing
one, run `python3 runtimes/csharp/benchmarks/asm/asm.py compare origin/main` (about two minutes, see runtimes/csharp/benchmarks/README.md) and explain or
fix every method it lists; on a branch whose `origin/main` has moved since it started, compare its base instead
(`git merge-base HEAD origin/main`), or the diff holds other commits' changes too (round 129). To prove the code unchanged, dump both sides with `DOTNET_TieredCompilation=0` (the
variable around `asm.py dump [--ref REF] --runs 1 --asm FILE`) and compare the listings per method: FullOpts code
has no profile, so it repeats exactly, while Tier1 listed 7 of 2,356 methods in code a change did not reach.
`--ref REF` builds REF's own benchmarks: when the two sides' cases differ (a new case), the interceptors' numbers
shift and the diff lists unrelated `SerializeNNN`; dump the base from a worktree holding the head's
`runtimes/csharp/benchmarks/Nino.Benchmark`. A listing of several runs or threads can interleave two methods: compare the methods a
case reaches, normalized (addresses, labels, `align [N bytes]` padding, Arm64 `movz`/`movk` address immediates).
For the Arm64 side run `Nino.Benchmark.dll` directly with the cross JIT (runtimes/csharp/benchmarks/README.md), `DOTNET_STARTUP_HOOKS`
set to `jitwarm`'s build and `NINO_JIT_FILTER` to the case: `DOTNET_AltJit` around `asm.py` reaches its `dotnet
build`, which fails (round 105).

## The techniques, by topic

Each line is the rule; the reference file holds it in full with why and the runner numbers that set it. Read the file
for the topic you touch before changing code there: most of these were learned by a slower commit, and the evidence
says which CPUs and cases moved.

- **One storage layer for every collection, reads and writes alike** (the owner's rule, #355; the steps are #345-#355).
  Every collection whose memory is contiguous, or that keeps it inside (arrays, `List<T>`, spans, `Memory<T>`,
  `ArraySegment`, `ImmutableArray`, `Queue` and `Stack` rings, `HashSet` and `Dictionary` entries, the
  `PriorityQueue` heap, `SortedList`'s arrays, multi-dimensional arrays), is read and written by one bottom layer: it
  describes its storage as a view (segments with start, count, stride, an in-use test, ring wrap), and each element
  kind has one loop over the view. What is not contiguous joins the same layer piece by piece:
  `ReadOnlySequence<T>` segment by segment (each is a span), `LinkedList<T>`, `SortedSet` and the immutable trees as
  runs of nodes gathered into a span or walked, whichever measures faster, and a bare `IEnumerable<T>` buffered into a
  span. The element kind is decided once per type and formatter, in one place (a `static readonly` trait the JIT
  folds on .NET 8+ and reads once on netstandard), never again per collection: unmanaged/raw: one block with the SIMD
  kernels; `T?` or `NinoNullable<T>` of raw `T`: the slot kernels; a block formatter: the block path; a class:
  polymorphic dispatch inline and prefetch under `NinoPrefetch`'s gate; else the general formatter. Everything is
  devirtualized on .NET and netstandard (IL2CPP, Mono): capabilities through constrained calls on the formatter's
  struct type parameter, never an interface cast or a virtual call, and the dispatch of a polymorphic base in its
  fastest measured form with no indirect call per element. Every loop aligns its stores (or loads where that measures
  better) to the vector width or cache line, not only the SIMD ones. Gathering into a span goes through `NinoGather`
  (step 6, #355): a chunk of Nino's own per-thread buffer (16 KB, not tuned yet; never `ArrayPool.Shared`, whose
  large rents stay in its buckets), each chunk written or read by the same `NinoRuns` loop, a known count of values
  whose memory is their wire form walked straight into the writer's buffer. Keep a few chunks per element type, not
  one: a gather nested in a gather of the same type (a tree of nodes holding linked lists of nodes) found the one taken
  and allocated a whole chunk per inner collection. A chunk is a `T[]` per element type, not one byte buffer shared by
  every unmanaged type as first planned, because `ICollection<T>.CopyTo` copies only into a `T[]`, and a
  `ReadOnlyCollection<int>` of 1,000 copied in one `Array.Copy` where an enumerator makes two interface calls per
  element. Never wrap a gather in a try/finally to give its chunk back: a write or read that throws leaves its chunk to
  the collector, so a chunk half filled never returns to the buffer. Keep each gather one out-of-line call in the
  collection's write or read, the writer or reader by value (`NinoGather.Write`, `ReadAll`), and `Take`/`Return` calls
  too: inlined, the walk's setup, the thread-static lookups and a stateful target (a linked list's nodes) took the
  caller's registers (asm.py: `LinkedList<int>`'s read 29 to 62 stack accesses, a `ReadOnlyCollection<int>`'s write 35
  to 76). A copy of the writer around such a call (`CopyTo` in and out) did not help there. A new collection is
  built the fastest way its runtime allows, through its internals where `runtime-internals` proves the layout:
  `Dictionary` fills its entries and links the buckets in one pass after (#390, LuminPack's form), keeping the
  collision count and the repeated- and null-key checks; `SortedSet` builds its tree in one pass; look for the same
  in every other collection a read creates. Why: one loop per element kind is one place to make fast and to keep safe,
  instead of the same decision copied into each collection (the audit behind #345-#355 found dozens). DRY first:
  optimize the shared path, so every type on it gains, then give a type's own faster way (the fresh-build tricks above)
  an extra layer over it that falls back to it, never a branch in the shared loop or a copy of it (`audit-perf`,
  Method). The series lands on the integration branch `feat/storage-layer`, not on `main` (the owner's rule, #346
  step 2): each step is its own pull request into it, merged when CI is green, with `asm.py compare` run locally to
  catch a gross mistake but no runner A/B and no tuning of code a later step replaces; once the layer and its
  per-type layers are in, one pull request takes the branch to `main`, with the full runner A/B against the `main`
  the series started from, and the optimization happens there. Why: `main` stays releasable at its last stable
  state, the A/B budget is spent once on the whole instead of on intermediate shapes, and work is not spent polishing
  code that is replaced. A fix a step needs anyway counts when it is the shape the layer keeps. The once-per-type
  decision lives in `NinoKind` (`Internal/Collections/Traversal/NinoKind.cs`, #355 PR 1): every collection reads it,
  never `RawFormatter.Is`, a slot stride, a `typeof(StringFormatter)` or a loop query of its own. Key a trait on the
  formatter alone wherever the formatter decides it (`NinoKind<TFormatter>`: `Traits` with
  `NinoBodyTraits.Dispatches`, `IsString`, the loops, prefetch, the top-level writes), so it folds in code shared over a
  class element; key it on `<T, TFormatter>` only where the element decides it (`NinoKind<T, TFormatter>`: raw by
  `T`'s layout, slot, block by its memory), which happens only for values without references, whose code is never
  shared, and read it behind `IsReferenceOrContainsReferences<T>()` (`NinoKind.Of`, `NinoKind.Stride`). Raw is
  `RawFormatter`'s layout check (`NinoLayout` canonical on this runtime), never `unmanaged` or a `typeof` that assumes
  .NET's layout (`ValueTuple` and `KeyValuePair` are auto layout on CoreCLR; Mono, IL2CPP and Unity lay some types out
  otherwise): a type laid out otherwise is a block or per value. Compose a composite's kind from its items, never by a rule of
  its own (#348, #355 PR 2): a value tuple, a `KeyValuePair`, a generic `[NinoType]` struct (and the entry blocks of
  sets and dictionaries) read each item's size as `NinoBlocks.ItemSize` (raw by the layout check, else a block's
  `NinoBlockPlan.Size`, else -1) into `static readonly` fields, behind `IsReferenceOrContainsReferences` of the
  composite so shared code reads none; `FixedSize` is their sum or -1, `WriteFixed`/`ReadBlock` move each item at the
  summed offsets through `NinoBlocks.WriteItem`/`ReadItem`, `Serialize` reserves once when fixed, and `BlockMap` is
  `NinoBlockMap.Item` per item with this runtime's memory offsets (`Offset`/`Find`), so whether the block is its
  memory is `NinoBlockPlan.IsMemory` on each runtime, never assumed; a block whose own map is empty
  (`DateTimeOffset`: a read checks the range and widens the minutes) leaves the composite moved value by value. Why:
  `(int, DateTimeOffset)` and `KeyValuePair<int, BlockStruct>` were written element by element because each composite
  tested `RawFormatter.Is` per item and `DateTimeOffset` was a `typeof` in four places. Write every loop over
  `<T, TFormatter>` only, never over the parts of a composite (the owner's rule, #355 comment 6998107680): a pair is an
  element, and a set's or a dictionary's entry is the element of its run: a storage element (`NinoStorageFormatter`,
  `Internal/Collections/Storage/NinoStorageElement.cs`), the struct of a collection's storage whose wire bytes are one
  or two items at offsets measured on this runtime, the rest of its stride (hash code, link) skipped, composed as
  tuples are (block map of the items at their offsets, cursor form, run loop, dispatch bit), so the block kernels pack
  and scatter a run of entries with no code of their own; `KeyValuePairFormatter` composes a pair's items the same
  way; only a collection's thin top layer (reach the storage, measure its fields at startup, add, merge, link:
  `HashSetFormatter`, `NinoDictionary`, `SetTable`/`MapTable` under the one read `NinoEntries`) knows the collection.
  Why: steps 3-5 carried the key down as its own shape (`NinoRun.Key`, loops over `<TKey, TValue, TKeyFormatter,
  TValueFormatter>`, `NinoCursor.Pair` duplicating `KeyValuePair`), so only `Dictionary` got that loop; then the
  entries were read as `KeyValuePair` where a runtime laid them out so (`PairLayout.InEntries`, a startup branch) and
  composed otherwise, two read branches and a set and a map formatter each (`claude/entry-elements` removed both). The
  storage element is the extension point of every storage that is not a plain array of the element: a `SortedList`'s
  two arrays, a `Queue`'s ring in two segments, the chunks of nodes `NinoGather` walks (one element and fields struct
  each, no new loop). In code shared over a class, reach an entry's fields from the concrete entry type in the top
  layer, never through the element's fields struct: an instance method of a generic struct over `__Canon` is not
  inlined there (asm.py: `CollectionFields.Run<__Canon>` 247 to 378 bytes, `HashSet<string>` write 932 to 1,081 with
  an indirect call per value in the cursor loop); a set of values with references moves its values at the entries'
  stride instead (`CollectionFields.Values`, `NinoStorage.Items`: `HashSet<string>` write 932 to 843, read 2,322 to
  670 bytes and 96 to 9 stack accesses), and the dictionary's out-of-line write computes the key and value offsets
  from `MapEntry` and calls the by-value loops (`NinoRuns.WriteEach`/`WriteCursors`, writer by value and returned):
  the shared `NinoRuns.WriteEntries` inlined its loop on that method's writer parameter, which then lived in memory
  (`Dictionary<string, int>` WriteEntries 36 to 54 stack accesses; by value 36 to 30). Compute an in-use test from the
  entry's own fields' distances (`CollectionFields.InUse`), never from a static of a generic class: one not
  initialized when Tier1 compiles the caller is a helper call that spilled the set's write (`HashSet<int>` serialize
  958 to 1,120 bytes, 40 to 57 stack accesses). Decide the cursor loop in the caller from the item formatters
  (`NinoCursor.Own<T, TFormatter>()`, `CursorPairs`), never inside the shared loop from the element formatter
  (`NinoCursor.Own<TEntry, ...>`: `HashSet<int>` 1,046 bytes and 66 stack accesses, `HashSet<int?>` 1,234 and 86;
  reverted). In the read, a shared entry formatter's block test costs nothing more: `NinoKind.Of` is behind the
  no-references guard; the `Dictionary<int, Base>` read shim grew 92 to 167 bytes (a reader copied into the
  out-of-line `NinoEntries.ReadEach`, plus the method's generic lookup) with one copy of the reader instead of two.
  The entries' write (`NinoDictionary.WriteEntries`: the run, then the loop) is out of line with the writer by value,
  one call per dictionary: inlined, its tests (`PairRuns`, the cursor pair test, the run) spent the budget of a
  generated body holding two dictionaries, which then called it with the writer by ref and compiled both loops of the
  other (asm.py: a `Lookup`'s top-level write 107 to 189 stack accesses, 149 out of line, in 27 % less code). Entries
  without references (raw or block items) stay inline; with them smaller, a `Lookup`'s whole top-level write is
  inlined into both loops of `NinoRuns.Write` (1,231 to 2,010 bytes, 94 to 103 stack accesses, no call per element;
  the case 138 to 112). Add no generic member to `INinoFormatter<T>`: Unity
  2022.3's IL2CPP fails to build a player with a generic default member (#405's `WritePairs<TKey, TKeyFormatter>`). The
  view is one cursor, `NinoRun` (#355 PR 3: the first value, count, stride, the in-use test of free entries, whose rule
  each runtime proves at startup), and the loops are `NinoRuns`, one per kind:
  every contiguous collection builds a run and calls them, never a loop of its own. Reads take the same view (#355
  step 4): `NinoRuns.Read` fills a run of values in a row (arrays, a list's backing array before its count is
  published, spans, `Memory`, rings from 0, multi-dimensional arrays, lookups), `NinoEntries` fills the new
  entries of a set or a dictionary (each entry the element: raw items and blocks scattered by `ReadAll` through the
  storage element's block map, in Untrusted mode too where `ReadSize` keeps the checks; the cursor loop; others value
  by value), and `HashTables.Link` links them. A polymorphic base's values go
  through its generated `WriteRun`, whose body is Nino.Core's pieces with the base's compare chain inside (a step
  carried into a Core loop lost: dispatch.md). A composite carries `NinoBodyTraits.Dispatches` where an item's
  formatter does (`NinoBlocks.Dispatches`: pairs, tuples, generated generic structs). Strings, UTF-8 strings and composites
  holding them take one cursor loop per direction (#347, #355 step 5: `NinoRuns.WriteCursors`/`ReadCursors` over each
  element's cursor form, `NinoCursor`: `MaxSize`, `WriteAt`, `ReadAt` returning byte counts; a pair's form is
  `KeyValuePairFormatter`'s, composed from its items), never a string loop of its own. Keep that loop's state in registers: give it only the free space
  (a reference and the bytes left) or the data and its end, return what it took, and let the caller commit, grow and
  go on; the stores it fills (`Entries`, `Adds`, `Merges`) are plain structs it dispatches on by `typeof`. Three
  traps cost iterations there (`asm.py`, the listings in references/jit.md): a `static readonly` field of
  `NinoKind<TFormatter>` read in code shared over a class element (`__Canon`) is a constant only when the class was
  initialized before Tier1 compiled the method, and a path that returns before reading it (raw, block) leaves it a
  helper call with the loop compiled in (`HashSet<NinoNullable<int>>` 811 to 1,174 bytes), so gate it behind the kind
  first; in shared code `typeof(T) == typeof(string)` with `T` a class is no constant, `typeof(TFormatter)` is; an
  instance method of a generic struct called from shared code is not inlined (an indirect `call r8` per pair), a
  static generic method is.
- **CPU policy in one place.** A choice of path tuned for a CPU family (Arm64, an x64 vendor or generation) reads
  `NinoSimd`'s properties (`HasArm64`, `Prefer512`), and a size band shared by several call sites is
  one named predicate with its constants and its evidence (`NinoUnsafe.IsArm64Short`); only an intrinsic call checks
  its own ISA's `IsSupported` (`AdvSimd.Arm64.IsSupported ? AdvSimd.Arm64.ZipLow(a, b) : Sse2.UnpackLow(a, b)`). Why:
  the codex/nino-audit series wrote the Arm64 short-copy band six times with four ranges and three gates, and spelled
  `!Sse2.IsSupported && AdvSimd.Arm64.IsSupported` by hand beside `HasArm64`.
- **Test a vector width with the BCL's `Vector128/256/512.IsHardwareAccelerated` in place, never through a wrapper
  property.** Why: an `AggressiveInlining` getter is still an inlinee charged to the caller's inlining budget, and the
  checks sit in helpers inlined into every case (copies, strings, masks, checks). Evidence: `9bd3c982` routed them
  through `NinoSimd.Has128/256/512`; on x64 `ScatteredTest.NinoSerializeInOrderShapes` then inlined
  `WriteRawWithId<Rect>` but called `Writer.Skip` and `RectFormatter.WriteRawBody` from it (two calls per element,
  `Skip` an `AggressiveInlining` method), 6-12% slower than main; with the checks in place again the listing called
  `WriteRawWithId` once as on main and measured 2.66/3.01 ms min/median against main's 2.77/3.05 (in-process loop,
  six alternated rounds, Emerald Rapids).
- **The JIT** ([references/jit.md](references/jit.md); the measured experiments by area in [jit-io.md](references/jit-io.md) (Reader, Writer, buffer writers, streams, sequences), [jit-arrays.md](references/jit-arrays.md) (raw arrays, spans, memory, external buffer writers), [jit-formatters.md](references/jit-formatters.md) (BitArray, BigInteger, StringBuilder, hashing, caches) and [jit-collections.md](references/jit-collections.md) (collections, dictionary refill)): keep rare or large paths out of line (`NoInlining`), platform
  branches in the out-of-line part, and decide options at generation time, never as a run-time branch on the default
  path (the root's budget is sized from its IL); avoid passing `ref reader` / `ref writer` to an out-of-line helper
  when it would force a register-resident cursor to spill. Check the caller and helper when the cursor is already
  held by reference, as in the multidimensional heavy read ([vectors-multidim.md](references/vectors-multidim.md#heavy-reads)). Mark every small
  helper `AggressiveInlining`; add nothing to a method whose inlinee sits at the edge of the budget; write a platform
  test as `A || B` of branch-free operands at
  each site; fold JIT-time checks into one `static readonly`; keep locals few; a smaller listing is not a faster one;
  fast modulo on x64 only; keep a hot field off other threads' cache lines with a padded struct field; code run once
  per type at startup calls nothing generic over the formatter.
- **Dispatch** ([references/dispatch.md](references/dispatch.md)): no virtual dispatch on a serialize or deserialize
  path where a direct form exists (the owner's rule); call a formatter known only at run time through a `static
  readonly` field read directly, never through `??`, and keep it never null; the inventory of the indirect calls that
  stay (another assembly's `[NinoType]`, shared generic code, the netstandard block loops, user-extensible points, one
  per top-level call in shared code) and why.
- **Bounds checks and vector loops** ([references/vectors.md](references/vectors.md), the index of the vector files):
  index through a ref to the first element and `Unsafe.Add` behind the one length check, and reuse a length proof
  instead of checking twice; dispatch 512/256/128/scalar from `NinoSimd`; bump `ref` cursors and count down (with the
  measured exceptions); take `Vector128` parts out of the loop on Arm64; consecutive `tbl` registers; `Unsafe.As` for
  `Vector<T>` in the netstandard builds. By concern: [checks](references/vectors-checks.md) of strided bytes, bools
  and value ranges with accumulators and subtract-and-OR; [nullable slots](references/vectors-nullable.md);
  [blocks](references/vectors-blocks.md): strided runtime entries through the block path, per-CPU gates by CPUID model
  (Ice Lake), Arm64 lane groups on the 16-byte grid, allocate uninitialized only when every byte is written, nothing
  added to `ArrayFormatter` / `ListFormatter.Deserialize`, research the microarchitecture before probing a gap on one
  CPU, and the Ice Lake attempts not to retry; [copies](references/vectors-copies.md): per-vendor long copies, the
  Arm64 65-128-byte head and tail, the masked copy; [strings](references/vectors-strings.md);
  [collections](references/vectors-collections.md) (`SortedList`, pair interleave, `PriorityQueue`);
  [multidimensional arrays](references/vectors-multidim.md).
- **Unity, Burst and IL2CPP** ([references/unity.md](references/unity.md)): a generic formatter takes its type
  parameters' formatters as type parameters; never cast `default(TFormatter)` to an interface per value; native
  kernels as function pointers in static fields, each `calli` in its own `NoInlining` helper, called only within their
  contract (`Permute`'s ranges never overlap), checked in Core; thresholds from a sweep;
  build only the mask a call needs; where the WebGL kernels pay; what an IL2CPP class read costs.
- **Prefetch, every runtime** ([references/prefetch.md](references/prefetch.md)): decided at generation time for
  references in contiguous containers, how each platform prefetches, and every attempt in .NET, Go and Rust with why it
  did or did not land (.NET as the opt-in `NinoPrefetchLargeCollections`, Rust gated on the last-level cache, Go not
  landed).

## Recording a technique

A technique found and measured, faster or slower, goes into the reference file of its topic as one bullet, written as
AGENTS.md "Keep what you learn" says (rule, why, evidence: case, CPUs, runner numbers, commit or run). A new topic gets
a file and a line above. Go, Rust and TypeScript techniques go to `optimize-runtimes`.

## Reading the listings

- On an Arm64 Mac, compare x64 instructions through the matching cross JIT in a private .NET runtime copy.
  Use the listing to check instructions and code size. Use an actual x64 runner for throughput claims.
  A short-copy change (88315b4c) grew `ToArray` from 205 to 412 x64 bytes, or 478 with AVX2 and AVX-512
  disabled, while the x64 serialization caller's normalized listing stayed identical.


- x64: `--asm` of `runtimes/csharp/benchmarks/asm/asm.py`, once per ISA (`DOTNET_EnableAVX512=0`, `DOTNET_EnableAVX2=0`).
- NativeAOT: ILC compiles with the JIT, which takes the same options as `--codegenopt`: publish
  `runtimes/csharp/benchmarks/Nino.Benchmark -r linux-x64` with `PublishAot` set in a props file passed as
  `-p:CustomBeforeMicrosoftCommonProps=` (conditioned on the project name, or the netstandard references get it) and
  `<IlcArg Include="--codegenopt:JitDisasm=*" />`, `--codegenopt:JitStdOutFile=<file>` and `--parallelism:1` in one
  passed as `-p:CustomAfterMicrosoftCommonTargets=`, with `<TrimmerRootAssembly Include="Nino.Benchmark" />` (7
  minutes, 570 MB of listings for `*`; `IlcGenerateMapFile`/`IlcGenerateMstatFile` add the map). Direct calls show as
  `call Name(...)`; `call [rax+0x..]Type:Method` is virtual, `call [r11]Iface:Method` an interface dispatch cell,
  `test reg, 2` before `call [reg-0x02]` a fat pointer of shared generic code, and `lea rax, [(reloc ..)]` then
  `call rax` a helper. Walk the direct calls from each case to find what it reaches. Its `--verify` stops in
  MessagePack's setup (not AOT-safe), so time a scratch program instead.
- Arm64 from an x64 machine: `runtimes/csharp/benchmarks/asm/cross-jit.sh` installs the other architecture's JIT, and
  `DISASM=1 runtimes/csharp/benchmarks/members/run.sh` writes both listings for the member cases (that JIT only lists: with
  `DOTNET_AltJit='*'` around `asm.py`, the build and the benchmark died with SIGSEGV); or build for `linux-arm64` and
  run under `qemu-aarch64-static` with
  `DOTNET_TieredCompilation=0 DOTNET_JitDisasm='<method>' DOTNET_JitStdOutFile=<file>` (the code paths only a type
  outside the benchmark suite takes, such as `(byte, int)` for the 64-byte permutes, need a small program of their own).
  A clean loop is loads (`ldp`), lookups, stores (`stp`) and the cursor adds; `stp`/`ldp` to `[fp, ...]`, `mul` or
  `mov v..` in it are the signs above.
- The signs in any listing: `Reader:Skip`, `Reader:CopyTo` or `NinoUnsafe:Read` as calls (the budget ran out);
  `NinoReadContext:Return` or `NinoWriteContext:Return` calls and `[rbp-0x..]` loads and stores of the cursor (the
  reader or writer was not kept in registers).
- The block order of a Tier1 method varies between dumps of the same code (profile data): `asm.py` dumps of
  `--filter TupleList` showed `NinoBlockCollections.WriteEach<(int, long)>` with its permute loop moved, dumps of
  `--filter SerializeTupleList` the same listing on both sides. Before calling a listing changed, dump both sides
  again with the narrowest filter and compare the instructions with addresses and labels normalized.
- Timings on a shared machine vary by ±15 %: read the code before trusting a local difference, and take the decision
  from runners (`benchmark`). The wire format must not change unless that is the point: compare the bytes.

## Owned elsewhere

- `NinoSettings.Untrusted` tests stay a single `if` on a frozen field with the work out of line (`audit-security`).
- Paths through a runtime's internals: `runtime-internals`. Platform matrix and emulators: `platforms`; a vector
  path chosen by `X.IsSupported` uses X's own intrinsics under that test, or NativeAOT runs it in software
  (`platforms`).
- Go, Rust and TypeScript: `optimize-runtimes` (the techniques: inlining and GC, bounds checks, Rust's fixed-size
  blocks, strings, dispatch, Go's block checks); their rules: `audit-runtimes` and `runtimes/*/AGENTS.md`.
- Generated code's listings: `audit-generated-code`.
