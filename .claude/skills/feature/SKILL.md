---
name: feature
description: "The definition of done for a Nino change - runtime, generator, diagnostics and code fixes, wire format, runtimes in other languages, tests, security, machine code, benchmarks, docs and commits. Use when building or reviewing any change before it lands; a fresh session working on a new request follows it. Triggers: any edit under src/, runtimes/ or conformance/, a new attribute, API, option, built-in type or NINO diagnostic, a bug fix, \"add\", \"implement\", \"support\", \"fix\", 加功能, 实现, 支持, 修复, 改一下."
metadata:
  internal: true
---

# Shipping a change

A change lands only with every part below that applies to it. Each part names the skill that owns its rules; read
that skill when the part applies, not only this list.

## 1. Runtime (`audit-core`, `optimize`, `audit-security`)

- The code standard (`audit-code`): one implementation with thin typed wrappers, one way per job, dependencies in
  the allowed direction; families (tuples, multi-dimensional arrays) come from `tools/CodeGen` templates, never by
  hand.
- The read and write paths stay as fast on every platform (`platforms`): rare or large paths out of line, small
  helpers `AggressiveInlining`, no `ref reader` / `ref writer` passed to a call that is not inlined; the machine code
  is checked with `python3 benchmarks/asm.py compare origin/main` and every method it lists is explained or fixed.
- Every check of the data throws `NinoDataException`; lengths are checked before allocating; recursion counts depth;
  hash tables Nino fills count collisions (`audit-security`).
- A path through a runtime's internals follows `runtime-internals` (startup check, public API fallback, a
  `FastPaths` row in `tests/Nino.RuntimeTests`).
- Public API: XML docs, trim/AOT annotations where reflection is involved, no break without the owner.

## 2. Generator (`audit-generator`, `audit-generated-code`)

- Incremental rules: equatable symbol-free models, a tracking name and caching assertion per stage, no diagnostics
  from the generator (the analyzer reports).
- A new way to declare a type (kind, attribute option, place) adds a dimension to
  `tests/Nino.UnitTests/Corner/ShapeMatrixTests.cs`.
- Names the model may leave empty go through `TypeModel.Need`; what a type gets is decided by the model's rules.
- The generated text reads like hand-written code and compiles to the same machine code as before unless the change
  is meant to change it (`audit-generated-code`).

## 3. Diagnostics and code fixes (`audit-analyzer`)

Every misuse that compiles and every declaration that costs gets a NINO rule with a message naming the member and the
fix; a mechanical fix gets a code fix; tests for each (positive, negative, false-positive guard, fix output);
`AnalyzerReleases.Unshipped.md`, `docs/diagnostics.md`.

## 4. Wire format (`wire-format`)

Anything that changes the bytes: the spec, the conformance corpus, the Go, Rust and TypeScript runtimes, nino-gen's
templates, every golden-bytes test (Unity's too), a `Wire-Format:` trailer.

## 5. Other runtimes and nino-gen (`audit-runtimes`)

A feature users can declare in C# is read and written by the Go, Rust and TypeScript runtimes and by nino-gen's
generated code, or nino-gen reports NINOGEN002 for it; their own rules are in `runtimes/*/AGENTS.md` and
`src/Nino.Codegen/AGENTS.md`.

## 6. Tests

- Behavior, in the folder of `tests/Nino.UnitTests` that owns the area (`tests/AGENTS.md`); a regression test that
  fails before each fix.
- Hostile input for every new read path (`StrictReadTests`, `HashFloodingTests`, `UntrustedModeTests`, the nesting
  tests) and a fuzz target or probe when it reads new shapes (`tests/Nino.Fuzz`).
- The matrix of `verify`: the netstandard builds, interceptors off, the Roslyn 4.8 generator, no SIMD, Mono, Arm.
- Tests assert exact exception types (`Assert.ThrowsExactly`).

## 7. Benchmarks (`benchmark`)

A hot-path change is measured on GitHub runners (the A/B workflows), never published from a local run; a new
type or feature users will compare gets a benchmark case (and a MemoryPack / MessagePack / protobuf-net column where
those can express it).

## 8. Docs (`audit-docs`)

Nino's README, README.zh.md, `docs/` and the nino.docs site (English and Chinese pages, the changelog), the public
skill `skills/nino/SKILL.md`, the file map of each directory the change touches (`audit-layout`). Numbers only
through the synced benchmark data.

## 9. Commits and landing (`verify`)

Conventional Commits with a body that says why; the release-note trailer a user-facing change needs (AGENTS.md,
Rules); logic, docs and CI in separate commits; `verify` before anything reaches `main`. What the change taught (a
rule, a technique, a trap) goes into the skill that owns it.
