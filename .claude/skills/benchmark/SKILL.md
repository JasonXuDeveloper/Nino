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

- Use 64 KiB as the L1 data-cache budget when estimating a hot path's working set. Count the live input, output,
  lookup data and temporary buffers together. For hardware measurements, report the real CPU and core type, and
  compare load and store misses per operation. Cross-JIT assembly provides instruction evidence only.

| Question | Run |
|---|---|
| .NET case against competitors, locally | `QUICK=1 FILTER='*SimpleTest*' benchmarks/run.sh` (any BenchmarkDotNet filter; quick mode stops each case at 6 to 10 iterations under 2 % error, about 30-45 min for everything) |
| Every case still reads back right | `Nino.Benchmark --verify` (Verify.cs) |
| One member's cost, v3 against v4 | `benchmarks/members/run.sh` (`DISASM=1` for listings) |
| Did the machine code change | `python3 benchmarks/asm/asm.py compare origin/main` (`optimize`) |
| Generator time and generated size | `benchmarks/Nino.GeneratorBenchmark` (`audit-generator`); on runners, `generator` lines of runtime-ab (below) |
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

- Time each build in a process of its own, not two assembly load contexts in one process: a byte-identical A/A of
  the 64-item `BlockWaypoint` case in one process showed a 5–15% gap on several Arm64 runs, even with the warmup
  order alternated; in a process per build, four default-JIT A/B rounds at each of three offsets showed no
  repeatable SIMD regression for the scalar cursor gate (the code-generation result: `optimize/references/vectors-blocks.md#scalar-block-reads`).
- Validate before warming, warm the timing wrapper as well as the target under default tiering, and never classify
  a surprising small-case result from a short per-case warmup until a longer isolated warmup and same-binary
  controls reproduce it: the string-miss probe timed a seven-char ASCII hit at 29–44 ns after 100 ms per case, and
  near 3.7 ns on both builds after an isolated one-second warmup (Tier1 listings). The fixture that held validates
  first, warms every case path and its wrapper for two seconds, then alternates complete processes over four rounds.

