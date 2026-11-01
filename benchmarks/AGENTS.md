# Benchmarks: file map

> Read when: adding, changing or running a .NET benchmark, or a script that turns benchmark results into the README, docs or a release body.

BenchmarkDotNet projects and the Python and shell scripts around them. benchmarks/README.md is the reference for
every option, class and environment variable: read it instead of guessing. The runtimes outside .NET keep their
benchmarks in `runtimes/*/bench`. `audit-code` audits the scripts, `benchmark` runs them, `audit-perf` and `optimize` use them.

```
README.md                    options of run.sh, the classes, MessagePack / protobuf-net, member by member, machine code
                             (asm.py, cross JIT), the runtimes' benchmarks
run.sh                       the published runtime benchmark: v3 3.3.13 then the full v4 suite on one machine (QUICK=1,
                             FILTER=...); writes benchmarks/results/ (ignored)
Nino.Benchmark/              the BenchmarkDotNet suite: Nino against MemoryPack, MessagePack and protobuf-net per class
                             (SimpleTest, PolymorphicTest, GenericPolymorphicTest, PooledTest, PooledGraphTest,
                             ReferenceTest, VersionTolerantTest, FixedSizeTest, CommonTypesTest, CollectionsTest,
                             BclTypesTest, ScatteredTest, UntrustedTest), CpuProbe (block copies against Memmove),
                             Verify.cs (--verify: every case reads back right), NinoJobAttribute (the job, quick mode), PayloadColumn; V3/ +
                             NINO_BENCH_V3=true: the v3 3.3.13 build of SimpleTest; -p:NinoBenchLegacy for older refs
Nino.GeneratorBenchmark/     the source generator on a synthetic workload: generator time, generated size, emit,
                             analyzer, re-generation after edits
shards.json                  the classes and the filters of each CI shard: "report" (report.yml, x64 and Arm64) and
                             "ab" (ab-bench.yml)
ab.py                        two github reports of the same benchmarks (base, head): head against base and MemoryPack
asm.py                       the Tier1 machine code of the cases: dump / diff / compare REF (`compare origin/main`
                             before committing a read or write path change)
jitwarm/                     the startup hook asm.py runs Nino.Benchmark with: calls each case until Tier1, then exits
cross-jit.sh                 installs the JIT for the other 64-bit architecture, so DOTNET_JitDisasm prints x64 and
                             Arm64 code on one machine
members/                     member-by-member v3 against v4 (run.sh: v3, v4, v3 in process; DISASM=1 for listings):
                             shared/Parts.cs built as v3/P3.csproj (NuGet 3.3.13) and v4/P4.csproj; table.py,
                             split-asm.py; results/ ignored
bdn.py                       the one parser of BenchmarkDotNet github reports, units and FLOOR_NS (1 ns), for the
                             scripts below; show: the time format of every release-body table
merge_reports.py             merges the reports of parallel jobs into one report (report.yml)
compact_report.py            shortens a merged report for a release body (125,000-character limit)
compare_v3.py                v3 3.3.13 against v4 from two SimpleTest reports, with both MemoryPack runs
readme.py                    writes the numbers of README.md / README.zh.md, the chart (docs/assets) and the runtime
                             tables of docs/v4-benchmarks.md from a joined report
release_body.py              the release body's benchmark sections (Performance Report, v3 comparison, Arm64) for
                             report.yml; the docs site finds them by their headings
runtimes_report.py           the release body's Go, Rust and TypeScript sections from each benchmark's output
unity_report.py              the release body's Unity section from the IL2CPP player's results (unity.yml artifacts)
test_unity_report.py         tests of unity_report.py on fixtures/
fixtures/                    unity-bench-<version>/ results.xml + build.txt, and unity-bench-empty/, for those tests
.gitignore                   results/
```

## Rules for these files

- Headings the release body uses (release_body.py, runtimes_report.py, unity_report.py) are read by the docs site
  (Nino.Docs `scripts/sync-benchmarks.ts`): change one only together with its parser there.
- A script that reads BenchmarkDotNet reports does it through `bdn.py` (one parser, one unit table, one floor);
  the 1 ns floor is the docs site's (`src/lib/benchmarks.ts`) too.

## Running them

How to run, A/B on runners and what may be published: the `benchmark` skill.
