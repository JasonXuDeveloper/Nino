---
name: benchmark
description: "How to measure Nino and what may be published - the .NET BenchmarkDotNet suite (quick mode, filters, v3 3.3.13 back to back, MemoryPack, MessagePack, protobuf-net), member-by-member and generator benchmarks, the Go, Rust and TypeScript benches, A/B runs on GitHub runners (ab-request.txt, dotnet-ab-request.txt, runtime-ab, dotnet-ab, ab-bench) judged per CPU against the noise floor, adding a benchmark case, and the release numbers report.yml writes and the docs site syncs. Use when running, adding or reading a benchmark, deciding whether a change is faster, or touching a number in README, docs or a release. Triggers: benchmark, bench, BenchmarkDotNet, run.sh, QUICK=1, FILTER, A/B, AB, ab-request.txt, dotnet-ab-request.txt, runtime-ab, dotnet-ab, ab-bench.yml, report.yml, benchstat, criterion, tinybench, noise floor, floor, runner numbers, Performance trailer, Performance Report, shards.json, runner 的数字, 基准, 跑分, 跑个AB, 测一下, 测速, 性能数据, 发布数据."
metadata:
  internal: true
---

# Benchmarks

Every number that counts comes from a GitHub runner. Local runs only choose between ideas; runners decide; only
`report.yml` publishes. Where the next win is: `audit-perf`. The techniques: `optimize` (.NET, Unity) and `optimize-runtimes`. The files and options:
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

To choose between local variants of a read or write path when BenchmarkDotNet's quick mode varies more than the
difference (±15 % on a shared 4-core machine), time the benchmark's own method in a loop in one process per build: a
console app that references each tree's built `Nino.Benchmark.dll`, binds the case to a `Func<int>` with
`Delegate.CreateDelegate`, warms it past Tier1 (two rounds of 200,000 calls and a pause), then times 2,000,000 calls;
run the builds alternated six times (`DOTNET_gcServer=1` where the class has `[GcServer(true)]`) and compare minimum
and median. `NinoDeserializePooled` read 213-284 ns (rc.7), 250-296 (main) round after round there, where quick mode
gave overlapping ranges. Never pin it to one CPU (`taskset`): the tiering thread starves and the loop times Tier0 code
(1,000 ns instead of 250); `DOTNET_TieredCompilation=0` times code whose `static readonly` fields do not fold, not what
runners measure. Runners still decide (round 123).

