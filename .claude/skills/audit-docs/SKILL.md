---
name: audit-docs
description: "Audit and write all of Nino's docs - the nino.docs site (English and Chinese pages, changelog, benchmark data), README.md and README.zh.md, docs/ in this repository, CONTRIBUTING.md, the runtimes' READMEs and the public skill skills/nino-serialization - so that a reader finds each answer fast and every feature is documented in order with its recommended form, its fast form, its costs and pitfalls, every statement matches the code, nothing is said twice, and English and Chinese agree. Use once the code is final (the docs pass), after a user-facing change lands, and before a release. Triggers: docs, README, *.mdx, nino.docs, changelog, en/zh, llms.txt, the public skill, sidebar, page order, overlap, reader experience, \"docs out of date\", 文档, 网站, 中文文档, 更新日志, 例子, 文档同步, 写得不对, 侧边栏, 页面顺序, 阅读体验, 重叠."
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
  Measure the walk, do not estimate it: a throwaway Playwright script (scratchpad, Chromium from `CHROMIUM_PATH`,
  `build/client` served with `/en` mapped to `en.html`) that opens each step at 1280x800 and 390x844 and prints the
  page's height in screens and the screen the answer's text is on gives the before/after numbers; add one step per
  feature of the release under audit (round 129: the Rust hand-written macros were on screen 36 of 52 of the codegen
  page under no heading of their own, and Quick Start put four screens of per-platform install detail before the
  first serialization).

## 1. Inventory: every feature, in order

Build the list from the source, not from the pages:

- every public attribute (`src/Nino.Core/Attributes`), its options and where it may go;
- every public entry point (`NinoSerializer`, `NinoDeserializer`, their overloads: arrays, spans, `IBufferWriter`,
  streams, exact-size and into-existing forms), `NinoSettings` member, pool and registry API;
- every MSBuild property and generator option users set, every NINO diagnostic and code fix;
- every built-in type and collection (BCL, immutable, concurrent, Unity types), each package (`Nino.AspNetCore`,
  `Nino.Immutable`, `Nino.UnityTypes`, Unity, `Nino.Codegen.Tool`) and each runtime's public API;
- every nino-gen option (`src/Nino.Codegen/Options.cs`) and what each language supports.

