---
name: optimize
description: "Nino's optimization techniques, measured and kept - what made read and write paths faster or slower in .NET (the JIT's inlining budget, keeping the reader and writer in registers, out-of-line rare paths, static readonly checks, bounds-check elimination with a ref to the first element and Unsafe.Add, vector loops on x64 AVX-512/AVX2/SSSE3 and Arm64 tbl/ldp/stp) and the same ideas in Go, Rust and TypeScript, plus reading the machine code (asm.py, JitDisasm, check_bce). Use when writing or reviewing a hot path in any runtime, before committing a change to a read or write path, when a case got slower, or when a new trick is found (record it here). Triggers: optimize, faster, hot path, inlining, AggressiveInlining, NoInlining, bounds check, RNGCHKFAIL, Unsafe.Add, GetReference, GetArrayDataReference, check_bce, panic_bounds_check, Vector128/256/512, Avx2, AdvSimd, SIMD, tbl, shuffle, permute, spills, asm.py, JitDisasm, Tier1, virtual dispatch, indirect call, func value, megamorphic, dyn Trait, 虚调用, 优化, 优化技巧, 性能优化, 边界检查, 机器码, 汇编, 内联, 向量化."
metadata:
  internal: true
---

# Optimization techniques

The tricks that made Nino fast, the ones that made it slower, and how to see which you have. This skill owns the
techniques; how to measure is `benchmark`, where the next win is `audit-perf`, and the
platforms each technique must hold on are `platforms`. A technique found and measured (faster or slower) is added
here, as a bullet: the rule, why, the evidence.

A change to a read or write path (Nino.Core, Nino.Immutable, the code the generator emits) can slow cases it does not
touch: everything a top-level call reaches is inlined into one method, under one inlining budget. Before committing
one, run `python3 benchmarks/asm.py compare origin/main` (about two minutes, see benchmarks/README.md) and explain or
fix every method it lists. To prove the code unchanged, dump both sides with `DOTNET_TieredCompilation=0` (the
variable around `asm.py dump [--ref REF] --runs 1 --asm FILE`) and compare the listings per method: FullOpts code
has no profile, so it repeats exactly, while Tier1 listed 7 of 2,356 methods in code a change did not reach.

## What has gone wrong before

