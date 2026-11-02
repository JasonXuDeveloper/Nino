---
name: optimize-runtimes
description: "Nino's optimization techniques in Go, Rust and TypeScript, measured and kept - V8's 460-byte inlining limit and cfg(target_arch), GC and typed arrays in V8, bounds checks (check_bce, chunks_exact, DataView), Rust's fixed-size blocks, the string kernels (Rust AVX2/AVX-512/NEON, Go assembly, wasm), direct calls instead of func values, closures and dyn dispatch in collection loops, reuse branches off the fresh read path, set and key checks, and Go's block checks. Use when writing or reviewing a hot path in runtimes/ or in nino-gen's templates, when a Go, Rust or TypeScript case got slower, or when a new trick is found there (record it here). Triggers: check_bce, panic_bounds_check, go tool objdump, func value, dyn Trait, monomorphized, V8, V8 inlining, --trace-gc, typed arrays, Float64Array, FIXED_SIZE, chunks_exact, strings_wire, simd.rs, NEON, llvm-mca, Go 优化, Rust 优化, TS 优化, 运行时性能."
metadata:
  internal: true
---

# Optimization techniques outside .NET

The techniques that made the Go, Rust and TypeScript runtimes and nino-gen's generated code faster or slower. The
.NET and Unity techniques, reading .NET machine code and the prefetch design of every runtime are `optimize` (prefetch:
its references/prefetch.md). How to measure on runners is `benchmark` (runtime A/B), where the next win is
`audit-perf`, each runtime's rules are `runtimes/*/AGENTS.md` and `audit-runtimes`.

## The techniques, by topic

Each line is the rule; the reference file holds it in full with why and the runner numbers. Read the file for the
topic you touch before changing code there: several of these looked like wins locally and lost on one CPU.

- **Inlining and GC** ([references/inlining-gc.md](references/inlining-gc.md)): keep a TypeScript hot helper under
  V8's 460 bytes of bytecode (split the rare path out); put a shape that helps one Rust target behind
  `cfg(target_arch)`; read `--trace-gc` before changing a TypeScript write loop that got slower (it is often GC of
  `ArrayBuffer`s); never touch `.buffer` of a typed array of 64 bytes or less, and give larger ones a shared slab;
  typed arrays stay nino-gen's opt-in.
- **Bounds checks and blocks** ([references/blocks.md](references/blocks.md)): Go slices once and indexes with
  constants (`check_bce`), Rust takes fixed arrays and `chunks_exact`, TypeScript indexes inside a loop bounded by
  `length` and reads through one `DataView` call; every fixed-size Rust shape gets `FIXED_SIZE` with
  `decode_fixed` / `encode_fixed` and no hand-written shuffle kernel; Go checks blocks of 8- or 16-byte range-limited
  values four a step, 4-byte values one at a time, each type's check in a non-generic `//go:noinline` function, bools
  at a fixed offset four elements a step; UTF-16 copied as memory on little-endian hosts.
- **Strings** ([references/strings.md](references/strings.md)): size the Rust AVX2 writer's units by the whole window;
  hand a stopped Latin-1 window to the UTF-16 pass; keep Arm64's scalar `short_wide_inline` for short text and model a
  vector attempt with llvm-mca over the executed stream first; give each arm of a per-char decoder its own end check;
  mixed-text serialize trails memcpy-based competitors by design; no decoded-key cache or windowed `TextDecoder` in
  TypeScript, and vary the bench data before chasing JSON's leads.
- **Dispatch and reuse** ([references/dispatch.md](references/dispatch.md)): call the element's code directly in every
  collection loop, never through a func value, interface method or closure (the owner's rule; Go writes its loops,
  class bodies open in place, Rust sealed forwarders `#[inline]`); keep the reuse branches of read-into paths off the
  fresh read path: share the body, not the dispatch.
- **Sets and dictionary keys** ([references/sets-and-keys.md](references/sets-and-keys.md)): check repeats in an
  open-addressing table of the reader's own with a probe bound, never a sort or a Go map first; TypeScript's
  `NinoKeyGuard` counts the final table only, and a fresh set keeps its `add` loop.

## Recording a technique

A technique found and measured, faster or slower, goes into the reference file of its topic as one bullet: the rule in
the imperative, why, then the evidence (case, CPUs, runner numbers, commit or run), extending the bullet that already
covers it. A new topic gets a file and a line above. A technique for .NET or Unity goes to `optimize`.
