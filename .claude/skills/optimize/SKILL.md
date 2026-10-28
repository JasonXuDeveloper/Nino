---
name: optimize
description: "Nino's optimization techniques, measured and kept - what made read and write paths faster or slower in .NET (the JIT's inlining budget, keeping the reader and writer in registers, out-of-line rare paths, static readonly checks, bounds-check elimination with a ref to the first element and Unsafe.Add, vector loops on x64 AVX-512/AVX2/SSSE3 and Arm64 tbl/ldp/stp) and the same ideas in Go, Rust and TypeScript, plus reading the machine code (asm.py, JitDisasm, check_bce). Use when writing or reviewing a hot path in any runtime, before committing a change to a read or write path, when a case got slower, or when a new trick is found (record it here). Triggers: optimize, faster, hot path, inlining, AggressiveInlining, NoInlining, bounds check, RNGCHKFAIL, Unsafe.Add, GetReference, GetArrayDataReference, check_bce, panic_bounds_check, Vector128/256/512, Avx2, AdvSimd, SIMD, tbl, shuffle, permute, spills, asm.py, JitDisasm, Tier1, 优化, 优化技巧, 性能优化, 边界检查, 机器码, 汇编, 内联, 向量化."
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
  identical to main (`CopyBlock` keeps Arm64's branch out of `Copy` for the same reason).
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
  loop into locals.
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
- Timings on a shared machine vary by ±15 %: read the code before trusting a local difference, and take the decision
  from runners (`benchmark`). The wire format must not change unless that is the point: compare the bytes.

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
  faster on 7763), and Players and PolymorphicClassArray 2 % slower on 7763. Next design to try: pass neither: a
  `NoInlining` helper keyed on the formatter (no lookup) prefetches the next run of 16 objects and their members'
  targets, called once per 16 elements from a chunked copy of the plain loop, so the writer and reader stay in
  registers and no frame grows; and find why EPYC 9V74 loses on objects in allocation order before landing it.
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

## Owned elsewhere

- `NinoSettings.Untrusted` tests stay a single `if` on a frozen field with the work out of line (`audit-security`).
- Paths through a runtime's internals: `runtime-internals`. Platform matrix and emulators: `platforms`; a vector
  path chosen by `X.IsSupported` uses X's own intrinsics under that test, or NativeAOT runs it in software
  (`platforms`).
- The same rules for Go, Rust and TypeScript: `audit-runtimes` and `runtimes/*/AGENTS.md`.
- Generated code's listings: `audit-generated-code`.
