---
name: audit
description: "Run an audit round of Nino with parallel agents in worktrees - layout, redundancy, code, core, security, performance, machine code, generator, generated code, analyzer, runtimes, CI, naming, docs - code first, docs last, then triage, land and verify. Use when asked to review the whole codebase, to clean up, to find what is left, or to \"do another round\". Triggers: \"another round\", \"review everything\", \"audit\", \"scan\", 再来一轮, 全面审查, 审一遍, 扫一遍, 检查整个项目, 代码质量扫描."
metadata:
  internal: true
---

# Audit round

One round = parallel audits, each in its own worktree and branch off `origin/main`, then triage and landing.

## Scopes (one agent each, never two on the same files)

| Skill | Owns | Looks for |
|---|---|---|
| `audit-layout` | directory structure, project split, file placement, every `AGENTS.md` file map | misplaced or dead files, folders that mix concerns, maps out of date |
| `audit-code` | `tools/`, `tests/` structure, `benchmarks/` scripts, `.github/` scripts, `src/Nino.Unity`, the seams between projects | non-DRY code, coupling, dead code, god files, two ways to do one thing |
| `audit-core` | `src/Nino.Core`, `src/Nino.Immutable`, `src/Nino.AspNetCore`, `src/Nino.UnityTypes` | correctness, redundancy, coupling, lifecycle, threads, platform fallbacks |
| `audit-security` | every read path (.NET and the runtimes), `tests/Nino.Fuzz`, the malicious corpus | hostile input that crashes, over-allocates, recurses or reads outside |
| `audit-perf` | no files: research, prototypes, A/B on runners | the next win, gaps against MemoryPack and each runtime's competitors |
| `optimize` | read and write paths' machine code (with the owners of the files) | budget overruns, spills, calls in loops, per-ISA problems |
| `audit-generator` | `src/Nino.Generator` pipeline, models, `src/Nino.Generator.Roslyn*` | incremental rules, IDE cost, shapes that do not compile |
| `audit-generated-code` | the templates and emitters of `src/Nino.Generator/Generation/Emit`, nino-gen's templates | generated text and its machine code |
| `audit-analyzer` | `src/Nino.Generator/Analysis`, `src/Nino.Generator.CodeFixes` | missing rules, false positives, code fixes, tests per rule |
| `audit-runtimes` | `runtimes/*`, `src/Nino.Codegen` | safety, speed, SIMD, parity with .NET |
| `audit-ci` | `.github/workflows`, `.github/actions` | slow or serial jobs, red risks, pinning |
| `audit-redundancy` | no files of its own: the sweep across every layer; fixes go to the owners | duplicated or dead code, unused files, overlapping stages, rules, APIs, generated code, skills and docs |
| `audit-naming` | every name: a report; internal renames in their own commit | ambiguous or inconsistent names, Chinese terms |
| `audit-docs` | README, README.zh.md, `docs/`, CONTRIBUTING.md, runtime READMEs, `skills/nino`, nino.docs | docs that disagree with the code or each other, duplication, gaps |

`audit-generator`, `audit-generated-code` and `audit-analyzer` share `src/Nino.Generator`: give the shared files
(`NinoSymbols.cs`, `Model*.cs`) to one of them, or run them in different rounds. `optimize` and `audit-perf`
report findings to the owner of the file unless the round gives them the file. `audit-naming` reports only, so it
runs beside any of them; `audit-redundancy` reports its findings to each owner, or fixes them itself in a round where
the owners are not running.

## Order

Code audits first; then `audit-docs` once over everything with the collected docs to-dos (agents do not edit doc
pages while code still changes: they list what each page must now say). Every agent also reports what it left undone,
which goes on the same list: nothing is dropped silently.

## Starting an agent

- `git -C /workspace/Nino worktree add <scratchpad>/<branch> -b <branch> origin/main`; the agent reads AGENTS.md,
  its skill, `feature`, and the AGENTS.md of each directory it owns.
- Give it: its skill, the paths it owns and must not touch, the commit command (AGENTS.md), "commit on your branch,
  do not push or merge", the shell rules (lock, PATH), and that it must wait for its own background commands instead
  of ending its turn.
- Scratch files carry the branch name as prefix: agents share the scratchpad.
- A/B runs on runners go through the test branch: tell agents to merge the latest test branch before pushing and to
  batch requests (`benchmark`).
- At most about five agents at once; agents that run heavy builds or benchmarks share `/tmp/nino-heavy.lock`.

## Triage and landing

1. Read each report and its diff. Reject changes that widen the API without need, weaken a check, or move a number
   the wrong way; read every test an agent changed: assertions stay.
2. Land one branch at a time on `origin/main` (cherry-pick, fix the messages), with `verify` for what it touched; a
   hot-path branch lands only with runner numbers against main.
3. After the code lands, run `audit-docs` with the to-do list, then `verify`, then fast-forward `main`.
4. Update the skills with what the round taught and cross-check them (AGENTS.md, Workflows).
5. Remove finished worktrees (`git worktree remove`, `git worktree prune`).
