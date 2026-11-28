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
build`, which fails (round 105). A dump that aborts with glibc's `free(): invalid pointer`,
`printf_buffer_to_file.c:86 ... assertion failed` or `invalid stdio handle` is the process exiting while the JIT
still prints a listing, not Nino: the runtime's shutdown closes (frees) the `DOTNET_JitStdOutFile` stream under the
background Tier1 compiles still writing to it. Make any program that exits right after warming methods under
`DOTNET_JitDisasm` wait first until `JitInfo.GetCompiledMethodCount()` stands still, as `jitwarm` does, and catch
such an abort with a kernel core (`ulimit -c unlimited`), not `DOTNET_DbgEnableMiniDump`, under which it never
showed (#413: a program without Nino that called `Environment.Exit` while the JIT printed aborted in 27 of 1,081
runs, once with `free(): invalid pointer`, under `libc_malloc_debug` with `invalid stdio handle`, and in 0 of 550
with createdump on; 0 of 400 with the wait. Its core: two JIT threads inside `vfprintf` on the one stream at once,
the main thread in `exit`'s handlers. Nino.Benchmark's JIT compiled another 902 methods in the 1.7 s after the
hook's last round, the window its `Environment.Exit` fell in; the review of the unsafe cursor, entry and flat-kernel
writes found none that reaches native memory).

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
  element, and a set's or a dictionary's entry is the element of its run: a storage element (`NinoSetEntryFormatter`,
  `NinoMapEntryFormatter`, `Internal/Collections/Storage/NinoStorageElement.cs`), the struct of a collection's storage whose wire bytes are one
  or two items at offsets measured on this runtime, the rest of its stride (hash code, link) skipped, composed as
  tuples are (block map of the items at their offsets, cursor form, run loop, dispatch bit; the cursor form, run loop
  of every composite once, `NinoItems`, from the items' offsets; recycling per item, `NinoRecycler`), so the block kernels pack
  and scatter a run of entries with no code of their own; `KeyValuePairFormatter` composes a pair's items the same
  way; only a collection's thin top layer (reach the storage, measure its fields at startup, add, merge, link:
  `HashSetFormatter`, `NinoDictionary`, `SetTable`/`MapTable` under the one read `NinoEntries`) knows the collection.
  Why: steps 3-5 carried the key down as its own shape (`NinoRun.Key`, loops over `<TKey, TValue, TKeyFormatter,
  TValueFormatter>`, `NinoCursor.Pair` duplicating `KeyValuePair`), so only `Dictionary` got that loop; then the
  entries were read as `KeyValuePair` where a runtime laid them out so (`PairLayout.InEntries`, a startup branch) and
  composed otherwise, two read branches and a set and a map formatter each (`claude/entry-elements` removed both). Read the
  entry's fields as fields in the entry's own formatter, never through a fields struct type parameter (the generic
  `NinoStorageFormatter<TElement, ..., TFields>` with `INinoStorageFields` and the set's and dictionary's fields
  structs went for two concrete formatters, and `NinoDictionary.Entries`, which duplicated the map one for entries
  holding a class, with them: asm.py found no method worse, #355 step 6). A `Queue`'s ring is the run's two-segment
  write (`NinoRuns.Write(ref first, firstCount, ref second, secondCount, ...)`: one reservation for a block, else the
  count and the one write twice), not a storage element; pass the segments as `ref T` and a count, never as spans:
  slicing checked spans added `ThrowArgumentOutOfRange` paths and spilled the write (`Queue<int>` serialize 5 to 13
  stack accesses, 25 to 33 for a queue of a block struct). In code shared over a class, call no method of a struct type parameter instantiated over a class
  per element: each is an indirect call the JIT never inlines (a struct's method needs the exact type's context), and
  a static of the generic struct a helper call; reach an entry's fields from the concrete entry type in the top layer.
  Make every run of items from a run with `NinoRun.Items(offset, key)` (the one way: a set's values, a dictionary's or
  a pair's values with their keys; it replaced `NinoRun.Values`, `NinoStorage.Items` and `CollectionFields.Values`).
  A set of values with references moves its values at the entries' stride (`HashSet<string>` write 932 to 843 bytes); a set or dictionary whose items hold a string goes
  to the string run kernels (`NinoStringRuns`, below), which replaced #426's three workarounds for it: an entry
  formatter of its own over `NinoCursor`'s pair overloads (the storage element's fields-struct accessors had cost two
  indirect calls per pair in each of `MaxSize` and `WriteAt`, `Dictionary<string, string>` written 1.9 times as long,
  local, and offsets in its statics a `CORINFO_HELP_GET_NONGCSTATIC_BASE` call each, still 1.2 times), the hash table
  as the cursor loop's store (`INinoHashTable.Adding`, as a store wrapping it made two indirect calls per entry) and an
  inline add loop for a set whose values neither insert nor walk (`HashTables.Plan`: strings; through `NinoEntries`
  1.2 to 1.3 times as long, local); that last one lives on in the kernels as `NinoStringRuns.Add<TItem>` (below). Add through one store per
  collection where the JIT allows it: a set's or a dictionary's entries read value by value and inserted (the gather's
  read loops and `NinoEntries.AddBlock`) go through `NinoTableAdds` (an `INinoGatherTarget` over the collection's
  `INinoHashTable`, its `Add` a static of the concrete table, inlined), a `ConcurrentDictionary`'s through an
  `INinoAdder` of `NinoGather.Adds`; the string kernels keep their own inline adds (`SetAdds`, `KeyAdds`, ...: a store
  generic over the table is an indirect call per entry in code shared over a class, and the set's measured 1.1 times
  as long through one). The dictionary's
  out-of-line write computes the key and value offsets from `MapEntry` and calls the by-value loops
  (`NinoRuns.WriteEach`, `NinoItems.Write`, writer by value and returned): the shared `NinoRuns.WriteEntries`
  inlined its loop on that method's writer parameter, which then lived in memory (`Dictionary<string, int>` WriteEntries
  36 to 54 stack accesses; by value 36 to 30). Compute an in-use test from the entry's own fields' distances
  (`CollectionFields.InUse`), never from a static of a generic class: one not initialized when Tier1 compiles the caller
  is a helper call that spilled the set's write (`HashSet<int>` serialize 958 to 1,120 bytes, 40 to 57 stack accesses).
  Decide the string run kernels' path in the caller from the item
  formatters (`NinoCursor.Own<T, TFormatter>()`, `CursorPairs`), never inside the shared loop from the element
  formatter (`NinoCursor.Own<TEntry, ...>`: `HashSet<int>` 1,046 bytes and 66 stack accesses, `HashSet<int?>` 1,234
  and 86; reverted). In the read, a shared entry formatter's block test costs nothing more: `NinoKind.Of` is behind
  the no-references guard; the `Dictionary<int, Base>` read shim grew 92 to 167 bytes (a reader copied into the
  out-of-line `NinoEntries.ReadEach`, plus the method's generic lookup) with one copy of the reader instead of two.
  The entries' write (`NinoDictionary.WriteEntries`: the run, then the loop) is out of line with the writer by value,
  one call per dictionary: inlined, its tests (`NinoItems.RunLoop`, the cursor pair test, the run) spent the budget of a
  generated body holding two dictionaries, which then called it with the writer by ref and compiled both loops of the
  other (asm.py: a `Lookup`'s top-level write 107 to 189 stack accesses, 149 out of line, in 27 % less code). Entries
  without references (raw or block items) stay inline; with them smaller, a `Lookup`'s whole top-level write is
  inlined into both loops of `NinoRuns.Write` (1,231 to 2,010 bytes, 94 to 103 stack accesses, no call per element;
  the case 138 to 112). Compose recycling as the cursor form is: a collection that drops elements recycles them with
  `NinoRecycler.MayRecycle<T, TFormatter>` / `Recycle` / `DiscardAll<T, TFormatter>`, and a composite's items through
  its formatter's internal `RecyclesItems` / `ItemsMayRecycle` / `RecycleItems` (pairs, value tuples, entries), never a
  per-collection pair walk (`DiscardPairs` went). Keep the walk over dropped elements and a composite's may-recycle test
  out of line (`NoInlining`): inlined, `List<(int, long)>` deserialize grew 1,196 to 1,678 bytes. A composite object
  (a `Tuple`) recycles its items through the same members (`NinoRecycler.Recycle`, `Replace` and `Discard` of `<T,
  TFormatter>`: its items where no handler takes the tuple and no pool takes it back), but a collection asks it only as
  itself (`NinoRecycler<T>.MayRecycle`, true for a non-sealed class once a pool exists): asking its items too, even out
  of line, compiled a call into the read of every list of classes (asm.py: 30 `ListFormatter<__Canon, ...>.Deserialize`
  and generated bodies, storage-typed), whose code is shared. Compose every composite's cursor form in `NinoItems`
  (a pair, a value tuple of two to seven, a dictionary's entry) into the one chain of items every composite is
  (`NinoItemAt<TMember, TItem, TNext>`, a generated flat class's too; #355 step A): each item's bottom item picked
  outside the loop, at its offset carried as data (`NinoOffset`, an `int` widened where it is added): an accessor
  generic over the composite would make the kernel code shared over a class item, where each call of it is indirect
  (the rule below). End a chain at its last item (`NinoItemAt<TMember, TItem>`), never at an empty end node: its byte
  and padding made a pair's chain (two `nint` offsets) 24 bytes passed through the stack, then (two `int`s) 12 bytes
  built in memory (asm.py: a `Dictionary<string, string>`'s `WriteEntries` 38 to 45 and 44 stack accesses, its
  `ReadStrings` 14 to 23); two `int`s alone are one register, and the listings match the base's. A tuple of three or
  more is picked through a plan of its items declared last item first (`NinoItemPlan<T, TFormatter, TNext>` ...
  `NinoItemPlanEnd`, `WriteLast` starting the chain, each node putting its item in front of the chain it is given)
  through a generic interface method (`INinoItemPlan.Write<TTail>`, a constrained call on the exact plan), one lookup
  per node and run, none per element; write each node's pick of its bottom item out in the node's own method, never in
  a static helper generic over the item: in a node over a class item that call takes a lookup and is never inlined
  (asm.py, `List<(string, int, string)>` storage-typed: the first node 225 to 371 bytes, a second call per node and
  run), and keep the node's methods generic over one type parameter (a store as a second one cost an instantiating
  stub per node over a class item). A pair keeps a static pick of both items (`NinoItems.Write`, `Read`), the one
  pair-specific piece: code shared over a class item (a `KeyValuePair<string, int>`'s, a `Dictionary<string,
  string>`'s) inlines it whole down to the kernel call, where entering the plan would add a call per node and run.
  Beside a string, a slot's value (a raw `T?`) is an item in its single value's
  bytes (`NinoSlotItem` over two internal non-generic members of `INinoFormatter<T>`, `WriteAt`/`ReadAt`, implemented by
  `UnmanagedNullableFormatter`): `NinoCursor.Item` is the one predicate of what may sit beside a string. A raw run Nino
  checks (bools, Untrusted mode) is checked in the data out of line and then copied as one block in `NinoRuns.Read`
  (`CheckRaw`), never sent to the per-value loop: nothing the check rejects is stored, so a rejected read leaves an
  existing array as it was. Write every collection that is not one run through the gather seam, never element by
  element (#355, `CompilerServices`, hidden): a package outside Nino.Core (Nino.Immutable) says only how its
  collection is walked, an `INinoGatherSource<T>` struct handed to `NinoGathered.Write` (or `WriteReversed` for a
  stack walked from its top), and Nino.Core's run loops write each chunk (raw values as one block or straight into the
  writer's buffer, slots, strings, a polymorphic base's run loop); `NinoElement.Write`, the per-element write it
  replaced, went. Walk through the collection's fields where a startup check proves them, else its struct enumerator,
  never `IEnumerable<T>` (two interface calls per element: 1,000 ints of an `ImmutableQueue` written in 1.6 times the
  time, walked into a rented array first 1.2). Walk a tree recursively through `NinoTreeWalk`, the path saved on a
  stack of nodes only when a fill stops inside the tree and that stack taken then: an iterative walk that pushed every
  node into an object array (a write barrier per push) and wrote its state back per node, two nested walks per element
  for the hash trees, wrote 1,000 ints of an `ImmutableHashSet` in 1.8 times feat/storage-layer's time (its recursive
  copy into a rented array), `ImmutableSortedSet` and `ImmutableSortedDictionary` 1.6, `ImmutableDictionary` 1.5,
  `SortedSet` 1.2; recursive, against the same base: `SortedSet` 0.52, `SortedDictionary` 0.57,
  `ImmutableSortedDictionary` 0.75, `ImmutableSortedSet` 0.82, `ImmutableDictionary` 0.84, `ImmutableHashSet` 0.92
  (local quick runs, three per side in both orders, Xeon 2.10 GHz). What a node holds is the collection's
  `INinoTreeNodes<T>` struct (an element at an offset, a key and value made a pair, a hash bucket's first item that
  ends the fill when the bucket holds more). Walk a tree of class elements over a struct holding one reference
  (`NinoTreeWalk.Reference` and `ReferenceAt`; Nino.Immutable's `ItemReference` and `BucketReferences`, a node read as
  `HashTree<object>`'s), never over the class `T` itself: over a class the walk and its `INinoTreeNodes<T>` struct are
  code shared over every class, and each element's `Take` is a dictionary lookup and an indirect call (and a generic
  static such as a hash tree's `NoMore` a lookup per element: pass it in the struct, read once per fill, which is also
  the form IL2CPP and NativeAOT keep constant, #436; it costs a value item's hash-tree fill 8 stack accesses once per
  fill). Put the class walk in a method of its own (`FillReferences`) and keep `NinoTreeItems<T>.Fill`
  `AggressiveInlining`: with the class branch inline, folded away for a value element, the JIT judged the fill an
  "unprofitable inline" into `NinoGather.Write` (read from `MethodJitInliningFailed`, #434), a call per fill for
  `SortedSet<int>` and the immutable trees of values.
  `PolymorphicFooImmutableList` serialized in 0.74-0.77 of the base's time, 1,000 values of an `ImmutableHashSet` of a
  class 0.61-0.66 (local, alternated, #355 polymorphic step). A key and value pair holding a class
  (`ImmutableDictionary<int, Base>`) still takes them through shared code. Keep a struct enumerator in a local for a fill's loop, written back once:
  a field of the source reached through it is read and written in memory each step (`ImmutableStack` 0.75 of the base
  with the local). Walk linked nodes through a checked mirror where the enumerator's steps are calls
  (`ImmutableQueue`'s and `ImmutableStack`'s enumerators call `Pop` and `Peek` per element, another assembly's, not
  inlined: the queue 1.55 times the base's time through them, 0.81 through `StackNodes`, the stack 0.72). Keep each
  collection's write one out-of-line call with the writer by value, the walk's source built inside it with any
  enumerator fallback: built inline and passed by value, the source and the fallback's setup took every caller's
  registers (asm.py, a top-level `ImmutableSortedSet<int>` write 14 to 43 stack accesses, 33 out of line;
  `ImmutableHashSet<int>` 33 to 47, back under asm.py's threshold). Add no generic member to `INinoFormatter<T>`:
  Unity 2022.3's IL2CPP fails to build a player with a generic default member (#405's
  `WritePairs<TKey, TKeyFormatter>`). The view is one cursor, `NinoRun` (#355 PR 3: the first value, count, stride,
  the in-use test of free entries, whose rule
  each runtime proves at startup), and the loops are `NinoRuns`, a kernel per kind and one loop of values per direction:
  every contiguous collection builds a run and calls them, never a loop of its own. Reads take the same view (#355
  step 4): `NinoRuns.Read` fills a run of values in a row (arrays, a list's backing array before its count is
  published, spans, `Memory`, rings from 0, multi-dimensional arrays, lookups), `NinoEntries` fills the new
  entries of a set or a dictionary (each entry the element: raw items and blocks scattered by `ReadAll` through the
  storage element's block map, in Untrusted mode too where `ReadSize` keeps the checks; strings through the string run kernels; others value
  by value), and `HashTables.Link` links them. A polymorphic base's values go
  through its generated `WriteRun`, whose body is Nino.Core's pieces with the base's compare chain inside (a step
  carried into a Core loop lost: dispatch.md). A composite carries `NinoBodyTraits.Dispatches` where an item's
  formatter does (`NinoBlocks.Dispatches`: pairs, tuples, generated generic structs). Strings, UTF-8 strings and composites
  holding them are a bottom-layer kind with kernels of their own, one per direction (#355: `NinoStringRuns.Write`,
  `Read`), as raw values have the block kernels, never a string loop in a top layer: the top layers call them once per
  run under `NinoKind<TFormatter>.Strings` (`NinoRuns.WriteStrings`/`ReadStrings`, `HashSetFormatter`,
  `NinoDictionary`, `KeyValuePairFormatter`'s `WriteStrings`/`ReadStrings`). Their items (`INinoItem`, the one item interface over `ref byte item`:
  `NinoUtf16Item`, `NinoUtf8Item`, `NinoValueItem`, `NinoSlotItem`; `MaxSize` a long, `WriteAt`, `ReadAt` returning
  byte counts) are not generic over a class and a composite's element is the chain of them (`NinoItemAt` over
  `NinoOffset`), so the kernels are never shared code. Narrow the long bound to an `int` out of line where a driver
  grows the buffer (`NinoStringRuns.RoomFor`): inline it cost the drivers 8 stack accesses (asm.py). Keep their state in registers, five values at most across the string kernels' and
  the stores' calls (x64's callee-saved registers): the write takes only the free space (a reference and the bytes
  left), checks each element's longest form against it and returns where it stopped, its driver growing the buffer
  and going on; the read checks each item's lengths against the bytes left as it reads it (`ReadAt`, before it allocates)
  with a moving source, the bytes left, the elements left and the element; the run was sized by the data (a byte an
  element at least), so a rejected read has allocated in proportion to the bytes it read. Measured slower, so not
  retried blind: a first pass that measured every length of the run before reading any (`Measure`, out of line, as
  #427 first had it: a chain of dependent loads per string, and for UTF-8 a call per string; without it, local quick
  runs of 1,000 strings: `string[]` read 1.2 to 1.3 times faster, `Utf8MixedStrings` 1.2, `Dictionary<string,
  string>` 1.15, `HashSet<string>` 1.1; inlining the UTF-8 measure's one-byte counts alone only brought
  `Utf8MixedStrings` from 1.14 times the base's time to on par); a first pass that summed the run's longest forms to
  reserve them once (1,000 strings written 1.3 to 1.4 times as long, local quick runs); an offset in each string item (a register more in a run of strings alone:
  `string[]` written 1.2 times as long; a composite places its items, `NinoOffset`s); the measure loop inside
  the read's method and a store whose fields or add were in the loop (its position, count and element on the stack:
  `HashSet<string>` read 1.3 to 1.5 times as long). A store that hands each element on (`INinoRunStore.OneSlot`: a
  dictionary's adds into one entry) has its `Stored` out of line, called on its home on the stack, and its
  items are read fresh (`ReadAt`'s `fresh`, written even when null: the one entry still holds the previous string,
  a set read a null after a string as a duplicate). A set that guards its own buckets (strings on .NET) adds in a
  loop of its own over the item (`NinoStringRuns.Add<TItem>`: a local read fresh, then `HashSet<string>.Add`, which
  the JIT inlines there): through the store (`SetAdds`, its `Stored` an out-of-line call around the add) the 1,000
  strings of `StringSet` read 1.1 times as long as the base's inline loop, through `Add` on par (local quick runs). The dispatch to the kernels inline in `NinoRuns.Write`/`Read`,
  inlined into every collection, spent their callers' budgets (KeyValuePairs, NullableGridInts, BlockingCollection
  serialized with their writers in memory): keep it one out-of-line call there. Three traps cost iterations
  (`asm.py`, the listings in references/jit.md): a `static readonly` field of
  `NinoKind<TFormatter>` read in code shared over a class element (`__Canon`) is a constant only when the class was
  initialized before Tier1 compiled the method, and a path that returns before reading it (raw, block) leaves it a
  helper call with the loop compiled in (`HashSet<NinoNullable<int>>` 811 to 1,174 bytes), so gate it behind the kind
  first; in shared code `typeof(T) == typeof(string)` with `T` a class is no constant, `typeof(TFormatter)` is; an
  instance method of a generic struct called from shared code is not inlined (an indirect `call r8` per pair), a
  static generic method is. One write of a run of any stride and one loop per direction (`NinoRuns.Write`,
  `WriteLoop`, `ReadLoop`; #355 step 5) took these, each from `asm.py compare` against the base: pass a run in a row
  as constants (a `row` and `counted` flag, the size of `T`, `NoKey`, `AllInUse`), never read them from the
  `NinoRun`, whose five fields the JIT does not fold (every kind's path compiled into every array's write); the
  import folds only intrinsics (`IsReferenceOrContainsReferences<T>()`), constant arguments and initialized `static
  readonly` fields read directly, never the result of an inlined call (`NinoKind.Stride`, `NinoCursor.Own`,
  `WritesOnLocal`), so a branch behind such a call is imported and an address of the writer taken there exposes it;
  write the choice between two calls that return the writer, or two arguments of one, as `if`/`else` statements,
  never `?:` (a `KeyValuePair` array's top-level write 5 to 38 stack accesses for a path it never takes); in a
  loop, test each piece against the caller's constants before calling it (`if (key != NoKey) WriteKey(...)`,
  `if (IsReferenceOrContainsReferences<T>()) Serialize(...) else WriteOne(...)`): a call whose test folds inside it
  is still imported and charged to the root's budget (AppData `Events` ran out and called the writer's properties,
  19 to 86 stack accesses); guard a slot path with the intrinsic before the kind and pass the value with `in` (by
  value with a span over it, every `ManagedStruct` array write and every `ReadEach` gained 6 stack accesses); take
  the reader of an inlined loop by `ref` (by value, each caller copied its reader parameter in its prolog); and
  leave the by-value write of a row (`WriteEach`) to the JIT with its loop spelled out, as `WriteOnLocal` was: a
  `NoInlining` one left `FixedClasses`' writer in memory (8 to 30), one more level of inlining (`WriteLoop` inside
  it) spent `Events`' budget. Tried and dropped: a call to the full `NinoRuns.Read` from a set's fill (shared code
  read `NinoKind<Entries>.Cursor` as a lookup: `NinoEntries<MapEntry<int, __Canon>>` 450 to 922 bytes; it calls
  `ReadLoop`), `NinoCollections.WriteSpan` as `NinoRuns.Write(values, length)` (classes' array writes inlined every
  path, `List<ICommand>` 343 to 1,289 bytes; it keeps its block test and length). A collection's own fast paths are
  layers inside its one read and one write, never entry points beside them (#350: `SortedList`'s int-only read, its
  `TypeCode` split and its hand zip became `BclDictionaries.SplitInPlace` and `Zip` under the gather); keep that one
  read out of line with the reader by value once it holds every path (inlined into a top-level `SortedList<int, int>`
  read it doubled it, 598 to 1,166 bytes and 17 to 34 stack accesses), and gate a layer on what the kernel needs, never on one
  kind: "memory is the wire form" is `NinoGather.InPlace` (raw without padding, or a block that is its memory), and a
  `KeyValuePair` of raw items is a block, not raw (its formatter is no raw formatter), so the split first gated on
  `Raw` sent `SortedList<int, int>` through the gather's chunk and the comparer's order check: 1,000 pairs read 1.7
  times as long (local quick runs, 1,928 against 1,152 ns) until it took `InPlace` and no bool or Untrusted check. Its write takes the gather's in-place step on the caller's
  writer (through `NinoGather.Write`'s call, the writer's copies made 1,000 pairs 1.1 to 1.15 times as long to write),
  as four lines of its own: that step as a helper inlined into `NinoGather.Write` too kept every enumerated gather's
  source in memory (asm.py: 57 to 65 stack accesses by value, 60 to 65 by reference).
- **Reach a generated class's members from a kernel through one accessor struct per member, never through a static
  field or table** (#355, the generator seam; `FlatEmitter`, `NinoFlat<TLayout, TBody>`). A flat class (raw members,
  bools, strings) is one chain of items nested in type arguments, each item's member a `readonly struct` whose
  `ref byte Ref(ref byte item)` returns the field (`NinoMemberAccess.Bytes(ref NinoMemberAccess.As<T>(ref item).Field)`:
  the struct in place, or for a class a reference to the object) and whose `Size` is a literal, so the field is a
  constant offset once inlined. Hand a class's chain the object as `ref Unsafe.As<object, byte>(ref local)` (the element
  item's local, `NinoFlatSealedItem`): once inlined the JIT turns the indirections back into the local, and the flat
  kernels' listings matched `object`-based items' (asm.py, ScatteredTest: read 1,066/64 to 1,051/57 bytes/stack
  accesses, write loop 462/12 to 443/12). The members' empty accessor structs as fields of the nodes cost nothing. Give a member generated code cannot reach by reference (a property) a node of its own that runs the same bottom
  item over a local copy (`NinoPropertyAt`, #355 step B), never a second bottom item per kind. Gate a generic flat
  type's run hook on every type argument being a value type (`HasFlat => typeof(T).IsValueType`) and write its one value
  member by member: over a class argument the chain is code shared over it, where each accessor is a call (the rule on
  shared code above). Send a flat struct's runs through the public `HasFlat` hooks from the cursor branch
  (`NinoCursor.Own`, `NinoRuns.WriteStrings`/`ReadStrings`, the reader by value out of line), not through the internal
  `HasCursor` trio, whose `scoped` parameters generated code for Unity's C# 9 cannot implement. The prototype's v1 read each member's offset
  and size from generic statics (`static readonly`, #436): B's write loop was 3,708 bytes with 165 stack operands and
  wrote in 1.08 times the base's time; v2's accessor structs made it 1,056 bytes and 31 operands, writes 0.85-0.90
  (local, alternated). Read the run's startup check (`__Check`, block placement and Untrusted's `ReadsAsIs`) once per
  call in the hook and pick a chain there, never per element. Keep runs under `NinoFlatRuns.MinRun` (16) element by
  element: a run's fixed cost (about 30 ns: entry, the check, the loop's setup) made a 2-element array 2.2 times as
  long to write and WorkloadTest's `MediaContent` 1.64. One body's write never takes the block chain (a block's copy
  pays off in runs) and keeps the sealed class's `__WriteTop` twin: without it one value wrote in 1.11 times the
  base's time, with it 0.88. Read one body member by member (`BodyEmitter`), never through the chain: through it a
  body inlined into a polymorphic dispatch kept more on the stack (ChatMessage's `ReadBody` 14 to 33 stack accesses,
  UnionItem 8 to 13), and after three fixes to the chain UnionArray still read in 1.03-1.16 times the base's time;
  member by member the listings match the base's. Keep the flat branch out of the value-type read loop
  (`NinoRuns.ReadEach`): folded away for a struct, it still made the JIT stop inlining a union's `ReadBody` there
  (TaggedUnionArray, with no flat class at all, read in 1.06-1.09); classes go to `ReadClasses` on a constant `typeof`
  test, and both union reads came back to 1.00-1.03 (local, alternated). What did not move: reads of long runs stay on par (0.99-1.00 at 1,000 elements; the read is bound by
  the string allocations, which the member-by-member body pays too), while writes of 1,000 elements took 0.46
  (array) and 0.68 (list) for a class of four primitives and two strings. Six traps `asm.py compare` found in the
  full suite, each fixed: (1) an element that already held an object left the run, two out-of-line calls each:
  ScatteredTest's read into a million items 2.3x; read into it instead. (2) Reading new and existing elements with
  `fresh` a constant on each path inlined the body twice: 1,736 bytes, 87 stack accesses, still 1.6x; pass `fresh`
  as a value, one copy. (3) The string item compared an existing string inline (`NinoStrings.Read` with a current
  string): one body inlined into a polymorphic dispatch went 14 to 61 stack accesses; compare out of line
  (`Reader.ReadStringAt`, as `ReadString(current)` does). (4) Passing the reader by value, then by reference, to the
  run hook from `NinoRuns.Read`, inlined into every collection, kept it in memory even for short runs (8 to 27);
  put the branch in `ReadEach`, the out-of-line call those runs already make. On the write side the branch goes after the block, run-loop and string branches (read first, `NinoKind<TFormatter>.Flat` in code shared over classes is no constant: a string array's write 4 to 29 stack accesses, a polymorphic base's `WriteRun` 86 to 234) but before `WritesOnLocal`, which sealed flat classes take too (behind it the kernels went unused, the same bytes: ScatteredTest's write 1.33x); bytes cannot tell which path ran: `FlatTests.RunsGoThroughTheKernels` looks for the kernels' drivers on the stack (a pool behind a one-byte buffer records each growth; a cut run's exception), and failed with the branch moved back. (5) The chain inlined as one body into
  a union's or a base's dispatch took the caller's budget and left the accessors as calls (UnionArray write 1.08x);
  one body's write is always out of line, a small one's read inline (out of line it read 1.13x). (6) A string item
  kept the field's address and old value live across the string's allocation (UnionItem's inline read 7 to 29 stack
  accesses, UnionArray read 1.1-1.15x): store a string read into an empty field as read and take the field's
  address again after it (12). And the cursor write of a UTF-8 string (`Writer.WriteUtf8At`) counted a long
  string's bytes again after the item's `MaxSize` had (1.35x for 100,000 chars): pass the bound where both ends of
  the count take one header size.
- **Never call the runtime's `Utf8` transcoder for a short ASCII string right after Nino's own 256-bit code.** The
  ReadyToRun CoreLib's `Utf8.FromUtf16`/`ToUtf16` are SSE-encoded, and an inlined AVX2 string kernel before them
  leaves the upper vector state dirty, so each call paid an AVX-SSE transition until the caller tiered up (TC=0, and
  every first call): a class `{ string Name; [NinoUtf8] string Tag; }` wrote in 177 ns, with the members swapped
  25 ns, with `DOTNET_ReadyToRun=0` 28 ns. Strings of at most 32 chars take Nino's own ASCII path
  (`NinoStrings.NarrowShortAscii`: overlapping 128-bit loads narrowed; `IsShortAscii` then the Latin-1 widen), the
  rest the transcoder: a seven-member flat class 179 to 26 ns to write and 223 to 73 ns to read at TC=0. A
  word-at-a-time scalar loop there was slower than the transcoder at Tier1 (a 32-char read 1.36x, a write 1.21x). Suspect this whenever a TC=0 case is several times its Tier1
  time and the order of members changes it.
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
- **Never box** (the owner's rule): nothing on a read or write path, nor on a branch of one that FullOpts code compiles
  before the field guarding it is set, boxes a value type. Test a value of a type parameter for null with
  `NinoUnsafe.IsNull` (one load of the presence byte at the offset `NinoPresence<T>` found, in a static with no
  initializer: `Nullable<T>.Equals(object)` boxed nothing but stayed a call, `HashTables.Hash<int?>` 1 -> 6 stack
  accesses, and a `static readonly` offset needed a class-init helper where the class was not yet initialized,
  `HashTables.Link<int?>` 24 -> 59 stack accesses), never `== null`: on a `T?` the JIT
  removes that box only where the boxed value has no side effects, and .NET 11's JIT kept it in an inlined call (#449: `HashTables.Hash` boxed every non-null `int?` of a
  `HashSet<int?>` read, 46,152 B against 22,176 per 1,000 and 2.2x main, cc8f4132; the dictionary path of the same
  test still boxed in a `Dictionary<int?, int>` read, and `NinoRecycler.Discard` a `ManagedStruct?` replaced by null
  in `NinoRuns.ReadEach`, 61694e6b).
  .NET 8 and 10 folded every one of them, so measure on .NET 11 too (`AllocationTests` runs on net11.0; a program on
  .NET 10 saw none). Never use the formatter in a default interface member (`GetType()` there kept the struct boxed on
  every unfolded branch that reaches it), never pass a struct as `object` (`Unsafe.As<Array>(value)`) or an interface,
  and hand a constructor that takes an `IEnumerable<T>` a `RentedItems<T>`, never an `ArraySegment<T>`: the
  benchmark's listings went from 10 boxing methods to 0 at Tier1 and from 92 to 0 in FullOpts code (asm.py `dump
  --boxes`, which names the type of each inline box: a listing alone shows it as a `CORINFO_HELP_NEWSFAST` like any
  `new`). By design and listed apart (`BOXES_BY_DESIGN` in asm.py): a C# union's case (the language stores it as
  `object`), the startup checks' `object` keys, registration by reflection, the one `INinoRawArrayFormatter` object
  per type. Not Nino's: .NET 11.0's `Dictionary` boxes a `T?` key in every lookup and insert the JIT inlines into the
  caller (1,000 keys read into an existing `Dictionary<int?, int>`: 24,000 B; none on .NET 10), and a user struct
  without `IEquatable<T>` is boxed by the runtime's comparer as a set's or dictionary's key.
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
- Arm64 listings of any case on an x64 machine: `Nino.Benchmark.dll` run directly with `DOTNET_STARTUP_HOOKS` set to
  `asm/jitwarm`'s build, `NINO_JIT_FILTER` to the case, `DOTNET_AltJit='*'`, `DOTNET_AltJitName` the name
  `asm/cross-jit.sh` prints and `DOTNET_JitDisasm` the methods, stdout to a file, then `members/split-asm.py`; count
  `[fp, ...]`/`[sp, ...]` accesses other than the prolog's `stp`/`ldp`. The .NET 11 cross JIT heads its listings
  "arm64" in lowercase: split-asm.py matched "ARM64" and wrote every listing to the x64 file until #347's check fixed
  it. Arm64 has ten callee-saved registers (x19-x28) to x64's five: the string run kernels' read keeps its source,
  bytes left, count, element and stride in x20-x24 with one spill (the new string around the widen call), the UTF-16
  and pair writes none, where x64 spills more (#347, storage-347-350).
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
- A box: `CORINFO_HELP_BOX_NULLABLE` (a `T?`), or `CORINFO_HELP_NEWSFAST` of a value type's method table, which only
  `asm.py dump --boxes` can name; read the branch it sits on before calling it dead.
- The signs in any listing: `Reader:Skip`, `Reader:CopyTo` or `NinoUnsafe:Read` as calls (the budget or the local limit ran out: `asm.py --inline` names which, references/jit.md);
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
