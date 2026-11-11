# Shorter generated text: aliases and spellings

Part of the `audit-generated-code` skill. The measured rules for how Nino.Generator spells what it emits (alias
names, namespace aliases, qualifiers, inferred types, registrations), each with its evidence and the attempts that
lost. Read it before adding or renaming an alias, changing a qualified name, an interceptor or registration line, or
when output bytes per type grew. The rules in SKILL.md ("The text", "Size") stand above these; what the alias pass
costs the generator itself is `audit-generator`
([emit-text.md](../../audit-generator/references/emit-text.md)).

How every bullet here was proven: the generated source size of `tests/Nino.UnitTests` (and `benchmarks/Nino.Benchmark`)
over all files, the consumer IL normalized for file-local type names, and for a sampled formatter its Arm64 and x64
listings, its wire bytes and its allocation. A text change whose IL matches is a size change: do not claim a speedup
from local timings that also vary between identical binaries.

## Alias names

- **Spell each per-file alias as short as it can be** (`__MI` for the MethodImpl attribute, `__IL` for the shared
  inlining flags type, `__T` for the type's own name, `__C` Core, `__CS` compiler services, `__SCG` generic
  collections, `__NinoAt` the location attribute): an alias is repeated in every line that uses it, so its length is
  a measurable share of the output, and the IL does not change.
  - Shortening the attribute alias removed 29,864 source bytes from the unit test project (6d82f7a7), with identical
    generated IL and controlled Arm64 Tier 1 listings. Shortening it again to `__MI` saved 25,356 bytes across 579
    files and 312–344 KB of allocation per cold project replay. Full consumer IL matched after normalizing the
    compiler's file-local interceptor type suffix. The sampled writer retained 172 Arm64 and 145 emulated x64
    FullOpts bytes, the wire and 0 B allocation.
  - Shortening the shared flags type to `__IL` saved another 23,044 bytes across 580 files and 302–384 KB per cold
    project replay. Consumer IL matched after normalizing the internal type names and relocated data addresses. The
    sampled round trip kept 376 Arm64 and 329 emulated x64 FullOpts bytes, identical wire bytes, and 0 B allocation.
    The only large timing swing also appeared in the identical-binary control.
  - Shortening the self alias to `__T` saved 29,008 bytes across 494 files. Cold project allocation fell by 131–247 KB
    in four pairs, with mixed timings before a counterbalanced cold-only replay. Normalized consumer IL and the
    sampled wire matched. Wait for Tier 1 before comparing the round trip: the initial samples sometimes mixed tiers.
    With longer warmup, the Arm64 Tier 1 body remained 376 bytes, emulated x64 FullOpts stayed at 329 bytes, and
    timing stayed close to the identical-binary control at 0 B allocation.
- **Keep the fully qualified BCL attribute when a formatter's type parameter shadows the flags type**, in C# 9 and
  later too: the type-parameter fallback is what keeps `[__MI(__IL.Inline)]` compiling in a generic formatter.
- **Keep formatter aliases, and the Core alias, inside `NinoGen`**: an alias outside the namespace collides with a
  consumer's global type. Moving the Core alias before the assembly layout attributes saved 21,942 bytes but failed
  with CS0576 when the consumer declared a same-named global type.
- **Alias repeated non-generic named raw member types in symbolic sizes and loads; never alias a constructed type**:
  an alias of a constructed type adds TypeSpec metadata. The named-type aliases landed in acc67d4b; expanding them
  recovered the same source, and generated IL, Arm64 Tier 1 summaries and normalized x64 listings matched the
  baseline.

## Namespace aliases