In the Unity player, time small cases without allocation: `NinoSerializer.Serialize` to a new array made the
6000.3 player's string rows bimodal (the same case 1.0x or 1.3x from run to run); `SerializeTo` a reused
`NinoArrayBufferWriter`, `GC.Collect` before each sample group and groups in the order A B B A, keeping the lower of
each pair, kept most rows within 10 % between the two runs of a group (unity.yml run 40735278928). A WebGL player times in a coroutine (`platforms`). Compare two
commits in Unity only within one CPU: each unity.yml job takes whatever runner it gets (`ProcessorType` in the
player's `results.xml`), and of two base/head pairs (runs 40818519412, 40822269653, 40838031821) none put the same
Unity version on the same CPU (AMD EPYC 7763, 9V74, 9V45), with untouched cases 13 to 34 % apart between them. Judge a
Unity change by the with/without ratio inside one run, or put both variants in one player.

Run a local A/B from worktrees nobody edits while it runs, one per side: BenchmarkDotNet builds a project of its
own (`Nino.Benchmark-Quick-1`) from the sources when each run starts, so an edit to a tree whose run is queued is
measured as that side (and a broken edit fails the run). The review of codex/nino-audit edited its review tree while
the branch's run waited behind main's; the branch side then ran from a worktree of the unedited commit.

Both .NET modes run v3 3.3.13 (NuGet, `NINO_BENCH_V3=true`) once, directly before v4, on the same machine; MemoryPack
runs in both, so its two results show how much the machine moved. Heavy runs go through
`flock -o /tmp/nino-heavy.lock`.

## Deciding on runners (A/B)

1. Request lines on the test branch `bench/ab`:
   - **Reach the commits** with `git merge -s ours --no-edit <ref>` (history only: the test branch keeps main's code,
     so its CI runs main's code; merging an experiment's code ran CI on it, and a Go test that overflowed `int` on 386
     and a Windows stack test mailed the owner a failure per push).
   - **Runtime lines** go into `.github/ab-request.txt` and run `runtime-ab.yml`:
     `<go|go:<dir>|go#<rounds>|rust|rust:<bench>|typescript|generator|generator#<rounds>> <base sha> <head sha> [filter] [<base env> <head env>]`.
     At most 8 lines per push, 10 jobs each (the workflow measures the first 8 and warns; `[ab: new lines only]` in the
     commit message runs only the lines that push added). A push measures only the lines its parent did not have: to
     run a line again or twice, add or change a note after ` #` (`rust <base> <head> records-1000 # copy 2`). Never
     bend the filter to make lines differ: `r{4}ecords` demands the letter four times, every job of such a line matches
     no bench and fails (the first three-way prefetch round, 400 failed jobs).
   - **Generator and analyzer lines** are runtime-ab's `generator#<rounds> <base> <head> <arguments joined by commas>`
     (`generator#10 d2fa565f abe4906b 30,8,--layouts`): the head's `benchmarks/Nino.GeneratorBenchmark` (so the head
     needs every option the line passes) loads each side's `Nino.Generator` (`--generator`) in a process per side and
     round, and each process gives the median of its warm runs: generator time, the analyzers' own time and the
     analysis wall time (`generate`, `analyzers`, `wall` in the summary, head against base). Judge the analyzer on
     Neoverse-N2 first: its floors were 0.995-1.003 for the analyzers' time and 0.999 for the wall time at n=50,
     while x64 floors of the analyzers' time reached 0.945 (EPYC 9V74, n=20) and 1.09 (9V45, n=10) on identical code
     (runs 42509534286, 42518707231). A line takes about 35 minutes for 10 jobs of 10 rounds at `30,8,--layouts`.
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
     `RUSTFLAGS=-Cllvm-args=-align-all-functions=6`, addresses stripped (after items moved between modules, also
     the `::h<hash>` suffixes, the moved paths mapped and the `# <symbol>` comments; criterion's code and a bench's
     own setup can land in other codegen units of the bench crate, so compare every `nino::` function and timed
     closure; Go numbers `init.N` in file order); .NET, the generated files
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

## Upgrading a competitor

- When `Directory.Packages.props` moves MemoryPack, MessagePack, protobuf-net or Google.Protobuf to a new version
  (or the benchmark's .NET, which is System.Text.Json's), update the version
  the docs site names in the same series: `git grep <old version>` in Nino.Docs (the home page's
  `src/components/home-sections.tsx`, the benchmark and `vs-*` pages in both languages, `scripts/csharp-check`). Why:
  the site syncs the numbers from the release's assets but writes each library's version by hand, so new numbers would
  sit next to the old version.

## Adding a case

