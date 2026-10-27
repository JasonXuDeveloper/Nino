---
name: wire-format
description: "Everything a change to Nino's bytes must update - the spec (docs/spec/wire-format.md), the conformance corpus and its version, the Go, Rust and TypeScript runtimes, nino-gen's templates and the committed generated code, the Unity Burst kernels, every golden-bytes test, byte examples in the docs and the Wire-Format trailer. Use when a change alters what Nino writes or accepts for any type, or when a golden-bytes or conformance test fails. Triggers: wire format, bytes change, header, encoding, layout, golden bytes, GoldenBytesTests, BurstKernelTests, conformance corpus, corpusVersion, docs/spec, Wire-Format trailer, 线格式, 字节格式, 编码, 兼容性."
metadata:
  internal: true
---

# Changing the wire format

The bytes are the contract between .NET, Unity, Go, Rust, TypeScript and every program that stored them. A change to
them is deliberate, complete in one landing, and called out.

## Before

- Ask whether the owner wants it: a wire-format change nobody asked for is the owner's decision.
- Compare candidate encodings by size and speed in every runtime (a decoder that is fast in .NET but branchy in Go or
  TypeScript is not a win), and keep the format canonical: one encoding per value, so readers can reject the rest.

## Everything that changes with it

| Place | What |
|---|---|
| `docs/spec/wire-format.md` | the encoding, its examples (Appendix A), §9 for what readers must reject |
| `src/Nino.Core` | writer and reader, every fast path (`optimize`, `platforms`: x64 ISAs, Arm64, no SIMD, big-endian guards) |
| `src/Nino.Generator` | the emitters that inline the encoding (`audit-generated-code`) |
| `src/Nino.Unity/Packages/com.jasonxudeveloper.nino/Burst/NinoBurst.cs` | the Burst kernels, and `Assets/Test/Runtime/Burst/BurstKernelTests.cs`' golden bytes |
| `conformance/` | regenerate (`dotnet run --project conformance/Nino.Conformance -c Release -- --out conformance/corpus`), new valid and malicious cases for the new encoding's edges, `corpusVersion` when the corpus layout or schema changes |
| `runtimes/go`, `runtimes/rust`, `runtimes/typescript` | reader, writer, fast paths, their tests and fuzzers; each reads the new corpus |
| `src/Nino.Codegen/Templates` | the generated readers and writers; regenerate the committed output (`NINO_UPDATE_GENERATED=1 dotnet test tests/Nino.Codegen.Tests`) |
| golden-bytes tests | `tests/Nino.UnitTests/Serialization/GoldenBytesTests.cs`, `tests/Nino.Fuzz/Golden`, Unity's, any test with expected hex |
| docs | byte examples in README, `docs/`, the runtimes' READMEs and the nino.docs pages (`audit-docs`) |
| benchmarks | sizes shown in reports; the old-main baseline for the A/B (`benchmark`) |

Grep for the old bytes in every form (hex with and without spaces, C# byte arrays, Go and Rust literals) before
calling it done: the Unity golden bytes were missed once and turned CI red after the landing.

## Proof

- The conformance corpus verifies in .NET, Go, Rust and TypeScript; every runtime round-trips every valid case and
  rejects every malicious case and truncation.
- The fuzzers (`audit-security`) and the full unit test matrix (`verify`), Unity's run included.
- Runner A/B against the previous main in every runtime (`benchmark`): the new encoding must not cost speed.

## Commit

One landing for the whole change (a main where .NET and Go disagree is never pushed), with a `Wire-Format:` trailer
naming the bytes that change and for which types, and the version it ships in. The docs site's changelog gets a "Wire
Format" entry first in its release section.
