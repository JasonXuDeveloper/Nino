# Bounds checks and blocks of fixed-size values (Go, Rust, TypeScript)

Part of the `optimize-runtimes` skill: one bounds check per run in each language, Rust's fixed-size block path,
and Go's checks over blocks of range-limited values and bools. Read it before writing a loop over elements or a
value check in a runtime or a template.

## Bounds checks

- Go has no unchecked access outside `unsafe`; slice once and index with constants
  (`d := dst[j : j+4 : j+4]; d[0] = ...`, `_ = b[n-1]`), checked with `go build -gcflags=-d=ssa/check_bce/debug=1`.
  Rust keeps `unsafe` in `src/simd.rs`: take a fixed array (`r.array::<N>()`, `try_into`, `chunks_exact`) so one
  check covers a run. TypeScript: index typed arrays inside a loop whose bound is the array's `length`, read multi-byte
  values through one `DataView` call, not byte by byte.

## Fixed-size blocks in Rust

- Write fixed-size Rust sets through `extend_fixed_iter`, reserving the count and payload together in iteration order.
  In runtime A/B 44329792122, the median write time for 1,000 `i32` hash-set elements fell 33–43 % across EPYC
  7763, 9V74, 9V45 and Xeon 8573C, and 58 % on Neoverse-N2. The GUID hash-set case fell 26–44 % on the same
  x64 CPUs and 58 % on Neoverse-N2. The ordered `i32` case fell 37 % on Neoverse-N2 and ranged from on par
  to 20 % lower on x64. The comparison used the same block benchmark harness on both sides.

- In Rust, give every fixed-size shape `FIXED_SIZE` with `decode_fixed` / `encode_fixed` and let `read_fixed` /
  `extend_fixed` run it over `chunks_exact`; write no shuffle kernel. LLVM already turns a block whose wire bytes
  equal its memory ((i32, i32), a raw struct, a derived plain `{i32, f32}`) into vector copies (`vmovups ymm` on
  AVX2, `movups` on baseline x86-64, `ldp`/`stp q` on aarch64), and a padded or mixed shape into one bounds check
  and a scalar loop (writes unrolled 4x) that is store-bound, so a gather would not pay. What was missing was the
  block path itself: derived plain structs (derive and nino-gen) had no `FIXED_SIZE`, and giving them one took
  `Vec<{i32, f32}>` 80-91 % and `Vec<{i64, bool, i32}>` 61-88 % faster on every runner CPU (runtime A/B run
  40868354821). Keep tuples on their own macro and `bool::decode_fixed` as `match`: routing tuples through
  `decode_member` and testing a bool as `b <= 1` made `(i64, bool)` reads 2-29 % slower (Xeon 8573C +29 %, EPYC 7763
  +11 %, run 40829803604); with both kept, tuples were on par.
- Store a fixed-size value's bytes into `encode_fixed` / `encode_raw`'s slice a register at a time (`store_words` in
  `types.rs`: eight bytes a copy), never as one copy wider than a register, and test the bytes that must be zero by
  ORing each run whole, never through `iter().chain().any()`. `extend_fixed` encodes each element into a buffer on
  the stack and copies it out; LLVM drops the buffer only when every store into it is a legal integer, so a `Guid`
  (`[u8; 16]`, one 16-byte copy) at offset 4 of a `Guid?` slot or 16 of a `NinoNullable<Guid>` stayed in memory and
  the copy out, split at other offsets, waited on store forwarding for every element (the listing: `movups %xmm,
  3(%rsp)` then `movaps (%rsp)` and `movl 15(%rsp)`). Local Xeon, 10,000 elements, `--bench blocks` (a decision
  only, never published): writing `Vec<NullableSlot<Guid>>` 75 µs to 10.2 µs, `Vec<NinoNullable<Guid>>` 75 µs to
  13.4 µs, plain `Vec<Guid>` unchanged; reading `Vec<NinoNullable<Guid>>` 68 µs to 24 µs and
  `Vec<NinoNullable<i32>>` 20 µs to 11.7 µs with the ORed check. Zeroing each element in place instead of the stack
  buffer (`write_bytes`, then encode into the room) fixed the Guid but wrote `NullableSlot<i32>` 30 %,
  `NullableSlot<i64>` 43 % and `NullableSlot<Decimal>` 64 % slower: not kept. This is what let `RawNullables<T>` go:
  `Vec<NullableSlot<T>>` on the block path reads i32, i64 and Guid slots 1.5, 2.3 and 5 times as fast as its
  per-element loop did and writes them as fast or faster.

