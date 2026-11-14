# Benchmarks: file map

> Read when: adding, changing or running a .NET benchmark, or a script that turns benchmark results into the README, docs or a release body.

BenchmarkDotNet projects and the Python and shell scripts around them. benchmarks/README.md is the reference for
every option, class and environment variable: read it instead of guessing. The runtimes outside .NET keep their
benchmarks in `runtimes/*/bench`. `audit-code` audits the scripts, `benchmark` runs them, `audit-perf` and `optimize` use them.

```
README.md                    options of run.sh, the classes, MessagePack / protobuf-net / Google.Protobuf / JSON, member
                             by member, machine code
                             (asm/asm.py, cross JIT), the runtimes' benchmarks
run.sh                       the published runtime benchmark: v3 3.3.13 then the full v4 suite on one machine (QUICK=1,
                             FILTER=...); writes benchmarks/results/ (ignored)
Nino.Benchmark/              the BenchmarkDotNet suite: Cases/Collections, Cases/Graphs and Cases/Scenarios compare Nino
                             against MemoryPack, MessagePack and protobuf-net per class (SimpleTest, PolymorphicTest,
                             NullableTest and CollectionsTest's nullable collections against Google.Protobuf, through
                             Fixtures/cases.proto, and System.Text.Json too)
                             (SimpleTest, PolymorphicTest, NullableTest, GenericPolymorphicTest, PooledTest,
                             PooledGraphTest, ReferenceTest, VersionTolerantTest, FixedSizeTest, CommonTypesTest,
                             CollectionsTest, BclTypesTest, ScatteredTest, UntrustedTest, ExternalBufferTest), Workloads/ (WorkloadTest: 12 real-world
                             payloads, the headline numbers, also against Google.Protobuf through
                             workloads.proto, which --write-proto generates, and System.Text.Json), CpuProbe (block copies against Memmove),
                             Harness/Verify.cs (--verify: every case reads back right), NinoJobAttribute (the job, quick mode), PayloadColumn; V3/ +
                             Fixtures/ contains shared models and payloads (ProtobufModel.cs: protobuf-net's model, the
                             .proto files --write-proto generates, the read-back of Google.Protobuf's messages;
                             NullableBenchmarkData.cs: NullableTest's models, NullableFields and ManagedStruct, a
                             struct with a reference, and MemoryPack's SafeNullable copies).
                             Each class's models live in Fixtures/ (<Class>BenchmarkData.cs and the like), shared
                             with its v3 port. NINO_BENCH_V3=true: the v3 3.3.13 build of every class's Nino and
                             MemoryPack cases that 3.3.13 can express (V3/, each file listing what it does
                             differently and leaves out; V3/Versioned: the version-tolerant models with
                             WEAK_VERSION_TOLERANCE);
                             -p:NinoBenchLegacy for older refs
Nino.GeneratorBenchmark/     the source generator on a synthetic workload: generator time, generated size, emit,
                             analyzer, re-generation after edits; --generics adds generic slots and subtypes,
                             --calls top-level calls of Nino and of another serializer, --layouts a layout baseline
                             of every type (the analyzer's NINO034-NINO037 check), --runs the measured runs after
                             --warmup's (60 s, at least one run),
                             --no-analyzers, --json (medians, sizes, machine: report.yml's build section); its restore
                             downloads v3 3.3.13's generator and runtime (PackageDownload) for --generator/--core
shards.json                  the classes and the filters of each CI shard: "report" (report.yml, x64 and Arm64) and
                             "ab" (ab-bench.yml)
compare/                     comparisons of two runs, for development (never published on their own)
  ab.py                      two github reports of the same benchmarks (base, head): head against base and MemoryPack;
                             head alone (one report): head against MemoryPack (ab-bench.yml)
  compare_v3.py              v3 3.3.13 against v4 from a v3 and a v4 report (every case of V3/), with both MemoryPack runs (run.sh, and
                             the release body's v3 section)
asm/                         the machine code of the cases
  asm.py                     the Tier1 machine code of the cases: dump / diff / compare REF (`compare origin/main`
                             before committing a read or write path change)
  jitwarm/                   the startup hook asm.py runs Nino.Benchmark with: calls each case until Tier1, then exits
  cross-jit.sh               installs the JIT for the other 64-bit architecture, so DOTNET_JitDisasm prints x64 and
                             Arm64 code on one machine
members/                     member-by-member v3 against v4 (run.sh: v3, v4, v3 in process; DISASM=1 for listings):
                             shared/Parts.cs built as v3/P3.csproj (NuGet 3.3.13) and v4/P4.csproj; table.py,
                             split-asm.py; results/ ignored
reports/                     what a release publishes: the results of runners as release assets, the body's charts,
                             README and docs
  bdn.py                     the one parser of BenchmarkDotNet github reports, units and FLOOR_NS (1 ns), for every
                             script that reads one (compare/ and members/table.py import it from here); show: the time
                             format of every section's table; cpu: how tables name the machine
  merge_reports.py           merges the reports of parallel jobs into one report (report.yml)
  release_assets.py          the release's benchmark assets: their names (NAMES), benchmarks-manifest.json, the release
                             (GitHub, or a directory in a dry run), fetch, has (report.yml's skip checks)
  release_body.py            publish: attaches a job's results, draws a chart per section (README's chart groups in
                             a .NET section), writes the body's benchmark part (<details> with summary, charts and
                             links) from the manifest (report.yml)
  charts.py                  the charts (README's and the release body's) as SVG (light and dark), the <picture> and
                             <details> that show them, and the overview PNG by headless Chrome (readme.py --png)
  readme.py                  writes the numbers of README.md / README.zh.md, the chart groups (GROUPS, chart_groups;
                             docs/assets, README and docs/v4-benchmarks.md, the release body's too) and the runtime
                             and build tables of docs/v4-benchmarks.md from a release's assets (--assets, --release)
                             or a joined report (--v4) and a build section (--build)
  generator_report.py        the build section (v3 3.3.13's source generator against the release's) from
                             Nino.GeneratorBenchmark's --json files, and its parser (readme.py)
  runtimes_report.py         the Go, Rust and TypeScript sections from each benchmark's output
  unity_report.py            the Unity section from the IL2CPP player's results (unity.yml artifacts)
  test_generator_report.py   tests of generator_report.py and readme.py --build on fixtures/generator/
  test_unity_report.py       tests of unity_report.py on fixtures/
  test_release_body.py       a dry run of report.yml's publish jobs (no network, no Chrome), the old sections, the
                             PNG crop (agents.yml runs every test_*.py)
  fixtures/                  unity-bench-<version>/ results.xml + build.txt, and unity-bench-empty/, for those tests;
                             generator/ v3-<roots>.json and v4-<roots>.json (the old local run's numbers)
.gitignore                   results/
```

