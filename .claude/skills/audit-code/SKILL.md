---
name: audit-code
description: "Audit Nino's code for cleanliness and maintainability and fix it - the code standard every skill applies (DRY, decoupled, clear), coupling, god files and god methods, one problem solved two ways, comments that restate instead of explaining why - in the files no specialist owns: tools, the structure of tests, benchmarks and their Python scripts, .github scripts, the Unity project and the seams between projects. Use for a cleanup round, after large merges or a burst of optimization work, or when code was written in a hurry, or when the test suite is slow. Triggers: tools/, tools/CodeGen, tests/Shared, slow tests, test suite too slow, MSTestSettings, DoNotParallelize, benchmarks/*.py, python, .github/scripts, src/Nino.Unity, refactor, clean up, cleanup, coupling, god file, god class, maintainability, 整洁, 清理, 重构, 解耦, 重复代码, 可维护性, 脚本, 太大了, 拆分, 测试太慢."
metadata:
  internal: true
---

# Code audit (cleanliness)

Fast code that nobody can change is not done. This audit reads every project for what makes code hard to change and
fixes it without making it slower. The code standard below is stated here in full; `audit-core`, `audit-generator`,
`audit-generated-code`, `audit-analyzer` and `audit-runtimes` apply it to their own files, and this audit owns
everything else and the seams between projects. Where files
live is `audit-layout`.

## Owns

