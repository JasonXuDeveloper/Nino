---
name: audit-generated-code
description: "Audit what the generators write - the C# formatters, registrations and interceptors Nino.Generator emits, and the TypeScript, Go and Rust nino-gen writes - for readable text (no dead locals, no repeated qualified names, values said once), for size (build time, package size), and for the machine code it compiles to (inlined to one method, no spills, block copies and vector paths taken). Use when changing an emitter or a template, when generated code is slow, large or hard to read, or when a type's serializer is slower than a hand-written one. Triggers: Generation/Emit, *.g.cs, EmitCompilerGeneratedFiles, generated code, generated formatter, global::, alias, IL, BodyEmitter, DispatchEmitter, FormatterTraitsOf, EndRegion, PatchLength, CodeWriter, WithAliases, Templates/*.sbn, nino_types.go, nino_types.rs, nino_types.ts, NINO_UPDATE_GENERATED, 生成代码, 生成的代码, 生成产物, 别名, 生成的读写路径."
metadata:
  internal: true
---

# Generated code audit

Generated code is code users read (Go To Definition opens it) and the code their programs run. It is held to the
standard of the best hand-written code, and to `optimize` for its speed. The emitters are
`src/Nino.Generator/Generation/Emit` (C#) and `src/Nino.Codegen/Templates` with `Targets/` (TypeScript, Go, Rust).
Each section below states its rules in one line; the measurements behind them, and the attempts that lost, live in
`references/` (linked where they apply). Record a new measurement in that file, in the form AGENTS.md gives ("Keep
what you learn"), and its one-line rule here.

## See it

- C#: build a test project with `-p:EmitCompilerGeneratedFiles=true` and read `obj/**/generated/Nino.Generator/`;
  read only files the build just wrote (the folder keeps files of forms no longer generated). With
  `-p:CompilerGeneratedFilesOutputPath=<dir>` (a global property: Nino.Core's PolySharp output lands there too)
  create `<dir>` first, or the build fails with CS0016 races, and pass `--no-incremental`, or an up-to-date build
  writes nothing (round 130). A feature off by default proves itself zero cost the same way: both trees of
  `tests/Nino.UnitTests` (613 files) and `benchmarks/Nino.Benchmark` (261) identical before and after. Cover each shape:
  positional, version-tolerant and keyed types, structs (raw and member-wise), polymorphic roots, generics,
  constructors and factories, pooled types, reference tracking, `[NinoUtf8]`, custom formatters, interceptors.
- nino-gen: the committed outputs in `tests/Nino.Codegen.Tests` and the runtimes' `internal/sample` / corpus types.

## The text

- **Short names through one alias block**: a type named more than once goes through a file-level `using` alias, not
  `global::System...` in every line; never a plain `using` a user type could shadow. Every file is framed by
  `GeneratedCode.File`, and `GeneratedCode.WithAliases` rewrites a body's repeated names (the type's own as `__T`,
  `Writer`, `Reader`, `NinoObject`, `NinoUnsafe`, ...) where the alias makes the file shorter: write full names in
  emitters and add a name to its table, never a second alias mechanism. It rewrites string literals too, so a
  message names a type without `global::` ("cannot construct __NinoT" shipped that way). The generic member
  formatters go through the same rule (`__NinoF0`...; round 128 removed `FormatterNames`' own rule, any name over 48
  characters, used once or not); a non-generic formatter stays its type name, which is what a reader wants. The
  generated types lose `global::NinoGen.` there (every body is in that namespace), and a formatter names its own
  twins `__Top` / `__Shallow`. A text-based alias matches whole names at both ends: the struct's own declaration
  once became `struct __NinoF0` when a self-holding type's formatter was a candidate. Member formatters are numbered in member order and
  declared without `global::NinoGen.` (alias declarations are inside the namespace too). Share the inlining flags as
  constants in the assembly's generated registry file: `[__MI(__IL.Inline)]` and
  `[__MI(__IL.NoInline)]` need only the attribute alias per file. Use the fully qualified BCL attribute
  if a formatter's type parameter shadows the flags type. The compiler folds the constants into the same method
  flags, with no helper calls.
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
- **nino-gen's Rust: per-type boilerplate is a crate macro**: what the template, the derive, `gen.py` and the benches'
  `Point` write the same for every type of a kind is a `macro_rules!` in `runtimes/rust/src/lib.rs` they all call
  (`sealed_read!`/`sealed_write!`, `raw_read!`/`raw_write!`), and doc comments on per-type items live in the macro
  that emits them (`type_consts!`: NINO_TYPE_ID, NINO_TYPE_NAME, NINO_LAYOUT). Round 129 took `tests/generated` from
  436 KB to 381 KB. A macro call's layout is rustfmt's: one line when it fits 120 columns, else an argument per line
  when each fits, else left on one line (rustfmt gives up on an overlong string), or `cargo fmt --check` fails.
- **Measured spelling rules**, one line each; the numbers and the attempts that lost are in
  [references/text-size.md](references/text-size.md) (read it before adding or renaming an alias, changing a
  qualified name, an interceptor or a registration line, or when output bytes per type grew):
  - Spell per-file aliases short (`__MI`, `__IL`, `__T`, `__C`, `__CS`, `__SCG`, `__NinoAt`); keep the fully
    qualified attribute where a type parameter shadows `__IL`.
  - Keep formatter and Core aliases inside `NinoGen` (outside, CS0576); never alias a constructed type (TypeSpec).
  - Let one namespace alias replace overlapping type aliases; match its prefix with the dot (`__C.` beside `__CS.`).
  - Write `__CS::` in generic selectors and dispatch; keep open generic partial bodies qualified (type parameters
    shadow aliases).
  - Add a name every file may repeat to `WithAliases`' table (one scan per name, one copy of the body however many
    pay); write an alias in the emitter only where it alone knows the alias is safe (`__CS::` beside type parameters).
  - Shorten qualifiers in interceptors and usage registrations, not location attribute arguments or registry
    literals; compare the consumer DLL's metadata, not only its IL.
  - Keep generic formatter registrations one attribute per line: a grouped list costs Roslyn's parse more.
  - Let raw stores infer their type, use `new(...)` where the line names the type, emit constant type IDs in place,
    and omit the empty prefetch member hook when Core has the object-only interface.
- **Size**: output bytes per type stay flat or shrink (build time and IL size grow with it); measure with
  GeneratorBenchmark and the package size of a sample. Measure the whole output, not one file: clear
  `obj/Release/net10.0/generated` first (it keeps files of forms no longer generated), then
  `cat generated/**/*.cs | wc -c` for `tests/Nino.UnitTests` and `benchmarks/Nino.Benchmark` (the realistic one).
  Round 129: 6,694 bytes per type in the benchmarks (99 types), 13% of it the interceptors, which are type names
  the compiler needs. Indentation was 14% of all bytes: a file that declares one namespace declares it
  file-scoped where the language allows (C# 10, `GeneratedCode.File`; Unity's C# 9 and the files that declare a
  second namespace, the interceptors' attribute and the partial accessors, keep the block), which took 4.4%
  (benchmarks) and 5.1% (unit tests) with the IL identical. Rejected with their numbers (benchmarks / unit tests),
  so nobody retries them blind: a `__NinoCore.` namespace alias for `global::Nino.Core.` (1.0% / 1.1%, a second
  spelling of one namespace per file; it later landed as `__C` once it replaced the reader and writer aliases
  instead of adding to them, references/text-size.md); dropping `[__NinoInline(AggressiveInlining)]` from the interceptors (1.1% /
  1.3%, and `optimize` keeps it on every small helper the budget must not drop); dropping their
  `// File.cs(line,column)` comments (0.8% / 1.6%, the only readable link to the call); NinoDirect overloads
  inferring `T` from the argument (1.0% / 1.2%, new public API); a non-generic `NinoSubtypes` for the dispatch's
  one-line forwarders (0.1% / 0.5%); one `[DynamicallyAccessedMembers]` instead of a `[DynamicDependency]` per
  measured struct (0.2% / 0.4%, and an annotation names no list of types); tabs (6.3% / 7.3% more: C# is indented with
  spaces). A value a `switch` case already holds is its variable: the registry's registration takes `(id)`, not the
  case's literal again (round 129). One act is one call that names each type once: a concrete type's formatter and
  handler are one `NinoTypes.Register<T, F>(id)` (`RegisterFixed`, `RegisterTop`, `RegisterTracked`), not
  `NinoFormatterCache<T>.TryRegister<F>()` plus `NinoPolymorphic.Register<T, F>(id)`: the registry 38% smaller in the
  unit tests, 32% in the benchmarks (2.3% / 2.0% of all output), its IL 22% smaller, and registration faster once the
  Nino.Core side calls nothing generic over the formatter (`optimize`, references/jit.md, Startup).
  Attribute strings cost emit time like code: the `[assembly: NinoTypeLayout]` strings (bd6a732b, f76fa0e0; each
  subtype repeats every inherited member, so a chain grows them quadratically) are 410 KB of GeneratorBenchmark's
  1.53 MB at 330 types and nearly half its PE (801 KB against 410 KB without them), and were most of its emit going
  from 0.42-0.44 s (1938da63) to 0.47-0.48 s (#262; local, serial emit of the dumped output, interleaved: 608 ms
  for 1938da63's text, 619 ms for main's without the layouts, 670 ms with them, 625 ms with each subtype's string cut
  to its own members). Shortening the published layout text is the owner's call (it is what the analyzer and
  nino-gen read); measure any new per-type attribute the same way.

- The code standard (`audit-code`: DRY, decoupled, clear) applies to these files, and redundancy in them
  (duplicated logic, dead code, two ways to do one job) is part of this audit; the sweep across layers is
  `audit-redundancy`.

## The emitter's own cost

What writing the output costs the generator (allocation, time, the emitters' IL and listings) is `audit-generator`'s:
read its [emit-text.md](../audit-generator/references/emit-text.md),
[emit-interceptors.md](../audit-generator/references/emit-interceptors.md) and
[pipeline-and-binding.md](../audit-generator/references/pipeline-and-binding.md) before changing `GeneratedCode`,
`CodeWriter`, `FormatterNames` or the interceptor emission.

## The machine code

- Everything a top-level call reaches inlines into one method under one budget (`optimize`): the generated
  read and write bodies keep rare paths (deferral, pooling, growth, polymorphic fallback) in calls out of line and
  the common path inline; a run of fixed-size members is one bounds check and one block copy where the layout allows.
- No indirect call in a generated read or write path where a direct one exists (`optimize`, the owner's rule): a
  member's formatter is named directly (a generic type's formatter takes its arguments' formatters), and
  `CachedFormatter<T>` only for a method's type parameter or a `[NinoType]` generated in another assembly (a type
  that holds itself gets a generated nesting struct, `optimize`); read the IL2CPP C++ as well as the Tier1 listing (`optimize`, references/unity.md); look for `call [rax+0x..]`, `call [r11]` and `call rax` in the Tier1
  listing and explain each.
- **Measured read-path rules**, one line each; the numbers and the attempts that lost are in
  [references/read-paths.md](references/read-paths.md) (read it before changing `BodyEmitter`'s reads or a
  formatter's traits in `FormatterTraitsOf`, or when a generated deserializer got slower or larger):
  - Guard the remaining length, then `ReadBlock`, for a complete raw group (no `TryReadBlock`).
  - Omit `EndRegion` for a version-tolerant member region whose formatters all preserve read context: a formatter
    that queues and registers nothing, or a wrapper whose inner formatters do (not dictionaries in general).
  - Keep `EndRegion` for keyed bodies and for version-tolerant collection reads.
  - Keyed members: `EndMember` only when not at EOF; per-case region reads in fallback switches of at most three
    members; the constant header and raw value checked together (bools as validated bytes).
  - Give a single-use read body one method (the instance method, or the twin's no-inline `Read` with an early return
    on an absent value); keep the unchanged-string check at the field assignment.
- **Measured write-path rules**, one line each; the numbers and the attempts that lost are in
  [references/write-paths.md](references/write-paths.md) (read it before changing `BodyEmitter`'s writes, the
  top-level writes or `DispatchEmitter`, or when a generated serializer got slower or larger):
  - Write the length up front for a wholly raw region; patch it without a nested region when nothing inside opens
    bodies (`OpensBodies == false`).
  - Pack a constant key and its length into one header store.
  - Select the presence byte once and write it once.
  - Keep out-of-line write bodies static behind the inlined forwarder (a dummy `this` per call measured slower in
    subtype dispatch); merge only single-use read bodies into the instance method.
  - A struct that opens no class body is a class's body: a local reader and writer, inlined where small (one or two
    formatter members of at most eight), so a loop over `T[]` or `T?[]` keeps its cursor in registers.
  - Dispatch: outline exact writes only for nesting subtypes; every comparison `GetType() == typeof(...)`, never
    `is`, `GetType()` never hoisted.
- **Reach an interface member a struct implements explicitly through a generic accessor constrained to the
  interface** (`MemberAccess.Interface`: `__G_X<__TI>(ref __TI o) where __TI : IFoo => o.X` in the formatter), a
  class's through a cast (`MemberAccess.Cast`, `((IFoo)value).X`): a cast of a struct boxes a copy and loses a write.
  Both compile to the plain field load and store of an implicit member (Release JitDisasm of
  `ExplicitMembersStructFormatter` and `ExplicitMembersFormatter`, round 129: `mov esi, dword ptr [rbx+0x0C]`, no call,
  no box). Accessors take the struct by `ref`, which an `in` parameter cannot give: `WriteFixed` of a struct with any
  member that is not `Direct` delegates to a by-value `__WriteFixed` as a sealed class's does (an
  `Unsafe.AsRef(in o)` getter needs C# 12 against .NET 8+'s `ref readonly` signature: CS9058 in the C# 11 test
  project).
- Check the Tier1 listing of generated serializers for the benchmark types on each ISA (`benchmarks/asm/asm.py`), and
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
