# Rust runtime: file map

> Read when: reading or changing the Rust runtime (runtimes/rust: crates nino-serialization and nino-serialization-derive), its SIMD kernels, tests or benchmarks.

A Cargo workspace: the crate `nino-serialization` (lib `nino`, MSRV 1.88, edition 2021, no required dependencies;
`#![deny(unsafe_code)]` outside `src/simd*`), the derive crate and the benchmarks. README.md documents the API,
attributes and the "Stricter than .NET" list. nino-gen's Rust target (`src/Nino.Codegen`) writes code in the shape
of `tests/conformance/types.rs`. `audit-runtimes` audits it, `audit-security` its read paths.

```
Cargo.toml                   the crate and the workspace (., bench, nino-derive); optional features uuid,
                             rust_decimal, chrono, num-bigint, indexmap, derive, serde; the tests' required features
Cargo.lock                   the workspace's resolved versions
rustfmt.toml                 max_width 120
build.rs                     sets cfg nino_avx512 when the compiler has the AVX-512 intrinsics (Rust 1.89+)
.gitignore                   target/
README.md                    the API, derive attributes, guarantees, "Stricter than .NET", tests, benchmarks
src/
  lib.rs                     crate root: modules, re-exports, the deny lints
  reader.rs                  Reader: bounds-checked little-endian reads, lengths checked against the bytes left
  writer.rs                  Writer: appends to a Vec<u8>
  error.rs                   the one error type (Error, ErrorKind)
  traits.rs                  the traits generated code implements and calls (NinoRead, NinoWrite, bodies)
  impls.rs                   NinoRead / NinoWrite for primitives, strings, Option, Box, tuples, std collections
  types.rs                   the .NET value types as plain Rust types keeping their exact bits
  strings.rs                 string headers and the default encoding's writer
  simd.rs                    the only unsafe module: string transcoding into a Vec's spare capacity and Latin-1 /
                             UTF-16 decoding into a String; its tests compare every kernel with a plain one
  simd/x86.rs                x86_64 kernels (SSSE3, SSE4.1, AVX2, AVX-512) picked at run time
  simd/neon.rs               Arm64 NEON kernels
  defer.rs                   deferred class bodies (spec §7.10): bounded stack whatever the depth
  shared.rs                  reference tracking (spec §7.9)
  convert.rs                 conversions to uuid, rust_decimal, chrono, num-bigint, indexmap types (features)
  serde_impls.rs             serde for Nino's value types (feature serde)
nino-derive/                 nino-serialization-derive: #[derive(NinoRead, NinoWrite)], the same code as the templates;
                             what the wire format cannot express is a compile error naming the field
bench/                       nino-bench: benches/compare.rs (against prost, rmp-serde, bincode, postcard, serde_json),
                             benches/common/point.rs (the Point codecs compare.rs and blocks.rs share),
                             benches/text.rs (string transcoding, for back-to-back runs; not published),
                             benches/blocks.rs (lists of fixed-size elements, likewise)
tests/
  conformance.rs             the corpus over tests/conformance/types.rs: valid, malicious, truncations, mutational fuzzing
                             (NINO_FUZZ_ITERATIONS)
  common/harness.rs          what conformance.rs and generated.rs share (by #[path]): the allocator that measures a
                             read, the corpus's location and files, the fuzz's mutations, the large stack
  conformance/gen.py         writes conformance/types.rs from the corpus's layouts (edit this, not types.rs)
  conformance/types.rs       generated codecs of the corpus's types; json.rs: values as the corpus's JSON
  generated.rs               the corpus over tests/generated/nino_types.rs (nino-gen's output; do not edit), and
                             tests/generated/shapes.rs (tests/Nino.Codegen.Tests/Shapes.cs as nino-gen writes it)
  derive.rs                  the derive against the corpus's types, checked against their layouts
  compile_fail.rs            trybuild: each tests/compile_fail/*.rs must fail with its .stderr
                             (TRYBUILD=overwrite rewrites them)
  spec.rs                    the specification's worked examples (Appendix A, §7.10), byte for byte, over samples/
  samples/mod.rs             hand-written sample types of the specification's examples
  values.rs                  each built-in value: bytes, round trip, errors
```

