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
                             against MemoryPack, MessagePack and protobuf-net per class
                             (SimpleTest, PolymorphicTest, GenericPolymorphicTest, PooledTest, PooledGraphTest,
                             ReferenceTest, VersionTolerantTest, FixedSizeTest, CommonTypesTest, CollectionsTest,
                             BclTypesTest, ScatteredTest, UntrustedTest), Workloads/ (WorkloadTest: 12 real-world
                             payloads, the headline numbers, also against Google.Protobuf through
                             workloads.proto, which --write-proto generates, and System.Text.Json), CpuProbe (block copies against Memmove),
                             Harness/Verify.cs (--verify: every case reads back right), NinoJobAttribute (the job, quick mode), PayloadColumn; V3/ +
                             Fixtures/ contains shared models and payloads. NINO_BENCH_V3=true: the v3 3.3.13 build of SimpleTest; -p:NinoBenchLegacy for older refs
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
  compare_v3.py              v3 3.3.13 against v4 from two SimpleTest reports, with both MemoryPack runs (run.sh, and
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
reports/                     what a release publishes: reports of runners into the release body, README and docs
  bdn.py                     the one parser of BenchmarkDotNet github reports, units and FLOOR_NS (1 ns), for every
                             script that reads one (compare/ and members/table.py import it from here); show: the time
                             format of every release-body table; cpu: how tables name the machine
  merge_reports.py           merges the reports of parallel jobs into one report (report.yml)
  compact_report.py          shortens a merged report for a release body (125,000-character limit)
  readme.py                  writes the numbers of README.md / README.zh.md, the chart (docs/assets) and the runtime
                             tables of docs/v4-benchmarks.md from a joined report (--v4), and the Build row and build
                             tables from the release body's build section (--build)
  generator_report.py        the release body's build section (v3 3.3.13's source generator against the release's)
                             from Nino.GeneratorBenchmark's --json files, and its parser (readme.py --build)
  test_generator_report.py   tests of generator_report.py and readme.py --build on fixtures/generator/
  release_body.py            the release body's benchmark sections (Performance Report, v3 comparison, Arm64) for
                             report.yml; the docs site finds them by their headings
  runtimes_report.py         the release body's Go, Rust and TypeScript sections from each benchmark's output
  unity_report.py            the release body's Unity section from the IL2CPP player's results (unity.yml artifacts)
  test_unity_report.py       tests of unity_report.py on fixtures/ (agents.yml runs them)
  fixtures/                  unity-bench-<version>/ results.xml + build.txt, and unity-bench-empty/, for those tests;
                             generator/ v3-<roots>.json and v4-<roots>.json (the old local run's numbers)
.gitignore                   results/
```

## Rules for these files

- Headings the release body uses (reports/release_body.py, runtimes_report.py, unity_report.py, generator_report.py) are read by the docs site
  (Nino.Docs `scripts/sync-benchmarks.ts`): change one only together with its parser there.
- Program.cs and the Harness files the v3 build lists (`Nino.Benchmark.csproj`, `NinoBenchV3`) compile against Nino
  3.3.13 with only those files: guard what they reach outside them with `#if !NINO_BENCH_V3`, and build
  `NINO_BENCH_V3=true dotnet build -c Release` after changing them. No CI job but report.yml builds it, so a break
  shows only at a release (51a7bd18's `--write-proto` did, until the rc.10 review).
- A script that reads BenchmarkDotNet reports does it through `reports/bdn.py` (one parser, one unit table, one floor);
  the 1 ns floor is the docs site's (`src/lib/benchmarks.ts`) too.

## Running them

How to run, A/B on runners and what may be published: the `benchmark` skill.
