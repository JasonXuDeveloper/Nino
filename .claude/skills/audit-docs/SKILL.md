---
name: audit-docs
description: "Audit and write all of Nino's docs - the nino.docs site (English and Chinese pages, changelog, benchmark data), README.md and README.zh.md, docs/ in this repository, CONTRIBUTING.md, the runtimes' READMEs and the public skill skills/nino-serialization - so that a reader finds each answer fast and every feature is documented in order with its recommended form, its fast form, its costs and pitfalls, every statement matches the code, nothing is said twice, and English and Chinese agree. Use once the code is final (the docs pass), after a user-facing change lands, and before a release. Triggers: docs, README, *.mdx, nino.docs, changelog, page, example, en/zh, llms.txt, the public skill, sidebar, page order, overlap, reader experience, \"docs out of date\", 文档, 网站, 中文文档, 更新日志, 例子, 文档同步, 写得不对, 侧边栏, 页面顺序, 阅读体验, 重叠."
metadata:
  internal: true
---

# Docs audit

The site is the separate repository JasonXuDeveloper/Nino.Docs (local clone `/workspace/nino.docs`; read its
AGENTS.md and `WRITING.md` first: page anatomy, tone, numbers, components). This repository holds README.md and
README.zh.md (the landing page on GitHub and NuGet), `docs/` (design, spec, codegen, diagnostics, pitfalls),
CONTRIBUTING.md, `runtimes/*/README.md` and the public skill.

## 0. The reader first

- **Audit and optimize from the reader's experience; every other rule in this skill (inventory, anatomy, visual
  forms, the IA checklist, components) serves this one.** Before and after a docs pass, walk the site as three readers
  and count the friction: (1) a newcomer who wants a first type serialized in five minutes (home -> Quick Start ->
  Serialize and Deserialize -> Defining Types); (2) a user hunting one fact ("how do I version a type", "is X
  supported on IL2CPP", "what does NINO007 mean"); (3) an evaluator comparing Nino with MemoryPack (vs-memorypack ->
  benchmarks -> limitations). Count clicks and scrolls to the answer, dead ends, a word used before it is explained,
  walls of text, anything read twice; fix them, and put the before/after walk in the report. A change that makes a
  page richer but not easier to read is not made. Why: the owner's rule (round 107: "the main goal is the reader's
  reading experience"). Evidence: round 107's walk found Quick Start seventh in the sidebar behind six "Overview"
  pages and the introduction's "Start Here" at its bottom, "Versioning" (Nino's format history) beside "Evolving
  Your Data" (versioning your types), NINO007 one row of seven tables, and the evaluator's pages split between
  "Overview" and "Reference"; nino.docs `WRITING.md`, "The reader first", has the three journeys.

## 1. Inventory: every feature, in order

Build the list from the source, not from the pages:

- every public attribute (`src/Nino.Core/Attributes`), its options and where it may go;
- every public entry point (`NinoSerializer`, `NinoDeserializer`, their overloads: arrays, spans, `IBufferWriter`,
  streams, exact-size and into-existing forms), `NinoSettings` member, pool and registry API;
- every MSBuild property and generator option users set, every NINO diagnostic and code fix;
- every built-in type and collection (BCL, immutable, concurrent, Unity types), each package (`Nino.AspNetCore`,
  `Nino.Immutable`, `Nino.UnityTypes`, Unity, `Nino.Codegen.Tool`) and each runtime's public API;
- every nino-gen option (`src/Nino.Codegen/Options.cs`) and what each language supports.

