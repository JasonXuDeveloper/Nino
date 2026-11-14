---
name: audit-runtimes
description: "Audit Nino's runtimes outside .NET (runtimes/go, runtimes/rust, runtimes/typescript) and nino-gen (src/Nino.Codegen), which writes code for them - safety on hostile input, speed against each language's competitors, SIMD beyond strings, allocation, idiomatic API, parity with .NET's bytes and limits, and the templates against the hand-written samples. Use when changing a runtime or a template, when a runtime is slower than a competitor or than the previous main, or for a runtimes review. Triggers: Go runtime, Rust runtime, TypeScript runtime, TS runtime, runtimes/go, runtimes/rust, runtimes/typescript, nino.ts, src/Nino.Codegen, nino-gen, Templates/*.sbn, NINOGEN*, cargo, go test, npm test, wasm, benchstat, criterion, Go 运行时, Rust 运行时, TS 运行时, 代码生成."
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
- Take what a layout does not publish about the canonical layout (spec §5.2: a raw type's alignment, which sets a
  `T?[]` element's padding, §6.2) from Nino.Core's own algorithm, never from a rule of nino-gen's or a guess from
  offsets: nino-gen links `NinoLayoutCore.cs` (`Model/CanonicalLayout.cs`, `RawStructModel.Align`), and Rust's
  `nino::Raw::ALIGN` is that alignment (Int128 16). Why: r104 found a sequential-only rule and an offset guess capped
  at 8 that missed value tuples' auto layout and Int128: `Box<Cell?[]>` of a struct holding a `(byte, short)` wrote a
  14-byte stride where .NET writes 16 in all three languages, and Rust's `RawNullables<i128>` 24 where .NET writes
  32. Pin such a value against bytes .NET writes (`Shapes.Strides`: ModelTests, and each runtime's shapes test), not
  a round trip, which agrees with itself.
- Teach nino-gen every .NET type that is given an existing encoding, and prove it with a twin: nino-gen resolves an
  encoding against the member's .NET type by name (`CodegenModel.Value` for `nullable`, `CodegenModel.Raw` for a
  nullable in raw memory, `ArgumentEncoding` and `NullableStride` for type arguments, which no layout describes), and
  so does Rust's `tests/conformance/gen.py`, so the same encoding under a new type name has no form there. Why:
  `NinoNullable<T>` is published as `T?` (`nullable(raw(n))`, the stride of `T?[]`), and nino-gen left every type
  holding one out of all three languages (NINOGEN002) while `nino-gen inspect` failed with "has no decodable form"
  (#285; `CodegenModel.IsNullable`). The twin is the same sample declared both ways (the corpus's `NinoNullables` and
  `NullableTwins`, Shapes.cs' `NinoNullableArgs` and `NullableArgs`), whose IR
  (`ModelTests.NinoNullableIsTheValueOfNullable`), generated members and codecs
  (`TargetTests.NinoNullableIsGeneratedAsNullable`), inspect tree and bytes are compared, with a corpus case written
  from one and read as the other (`ninonullables-from-nullable`). Inside a plain struct copied as raw memory the two
  are not the same bytes (the presence is a whole `T`, the value at `T`'s size): TypeScript and Go read the offsets
  the layout gives, Rust's `Option<T>` in raw memory only the layout of `T?`, so Rust leaves a `T` wider than its
  alignment out there (`TargetTests.RustLeavesOutANinoNullableWiderThanItsAlignmentInRawMemory`).
- Resolve a raw field's type from the layout when the name it publishes is canonical rather than declared:
  `DateTimeOffset` publishes `offsetMinutes` (an int at 0) and `dateTime` (at 8), which no runtime declares (.NET 10
  `_offsetMinutes`, .NET 8 a short), so reflection found nothing and nino-gen left every type holding one out
  (`CodegenModel.FieldType`). A pointer field is nuint (8 bytes, zero-extended), not an unsupported reference.
  TypeScript keeps NINOGEN002 for a `DateTimeOffset` anywhere: its `Date` drops the tick and writes the kind as UTC
  (r104: bcl-raw-offset wrote `dc48` where .NET writes `dc08`). Lock such a mapping in ModelTests against bytes .NET
  writes (`HostDependentLeavesAreWhereNinoCoreWritesThem`) and with a corpus case every runtime reads.
- Type a value as nullable wherever .NET can write null there: a set's elements may be null (`HashSet<string>`
  holds one null: Go `*string`, Rust `Option<WideString>`/`Option<Utf8>`, TypeScript `string | null`), only a
  dictionary's keys never are (.NET throws on a null key and `NinoThrow.NullKey` rejects one on read). Why: r129 found
  nino-gen's Rust target typing set elements as map keys (`IndexSet<WideString>`), which failed every .NET
  `HashSet<string>` holding null (corpus case `collections-set-null`, `TargetTests.SetStringsMayBeNullDictionaryKeysNever`).
- nino-gen refuses what Nino.Core no longer serializes even in a layout an older build published
  (`System.Numerics.Vector<T>`: `CodegenModel.IsHostSizedVector`, `Shapes.OldVector`).
- A C# feature users can declare is supported by each runtime and by nino-gen's templates, or reported as NINOGEN002
  naming the type, never generated code that reads differently.
- A rename of a runtime's API that no release carries gets no alias (`audit-naming`, "Output and applying").
- Each runtime offers .NET's API shapes in its own idiom, under the same concept names: a new value
  (`Serialize`), memory the caller owns and reuses (`Serialize(value, bufferWriter)` with a `NinoArrayBufferWriter`:
  TypeScript `serializeInto` with a `NinoWriter` and `reset`, or a `Uint8Array`; Go `Append` and `Writer.Reset`; Rust
  `serialize_into`), and `Exact` reads. Why: the owner wants them "the same as C#" in every runtime, and in Node a new
  `ArrayBuffer` per value adds up to full GCs (`benchmark`, "Keep only the running serializer's data live").
  Reading into an existing value (`Deserialize(data, ref value)`) is TypeScript `deserializeInto` (`NinoCodec.readInto`),
  Go `DeserializeInto` (`ReadXInto`) and Rust `deserialize_into` (`NinoRead::read_into`), with one read body per type:
  the plain read builds a new value and reads into it (`readX(r, v = new X())`, `ReadX` calling `ReadXInto`, Rust's
  struct `read` building `Default`), so a template change to a read path reaches both. The corpus tests read every
  valid case into the previous case's value and every hostile input into a valid value (same verdict as a fresh read).

## Safety

No input may panic, throw an unexpected error, read out of bounds, recurse without a bound or allocate out of
proportion (Go: sticky errors, no `recover`; Rust: the crate's lints, `unsafe` only in `src/simd.rs` with SAFETY
comments and Miri; TypeScript: bounds before allocating, `Map` for keys from the data). Fuzzers run on every change
to a read path. Rust has two fuzzers: the corpus mutation in `cargo test` (stable, CI) and coverage-guided
`runtimes/rust/fuzz` (cargo-fuzz, nightly, local): run the second for a read-path change too, with LeakSanitizer on
(a leak is a finding: `audit-security`, cyclic graphs). Round 103: its first run found that a cyclic `Shared` graph
leaked; after the arena per read, 11.1 million inputs of nino-gen's code and 6.3 million of the hand-written codecs,
nothing.

## Speed

- Each runtime leads every case of its `bench` against its competitors, on x64 and Arm64, judged per CPU on runners
  against the main-vs-main floor (`benchmark`). A bench that times one input over and over is not a fair lead: its
  parse cases must rotate their inputs, and each serializer runs with only its own data live (`benchmark`, "Adding a
  case").
- SIMD beyond strings where it measures faster (primitive blocks, bool validation, fixed-size records, transcoding),
  with the portable path kept and tested (Go `purego`, Rust without the features, TypeScript without WebAssembly).
- Hot paths: one bounds check per run of fixed-size members and per loop (the idioms per language are in
  `optimize-runtimes`, references/blocks.md), no reflection or boxing outside polymorphic slots, rare
  paths out of line (`//go:noinline`, `#[inline(never)]`), writing a value free of allocations.

## Templates (nino-gen)

- Logic in the IR (`Model/`) and each target's C# (`Targets/`), never in the templates, which only assemble text;
  a decision about the bytes goes into the IR once, for every language.
- What the hand-written samples do, the templates write (`runtimes/go/internal/sample`, Rust's conformance types,
  the TypeScript samples); the generated code is held to `audit-generated-code`.
- Every option is defined once in `Options.cs`; `build/Nino.Codegen.targets` and the integration tests agree.
- `nino-gen layouts` (`LayoutsCommand.cs`) links the layout baseline's format and rules from
  `src/Nino.Generator/Common` (`LayoutBaseline.cs`, `LayoutRules.cs`): a rule changes there, for the analyzer, its code
  fixes and nino-gen at once, never in a copy here (`audit-analyzer`, the layout baseline).

- The code standard (`audit-code`: DRY, decoupled, clear) applies to these files, and redundancy in them
  (duplicated logic, dead code, two ways to do one job) is part of this audit; the sweep across layers is
  `audit-redundancy`.

- `nino-gen inspect` (`InspectCommand.cs`, `Inspect/`) interprets the IR; it never parses layouts or decides about
  the bytes on its own. Read UTF-16 text from the wire with `MemoryMarshal.Cast<byte, char>`, never
  `Encoding.Unicode.GetString`, which replaces lone surrogates with U+FFFD (spec §4.1 keeps them; `string-lone-surrogates`
  caught it). Raise the depth of `JsonDocument` (64) and `Utf8JsonWriter` (1000) for deep corpus cases, and run deep
  reads on a thread with a large stack. "Struct holds itself" is computed in four places (`CodegenModel.HoldsItself`,
  used by inspect, and each target's own, which filters by the types it supports): merge them when a target changes.
- A `[NinoType]` added to `tests/Nino.Codegen.Tests` (Shapes.cs, InspectTypes.cs) is in nino-gen's generated Shapes
  files unless every target leaves it out, and a keyed or version-tolerant one is in `LayoutsCommandTests`' baseline
  list: update that list with it.

## Proof

Each runtime's checks before committing (its AGENTS.md: vet/race/fuzz, fmt/clippy/test with `NINO_CORPUS`,
`npm ci && npm test`), the regenerated committed outputs (`NINO_UPDATE_GENERATED=1`), and runner A/B for speed.