In the Unity player, time small cases without allocation: `NinoSerializer.Serialize` to a new array made the
6000.3 player's string rows bimodal (the same case 1.0x or 1.3x from run to run); `SerializeTo` a reused
`NinoArrayBufferWriter`, `GC.Collect` before each sample group and groups in the order A B B A, keeping the lower of
each pair, kept most rows within 10 % between the two runs of a group (unity.yml run 40735278928). A WebGL player times in a coroutine (`platforms`). Compare two
commits in Unity only within one CPU: each unity.yml job takes whatever runner it gets (`ProcessorType` in the
player's `results.xml`), and of two base/head pairs (runs 40818519412, 40822269653, 40838031821) none put the same
Unity version on the same CPU (AMD EPYC 7763, 9V74, 9V45), with untouched cases 13 to 34 % apart between them. Judge a
Unity change by the with/without ratio inside one run, or put both variants in one player.

Both .NET modes run v3 3.3.13 (NuGet, `NINO_BENCH_V3=true`) once, directly before v4, on the same machine; MemoryPack
runs in both, so its two results show how much the machine moved. Heavy runs go through
`flock -o /tmp/nino-heavy.lock`.

## Deciding on runners (A/B)

1. Request lines on the test branch `bench/ab`:
   - **Reach the commits** with `git merge -s ours --no-edit <ref>` (history only: the test branch keeps main's code,
     so its CI runs main's code; merging an experiment's code ran CI on it, and a Go test that overflowed `int` on 386
     and a Windows stack test mailed the owner a failure per push).
   - **Runtime lines** go into `.github/ab-request.txt` and run `runtime-ab.yml`:
     `<go|go:<dir>|go#<rounds>|rust|rust:<bench>|typescript> <base sha> <head sha> [filter] [<base env> <head env>]`.
     At most 8 lines per push, 10 jobs each (the workflow measures the first 8 and warns; `[ab: new lines only]` in the
     commit message runs only the lines that push added). A push measures only the lines its parent did not have: to
     run a line again or twice, add or change a note after ` #` (`rust <base> <head> records-1000 # copy 2`). Never
     bend the filter to make lines differ: `r{4}ecords` demands the letter four times, every job of such a line matches
     no bench and fails (the first three-way prefetch round, 400 failed jobs).
   - **.NET lines** go into `.github/dotnet-ab-request.txt` and run `dotnet-ab.yml`:
     `[cpu=<regex>] <base sha> <head sha> <rounds> <BenchmarkDotNet filters...>`, each line a job of its own per runner
     (with `cpu=`, up to 8 x64 slots, each measuring only on a matching CPU; the file's header). Both sides build the
     head's `benchmarks/Nino.Benchmark`. At most 3 active lines across all workers, and Arm64 runs one job per line, so
     more Neoverse-N2 samples need duplicate lines. The workflow measures every active line on every push to the file,
     not only new ones: mark finished lines `# done (run N):` in the push that adds the next ones, or, with nothing to
     add, in a commit whose message carries `[skip ci]` (round 105).
   - Keep other workers' lines, mark finished ones `# done:`, batch requests into one push, merge only, never
     force-push.
   - **A release tag**: ask the owner to dispatch `ab-bench.yml` on `main` with `compare` (a session cannot: its token
     lacks `actions: write`, `audit-ci`). It builds head's `benchmarks/Nino.Benchmark` on the tag with head's
     `Directory.Packages.props` in `benchmarks/`, an empty root `Directory.Build.props` where the tag has none
     (v4.0.0-rc.7 kept its props in `src/`, without central package versions) and `NinoBenchLegacy` when the full build
     fails, and fails the run when even that does not build. From a session, compare a tag with dotnet-ab: base = the
     tag plus the same two files committed (0af6bb5e), head = the commit plus `NinoBenchLegacy` set in
     `benchmarks/Nino.Benchmark/Directory.Build.props` (dotnet-ab passes no property; 115 of 128 Nino cases against
     rc.7, run 41413072278). A benchmark an older tag cannot compile goes inside `#if !NINO_BENCH_LEGACY` on main
     (CpuProbe, UntrustedTest, GenericPolymorphicTest).
   - **Spend CI as a budget** (the owner: "跑太多 ci job 了，concurrency 预算都吃没了"): the account runs a limited
     number of jobs at once, shared by main's CI and both A/B workflows, so one A/B run is in flight at a time across
     all workers and both workflows. Before pushing the test branch, check the API for a queued or running A/B run; if
     one is, add your lines to a shared queue file outside the repository (agents use
     `/workspace/agent-notes/ab-queue.txt` under `flock`, not /tmp, which a container restart wipes) and wait; whoever
     pushes next takes every queued line in one commit and tells their owners the run id. Request only lines that
     decide a question (narrow filters, one floor per base shared by everyone, repeats only for a verdict). The
     workflows cap themselves (`max-parallel` 10 runtime, 8 .NET jobs); cancelling and re-running from a session
     returns 403, so a run started is a run spent: 8 queued runtime runs of 25 lines (250 jobs each) once blocked every
     other workflow for hours. The whole .NET suite in one line, `*Test.Nino*` (128 cases, quick), takes about 24
     minutes a round on every runner: 5 rounds are 2 hours a job, and an A/B line, its floor and a second Neoverse line
     took 6 h 17 min (run 41364890698); a filter of 13 cases at 12 rounds took 2 hours (41399172268).
   - **Skip an A/B whose code is unchanged** (identical generated files or machine code prove it), and prove it per
     runtime before requesting the line: Go, `go test -c` the bench at both commits and diff `go tool objdump` with
     paths and addresses stripped; TypeScript, runtime-ab runs the head's `bench.ts` on both sides, so the sides differ
     only in `src/nino.ts`, what nino-gen writes for `bench/Bench.dll` and the data `Bench.dll` writes (diff all
     three); Rust, `objdump -d` the bench binary each side builds with the line's
     `RUSTFLAGS=-Cllvm-args=-align-all-functions=6`, addresses stripped; .NET, the generated files
     (`-p:EmitCompilerGeneratedFiles=true`) and the FullOpts listings (`optimize`). Round 105 needed 2 runtime lines of
     8 (Go: only the codec builder differed; TypeScript: all three identical; Rust `compare` and `text`: identical
     code, `blocks`: not).
   - **Pick the CPUs** ([references/runners.md](references/runners.md), before writing a `cpu=` filter, requesting a
     probe or reading a verdict for one CPU): `cpu='EPYC|Neoverse'`, or `'EPYC|Xeon|Neoverse'` for a change with an
     AVX-512 path, never a model number alone and never a space; list the CPUs a run drew before reading a verdict and
     queue the line again for one it missed; an Xeon 8370C takes several runs, so take its floor from the A/B line's
     own base rounds; build a probe in Release before pushing it and confirm its verdict with the case; read the CPU
     flags, which differ between VMs of one model.
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
   Read the rounds of a surprising median: a case can take one of a few times per process on both sides (Xeon
   8370C, `NinoSerializeKeyValuePairs` 105, 151 or 212 ns on identical code), and its median then follows the draw.
   Which time a side draws can follow the filter, so a confirmation line can turn a case it did not touch: with
   identical Tier1 code (x64 apart from loop-alignment padding, Arm64 through the cross JIT) `NinoSerializeTupleList`
   was 1.01 against round 105's base on EPYC 7763 in the full suite (n=20, both sides 209-216 ns) and 1.21 in a
   13-case line (n=72: base 190, head 210 or 245 ns), 0.98 and 1.39 on Xeon 6975P (base 69 or 98, head 97), 1.035 and
   1.065 on Neoverse-N2 (runs 41364890698, 41399172268).
   On the test branch, `runtime-ab.yml` builds both sides of a Rust line with
   `RUSTFLAGS=-Cllvm-args=-align-all-functions=6` (every function at a 64-byte boundary): on Zen 5 identical code
   moved by 16 bytes changed timings by 10–50%. A side's env `NINO_AB_RUSTFLAGS=` builds without it. A side's env
   is one `NAME=VALUE` without spaces: for several flags, set `CARGO_ENCODED_RUSTFLAGS` to them separated by the
   0x1f byte; cargo then ignores `RUSTFLAGS`, so repeat the alignment flag there.
4. A loss or a wash is reverted, and its numbers go into the commit message or the report, so nobody tries it again
   blind.

## Adding a case

- .NET: a class in `benchmarks/Nino.Benchmark` with Nino and every competitor that can express the case (MemoryPack,
  MessagePack, protobuf-net), a `Verify.cs` entry, the class in `shards.json` (the report and A/B shards): a report
  shard picked by the minutes each shard's "Run benchmarks" step took in the last report, not by case counts
  (`audit-ci`, Fast and parallel). Setup that calls an ISA's API (a probe's `X86Base.CpuId`) checks `IsSupported`:
  ci.yml runs `--verify` on x64 and on Arm64 (`audit-ci`, Scripts only a release runs).
- A case that needs a process-wide setting frozen on first use (`NinoSettings.Untrusted`, `UntrustedTest`) sets it in
  its class's static constructor inside `try`/`catch (InvalidOperationException)` and never throws from
  `[GlobalSetup]` when it is already frozen: BenchmarkDotNet times each case in a process of its own, but
  `--verify` and asm.py's jitwarm hook run every class's setup in one process (a throwing guard aborted
  `asm.py compare`).
- Read a joined BenchmarkDotNet summary with `grep '\| Nino'`, not `'^\| *Nino'`: with several classes it starts each
  row with a Type column.
- Go, Rust, TypeScript: the same data in each `bench` against that language's competitors. Every column ends with
  the same value: where a competitor has no such type (protobuf, MessagePack and JSON have no set), its deserialize
  timing makes what it read into the case's type (Rust `formats!`'s `$from_proto`, TypeScript's `Revive`), and where
  Nino's own form is a slice (Go's sets) the others read the same slices. A TypeScript scenario whose codec the
  generated code lacks is skipped, so runtime-ab can run the head's `bench.ts` on an older base.
- In `runtimes/*/bench` ([references/cases.md](references/cases.md), before adding or changing such a case, or when
  a TypeScript number moves without a code change): rotate the inputs of any microbenchmark that parses (K variants,
  round robin, made outside the timed region); keep only the running serializer's data live; read a runtime API newer
  than the bench off the module namespace and skip its task when undefined, judging such a line against a null line.
- A new section or heading in the release body changes with its parser in Nino.Docs (`scripts/sync-benchmarks.ts`)
  and the components that show it (`audit-docs`).

## Publishing

- Never publish a quick, local or A/B number. README.md, docs/v4-benchmarks.md, the chart, the release notes and the
  docs site take them only from `report.yml` (x64 and Arm64 shards, the runtimes and Unity sections, job id
  `ShortRun-.NET 10.0`), through `release_body.py` / `readme.py` and the site's sync.
- Within 5 % is on par and is written so; under the 1 ns floor (`bdn.py`, `FLOOR_NS`) a time is "too fast to
  measure"; the docs never call Nino slower; v3 means 3.3.13.