Then the reading order: the sidebar (`content/docs/v4/meta.json`) is the reader's path: Get Started, Define Types,
Features, Guides (the collapsed `advanced/` folder), Compare, Reference, Project (nino.docs `WRITING.md`, Order). Every inventory item has one owning page and a place in that order; an item without one
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
5. **Form: the most efficient one, prose last** (the owner: "docs must be intuitive, not necessarily many words").
   Rewrite every section a table, a support matrix (`<Matrix>`), a byte figure (`<Bytes>`), a Mermaid diagram,
   `<Steps>`, code-block tabs, a `TypeTable` or a `Files` tree shows faster; keep prose for why, trade-offs and
   caveats. Readers scan, and a paragraph of facts hides the one they look for. Round 105: the security page's list
   of rejected inputs became one default-versus-`Untrusted` matrix, the evolving-data and vs-memorypack ✓/✗ tables
   matrices, an ASCII `length | Id | CustomerId` comment a v1/v2 byte figure. How each form is written is nino.docs'
   `WRITING.md`, "Visual forms".
   - **Re-read Fumadocs' component and MDX-plugin list for the site's version in every docs pass** (fumadocs-ui and
     fumadocs-core in its package.json, their `dist/components` and `dist/mdx-plugins`) and adopt what fits a
     prerendered site with Markdown copies: most of the value was installed and unused (round 105, 16.17.3: code-block
     tabs replaced 34 `<Tabs>` that only wrapped fences; `remarkSteps` was rejected because it runs after
     `remarkHeading`, whose ids and TOC keep the `[step]` tag).
   - **Colour only in byte figures and matrices**: the five calm category tints (`--nino-cat-1` to `-5`, light and
     dark, fixed meanings) are allowed there and nowhere else, where the single accent stays (the owner's decision,
     round 105); a page's meaning never depends on colour alone (the Markdown copy has none).
   - **Diagrams are Mermaid, drawn at build time** (EasyEcs docs' way: `rehypeMermaid` in Playwright's Chromium,
     cached per page by sha256 of Mermaid's version, the options and the diagrams, in memory and in
     `node_modules/.cache/mermaid`; the page loads no Mermaid, the Markdown copy keeps the source): small `LR` flowcharts
     of rounded nodes, never taller than about 420px at desktop width (a decision with a few outcomes is a table: round
     105's diamond trees were taller than a screen), Chinese labels on Chinese pages, never hand-drawn SVG. The cache is what keeps local builds fast; Cloudflare
     Pages keeps no `node_modules/.cache` for a React Router site, so its builds draw every diagram (measured in
     round 105: nino.docs AGENTS.md, Build & Deploy).
   - **Byte figures come from the specification's worked examples** (Appendix A, dumps of a real run) or from its
     rules checked against them, never from memory.
6. **Chinese**: the same content and structure as English, written natively (`WRITING.md`, Tone: Chinese), headings
   with the English id (`## 键控成员 [#keyed-members]`), links with the same anchors.

## 3. Across pages and repositories

- **Run the information-architecture checklist in every docs pass** (nino.docs `WRITING.md`, "Information
  architecture": one home per fact, the sidebar as the reader's path with sections of about 3 to 7 and folders for
  bigger clusters, short-noun titles that match their links, common before rare within a page, a lead and next steps
  on every page, length limits, redirects for moved pages and anchors, no orphan pages or duplicate examples, the
  newcomer path from the home page, titles and descriptions that read alone in search and llms.txt, 390px width).
  Why: pages are added one feature at a time, and nobody sees the whole until the sidebar is too long to scan (the
  owner, round 107: "the left sidebar has a lot in it; is its order right?"). Evidence: round 107 found an Overview
  section of six pages before Quick Start and a Reference section of eleven mixing specs with benchmarks, and
  rebuilt the sidebar into seven sections of 3 to 7 with Guides collapsed; it merged v3-vs-v4 into whats-new
  (`#features`, redirects and an AnchorRedirect for `#runtime`), moved best practices' copy of the zero-allocation
  recipes to `zero-allocation#message-loop`, cut format versions' and limitations' repeats of whats-new, moved the
  glossary out of basic into its own page, and gave supported types and untrusted data next steps (both were dead
  ends).
- **Interactive components serve finding and comparing, from one data source, and read the same without
  JavaScript** (nino.docs `WRITING.md`, Visual forms: `AnalyzerRules`, the benchmark tables' ratio bars, `Release`,
  `Term`/`Glossary`, `BytePlayground`; when to use each). Each prerenders its full content and has a Markdown form
  from the same JSON. A NINO rule added or changed in Nino is a row of nino.docs' `src/data/analyzer-rules.json`
  (English and Chinese) in the same docs pass, as it is a section of `docs/diagnostics.md`; a new glossary term goes
  into `src/data/glossary.json`. Why: the owner asked why a filterable rules table, ratio bars, a changelog
  timeline, glossary hovers and a byte playground were left out (round 107); each answers a reader's question faster
  (find NINO007, see a ratio, scan a release, decode a word, see the bytes) and none duplicates the text.
- **Every byte the site shows is checked against what Nino writes**: nino.docs' `bun run check:bytes` finds each
  `<Bytes>` row and each byte-playground row in a hex dump of `docs/spec/wire-format.md` or in
  `conformance/corpus/valid/*.bin`; a figure derived from the rules carries `derived="..."` and is listed. Run it in
  the docs pass of any wire-format change (`wire-format` lists the docs). Evidence: round 107's first run found four
  figure rows, in each language, derived from the rules (an evolving-data v1/v2 body, a struct-layouts tuple,
  polymorphism's sealed Circle); every byte-playground row is one of the spec's Appendix A dumps.

- **One owner per explanation**: other pages say it in a sentence and link. README, the site, `docs/` and the public
  skill do not repeat each other's long sections; README summarizes and links the site. Within `docs/`, the page that
  explains a pitfall owns its example: the `Vector<T>` fixed-lane class was copied whole into diagnostics.md, layouts.md
  and the public skill; pitfalls.md keeps it, the others a sentence and a link, and the public skill, installed without
  the repository, one compact form of it and a link to the site. A list of per-host facts has one home too: which host
  has which `Vector<T>` width is only in the spec's §3; every other page says "16, 32 or 64 bytes, chosen by the
  CPU" and links it (round 105 found three different lists, one of them "16 or 32"), and the measured BCL layouts are
  only in docs/layouts.md and the site's struct-layouts page.
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
  `present` and `presentAt` when rc.7 had only `notNull` (round 103 final pass). A trailer that landed wrong is corrected
  with a git note in `refs/notes/release`, never by rewriting `main` (the owner's choice, round 104): the note
  replaces that commit's highlight trailers in the release body (`release_notes.py`), and the site's changelog and
  migration table get the same fix.
  The same holds for speed: a runtime or API new in the release gets no
  "N% faster" entries against `main` before it; fold what it achieves into the entry that introduces it, against
  competitors only through the synced data (rc.8's 14 Go and Rust Performance entries became two sentences in the
  runtimes' entry, round 102).
- **Chinese terms**: one per concept, from the glossary in nino.docs' `WRITING.md` (Tone: Chinese); grep the
  variants it lists across both repositories (README.zh.md too) in every pass. After replacing a term, grep for the
  spacing it leaves (a space before the CJK word, none after inline code: `的 存在字节`, `` `bool`或 ``; round 103
  found four), and read each hit of a variant before replacing it: 原地反序列化 meant reuse deserialization in
  README.zh.md but "read from the segment without a copy" on the streams and ASP.NET Core pages.
- **Renaming, merging or removing a site page**: the old URL and its `.md` copy get a 301 in `public/_redirects` (no
  `#fragment` in the target, so the browser keeps the reader's; renamed anchors get an `<AnchorRedirect>` on the
  target page, as v3-vs-v4's `#runtime` did in round 107) and an entry in
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
