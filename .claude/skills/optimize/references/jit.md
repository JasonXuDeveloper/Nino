# The JIT: inlining budget, registers, folding

Part of the `optimize` skill. The rules about what RyuJIT inlines, keeps in registers and folds, each with the
evidence that set it. Read it before changing a read or write path of Nino.Core, Nino.Immutable or the emitted code,
and when `asm.py compare` lists methods the change did not touch.

The general rules are here. The experiment logs behind them, one bullet per measured change with its numbers, are
split by the code they touch; read the one for the code you change:

- [jit-io.md](jit-io.md): `Reader` and `Writer` helpers, `NinoArrayBufferWriter` and its short copies, streams,
  `ReadOnlySequence` input and output (the multi-segment copy, `WriteSegments`, split headers).
- [jit-arrays.md](jit-arrays.md): raw arrays, `ArraySegment<T>`, `Memory<T>`, multidimensional arrays, and writing
  them straight into a caller's `IBufferWriter<byte>`.
- [jit-formatters.md](jit-formatters.md): `BitArray`, `BigInteger`, `StringBuilder`, the seeded string hash, caches.
- [jit-collections.md](jit-collections.md): dictionaries, hash sets, queues, stacks, `SortedList`, `PriorityQueue`,
  lookups, sorted sets, linked lists, the shared enumerable loop and the dictionary refill.

Most of those experiments were timed only on a local Apple M4 (Arm64); a bullet says "Arm64 only; x64 unmeasured"
where that is so, and its x64 sizes are listings, not throughput.

## Inlining budget

