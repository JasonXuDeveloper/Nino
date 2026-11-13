# Tests: file map

> Read when: adding or changing a test, or finding where a feature, a platform or hostile input is tested.

The .NET test projects (MSTest), the trimmed / NativeAOT program, the runtime-internals program and the fuzzer. The
runtimes outside .NET test themselves (`runtimes/*/AGENTS.md`); the shared corpus is `tests/conformance/`. `audit-code`
audits the structure, `audit-security` the fuzzer and the hostile-input tests; the `verify` skill lists the commands.

```
conformance/                 the corpus every runtime reads and writes, and Nino.Conformance, which writes and
                             verifies it (conformance/AGENTS.md)
Nino.UnitTests/              the main suite (net8.0, net10.0, and net11.0 with a .NET 11 SDK; netstandard2.1
                             compile-only, as Unity builds it);
                             its types are marked version tolerant; -p:NinoCoreTfm picks a Nino.Core
                             build; folders below
Nino.UnitTests.Subset/       netstandard2.1 library of types the main suite needs positional (raw, fixed-size, block
                             and deep types) or compiled with a newer C# (required members, primary constructors),
                             compiled with the Roslyn 4.3 generator
Nino.RuntimeTests/           one program (net10.0; net472 with the netstandard2.0 builds) that CI's "Runtime internals"
                             job runs on .NET (x64, Arm64, Arm32 under qemu, Windows x86), Mono and .NET Framework 4.8:
                             the same bytes everywhere, and every fast path through a runtime's internals taken
                             (FastPaths: a row per startup flag of CollectionViews, HashTables, ConcurrentTables,
                             SortedSetNodes, ObjectTypes, HashTrees, SortedTrees); BclStructs.cs: every BCL struct
                             inside a raw struct, and tuples and pairs, against CoreCLR x64's bytes both ways
                             (BclFixtures.cs and BclFixtureTypes.cs, linked from Unity's Assets/Test/Runtime, which
                             shares them; `--write-fixtures` rewrites BclFixtures.cs), and each runtime's layout of them
                             (`# layout` lines; layouts.py turns CI's into docs/layouts.md's tables); FunctionPointers.cs:
                             function pointers in raw structs, 8 bytes zero-extended from the generated registry: a
                             field reported as an IntPtr (as Mono and IL2CPP report one; every build) and declared
                             delegate* fields, directly, through an alias and captured by a primary constructor, the
                             last two reached only by generic code (net10.0 only: stock Mono 6.8 and 6.12 cannot load
                             such a struct)
Nino.AotTests/               a program over every kind of type, published trimmed and with NativeAOT (warnings are
                             errors); prints a hash of everything written to compare runs, and checks that the block
                             shuffle keeps its instruction (pshufb) under NativeAOT
