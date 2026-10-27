---
name: audit-naming
description: "Audit every name in Nino - public API, attributes and their options, settings, generated members and hint names, diagnostics' titles and messages, internal types, methods, fields, files and projects, tests and benchmarks, the runtimes' APIs in Go, Rust and TypeScript, docs headings and anchors, examples, and the Chinese terms - for ambiguity, wrong meaning, convention and consistency. Use in every audit round and on every new public name before it lands. Report first; public renames are the owner's call. Triggers: a new or renamed type, member, attribute, option, file, rule, page or term, \"is this name right\", 命名, 改名, 名字, 术语."
metadata:
  internal: true
---

# Naming audit

A name is the first doc a reader sees.

| Layer | Where |
|---|---|
| Public API | `src/Nino.Core` public types and members, attributes and their parameters, `NinoSettings`, exceptions' messages |
| Generated | members, types and hint names the generator writes; nino-gen's generated names per language |
| Diagnostics | NINO / NINOGEN titles, messages and code-fix titles |
| Internal code | internal and private types, methods, fields, locals, files, folders, projects |
| Runtimes | Go, Rust and TypeScript public APIs, following each language's conventions (Go `MixedCaps`, Rust `snake_case` and crate naming, TypeScript `camelCase`) while naming the same concept the same way |
| Tests and benchmarks | test names say what is checked; benchmark classes and methods as the reports show them |
| Docs | page titles, headings, anchors, file names, prose naming an API exactly as the code does |
| Skills | skill names and descriptions (agents pick a skill by them) |
| Chinese | one term per concept across pages and the public skill |

## For every name, ask

- **Ambiguity**: one word for two things (format, layout, raw, block, type id, version, key), or two names for one?
- **Behavior**: does it say its cost, whether it allocates, whether it throws? `Try` returns false, `Get` throws.
- **Convention**: does it match .NET's (and the serializers users compare with: MemoryPack, MessagePack) where the
  behavior is the same, and differ where it differs?
- **Call sites**: does it read well in the docs' examples and the tests?
- **History**: a name that no longer matches what the code does after a refactor; a file not named after the type or
  concern inside.

## Output and applying

- A table per layer: current | problem | proposed | breaking | confidence | call site before → after, ranked by
  impact, split into clearly right and owner's call, plus a one-line "checked and fine" list.
- Internal, test, benchmark (keeping the report keys the docs site syncs) and docs names are renamed without
  approval, in their own commit. Public renames wait for the owner; then they need the changelog, the docs in both
  languages, the public skill, and a migration note.
- Stale names: `git grep` the old name across this repository and nino.docs; only history pages keep it.