- A branch whose condition is a method call (`RawFormatter.Is`, `NinoBlock.Size`, ...) is compiled and inlined even
  for the types where it is dead, and uses up the budget there. Keep rare or large paths (vector loops, element by
  element loops, string allocation, recycling) out of line (`NoInlining`), with only the common path inline. This
  holds for a test that folds away too: callers' budgets count an inlinee's whole IL, so a branch for one platform
  goes in the out-of-line part, never in the inline one. Evidence: `Is64Bit || IsX86 || UnalignedWide32` written in
  `NinoUnsafe.Copy` changed the x64 code of an ImmutableSortedDictionary write (`NinoBlock.WriteAll` inlined, `Copy`
  left a call) in an `asm.py` dump with `DOTNET_TieredCompilation=0`; moved into `CopyOver64`, all 990 methods were
  identical to main (`CopyBlock` keeps Arm64's branch out of `Copy` for the same reason). The root's own IL counts
  the other way: the JIT sizes a method's whole inlining budget from its IL size (estimate 60 + 3 per IL byte, times
  10), dead branches included, so a folded test in a root that is otherwise one call (an interceptor) raises the budget
  and changes what is inlined under it. Never add an option to the default path as a run-time branch, not even a
  `static readonly` one: decide it at generation time (an MSBuild property the generator reads, `NinoRegularCalls`,
  `NinoPrefetchLargeCollections`). Evidence: an interceptor `NinoPrefetched.Enabled ? SerializePrefetched(...) :
  Serialize(...)`, folded at Tier1, made PolymorphicClassArray's `Serialize(in PolymorphicClassBase[],
  IBufferWriter)` 1,060 bytes instead of main's 770 (single-block inlinees 142 to 155; `DOTNET_TieredPGO=0` dumps),
  while NonGenericBase's 1,112 methods stayed identical.
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
- Write a platform test that must fold as `A || B || ...` at each site, each operand a branch-free property (a
  constant or one `static readonly` field), never as one property whose body has `||`: the inliner gives a property
  with several returns a temporary, so the branch folds only after struct promotion was decided. Evidence: with
  `NinoUnsafe`'s 32-bit gate as one property (`Is64Bit || X86Base.IsSupported || WideAccess.Unaligned`), x64 read a
  24-byte struct with three `vmovsd` pairs instead of `vmovdqu` + `mov` and a 16-byte one with six `mov`s instead of
  two `vmovups`; written `Is64Bit || IsX86 || Unsafe.SizeOf<T>() <= 4 || UnalignedWide32` at each site, the x64
  listing was identical to main. A `|` of a constant and a field does not fold either: R2R x86 kept the field load
  and its class-init helper call.
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
- A JIT-time check made of several helper calls is one `static readonly` field instead (`NinoElements.Traits`,
  `NinoBlock<T, TFormatter>.Size`): one load, nothing to inline.
- Call a formatter known only at run time through a `static readonly` field read directly, never through
  `field ?? fallback` or a local that merges two sources: Tier1 knows the exact class of an initialized class's
  `static readonly` object and calls (and inlines) its overrides, but a merge loses it, and the JIT then loads the
  field, compares its method table and keeps a virtual call behind the compare. Keep such a field never null (a
  stand-in object whose calls report the error, `NinoMissingFormatter<T>`) instead of checking it. Evidence:
  `NinoTopFormatter<T>.Get` was `Formatter ?? NinoFormatterCache<T>.Instance`; every `CachedFormatter<T>` call (24
  sites in the benchmark, the `ProbeBox<T>` subtypes) was such a guard and virtual call, none after (6d711cb:
  GenericUnmanagedTupleBase deserializes in 0.86-0.94 of the time on EPYC 7763, 9V45, 9V74 and Neoverse-N2, floor
  0.99-1.01); a
  scratch program shows the same for any `static readonly` base-class field read with `??`. Find them with
  `call [rax+0x..]Nino...:Serialize` after a `cmp qword ptr [rdi], <method table>` in a Tier1 listing.
- No virtual dispatch on a serialize or deserialize path where a direct form exists (the owner's rule): formatters
  are structs passed as constrained type arguments, generated code names them directly, collections test their
  concrete types (`List<T>`, `T[]`) before their interfaces, and the one indirect call a polymorphic or
  user-extensible point needs (an unknown subtype through `NinoSubtypeHandler`, a recycler or `[NinoFactory]`
  delegate, a user collection's enumerator) sits behind the generated exact-type checks, out of line. What is left
  in the Tier1 listings and why is the inventory of the 6d711cb round: subtype handlers after the exact-type
  dispatch (cold), `NinoActivator<T>` delegates for user-derived collection types only (BCL types are created with
  `new`), BCL internals (`ArrayPool`, string comparers inside `Dictionary`), calls through a shared generic's
  dictionary (`T` a class: `CORINFO_HELP_RUNTIMEHANDLE_METHOD` then `call rax`), and the enumerator of a collection
  declared as an interface whose object is neither a list nor an array. IL2CPP, Mono and NativeAOT do not devirtualize
  `static readonly` fields: there `CachedFormatter<T>` and the top-level calls without interceptors (Unity's Roslyn
  4.3) stay one virtual call per value of that type.
- Do not inline a generic method that needs a runtime lookup into shared code on a hot path.
- Past the JIT's tracking limit, locals are zeroed in the prolog: rare paths out of line also keep the local count
  down.
- A smaller listing is not a faster one: measure. Reading TimeZoneInfo's two strings through one out-of-line helper
  with two `out` strings cut the listing but put both on the stack: 7.5 to 12.8 % slower on runners. A constant
  parameter at each call site (`share: false`) let the inliner drop the dead branch per caller instead, with the
  listing as before and the string classes 17 to 21 % faster.
- Outside .NET the same budget exists: V8 inlines only functions under 460 bytes of bytecode, so a TypeScript read
  helper that grows past it (`otherString` at 574 bytes) becomes a call on every use; split the rare path out. Rust:
  when a shape helps one target and hurts another, put it behind `cfg(target_arch)` and keep the generic loop for
  every other target (the map read taking one check per entry is x86/x86_64 only: on Arm64 it held the key/value pair
  in a SIMD register across the hash call and Neoverse-N2 ran 3 to 4 % slower). Test both paths (`platforms`).
- TypeScript output is the bytes it writes: a slower serialize in a large heap is often GC, not the loop. Before changing
  a write loop for a case that got slower, run it with `--trace-gc` and read whose pauses land in its task. Every new
  `ArrayBuffer` counts as external memory, and Node 24 starts a full GC for each 64 MB of them that no scavenge
  freed (`benchmark`, "Keep only the running serializer's data live"). Its cost grows with the live heap, and no
  code in `nino.ts` avoids it while each result is new memory. Two Mesh write loops measured no faster on runners
  (test branch ef4917d, run 40815167424, against Nino's own loop in the same job):
  writing each `Vec3` into a `Float32Array` scratch, then one `set` into the output, measured 0.455-0.53 against
  0.49-0.51 ms in Chromium (a wash within the 0.93-1.03 floor), and in Node 1.1-1.3 ms against 0.50-0.62 ms
  because its task met more of the GC pauses. Loading element i+8's `x` to start its miss early measured 0.49-0.55
  against 0.49-0.51 ms. Reading the same graph without writing took only 0.11-0.17 ms. Gains there come from fewer
  objects and less GC, not from the loop.
- Count a typed array's backing store as an allocation in V8: give each typed array over 64 bytes one of its own only
  when it must outlive the call, and never touch `.buffer` of one of 64 bytes or less. Those V8 keeps on its heap, and
  `.buffer` moves it off-heap: copying 4 int64s through it read Event 9-32 times slower than an element loop (test
  branch 97deeca, run 40817550027). Above 64 bytes a fresh `ArrayBuffer` per array cost more than the copy: views of a
  shared 64 KB slab (as Node pools small `Buffer`s) read Samples 0.31-0.78x the time of an own buffer each (f85f4e7),
  and locally `slice()`, `set()` into `new Float64Array(n)` and a DataView loop into one were all 2x the slab in
  Chromium and 4-5x in Node. Even with the slab, Chromium read 3 x 1,000 numbers 2.0-3.3 times slower into typed arrays
  than into `number[]` (Node 0.55-0.85x; run 40865735513, d07506b), so typed arrays are nino-gen's opt-in
  `--numeric-arrays typed`, not its default: serialize was faster in both engines (Samples 0.35-0.77x).
- Replace `%` by the runtime's fast modulo (multiply-high, `HashTables.Bucket`) on x64 only: on Arm64 `udiv`/`msub`
  is cheap and the multiply sequence lost. ConcurrentDictionary's build with fast modulo read 18-23 % faster on EPYC
  7763, on par on Zen 4 (9V45, 9V74), and 1.1 % slower on Neoverse-N2 in two rounds (3d4baf2).

## Bounds checks

- Index an array or span on a hot path through a ref to its first element and `Unsafe.Add`, never `a[i]` or
  `span[i]`: take `ref MemoryMarshal.GetReference(span)` or `ref NinoUnsafe.GetArrayDataReference(array)` once (the
  wrapper covers netstandard2.0, which lacks `MemoryMarshal.GetArrayDataReference`), check the length once against
  what the loop will touch, then walk with `Unsafe.Add(ref first, i)` or a bumped cursor. Why: the JIT removes a
  check only when it can prove `i < Length` (a `for` over `0..Length` of the same local); anything else (two arrays,
  an index from the data, a count read earlier, `i + 1`) leaves a compare and a `CORINFO_HELP_RNGCHKFAIL` call per
  access, and the throw block also costs the method its inlining budget. On IL2CPP the gain is larger: it emits its
  own check on every array access unless the project turns it off, and `Unsafe.Add` has none.
- The one check you keep is what makes it safe: on a read path it is the length check against the bytes left
  (`audit-security`), and every `Unsafe.Add` must stay inside what that check covered. Never `ref array[0]` (throws
  on an empty array, and is a bounds check itself); `GetReference` of an empty span is a valid ref that must not be
  read. `ref span.GetPinnableReference()` is the same as `GetReference` but returns a null ref for an empty span:
  use it only with `fixed`.
- Find the checks left: `CORINFO_HELP_RNGCHKFAIL` in the Tier1 listing (`benchmarks/asm.py dump --asm FILE`),
  counted per method; a hot method with one in its loop is a finding, one on a cold path out of line is not.
- Other runtimes: Go has no unchecked access outside `unsafe`; slice once and index with constants
  (`d := dst[j : j+4 : j+4]; d[0] = ...`, `_ = b[n-1]`), checked with `go build -gcflags=-d=ssa/check_bce/debug=1`.
  Rust keeps `unsafe` in `src/simd.rs`: take a fixed array (`r.array::<N>()`, `try_into`, `chunks_exact`) so one
  check covers a run. TypeScript: index typed arrays inside a loop whose bound is the array's `length`, read multi-byte
  values through one `DataView` call, not byte by byte.

## Vector loops

- Bump `ref` cursors and count down (`at = ref Unsafe.Add(ref at, step)`, `while (--n != 0)`), never
  `(nuint)i * size` per iteration; take everything that does not change per iteration (masks, indices) out of the
  loop into locals. Scalar tails too: `NinoBlock.WriteEach`'s per-value loop with an index scaled twice per
  value wrote 1,000 dictionary entries in 658 ns without hardware intrinsics, with cursors 393 (main's per-entry
  loop: 399).
- On Arm64 (and anywhere without AVX-512) `Vector512<T>` and `Vector256<T>` are structs of 16-byte vectors: passed
  into an inlined loop body they are copied through the stack every iteration. Take their `Vector128` parts out once,
  before the loop, and pass those.
- Arm64 `tbl` with two to four table registers needs consecutive registers: build the tuples from loads made once
  per iteration, before any lookup (`(w0, w1)`, `(w1, w2)`), never from per-lookup loads, or the JIT adds `mov`s.
  Each table register is a micro-op on Neoverse cores: give each lookup only the registers its bytes come from, and
  store only the bytes the values fill (`NinoBlock.WireTables64`); check each shape, as types that compile to the
  same code can differ by the data (`(byte, int)` took twice as long as `(short, int)`).
- Load and store a `Vector<T>` in the netstandard builds with `Unsafe.As<byte, Vector<T>>(ref at)` (plain `ldobj`
  / `stobj`), never `Unsafe.ReadUnaligned` / `WriteUnaligned`: classic Mono compiles the `unaligned.` prefix on a
  struct wider than its inline-copy limit to a call to memcpy through a stack slot (`mini_emit_memory_load` in
  mono/mini/memory-access.c), and plain ones to `movups`. RyuJIT never assumes a vector aligned, so its code is the
  same. Evidence: `NinoMask.CopyVectors` on Mono 6.8 serialized 1,000 padded structs in 36 to 40 µs with
  `ReadUnaligned`, 8.4 to 9.2 with `Unsafe.As` (11.8 to 13.4 with ulongs); its .NET 10 listings (netstandard2.0 and
  net10.0 builds) were identical. Which `Vector<T>` operations Mono accelerates at all is `platforms`.
- ARM64 is a load/store architecture: what x64 does in one instruction on memory is a load, an operation and a store
  there, and 64-bit constants and addresses take several instructions. Keep hot state in registers.
- Check bytes spread at a fixed stride (the bools of a block, `NinoChecks.Strided`) eight at a time into four
  accumulators, OR-ed once at the end: one accumulator is a chain of dependent ORs, one cycle per byte. Evidence:
  1,000 bytes 15 apart in 155 ns instead of 535 on a 2.1 GHz AVX-512 Xeon (four a step: 195); on GitHub's runners
  1,000 `Waypoint`s (a bool at offset 14 of 15) deserialized in 0.81 to 0.93 times the time on every CPU (EPYC 7763,
  9V45, 9V74, Xeon 8573C and 8370C, Neoverse-N2). Rejected: a vector check with a periodic mask of the bool
  positions, 211 ns with 64-byte vectors and 325 with 32-byte ones against the scalar 155.
- Check ranges of values in a run (Untrusted mode's bool, DateTime, decimal, DateOnly and TimeOnly fields,
  `NinoValueChecks`) with a subtract and an OR, never with compares: per stride, tables of masks and bounds repeat over
  lcm(stride, vector width), each vector ORs `bound - (v & mask)` into an accumulator (bytes: `v & 0xFE`) and only its
  sign bits are tested at the end. Use the vectors only when they cost less than the branch-free scalar loop
  (`2 * passes * stride <= fields * width`). Evidence (local Xeon, AVX-512, 1,000 values): unsigned `GreaterThan`
  compares were slower than scalar (a bool/DateTime/int/double struct 1,616 ns against 1,526; a 48-byte struct of
  every kind 9,849 against 5,658); subtract and OR with the cost rule took 1,535 and 5,644 against main's 3,806 and
  12,040 (main branched per field), DateTime[] 382 against 1,914; on runners the Untrusted list case (1,000 structs
  with a DateTime and a bool) took 0.47-0.69 times main's time on every CPU.
- Write a runtime's strided entries (a `HashSet<T>`'s or `Dictionary<K, V>`'s entry array) through the block path
  (`SetEntryBlock`, `MapEntryBlock`: a block map of the key and value at their offsets in the entry), never one
  `Write` per entry: the permutes pack them like any padded struct. Evidence (GitHub runners, runs 40842440261 and
  40865180098, n=12-48 a CPU, floors 0.99-1.01): 1,000-entry `HashSet<int>` serialize 0.34-0.49 times the time,
  `Dictionary<int, int>` 0.29-0.56, the polymorphic class array holding both 0.69-0.84.
- Compare bytes with chars eight at a time where no vectors are (netstandard `NinoStrings.SameBytes`): widen a
  ulong of bytes to two ulongs of chars (`x = (x | x << 16) & 0x0000FFFF0000FFFF; x = (x | x << 8) &
  0x00FF00FF00FF00FF`) on little-endian machines, with an overlapping last step, instead of one char a step.
- Gate an instruction choice per CPU where the runners disagree, by CPUID at startup in a `static readonly` (the JIT
  folds it), never by a `Vector512.IsHardwareAccelerated`-style ISA flag alone: AVX-512 VBMI is on Ice Lake, Sapphire
  Rapids and Zen 4/5 alike. `NinoBlock.WritePairs` (four 24-byte values per `vpermt2b` of 128 bytes of memory into 64
  of blocks) wrote 1,000 `Waypoint`s in 0.64 times the time of 32-byte lane groups on Xeon Platinum 8573C, 0.74 on
  EPYC 9V45 and 0.97 on 9V74, but 1.44 times on Xeon Platinum 8370C (Ice Lake): `NinoUnsafe.PairPermutes` takes AMD
  and Intel with AVX512-FP16 (CPUID 7.0 EDX bit 23, Sapphire Rapids on). Include `Xeon` in the A/B line's CPU regex
  for any AVX-512 path, or Ice Lake goes unmeasured.
- On Arm64, move values over 8 bytes in partial lane groups (`NinoLanes.Solve` with `partial`), as x64 without VBMI
  does: one at a time they took 1.44 µs for 1,000 `Waypoint`s on Neoverse-N2, in lane groups 0.43 (MemoryPack's copy
  of their memory 0.39).
- Copy long blocks per vendor and per destination: past 32 KB a 32-byte vector loop beats Memmove into a new zeroed
  array on Intel (Xeon 8370C, 160,000 bytes: 3.18 µs instead of 3.51) and loses there on Zen 5 (EPYC 9V45: 1.81
  against 1.67), while on AMD it beats Memmove into a new uninitialized array (9V45 and 9V74: 0.93 to 0.95 times the
  time). `NinoUnsafe.CopyOver64` (zeroed or existing memory) takes it on Intel only, `CopyToNew` on AMD only.
- Allocate a new array uninitialized only where the read writes every byte of it, padding included
  (`NinoElements.CopiedWhole`): block reads (lanes, permutes, per value) leave a padded value's padding as it was (9
  of a `Waypoint`'s 24 bytes, 4 of an `(int, long)`'s 16, on every ISA), so an uninitialized array would hand stale
  heap memory to `MemoryMarshal.AsBytes` or a raw re-serializer. Rejected for that, though faster: padded blocks
  allocated uninitialized read 1,000 `Waypoint`s in 0.68 to 0.76 times main's time (EPYC 7763, 9V74, Xeon 8370C,
  Neoverse-N2) and 1,000 `(int, long)` in 0.89 on the 7763; it can land only with reads that write the padding too.
- In Rust, give every fixed-size shape `FIXED_SIZE` with `decode_fixed` / `encode_fixed` and let `read_fixed` /
  `extend_fixed` run it over `chunks_exact`; write no shuffle kernel. LLVM already turns a block whose wire bytes
  equal its memory ((i32, i32), a raw struct, a derived plain `{i32, f32}`) into vector copies (`vmovups ymm` on
  AVX2, `movups` on baseline x86-64, `ldp`/`stp q` on aarch64), and a padded or mixed shape into one bounds check
  and a scalar loop (writes unrolled 4x) that is store-bound, so a gather would not pay. What was missing was the
  block path itself: derived plain structs (derive and nino-gen) had no `FIXED_SIZE`, and giving them one took
  `Vec<{i32, f32}>` 80-91 % and `Vec<{i64, bool, i32}>` 61-88 % faster on every runner CPU (runtime A/B run
  40868354821). Keep tuples on their own macro and `bool::decode_fixed` as `match`: routing tuples through
  `decode_next` and testing a bool as `b <= 1` made `(i64, bool)` reads 2-29 % slower (Xeon 8573C +29 %, EPYC 7763
  +11 %, run 40829803604); with both kept, tuples were on par.

## Unity kernels and IL2CPP

- Hold native kernels as function pointers in static fields, never behind a virtual base: `NinoKernels` is a table of
  `delegate* unmanaged[Cdecl]` that `Register` copies into `NinoKernelHook`'s static fields, a missing pointer leaves
  its threshold at `int.MaxValue`, so a call site is one static load, a compare and a `calli` (IL2CPP: a C function
  pointer call), with no object, null check or vtable slot. The abstract class it replaced cost, in IL2CPP's C++ of
  `NarrowWords`, a load of the registered object, a `NullCheck` and `VirtualFuncInvoker3<...>::Invoke(7, ...)` into
  a `PointerKernels_Narrow` that then called the pointer (unity.yml run 40818519412, artifact `il2cpp-cpp-*`).
  Burst gives the pointers (`FunctionPointer<T>.Value`); per call they were 0.3 to 0.6 ns faster than `.Invoke`
  delegates (Narrow16 5.99 against 6.29 ns, Permute1 16.2 against 17.2 on 2022.3, run 40717516438), and the delegate
  fallback is gone: a table only `calli` can call holds nothing else. With the table registered, cases under every
  threshold were on par with no kernels (Latin-1 8 chars, one tuple, 15-char `[NinoUtf8]` text: within 2 %, IL2CPP
  x64, unity.yml run 40822269653).
- Put every `calli` in its own `NoInlining` helper (`NinoKernelHook.TryNarrow`, `NarrowAsciiPrefix`, ...) and keep the
  threshold compare in the caller: classic Mono never inlines a method that contains a `calli`, so a pointer call
  written into `NinoStrings.NarrowWords` stopped it inlining into `WriteLong` and every Latin-1 write paid a call with
  no kernel registered (Mono 6.8 local, 1000 chars 469 against 564 ns; on par once the call moved out, the listing of
  `WriteLong` showing `NarrowWords` inlined again with `MONO_VERBOSE_METHOD`).
- Set each kernel threshold from a sweep that forces the kernels from 1 value or 16 chars, per call site, with
  allocation kept out (`SerializeTo` into a reused writer, `GC.Collect` before each group, groups A B B A, the lower of
  each pair: `benchmark`). The sweep of unity.yml run 40735278928 (IL2CPP x64 players, 2022.3 and 6000.3): blocks of
  tuples gain from 1 to 2 values (1.04 to 1.30x at 1 to 2, 1.5 to 1.9x at 4); Latin-1 strings from 16 chars (1.24 to
  1.55x); text above U+00FF costs up to 10 % at any length (the failed narrowing), so a lower MinChars adds no new loss;
  the padded copy (`NinoLayout.Copy`, whose own path copies masked words) breaks even at 12 to 16 values, hence its own
  floor (`NinoKernelHook.CopyFloor`).
- Build only the mask a call needs: `PermuteVectors` built all sixteen 16-byte masks on the stack before its one-load,
  one-store loop, about 35 ns per call; the table itself as one mask moved the padded copy's break-even from about 24
  values to 12 (0.69 to 0.75x at 1 to 2 values before, 0.75 to 0.80x after; 1.06 to 1.09x at 16).
- On WebGL (wasm SIMD, `Nino.WebGL`) the same kernels pay off only for strings from 16 chars (1.07 to 1.45x, 2 to 10x
  from 1000) and the padded copy from 32 values (2.3 to 5.3x from 1000); blocks of tuples gained nothing consistent
  (Unity 6000.3 and 2022.3, headless Chrome, run 40735278928).
- IL2CPP class formatters: a `SimpleClass` read (3.4 us on 6000.3, 2.1 on 2022.3) is mostly objects and collections
  built, not Nino's code: per member, `Dictionary<int, string>`(10) 1.2 us, `Dictionary<int, int>`(10) 0.9 us (the
  `HashTables` fill is on there: `RuntimePathsTests` asserts every flag), a `List<class>`(10) about 0.1 us per new
  element, the 360-char UTF-8 string 0.65 us; Burst changes the class only through its strings (`Name` 1.2 to 1.4x). The
  generated C++ shows shared generic code where a type argument is a class (`ListFormatter<__Canon, ...>`: an
  `il2cpp_rgctx_method` lookup per call, `il2cpp_codegen_runtime_class_init_inline(Reader)` before Reader calls) but no
  per-element cost that a change in Nino removes was found; time classes of one member each (the run above did) before
  changing a formatter for IL2CPP.

## Reading the listings

- x64: `--asm` of `benchmarks/asm.py`, once per ISA (`DOTNET_EnableAVX512=0`, `DOTNET_EnableAVX2=0`).
- Arm64 from an x64 machine: `benchmarks/cross-jit.sh` installs the other architecture's JIT, and
  `DISASM=1 benchmarks/members/run.sh` writes both listings for the member cases (that JIT only lists: with
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
  `--filter TupleList` showed `NinoBlock.WriteEach<(int, long)>` with its permute loop moved, dumps of
  `--filter SerializeTupleList` the same listing on both sides. Before calling a listing changed, dump both sides
  again with the narrowest filter and compare the instructions with addresses and labels normalized.
- Timings on a shared machine vary by ±15 %: read the code before trusting a local difference, and take the decision
  from runners (`benchmark`). The wire format must not change unless that is the point: compare the bytes.

## Strings outside .NET

- Keep sizing the Rust AVX2 writer's UTF-16 units by the longest char in the whole 32-byte window: sizing each
  16-byte half by its own longest char (test branch 907b0f7; a lane after a four-byte lead must then count for the
  next half too, `(three >> 16) | (four >> 14)`, or `"xxxxxxxxxxxxxx𐀀"` breaks) made text mixed/16 and /24 5.5 and
  6.6 % faster and strings-mixed-1000 2.3 % on EPYC 7763, but cyrillic/16 3.0 % slower (n=32, floor -0.3 %, runs
  40753062510 and 40756504471): the second computation costs more than it saves when both halves are alike.
- Hand the window a Latin-1 pass stopped at to the UTF-16 pass instead of loading it again. Rust AVX-512
  (`wide_avx512`, 4e5465d): strings-mixed-1000 serialize 8.8 % faster on EPYC 9V45 (n=20), 8.9 % on Xeon 8573C
  (n=14), 6.0 % on Xeon 8370C (n=9), 3.5 % on 9V74 (n=35); text of 16 chars 4-16 % faster, every class (runs
  40753062510, 40756504471, 40761692830).
- Rust Arm64 keeps scalar `short_wide_inline` for text up to 64 bytes: routing short non-ASCII text to `neon::wide`
  (test branch 0fced91, dda2c89; run 40734299173, n=15 on Neoverse-N2) made strings-mixed-1000 50 % slower, text
  mixed/16 112 %, cjk/16 75 %, cyrillic/16 18 % and latin1/16 9 %; only latin1/24 (-25 %) and cjk/24 (-12 %) gained.
- Rust strings-mixed serialize trails rmp-serde on every CPU by design: the competitors copy UTF-8 with memcpy, and
  Nino's wire format is Latin-1 or UTF-16, so mixed text is transcoded. Narrow it in the kernels; do not expect to
  pass a copy.
- TypeScript: a cache of decoded map-key strings (4,096 and 16,384 slots, keyed by a hash of the bytes) made Lookup
  deserialize slower (0.90 to 1.12-1.14 ms locally): hashing bytes in JavaScript costs more than decoding them, and
  the case has 5,000 distinct keys. On Chromium, decoding short strings through a windowed `TextDecoder('latin1')`
  left ASCII unchanged and made mixed strings 30 times slower (0.045 to 1.34 ms). The bench's JSON leads on Lookup and
  ASCII strings deserialize are an artifact of parsing the same text every iteration: V8's JSON.parse reuses
  hidden-class transitions and internalized keys for repeated text (a Lookup-like text took 0.21 ms a parse repeated,
  0.25 with two texts alternating, 7.8-8.4 ms with 200 in turn; string arrays 63 against 112 µs), while building Maps
  from fresh key strings costs the same either way (0.65 against 0.67 ms). Vary the bench data before chasing those
  cases.

## Prefetch

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
  could not do that, see "What has gone wrong before" on the root's inlining budget). Test branch run 40916374747
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
  Rust, landed (869408d): `NinoWrite::PREFETCH` (bytes read through pointers, summed by the derive and nino-gen) is a
  const, so `write_many` of every other type folds the test away; a slice of 512 or more whose estimated data
  outgrows the last-level cache (sysfs, 32 MiB default) takes the prefetching loop, the size test `#[cold]` out of
  line. 1,000,000 shuffled records took 67-78 % less time on EPYC 7763, 9V45, 9V74 and Neoverse-N2, 39 % on Xeon
  8370C, on par on Xeon 8573C (its cache holds the set). Gate on the cache, not a fixed budget: prefetching every
  list over 1 MiB (b93fe69) had the same gains but moved cached records-1000 1.5-3 % on EPYC and Neoverse-N2; gated,
  records-1000 stayed within a null change's spread measured in the same run (`benchmark`). Putting the eligible
  loop out of line at a 64-byte boundary (8dc3b04) did not help: +2.7 % on 7763 and N2.