Nino.AspNetCore.Tests/       Nino.AspNetCore: MVC and Minimal API round trips, errors, allocations, unloading
Nino.Codegen.Tests/          nino-gen: the IR (ModelTests), targets and command line (TargetTests), nino-gen layouts
                             (LayoutsCommandTests), the layout attribute contract, and GeneratedFilesTests: the files committed under runtimes/ are what nino-gen
                             writes (NINO_UPDATE_GENERATED=1 rewrites them); Shapes.cs: shapes the corpus lacks;
                             InspectCommandTests: nino-gen inspect over the whole corpus (valid, malicious, truncated,
                             mutated) and the text of chosen cases against Inspect/*.txt (NINO_UPDATE_GENERATED=1
                             rewrites them), with InspectTypes.cs (custom-formatter members)
Nino.Codegen.Integration.Tests/  build/Nino.Codegen.targets in real builds (BuildTests), and the generated
                             TypeScript (TypeScriptTests, harness.ts under Node), Go (GoTests) and Rust (RustTests)
                             against the bytes .NET writes for Samples.cs; GenericSamples.cs: the generic values and
                             the helpers the Go and Rust tests share
Nino.Fuzz/                   the deserializer fuzzer (README.md): the commands at its root (Program.cs: fuzz, repro,
                             list; Probes.cs: deep nesting, colliding keys; SelfTest.cs; LibFuzzerEntry.cs); Engine/
                             (mutations under a supervisor, workers, guard pages, findings), Checks/ (Invariants.cs,
                             StaleMemory.cs: freed memory left by rejected reads), Inputs/ (the root types, their
                             samples and the targets); libfuzzer/ (SharpFuzz driver, run.sh); Golden/ seeds it from
                             GoldenBytesTests; findings/ is ignored
Shared/                      RepositoryFiles.cs + .props: the repository root as assembly metadata, for tests that check
                             committed files (Nino.UnitTests, Nino.Codegen.Tests, Nino.Codegen.Integration.Tests)
```

## Nino.UnitTests/

```
Serialization/               round trips of the public API: private members, custom formatters, reference tracking,
                             streams, threads, issue regressions (IssueTests), serialization callbacks (CallbackTests:
                             order and counts on every path, with Subset's CallbackTypes.cs); GoldenBytesTests pins the
                             wire format for every Nino.Core build (and seeds the fuzzer)
  Types/                     primitives, BCL types, DateTimeOffset, tuples and pairs, records, generics
  Strings/                   string encodings (StringEncodingTests) and UTF-8 on the wire (Utf8WireTests)
Collections/                 arrays, BCL and user collections, empty ones, Stack/Queue through their arrays, the
                             prefetch of long top-level arrays and lists of scattered objects and the interceptors that
                             route them there (PrefetchTests)
  FastPaths/                 the collection fast paths (arrays, blocks, dictionaries, linked lists, priority queues,
                             sorted collections and lists; CollectionFastPathFixtures) and the HashTables fill
Layout/                      raw structs, padding, the portable (canonical) layout, BCL structs inside raw structs
                             (BclStructLayoutTests: CoreCLR's auto layout of tuples, DateTimeOffset), block structs,
                             fixed-size types, the unmanaged constraint, top-level fast paths
Polymorphism/                inheritance, dispatch, the registry, generic polymorphic bases, cross-assembly subtypes,
                             what attributes on interface members do and explicit implementations of them
                             (InterfaceMemberAttributeTests)
Versioning/                  version-tolerant and keyed types read across versions
Reuse/                       reuse deserialization (ref overloads), [NinoFactory], pools, recycling ownership, null targets
Registration/                [NinoFormatter] registrations and lazy registration by type id
Security/                    hostile input: StrictReadTests, HashFloodingTests, UntrustedModeTests, UntrustedInputTests,
                             ValueCheckLayoutTests (Untrusted mode's value checks over random struct layouts, scalar and
                             vector, against the types' own constructors), SettingsTests (limits frozen on first use,
                             each case in its own load context)
  Nesting/                   DeepNesting, BoundedNesting, SelfNestingCollection, WriteDepth
Generator/                   the generator, the analyzer and the code fixes: incremental caching
                             (IncrementalGeneratorTests), the tools/CodeGen output checks (*FormattersGenerationTests)
  Analyzer/                  AnalyzerTests (every rule; the AnalyzerReleases files), a partial file per group of rules
                             (Types, Construction, Members, Utf8, InterfaceMembers, Vectors, Features, Pooling,
                             Registrations); the callbacks' rules and fixes (CallbackAnalyzerTests: NINO038-NINO044)
  CodeFixes/                 CodeFixTests, a partial file per group of fixes (Types, Members, Annotations, Utf8,
                             InterfaceMembers, WeakTolerance, Features, Pooling)
  LayoutBaseline/            the layout baseline: LayoutRulesTests (its rules and TOML on the shared sources, linked),
                             LayoutAnalyzerTests (NINO034-NINO037, their fixes and the refactoring),
                             LayoutBaselineTests, LayoutMatchesParityTests (LayoutEmitter.Matches against Emit)
  GeneratedCode/             what the emitted code does: regions only patched (PatchOnlyRegionTests), formatters that
                             keep the read context (ReadContextParityTests), __Top twins (TopLevelInlineTests),
                             receiver aliases (ReceiverAliasTests)
Corner/                      corner cases grouped into Api/, Layout/, Modeling/, and ObjectGraphs/;
                             CornerCompiler builds in-memory fixtures, and Modeling/ShapeMatrixTests.cs
                             generates, compiles and round-trips type hierarchies
Platforms/                   Unity (UnityTypesTests against the UnityEngine stub, UnityMock/; UnityPackageDllTests: the
                             Unity package's committed DLLs have the public API of their sources), HybridCLR hot-update
                             assemblies, the NinoKernels hook, ET framework collection shapes
Infrastructure/              shared helpers: Rt (round trips, FromHex/ToHex, OnSmallStack), GuardedMemory (bytes placed
                             against an inaccessible page, so a read past them crashes), SharedTypes,
                             TestCompilation, TestReferences (the analyzer tests' references, created once),
                             MSTestSettings (classes run in parallel)
Nino.UnitTests.csproj        targets, references (Nino.Core, Immutable, UnityTypes, the
                             generator's Roslyn 4.3 build, tools/CodeGen templates)
```

## Rules for these files

- A hostile-input check asserts the exact exception type (`Assert.ThrowsExactly<NinoDataException>`); the caller's
  mistakes and the write side throw `InvalidOperationException`.
- A new way to declare a type adds a dimension to `Corner/Modeling/ShapeMatrixTests.cs`; a new path through a runtime's
  internals adds a case with known contents and a `FastPaths` row to `Nino.RuntimeTests`.
- Test classes run side by side, the methods of one class in sequence (`Infrastructure/MSTestSettings.cs`). A test
  that changes or measures process-wide state (`NinoKernels`, the shared pools, the whole heap, unloading) is
  `[DoNotParallelize]` (it then runs alone, after the others); counters shared by the methods of one class need
  nothing. Split a slow data-driven test over classes, as `ShapeMatrixTests` puts two rows in each, so its rows
  share out over the cores.
- A type of the main suite is `[NinoType(format: NinoFormat.VersionTolerant)]` (keyed where a test needs it); a test
  that needs a positional type (raw, fixed size, block) puts it in `Nino.UnitTests.Subset`.
- Committed generated files are checked, never edited by hand: `GeneratedFilesTests` (runtimes/), the
  `*FormattersGenerationTests` (Nino.Core's `.g.cs`).