- **Use one namespace alias where it replaces overlapping type aliases**, rather than adding it next to them: two
  rewrites of the same names cost allocation and save less. Adding the compiler-services namespace alias as a
  separate rewrite allocated about 6.7 MB more in a cold local generator replay; skipping the overlapping rewrites
  reduced that overhead to about 0.94 MB and removed 47,024 bytes from the unit test project's generated output. The
  normalized IL matched after accounting for file-local type names, explicit interface member spellings and data
  RVAs. Warm replay and method-body edits were within the local noise floor.
  - The Core alias (first `__NC`) replaced the overlapping reader and writer aliases after the compiler-services pass:
    it removed 36,002 bytes across 577 replayed files and saved 0.50 MB of cold allocations. The normalized unit test
    assembly IL matched, timings were flat, and the alias helper grew from 490 to 571 IL bytes.
  - Share that alias with the streaming namespace writer for usages and interceptors: the unit test replay emitted
    19,620 fewer bytes and the benchmark 3,102 fewer. Expanding the alias restored the original text. All 1,850
    normalized generated benchmark methods matched, as did the Arm64 and x64 cross-JIT registration entry sizes.
    Warm registration allocated 0 B and timings varied within the controls.
  - `__SCG` for repeated generic collection names: the unit test replay emitted 18,060 fewer bytes and the benchmark
    2,376 fewer, the original text restored by alias expansion. The shorter threshold can introduce the alias into a
    smaller file, so normalize aliases in assertions about registration content and assert the spelling only in the
    compaction test. Benchmark IL, wire bytes and registration allocations matched, with no repeatable timing change.
  - `__CS` for the shared compiler-services alias (7053428a): with the original substring guard, an alias starting
    with `__NC` made Core fall back to the longer Reader and Writer aliases, and that overlapping candidate grew the
    benchmark output by 2,208 B. The final spelling saved 6,024 B across 104 benchmark files and 23,409 B across 582
    unit-test files. Expanding aliases restored the original sources. All 3,340 benchmark method signatures and
    bodies matched after normalizing file-local type names. The sampled Arm64 and x64 cross-JIT dispatch sizes, three
    wire outputs and runtime allocations matched. Timings varied against A/A. Both Roslyn targeted suites and the
    versioning, tracking and depth tests passed.
  - **Check the full alias prefix, including the dot**, so `__C.` and `__CS.` can coexist (a8097712): shortening
    Core to `__C` saved 9,365 B across all 583 unit-test files. Expanding the alias restored byte-identical output
    in all four replay stages. Consumer IL matched apart from the two test assertion strings. The sampled
    version-tolerant writer and reader stayed at 2,648 and 1,348 Arm64 FullOpts bytes and 104 and 170 offline x64
    bytes. Wire bytes and zero steady allocation matched; local timing varied with the duplicate baseline. Both
    Roslyn builds passed 63 targeted tests.
- **Alias the compiler-services namespace in closed dispatch fallbacks when at least two bodies share the alias;
  keep open generic partial bodies qualified**: user type parameters can shadow a namespace alias. This saved 10,659
  generated source bytes in the unit test project with identical dispatch IL and DLL size (ddd7bc90). Check both
  file-scoped and block namespaces with an alias-named type parameter.
- **Reuse the dispatch namespace alias for exact subtype calls as well as fallbacks, and in generic selectors with
  `::`** (`__CS::...`), so a user's same-named type parameter cannot shadow it.
  - Exact subtype calls (686fd8d1): removed 3,380 bytes from the unit-test dispatch and 1,872 from the benchmark
    dispatch, with all 760 dispatch method bodies byte-identical in IL. The 1,000-element polymorphic array wrote the
    same bytes, with identical allocation and no repeatable timing change. The sampled Arm64 and x64 cross-JIT
    dispatch method sizes matched. Both targeted suites passed.
  - Generic selectors: the unit-test dispatch shrank by 2,112 bytes and the other 576 files matched on both Roslyn
    builds. The compiled IL listing and the sample wire matched. For the closed tuple selector, Arm64 FullOpts
    write/read stayed at 948/296 bytes, x64 cross-JIT at 208/169, and local reuse reads and writes at zero
    allocations. Timing varied between identical baselines as well: a source-size change.
