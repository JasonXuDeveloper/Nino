---
name: optimize-runtimes
description: "Nino's optimization techniques in Go, Rust and TypeScript, measured and kept - the inlining budget outside .NET (V8's 460 bytes, cfg(target_arch)), GC and typed arrays in V8, bounds checks (check_bce, chunks_exact, DataView), Rust's fixed-size blocks, the string kernels (Rust AVX2/AVX-512/NEON, Go assembly, wasm), direct calls instead of func values, closures and dyn dispatch in collection loops, reuse branches off the fresh read path, and Go's block checks of range-limited values. Use when writing or reviewing a hot path in runtimes/ or in nino-gen's templates, when a runtime case got slower, or when a new trick is found there (record it here). Triggers: runtimes/go, runtimes/rust, runtimes/typescript, Templates/*.sbn, check_bce, panic_bounds_check, go tool objdump, func value, dyn Trait, monomorphized, V8 inlining, --trace-gc, typed arrays, Float64Array, FIXED_SIZE, chunks_exact, strings_wire, simd.rs, Go 优化, Rust 优化, TS 优化, 运行时性能, 虚调用."
metadata:
  internal: true
---

# Optimization techniques outside .NET

The techniques that made the Go, Rust and TypeScript runtimes and nino-gen's generated code faster or slower, split
out of `optimize`, which keeps .NET, Unity, reading the machine code and the prefetch design (which covers every
runtime: `optimize`, "Prefetch"). How to measure on runners is `benchmark` (runtime A/B), where the next win is
`audit-perf`, each runtime's rules are `runtimes/*/AGENTS.md` and `audit-runtimes`. A technique found and measured
(faster or slower) is added here, as a bullet: the rule, why, the evidence.

## The inlining budget and GC

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

## Bounds checks

- Go has no unchecked access outside `unsafe`; slice once and index with constants
  (`d := dst[j : j+4 : j+4]; d[0] = ...`, `_ = b[n-1]`), checked with `go build -gcflags=-d=ssa/check_bce/debug=1`.
  Rust keeps `unsafe` in `src/simd.rs`: take a fixed array (`r.array::<N>()`, `try_into`, `chunks_exact`) so one
  check covers a run. TypeScript: index typed arrays inside a loop whose bound is the array's `length`, read multi-byte
  values through one `DataView` call, not byte by byte.

## Fixed-size blocks in Rust

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
  in the same jobs). One case held past its floor: 1M records serialize took 1.20 of the time on Xeon 8573C (4 jobs of 12 rounds,
  1.19-1.25, floor 0.975-0.996) while main with only the records loop written out took 0.955 (bcbbcbe, one job) and
  the change without it 1.02 (31b8e79); calling the class body directly removed it (below). A loss that holds is
  fixed, not filed as layout, however the controls point. Rust needs nothing of the kind (generics are monomorphized; `dyn` only on the
  deferral path past depth 128), nor does nino-gen's TypeScript (a function per collection shape, subtypes by a
  `switch` on the id or the constructor). Class bodies and dictionary key checks went the same way (414a0a28): `Reader`/`Writer.EnterBody` and `ExitBody` open a
  body in place and fall back to `ReadClassInto`/`WriteClass` from 128 open bodies, and `nino.UniqueEntries` checks
  the pairs once; 1M records then read in 0.93-0.96 of the time on EPYC 7763, 9V45, 9V74 and Neoverse-N2, and the
  Xeon 8573C loss above went to 0.947 of the old base (one job of 12 rounds). Sets followed with `nino.UniqueValues` (no bench case
  holds a set, so no runner number). Still dispatched by design: tracked classes, generic type arguments (Go `NinoArg`, TypeScript codec objects) and the struct-tag
  path (a plan of closures).
- Keep reuse branches off the read path of a new value: share the body, not the dispatch. Reading into an existing
  value (`deserializeInto`, `ReadXInto`, `read_into`) shares each type's body with the plain read, but a member that
  holds nothing goes straight to the plain read (`v.m == null ? readX(r) : into`), and a slot's switch and a
  collection's loop keep a fresh form without the reuse checks. TypeScript with one switch and one helper for both
  (`read: (r) => XCodec.readInto(r, null)`, helpers taking an optional `cur`) took Player deserialize 4-9 % and Zoo
  7-8 % longer in Chromium (run 40961396786, n=9-30); with the fresh forms back, on par (40969410249). Reading a
  struct member into the instance its class's initializer already made, instead of a second one, made Mesh
  deserialize 8-24 % faster. Go kept its slots' and sealed classes' plain reads (with 414a0a28's direct body calls)
  and read lists through the into loop: on par (40976827882, geomean within 0.8 %); Rust's `read = Default +
  read_into` was on par too (40925188020).

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
