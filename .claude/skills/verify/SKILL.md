---
name: verify
description: "The full Nino gate before anything reaches main or the test branch - build, the unit test matrix, runtime internals, AOT, fuzz, conformance, Go/Rust/TypeScript checks, codegen, actionlint, agent instructions, the docs site - then landing by fast-forward and how the owner cuts a release. Use before any push to main or the test branch, after landing other branches, and before a release. Triggers: push, land, merge to main, cherry-pick, release, rc, \"is it ready\", CI red, 合并, 合入, 推送, 发版, 发布, 能不能合."
metadata:
  internal: true
---

# Verify

Run what the change touches; before a release, all of it. Every command runs under `flock -o /tmp/nino-heavy.lock`
when other agents share the machine, after `export PATH=$HOME/.dotnet:$HOME/go/bin:$PATH DOTNET_ROOT=$HOME/.dotnet`.

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
dotnet test tests/Nino.AspNetCore.Tests tests/Nino.Codegen.Tests tests/Nino.Codegen.Integration.Tests
```

- Runtime internals on every runtime a change to `CollectionViews`, mirrors or retyping reaches (`runtime-internals`,
  `platforms`: .NET, Mono, Arm64 and Arm32 under qemu); trimmed and Native AOT publish of `tests/Nino.AotTests`.
- Read or security change: `dotnet run -c Release --project tests/Nino.Fuzz -- selftest`, then a timed `fuzz` with
  and without `DOTNET_EnableHWIntrinsic=0` (`audit-security`).
- Read or write path: `python3 benchmarks/asm.py compare origin/main`, every listed method explained (`optimize`).
- Bytes: `dotnet run --project conformance/Nino.Conformance -c Release -- --verify conformance/corpus` (`wire-format`).

## Other runtimes and nino-gen

The commands in `runtimes/go/AGENTS.md`, `runtimes/rust/AGENTS.md`, `runtimes/typescript/AGENTS.md` and
`src/Nino.Codegen/AGENTS.md` (vet/race/fuzz; fmt/clippy/test with `NINO_CORPUS`; `npm ci && npm test`; regenerate
with `NINO_UPDATE_GENERATED=1`).

## Repository

```bash
actionlint                                  # with .github/actionlint.yaml, after any workflow change
tools/agents.sh --check                     # indexes, CLAUDE.md = @AGENTS.md, file maps, skill metadata
npx -y skills add . --list                  # lists only `nino`: every development skill is internal
```

Docs site change: in the nino.docs clone, `bun run check && bun run build`.

## Landing

- Read the diff once more as CI would: what would make a job fail? A red CI run costs a cycle and the owner's
  patience; never push to find out.
- Cherry-pick or merge workers' branches onto `origin/main`, drop session lines from their messages, add the trailers
  a user-facing change needs; then fast-forward `main` (AGENTS.md, Rules). The test branch takes merges only.
- Watch the runs of the pushed commit to the end, and fix a red one at once.
- After landing a user-facing change, the nino.docs changelog gets its entry (`audit-docs`).

## Releases (the owner's)

The owner runs Actions → Release on `main` with a version (`.github/scripts/bump_version.py` bumps every package,
the workflow tags, publishes NuGet, crates, npm and the Go tag through Trusted Publishing, and writes the notes from
the trailers); `report.yml` then benchmarks the tag on x64 and Arm64 runners and puts the Performance Report in the
release body, which the docs site syncs. CONTRIBUTING.md, "Releasing", has the details. Never trigger a release.
