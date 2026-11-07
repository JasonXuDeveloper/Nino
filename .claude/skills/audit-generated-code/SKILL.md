---
name: audit-generated-code
description: "Audit what the generators write - the C# formatters, registrations and interceptors Nino.Generator emits, and the TypeScript, Go and Rust nino-gen writes - for readable text (no dead locals, no repeated qualified names, values said once), for size (build time, package size), and for the machine code it compiles to (inlined to one method, no spills, block copies and vector paths taken). Use when changing an emitter or a template, when generated code is slow, large or hard to read, or when a type's serializer is slower than a hand-written one. Triggers: Generation/Emit, *.g.cs, EmitCompilerGeneratedFiles, generated code, generated formatter, global::, alias, IL, Templates/*.sbn, nino_types.go, nino_types.rs, nino_types.ts, NINO_UPDATE_GENERATED, 生成代码, 生成的代码, 生成产物."
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
  message names a type without `global::` ("cannot construct __NinoT" shipped that way). The generic member
  formatters go through the same rule (`__NinoF0`...; round 128 removed `FormatterNames`' own rule, any name over 48
  characters, used once or not); a non-generic formatter stays its type name, which is what a reader wants. The
  generated types lose `global::NinoGen.` there (every body is in that namespace), and a formatter names its own
  twins `__Top` / `__Shallow`. A text-based alias matches whole names at both ends: the struct's own declaration
  once became `struct __NinoF0` when a self-holding type's formatter was a candidate. Member formatters are numbered in member order and
  declared without `global::NinoGen.` (alias declarations are inside the namespace too); the inlining attributes are
  `[__NinoInline(AggressiveInlining)]` through `using static ...MethodImplOptions` (round 129: 4% of the unit tests'
  output was the second alias's name).
- **Reads take the reader first**, every generated read included: round 128's rewriter swapped `(ref X, ref Reader)`
  pairs only, and the generated `ReadMembers(T value, ref Reader reader)` kept the old order until round 129. A
  signature generated code repeats is written once in `Signature`.
- **Layout**: a statement per line, as a formatter would leave it (no `{ int id = ...; if (...) { ... } }` on one
  line); a method with nothing to decide is one line (`=> NinoSubtypes<T>.Serialize(...)`: the dispatch of a base
  with no subtype in the assembly, nearly all of them).
- **No line that does nothing**: no identity copy, no unread local, no empty method, no `x = default` the
  constructor gives, no parameter nobody reads. Each local also counts toward the JIT's tracking limit.
- **Say a value once**: constants in place, setup that does not change per member written once, a table declared once
  per type. Leaner IL is not by itself faster: raw block offsets written as `n * SizeOf<T>()` instead of a sum of
  `SizeOf<T>()` calls made the PolymorphicFooBase dispatch inline every subtype's `BodySize` (x64 76 stack accesses
  to 1, Arm64 57 to 9), yet PolymorphicClass and PolymorphicClassArray serialize measured 1.2-2.1% slower on EPYC
  7763 and Neoverse-N2 in two runner A/Bs, so the sum stayed. Take the decision from runners, not the listing.
- **Parentheses only where precedence needs them**; layout as a formatter would leave it. Blank lines are
  `CodeWriter`'s: an emitter asks for one with `Line()` between the things it writes, and the writer drops it after an
  opening brace, before a closing one and next to another (round 103: 65 of 533 unit-test files had one there,
  mostly interface formatters whose body is only their entry points). A cast of a constant is the constant
  (`Remaining >= 8`, not `>= (uint)(8)`; the parenthesized form stays for a size with `SizeOf<T>()` in it).
- **Names a reader can follow**: locals after the member they hold; numbered helpers numbered by what they are for,
  so one shape's read and write carry one number (nino-gen's Go `readV3`/`writeV3`/`decodeV3`/`encodeV3` and
  TypeScript `read$3`/`readInto$3`/`write$3`, `CodegenTarget.ShapeNumber`; until round 128 one counter ran across
  all of them and `read$0` sat next to `readInto$1`).