## Dispatch outside .NET

- Call the element's code directly in every collection loop, never through a func value, an interface method or a
  shared helper given a closure (the owner's rule: no virtual dispatch on a serialize or deserialize path). Go: a
  generic helper that takes `read func(*Reader) T` is not inlined, and GC-shape stenciling would pass a dictionary
  anyway, so each element is an indirect call the compiler can neither inline nor see through; a type parameter with
  methods does not help either (types of one shape share the dictionary's indirect call). nino-gen's Go helpers
  write their loops (`read_loop`, `write_loop` in `Templates/go/types.sbn`; `go tool objdump` shows `CALL ...ReadBag`
  where it was `CALL CX`). Runners (4225c20 against main 78ceaa1, go#12, 12 rounds a job, medians of jobs against
  main-vs-main floors within 0.98-1.02): a `string[]` read through `nino.ReadStrings` instead of `ReadSlice(r, 1,
  (*Reader).String)` took 0.74 of the time on EPYC 7763, 0.80 on 9V74, 0.82 on Xeon 8573C, 0.86 on 8370C, 0.91 on
  Neoverse-N2; mixed and [NinoUtf8] strings 0.95-0.97; 1M records read 0.92 on EPYC 7763; SimpleClass serialize
  0.94-0.97 on every CPU but EPYC 7763 (1.015, n=12, where the unchanged machine code of `WriteStrings` moved 1.008
  in the same jobs). One case moved by layout alone: 1M records serialize took 1.20 of the time on Xeon 8573C (n=4 jobs, 1.19-1.25,
  floor 0.975-0.996), but main with only the records loop written out took 0.955 (bcbbcbe, n=1) and the change with
  that loop put back to `WriteSlice` 1.02 (31b8e79): the cost came from the strings and SimpleClass code that case
  never runs, so run such controls (each piece alone on main, the change without it) before blaming a loop. Rust needs nothing of the kind (generics are monomorphized; `dyn` only on the
  deferral path past depth 128), nor does nino-gen's TypeScript (a function per collection shape, subtypes by a
  `switch` on the id or the constructor). Still dispatched, by design or pending the owner: the Go class body
  (`Class[T].Read`, one indirect call an object: direct calls through a `BeginBody`/`EndBody` pair read 1M records
  in 0.93-0.95 of the time on every CPU, other cases on par, 479f92e, not landed: public API), Go
  `ReadUnique`/`ReadUniqueEntries` (their integer-key check is internal), generic type arguments (Go `NinoArg`,
  TypeScript codec objects) and the struct-tag path (a plan of closures by design).

## Checking blocks in Go

- Check a block of range-limited 8- or 16-byte values four a step with one compare each (`w[0].Valid() && ... &&
  w[3].Valid()`, then the failing step value by value), each `Valid` a single compare (a signed range from 0 as
  unsigned; a Decimal's flags without the sign, rotated by 16 bits, `<= 28`). Writing 1000 values (runtime A/B runs
  40822228516, 40864042296, 40868383597; floors within 3 %): DateTime 29-38 % faster on every CPU, Decimal 2-24 %,
  TimeOnly up to 26 % (ae414a0). Branch-free forms were slower than the branchy loop on a local Xeon and were not
  kept: `x | (max - x)` sign tests in four accumulators (+5 to +25 %), `max()` through CMOV (+10 %), an OR of all
  values compared once (exact only when the values share a range; twice the time on random dates). Keep 4-byte
  values (DateOnly) one at a time: four compare-branches a step wrote 19 % slower on Xeon 8370C (two rounds, 21 %
  faster on Neoverse-N2), and two words of two lanes with one branch (top bit, or the rest plus `2^31-1-Max` reaching
  it) 8-19 % slower on Xeon 8370C, EPYC 7763 and 9V74.
- Give each type's check over a block a non-generic function of its own (`//go:noinline`), called from the generic
  helper's type switch: inlined into `invalidAt[T]`, every instantiation (one per GC shape) compiled all four loops,
  and the DateOnly loop, its instructions unchanged, moved to an address that ran it 6.8 % slower on Neoverse-N2
  (run 40925188020; out of line, 3.2 % faster, run 40934073630).
- Read a byte at a fixed offset of each element (the bools of raw structs, `rawBools`) four elements a step into four
  accumulators through `unsafe.Add` on the slice's pointer, adding only in-range offsets (a pointer past the end
  fails `-race`'s checkptr): raw structs of 8 bytes with a bool 26-38 % faster writing and 4-14 % reading. A mask of
  eight elements ANDed a word at a time (as `clearPads`) was 2-6 times slower locally for one or three bools.
- Copy UTF-16 units as memory on little-endian hosts (`Reader.UTF16`, `Writer.UTF16`): wide text 60-63 % faster to
  read and 92-96 % to write; narrowing Latin-1 four units a word 40-64 % faster to write. Widening Latin-1 four bytes
  a word on read (`load32`, `widen`, `store64` into a fresh slice) was slower than the byte loop: +86 % on EPYC 7763,
  +56 % on Xeon 6973P, +10 % on Neoverse-N2 (run 40822228516); the byte loop stays.

## Owned elsewhere

- `NinoSettings.Untrusted` tests stay a single `if` on a frozen field with the work out of line (`audit-security`).
- Paths through a runtime's internals: `runtime-internals`. Platform matrix and emulators: `platforms`; a vector
  path chosen by `X.IsSupported` uses X's own intrinsics under that test, or NativeAOT runs it in software
  (`platforms`).
- The same rules for Go, Rust and TypeScript: `audit-runtimes` and `runtimes/*/AGENTS.md`.
- Generated code's listings: `audit-generated-code`.
