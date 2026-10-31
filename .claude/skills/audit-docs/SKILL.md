---
name: audit-docs
description: "Audit and write all of Nino's docs - the nino.docs site (English and Chinese pages, changelog, benchmark data), README.md and README.zh.md, docs/ in this repository, CONTRIBUTING.md, the runtimes' READMEs and the public skill skills/nino-serialization - so that every feature is documented in order with its recommended form, its fast form, its costs and pitfalls, every statement matches the code, nothing is said twice, and English and Chinese agree. Use once the code is final (the docs pass), after a user-facing change lands, and before a release. Triggers: docs, README, *.mdx, nino.docs, changelog, page, example, en/zh, llms.txt, the public skill, \"docs out of date\", 文档, 网站, 中文文档, 更新日志, 例子, 文档同步, 写得不对."
metadata:
  internal: true
---

# Docs audit

The site is the separate repository JasonXuDeveloper/Nino.Docs (local clone `/workspace/nino.docs`; read its
AGENTS.md and `WRITING.md` first: page anatomy, tone, numbers, components). This repository holds README.md and
README.zh.md (the landing page on GitHub and NuGet), `docs/` (design, spec, codegen, diagnostics, pitfalls),
CONTRIBUTING.md, `runtimes/*/README.md` and the public skill.

## 1. Inventory: every feature, in order

Build the list from the source, not from the pages:

- every public attribute (`src/Nino.Core/Attributes`), its options and where it may go;
- every public entry point (`NinoSerializer`, `NinoDeserializer`, their overloads: arrays, spans, `IBufferWriter`,
  streams, exact-size and into-existing forms), `NinoSettings` member, pool and registry API;
- every MSBuild property and generator option users set, every NINO diagnostic and code fix;
- every built-in type and collection (BCL, immutable, concurrent, Unity types), each package (`Nino.AspNetCore`,
  `Nino.Immutable`, `Nino.UnityTypes`, Unity, `Nino.Codegen.Tool`) and each runtime's public API;
- every nino-gen option (`src/Nino.Codegen/Options.cs`) and what each language supports.

Then the reading order: the sidebar (`content/docs/v4/meta.json`) goes from what everyone needs (install, first
type, serialize) to what some need (versioning, polymorphism, references, pooling, custom formatters, security) to
integrations and reference. Every inventory item has one owning page and a place in that order; an item without one
is a finding, and so is a page that teaches a feature before the one it builds on.

## 2. Per page (English and Chinese)