## Checking blocks in Go

- Reserve one checked block for fixed-width Go containers. Use constant wire offsets for padded primitive pairs
  and copy compatible primitive grids as blocks. In runtime A/B
  [44339615693](https://github.com/JasonXuDeveloper/Nino/actions/runs/44339615693), padded-pair read times fell
  66–75 % on the measured x64 CPUs and 72 % on Neoverse-N2. Byte-grid writes fell 92–96 % on both architectures.
  Fixed DateTimeOffset sequence writes fell 52–59 % on x64 and 47 % on Neoverse-N2.
- Encode fixed map entries through their addresses. In the original fixed-stride loop the x64 GUID entry value
  crossed two stack temporaries. The address loop reduced ten `MOVUPS` per entry to two, with the stride guard
  intact. In [44347813411](https://github.com/JasonXuDeveloper/Nino/actions/runs/44347813411) the revised GUID map
  write took 82–85 % less time on x64 and 81 % less on Neoverse-N2. The revised map read took 52–54 % less on
  x64 and 45 % less on Neoverse-N2.
- Keep generated raw fallback loops on direct codecs after the raw-memory attempt. In the
  [prepared-key follow-up](https://github.com/JasonXuDeveloper/Nino/actions/runs/44451141322) both native
  listings call `repeatedBits` directly for padded primitive pairs. The eight-pair read was on par on
  EPYC and Neoverse-N2. Its Xeon 6973P-C sample took 9 % less time, with no same-CPU control.

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

## Byte block copies

- Confirm copy microbenchmarks in the codec that allocates or reuses the real buffer. Runtime A/B
  [44339615693](https://github.com/JasonXuDeveloper/Nino/actions/runs/44339615693) probed disjoint copies with
  rotated buffers, including a destination offset of four bytes. Go's candidate lowered the Neoverse-N2 time
  by 10–33 % at 2–4 KiB but raised it by 21 % at 256 KiB with that offset. On the x64 runners it lowered the
  hot 1 MiB time by 20–44 %, while the 2–4 KiB results varied by CPU and alignment.
  Keep Go's built-in copy in `Reader.ByteArray`. The Arm64 candidate for fresh 2–4 KiB reads took 5–9 %
  longer with a hot input and 24–26 % longer with a 64 MiB source rotation on Neoverse-N2
  ([44347813411](https://github.com/JasonXuDeveloper/Nino/actions/runs/44347813411), `DeserializeExact`).
  The 128-byte fallback control also took 30 % longer. The hot 1 MiB x64 copy result remains a microbenchmark.
- Compare Rust's standard copy under the default loader and with
  `GLIBC_TUNABLES=glibc.cpu.x86_rep_movsb_threshold=2112`. In the same run, forcing the historical lower
  threshold made the unaligned 2,113-byte standard copy 7.1× its default time on EPYC 7763 and 5.8× on 9V74.
  AVX2 took about 13–15 % of that tuned time. With the default loader the custom vectors had mixed x64 results
  and the unaligned NEON copies cost 15–25 % more through 64 KiB. The lower threshold reproduces the AMD
  `rep movsb` transition. The current loader still has its own upper stop threshold, so the 1 MiB row does
  not reproduce every historical glibc configuration.
  In real Rust codecs ([44347813411](https://github.com/JasonXuDeveloper/Nino/actions/runs/44347813411)),
  the scoped AVX2 candidate lowered reused writes by 14–33 % at 2,113 and 4,096 bytes on EPYC with the
  forced threshold. The 2,113-byte writes also fell 9–10 % under the default loader on EPYC 7763 and 9V74.
  Ordinary read measurements shifted by about 25–30 % even with the same revision on both sides. Keep the
  current Rust copy until a write-only path and its production dispatch have their own measurements.
- Keep `TypedArray.set` for ordinary TypeScript buffers. In 44339615693, copying through Wasm memory with both
  staging copies took 82–339 % more time than `TypedArray.set` across the default and historical threshold
  runs. For views already backed by Wasm memory, `memory.copy` varied from 8 % lower to 17 % higher by size and
  CPU. These views avoid the staging cost and are timed separately from ordinary arrays.