- A branch whose condition is a method call (`RawFormatter.Is`, `NinoBlockCollections.FixedSize`, ...) is compiled and
  inlined even for the types where it is dead, and uses up the budget there. Keep rare or large paths (vector loops,
  element by element loops, string allocation, recycling) out of line (`NoInlining`), with only the common path inline.
  Check that the outlined path is cold for each common input size, compare the caller and helper on Arm64 and x64,
  and measure singleton, short and vector-sized inputs separately: a smaller hot entry does not show that the outlined
  call is cheap. In the packed 32-bit `SortedList<uint, uint>` write probe, outlining the generic scalar fallback
  shrank the Arm64 Tier 1 SIMD entry from 1,268 to 100 bytes, but moved singleton writes across a call boundary and
  slowed them in all four local runs; keeping the two unaligned singleton stores in the entry took 128 bytes (Arm64
  only; x64 unmeasured).
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
  The same holds for a literal `bool` option on a large parser: keep the default parser's signature and IL unchanged
  and pass such knowledge through its caller's existing local instead, because the option changes inlining for every
  other caller. Adding a `compareShortLatin1` bool to `NinoStrings.Read` removed the repeated short miss comparison
  after folding, but changed fresh generated formatter inlining: `PooledSimpleClass.__ReadBodyFresh` stack accesses
  rose from 18 to 81, and its new-object caller from 43 to 107 in the FullOpts pooled comparison. The complete
  string-miss variants and validation are in [vectors-strings.md](vectors-strings.md) (the failed short Latin-1
  comparison carried through the caller's local, `6723d580`).

- Give an out-of-line kernel one out-of-line entry with its loops inlined into it, and keep that entry at Tier1:
  a size dispatch (`NinoUnsafe.CopyOver64`) that called the vector loop (`CopyVectors`) out of line made two calls per
  copy of 129 bytes to 32 KB, three through `CopyToNew`, where rc.7's inlined `Copy` made one: 100 12-byte structs
  serialized in 0.98-1.27 times rc.7's time (.NET A/B run 41413072278). The loops are now `AggressiveInlining` into
  `CopyOver64Inline`, which `CopyOver64` and `CopyToNew` both inline. Never mark such an entry `AggressiveOptimization`
  to keep the loops' layout when it reads `static readonly` fields (a helper that reads none, such as the linked-list
  ring helper in [jit-collections.md](jit-collections.md), may): fully optimized code folds no `static readonly` field (it tested `NinoUnsafe`'s class init
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
  gap is not closable in Nino's code. Check the Tier1 listing on x64 and with the Arm64 cross JIT (`benchmarks/asm/cross-jit.sh`): one
  method, a two-instruction prolog.
- Inlining a small out-of-line method that the JIT left as a call can be a wash: `NinoLookup.Add` inlined into the
  `ILookup` read (one call per grouping instead of two) measured 1.002 and 1.003 times main on EPYC 7763 and
  Neoverse-N2 (run 41455371503); not landed. Forcing `NinoDictionary.Refill` (the merge into an existing dictionary)
  inline, as the JIT chose for rc.7, grew every read into an existing dictionary (PooledSimpleClass `__ReadBody` 2,731
  to 3,514 bytes, 127 to 189 stack accesses) and made `NinoDeserializePooled` no faster in an in-process timing loop
  (`benchmark`); not landed.

## Registers

- Never pass `ref reader` or `ref writer` to a method that is not inlined, even in a dead branch, while the caller
  holds the cursor in registers: the address keeps the struct in memory for the whole method. (Where the caller
  already holds the cursor by reference, the address costs nothing more: compare caller and helper listings, as in
  the multidimensional heavy read, [vectors-multidim.md](vectors-multidim.md#heavy-reads).) Pass it by value and return it (`reader = ReadEach(values, reader)`).
  An instance method of `Reader` or `Writer` left out of line passes `this` the same way: `Reader.ReadString(current)`
  calls `ReadStringOver` with the reader's address whenever `current` may be non-null, so generated code reads a new
  instance's strings with `ReadString()` (`MemberModel.Fresh`): SimpleClass's `__ReadTop` went from 108 stack
  accesses to 10, TolerantClass's from 174 to 10. Look for `Reader:...:this` and `Writer:...:this` calls in a listing.
  The same rule keeps multi-segment input a copy: a `ReadOnlySequence<byte>` read across segments in place would need
  every bounds check to refill the reader out of line instead of throwing (the reader's address in every method that
  reads, or a refill inlined into every check), and regions, sections and deferrals address the data by absolute
  position. So the sequence overloads copy data that spans segments once, out of line (to the stack up to 1,024 bytes,
  else a pooled array: `NinoSequences`), and read one segment in place (Nino `d275a71e`). How that copy is sized,
  shared and looped, and the two-segment fast paths: [jit-io.md](jit-io.md), "ReadOnlySequence input and output".
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
  1.000 floor, run 41593418245; `CopyPairs` out of line made `NinoNodePooled` 1.033). How the refill
  copies its `Reader` and the refill probes that were withdrawn: [jit-collections.md](jit-collections.md),
  "Dictionary refill".
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
- Choose a formatter's path on .NET 8+ by `default(TFormatter) is IMarker` (a struct's box tested against an
  interface, folded while the call is imported), not by a `static readonly` field read through an inlined method
  (`RawFormatter.Is<T, TFormatter>()`), when the path not taken touches a struct's fields: the dead path is imported,
  and its field reads and writes decide struct promotion before the branch folds. Watch `Nullable<T>` most: a `T?`
  local promoted to its fields is joined again through the stack to be returned or copied, a store-forwarding stall
  (a load spanning a byte store and a dword store). Evidence: `NullableFormatter.Deserialize`'s non-raw path
  (`GetValueOrDefault`, `value = item`) kept `NullableTest.NinoDeserializeNullableStruct` (12-byte `SimpleStruct`,
  Pack 4) at 9.2 ns on a Xeon (Emerald Rapids) with the read itself in two registers; with the test folded at import,
  the case is two loads, a shift and `rax`/`rdx` (1.0 ns; MemoryPack 1.3). Any other field access of the `T?`
  promotes it too: a null read passes the value held to an out-of-line `Reader.Recycle` (which reads `HasValue`) only
  when `NinoRecycler<T?>.Handler` is set, copying it only there (copied before the test, every null `Guid?` field
  was loaded and stored to the stack), and the end-of-data path throws instead of calling the `Nullable` constructor.
  Keep `Reader.ReadNullableRaw` small: inlined into every nullable member, its first form (a `MayRecycle` test of
  three fields, a second null path) used up `ProfileV2Formatter.ReadBody`'s inlining budget, which then called
  `NinoUnsafe.Read` and the word constructor. Find such a cause by removing paths one at a time and dumping the case (a scratch program
  of the same shape stayed clean, so the cause was in Nino's code, not the JIT's handling of `T?` itself).
- Leave `NullableFormatter`'s path for a `T` that is not raw as it is (`TryReadPresent`, `T item =
  value.GetValueOrDefault()`, the body, `value = item`; the write: the flag, then `GetValueOrDefault()` to the
  body): once the body of `T` is inlined (`audit-generated-code`, references/write-paths.md, "The write body's
  methods") the copies are gone from the machine code. Evidence (x64 Tier1, `ManagedStruct?`, a7a4af24): the array
  read loads only the reference it may reuse (`mov r13, [r15+0x08]`) and stores the flag and the three fields into
  the slot, one write barrier an element; the top-level read builds the value in registers and stores it into the
  return buffer field by field, with no load of the whole (no stall). Reading in place through
  `Nullable.GetValueRefOrDefaultRef` would take the address of that local and keep it in memory, so it was not
  built. The cost was the out-of-line body, which took the reader's address: look there first when a `T?` of a
  managed struct is slow.
- A JIT-time check made of several helper calls is one `static readonly` field instead (`NinoElements.Traits`,
  `NinoBlockPlan<T, TFormatter>.Size`): one load, nothing to inline.
- Decide "copy memory" against "convert" by a `static readonly` computed before Tier1 (`NinoLayout<T>.IsCanonical`,
  `CopiesAsIs`, `HasPadding`, `Size`), so the common platform (64-bit CoreCLR) pays nothing and every other one adapts
  to it (the owner's rule: 32-bit adapts to 64-bit, never the reverse). Prove it with JitDisasm of a scratch program
  that calls the array, list and value paths in a loop until Tier1 (`DOTNET_JitDisasm='P:* Nino*:* NinoGen*:*'`): no
  `NinoLayout` field or call is left (`NinoLayout<T>.Size` compiles to `mov eax, 32`, `CopyToWire` to a tail jump
  into `NinoMask.Copy`), for `Vector<int>[]` and raw structs holding tuples alike (round of the Vector<T> layout).

## Locals, listing size and arithmetic

- Scope a captured local inside the branch that needs its closure. Why: the then netstandard2.0 `Lazy<T>` formatter
  (`a792e149`) previously allocated before the presence check; moving the local into the present branch removed that
  null-path allocation on Arm64 and x64 listings. A late helper with a large generic local would move that value onto
  the stack, and changing the Lazy publication mode lost present throughput.
- Pass a present nullable's raw value to a readonly-ref layout writer through `Nullable.GetValueRefOrDefaultRef` on
  .NET 8 and later (`546d8265`). Why: taking a value copy first zeroed and copied a stack temporary in the local Arm64
  listing. Evidence: the 128-byte probe's frame went from 336 to 208 bytes.
- Keep the small raw fallback's typed local in a method separate from the large raw entry, with a single final load
  within each path, and compare trusted packed and wide reads as well as untrusted allocations. Why: a size check in
  the same method does not stop Tier 0 from reserving a stack slot for a large `T` on an untaken branch.
- Check the frozen untrusted setting before the layout branch and return directly from the existing validated reader
  in that mode; keep the byref layout fallback for trusted types that cannot be loaded directly. Why: combining both
  fallbacks into a typed return added a full stack slot and a second copy to the trusted Arm64 caller, and a result
  local with a single return kept both copies. Evidence: testing the setting first restored the 156-byte trusted
  caller; untrusted reads of 1 and 4 KiB saved 1,048 and 4,120 allocated bytes at offsets 1 and 7 and beat the
  identical-binary control in all four local rounds at each offset; the x64 offline entry retained both settings
  branches and tail-jumped into the validated reader with no large stack slot. Arm64 only; x64 unmeasured.
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