Generated files: `tests/generated/*` is rewritten by `NINO_UPDATE_GENERATED=1 dotnet test tests/Nino.Codegen.Tests`,
`tests/conformance/types.rs` by `tests/conformance/gen.py`.

## Rust runtime (runtimes/rust)

- Security: the reader takes hostile input. No input may panic it, read out of bounds, recurse without a bound or
  allocate out of proportion to the data. The crate is `#![deny(unsafe_code)]` and denies `indexing_slicing`,
  `unwrap_used`, `expect_used`, `panic` and `arithmetic_side_effects` (lib.rs): use `get`, `checked_*`/`wrapping_*` with
  a comment saying why it cannot wrap, and errors. The one module allowed `unsafe` is `src/simd.rs` (with its submodules
  `simd/x86.rs` and `simd/neon.rs`, a kernel per instruction set): the string writer's transcoding, and the reader's
  decoding of Latin-1 and UTF-16 into a `String`'s spare capacity (`from_utf8_unchecked` only on what the decoder itself
  writes from code units, so valid UTF-8 by construction; unpaired surrogates become U+FFFD). The reader's bounds checks
  stay safe code: an unsafe decoder takes a `&[u8]` already taken from the input. Every block has a `// SAFETY:`
  comment, its tests compare every block kind and decoder with a plain one for every length up to 256 and random input,
  and the `rust-miri` job in `.github/workflows/runtimes.yml` runs them under Miri (x86_64 with SSE2 only, SSSE3,
  SSE4.1, AVX2 and AVX-512, Arm64, s390x). A kernel for an instruction set stays only where it measured faster on the
  CPUs it targets (`cargo bench -p nino-bench --bench text`, by class and length, and `--bench compare`), with the
  others kept as they were. Add `unsafe` only for a measured win, and only there.
- Allocation: a collection reserves `r.capacity::<T>(count)`, never `count` itself: the per-read budget (16 bytes per
  input byte plus 4 KiB) stops nested collections each pre-allocating against the same bytes (the fuzz found that).
  Check a count against the bytes left (`length(min_size)`) before reading elements.
- Depth: class bodies recurse up to `INLINE_DEPTH` and are deferred past it, as .NET does; structs count toward the
  limit through `enter_struct`/`exit_struct`. Never add a recursive path that skips them.
- Stricter than .NET, never looser: reject what .NET never writes (bools other than 0/1, out-of-range dates, decimal
  flags, non-minimal BigInteger, raw padding, duplicate keys, malformed sections), and read everything it writes.
  A new check goes in README.md's "Stricter than .NET" list.
- Performance: the generated (and derived) code is what the templates write: a run of fixed-size members is one
  bounds check (`r.array::<N>()`, `w.reserve(n)`), `#[inline]` on every small codec, and rare or large paths
  (`latin1_wide`, UTF-16 decoding, building an `Error`) `#[inline(never)]`. Compare with `cargo bench -p nino-bench`
  back to back on one machine; never publish the numbers.
- The derive (`nino-serialization-derive`, in `nino-derive/`) generates the same code as the templates; anything the
  wire format cannot express is a compile error naming the field, with a case in `tests/compile_fail/`. Give a new
  attribute a corpus or derive test.
- Before committing: `cargo fmt --all --check`, `cargo clippy --workspace --all-features --all-targets -- -D warnings`
  and `cargo test --workspace --all-features` with `NINO_CORPUS` set (the conformance, derive and fuzz tests skip
  without a corpus). `tests/conformance/types.rs` is generated by `tests/conformance/gen.py`; edit the generator.
  Builds share one `CARGO_TARGET_DIR`: Rust targets are large.
