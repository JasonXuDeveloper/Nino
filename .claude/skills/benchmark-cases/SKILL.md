---
name: benchmark-cases
description: "One set of benchmark cases on every platform - the .NET suite's [BenchCase]s and adapters (BenchGen), the bytes and case list --write-data hands to Go, Rust, TypeScript and Unity, nino-gen's codecs looked up by .NET type, one adapter per competitor in each language, Broken cases, the case manifest, the report formats and the docs site's parsers that read them, and the checks that prove a case or a competitor. Use when adding or changing a benchmark case, adding or upgrading a competitor (MemoryPack, MessagePack, protobuf-net, Google.Protobuf, System.Text.Json, LuminPack, or a Go, Rust or TypeScript library), when a case is missing on one platform, or when a report's format changes. Triggers: BenchCase, BenchGen, bench-cases.json, cases.json, roots.txt, generics.txt, --write-data, Adapters/, adapter_*.go, Broken, Quick, Nino.Benchmark.Schema, NinoCodecOf, ninoCodecOf, with_nino_type, BenchCases.cs, new case, add a case, competitor, new serializer, sync-benchmarks.ts, 加用例, 加个用例, 新用例, 加竞品, 对比库, 加一个库, 用例清单, 各平台用例."
metadata:
  internal: true
---

# Benchmark cases