- .NET: a class in `benchmarks/Nino.Benchmark` with Nino and every competitor that can express the case (MemoryPack,
  MessagePack, protobuf-net; Google.Protobuf and System.Text.Json in `SimpleTest`, `PolymorphicTest`, `NullableTest`,
  `CollectionsTest`'s nullable collections and the workloads), a `Verify.cs` entry, its class in a chart group
  (`readme.py`'s `GROUPS`, the site's `dotnetGroups` in `src/lib/benchmarks.ts`; a class neither names lands in
  "Others"), its v3 port (below), the class in `shards.json` (the report and A/B shards): a report
  shard picked by the minutes each shard's "Run benchmarks" step took in the last report, and at most about 70 cases
  of v4 and v3 together (`--list flat` of both builds with the shard's globs; about 15 minutes a shard plus half a
  minute a case, under report.yml's 150-minute timeout): a new shard (the next letter) rather than an overfull one
  (`audit-ci`, Fast and parallel). Setup that calls an ISA's API (a probe's `X86Base.CpuId`) checks `IsSupported`:
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
  (Google.Protobuf would misread it), so a nullable enum stays bare with a zero member the data never uses. A
  `Nullable<T>` of a message type cannot be wrapped in a repeated field at all ("No serializer for type
  System.Nullable`1[SimpleStruct]"): write an array of a contract with one optional field (`OptionalSimpleStruct`,
  the same bytes), made outside the timed region; a top-level null is such a contract with the field absent. A
  baseline that reads null (`NullableObject`) carries `[ReadsNull]`, so Verify checks the others read null too.
- In `WorkloadTest` a library that lacks a feature is measured through its usual workaround (copies of shared
  objects, a new instance instead of reading into one; the owner's choice for workloads), said in the model's comment
  and in benchmarks/README.md; the other classes keep "no method rather than other data".
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
- A new result, asset name or format changes with its reader in Nino.Docs (`scripts/sync-benchmarks.ts`, which reads
  the assets by name) and the components that show it (`audit-docs`): the names are `release_assets.py`'s `NAMES`.
- Publish every result as a release asset listed in `benchmarks-manifest.json`, never as a table in the body:
  `release_body.py publish` (each publish job of report.yml, one after another) attaches the files (merged
  BenchmarkDotNet reports, the runtime, Unity and build sections, their JSON/CSV data), draws one chart per section
  (`charts.py`; a light and a dark SVG, in a `<picture>` with `prefers-color-scheme`), except the .NET x64 and Arm64
  sections, which get README's charts, one per group (`readme.chart_groups`, the same function; v3 in x64's, none in
  Arm64's; v3's section has only its summary and table, the owner's call: "v4 v3 memorypack messagepack ...
  统一比较"), each in a collapsed `<details>` inside the section with a plain summary (GitHub does not indent a
  nested block; bold summaries are the sections'), listed in the manifest entry's `charts`
  (`charts/<tag>/dotnet-<arch>-<group>.svg` and `-dark.svg`), and rewrites the body's benchmark part between its
  markers: a `<details>` per section with a one-line summary, the charts and links to the assets, about 12,000
  characters for the three .NET sections with every group and 20,000 for everything. Why: the tables grew toward GitHub's 125,000-character limit with every library and
  case (v4.0.0-rc.9: x64 and Arm64 took 34k each, the runtime sections 29k, and the 10k Unity section was dropped at
  116,977), and README, docs and the site parsed them back out of the body (#272). A body from before the manifest
  keeps an old section until the manifest has its asset.
- Reference a chart from the body by its raw URL on the release-notes branch (`charts/<tag>/<name>.svg?v=<hash>`, `<name>-dark.svg` for the dark theme),
  never by its release-download URL: GitHub serves release assets as `application/octet-stream` with
  `Content-Disposition: attachment` (the download redirect sets `response-content-type=application/octet-stream`),
  which its image proxy camo does not show, while raw.githubusercontent.com serves `image/svg+xml`. Git notes keep
  files whose paths are not commit ids, so the charts share the branch with release_notes.py's notes (the owner's
  choice: no new branch). Draw SVG, never PNG: PNGs at a
  readable 2x scale (about 1,880 x 2,220 px each) came to about 5.4 MB a release and needed Chrome, the SVGs are a
  few KB, sharp at any zoom, and a pair per chart follows the reader's theme (the owner's call). readme.py's
  `--png` (the overview chart for nuget.org, which shows no SVG) still uses headless Chrome: run it without
  `--user-data-dir` in a cloud session, with which Chromium 1243 wrote nothing and never exited.
- Dry-run the publish jobs before changing them: `python3 benchmarks/reports/release_body.py publish --tag <tag>
  --dir <work> --release-dir <fake release dir> NAME=PATH...` for each job in report.yml's order (a directory with
  `body.md` stands in for the release), then `readme.py --assets <fake>/assets` in a copy
  of the tree; `test_release_body.py` runs the same.

## Publishing

- Chart every .NET case, in the group of its class (the owner, 2026-11-13: real workloads, basic types and structs,
  polymorphism, nullable values, collections and common types, reuse and pooling, reference tracking and version
  tolerance, others): README, docs/v4-benchmarks.md and the release body draw them with `readme.py`'s `chart_groups`
  (one function, one order: Nino v4, v3 n/a where 3.3.13 has no port, then each library that has a row), one light
  and one dark SVG per group (`docs/assets/benchmark-<group>.svg`, a few dozen KB: bars and labels carry CSS classes,
  not inline styles), each in a `<details>` with the group as its summary and a `<picture>` by
  `prefers-color-scheme`; the site groups its interactive charts the same way. Check a chart change by rendering it
  in both themes (`charts.svg` into an HTML page, Playwright's `colorScheme`) and reading the screenshots: a long
  group title runs past its column (the reuse chart's allocations moved into the bar labels for that).
- Never publish a quick, local or A/B number. README.md, docs/v4-benchmarks.md, the charts, the release notes and the
  docs site take them only from `report.yml` (x64 and Arm64 shards, the runtimes and Unity sections, job id
  `ShortRun-.NET 10.0`), through the release's assets: `release_body.py` / `readme.py --assets` and the site's sync.
- Take the build-time numbers (README's Build row `v3.build`, docs/v4-benchmarks.md's `doc.build.*` tables, the site's
  build tables) only from the release's `build-x64.md` asset (the "🏗️ Build: v3 3.3.13 vs v4 (source generator)"
  section): report.yml's
  `generator` job runs this commit's `Nino.GeneratorBenchmark --json --no-analyzers` with v3 3.3.13's generator and
  core (NuGet, PackageDownload) and the tag's, at 330 and 1,100 types on one x64 runner, `generator_report.py` writes
  the section, and `publish-generator` publishes it (with the --json files) and runs `readme.py --assets` for the
  newest release. Why: they were
  typed in from one local run on a 4-core Xeon, which no release refreshed (v4's generated source at 330 types had
  grown from the published 1.31 MB to 1.53 MB by main's f36cfaff). v3 at 1,100 types takes about 8 minutes a run, so it runs 3 times (2 measured).
- Time the generator benchmark only after its warm-up (`--warmup`, 60 s by default, at least one run; `--runs` counts
  the measured runs after it), and never compare a v4 time from a run with a shorter warm-up: Roslyn runs in the
  harness's process, so a workload of seconds is timed while the JIT still tiers the compiler up and its heap grows.
  Evidence: a local `--runs 3` (median of runs 1-2) showed v4's emit at 1,100 types at 3.62 s against the published
  2.1 s (221x to 129x against v3); back to back on the same Xeon, main (9fbcf901) emitted in 4.3, 3.4, 3.3, 3.2,
  2.8, 2.1 s in runs 0-5 and 1.75-1.85 s from run 6, the published commit (1938da63) in 1.8-2.0 s from run 5: on par.
  v3's runs take minutes and settle in the first. The 0.7 MB of source added since (5.09 MB against 4.38) is the
  `[assembly: NinoTypeLayout]` metadata (1.37 MB, from bd6a732b and f76fa0e0) less the later shortening, and costs
  emit 7 % more allocation (408 MB against 382) and no time beyond the noise.
- The headline (README's ranges, the site's headline) comes from `WorkloadTest`'s 12 workloads, against
  every library it measures (the owner's choice, 2026-11-11: real-world payloads, not SimpleClass); v3 runs every case
  3.3.13 can do the job of, in its own way (the owner, 2026-11-13: "v3和v4支持的东西应该一样多"; leave a case out
  only with proof that v3 cannot do it, the missing API or the generator's code, shown to the owner): every class has
  its port (`V3/<Class>.cs`, whose header lists what it does differently and what it leaves out, with the proof; the
  overview chart shows `NullableFieldsArray`, `NullableStructs` and `NullableLongs` after the polymorphic group,
  `readme.py`'s `NULLABLE_CASES` in `CHART_CASES`; PooledGraphTest's `Leaves` are the reuse chart's "Into existing
  objects · leaves" bar). Find v3's way in the decompiled 3.3.13 (`ilspycmd -p` on `nino.serialization/3.3.13/lib/net8.0/
  Nino.Core.dll` and `nino.generator/3.3.13/analyzers/dotnet/cs/Nino.Generator.dll`, whose emitters hold the
  generated code as strings) and the archived v3 docs (nino.docs `content/docs/v3`). What 3.3.13 has, and how the
  ports use it: interfaces, abstract classes and generics as `[NinoType]` (every class is written with a type id,
  a hash of its name); version tolerance only through `WEAK_VERSION_TOLERANCE`, a symbol the generated code reads,
  so it holds for a whole project and changes every type's bytes (`V3/Versioned` builds the versioned models alone);
  only appended members (a deleted member keeps its slot: ProfileV2's `AvatarUrl`); `[NinoFormerName]` keeps a type's
  id across a rename (the V2 types read the V1 types' bytes); no reference tracking (`TypeCollector.Ref` exists, no
  generated code uses it: shared objects are written as copies, as MessagePack's); no reading into an existing
  object (the generator writes `Deserialize(out T, ref Reader)` only; collections with `Clear` and `Add` get
  `Deserialize(TCollection value, ref Reader reader)`, which keeps the list and reads new elements: the v3 Leaves
  "in place" case; an array, or a `[NinoType]` object, is read new: ScatteredTest's `Into`, the workloads' in-place
  reads); no pool (`[NinoPooled]`, `ReturnToPool` and `NinoPool` are v4's: `NinoLeavesPooledElements`,
  `NinoNodePooled` and `NinoDeserializePooled` stay v4 only); no settings (`NinoSettings.Untrusted` is v4's: v3's
  UntrustedTest reads unchecked, as MemoryPack does), no `GetFixedSize`; collection overloads only for the collection
  types its project's source names (its CollectionGenerator collects every generic name, array type and tuple type:
  `V3/Versioned/CollectionTypes.cs` names `TolerantClass[]`), including `Serialize(Span<T>)` and `ICollection<T>` for
  unmanaged `T` (BclTypesTest's and ExternalBufferTest's `Memory<int>` and one-segment `ReadOnlySequence<int>` are
  written through their span, `IReadOnlyList<string>` as the `ICollection<string>` it is); reading only from a
  `ReadOnlySpan<byte>` (a `ReadOnlySequence<byte>` is copied into one span first) and building a collection only by
  constructor and `Add` (the immutable collections are read as an array and built with `CreateRange`; v3 writes no
  `ImmutableQueue` or `ImmutableStack`, so their cases write `ToArray()`). What it cannot do at all, with the proof
  in the port: anything its generator's selector rejects binds to the generic `Serialize<T>(T) where T : unmanaged`
  (CS8377) and 3.3.13 has no formatter API (its runtime exports only the attributes, `NinoTuple`, `Reader`, `Writer`
  and `TypeCollector`): BclTypesTest's `BigInteger`, `Uri`, `Version`, `StringBuilder`, `BitArray`, `CultureInfo`,
  `TimeZoneInfo` and `Lazy<string>` (a hand conversion to a string or bytes would time the conversion; protobuf-net
  sits these out too); `ILookup` (its generator writes code that does not compile, CS8377 on `IGrouping`); a list
  that holds itself (CollectionsTest's `SelfTree`: `CollectionSerializerGenerator` recurses until the compiler dies
  of a stack overflow, so the type stays in the v4 `Cases/` file); a graph with a cycle (ReferenceTest: v3
  writes every reference by value and overflows the stack). Measure v3 through these workarounds, as MessagePack and
  protobuf-net get theirs, never leave a case out for them (the owner, 2026-11-13: "跟messagepack/pb一样就行"). A v3 port
  (`benchmarks/Nino.Benchmark/V3/<Class>.cs`) keeps the v4 class's name, method names, data and attributes and
  changes only the Nino calls (`NinoGen.Serializer` / `NinoGen.Deserializer`, `ArrayBufferWriter<byte>`), shares the
  v4 models (in `Fixtures/`; an attribute 3.3.13 lacks behind `#if NINO_BENCH_V3`), drops the MessagePack,
  protobuf-net, Google.Protobuf and JSON cases (the v3 run times Nino and MemoryPack), and joins the `"v3"` globs of the
  `shards.json` shard that runs the v4 cases (the csproj compiles `V3/*.cs`, `run.sh` runs every shard's v3 globs); report.yml leaves a `v3-<shard>/none` marker for a release built before
  the port, so older tags still get their v3 section. A change of workload, case key or method prefix changes `readme.py`'s `WORKLOADS` / `OTHERS`
  and the site's `src/lib/benchmarks.ts` and sync in the same series. Prose and markers that name the workloads land
  together with `readme.py`; until a release's report measured them, their markers read "—".
- Write every multiple a reader sees with one decimal below 10 and a whole number from 10, rounded half up from the
  unrounded ratio ("1.1×", "1.4×", "77×", "231×"; a range "1.1–1.4×", one value when both ends round alike), through
  `bdn.times` only (charts, README, docs tables, the v3 sentence, the release body's summaries, the build and Unity
  sections, compare_v3.py; the docs site's `formatRatio` the same). Why: the owner chose one precision (rc.10) after
  the charts wrote "4.1×", the tables "4.06× faster" and the site "4.06×" for the same case. Within 5 % stays "on par"
  (`bdn.on_par`), so the first ratio beyond it reads "1.1×", never "1.0×" or "1.05×". Two decimals stay only in the
  developers' A/B tools (`ab.py`, `members/table.py`), which judge changes against a noise floor of a few percent.
- Within 5 % is on par and is written so; under the 1 ns floor (`bdn.py`, `FLOOR_NS`) a time is "< 1 ns"; Nino v4
  under it against a measurable time is a lower bound, that time / 1 ns ("> 77×" in a chart, `charts.multiple`;
  "> 1.4× faster" in a table or the v3 sentence, `bdn.lower_bound`; the docs site the same), never a ratio, which
  would only reflect noise; both under it are on par; a range (`ser.range`, the v3 median) leaves such a case out,
  its ends being measured ratios; A/B comparisons (`ab.py`) keep "-". Lower-bound nothing else. Why: the owner
  asked for a multiple where the charts showed MemoryPack 1.40 ns, MessagePack 77.2 ns and protobuf-net 231 ns
  beside Nino's "< 1 ns" with none ("这种怎么没写倍数？", rc.10), and "> N×" is true since Nino took less than 1 ns.
  The docs never call Nino slower; v3 means 3.3.13.
- Publish an Allocated cell only when the row's GC counts agree with it: BenchmarkDotNet printed "-" (nothing
  allocated) beside a nonzero Gen0 for 31 of 445 rows of v4.0.0-rc.9's x64 report and 6 of its Arm64 one, different
  rows on each (`ProtobufDeserializeNew` allocates 3,544 B per call measured again), and the site showed them as 0 B.
  `merge_reports.py` writes "?" for such a cell (`unmeasured_allocation`), which the site and `readme.py` show as
  unknown; a claim of 0 B (a reuse row, "no allocation") needs a run where the GC count is zero too. Every table of the
  site's Benchmarks page names every library that wrote the same data (MemoryPack, MessagePack, protobuf-net;
  SimpleTest, PolymorphicTest, NullableTest, the nullable collections and the workloads add Google.Protobuf and
  System.Text.Json): a new benchmark class gets its case list in the site's
  `moreCases` or `extraCases`, never only the raw results table (round 130: five classes and the reuse and
  polymorphism tables showed MemoryPack alone).
