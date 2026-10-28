---
name: benchmark
description: "How to measure Nino and what may be published - the .NET BenchmarkDotNet suite (quick mode, filters, v3 3.3.13 back to back, MemoryPack, MessagePack, protobuf-net), member-by-member and generator benchmarks, machine-code listings, the Go, Rust and TypeScript benches, A/B runs on GitHub runners (ab-request.txt, runtime-ab, dotnet-ab, ab-bench) judged per CPU against the noise floor, adding a benchmark case, and the release numbers report.yml writes and the docs site syncs. Use when running, adding or reading a benchmark, deciding whether a change is faster, or touching a number in README, docs or a release. Triggers: benchmark, bench, BenchmarkDotNet, run.sh, QUICK=1, FILTER, A/B, AB, ab-request.txt, runtime-ab, dotnet-ab, ab-bench.yml, report.yml, benchstat, criterion, tinybench, noise floor, Performance Report, shards.json, 基准, 跑分, 跑个AB, 测一下, 测速, 性能数据, 发布数据."
metadata:
  internal: true
---

# Benchmarks

Every number that counts comes from a GitHub runner. Local runs only choose between ideas; runners decide; only
`report.yml` publishes. Where the next win is: `audit-perf`. The techniques: `optimize`. The files and options:
`benchmarks/AGENTS.md` and benchmarks/README.md (read it instead of guessing an option).

## What to run

| Question | Run |
|---|---|
| .NET case against competitors, locally | `QUICK=1 FILTER='*SimpleTest*' benchmarks/run.sh` (any BenchmarkDotNet filter; quick mode stops each case at 6 to 10 iterations under 2 % error, about 30-45 min for everything) |
| Every case still reads back right | `Nino.Benchmark --verify` (Verify.cs) |
| One member's cost, v3 against v4 | `benchmarks/members/run.sh` (`DISASM=1` for listings) |
| Did the machine code change | `python3 benchmarks/asm.py compare origin/main` (`optimize`) |
| Generator time and generated size | `benchmarks/Nino.GeneratorBenchmark` (`audit-generator`) |
| Go | `cd runtimes/go/bench && go test -run='^$' -bench=. -benchmem -count=6 . \| tee bench.txt`, then benchstat |
| Rust | `cargo bench -p nino-bench --bench compare` (and `--bench text` for transcoding) |
| TypeScript | `runtimes/typescript/bench/run.sh [scenario]` (Node and headless Chromium) |
| Unity, IL2CPP and Burst | `unity.yml` only (one license seat, `audit-ci`) |

Both .NET modes run v3 3.3.13 (NuGet, `NINO_BENCH_V3=true`) once, directly before v4, on the same machine; MemoryPack
runs in both, so its two results show how much the machine moved. Heavy runs go through
`flock -o /tmp/nino-heavy.lock`.

## Deciding on runners (A/B)

1. Merge the work into the test branch `claude/zen-goldberg-5qqqcu` and add a line to `.github/ab-request.txt` there:
   `<go|go:<dir>|go#<rounds>|rust|typescript> <base sha> <head sha> [filter] [<base env> <head env>]` runs
   `runtime-ab.yml`; .NET lines run `dotnet-ab.yml`. Keep other workers' lines, mark finished ones `# done:`, batch
   requests into one push, merge only, never force-push. `ab-bench.yml` on `main` compares a ref with the previous
   release or its base on one runner.
2. Always request the floor too: the base against itself, on the same runners.
3. Judge per CPU (AMD EPYC 7763, EPYC 9V74 / Zen 4, EPYC 9V45 / Zen 5, Xeon Platinum / 6973P, Neoverse-N2 / V3):
   n >= 12 per side; faster only beyond that CPU's floor; no case slower beyond it. Go through benchstat, Rust through
   criterion's change estimate, .NET through the medians.
   On the test branch, `runtime-ab.yml` builds both sides of a Rust line with
   `RUSTFLAGS=-Cllvm-args=-align-all-functions=6` (every function at a 64-byte boundary): on Zen 5 identical code
   moved by 16 bytes changed timings by 10–50%. A side's env `NINO_AB_RUSTFLAGS=` builds without it. A side's env
   is one `NAME=VALUE` without spaces: for several flags, set `CARGO_ENCODED_RUSTFLAGS` to them separated by the
   0x1f byte; cargo then ignores `RUSTFLAGS`, so repeat the alignment flag there.
4. A loss or a wash is reverted, and its numbers go into the commit message or the report, so nobody tries it again
   blind.

## Adding a case

- .NET: a class in `benchmarks/Nino.Benchmark` with Nino and every competitor that can express the case (MemoryPack,
  MessagePack, protobuf-net), a `Verify.cs` entry, the class in `shards.json` (the report and A/B shards).
- Go, Rust, TypeScript: the same data in each `bench` against that language's competitors.
- A new section or heading in the release body changes with its parser in Nino.Docs (`scripts/sync-benchmarks.ts`)
  and the components that show it (`audit-docs`).

## Publishing

- Never publish a quick, local or A/B number. README.md, docs/v4-benchmarks.md, the chart, the release notes and the
  docs site take them only from `report.yml` (x64 and Arm64 shards, the runtimes and Unity sections, job id
  `ShortRun-.NET 10.0`), through `release_body.py` / `readme.py` and the site's sync.
- Within 5 % is on par and is written so; under the 1 ns floor (`bdn.py`, `FLOOR_NS`) a time is "too fast to
  measure"; the docs never call Nino slower; v3 means 3.3.13.
