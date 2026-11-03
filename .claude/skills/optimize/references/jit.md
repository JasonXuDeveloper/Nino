# The JIT: inlining budget, registers, folding

Part of the `optimize` skill. The rules about what RyuJIT inlines, keeps in registers and folds, each with the
evidence that set it. Read it before changing a read or write path of Nino.Core, Nino.Immutable or the emitted code,
and when `asm.py compare` lists methods the change did not touch.

## Inlining budget

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
  0.98-1.04 in the same jobs and the struct copy's addresses the same on both sides ('// ADDR' print); unchanged with
  `DOTNET_JitAlignLoops=0` (1.084, 1.069) and `DOTNET_TieredPGO=0` (1.061) in the quick jobs, and checking the
  64-byte path first in `CopyOver64Inline` was no faster locally on Emerald Rapids (.NET A/B runs 41621699299,
  41659495441, 41665524359, 41671303527). The gap was in main before round 123's copy changes (main 1.118 on 8573C,
  run 41413072278) and head against main is on par there. Check the Tier1 listing on x64 and with the Arm64 cross JIT (`benchmarks/cross-jit.sh`): one
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
- Mark every small helper a read or write path calls `AggressiveInlining`, even one whose arguments are constants
  and that looks certain to fold: in a large method the JIT's inliner gives up on it. `NinoLanes.Paired`, called
  with constants in the AVX2 group loop, stayed a call per load and spilled the vectors around it: AVX2 writes took
  twice as long as SSSE3's. Look for such calls in the Tier1 listing of every ISA a path takes (`--asm`, with
  `DOTNET_EnableAVX512=0` and `DOTNET_EnableAVX2=0` too).

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

- Past the JIT's tracking limit, locals are zeroed in the prolog: rare paths out of line also keep the local count
  down.
- A smaller listing is not a faster one: measure. Reading TimeZoneInfo's two strings through one out-of-line helper
  with two `out` strings cut the listing but put both on the stack: 7.5 to 12.8 % slower on runners. A constant
  parameter at each call site (`share: false`) let the inliner drop the dead branch per caller instead, with the
  listing as before and the string classes 17 to 21 % faster.
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
