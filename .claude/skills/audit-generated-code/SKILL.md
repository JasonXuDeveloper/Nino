---
name: audit-generated-code
description: "Audit what the generators write - the C# formatters, registrations and interceptors Nino.Generator emits, and the TypeScript, Go and Rust nino-gen writes - for readable text (no dead locals, no repeated qualified names, values said once), for size (build time, package size), and for the machine code it compiles to (inlined to one method, no spills, block copies and vector paths taken). Use when changing an emitter or a template, when generated code is slow, large or hard to read, or when a type's serializer is slower than a hand-written one. Triggers: Generation/Emit, *.g.cs, EmitCompilerGeneratedFiles, generated formatter, Templates/*.sbn, nino_types.go, nino_types.rs, nino_types.ts, NINO_UPDATE_GENERATED, 生成的代码, 生成产物, 生成代码质量."
metadata:
  internal: true
---

# Generated code audit

Generated code is code users read (Go To Definition opens it) and the code their programs run. It is held to the
standard of the best hand-written code, and to `optimize` for its speed. The emitters are
`src/Nino.Generator/Generation/Emit` (C#) and `src/Nino.Codegen/Templates` with `Targets/` (TypeScript, Go, Rust).

## See it

- C#: build a test project with `-p:EmitCompilerGeneratedFiles=true` and read `obj/**/generated/Nino.Generator/`;
  read only files the build just wrote (the folder keeps files of forms no longer generated). Cover each shape:
  positional, version-tolerant and keyed types, structs (raw and member-wise), polymorphic roots, generics,
  constructors and factories, pooled types, reference tracking, `[NinoUtf8]`, custom formatters, interceptors.
- nino-gen: the committed outputs in `tests/Nino.Codegen.Tests` and the runtimes' `internal/sample` / corpus types.

## The text

- **Short names through one alias block**: a type named more than once goes through a file-level `using` alias, not
  `global::System...` in every line; never a plain `using` a user type could shadow. Every file is framed by
  `GeneratedCode.File`, and `GeneratedCode.WithAliases` rewrites a body's repeated names (the type's own as `__NinoT`,
  `Writer`, `Reader`, `NinoObject`, `NinoUnsafe`, ...) where the alias makes the file shorter: write full names in
  emitters and add a name to its table, never a second alias mechanism. It rewrites string literals too, so a
  message names a type without `global::` ("cannot construct __NinoT" shipped that way).
- **No line that does nothing**: no identity copy, no unread local, no empty method, no `x = default` the
  constructor gives, no parameter nobody reads. Each local also counts toward the JIT's tracking limit.
- **Say a value once**: constants in place, setup that does not change per member written once, a table declared once
  per type. Leaner IL is not by itself faster: raw block offsets written as `n * SizeOf<T>()` instead of a sum of
  `SizeOf<T>()` calls made the PolymorphicFooBase dispatch inline every subtype's `BodySize` (x64 76 stack accesses
  to 1, Arm64 57 to 9), yet PolymorphicClass and PolymorphicClassArray serialize measured 1.2-2.1% slower on EPYC
  7763 and Neoverse-N2 in two runner A/Bs, so the sum stayed. Take the decision from runners, not the listing.
- **Parentheses only where precedence needs them**; layout as a formatter would leave it.
- **Names a reader can follow**: locals after the member they hold.
- **Generate once, refer to it**: a form that needs code another file generates names that code instead of copying
  it; a family that differs only by type comes from one template.
- **Size**: output bytes per type stay flat or shrink (build time and IL size grow with it); measure with
  GeneratorBenchmark and the package size of a sample.

- The code standard (`audit-code`: DRY, decoupled, clear) applies to these files, and redundancy in them
  (duplicated logic, dead code, two ways to do one job) is part of this audit; the sweep across layers is
  `audit-redundancy`.

## The machine code

- Everything a top-level call reaches inlines into one method under one budget (`optimize`): the generated
  read and write bodies keep rare paths (deferral, pooling, growth, polymorphic fallback) in calls out of line and
  the common path inline; a run of fixed-size members is one bounds check and one block copy where the layout allows.
- Check the Tier1 listing of generated serializers for the benchmark types on each ISA (`benchmarks/asm.py`), and
  compare with the hand-written equivalent where one exists: generated code is not slower than what a user would
  write by hand.
- nino-gen output follows each runtime's performance and safety rules (`runtimes/*/AGENTS.md`): one bounds check per
  run of fixed-size members, bounds checked before allocating, depth counted as .NET does, rare paths out of line.

## Proof

- Text only (names, aliases, parentheses, layout): the IL of the generated methods is identical between base and
  head: build `benchmarks/Nino.Benchmark` (and `tests/Nino.UnitTests -f net10.0` for shapes the benchmark lacks, such
  as keyed types) on both, `ilspycmd -il <dll> > side.il` (a dotnet tool), and `diff`: empty. (`-p:DebugType=none`
  to compare the DLLs' bytes fails: Nino.Core embeds its sources.) Save the base side's `.il`, generated files and
  `asm.py dump` JSON once, then compare each commit against them.
- IL changes: the Tier1 listings of the benchmark types identical or better (`asm.py compare`), else runner A/B
  (`benchmark`). A whole-suite `asm.py diff` also lists methods the change cannot reach (PGO varies run to run):
  compare the listings of the methods the changed generated code compiles into (their names in the dump), and
  explain those.
- nino-gen: regenerate (`NINO_UPDATE_GENERATED=1 dotnet test tests/Nino.Codegen.Tests`), then the TypeScript, Go and
  Rust checks against the corpus (`src/Nino.Codegen/AGENTS.md`).
- Bytes never change unless that is the change (`wire-format`).
