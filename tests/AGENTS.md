# Tests: file map

> Read when: adding or changing a test, or finding where a feature, a platform or hostile input is tested.

The .NET test projects (MSTest), the trimmed / NativeAOT program, the runtime-internals program and the fuzzer. The
runtimes outside .NET test themselves (`runtimes/*/AGENTS.md`); the shared corpus is `conformance/`. `audit-code`
audits the structure, `audit-security` the fuzzer and the hostile-input tests; the `verify` skill lists the commands.

```
Nino.UnitTests/              the main suite (net8.0, net10.0; netstandard2.1 compile-only, as Unity builds it);
                             WEAK_VERSION_TOLERANCE makes its types version tolerant; -p:NinoCoreTfm picks a Nino.Core
                             build; folders below
Nino.UnitTests.Subset/       netstandard2.1 library of types the main suite needs in an assembly without
                             WEAK_VERSION_TOLERANCE or with a newer C# (raw, fixed-size, block and deep types,
                             required members, primary constructors), compiled with the Roslyn 4.3 generator
Nino.RuntimeTests/           one program (net10.0; net472 with the netstandard2.0 builds) that CI's "Runtime internals"
                             job runs on .NET (x64, Arm64, Arm32 under qemu, Windows x86), Mono and .NET Framework 4.8:
                             the same bytes everywhere, and every fast path through a runtime's internals taken
                             (FastPaths: a row per startup flag of CollectionViews, HashTables, ConcurrentTables,
                             SortedSetNodes, ObjectTypes, HashTrees, SortedTrees)
Nino.AotTests/               a program over every kind of type, published trimmed and with NativeAOT (warnings are
                             errors); prints a hash of everything written to compare runs, and checks that the block
                             shuffle keeps its instruction (pshufb) under NativeAOT
Nino.AspNetCore.Tests/       Nino.AspNetCore: MVC and Minimal API round trips, errors, allocations, unloading
Nino.Codegen.Tests/          nino-gen: the IR (ModelTests), targets and command line (TargetTests), the layout attribute
                             contract, and GeneratedFilesTests: the files committed under runtimes/ are what nino-gen
                             writes (NINO_UPDATE_GENERATED=1 rewrites them); Shapes.cs: shapes the corpus lacks
Nino.Codegen.Integration.Tests/  build/Nino.Codegen.targets in real builds (BuildTests), and the generated
                             TypeScript (TypeScriptTests, harness.ts under Node), Go and Rust (GoTests, RustTests in
                             GenericSamples.cs) against the bytes .NET writes for Samples.cs
Nino.Fuzz/                   the deserializer fuzzer (README.md): fuzz (mutations under a supervisor, guard pages,
                             Invariants.cs, StaleMemory.cs: freed memory left by rejected reads), probes (deep nesting,
                             colliding keys), selftest, repro, list, libfuzzer/ (SharpFuzz driver, run.sh); Golden/
                             seeds it from GoldenBytesTests; findings/ is ignored
Shared/                      RepositoryFiles.cs + .props: the repository root as assembly metadata, for tests that check
                             committed files (Nino.UnitTests, Nino.Codegen.Tests, Nino.Codegen.Integration.Tests)
```

## Nino.UnitTests/

```
Serialization/               round trips of the public API: primitives, strings (StringEncodingTests, Utf8WireTests),
                             BCL types, DateTimeOffset, tuples, records, generics, private members, custom formatters,
                             reference tracking, threads, issue regressions (IssueTests); GoldenBytesTests pins the
                             wire format for every Nino.Core build (and seeds the fuzzer)
Collections/                 arrays, BCL and user collections, empty ones, Stack/Queue through their arrays, the
                             HashTables fill, the collection fast paths, the prefetch of long top-level arrays and lists
                             of scattered objects and the interceptors that route them there (PrefetchTests)
Layout/                      raw structs, padding, the portable (canonical) layout, block structs, fixed-size types,
                             the unmanaged constraint, top-level fast paths
Polymorphism/                inheritance, dispatch, the registry, generic polymorphic bases, cross-assembly subtypes
Versioning/                  version-tolerant and keyed types read across versions
Reuse/                       reuse deserialization (ref overloads), [NinoFactory], pools, recycling ownership, null targets
Registration/                [NinoFormatter] registrations and lazy registration by type id
Security/                    hostile input: StrictReadTests, HashFloodingTests, UntrustedModeTests, UntrustedInputTests,
                             SettingsTests (limits frozen on first use, each case in its own load context), the nesting
                             tests (DeepNesting, BoundedNesting, SelfNestingCollection, WriteDepth)
Generator/                   AnalyzerTests (every rule; the AnalyzerReleases files), CodeFixTests, incremental caching,
                             __Top twins, receiver aliases, the tools/CodeGen output checks (*FormattersGenerationTests)
Corner/                      corner cases compiled in memory (CornerCompiler): ShapeMatrixTests (every way to declare a
                             type hierarchy, generated, compiled and round-tripped), construction, members, generics,
                             pooling, references, versioning, concurrency, unloading, layout metadata, raw-rule
                             agreement between generator and runtime, cross-assembly raw structs
Platforms/                   Unity (UnityTypesTests against the UnityEngine stub, UnityMock/), HybridCLR hot-update
                             assemblies, the NinoKernels hook, ET framework collection shapes
Infrastructure/              shared helpers: Rt (round trips), SharedTypes, TestCompilation, TestReferences
Nino.UnitTests.csproj        targets, WEAK_VERSION_TOLERANCE, references (Nino.Core, Immutable, UnityTypes, the
                             generator's Roslyn 4.3 build, tools/CodeGen templates)
```

## Rules for these files

- A hostile-input check asserts the exact exception type (`Assert.ThrowsExactly<NinoDataException>`); the caller's
  mistakes and the write side throw `InvalidOperationException`.
- A new way to declare a type adds a dimension to `Corner/ShapeMatrixTests.cs`; a new path through a runtime's
  internals adds a case with known contents and a `FastPaths` row to `Nino.RuntimeTests`.
- A test that needs a type outside `WEAK_VERSION_TOLERANCE` (raw, fixed size, block) puts it in
  `Nino.UnitTests.Subset`.
- Committed generated files are checked, never edited by hand: `GeneratedFilesTests` (runtimes/), the
  `*FormattersGenerationTests` (Nino.Core's `.g.cs`).