Write the result as a table, API -> owning page#anchor, or "missing" / "only in passing", in the round's report.
Collect the public types with `grep -rnE "^\s*(public|protected) .*(class|struct|interface|enum)" src/Nino.Core
src/Nino.Immutable src/Nino.AspNetCore src/Nino.UnityTypes` and their public members, minus `[EditorBrowsable(Never)]`,
`Nino.Core.CompilerServices` and members documented as for generated code; every `CompilerVisibleProperty` and
`$(Nino*)` in `src/*/build/*.props|targets`. Every `NinoSettings` member and constant has a row on the settings page
(`/en/docs/v4/settings`: type, default, range, frozen on first use or not), which owns the list, and its guide
(security, pooling) links it. A public type with no page is documented on the page that owns its topic, or reported
to the owner as one to hide (whether it stays public is the owner's call). Why: round 130, the owner found no page
listing NinoSettings; the inventory also found `DefaultMaxDepth`, `DefaultMaxHashCollisions`, `NinoInputFormatter`,
`NinoOutputFormatter`, `NinoKernels`, `CachedFormatter<T>`, the built-in formatter structs, `Reader.ReadBlock`,
`Writer.Reserve` and two `IsRegistered` properties public but undocumented; the owner kept all of them public, and
they now live on simd#registering-kernels, custom-formatters#built-in-formatters and #raw-bytes, and
platforms#checking-registration.

Then the reading order: the sidebar (`content/docs/v4/meta.json`) is the reader's path, three learning tracks first
(Basics: Get Started, Define Types, Before You Ship; Performance; Advanced Features), then Guides (the collapsed
`advanced/` folder), Compare, Reference, Under the Hood and Project (nino.docs `WRITING.md`, Order). Walk each track by
its first Next Steps card from Quick Start: it must reach Guides without a dead end or a page twice (round 130: 16
pages, Quick Start to Streams and Compression). Every inventory item has one owning page and a place in that order; an item without one
is a finding, and so is a page that teaches a feature before the one it builds on.

## 2. Per page (English and Chinese)

1. **Anatomy** (`WRITING.md`, in this order): what it is; when to use it and when not (with what to use instead); how
   to use it, **the recommended form first** (an attribute, a generated path, the simplest overload) with a complete
   example that compiles; a full example where a short program can show the feature alone; **how to use it fast** (the
   overloads and settings that change the cost: exact-size buffers, `IBufferWriter`, reusing instances, pooling,
   `[NinoUtf8]`, raw structs, block copies, untrusted mode's cost), each with its benchmark through a component;
   what it costs (bytes on the wire, time, allocations, what is free when unused); pitfalls (Mistake | What happens |
   Caught by: the NINO rule, the exception); related pages. Check the anatomy by grepping, not by memory: round 129
   found a "Mistake | What happens | Caught by" table on two pages of 46 (Unity, build configuration), none for the
   rules a release added (NINO030-NINO033), and added them where those rules' features live (defining types, evolving
   data, untrusted data). A benchmark class Nino adds is described on the benchmarks page and linked from its
   feature's cost section: `UntrustedTest` had synced rows from rc.8 on and no page said what it measured.
   **The newest release's API is the main text; older releases get a Callout.** When a release adds an API that
   replaces hand-written code (a built-in overload, an attribute, a command), rewrite every example and sentence to
   the new API in the same docs pass, and put how earlier releases do it ("On rc.9 and earlier ...") in one short
   Callout after it, with the old helper in an Accordion if it is long; never keep the old helper as the main code
   with a note that the new API exists, and never show a built-in call and a hand-written replacement for the same
   job on one page. Code a page still has to write itself (frames for several messages on one stream, compression)
   says in its first sentence why no built-in call does it. Grep the release's changelog entries' API names across
   the site and README to find every page to rewrite. Why: the owner (rc.10: "一会是我们的stream api，一会是自己封装的
   ... 应该把代码改成最新的，info里说老版本怎么做"); the streams, networking and caching pages kept rc.9's
   ReadOnlySequence and Stream helpers as the main code with an rc.10 note beside them.
   **State what a release ships without its history.** Outside the changelog and the migration guide, a page never
   says which release candidate added a feature ("since 4.0.0-rc.6", "measured since rc.8", "needs rc.6 or later"),
   and a comparison never lists under a competitor's leads what Nino has built in; what earlier releases lack goes
   in an info Callout at most. Grep `rc\.[0-9]` over the v4 pages in every docs pass. Why: the owner (rc.10:
   "正式发布时支持的东西就直接写就行，rc里的限制最多就是加个info"); vs-memorypack's "Where MemoryPack Is Ahead" listed
   the built-in types Nino has "since 4.0.0-rc.5", and nine pages carried such dates (nino.docs 7f3f0cc).
2. **True to the code**: every API, attribute, option, default, limit and exception named exists today and behaves as
   written; check a doubtful claim with a throwaway test. Names match the code exactly. No API that is not in the
   source (the site's rule).
   A claim about the code's whole surface ("the reader is safe code", "the only `unsafe` is the SIMD module") is
   re-checked whenever that surface grows: grep the runtime READMEs, crate docs, the codegen pages and the changelog
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
   Read the XML doc of every setting and attribute a page describes against the page: round 129 found the security
   page leaving `ConcurrentDictionary` out of the tables `Untrusted` does not seed, which `NinoSettings.Untrusted`'s
   doc and `NinoSeededHash`'s three callers state.
3. **Examples compile and are exemplary**: every C# fence compiles against the current source: run nino.docs'
   `bun run check:csharp` (compiles every v4 fence against a Nino checkout, `NINO_DIR`, default `../Nino`; a titled
   fence as a whole file with its own `using` lines, an ` excerpt` as a fragment; nino.docs `WRITING.md`, Code), no NINO warning,
   models a real domain (game state, messages, saves), and uses the recommended form; byte examples match what
   Nino writes now (`wire-format`). Name the page's own helpers without the `Nino` prefix, which is the library's:
   a reader cannot tell `NinoStream.Read<T>` from an API (adapters named after the format, `NinoKafkaSerializer`,
   stay); grep the site for the new name first (round 103: `FrameStream` was already another page's class, so the
   streams helper became `StreamFrames`).
   **Compiling is not correct: run the examples that read, frame, store or recover data.** Copy a page's titled files
   into a scratch console against the checkout and feed them a crash, a hostile input and a second message, because
   the review of every v4 example (nino.docs `fc154d2`, `9de4434`) found 20 that compiled and were wrong: a Brotli
   frame without its compressed length (frames could not follow one another), an event log that appended after a
   torn record (the next replay read across it), lengths from the data rented before the data arrived (8 bytes rented
   64 MB), a custom formatter's capacity allocated unchecked (9 bytes threw OutOfMemoryException), one bad UDP datagram
   or Kafka message ending its receive loop, two caches sharing a key, `TryRegister` of an abstract type, and a stated
   struct layout the generator does not produce. Check for each: is every frame self-delimiting, is every length read
   from data bounded before anything is allocated for it, does one bad message end only itself, and does the prose
   (a byte count, a layout, a version) match what running it prints.
   **Measure what a hot-path example allocates** (`GC.GetAllocatedBytesForCurrentThread` around a warmed loop): a page
   that teaches Nino's zero-allocation form must not wrap it in code that allocates per message. Use one-shot static
   APIs over instances that own native handles (`BrotliDecoder.TryDecompress` into a pooled buffer, not
   `new BrotliDecoder()` per frame: nino.docs `13aa288`, 0 B per round trip); name the span or buffer overload a
   client offers before calling `byte[]` its cost (Godot's `SendBytes(ReadOnlySpan<byte>)`, `IBufferDistributedCache`);
   pass state to a library's callback instead of a capturing lambda (HybridCache's `GetOrCreateAsync(key, state, ...)`);
   decode once per message and into a reused instance by `ref`; return a library's `ValueTask` instead of wrapping it
   in `async`.
4. **Numbers**: never typed. Runtime numbers only through `BenchValue`, `RuntimeTable`, `ResultsTable`,
   `BenchmarkChart`, `RuntimesTables`, `UnityTables` from the synced data; within 5 % is on par and is written so; the
   docs never call Nino "slower"; v3 means 3.3.13.
5. **Form: the most efficient one, prose last** ([references/forms.md](references/forms.md)): rewrite every section
   a table, a matrix, a byte figure, a Mermaid diagram, `<Steps>`, code-block tabs, a `TypeTable` or a `Files` tree
   shows faster, and keep prose for why, trade-offs and caveats; re-read Fumadocs' components each pass; colour only
   in byte figures and matrices; diagrams are small Mermaid drawn at build time; byte figures from the spec's worked
   examples. Read it before changing a section's form, adding a diagram or byte figure, or upgrading Fumadocs.
6. **Chinese**: the same content and structure as English, written natively (`WRITING.md`, Tone: Chinese), headings
   with the English id (`## 键控成员 [#keyed-members]`), links with the same anchors. Read every Chinese paragraph
   next to its English one and rewrite what reads as a translation, by the marks `WRITING.md` lists (stacked 的
   modifiers, 被, 该/其/使得 padding, word-for-word headings); keep sentences that already read well. Chinese written from an English draft (a new page, a changed paragraph,
   README.zh.md, UI strings) always gets a second pass before it lands: re-read the Chinese alone, not beside the
   English, as a Chinese developer would, and rewrite what still reads as translated (owner: "翻译后的文本也需要润色"). Why: round 130,
   the owner found the site, README.zh.md and the landing copy "太机翻了"; five agents rewrote about 50 pages, the UI
   strings, glossary, analyzer rules and README.zh.md with no fact changed. Cover the strings outside the pages too
   (`app/routes/home.tsx`, `src/components`, `src/lib/site.ts`, `scripts/generate-static.ts`' llms.txt and social
   cards, `src/data/*.json`), and keep each term the same in all of them.

## 3. Across pages and repositories

- **Run the information-architecture checklist in every docs pass** ([references/ia.md](references/ia.md)): one home
  per fact, the sidebar as the reader's path in sections of about 3 to 7, a lead and next steps on every page,
  redirects for moved pages and anchors, no orphans (nino.docs `WRITING.md`, "Information architecture"). Read it in
  any pass that adds, moves, merges or splits a page or section, or reorders the sidebar.
- **Interactive components serve finding and comparing, from one data source, and read the same without
  JavaScript; every byte the site shows is checked against what Nino writes** ([references/forms.md](references/forms.md),
  "Interactive components and data"): a NINO rule added or changed is a row of nino.docs'
  `src/data/analyzer-rules.json` in the same docs pass; run `bun run check:bytes` in the docs pass of any wire-format
  change.

- **One owner per explanation**: other pages say it in a sentence and link. README, the site, `docs/` and the public
  skill do not repeat each other's long sections; README summarizes and links the site. Within `docs/`, the page that
  explains a pitfall owns its example: the `Vector<T>` fixed-lane class was copied whole into diagnostics.md, layouts.md
  and the public skill; pitfalls.md keeps it, the others a sentence and a link, and the public skill, installed without
  the repository, one compact form of it and a link to the site. A list of per-host facts has one home too: which host
  has which `Vector<T>` width is only in the spec's §3; every other page says "16, 32 or 64 bytes, chosen by the
  CPU" and links it (round 105 found three different lists, one of them "16 or 32"), and the measured BCL layouts are
  only in docs/layouts.md and the site's struct-layouts page.
  For code generation: the site's codegen pages own the user guide: the overview `advanced/codegen` (setup, the
  command line, options and their naming rules, diagnostics, the cross-language type summary, raw struct fields,
  generics, reference tracking, `[NinoType]` collections, the checks every reader makes, what no language gets) and
  one page per language, `advanced/typescript`, `advanced/go` and `advanced/rust` (its usage and generated API, type
  mapping, its forms of generics and tracked classes, its own untrusted-data checks, what it leaves out, its
  benchmark); a fact of one language lives on its page, and the overview says it in a sentence and links (round 129
  split the 8,850-word page this way); each `runtimes/*/README.md`
  owns its install, API reference and implementation notes and links the guide; `docs/codegen.md` is for
  contributors (pipeline, why each target writes what it does, adding a type, regenerating, tests). Before cutting a
  copy, move every fact only it has to its owner (round 102 moved the camel/snake rules, Go slot marker methods and
  Rust `RawNullables` to the site), keep the cut section's anchor and point the inbound links at the owner's anchor,
  so readers do not land on the summary (round 103: the platforms page's binary format and SIMD sections became
  summaries; about 30 links moved to `wire-format#strings` and `simd#dotnet`).
- **Keep README a storefront in reading order, short and plain**: hero (tagline, the platforms with Native AOT
  named, two badge rows, nav links), "Why Nino" as 5 to 8 claims of a line or two (SIMD on every platform among them), each with its link (the first
  names MemoryPack, MessagePack and protobuf-net: not every reader knows MemoryPack), Install, a short Quick start, an
  "AI coding assistants" section of its own (the skill's install command, what it teaches, llms.txt; owner: not a
  sub-item of Install), Benchmarks (the speed table against MemoryPack, MessagePack and protobuf-net, then the chart: a light and a
  dark PNG in a `<picture>` with `prefers-color-scheme`, both written by `readme.py` and rendered by `report.yml`),
  a compact "Built for" list, the MemoryPack feature table (no speed rows: the Benchmarks table has them), v4 vs v3,
  Contributing, License; no callout or long list before Install, README.zh.md the same sections in the same order,
  and links to the site for the rest. No emoji anywhere in either README, in headings, links, tables or lists (owner,
  round 130: "AI 味道有点重，emoji 用的也不统一"; ✅ in tables became words). Why: a reader decides in the first two
  screens (round 129, owner: "README 有点乱"; Install sat at 4,300 px of 9,200, now at 850 of 4,800; round 130 cut
  the English README from 1,240 to 960 words). Badges: every badge `style=for-the-badge` with `logoColor=white`
  (owner, round 130: the flat-square second row looked inconsistent): a row of packages (NuGet, OpenUPM, crates.io,
  npm, Go), each in its platform's colours and logo (`labelColor`, `color`), then a row of build, license, docs and
  DeepWiki on a dark `labelColor=24292F`. Every package badge shows the newest version, prereleases included
  (`nuget/vpre`, npm's `next` tag); Go has no registry, so its badge links pkg.go.dev. Every speed number in README is
  a `<!--bench:...-->` marker `benchmarks/reports/readme.py` fills from the release run (`ser.range`, `deser.range`,
  `msgpack.*`, `protobuf.*`, `reuse.inplace`, `v3.*`); a new number gets a key there, never a hand-typed value. Never
  start a line of a paragraph or list item with a marker: a line starting `<!--` opens an HTML block, and the rest
  of the item loses its bold and links (README.zh.md's first bullet, round 129). GitHub renders only pushed content,
  so render the branch locally (GitHub's own renderer: `gh api markdown` with `mode: markdown`, not `gfm`, which
  turns every line break into `<br>`; github-markdown-css at 838 px; screenshot with headless Chrome) and check the
  markers by running `benchmarks/reports/readme.py`'s `fill` on a copy with the newest release's report (its
  `benchmark-x64-full.md` and `benchmark-v3-full.md` assets): every key found, no value changed.
- **Copies that must exist agree**: README / README.zh.md, the site's index and comparison pages, the analyzers page
  and `docs/diagnostics.md`, `docs/spec/wire-format.md` and the site's wire-format page, the public skill, the
  runtimes' READMEs and the codegen pages.
- **Every feature is everywhere it belongs** (`feature`, part 8): its guide page (both languages), the analyzers page
  for its rules, the platforms page if it is platform-specific, the public skill, README's feature list when users
  choose Nino for it, the changelog.
- **Changelog** ([references/changelog.md](references/changelog.md)): one section per v4 release, newest first,
  user-facing items only, "Wire Format" first, entries of one area together; it describes what changed between
  releases, not within one (check the old name shipped: `git grep -n <old> <last tag>`); bring it in line with Nino at
  the start of any docs work; a wrong trailer is fixed by a git note. Read it before writing or checking a changelog
  entry, a migration row or a release-notes trailer.
- **The public skill** (`skills/nino-serialization/SKILL.md`) holds what users' agents need to write correct, fast Nino code: the
  recommended forms, the fast forms, the pitfalls and the diagnostics, matching the pages.
- Stale names: grep the old names of every rename across both repositories; history pages (changelog, migration,
  v3-vs-v4) keep them on purpose.
- **Chinese terms**: one per concept, from the glossary in nino.docs' `WRITING.md` (Tone: Chinese); grep the
  variants it lists across both repositories (README.zh.md too) in every pass. After replacing a term, grep for the
  spacing it leaves (a space before the CJK word, none after inline code: `的 存在字节`, `` `bool`或 ``; round 103
  found four), and read each hit of a variant before replacing it: 原地反序列化 meant reuse deserialization in
  README.zh.md but "read from the segment without a copy" on the streams and ASP.NET Core pages.
- **Renaming, merging or removing a site page** ([references/ia.md](references/ia.md), "Renaming, merging or
  removing a site page"): a 301 for the old URL and its `.md` copy, `_routes.json`, the csharp-check Context file,
  links from v2/v3 pages and Nino's READMEs, and a grep of `src/` and `scripts/` for the slug. A removed URL with no
  page to go to answers 404, never a redirect to a 404 or a 200 not-found page (nino.docs AGENTS.md, Redirects: no
  soft 404s, which Google Search Console reports).

- The code standard (`audit-code`: DRY, decoupled, clear) applies to these files, and redundancy in them
  (duplicated logic, dead code, two ways to do one job) is part of this audit; the sweep across layers is
  `audit-redundancy`.

## 4. Finish

- nino.docs: `bun run check && bun run build` (links, anchors, icons, types, prerender, llms.txt), and
  `bun run check:csharp` when a C# fence changed; commit docs apart
  from code; push to its `main`.
- This repository: links in README and `docs/` resolve; `tools/agents.sh --check` when maps or skills changed.
- Report per page: what was missing, wrong or duplicated, and what you changed; what you left (with the reason).
