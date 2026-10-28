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
fix every method it lists.

## What has gone wrong before

- A branch whose condition is a method call (`RawFormatter.Is`, `NinoBlock.Size`, ...) is compiled and inlined even
  for the types where it is dead, and uses up the budget there. Keep rare or large paths (vector loops, element by
  element loops, string allocation, recycling) out of line (`NoInlining`), with only the common path inline.
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
- ARM64 is a load/store architecture: what x64 does in one instruction on memory is a load, an operation and a store
  there, and 64-bit constants and addresses take several instructions. Keep hot state in registers.

## Reading the listings

- x64: `--asm` of `benchmarks/asm.py`, once per ISA (`DOTNET_EnableAVX512=0`, `DOTNET_EnableAVX2=0`).
- Arm64 from an x64 machine: `benchmarks/cross-jit.sh` installs the other architecture's JIT, and
  `DISASM=1 benchmarks/members/run.sh` writes both listings for the member cases; or build for `linux-arm64` and run
  under `qemu-aarch64-static` with `DOTNET_TieredCompilation=0 DOTNET_JitDisasm='<method>' DOTNET_JitStdOutFile=<file>`
  (the code paths only a type outside the benchmark suite takes, such as `(byte, int)` for the 64-byte permutes, need
  a small program of their own). A clean loop is loads (`ldp`), lookups, stores (`stp`) and the cursor adds;
  `stp`/`ldp` to `[fp, ...]`, `mul` or `mov v..` in it are the signs above.
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
  eligible one adds one length compare and an out-of-line loop that takes the reader or writer by value. Two stages
  for elements with reference members: at element i prefetch element i+2D, at i+D prefetch its members' targets.
  Why: a prefetch on every element cost 14-15 % on 1,000 cached objects, and a large-collection branch that needed a
  runtime generic lookup spilled the caller (Shapes 1 to 7 stack accesses); shuffled 1M-object arrays serialized 1.2
  to 2.3 times as fast locally with the prefetching loop. Platforms: .NET x64 `Sse.Prefetch0`; Unity through a
  `NinoKernels` hook to Burst's `Common.Prefetch`; Rust `_mm_prefetch` on x86 and `asm!("prfm pldl1keep, ...")` on
  aarch64, both in `src/simd.rs`; Go only through an assembly stub, one call per batch; .NET Arm64, wasm and
  TypeScript have no prefetch instruction. Go, measured and not landed: a stub `prefetch8` (eight PREFETCHT0 / PRFM PLDL1KEEP a
  call) in `WriteSlice` above a per-type threshold, objects 16 elements ahead and their strings 8 ahead, wrote
  1,000,000 shuffled records 2.2-3.1 times as fast on EPYC 7763, 9V45 and 9V74 and 4-5.5 times on Neoverse-N2, but
  13-38 % slower on Xeon 6973P-C, whose L3 holds the 100 MB set (its base was already 2-3 times as fast: nothing to
  hide, only the extra loads and calls), and moved short-slice cases 1.5-4.7 % on x64 (test branch 4e2099b, 42ae966
  against c44171f). Before trying it again, find a gate for data larger than the last-level cache. Keep only the
  length compare inline: a plan lookup that does not inline (cost 147) in every `WriteSlice` cost 1-3.8 % on
  SimpleClass.

## Owned elsewhere

- `NinoSettings.Untrusted` tests stay a single `if` on a frozen field with the work out of line (`audit-security`).
- Paths through a runtime's internals: `runtime-internals`. Platform matrix and emulators: `platforms`; a vector
  path chosen by `X.IsSupported` uses X's own intrinsics under that test, or NativeAOT runs it in software
  (`platforms`).
- The same rules for Go, Rust and TypeScript: `audit-runtimes` and `runtimes/*/AGENTS.md`.
- Generated code's listings: `audit-generated-code`.