1. **Anatomy** (`WRITING.md`, in this order): what it is; when to use it and when not (with what to use instead); how
   to use it, **the recommended form first** (an attribute, a generated path, the simplest overload) with a complete
   example that compiles; a full example where a short program can show the feature alone; **how to use it fast** (the
   overloads and settings that change the cost: exact-size buffers, `IBufferWriter`, reusing instances, pooling,
   `[NinoUtf8]`, raw structs, block copies, untrusted mode's cost), each with its benchmark through a component;
   what it costs (bytes on the wire, time, allocations, what is free when unused); pitfalls (Mistake | What happens |
   Caught by: the NINO rule, the exception); related pages.
2. **True to the code**: every API, attribute, option, default, limit and exception named exists today and behaves as
   written; check a doubtful claim with a throwaway test. Names match the code exactly. No API that is not in the
   source (the site's rule).
   A claim about the code's whole surface ("the reader is safe code", "the only `unsafe` is the SIMD module") is
   re-checked whenever that surface grows: grep the runtime READMEs, crate docs, the codegen page and the changelog
   for it (round 103: Rust's reference tracking added one `unsafe` block in `Shared::get`; the README's own first
   bullet, the codegen page and rc.8's entry still said only the SIMD module was unsafe).
   A claim about a tool outside Nino (a compiler flag, a runtime setting, an engine version) cites that tool's
   official docs or source in the commit body, and says only what the setting changes in Nino's code; a claim about
   Nino's own behavior is grepped first, even an obvious one: the build-configuration page's draft said Nino finds
   nothing through reflection in IL2CPP, but `NinoRegistry` reads assembly attributes (it now says only what the
   unity page states: registries are kept by `[RuntimeInitializeOnLoadMethod]`). Unity's Manual moved to
   docs.unity.com and many old docs.unity3d.com Manual URLs 404 or redirect; its ScriptReference pages (enum values
   such as `Il2CppCompilerConfiguration`) still answer with curl. Settings guidance by platform has one owner,
   `advanced/build-configuration`, which points to the owners of each detail (`IlcInstructionSet`: platforms; Burst
   targets and WebGL SIMD: unity; the wasm text module and `--numeric-arrays`: codegen).
3. **Examples compile and are exemplary**: every C# fence compiles against the current source (until a fence checker
   exists, paste the fences of the pages you touch into a scratch project that references `src/`), no NINO warning,
   models a real domain (game state, messages, saves), and uses the recommended form; byte examples match what
   Nino writes now (`wire-format`). Name the page's own helpers without the `Nino` prefix, which is the library's:
   a reader cannot tell `NinoStream.Read<T>` from an API (adapters named after the format, `NinoKafkaSerializer`,
   stay); grep the site for the new name first (round 103: `FrameStream` was already another page's class, so the
   streams helper became `StreamFrames`).
4. **Numbers**: never typed. Runtime numbers only through `BenchValue`, `RuntimeTable`, `ResultsTable`,
   `BenchmarkChart`, `RuntimesTables`, `UnityTables` from the synced data; within 5 % is on par and is written so; the
   docs never call Nino "slower"; v3 means 3.3.13.
5. **Chinese**: the same content and structure as English, written natively (`WRITING.md`, Tone: Chinese), headings
   with the English id (`## 键控成员 [#keyed-members]`), links with the same anchors.

## 3. Across pages and repositories

- **One owner per explanation**: other pages say it in a sentence and link. README, the site, `docs/` and the public
  skill do not repeat each other's long sections; README summarizes and links the site.
  For code generation: the site's codegen page owns the user guide (options and their naming rules, type mapping,
  per-language usage, generics, reference tracking, untrusted data, what is not generated); each `runtimes/*/README.md`
  owns its install, API reference and implementation notes and links the guide; `docs/codegen.md` is for
  contributors (pipeline, why each target writes what it does, adding a type, regenerating, tests). Before cutting a
  copy, move every fact only it has to its owner (round 102 moved the camel/snake rules, Go slot marker methods and
  Rust `RawNullables` to the site), keep the cut section's anchor and point the inbound links at the owner's anchor,
  so readers do not land on the summary (round 103: the platforms page's binary format and SIMD sections became
  summaries; about 30 links moved to `wire-format#strings` and `simd#dotnet`).
- **Copies that must exist agree**: README / README.zh.md, the site's index and comparison pages, the analyzers page
  and `docs/diagnostics.md`, `docs/spec/wire-format.md` and the site's wire-format page, the public skill, the
  runtimes' READMEs and the codegen page.
- **Every feature is everywhere it belongs** (`feature`, part 8): its guide page (both languages), the analyzers page
  for its rules, the platforms page if it is platform-specific, the public skill, README's feature list when users
  choose Nino for it, the changelog.
- **Changelog** (`changelog.mdx` / `.zh.mdx`): one section per v4 release (prereleases included), newest first; user-
  facing items only, "Wire Format" first, then "New", "Performance", fixes; within a subsection, entries of one area
  stay together (.NET, then its integrations, then TypeScript, Go and Rust), since agents append in commit order
  (round 103 regrouped six rc.8 fixes, keeping English and Chinese in the same order); each entry checked against the source,
  linking its issue and its page. Bring it in line with Nino at the start of any docs work (nino.docs AGENTS.md has
  the steps: releases, `git log <tag>..origin/main` with the trailers). A user-facing commit without a trailer
  needs a follow-up commit that carries one. The migration page's "Upgrading from an Earlier Release Candidate"
  table follows the same rule: a row only for a name the previous release shipped.
- **The public skill** (`skills/nino-serialization/SKILL.md`) holds what users' agents need to write correct, fast Nino code: the
  recommended forms, the fast forms, the pitfalls and the diagnostics, matching the pages.
- Stale names: grep the old names of every rename across both repositories; history pages (changelog, migration,
  v3-vs-v4) keep them on purpose.
- **The changelog describes what changed between releases, not within one.** Before writing a "renamed" or "removed"
  entry, check the old name shipped: `git grep -n <old> <last tag>`. An API that is new in the release under audit is
  described as it is released; renames made while it was unreleased go at most into one entry for code written
  against `main`. Evidence: rc.8's Go `BeginEntry`, function-value helpers and Rust `PREFETCH` had three entries for names no
  release had (the Go and Rust runtimes first shipped in rc.8); rc.8's entry said TypeScript's `region` and `exit`
  became `beginRegion` and `exitBody`, but rc.7 had only `region` (`exit` came after it), and Rust's
  `INLINE_DEPTH`/`MAX_DEPTH` got no migration row (round 103). Name the old API as that release shipped it,
  namespace included: rc.8's entry called the registry `Nino.Core.CompilerServices.NinoPolymorphic<TBase>`, but rc.7
  had it in `Nino.Core` (it moved and was renamed within rc.8), and said TypeScript's `notNull` and `flagAt` became
  `present` and `presentAt` when rc.7 had only `notNull` (round 103 final pass). Commit trailers cannot be corrected
  once they land, so the site's changelog and migration table are where such a slip gets fixed.
  The same holds for speed: a runtime or API new in the release gets no
  "N% faster" entries against `main` before it; fold what it achieves into the entry that introduces it, against
  competitors only through the synced data (rc.8's 14 Go and Rust Performance entries became two sentences in the
  runtimes' entry, round 102).
- **Chinese terms**: one per concept, from the glossary in nino.docs' `WRITING.md` (Tone: Chinese); grep the
  variants it lists across both repositories (README.zh.md too) in every pass. After replacing a term, grep for the
  spacing it leaves (a space before the CJK word, none after inline code: `的 存在字节`, `` `bool`或 ``; round 103
  found four), and read each hit of a variant before replacing it: 原地反序列化 meant reuse deserialization in
  README.zh.md but "read from the segment without a copy" on the streams and ASP.NET Core pages.
- **Renaming a site page's slug**: the old URL and its `.md` copy get a 301 in `public/_redirects` and an entry in
  `public/_routes.json`'s `exclude` (paths a function handles never reach the redirects); `git mv` its
  `scripts/csharp-check/Context/<slug>.cs` and change its `namespace Docs.Page_<slug>` (the check reports the page's
  types as missing otherwise); update links in v2/v3 pages that point at v4 and in Nino's README.md/README.zh.md
  (perf -> benchmarks, round 101; perf-runtimes and perf-unity -> benchmarks-runtimes and benchmarks-unity, round
  102). Also grep `src/` and `scripts/` for the slug: page URLs built in code (`bench-data.tsx` builds the Unity
  page's link) are not caught by the link checker.

- The code standard (`audit-code`: DRY, decoupled, clear) applies to these files, and redundancy in them
  (duplicated logic, dead code, two ways to do one job) is part of this audit; the sweep across layers is
  `audit-redundancy`.

## 4. Finish

- nino.docs: `bun run check && bun run build` (links, anchors, icons, types, prerender, llms.txt); commit docs apart
  from code; push to its `main`.
- This repository: links in README and `docs/` resolve; `tools/agents.sh --check` when maps or skills changed.
- Report per page: what was missing, wrong or duplicated, and what you changed; what you left (with the reason).
