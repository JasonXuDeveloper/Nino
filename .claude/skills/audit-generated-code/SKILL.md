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
  read only files the build just wrote (the folder keeps files of forms no longer generated). With
  `-p:CompilerGeneratedFilesOutputPath=<dir>` (a global property: Nino.Core's PolySharp output lands there too)
  create `<dir>` first, or the build fails with CS0016 races, and pass `--no-incremental`, or an up-to-date build
  writes nothing (round 130). A feature off by default proves itself zero cost the same way: both trees of
  `tests/Nino.UnitTests` (613 files) and `benchmarks/Nino.Benchmark` (261) identical before and after. Cover each shape:
  positional, version-tolerant and keyed types, structs (raw and member-wise), polymorphic roots, generics,
  constructors and factories, pooled types, reference tracking, `[NinoUtf8]`, custom formatters, interceptors.
- nino-gen: the committed outputs in `tests/Nino.Codegen.Tests` and the runtimes' `internal/sample` / corpus types.

## The text

- Keep the by-value formatter text lookup weak. Roslyn can move an equal model to a new slot after a declaration
  is inserted. Reuse its immutable source text while a consumer holds it, then let the model and text collect.
  An index bound by entry count alone still held closed projects' generated texts in the real project GC probe.

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
  declared without `global::NinoGen.` (alias declarations are inside the namespace too). Share the inlining flags as
  constants in the assembly's generated registry file: `[__NinoMI(__NinoIL.Inline)]` and
  `[__NinoMI(__NinoIL.NoInline)]` need only the attribute alias per file. Use the fully qualified BCL attribute
  if a formatter's type parameter shadows the flags type. The compiler folds the constants into the same method
  flags, with no helper calls.
  Shortening the attribute alias removed 29,864 source bytes from the unit test project (6d82f7a7),
  with identical generated IL and controlled Arm64 Tier 1 listings.
  Alias repeated non-generic named raw member types in symbolic sizes and loads (acc67d4b). Avoid aliases
  of constructed types that add TypeSpec metadata. Expanding the aliases recovered the same source;
  generated IL, Arm64 Tier 1 summaries and normalized x64 listings matched the baseline.
- Alias the compiler-services namespace in closed dispatch fallbacks when at least two bodies share the alias.
  Keep open generic partial bodies qualified because user type parameters can shadow a namespace alias.
  This saved 10,659 generated source bytes in the unit test project with identical dispatch IL and DLL size
  (ddd7bc90). Check both file-scoped and block namespaces with an alias-named type parameter.
- Reuse common alias declarations and only rewrite alias numbers when their order changes. A file-scoped body
  already has its final indentation, so concatenate it with the preamble and alias block directly. Use the first
  generator pass to measure emission allocations, since later passes can reuse cached formatter source.
  Carrying original indices and building a remap saved about 48 KB per replay of the unit test formatters,
  but throughput followed load order and the Arm64 alias method grew from 3,060 to 3,480 bytes. Keep the
  simpler renumbering on this workload.
  Keep formatter alias ordering and renumbering together, separate from self, Core and raw type aliases.
- Find dispatch ID collisions with a set of seen IDs and allocate a collision set only on demand. Keep the
  original order and omit every entry with a colliding ID. This saved about 100 KB per dispatch emission on
  the unit test project with identical output. A separate attempt to stream the generated checks instead of
  materializing their list enlarged the Arm64 emitter method from 328 to 1,044 bytes and gave mixed timings.
- Traverse raw member groups as ranges of the existing model array. Continue from the first group after emitting
  its region prefix, so a mixed body needs neither copied groups nor a second grouping pass.
- Accumulate the symbolic part of each raw offset as its emitted text. Keep the constant separate and prepend
  it only when needed. The actual unit test formatter replay saved about 12 KB per pass, with identical source.
  Reuse the same offset formatting in fixed layouts to avoid copying the symbolic terms even for constant offsets.
- Write the length up front when a region consists entirely of one raw member group. Reserve the header and
  payload together. Raw stores cannot queue deferred bodies, so the writer does not need to open a region or patch
  its length. Keep the begin/end path for mixed bodies and collections. This removed 2,747 generated bytes from
  the unit test project and reduced the Arm64 `PaddedItem[]` element loop from 428 to 252 machine-code bytes.
- Keep the remaining-length guard followed by `ReadBlock` for a complete version-tolerant raw group. The JIT
  folded the duplicate source check in the inspected Arm64 and x64 listings. A ref-returning `TryReadBlock`
  with an out flag reduced generated source by 706 bytes but enlarged the Arm64 `PaddedItem[]` read loop and
  slowed the local A/B. Keep the checked member-by-member fallback for old data and truncated fields.
- Omit `EndRegion` for a version-tolerant member region whose raw members and formatters preserve read context.
  Classify built-in strings, raw arrays and lists, and dictionaries with raw keys and raw or UTF-16 string
  values in `FormatterTraitsOf`. Keep `ReadRegion` to validate lengths and any section of deferred bodies
  from newer writers. The Arm64 `PaddedItem[]` loop dropped from 572 to
  452 machine-code bytes and `NamedItem`'s body from 1,136 to 940. Keep the unaligned raw reads and check a
  region at offset one.
  Keep the outer `EndRegion` for keyed bodies. Applying the same omission to context-preserving keyed members
  saved 936 source bytes in the unit test project and reduced Arm64 Tier 1 `KeyedStruct` read-body code from
  1,444 to 1,352 bytes, but the local unaligned array read slowed by about 2% in each of four alternating rounds.
- For a context-preserving member on the keyed in-order path, call `EndMember` only
  when its reader is not at EOF. This retains the exact-length error and skips copying
  the read context on success. Use the same guard on the shared switch exit only
  when every member in that switch preserves context.
  The `KeyedStruct` fixture gained 14 source bytes and 12 IL bytes but shrank from
  1,444 to 1,372 Arm64 Tier 1 bytes and from 1,338 to 1,251 offline x64 bytes.
  Four local timings overlapped the A/A spread. All 15 wire comparisons matched.
  Guarding the switch exit added another 14 source and 12 IL bytes, but reduced
  the reordered body's Arm64 Tier 1 size from 1,440 to 1,356 bytes and offline
  x64 from 1,251 to 1,141. After a longer warmup, four local 1,000-item reordered
  reads took 9.24–9.39 µs against 9.46–9.47 µs, at zero steady allocation.
  Retesting outer `EndRegion` omission after the member changes still favored
  keeping it. Pin the same Core binary on both sides and warm 16-item reordered
  reads until the array caller and element loop reach Tier 1. The apparent large
  short-array regression then fell to about 1% (157.85–158.34 to 159.00–159.59 ns
  in four rounds). The 1,000-item reordered case also slowed slightly. Removing
  the call saved 39 source bytes, 8 IL bytes, 92 Arm64 Tier 1 bytes and 167 offline
  x64 bytes. All 30 wire comparisons passed. Keep the omission as an experiment.
- On keyed fallback switches with at most three members, open the member reader in
  each known case and discard the validated region on the unknown path. This lets
  the JIT omit the unused child Reader. Keep one shared region read for larger
  switches to bound code growth. On `KeyedStruct` with one unknown key per value,
  1,000 reused items went from 8.43–8.49 to 7.35–7.56 µs in four local rounds, at
  zero steady allocation. Known reordered keys also improved, while the in-order
  array cases overlapped A/A. The file gained 174 source bytes and the body gained
  33 IL bytes, 324 Arm64 Tier 1 bytes and 384 offline x64 bytes.
  Sharing one region read for known cases after a separate unknown-key check
  saved 52 source bytes, 200 Arm64 Tier 1 bytes and 304 offline x64 bytes, but
  increased IL by 7 bytes and slowed 1,000 known reordered entries from roughly
  8.77 to 9.49–9.76 µs. Mapping each key to an ordinal before reading the shared
  region also slowed that case and took 8.57–8.68 µs for 1,000 unknown-key entries,
  against about 7.49 µs with separate reads. All three used the same Core DLL,
  stayed at zero steady allocation, and matched all 30 wire comparisons per
  candidate. Keep the per-case reads for small switches.
  Sharing a no-inline region reader while preserving the key switch also lost.
  It needed a scoped parent reference to return the child. The Arm64 Tier 1
  body fell from 1,684 to 1,556 bytes but the helper added 236 bytes. Offline
  x64 fell from 1,525 to 1,127 + 161 bytes. Generated source grew by 156 bytes
  and IL by 7 bytes. With the same Core DLL and zero steady allocation,
  1,000 known reordered entries took 16.95–17.13 µs against 8.77–8.79 µs,
  and 16 ordered entries slowed even though they bypassed the helper.
  All 30 wire comparisons passed. Keep the patch as a code-generation experiment.
  Do not add a separate region-skipping API for the unknown-key branch. An
  experimental `SkipRegion` shared the section validator and advanced only the
  parent, but discarding `ReadRegion` already lets the JIT remove the child.
  The Arm64 Tier 1 body stayed at 1,684 bytes, and offline x64 kept the same
  instructions and 1,525-byte size. Source size matched and IL saved one byte.
  Four same-Core A/B rounds overlapped A/A at zero steady allocation, with all
  30 wire comparisons passing.
- Classify `MemoryFormatter` over a raw element formatter as preserving read
  context, while keeping its existing formatter call. Keyed `Memory<int>` reuse
  saved 228 Arm64 Tier 1 bytes and 200 offline x64 bytes, with 28 extra source
  bytes and 21 extra IL bytes. Four local 1,000-item ordered reads fell from
  about 5.47 to 4.90–4.94 µs, and reordered reads also improved. Allocation
  stayed at zero. All 30 wire and round-trip comparisons passed. The keyed
  memory regression checks reuse and the exact-length error.
  `ReadOnlyMemoryFormatter` over raw elements also preserves read context.
  Keep allocating fresh backing storage. The keyed fixture stayed at 40 B
  per item with 28 more source bytes and 21 more IL bytes. Arm64 Tier 1
  fell from 2,260 to 2,008 bytes and offline x64 from 3,904 to 3,472.
  Sixteen reordered items improved in four paired rounds, while the 1,000-item
  results overlapped the A/A spread. All 30 wire and round-trip probes passed.
  `ArraySegmentFormatter` over raw elements preserves context as well.
  Keep the fresh backing array and the default segment's null marker.
  The keyed fixture added 28 source bytes and 21 IL bytes, but Arm64 Tier 1
  fell from 1,928 to 1,712 bytes and offline x64 from 1,666 to 1,466.
  After longer warmup, 1,000 reordered items fell from 9.80–10.04 to
  9.35–9.49 µs over four rounds. Ordered results overlapped A/A and allocation
  stayed at 40 B per item. All 30 wire and round-trip probes passed.
  `ReadOnlySequenceFormatter` over raw elements uses the same array read.
  The keyed sequence fixture stayed at 40 B per item and added 28 source and
  21 IL bytes. Arm64 Tier 1 fell from 2,348 to 2,092 bytes and offline x64
  from 4,012 to 3,557. Four 1,000-item ordered reads went from 8.93–8.98
  to 8.29–8.74 µs, and reordered reads also improved. All 30 wire and
  round-trip probes passed.
- Patch a keyed string member's length without opening a nested writer region.
  The direct UTF-16 and UTF-8 writes cannot defer bodies. Reserve the preceding raw
  members, key and length together as before. The `KeyedStruct` writer gained 19
  source bytes and 7 IL bytes, but fell from 1,232 to 1,092 Arm64 Tier 1 bytes
  and from 953 to 863 offline x64 bytes. Four local 1,000-item ASCII writes went
  from 5.20–5.23 to 5.10–5.12 µs at zero steady allocation. All 54 wire and
  round-trip comparisons matched across both string encodings and offsets 0, 1 and 7.
  Patch the outer keyed body the same way when it has no collection elements
  and every member is raw or a direct string. Keep the one-reservation raw
  prefix and the fixed-region path for wholly raw bodies. This saved another
  140 Arm64 Tier 1 bytes and 90 offline x64 bytes, with 20 more source bytes
  and 5 more IL bytes. Four local 1,000-item ASCII writes went from about
  5.10 to 4.75–4.77 µs at zero steady allocation. The 54 byte and round-trip
  checks included a UTF-8-only body with no raw prefix.
  Apply the same outer patch to version-tolerant bodies with only raw members
  and direct strings. On the version-tolerant fixture, source grew by 20 bytes
  and IL by 5, while Arm64 Tier 1 shrank from 868 to 728 bytes and offline x64
  from 703 to 613. Four 16-item ASCII rounds improved against the paired
  baseline. The 1,000-item results overlapped A/A after longer warmup, at zero
  steady allocation. All 54 wire and round-trip checks passed, including a
  UTF-8-only body.
  A version-tolerant collection can patch its member region too when those members
  are raw or direct strings. Its elements are written before the region opens.
  Keyed collections include their elements, so also require the collection formatter
  to have `OpensBodies == false`. The version-tolerant `NamedList` fixture added three source bytes with unchanged 38-byte
  IL. Arm64 Tier 1 fell from 1,672 to 1,532 bytes and offline x64 from 2,495 to 2,405.
  Four 1,000-item ASCII runs fell from 5.95–6.10 to 5.32–5.84 µs at zero steady
  allocation. All 27 wire and round-trip probes passed.
  Switching the fixture to keyed and applying the element check saved 140
  Arm64 Tier 1 bytes and 90 offline x64 bytes. Four 1,000-item writes fell
  from 6.77–6.93 to 6.13–6.33 µs at zero steady allocation. A keyed list
  containing a deep class chain still writes and reads its deferred elements.
  Carry the same resolved `OpensBodies` fact for members. A body with a built-in
  `List<int>` member can patch its outer length directly. Keep custom formatters
  conservative. The version-tolerant fixture gained 20 source bytes and 5 IL
  bytes but saved 140 Arm64 Tier 1 and 99 offline x64 bytes. Four 1,000-item
  writes went from 4.79–5.13 to 4.57–4.61 µs at zero steady allocation.
  The 54 wire and round-trip probes covered both keyed and version-tolerant bodies.
  Apply that same member predicate to individual keyed entries. With a built-in
  `List<int>` member, 1,000 ASCII writes fell from 7.06–7.10 to 6.04–6.16 µs
  across four same-Core rounds at zero allocation. The generated file gained
  19 bytes and the body gained 7 IL bytes, while Arm64 Tier 1 fell from 1,940 to
  1,800 bytes and offline x64 from 2,785 to 2,688. All 27 wire and round-trip
  probes passed, including null and wide strings at unaligned offsets.
  Keep `EndRegion` for version-tolerant collection reads. Omitting it when all
  members preserved context shrank the `NamedList` body from 1,228 to 1,148 Arm64
  Tier 1 bytes and from 1,744 to 1,643 offline x64 bytes, but 16 ASCII items
  slowed in all four paired rounds. A guard that skipped the call only at EOF
  brought the short array back into the A/A spread but added 12 Arm64 bytes,
  6 offline x64 bytes and 9 IL bytes, with the 1,000-item case still slower.
  Both experiments matched all 27 wire and round-trip probes at zero steady allocation.
- Use target-typed construction for an ordinary constructor when the generated return or assignment names the
  exact type. Keep explicit construction on the existing older-language path and in pool fallback expressions.
- Keep generic formatter registrations on individual assembly attribute lines. Grouping the
  83 formatter attributes removed 568 source bytes while keeping assembly attributes,
  compiled IL and wire output identical. The indented list allocated about 11 KB more
  per isolated Roslyn parse in both load orders. Smaller indented groups still allocated
  more, and the unindented probes saved at most 3.8 KB per parse. The emitter grew from
  915 to 952 IL bytes, 2,776 to 2,892 Arm64 FullOpts bytes, and 1,751 to 1,850 x64
  cross-JIT bytes. Whole-project timing showed no repeatable change. Both Roslyn
  builds matched all other outputs and preserved incremental reuse and collection.
- Shorten repeated namespace qualifiers in interceptor signatures and calls and in usage registration statements
  and type references, where one alias serves the whole file. Leave location attributes and registry literals
  alone. Compare consumer DLL metadata as well as method IL: typed aliases and factoring opaque location strings
  made the DLL larger in the local prototype. Reusing the namespace alias selector for usage registrations
  (120c9508) removed 20,759 source bytes in the unit test project with identical generated IL and DLL size.
  Use the short `__NinoAt` alias for the location attribute, leaving its arguments intact.
  This saved 15,448 source bytes in the real unit test project. The probe consumer had identical
  IL and DLL size, and Arm64 and x64 listings kept their code sizes. Four runtime rounds had no
  repeatable timing difference, and wire bytes and allocation matched. Both Roslyn versions
  differed only in the alias spelling, and 80 tests passed on each of .NET 8 and 10 (`237a414a`).
  Stop counting once the declaration pays for itself. For registration statements, merge the next exact prefix
  matches and append their source slices directly to the output writer. This preserves the emitted text and
  avoids intermediate replacement strings. Scanning every `global::` token was slower in the local probe.
- Emit constant type IDs directly in formatter and twin accessors. When no generated caller needs a constant
  field, retaining it duplicates consumer metadata without changing the getter's IL.
- Keep the presence-byte writes in their null and non-null branches in top-level local-writer wrappers.
  Sharing one capacity check reduced the `SimpleClass` listing on both architectures, but the same-process
  Arm64 A/B moved from about 47 ns to 49 ns. Keep the null test's existing operator semantics in any follow-up.
- Let raw stores infer the type from their member expression or typed header literal. Keep the
  explicit type on reads, where the return value cannot drive inference. This removed 5,020 source
  bytes across 239 files in the unit test project. The consumer DLL size and normalized IL matched.
  The keyed write stayed at 129 IL bytes, 804 Arm64 Tier 1 bytes and 541 x64 cross-JIT bytes.
  Local runtime timings overlapped the identical-binary control, at zero steady allocation.
- Pack a keyed member's constant key and byte length into one unaligned header store on write.
  Keep symbolic lengths on the existing path. The real unit test project emitted 2,329 fewer source bytes.
  For `KeyedStruct`, the x64 listing changed four header stores to two and the Arm64 listing stayed the same size.
- Check a constant keyed header and its plain raw value together on read. Leave the cursor unchanged on a
  mismatch so the checked fallback handles truncation, unknown keys and reordered members. Emit little-endian
  constants directly because Reader already rejects big-endian hosts. The unit test project emitted 3,820
  fewer bytes and the local `KeyedStruct[]` timing was broadly on par.
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
- Bypass sorting and renumbering when a formatter has a single alias candidate. In a unit test project
  snapshot, 122 files declared one formatter alias and 52 needed renumbering. Keeping the multi-alias
  order unchanged saved about 307 KB of cold allocations in the real project replay. All 577 replayed
  files matched byte for byte, and the cold and method-body edit timings were flat. For multiple aliases,
  checking whether the names are already ordered and precomputing the regex remap saved another 32 KB
  in cold replay. The emitter-only timing changed direction with the loaded variant order, and all 577
  output files still matched. The candidate kept 310 IL bytes in the original helper and added a
  177-byte renumbering helper (383 bytes combined before). Keep the simpler production path until a
  representative workload shows a larger benefit.
- Join declarations before concatenating the file-scoped output. The fixed-arity concatenation kept all
  577 replayed files byte-identical and saved about 229 KB per cold run on the unit test project. The
  variadic concatenation saved about 590 KB but repeatedly produced slower model-edit samples. With
  fixed arity the model-edit timings varied in both directions, and incremental model reuse and collection
  of obsolete compiler objects matched the baseline. The emitter helper grew from 304 to 319 IL bytes.
  For block namespaces, reserve the body length plus one quarter for indentation instead of twice
  the body length. The largest block file in the replay was a 617 KB interceptor file with about
  3% extra indentation. The smaller reservation saved 0.92 MB cold and 0.90 MB on a method-body edit
  with identical output and timings within the local noise floor.
- Inspect trimmed line boundaries through a span in `CodeWriter.Line`. Keep writing the original
  text so the output's whitespace stays identical. The unit test project replay saved about 15 KB
  of cold allocations and matched all 577 files. Timing was flat and the helper gained 11 IL bytes.
  Scanning ranges instead of splitting in `CodeWriter.Lines` saved only another 21 KB cold.
  It matched the same 577 files and did not improve elapsed time. The method grew from 62 to 73
  IL bytes, from 260 to 320 Arm64 bytes, and from 205 to 268 x64 cross-JIT bytes. Keep the simpler
  split for the registry and pool hook templates.
- Append interceptor signature slices and the method ordinal directly to the code writer. This
  avoids allocating two substrings and the assembled method line. In the 577-file unit test replay,
  cold and method-body edit runs each allocated about 1.1 MB less with byte-identical output.
  The main emitter went from 870 to 813 IL bytes and added a 99-byte append helper.
  Appending the location attribute and its optional comment directly saved another 0.50 MB on cold
  and edited replays. It kept the same output and reduced the main emitter to 764 IL bytes, adding
  a separate 99-byte append helper.
  Estimate the interceptor body buffer from both call sites and grouped methods, capped at the
  previous call-only reservation. The replay had 1,930 locations and 1,192 methods. The estimate
  reduced initial capacity from 741,120 to 628,480 characters and saved about 223 KB in each cold
  or edited replay, with identical output and no stable timing difference.
  Streaming namespace alias replacements into the numbered method avoided about 0.52 MB of cold
  allocations, but slowed the model-edit replay in both run orders. Applying it to only the call
  expression still slowed that case, saving about 0.45 MB. Both variants emitted the same 577
  files byte for byte. Keep the string replacements for this path.
- Reuse the first successful alias rewrite's builder for the rest of that file. The rewrite only
  shortens the body and the builder stays local, so later aliases do not allocate another
  body-sized buffer or retain one in the generator. The 577-file unit test replay matched byte for
  byte and saved 11.6 MB of cold allocations per run. Cold and hot timing differences were within
  local noise, with the `WithAliases` IL growing from 473 to 490 bytes.
  Storing the first eight match offsets on the stack skipped the duplicate prefix scan, but did
  not materially change allocations or whole-project timings. The emitter-only result changed
  direction with load order and the 577 files matched byte for byte. `WithAlias` grew from 242
  to 352 IL bytes and from 760 to 1,212 Arm64 FullOpts bytes. Keep the simpler profitability scan.
- Prefer one compiler-services namespace alias when it can replace overlapping type aliases. Adding it as a
  separate rewrite allocated about 6.7 MB more in a cold local generator replay. Skipping the overlapping
  rewrites reduced that overhead to about 0.94 MB and removed 47,024 bytes from the unit test project's
  generated output. The normalized IL matched after accounting for file-local type names, explicit interface
  member spellings and data RVAs. Warm replay and method-body edits were within the local noise floor.
  Reuse the existing dispatch namespace alias for the exact subtype calls as well as fallbacks.
  This removed 3,380 bytes from the unit-test dispatch and 1,872 from the benchmark dispatch,
  with all 760 dispatch method bodies byte-identical in IL. The 1,000-element polymorphic array
  wrote the same bytes, with identical allocation and no repeatable timing change. The sampled
  Arm64 and x64 cross-JIT dispatch method sizes matched. Both targeted suites passed (`686fd8d1`).
  Reuse that namespace alias in the generic selectors with `::`, so a user's same-named type
  parameter cannot shadow it. The unit-test dispatch shrank by 2,112 bytes and the other 576
  files matched on both Roslyn builds. The compiled IL listing and the sample wire matched.
  For the closed tuple selector, Arm64 FullOpts write/read stayed at 948/296 bytes, x64 cross-JIT
  at 208/169, and local reuse reads and writes at zero allocations. Timing varied between
  identical baselines as well, so treat this as a source-size change.
  Outline exact writes in wider subtype dispatches when the subtype body nests. Keep flat and raw writes
  inline. Outlining every non-raw case made the 1,000-element unmanaged tuple workload consistently slower.
  The nesting gate left that dispatch unchanged and the open-class tuple workload improved in four local
  Arm64 rounds. Its Tier 1 dispatch shrank from 2,476 to 604 bytes, with eight separate 332-byte helpers.
  Count those helpers and the caller separately. The benchmark emitted 2,160 more source bytes and all
  tested wire outputs matched, including short writes to unaligned offsets.
  Keep exact runtime type comparisons adjacent when a subtype can be derived from. Hoisting
  `value.GetType()` to a shared local saved 496 source bytes and shrank the sampled dispatch IL
  from 256 to 223 bytes. Arm64 Tier 1 already loaded the method table once in the original chain.
  The shared local materialized a runtime type object and grew the dispatch from 604 to 672
  machine bytes. The non-generic 1,000-item case was slower in all four local rounds.
  A shared generic comparison helper saved only 136 source bytes and grew the non-generic
  dispatch from 2,984 to 3,064 Arm64 Tier 1 bytes and from 1,148 to 2,281 x64 cross-JIT bytes.
  Using `is` for every sealed target saved 936 source bytes and kept the method-table comparison,
  but added an explicit null branch before the chain. The non-generic case was slower than its
  paired baseline in four local rounds. The wire bytes matched and both probes allocated 0 B.
  Keep the first comparison exact and use `is` only for later sealed targets. The first method-table
  load establishes non-nullness without the explicit branch. The benchmark dispatch saved 774
  source bytes and the sampled IL went from 256 to 151 bytes. The Arm64 entries grew by 4 and 8 bytes.
  The x64 cross listings shrank from 1,148 to 602 and from 1,068 to 519 bytes. Thread CPU and wall time
  varied against the same-binary controls, so treat this as a size change. Wire bytes, including
  unaligned writes, and 0 B steady-state allocation matched.
  A separate post-emission alias for `System.Runtime.CompilerServices` paid off in only 22 of 577
  replayed files. It removed 2,444 source bytes with identical normalized unit test assembly IL, but
  added about 740 KB of cold generator allocations and enlarged the alias helper from 473 to 512 IL
  bytes. Prefer emitting such an alias directly if a workload uses it often enough.
  A shorter `__NC` namespace alias for Core replaced the overlapping reader and writer aliases
  after the compiler-services pass. It removed 36,002 bytes across 577 replayed files and saved
  0.50 MB of cold allocations. The normalized unit test assembly IL matched, timings were flat,
  and the alias helper grew from 490 to 571 IL bytes.
  Share that alias with the streaming namespace writer for usages and interceptors. The unit
  test replay emitted 19,620 fewer bytes and the benchmark emitted 3,102 fewer bytes. Expanding
  the alias restored the original text. All 1,850 normalized generated benchmark methods
  matched, as did the Arm64 and x64 cross-JIT registration entry sizes. Warm registration
  allocated 0 B and timings varied within the controls.
  Use the shorter `__SCG` alias for repeated generic collection names. The unit test replay
  emitted 18,060 fewer bytes and the benchmark emitted 2,376 fewer bytes, with the original
  text restored by alias expansion. The shorter threshold can introduce the alias into a
  smaller file. Normalize aliases in assertions about registration content, and assert the
  spelling only in the compaction test. Benchmark IL, wire bytes and registration allocations
  matched, with no repeatable timing change.
  Use `__CS` for the shared compiler-services alias. Avoid a spelling that starts with `__NC`: the
  Core guard sees the substring and falls back to longer Reader and Writer aliases. The overlapping
  candidate grew the benchmark output by 2,208 B. The final spelling saved 6,024 B across 104 benchmark
  files and 23,409 B across 582 unit-test files. Expanding aliases restored the original sources.
  All 3,340 benchmark method signatures and bodies matched after normalizing file-local type names.
  The sampled Arm64 and x64 cross-JIT dispatch sizes, three wire outputs and runtime allocations matched.
  Timings varied against A/A. Both Roslyn targeted suites and the versioning, tracking and depth tests
  passed (`7053428a`).
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
  spelling of one namespace per file); dropping `[__NinoInline(AggressiveInlining)]` from the interceptors (1.1% /
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

- The code standard (`audit-code`: DRY, decoupled, clear) applies to these files, and redundancy in them
  (duplicated logic, dead code, two ways to do one job) is part of this audit; the sweep across layers is
  `audit-redundancy`.

## The machine code

- Emit the standalone out-of-line read body as the instance method when no inline twin or
  top-level reader calls its static body. Keep the no-inline boundary. The unit test project
  saved 50,736 source bytes across 328 of 582 files, with no changes outside the merged
  forwarders. The string reuse fixture kept its 65-byte body and removed an 8-byte IL
  forwarder. Arm64 FullOpts kept the body at 2,556 B and moved the caller from 612 to 628 B.
  In the x64 cross-JIT MinOpts listing the body grew from 233 to 256 B and the caller stayed
  at 163 B. Unaligned 64-character reuse improved in four paired runs and the A/A/B replay.
  Adjacent lengths varied. Wire bytes, allocations, 577-model reuse and collection matched.
  Both Roslyn target suites passed (`709af0ae`).
  Apply the same merge to standalone class writes that have no local-writer twin. Keep the
  struct's static body with its by-reference value. The unit test project saved 37,716
  source bytes across 252 files in each replay state. The string-holder write kept 22 IL bytes
  and lost an 8-byte forwarder. The body stayed at 1,208 B on Arm64 and 818 B on x64 cross-JIT,
  and the caller stayed at 272 and 214 B. Four local rounds showed no meaningful write
  timing change, with identical wire bytes and zero steady allocation. All 577 models were
  reused on edits and both Roslyn suites passed (`8ca095f4`).
- Omit the empty member hook for object-only prefetch formatters when Core exposes the object-only interface.
  Retain the existing generated contract against older Core versions. This removed 15,136 source bytes across
  148 of the unit test project's 582 files. The wire output and the activation threshold matched in both
  compatibility directions. See `optimize`'s prefetch reference for the eliminated loop and timing controls.
- Emit twin traits from the same model as the main formatter and return `default` directly when a
  twin cannot defer construction. Forward actual construction so pooling and factories stay in one
  body. This saved 8,693 source bytes across 140 files in the real test project. The probe twin's
  trait getter shrank from 15 to 2 IL bytes. The Arm64 and x64 FullOpts reader listings kept their
  code sizes. Four local A/B rounds showed no repeatable time change, and wire bytes and
  per-operation allocations matched. The targeted .NET 8 and 10 tests passed (`f0188eea`).
- Keep the unchanged-string check at the generated field assignment. An assembly-wide byref
  helper for normal string fields removed 18,847 source bytes in the real test project. The
  probe read body fell from 167 to 90 IL bytes plus a 19-byte helper, and x64 ReadyToRun fell
  from 501 to 377 bytes including that helper. Arm64 FullOpts grew from 1,500 to 1,524 bytes.
  After reversing run order, unchanged-string reuse was 4–8% slower across four rounds despite
  matching wire bytes and allocations. The isolated candidate passed 82 targeted tests.

  For a private field, a ref local removes the second accessor from source but may add a live
  interior reference across the string read. The baseline x64 FullOpts listing already had no
  accessor calls. On the private-field probe the read body IL stayed at 169 bytes, while the
  enclosing FullOpts read grew from 1,500 to 1,512 Arm64 bytes and from 367 to 373 x64 cross-JIT
  bytes. Unchanged reuse varied within 1.2% across four default-tiering runs and allocated 0 B.
  Changed and fresh reads varied across runs, with matching allocation and wire bytes. The real
  test project replay added 75 bytes across four of 577 generated files. Keep the baseline shape
  until a probe shows an actual machine-code or throughput benefit.
- Everything a top-level call reaches inlines into one method under one budget (`optimize`): the generated
  read and write bodies keep rare paths (deferral, pooling, growth, polymorphic fallback) in calls out of line and
  the common path inline; a run of fixed-size members is one bounds check and one block copy where the layout allows.
- No indirect call in a generated read or write path where a direct one exists (`optimize`, the owner's rule): a
  member's formatter is named directly (a generic type's formatter takes its arguments' formatters), and
  `CachedFormatter<T>` only for a method's type parameter or a `[NinoType]` generated in another assembly (a type
  that holds itself gets a generated nesting struct, `optimize`); read the IL2CPP C++ as well as the Tier1 listing (`optimize`, references/unity.md); look for `call [rax+0x..]`, `call [r11]` and `call rax` in the Tier1
  listing and explain each.
- **Reach an interface member a struct implements explicitly through a generic accessor constrained to the
  interface** (`MemberAccess.Interface`: `__G_X<__TI>(ref __TI o) where __TI : IFoo => o.X` in the formatter), a
  class's through a cast (`MemberAccess.Cast`, `((IFoo)value).X`): a cast of a struct boxes a copy and loses a write.
  Both compile to the plain field load and store of an implicit member (Release JitDisasm of
  `ExplicitMembersStructFormatter` and `ExplicitMembersFormatter`, round 129: `mov esi, dword ptr [rbx+0x0C]`, no call,
  no box). Accessors take the struct by `ref`, which an `in` parameter cannot give: `WriteFixed` of a struct with any
  member that is not `Direct` delegates to a by-value `__WriteFixed` as a sealed class's does (an
  `Unsafe.AsRef(in o)` getter needs C# 12 against .NET 8+'s `ref readonly` signature: CS9058 in the C# 11 test
  project).
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