`tools/`, the structure and shared fixtures of `tests/`, the harness and scripts of `benchmarks/`, `.github/scripts`
and `.github/actions`, `src/Nino.Unity` (its C# outside the package's runtime code), and code duplicated across
projects or languages.

## The code standard (stated here; every skill that writes code applies it)

- Apply `IDE0200` to one-time registrations where the method group preserves behavior. Cache the
  delegate explicitly at repeated lookup sites built by older Unity compilers so conversion to a
  method group does not introduce per-call allocations (`64b1a239`). Check compiler informational
  diagnostics via build SARIF and editor-only suggestions via a separate style scan.

### DRY

- **One implementation**: the same logic lives in one place (two loops, two parsers, two setup blocks, two copies
  of a test fixture, two Python scripts reading one report format, the same workflow steps in several jobs are
  findings). Merge into one shared core with thin typed wrappers; generate what differs only by type or arity
  (`tools/CodeGen`, the generator's templates, nino-gen's IR), never copy it by hand.
- **One way per job**: one problem is solved one way across the codebase (throw helpers, buffer patterns, report
  parsers, test idioms, option plumbing). Pick the better one, move every site to it, and write the choice into the
  owning skill so new code follows it.
- **One source of truth for a fact**: a name, a constant, a layout or a limit is declared once and referenced
  (`NinoNames` for the generator's view of Nino.Core, `wire.go` / `NinoWire` for wire constants, `Options.cs` for
  nino-gen's options); a second copy in another language is generated or checked against the first by a test.
  A fixture two C# test suites read lives where the stricter build can compile it and the other links it
  (`<Compile Include=... Link=...>`): Unity compiles only files under its `Assets/`, so CoreCLR's bytes for raw BCL
  layouts (`BclFixtures.cs`, `BclFixtureTypes.cs`) live in `src/Nino.Unity/Assets/Test/Runtime` and
  `tests/Nino.RuntimeTests` links them; Unity's `BclLayoutTests` had held a hand-copied hex string per case (round 104).
- Kept twice on purpose (a fast path beside its portable fallback, a per-platform or per-language copy the bytes are
  tested against) says so in a comment. The sweep across layers is `audit-redundancy`.

### Decoupled

Dependencies point one way, and each piece reaches the others through a public seam:

| Piece | May depend on | Never |
|---|---|---|
| `src/Nino.Core` | the BCL (per TFM) | any other Nino project, Unity, ASP.NET Core |
| `Nino.Immutable`, `Nino.AspNetCore`, `Nino.UnityTypes`, the Unity Burst kernels | Nino.Core's public API and its registration seams | each other; Nino.Core knowing them (they register themselves) |
| `src/Nino.Generator` (+ CodeFixes, Roslyn builds) | Roslyn only; Nino.Core by metadata names (`NinoNames`) | a reference to the runtime assembly |
| `src/Nino.Codegen` | the published layouts (`[NinoTypeLayout]`), its IR | the generator's internals |
| `runtimes/go`, `rust`, `typescript` | nothing (no dependencies; competitors live in their `bench` modules) | each other, .NET |
| `tests/`, `benchmarks/`, `tests/conformance/` | the projects they exercise | being referenced from `src/` |
| scripts (`benchmarks/*.py`, `.github/scripts`) | their inputs' documented formats | another script's internals (share a module instead) |

- `InternalsVisibleTo` is a coupling: allowed for the unit tests; for a package it needs a reason in a comment, and
  the internal it reaches is a candidate for a public or `EditorBrowsable(Never)` seam.
- A feature that must touch many files for one change, a module that knows another's private state, a cycle between
  files or types, and a `#if` block that grew into a second implementation are findings.

### Clear

- **No god files or god methods**: split by responsibility (partial classes, one file per concern), keeping hot paths
  inlined (`optimize`).
- **Comments say why**: invariants, formulas, magic numbers, the reason for a non-obvious choice; no comment that
  restates the code; XML docs on public members.
- **Standard practice over invention**: a BCL, Roslyn, Go, Rust or Node facility that already does the job replaces a
  hand-rolled one, unless the hand-rolled one is measured faster where it matters.
- **Fast and simple together**: a simplification that costs speed is not one; a fast path that duplicates a slow one
  shares its core.
- **Dead code** goes: members, files, options, scripts, workflow inputs and test helpers nothing uses; commented-out
  code; one-off migration scripts whose job is done.

- Redundancy in these files (duplicated logic, dead code, two ways to do one job) is part of this audit; the
  checklist across layers is `audit-redundancy`.

## The test suite's time

- **Measure a slow suite by phase before changing it**: restore, build (`-bl` and `ReportAnalyzer`, replayed with
  `dotnet msbuild x.binlog -noconlog "-flp:v=diag;performancesummary;logfile=x.log"`: the analyzer and generator
  tables per compilation), `--list-tests`, then a run with `--logger trx` summed per class and per test (a trx whose
  durations add up to the wall time ran in sequence) and `time` for the CPU it used. Round 128 (local, 4 cores): the
  build took 27 s cold for three TFMs (the generator about 2 s of each of the test project's compilations), the
  run 5 min 10 s per TFM, 66 % of it `ShapeMatrixTests` (16 rows of about 12 s: Roslyn generates, binds and emits
  some 1 MB of source, then the probes run), all in sequence because nothing turned MSTest's parallelism on.
- **Keep the unit suite parallel by class** (`tests/Nino.UnitTests/Infrastructure/MSTestSettings.cs`), with the
  rules in `tests/AGENTS.md`: classes keep their methods in sequence because many reset static counters in
  `[TestInitialize]`; a test that measures process-wide state (`GC.GetTotalMemory`, the shared pools, `NinoKernels`)
  is `[DoNotParallelize]`, which a parallel run proves by failing (`CultureNamesFromTheDataCannotGrowTheProcess`
  saw 14 to 250 MB of the other tests' heap). The suite is then CPU-bound: about 400 s of CPU on net10.0, most of it
  the ShapeMatrix compilations, so the wall time is the CPU over the cores; cut CPU next, not waits.
- **Share Roslyn inputs between test compilations**: one `MetadataReference` object per file for the whole run
  (`TestCompilation`, `TestReferences`), since Roslyn reuses what it read per reference object and reads a new one
  again (net10.0's analyzer and code-fix tests: 30 s to 17 s in sequence, local); compute what most callers do not
  read on first use (`CornerCompiler.Result.AnalyzerDiagnostics`: an analyzer pass of about 1 s a ShapeMatrix row).
- Measured and left: Release instead of Debug changed nothing (Roslyn comes optimized from NuGet; the time is
  Roslyn's), and `DOTNET_TieredCompilation=0` cut CPU by about a quarter but runs Nino's code at full opts only,
  not as users' processes run it. `PrefetchTests` scale with the machine's last-level cache (the threshold is a
  quarter of it over an element's bytes): 20 s on a 260 MB L3, an eighth of that on a runner's 32 MB.

## Fixing

- One concern per commit (logic, docs and CI apart); behavior unchanged unless a bug is found (then a regression
  test).
- Hot-path code: machine code and runner numbers before and after (`optimize`, `benchmark`); anything slower is
  reverted.
- After each change, the checks of `verify` for what it touched.
- Report what was merged, split, deleted and why; what was left (with the reason).
