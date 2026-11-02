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
