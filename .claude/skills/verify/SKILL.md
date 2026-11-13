---
name: verify
description: "The full Nino gate before anything reaches main or the test branch - build, the unit test matrix, runtime internals, AOT, fuzz, conformance, Go/Rust/TypeScript checks, codegen, actionlint, agent instructions, the docs site - then landing by fast-forward and how the owner cuts a release. Use before any push to main or the test branch, after landing other branches, and before a release. Triggers: push, landing, land on main, merge to main, fast-forward, cherry-pick, release, \"is it ready\", all checks, 合入, 合进, 合到 main, 分支能不能合, 推送, 推到, 发版, 发布, 全部检查."
metadata:
  internal: true
---

# Verify

Run what the change touches; before a release, all of it. Every command runs under `flock -o /tmp/nino-heavy.lock`
when other agents share the machine, after `export PATH=$HOME/.dotnet:$HOME/go/bin:$PATH DOTNET_ROOT=$HOME/.dotnet`.
With the lock shared, run a long series as one detached script (`setsid nohup script.sh &`, each step under the
lock, an exit line per step in a log, a done file at the end) and wait on the done file: a background task is killed
at its time limit while it still waits for the lock (round 103 lost a fuzz series and a test run that way).

## .NET

```bash
dotnet build -c Release                                    # 0 warnings
dotnet test tests/Nino.UnitTests -c Release                # split in two if memory is short:
                                                           #   --filter "FullyQualifiedName!~ShapeMatrixTests", then "~ShapeMatrixTests"
dotnet test tests/Nino.UnitTests -f net8.0 -p:NinoCoreTfm=netstandard2.0
dotnet test tests/Nino.UnitTests -f net8.0 -p:NinoCoreTfm=netstandard2.1
dotnet test tests/Nino.UnitTests -f net8.0 -p:NinoRegularCalls=true        # interceptors off
dotnet test tests/Nino.UnitTests -f net8.0 -p:NinoGeneratorBuild=4.8       # the .NET 8 SDK generator
DOTNET_EnableHWIntrinsic=0 dotnet test tests/Nino.UnitTests -c Release     # no SIMD (also AVX512=0, AVX2=0 for vector code)
for p in Nino.AspNetCore.Tests Nino.Codegen.Tests Nino.Codegen.Integration.Tests; do dotnet test tests/$p; done  # one project per call (MSB1008)
```

- Runtime internals on every runtime a change to `CollectionViews`, mirrors or retyping reaches (`runtime-internals`,
  `platforms`: .NET, Mono, Arm64 and Arm32 under qemu); trimmed and Native AOT publish of `tests/Nino.AotTests`.
- Check every requested class in a filtered gate, using `--list-tests --filter` when choosing the names. A combined
  OR filter can pass while one branch matches nothing: `RefTrack` does not match `ReferenceTrackingTests`.
  Use the exact `ReferenceTrackingTests` and `DeepNestingTests` names for reference and deferred-body coverage.
- Read or security change: `dotnet run -c Release -f net10.0 --project tests/Nino.Fuzz -- selftest`, then a timed `fuzz`
  with and without `DOTNET_EnableHWIntrinsic=0` (`audit-security`).
- Read or write path: `python3 benchmarks/asm/asm.py compare origin/main`, every listed method explained (`optimize`).
- Bytes: `dotnet run --project conformance/Nino.Conformance -c Release -- --verify conformance/corpus` (`wire-format`).
- Benchmarks or a type a benchmark reads: `dotnet run --project benchmarks/Nino.Benchmark -c Release -- --verify`
  (ci.yml runs it on x64 and Arm64; `benchmark`).

## Other runtimes and nino-gen

The commands in `runtimes/go/AGENTS.md`, `runtimes/rust/AGENTS.md`, `runtimes/typescript/AGENTS.md` and
`src/Nino.Codegen/AGENTS.md` (vet/race/fuzz; fmt/clippy/test with `NINO_CORPUS`; `npm ci && npm test`; regenerate
with `NINO_UPDATE_GENERATED=1`).

## Repository

```bash
actionlint                                  # with .github/actionlint.yaml, after any workflow change
tools/agents.sh --check                     # indexes, CLAUDE.md = @AGENTS.md, file maps, skill metadata
python3 .github/scripts/nuget_readme.py --check  # README.nuget.md (the packages' README) matches README.md
npx -y skills add . --list                  # lists only `nino-serialization`: every development skill is internal
```

Docs site change: in the nino.docs clone, `bun run check && bun run build`, and `bun run check:csharp` (with
`NINO_DIR` at this checkout) when a page's C# changed or Nino's public API did: it compiles every v4 C# fence.

## Landing

- Read the diff once more as CI would: what would make a job fail? A red CI run costs a cycle and the owner's
  patience; never push to find out.
- A change to the public API of Nino.Core or Nino.UnityTypes refreshes the Unity package's DLLs in its series
  (`src/Nino.Unity/AGENTS.md`, Rules; the owner's decision, round 121): `UnityPackageDllTests` fails in the
  netstandard2.1 run otherwise. Why: Unity users who install from the repository compile against the committed
  DLLs, and after rc.7 Nino.Burst named `NinoKernels`, which the committed Nino.Core.dll lacked (round 121). Compare APIs, never bytes: each DLL embeds the
  commit it was built from, and a cold build ordered PolySharp's polyfill types differently from a warm one (field
  tokens of `RequiresDynamicCodeAttribute`), while two warm builds were identical.
- Check `git status --short` before pushing. Run `tools/agents.sh --check` against a clean checkout when local
  generated files are present: the check also includes untracked, non-ignored files.
- Cherry-pick or merge workers' branches onto `origin/main`, drop session lines from their messages, add the trailers
  a user-facing change needs; then fast-forward `main` (AGENTS.md, Rules). The test branch takes merges only.
- Watch the runs of the pushed commit to the end, and fix a red one at once (`audit-ci` when the workflow itself is
  at fault).
- After landing a user-facing change, the nino.docs changelog gets its entry (`audit-docs`).

## Releases (the owner's)

The owner runs Actions → Release on `main` with a version (`.github/scripts/bump_version.py` bumps every package,
the workflow tags, publishes NuGet, crates, npm and the Go tag through Trusted Publishing, and writes the notes from
the trailers); `report.yml` then benchmarks the tag on x64 and Arm64 runners, attaches the results to the release as assets
(`benchmarks-manifest.json`) with a chart per section in the body, and the docs site syncs the assets. CONTRIBUTING.md, "Releasing", has the details. Never trigger a release.