- **Emit the compiler-services alias directly where the emitter knows it pays (two or more `UnsafeAccessor`
  annotations), not through a post-emission rewrite; keep the full qualifier when a generic parameter shadows it**:
  a rewrite pass scans every file to help a few.
  - Direct emission (14b075da): the 582-file unit-test replay shrank from 3,240,332 to 3,239,060 bytes across 12
    formatter files, with no repeatable timing or allocation change across the four cold and edit pairs. The
    compiled project matched all 14,374 method bodies and flags. The sampled formatter matched in normalized Arm64
    FullOpts and x64 offline ReadyToRun listings, and mixed A/B reuse results were within the repeated-binary spread
    at 0 B allocation and identical wire bytes. Both Roslyn builds passed 73 targeted tests.
  - Do not add the body rewrite on top: it saved another 274 bytes but consistently added about 100–232 KB of cold
    generator allocation.
  - Do not add a separate post-emission `System.Runtime.CompilerServices` alias: it paid off in only 22 of 577
    replayed files, removing 2,444 source bytes with identical normalized unit test assembly IL, but added about
    740 KB of cold generator allocations and enlarged the alias helper from 473 to 512 IL bytes.

## Interceptors and registrations

- **Shorten repeated namespace qualifiers in interceptor signatures and calls and in usage registration statements
  and type references, where one alias serves the whole file; leave location attributes' arguments and registry
  literals alone**: compare consumer DLL metadata as well as method IL, because typed aliases and factoring the
  opaque location strings made the DLL larger in the local prototype.
  - Reusing the namespace alias selector for usage registrations (120c9508) removed 20,759 source bytes in the unit
    test project with identical generated IL and DLL size.
  - The short `__NinoAt` alias for the location attribute (237a414a) saved 15,448 source bytes in the real unit test
    project. The probe consumer had identical IL and DLL size, and Arm64 and x64 listings kept their code sizes. Four
    runtime rounds had no repeatable timing difference, and wire bytes and allocation matched. Both Roslyn versions
    differed only in the alias spelling, and 80 tests passed on each of .NET 8 and 10.
  - Stop counting once the declaration pays for itself. For registration statements, merge the next exact prefix
    matches and append their source slices directly to the output writer: this preserves the emitted text and avoids
    intermediate replacement strings. Do not scan every `global::` token instead: it was slower in the local probe.
- **Keep generic formatter registrations on individual assembly attribute lines**: grouping them costs Roslyn more
  to parse than it saves. Grouping the 83 formatter attributes removed 568 source bytes while keeping assembly
  attributes, compiled IL and wire output identical, but the indented list allocated about 11 KB more per isolated
  Roslyn parse in both load orders. Smaller indented groups still allocated more, and the unindented probes saved at
  most 3.8 KB per parse. The emitter grew from 915 to 952 IL bytes, 2,776 to 2,892 Arm64 FullOpts bytes, and 1,751 to
  1,850 x64 cross-JIT bytes. Whole-project timing showed no repeatable change. Both Roslyn builds matched all other
  outputs and preserved incremental reuse and collection.

## Spellings in the bodies

- **Let raw stores infer the type from their member expression or typed header literal; keep the explicit type on
  reads**, where the return value cannot drive inference. This removed 5,020 source bytes across 239 files in the
  unit test project. The consumer DLL size and normalized IL matched. The keyed write stayed at 129 IL bytes, 804
  Arm64 Tier 1 bytes and 541 x64 cross-JIT bytes. Local runtime timings overlapped the identical-binary control, at
  zero steady allocation.
- **Use target-typed construction (`new(...)`) for an ordinary constructor when the generated return or assignment
  names the exact type**: the type is already on the line. Keep explicit construction on the existing older-language
  path and in pool fallback expressions.
- **Emit constant type IDs directly in formatter and twin accessors**: when no generated caller needs a constant
  field, retaining it duplicates consumer metadata without changing the getter's IL.
- **Omit the empty member hook for object-only prefetch formatters when Core exposes the object-only interface;
  retain the existing generated contract against older Core versions.** This removed 15,136 source bytes across 148
  of the unit test project's 582 files. The wire output and the activation threshold matched in both compatibility
  directions. The eliminated loop and its timing controls: `optimize`, references/prefetch.md.
