# The changelog and the migration table

Part of the `audit-docs` skill. How nino.docs' changelog (`changelog.mdx` / `.zh.mdx`) and the migration page's
"Upgrading from an Earlier Release Candidate" table are written and kept in line with Nino's releases and trailers.
Read it before writing or checking a changelog entry, a migration row, a highlight trailer or a git note in
`refs/notes/release`.

## Shape

- **One section per v4 release (prereleases included), newest first; user-facing items only**: "Wire Format" first,
  then "New", "Performance", fixes; within a subsection, entries of one area stay together (.NET, then its
  integrations, then TypeScript, Go and Rust), since agents append in commit order (round 103 regrouped six rc.8
  fixes, keeping English and Chinese in the same order); each entry checked against the source, linking its issue and
  its page.
- **Bring it in line with Nino at the start of any docs work** (nino.docs AGENTS.md has the steps: releases,
  `git log <tag>..origin/main` with the trailers). A user-facing commit without a trailer needs a follow-up commit that
  carries one.
  Read the highlights as the release body will: fetch the notes (`git fetch origin +release-notes:refs/notes/release`)
  and, per commit, take its note in `refs/notes/release` when it has one, else its trailers
  (`git notes --ref=release show <sha>`, `git log -1 --format='%(trailers:key=Added,key=Fixed,...,unfold)' <sha>`),
  then match every highlight to a changelog entry. Why: the pre-rc.10 audit found the `Fixed:` note of 86158168
  (a removed assembly kept alive) in no changelog entry; reading trailers alone misses notes that add a highlight.
- **The migration page's "Upgrading from an Earlier Release Candidate" table follows the same rule**: a row only for a
  name the previous release shipped.

## Between releases, not within one

- **The changelog describes what changed between releases, not within one.** Before writing a "renamed" or "removed"
  entry, check the old name shipped: `git grep -n <old> <last tag>`. An API that is new in the release under audit is
  described as it is released; renames made while it was unreleased go at most into one entry for code written
  against `main`. Evidence: rc.8's Go `BeginEntry`, function-value helpers and Rust `PREFETCH` had three entries for
  names no release had (the Go and Rust runtimes first shipped in rc.8); rc.8's entry said TypeScript's `region` and
  `exit` became `beginRegion` and `exitBody`, but rc.7 had only `region` (`exit` came after it), and Rust's
  `INLINE_DEPTH`/`MAX_DEPTH` got no migration row (round 103).
- **Name the old API as that release shipped it, namespace included**: rc.8's entry called the registry
  `Nino.Core.CompilerServices.NinoPolymorphic<TBase>`, but rc.7 had it in `Nino.Core` (it moved and was renamed within
  rc.8), and said TypeScript's `notNull` and `flagAt` became `present` and `presentAt` when rc.7 had only `notNull`
  (round 103 final pass).
- **The same holds for speed**: a runtime or API new in the release gets no "N% faster" entries against `main` before
  it; fold what it achieves into the entry that introduces it, against competitors only through the synced data
  (rc.8's 14 Go and Rust Performance entries became two sentences in the runtimes' entry, round 102).

## Fixing a trailer that landed wrong

- **Correct it with a git note in `refs/notes/release`, never by rewriting `main`** (the owner's choice, round 104):
  the note replaces that commit's highlight trailers in the release body (`release_notes.py`), and the site's
  changelog and migration table get the same fix.
