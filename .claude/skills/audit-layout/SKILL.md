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
- **A flat folder**: scripts of several jobs side by side at one level go into subfolders named by job, as
  `benchmarks/` did (reports/: what a release publishes; compare/: two runs while developing; asm/: machine code;
  run.sh, shards.json and members/ stay at the root). A module several jobs share stays with its main users, and the
  others put that folder on `sys.path` (`reports/bdn.py`, imported by compare/ and members/table.py). Test files
  group by what they test (`tests/Nino.UnitTests/Generator/{Analyzer,CodeFixes,LayoutBaseline,GeneratedCode}`,
  `tests/Nino.Fuzz/{Engine,Checks,Inputs}`), code by the part of the pipeline (nino-gen's
  `Targets/{Go,Rust,TypeScript}`).
- **A file too long to read**: past roughly 800 lines a file with sections (`#region`, `// ----- x` markers, test
  groups by rule) is split along them; how, and how to prove nothing changed, is "Splitting long files" below. A
  test part carries its topic in a comment above its partial declaration, and secondary top-level types go to their
  own files (`Pattern.cs`, `GoShape.cs`); the class keeps its name, so test names, filters and the docs naming them
  stay valid.
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

- Check a moved file is tracked at its new path (`git check-ignore -v <path>`, then `git ls-tree HEAD <path>`), and run
  `tools/agents.sh --check` in a clean clone of the commit, not the working tree: `.gitignore` ignores `.claude/*` but
  the folders it lists, so `git mv CLAUDE.md .claude/` staged only the deletion and agents.yml failed on main (e5b1a258).
- `git mv`, then update every reference: workflows (paths, `paths:` filters, cache keys), solution and project files,
  scripts that call each other, docs and READMEs, AGENTS.md maps, skills, `.gitattributes` and `.gitignore`.
  `git grep -n '<old path>'` must come back empty (history pages excepted).
- Prove a move or split of code changed nothing: for the generator, build tests/Nino.UnitTests with
  `-p:EmitCompilerGeneratedFiles=true` before (a clean worktree of the base) and after, and `diff -r` every
  `obj/**/generated` tree (byte-identical when the generator and its tests were split, test files moved too: Roslyn
  4.12's interceptor locations hash the file's content, not its path); for nino-gen, `NINO_UPDATE_GENERATED=1 dotnet
  test tests/Nino.Codegen.Tests` must leave `git status` clean. A splitter finds a class's closing brace as the last
  line at its indentation: test files hold C# sources in strings with `}` at column 0. Prune the usings a split leaves
  by building with `-p:EnforceCodeStyleInBuild=true -p:GenerateDocumentationFile=true` and a temporary `.editorconfig`
  raising IDE0005 to a warning (`dotnet format style --diagnostics IDE0005` removed nothing), then a plain build proves
  each removal.
- A move that published packages, OpenUPM or users' builds depend on (`src/Nino.Unity` is published from its path;
  the props and targets in each package's `build/` folder, such as `src/Nino.Generator/build`, are what NuGet imports
  into users' builds) stays where it is unless the owner agrees. The root `tools/UseNinoGenerator.props` is not one of them: only this
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
- **Split a long C# type by concern into partial files named `Type.Concern.cs` beside `Type.cs`**, one line each in
  the directory's AGENTS.md map: `Reader.Strings.cs`, `NinoLayout.Measure.cs`, startup checks in `.Checks.cs`
  (`HashTables.Checks.cs`), a .NET 8-only vector path in its own file wrapped whole in `#if NET8_0_OR_GREATER`
  (`NinoBlockCollections.Lanes.cs`); a folder per kind where one folder holds a dozen files of several kinds
  (`Formatters/Collections/Arrays`, `Sequences`, `Dictionaries`, `Concurrent`). Why: `partial` and moves leave the
  IL as it was, and namespaces do not follow folders here (`Nino.Core`, `Nino.Core.Internal`,
  `Nino.Core.CompilerServices`): keep them, a folder changes no API. Prove a split by the IL, not by `asm.py` alone:
  dump every method body with its tokens resolved to names (System.Reflection.Metadata), base and branch, and
  compare with the compiler's lambda and closure ordinals (`b__83_0`, `<>c__58`) masked, since they follow member
  order. The codex/nino-audit review's core split: identical in all four Nino.Core builds; `asm.py compare` still listed
  `Writer.Write(string)` (517 to 1,581 bytes, its string writes inlined in one of the head's runs: profile data).
- **Move methods and nested types only: every static field with an initializer, and every instance field of a struct,
  stay in the main file.** Why: the field initializers of a partial type run in an unspecified order across its
  files (C# spec, field initialization), and instance fields of a struct in two parts warn (CS0282, layout order
  undefined). `NinoBlockPlan`, `CollectionViews`, `HashTables` and `ConcurrentTables` keep their flags, each read
  from the one before it, in one file; their tables, candidates and checks moved out.
- **In the Unity project, split a class the same way and give each new file a `.meta`** with a fresh GUID
  (`python3 -c 'import uuid;print(uuid.uuid4().hex)'`, then `grep -rh '^guid:' --include=*.meta . | sort | uniq -d`
  is empty); a moved file is `git mv`ed with its `.meta`. One class-level attribute (`[BurstCompile]`) covers every
  part (`NinoBurst.Permute.cs`, `.Text.cs`, `.Interleave.cs`, `.Vectors.cs`), and each kernel's delegate moves with
  it. Unity's sources do not build here: compile the old and the new files against small stubs of the Unity API they
  use and compare the members both assemblies declare (MetadataLoadContext); only compiler-generated names may differ.
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
