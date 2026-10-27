---
name: audit-layout
description: "Audit and fix Nino's directory structure - where each project, script, test, fixture and doc lives, folders that mix concerns or hold one file, dead or one-off files, names of folders and files, and the agent instruction files that describe the tree (every AGENTS.md file map, the root index, CLAUDE.md imports, the skills links) - moving files with every reference updated. Use when adding a project, folder or script, when the tree feels messy, after a large merge, or when tools/agents.sh --check fails. Triggers: directory structure, folder, move, rename a file, file map, AGENTS.md, CLAUDE.md, tools/agents.sh, layout, misplaced file, 目录结构, 整理目录, 文件放哪, 文件清单."
metadata:
  internal: true
---

# Layout audit

A reader finds a file where its job says it is. The top-level layout is in the root AGENTS.md; each directory with
its own `AGENTS.md` maps every file in it.

## Look for

- **A folder that mixes concerns**: scripts of several jobs at one level (running, comparing, reporting, CI glue),
  projects next to their own tooling, tests next to fixtures they do not use.
- **A file in the wrong place**: a CI script under `benchmarks/`, a benchmark helper under `.github/`, a test project
  under `src/`, docs that belong to the website kept in the repository or the other way round.
- **Dead or one-off files**: migration scripts whose job is done (a restructure script after the restructure), stale
  fixtures, generated files committed by accident, folders with one file that could join a neighbor.
- **Names**: a folder or file named after its history rather than its job; two names for one kind of thing
  (`scripts/` here, `tools/` there).
- **Instruction files**: each directory with its own rules or a non-obvious set of files has an `AGENTS.md` (with a
  `> Read when:` line and a file map) and a `CLAUDE.md` that is `@AGENTS.md`; the root AGENTS.md's layout block is
  true; `.agents/skills` links to `.claude/skills`; every development skill is `metadata.internal: true`, and only
  `skills/` holds public ones.

- The code standard (`audit-code`: DRY, decoupled, clear) applies to these files, and redundancy in them
  (duplicated logic, dead code, two ways to do one job) is part of this audit; the sweep across layers is
  `audit-redundancy`.

## Moving files

- `git mv`, then update every reference: workflows (paths, `paths:` filters, cache keys), solution and project files,
  scripts that call each other, docs and READMEs, AGENTS.md maps, skills, `.gitattributes` and `.gitignore`.
  `git grep -n '<old path>'` must come back empty (history pages excepted).
- A move that published packages, OpenUPM or users' builds depend on (`src/Nino.Unity` is published from its path,
  `build/` props are imported by users) stays where it is unless the owner agrees.
- Run `verify` for what the references touch, `actionlint` for workflows, and `tools/agents.sh` (then `--check`).

## Report

Moves made (old → new, references updated), files deleted and why, and proposals left for the owner.