- **Generate once, refer to it**: a form that needs code another file generates names that code instead of copying
  it; a family that differs only by type comes from one template. A twin forwards to its formatter's member
  (`default(Self).X(...)`, AggressiveInlining both) rather than repeating its body: the `PrefetchMembers` loop was
  written up to three times per type (538 copies, 4.7% of the unit tests' output) until round 128, and with the
  forwarders `NinoPrefetch.Ahead`'s FullOpts listings stayed byte for byte the same. A loop every formatter would
  write the same way is Nino.Core's, and the interface takes what one value does: round 129 moved the last copy of
  that loop (285 copies) into `NinoPrefetch.Members`, `INinoPrefetchFormatter.PrefetchMembers(object value)` per
  element, 2.6% of the unit tests' output (3,814,657 to 3,714,852 bytes) with `Ahead`'s listings unchanged (how the
  loop must be written for that: `optimize`, `references/jit.md`, "Locals"). A switch whose cases end the
  same way ends it once after the switch (`default: continue;` for the keys it skips; the keyed fallback loop).
  One act is one member, taking a struct by reference: a block struct's collections and its top-level and member
  writes all call `WriteFixed(in T value, ref byte destination)` (`INinoBlockFormatter` extends
  `INinoFixedSizeFormatter`; round 101 merged `BlockSize`/`WriteBlock` into it). A by-value `WriteFixed` that the
  block write forwarded to made the block loops load every field before storing any (`NinoBlockCollections.WriteValue`,
  `WriteEach` of Waypoints changed); by reference, the listings are the old by-reference `WriteBlock`'s.
- **Size**: output bytes per type stay flat or shrink (build time and IL size grow with it); measure with
  GeneratorBenchmark and the package size of a sample.

- The code standard (`audit-code`: DRY, decoupled, clear) applies to these files, and redundancy in them
  (duplicated logic, dead code, two ways to do one job) is part of this audit; the sweep across layers is
  `audit-redundancy`.

## The machine code

- Everything a top-level call reaches inlines into one method under one budget (`optimize`): the generated
  read and write bodies keep rare paths (deferral, pooling, growth, polymorphic fallback) in calls out of line and
  the common path inline; a run of fixed-size members is one bounds check and one block copy where the layout allows.
- No indirect call in a generated read or write path where a direct one exists (`optimize`, the owner's rule): a
  member's formatter is named directly (a generic type's formatter takes its arguments' formatters), and
  `CachedFormatter<T>` only for a method's type parameter or a `[NinoType]` generated in another assembly (a type
  that holds itself gets a generated nesting struct, `optimize`); read the IL2CPP C++ as well as the Tier1 listing (`optimize`, references/unity.md); look for `call [rax+0x..]`, `call [r11]` and `call rax` in the Tier1
  listing and explain each.
- Check the Tier1 listing of generated serializers for the benchmark types on each ISA (`benchmarks/asm.py`), and
  compare with the hand-written equivalent where one exists: generated code is not slower than what a user would
  write by hand.
- nino-gen output follows each runtime's performance and safety rules (`runtimes/*/AGENTS.md`): one bounds check per
  run of fixed-size members, bounds checked before allocating, depth counted as .NET does, rare paths out of line.
- No dispatch per element: every loop nino-gen writes calls the element's code directly (no func value, interface
  call or closure per element; `optimize-runtimes`, references/dispatch.md).

## Proof

- Text only (names, aliases, parentheses, layout): the IL of the generated methods is identical between base and
  head: build `benchmarks/Nino.Benchmark` (and `tests/Nino.UnitTests -f net10.0` for shapes the benchmark lacks, such
  as keyed types) on both, `ilspycmd -il <dll> > side.il` (a dotnet tool), and `diff`: empty. The test DLL holds the
  tests too, so drop the `// Method begins at RVA` lines and compare the IL of each `NinoGen` class; a changed
  test moves every RVA after it. `diff -rB` of the two `generated/` folders shows what changed beyond blank lines. (`-p:DebugType=none`
  to compare the DLLs' bytes fails: Nino.Core embeds its sources.) An intended IL change (a parameter order) is proven by comparing per method (split at `.method` and `// end of method`): only the methods that name it differ (round 129: 418 of the unit tests', all calling or being `ReadMembers`). Save the base side's `.il`, generated files and
  `asm.py dump` JSON once, then compare each commit against them.
- IL changes: the Tier1 listings of the benchmark types identical or better (`asm.py compare`), else runner A/B
  (`benchmark`). A whole-suite `asm.py diff` also lists methods the change cannot reach (PGO varies run to run):
  compare the listings of the methods the changed generated code compiles into (their names in the dump), and
  explain those.
- nino-gen: regenerate (`NINO_UPDATE_GENERATED=1 dotnet test tests/Nino.Codegen.Tests`), then the TypeScript, Go and
  Rust checks against the corpus (`src/Nino.Codegen/AGENTS.md`).
- Bytes never change unless that is the change (`wire-format`).