## Rules for these files

- The asset names (reports/release_assets.py) and the formats of the assets (the merged reports, the sections of
  runtimes_report.py, unity_report.py, generator_report.py) are read by the docs site (Nino.Docs
  `scripts/sync-benchmarks.ts`), which parses the old sections' headings in the bodies of releases up to
  v4.0.0-rc.9 too: change one only together with its parser there.
- The models (`Fixtures/*.cs` but `CaseFormats.cs`, `ProtobufModel.cs` and `TolerantClass.cs`, and
  `Workloads/*Models.cs`) compile in the v3 build too: a class's models go in `Fixtures/`, never in its `Cases/` file,
  an attribute or member 3.3.13 lacks goes behind `#if NINO_BENCH_V3` (the v3 form first), and a version-tolerant
  model goes in the v3 project that tolerates versions (`V3/Versioned`: `VersionedModels.cs`, `TolerantClass.cs`; a
  collection of one that a case reads, such as `TolerantClass[]`, named in its `CollectionTypes.cs`, since v3 writes
  overloads only for the collection types its project names). A type v3's generator cannot take stays in its v4
  `Cases/` file, which the v3 build does not compile (CollectionsTest's `SelfTree`).
- Program.cs and the Harness files the v3 build lists (`Nino.Benchmark.csproj`, `NinoBenchV3`) compile against Nino
  3.3.13 with only those files: guard what they reach outside them with `#if !NINO_BENCH_V3`, and build
  `NINO_BENCH_V3=true dotnet build -c Release` (then `--verify` on `bin/v3`) after changing them or a model. No CI job
  but report.yml builds it, so a break shows only at a release (51a7bd18's `--write-proto` did, until the rc.10
  review).
- A new benchmark case gets its v3 port in the class's `V3/` file and its v3 globs in `shards.json` (Nino and
  MemoryPack only), or a line in that file saying why 3.3.13 cannot express it.
- A script that reads BenchmarkDotNet reports does it through `reports/bdn.py` (one parser, one unit table, one floor);
  the 1 ns floor is the docs site's (`src/lib/benchmarks.ts`) too.

## Running them

How to run, A/B on runners and what may be published: the `benchmark` skill.
