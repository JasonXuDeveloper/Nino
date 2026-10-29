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
| Rust | `cargo bench -p nino-bench --bench compare` (and `--bench text` for transcoding, `--bench blocks` for blocks of fixed-size elements). Back to back across worktrees with the shared `CARGO_TARGET_DIR`, touch each side's `.rs` and `Cargo.toml` files before its run, or give each side its own target directory: every worktree builds the workspace's crates under the same artifact names, and cargo, finding them newer than the other side's sources, ran one binary for both sides (land/rust's first local run) |
| TypeScript | `runtimes/typescript/bench/run.sh [scenario]` (Node and headless Chromium) |
| Unity, IL2CPP and Burst | `unity.yml` only (one license seat, `audit-ci`) |

In the Unity player, time small cases without allocation: `NinoSerializer.Serialize` to a new array made the
6000.3 player's string rows bimodal (the same case 1.0x or 1.3x from run to run); `SerializeTo` a reused
`NinoArrayBufferWriter`, `GC.Collect` before each sample group and groups in the order A B B A, keeping the lower of
each pair, kept most rows within 10 % between the two runs of a group (unity.yml run 40735278928). A WebGL player times in a coroutine (`platforms`).

Both .NET modes run v3 3.3.13 (NuGet, `NINO_BENCH_V3=true`) once, directly before v4, on the same machine; MemoryPack
runs in both, so its two results show how much the machine moved. Heavy runs go through
`flock -o /tmp/nino-heavy.lock`.

## Deciding on runners (A/B)

1. Make the commits reachable from the test branch `claude/zen-goldberg-5qqqcu` with `git merge -s ours --no-edit
   <ref>` (history only: the test branch keeps main's code, so its CI runs main's code; merging an experiment's code
   ran CI on it, and a Go test that overflowed `int` on 386 and a Windows stack test mailed the owner a failure per
   push), and add a line to `.github/ab-request.txt` there:
   `<go|go:<dir>|go#<rounds>|rust|typescript> <base sha> <head sha> [filter] [<base env> <head env>]` runs
   `runtime-ab.yml`; .NET lines run `dotnet-ab.yml`. Keep other workers' lines, mark finished ones `# done:`, batch
   requests into one push, merge only, never force-push. `ab-bench.yml` on `main` compares a ref with the previous
   release or its base on one runner. Limits: a matrix holds 256 jobs, so at most 25 runtime lines per push (the
   workflow measures the first 25 and warns; `[ab: new lines only]` in the commit message runs only the lines that
   push added); `.github/dotnet-ab-request.txt` takes at most 3 active lines across all workers, and Arm64 runs one job
   per line, so more Neoverse-N2 samples need duplicate lines. To run a line again or twice, add or change a note after
   ` #` (`rust <base> <head> records-1000 # copy 2`), since a push measures only the lines its parent did not have.
   Never bend the filter to make lines differ: `r{4}ecords` demands the letter four times, every job of such a line
   matches no bench and fails (the first three-way prefetch round, 400 failed jobs).
   Match x64 runners with `cpu='EPYC|Neoverse'`, not `'7763|Neoverse'`: runners are also EPYC 9V45 and 9V74, and a
   line whose regex misses them ends its slots without measuring (the first land/cmod round got one EPYC job).
2. Always request the floor too: the base against itself, on the same runners, over several jobs. A floor from one
   job underestimates the noise: the TimeZoneInfo floor jobs' own medians spanned 0.982 to 1.023 on one EPYC CPU.
   Before blaming a 1–2 % move on a change that touches code the case does not run, also request a null change in
   the same run: the base plus something that changes nothing (one more bench case), as its own line against the
   base. The same-against-same floor builds one binary, so it misses the layout noise of two binaries: on Rust
   records-1000 a null change (0635898 against a8b72d0, aligned build) moved +1.0 to +1.9 % on EPYC 7763, 9V45, 9V74
   and Xeon 8573C (n=10 to 36) and -0.4 % on Neoverse-N2 (n=80), as large as the 1.7 to 2.6 % the write prefetch
   (79d2c33) showed there.
3. Judge per CPU (AMD EPYC 7763, EPYC 9V74 / Zen 4, EPYC 9V45 / Zen 5, Xeon Platinum / 6973P, Neoverse-N2 / V3):
   n >= 12 per side; faster only beyond that CPU's floor; no case slower beyond it. Go through benchstat, Rust through
   criterion's change estimate, .NET through the medians. A case that runs the same machine code as the base (an
   identical listing, or IL identical to main) is not counted against the branch, whatever its number (the owner's
   rule): show the listings side by side in the report (the gen/dedupe landing, 61747f6, had cases up to 1.2 % past a
   one-job floor with IL identical to main). Pool rounds of the same code before judging; a single round is not n.
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
- Rotate the inputs of any microbenchmark that parses (deserialize, `JSON.parse`, decoders): prepare K variants of the
  same shape and size with different content (K=64 in `runtimes/typescript/bench`), give every serializer all of them
  before the timing, and let each iteration take the next one round robin, for serialize and deserialize alike;
  allocate the variants outside the timed region. Why: V8 reuses work for a JSON text it just parsed (hidden-class
  transitions, internalized keys), so one repeated input flatters JSON: a Lookup-like text took 0.21 ms a parse
  repeated, 7.8 to 8.4 ms with 200 different texts in turn. On runners (19801e3, landed as 81d0700, against main, run 40792547994, medians
  of 3 to 60 rounds a CPU, head-vs-head floors within 1-12 %) Lookup's JSON deserialize went from 0.72x Nino's time to
  8.97x on EPYC 7763 in Node (0.58x to 6.85x in Chromium), and from 0.62-0.79x to 8.5-9.5x on every other CPU
  (Neoverse-N2 0.76x to 8.48x); Strings ASCII barely moved (2.11x to 2.27x in Node on EPYC 7763). Rotation also takes
  the inputs out of the cache, which every serializer pays: Nino's Mesh serialize went from 0.19 to 1.15 ms on EPYC 7763
  and 0.22 to 1.19 ms on Neoverse-N2 (64 x 20 x 1,000 `Vec3` objects, likely each with boxed doubles), Player and
  OrderBook serialize 1.5x on EPYC 7763 (1.2x and 1.04x on Neoverse-N2).
- A new section or heading in the release body changes with its parser in Nino.Docs (`scripts/sync-benchmarks.ts`)
  and the components that show it (`audit-docs`).

## Publishing

- Never publish a quick, local or A/B number. README.md, docs/v4-benchmarks.md, the chart, the release notes and the
  docs site take them only from `report.yml` (x64 and Arm64 shards, the runtimes and Unity sections, job id
  `ShortRun-.NET 10.0`), through `release_body.py` / `readme.py` and the site's sync.
- Within 5 % is on par and is written so; under the 1 ns floor (`bdn.py`, `FLOOR_NS`) a time is "too fast to
  measure"; the docs never call Nino slower; v3 means 3.3.13.
