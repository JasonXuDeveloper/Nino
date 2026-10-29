---
name: audit-runtimes
description: "Audit Nino's runtimes outside .NET (runtimes/go, runtimes/rust, runtimes/typescript) and nino-gen (src/Nino.Codegen), which writes code for them - safety on hostile input, speed against each language's competitors, SIMD beyond strings, allocation, idiomatic API, parity with .NET's bytes and limits, and the templates against the hand-written samples. Use when changing a runtime or a template, when a runtime is slower than a competitor or than the previous main, or for a runtimes review. Triggers: runtimes/go, runtimes/rust, runtimes/typescript, nino.ts, src/Nino.Codegen, nino-gen, Templates/*.sbn, NINOGEN, cargo, go test, npm test, wasm, benchstat, criterion, Go 运行时, Rust 运行时, TS 运行时, 代码生成."
metadata:
  internal: true
---

# Runtimes audit

Each runtime's rules are in its `AGENTS.md` (`runtimes/go`, `runtimes/rust`, `runtimes/typescript`) and nino-gen's in
`src/Nino.Codegen/AGENTS.md`: read the one you touch in full. This skill is the checklist across them.

## Parity with .NET

- The same bytes for every value (the conformance corpus round-trips), the same rejections (every malicious case and
  truncation), the same depth counting and deferral, the same caps on lengths and allocations (`audit-security`).
- Stricter than .NET is allowed, looser never; each stricter check is listed in the runtime's README.
- A C# feature users can declare is supported by each runtime and by nino-gen's templates, or reported as NINOGEN002
  naming the type, never generated code that reads differently.

## Safety

No input may panic, throw an unexpected error, read out of bounds, recurse without a bound or allocate out of
proportion (Go: sticky errors, no `recover`; Rust: the crate's lints, `unsafe` only in `src/simd.rs` with SAFETY
comments and Miri; TypeScript: bounds before allocating, `Map` for keys from the data). Fuzzers run on every change
to a read path.

## Speed

- Each runtime leads every case of its `bench` against its competitors, on x64 and Arm64, judged per CPU on runners
  against the main-vs-main floor (`benchmark`). A bench that times one input over and over is not a fair lead: its
  parse cases must rotate their inputs (`benchmark`, "Adding a case").
- SIMD beyond strings where it measures faster (primitive blocks, bool validation, fixed-size records, transcoding),
  with the portable path kept and tested (Go `purego`, Rust without the features, TypeScript without WebAssembly).
- Hot paths: one bounds check per run of fixed-size members and per loop (the idioms per language are in
  `optimize`, "Bounds checks"), no reflection or boxing outside polymorphic slots, rare
  paths out of line (`//go:noinline`, `#[inline(never)]`), writing a value free of allocations.

## Templates (nino-gen)

- Logic in the IR (`Model/`) and each target's C# (`Targets/`), never in the templates, which only assemble text;
  a decision about the bytes goes into the IR once, for every language.
- What the hand-written samples do, the templates write (`runtimes/go/internal/sample`, Rust's conformance types,
  the TypeScript samples); the generated code is held to `audit-generated-code`.
- Every option is defined once in `Options.cs`; `build/Nino.Codegen.targets` and the integration tests agree.

- The code standard (`audit-code`: DRY, decoupled, clear) applies to these files, and redundancy in them
  (duplicated logic, dead code, two ways to do one job) is part of this audit; the sweep across layers is
  `audit-redundancy`.

## Proof

Each runtime's checks before committing (its AGENTS.md: vet/race/fuzz, fmt/clippy/test with `NINO_CORPUS`,
`npm ci && npm test`), the regenerated committed outputs (`NINO_UPDATE_GENERATED=1`), and runner A/B for speed.
