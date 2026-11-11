---
name: audit-layout
description: "Audit and fix Nino's directory structure - where each project, script, test, fixture and doc lives, folders that mix concerns or hold one file, dead or one-off files, names of folders and files, and the agent instruction files that describe the tree (every AGENTS.md file map, the root index, CLAUDE.md imports, the skills links) - moving files with every reference updated. Use when adding a project, folder or script, when the tree feels messy, after a large merge, or when tools/agents.sh --check fails. Triggers: directory structure, folder, move, rename a file, file map, AGENTS.md, CLAUDE.md, tools/agents.sh, layout, misplaced file, 目录结构, 整理目录, 目录太乱, 整理一下目录, 文件放哪, 文件清单."
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
  `skills/` holds public ones. A skill's `references/*.md` files are listed by nothing but its SKILL.md: each is
  linked from there with when to read it, and a file no SKILL.md links is dead.

- The code standard (`audit-code`: DRY, decoupled, clear) applies to these files, and redundancy in them
  (duplicated logic, dead code, two ways to do one job) is part of this audit; the sweep across layers is
  `audit-redundancy`.

## Moving files

- `git mv`, then update every reference: workflows (paths, `paths:` filters, cache keys), solution and project files,
  scripts that call each other, docs and READMEs, AGENTS.md maps, skills, `.gitattributes` and `.gitignore`.
  `git grep -n '<old path>'` must come back empty (history pages excepted).
- A move that published packages, OpenUPM or users' builds depend on (`src/Nino.Unity` is published from its path;
  the props and targets in each package's `build/` folder, such as `src/Nino.Generator/build`, are what NuGet imports
  into users' builds) stays where it is unless the owner agrees. The root `build/` is not one of them: only this
  repository's projects import it.
- Run `verify` for what the references touch, `actionlint` for workflows, and `tools/agents.sh` (then `--check`).

## Splitting long files

- **Split a long Markdown document into `<name>.md` and a `<name>/` folder of parts, and keep `<name>.md` as the
  index that links every section**: links from outside the repository (the docs site's link to the GitHub file)
  and comments that cite a section by name ("Deep nesting in docs/v4-design.md") keep landing. Keep each heading's
  text when its level changes (GitHub's anchor is the text), repoint `file.md#anchor` links to the part, and run a
  relative-link and anchor checker over every `*.md` after the move (`docs/v4-design.md`, 1,197 lines, became
  `v4-design/binary-format.md`, `runtime.md` and `generator.md`; 0 of 343 links broken). A specification with
  numbered sections and its own contents (`docs/spec/wire-format.md`) stays one file: readers cite it by `§`.
- **Split a long C# class into partial files named `Class.Concern.cs`**, each holding its concern's members and the
  delegates or constants only it uses (`NinoBurst.Permute.cs`, `.Text.cs`, `.Interleave.cs`, `.Vectors.cs`); one
  class-level attribute (`[BurstCompile]`) covers every part. In the Unity project each new file gets a `.meta`
  with a fresh GUID (`python3 -c 'import uuid;print(uuid.uuid4().hex)'`, then `grep -rh '^guid:' --include=*.meta
  . | sort | uniq -d` is empty), and a moved file is `git mv`ed with its `.meta`. Unity's sources do not build
  here: compile the old and the new files against small stubs of the Unity API they use and compare the members
  both assemblies declare (MetadataLoadContext); only compiler-generated names may differ.
- In the Unity project an asmdef, not the folder name, decides where an assembly compiles: `Assets/Test/Editor/Model`
  and `CrossRef` build for players too and sit under `Editor/` because only the editor tests use them (their
  namespaces say `Test.Editor`); move them only together with their namespaces.

## Adding files that nothing builds

A new kind of file inside source folders (instruction files, notes, eval runs, generated reports) reaches every
mechanism that selects files by path. Check each before landing it:
- workflow `paths:` filters: exclude it where it only costs runs (`'!**/AGENTS.md'`); the AGENTS.md files once ran
  every job, Unity's single license seat included, for a change nothing builds;
- packages: Cargo `exclude` (the crate ships its whole folder otherwise), npm `files`, NuGet `Pack` items, the Unity
  package copy in `release.yml`; a Go module always holds its folder, so keep only harmless text there;
- `.gitignore` for what tools write next to sources (skill-creator's `<skill>-workspace/`), and patterns broad enough
  to hide a source folder (anchor them: `/memory/`); `.gitattributes` for line endings, binaries and generated files;
- scripts that walk a folder (`glob`, `rglob`, `os.walk`) and treat every file in it as theirs.

## Report

Moves made (old → new, references updated), files deleted and why, and proposals left for the owner.
