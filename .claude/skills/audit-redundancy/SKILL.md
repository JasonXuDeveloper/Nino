---
name: audit-redundancy
description: "Find and remove redundancy everywhere in Nino - duplicated or dead code, unused files, generator stages or emitters that do the same work, analyzer rules that overlap, generated code repeated per type or per file, two APIs or options for one job, skills that state the same rule twice, docs that repeat each other, CI steps run twice - and merge each into one owner. Use in every audit round, after large merges or a burst of feature work, and whenever something looks familiar (\"didn't we already have this?\"). Triggers: redundant, duplicate, duplicated, dead code, unused, copy-paste, two ways, overlap, merge, consolidate, DRY, 冗余, 重复, 冗余代码, 冗余文件, 没用的, 合并, 去重."
metadata:
  internal: true
---

# Redundancy audit

Redundancy is the cost nobody measures: two copies drift apart, and a reader cannot tell which one is meant. Each
thing below has one owner; everything else points to it. This skill owns the sweep across layers; the specialist
skills fix what it finds in their files (the `audit` scopes table says who owns what).

## What to look for, per layer

| Layer | Redundant means | How to find it |
|---|---|---|
| Code | the same logic in two places (loops, parsers, checks, setup), a generic body per T, a family written by hand | `git grep` for distinctive lines; a clone detector (`npx jscpd --min-lines 8 src tests runtimes benchmarks`); compare files with similar names |
| Dead code | members, overloads, options, `#if` branches nothing uses | `dotnet build -p:EnableNETAnalyzers=true -warnaserror-` with IDE0051/IDE0052/CA1823 enabled for the run; `go vet`/`staticcheck` U1000; `cargo clippy` dead_code; `tsc --noUnusedLocals`; a grep for each public member's callers |
| Files | scripts, fixtures, projects, docs or workflows nothing references; one-off migration scripts whose job is done; generated files committed by accident | for each tracked file, `git grep -l <basename>` outside itself; workflows' `run:` lines; the solution file (`audit-layout` moves or deletes) |
| APIs and options | two entry points or settings for one job; an attribute that repeats what another or a parameter says | the public surface (`grep -n "public " src/Nino.Core`) grouped by what each does (`audit-core`; a public removal is the owner's call) |
| Generator | two stages or helpers computing the same fact (symbol walks in `NinoSymbols`, `TypeFacts`, `MemberCollector`), a model field derived twice, emitters that build the same text two ways | the pipeline's `WithTrackingName` list; grep for the same Roslyn calls across files (`audit-generator`) |
| Analyzer | two rules on one mistake, a rule that re-derives what the generator decides, a code fix duplicated per rule | snippets that trigger two rules; rules not sharing the model's checks (`audit-analyzer`) |
| Generated code | the same body emitted per type where a shared helper or a generic formatter would do, a file that copies code another file generates, setup written per member | output bytes per type (GeneratorBenchmark), the generated files diffed type against type (`audit-generated-code`) |
| Runtimes | the same codec written by hand in a runtime and in nino-gen's templates without a reason; logic in templates that belongs in the IR | `runtimes/*/internal/sample` against the generated output (`audit-runtimes`) |
| Tests and benchmarks | two tests checking the same thing, fixtures copied between projects, benchmark cases that measure the same path | test names per area; `tests/Shared` |
| CI | steps repeated across jobs, two jobs testing the same configuration, scripts doing one job twice | `audit-ci` |
| Docs | the same explanation on two pages, README repeating the site, `docs/` repeating the site or the spec, a public skill section that copies a page instead of summarizing it | one owner per explanation (`audit-docs`) |
| Skills and AGENTS.md | a rule stated in full in two skills, a skill whose scope another already covers, AGENTS.md repeating a skill | read the skills side by side; `grep` each rule's key phrase across `.claude/skills` and every `AGENTS.md` |

## Fixing

- Keep the better copy (faster, clearer, better tested), move every caller to it, delete the rest in the same change;
  a merged public API or attribute needs the owner and a migration note.
- Merging must not cost speed: hot paths keep their inlining (`optimize`), with runner numbers when in doubt.
- A rule merged between skills stays in full in its owner, with a one-line pointer in the others.
- Report: what was merged or deleted (old → new), what you kept twice on purpose and why (a fast path beside its
  portable fallback, a per-platform copy, a history page), and what is left for the owner.