Every platform times the same cases: the C# models of `runtimes/csharp/benchmarks` are the single source, and Go,
Rust, TypeScript and Unity read the bytes .NET writes for them (#331). A case is declared once, in C#; a competitor is
one adapter in its language. Running, A/B on runners and publishing are the `benchmark` skill; the files are in
`runtimes/csharp/benchmarks/AGENTS.md` and its README ("Cases and adapters", "Runtimes outside .NET"), and each
runtime's `AGENTS.md` for its `bench`.

## How a case reaches every platform

```
[BenchCase] + its value's field (C#)  --BenchGen-->  <Class>.Cases.g.cs (every .NET library, v4 and v3), bench-cases.json
Nino.Benchmark --write-data DIR       -->  <Case>.nino.bin, <Case>.protobuf.bin, cases.json, roots.txt, generics.txt
nino-gen on Nino.Benchmark.Schema     -->  each language's types and codecs (roots.txt names the top-level values)
each harness                          -->  reads cases.json, looks the codec up by the case's .NET type, reads .NET's
                                           bytes, writes them back identically, then times every adapter
```

- Declare a case only in C#, then regenerate (BenchGen, then each platform's `generate.sh`). Why: no platform keeps
  a case table of its own; Go (`model.NinoCodecOf`, `bench/cases.go`), Rust (`with_nino_type` /
  `with_nino_serde_type`, `benches/compare.rs`) and TypeScript (`ninoCodecOf`, `bench/bench.ts`) find each case's
  codec by the .NET type `cases.json` names, and Unity's `bench/generate.sh` writes `BenchCases.cs` from it. A
  generated per-language case table was considered and dropped: it would hold nothing the manifest lacks, and in Go
  the lookup cost nothing measurable against direct calls (SimpleStruct, locally; #372).
- Make a case's value a type nino-gen can model, or accept that a platform leaves it out with its reason. Why: a
  harness times a case only when its generated code reads .NET's bytes and writes the same bytes back, so a benchmark
  run is also a conformance check; a type nino-gen does not model is skipped there, never approximated. A root that
  is not a `[NinoType]` reaches nino-gen through `roots.txt` (`--roots`), never by wrapping it in a class, which would
  change the .NET bytes. A closed generic `[NinoType]` a value holds is listed in `generics.txt`, which a reader that
  never writes it (Unity's IL2CPP player) registers first.
- Keep the models C# 9 on netstandard2.1's API in `Fixtures/` (and `Workloads/*Models.cs`). Why:
  `Nino.Benchmark.Schema` builds the same files for nino-gen and Unity, with the other libraries' attributes as
  stand-ins, and ci.yml's "Check the benchmark schema" fails when nino-gen writes other code for its types than for
  `Nino.Benchmark`'s. A model that needs a newer C# (the C# unions of `PolymorphicTest`'s `UnionArray`, C# 15) goes
  in a file of its own behind `NET11_0_OR_GREATER && !NINO_BENCH_V3`, which only the benchmark's and the schema's
  net11.0 builds compile; `cases.json` marks such a case (`union`) and Unity's `bench/generate.sh` leaves it out (#328).

## Adding a case on .NET

- In a class that declares its cases (`[BenchCase]`: `SimpleTest`, `PolymorphicTest`, `WorkloadTest`,
  `GenericPolymorphicTest`, `CollectionsTest`, `CommonTypesTest`, `FixedSizeTest`, `BclTypesTest`, `NullableTest`,
  `VersionTolerantTest`, `StringsTest`, `RawArraysTest`, `AppDataTest`: every class that compares libraries case by case; the scenario classes, Pooled*, Reference,
  Untrusted, ExternalBuffer and Scattered, stay hand-written with their `V3/` ports): add the attribute and
  the value's field, run `dotnet run --project runtimes/csharp/tools/BenchGen`, never edit a `.Cases.g.cs` by hand;
  the methods of every library and the v3 part follow from the adapters (`Nino.Benchmark/Adapters/`), and a new
  library is one adapter. The rest of this list (`Verify.cs`, chart groups, `shards.json`) still applies. Mark a case
  `Quick = true` when a labeled pull request's trend run should cover its kind of data (29 cases, about one per kind
  of shape: the subset ci.yml runs, `bench-cases.json`), and write its models in C# 9 in `Fixtures/` (block-scoped
  namespaces, no collection expressions or primary constructors): `Nino.Benchmark.Schema` builds them for nino-gen
  and Unity, and ci.yml checks its types against the benchmark's.
  `runtimes/csharp/benchmarks/README.md`, "Cases and adapters", has the details. Why: a hand-written method per
  library and case is about 4,000 lines that drift apart; the generated ones compile to the IL of the hand-written
  ones (identical IL in all 240 v4 and 80 v3 methods of the first three classes, and identical Tier1 code of their
  Nino cases; in the next five, identical IL in all 650 v4 and 200 v3 methods but for field names and the bytes moving
  from `Serialized<Case>[slot]` arrays to a field per library). Compare `asm.py` listings of interceptors by call site,
  never by name: the generated files renumber them (`Serialize295` was another call site after the move, and looked
  four times larger). Compare IL with the renamed fields mapped back (a script that rewrites the old names), never by
  eye. A migration that moves where values or bytes are allocated is proven by one A/B on runners of the classes it
  touched, not by IL alone: allocation order alone moves timings (4K aliasing).
- Keep a method no adapter expresses in the class, beside its cases (protobuf-net's `.Items`, a conversion of a model
  of its own, v3's way through a type it writes no overload for), rather than growing BenchGen into a language: an
  adapter is for what every case of a library does. A variant every case of a library needs (MessagePack with options)
  is an adapter `Like` the library's, gated by `When`, with the plain adapter's `Unless`.
- .NET: a class in `runtimes/csharp/benchmarks/Nino.Benchmark` with Nino and every competitor that can express the case (MemoryPack,
  MessagePack, protobuf-net; Google.Protobuf and System.Text.Json in `SimpleTest`, `PolymorphicTest`, `NullableTest`,
  `CollectionsTest`'s nullable collections and the workloads), a `Verify.cs` entry, its class in a chart group
  (`readme.py`'s `GROUPS`, the site's `dotnetGroups` in `src/lib/benchmarks.ts`; a class neither names lands in
  "Others"), its v3 port (`runtimes/csharp/benchmarks/AGENTS.md`, Rules), the class in `shards.json` (the report and A/B shards): a report
  shard picked by the minutes each shard's "Run benchmarks" step took in the last report, and at most about 70 cases
  of v4 and v3 together (`--list flat` of both builds with the shard's globs; about 15 minutes a shard plus half a
  minute a case, under report.yml's 150-minute timeout): a new shard (the next letter) rather than an overfull one
  (`audit-ci`, Fast and parallel). Name a case so that no other shard's glob matches it, and check that
  every case of both builds is in exactly one report shard (`fnmatch` of the `--list flat` names against every
  shard's globs): `CollectionsTest`'s globs end at the case (`*.CollectionsTest.*Queue`), so a `NullableIntQueue`
  would have run in shard i too (the collections of `int?` are `NullableQueueInts`, ...), and an A/B shard takes a
  class by prefix, so their 80 Nino and MemoryPack cases all landed in A/B shard e (187 cases, the others about 50)
  until CollectionsTest was split by verb between a and e. Setup that calls an ISA's API (a probe's `X86Base.CpuId`) checks `IsSupported`:
  ci.yml runs `--verify` on x64 and on Arm64 (`audit-ci`, Scripts only a release runs).
- A case that needs a process-wide setting frozen on first use (`NinoSettings.Untrusted`, `UntrustedTest`) sets it in
  its class's static constructor inside `try`/`catch (InvalidOperationException)` and never throws from
  `[GlobalSetup]` when it is already frozen: BenchmarkDotNet times each case in a process of its own, but
  `--verify` and asm.py's jitwarm hook run every class's setup in one process (a throwing guard aborted
  `asm.py compare`).
- A competitor that reads into types of its own (Google.Protobuf's generated messages) implements `IReadBack`
  (Verify.cs), which turns its result into the baseline's type outside the timed region. Generate its schema rather
  than writing it: `Workloads/workloads.proto` is protobuf-net's `GetSchema` of the models at
  `CompatibilityLevel.Level300`, `Fixtures/cases.proto` that of SimpleTest's and PolymorphicTest's models at
  protobuf-net's default (importing its `bcl.proto`, vendored in `Fixtures/protobuf-net/`; a top-level array named by a
  list contract, `Fixtures/CaseFormats.cs`), both through `ProtoFile` (`--write-proto`; the setup fails on a stale file; a case
  that names a message the file lacks does not build, and `--write-proto` needs a build: append the new messages to
  the `.proto` by hand, build, run `--write-proto`, build again), Google.Protobuf's data is parsed
  from protobuf-net's bytes, and its result reads back through protobuf-net, so no converter is written by hand. Number
  a protobuf type's subtypes from 1 and its own members from 20: protobuf-net reads the subtype field before the
  base's, and Google.Protobuf writes fields in number order, so base fields first made protobuf-net create the
  abstract base and throw. Models whose numbers protobuf-net's published rows already depend on (SimpleTest's and
  PolymorphicTest's subtypes at 100) keep them: `ProtobufModel.ReadBack` moves every `oneof` field first, at every
  level, before protobuf-net reads Google.Protobuf's bytes back (without it, `--verify` threw in protobuf-net's
  `ReadSubType` on every PolymorphicTest case). Two versions of one model (a previous release's bytes read by this one) are sealed types:
  Nino writes a type id for a class that is not sealed, and the two types' ids differ (`ProfileCache` threw "not a
  registered subtype"). Write System.Text.Json through a reused `Utf8JsonWriter` with `SkipValidation`, as the
  serializer's own writers do, its source generator with `IncludeFields` for field-only models.
- Write nullable values through each library's own nullable form, never plain `Nullable<T>` for MemoryPack: its
  `SafeNullable<T>` (the owner's choice, 2026-11-13), because MemoryPack writes a `Nullable<T>` of an unmanaged T as
  its memory layout and leaves the padding uninitialized (one value, different bytes, leftover memory in them), while
  SafeNullable writes (hasValue as a T, value) like Nino's canonical bytes; copy the data to SafeNullable outside the
  timed region and turn results back in `IReadBack` (`SafeNullables`, `Fixtures/NullableBenchmarkData.cs`). Protobuf
  has no null in a repeated field (protobuf-net throws "An element ... was null"): wrap each element with protobuf-net's
  `[NullWrappedValue]` (a message with an optional value) on a contract around the collection; nullable scalars take
  `[NullWrappedValue]` at `CompatibilityLevel.Level300` (`google.protobuf.Int32Value`, ...: Google.Protobuf's `int?`).
  Keep such contracts at Level300: at the default level `GetSchema` names `decimal?`'s wrapper `Wrapped.bcl.Decimal`,
  which protoc rejects, and `[NullWrappedValue]` on an enum writes a message while the schema keeps the bare enum
  (Google.Protobuf would misread it), so a nullable enum stays bare with a zero member the data never uses. `GetSchema`
  writes the message of a wrapped scalar once for every kind of member that needs it (`Wrappedstring` for a Level300
  `decimal?` and again for a null-wrapped `string`), and protoc rejects the second definition ("already defined in
  nino.cases"): `ProtoFile.Generate` keeps each identical top-level message once. A
  `Nullable<T>` of a message type cannot be wrapped in a repeated field at all ("No serializer for type
  System.Nullable`1[SimpleStruct]"): write an array of a contract with one optional field (`OptionalSimpleStruct`,
  the same bytes), made outside the timed region; a top-level null is such a contract with the field absent. A
  baseline that reads null (`NullableObject`) carries `[ReadsNull]`, so Verify checks the others read null too.
- In `WorkloadTest` a library that lacks a feature is measured through its usual workaround (copies of shared
  objects, a new instance instead of reading into one; the owner's choice for workloads), said in the model's comment
  and in runtimes/csharp/benchmarks/README.md; the other classes keep "no method rather than other data".

## Adding a case on the other platforms

- Go, Rust, TypeScript: the same data in each `bench` against that language's competitors. Every column ends with
  the same value: where a competitor has no such type (protobuf, MessagePack and JSON have no set), its deserialize
  timing makes what it read into the case's type (Rust `formats!`'s `$from_proto`, TypeScript's `Revive`), and where
  Nino's own form is a slice (Go's sets) the others read the same slices. A TypeScript scenario whose codec the
  generated code lacks is skipped, so runtime-ab can run the head's `bench.ts` on an older base.
- In `runtimes/*/bench` ([references/runtime-inputs.md](references/runtime-inputs.md), before adding or changing such a case, or when
  a TypeScript number moves without a code change): rotate the inputs of any microbenchmark that parses (K variants,
  round robin, made outside the timed region); keep only the running serializer's data live; read a runtime API newer
  than the bench off the module namespace and skip its task when undefined, judging such a line against a null line.

## Adding or upgrading a competitor

- When `runtimes/csharp/Directory.Packages.props` moves MemoryPack, MessagePack, protobuf-net, Google.Protobuf or LuminPack to a new version
  (or the benchmark's .NET, which is System.Text.Json's), update the version
  the docs site names in the same series: `git grep <old version>` in Nino.Docs (the home page's
  `src/components/home-sections.tsx`, the benchmark and `vs-*` pages in both languages, `scripts/csharp-check`). Why:
  the site syncs the numbers from the release's assets but writes each library's version by hand, so new numbers would
  sit next to the old version.
- Declare a case a library breaks on with `Broken = "<Library> <compile|throws|crash|wrong-read>: <reason>"`, never
  leave it out with `Without` (which means the library has no form of the case) or silently. Why: BenchGen lists it in
  `bench-cases.json`, and the charts, tables and the docs site draw it as error with the reason, while a missing row
  reads as n/a (#368). On a LuminPack upgrade, drop each LuminPack `Broken` (`bench-cases.json` lists them; #356),
  run BenchGen, the build and `--verify`, and keep only the ones that still fail.
- Add a competitor as LuminPack was (#304): one adapter in `Nino.Benchmark/Adapters/` with a method prefix no other
  library's starts with, its fastest documented calls into a reused writer sized like the others', its attributes on
  the models (never on MemoryPack's own copies, `SafeNullable<T>`), BenchGen, then the build and `--verify`, and a
  `Broken` with its kind and reason on every case that fails. Then the report shards'
  explicit-prefix globs (`shards.json`, SimpleTest's; keep each report shard under about 70 cases),
  `compare/ab.py`'s `RIVALS`, `reports/readme.py`'s `OTHERS` (the charts' order) and `NA` (when its missing cases are
  bugs to show n/a rather than cases it cannot express), and the docs site's `otherLibs` (Nino.Docs
  `src/lib/benchmarks.ts`). Run every case before trusting it. Why: LuminPack 1.1.9 compiled and then read its own
  bytes back wrong in 13 cases, crashed the process reading a `BitArray` (SIGSEGV), threw writing generic bases over
  tuples, and its byte[] `Serialize` of a 1,000-element polymorphic list wrote past its growing 32 KiB writer and
  corrupted the native heap (`malloc(): corrupted top size`; `Harness/LuminPackBuffers.cs` sizes its writers up front).
  A process that dies in `--verify` names no case: print each method before calling it (a local edit of
  `Harness/Verify.cs`) to find it. Keep a competitor's source generator out of the v3 build and in the narrowest
  mode that still writes every type a case needs (LuminPack's `Medium`: `Full` wrote 8.6 MB and failed on
  `BclTypesTest`'s types, `Light` wrote no top-level arrays).
- Add a competitor outside .NET as one adapter in that language's bench, its column after Nino's in the report's
  order: Go, a type implementing `Adapter` (`Name`, `Prepare`) in `bench/adapter_<name>.go` and an entry in
  `adapters` (`bench/adapter.go`), its `Prepare` returning the codec through `checked`, or nil and the reason; Rust,
  an entry of `formats()` for a serde format, or a function beside `protobuf` in `benches/compare.rs`, which `guarded`
  runs; TypeScript, a name in `serializers` and its entry in `make` (`bench/bench.ts`), returning its tasks or the
  reason. Then its column in `runtimes_report.py` (the language's serializers, in order) and the docs site's
  `bench-runtimes.json` reader if the section's columns change. Why: the harness checks every adapter the same way
  (what it reads back must write .NET's Nino bytes) and leaves a case out with the reason rather than timing other
  work, so an adapter is the only place a library's code lives.

## Reports and the docs site

- A new result, asset name or format changes with its reader in Nino.Docs (`scripts/sync-benchmarks.ts`, which reads
  the assets by name) and the components that show it (`audit-docs`): the names are `release_assets.py`'s `NAMES`.
- Keep each report's headings and columns; only case names follow C#'s. Why: Nino.Docs' `scripts/sync-benchmarks.ts`
  finds every section by its heading and reads each table by its columns (the .NET reports by `Method` and `Mean`,
  the runtime, Unity and build sections by tables with neither), for releases back to v4.0.0-rc.9; a new column or a
  renamed heading lands in the same series as its parser there and its fixture (`scripts/fixtures/`).
- Name a case's group on the site through `dotnetGroups` (Nino.Docs `src/lib/benchmarks.ts`) and in `readme.py`'s
  `GROUPS`: Unity's charts and the runtime tables map each case to its group by the .NET case name (`unityCaseOf`).

## Checks

Run what the change reaches before pushing:

```bash
dotnet run --project runtimes/csharp/tools/BenchGen -- --check          # generated cases and bench-cases.json current
dotnet run --project runtimes/csharp/benchmarks/Nino.Benchmark -c Release -- --verify   # every case reads back right
NINO_BENCH_V3=true dotnet build runtimes/csharp/benchmarks/Nino.Benchmark -c Release    # the v3 build (then --verify on bin/v3)
(cd runtimes/go/bench && ./generate.sh --data && go test -run TestCases .)                # Go: each case and adapter checked
(cd runtimes/rust && bench/generate.sh --data && cargo test -p nino-bench --bench compare) # Rust: each benchmark once
NINO_BENCH_TIME=1 runtimes/typescript/bench/run.sh '<Case>'                               # TypeScript: the case runs
python3 runtimes/csharp/benchmarks/reports/test_release_body.py                           # a report format changed
```

ci.yml runs the first two and the schema check, runtimes.yml the Go, Rust and TypeScript benches on every case, and
unity.yml's benchmark run (dispatched with `benchmark: true`) the Unity player; a case missing from a report there is
the harness's reason on stderr, never silence.
