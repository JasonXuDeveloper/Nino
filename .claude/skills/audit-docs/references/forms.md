# Visual forms and components

Part of the `audit-docs` skill. Which form a fact takes on the site (tables, matrices, byte figures, diagrams, steps,
code-block tabs, interactive components), the Fumadocs components to adopt, colour, Mermaid and the byte checks, with
the rounds that set each rule. Read it before rewriting a section's form, adding a diagram, a byte figure or an
interactive component, or upgrading Fumadocs. How each form is written is nino.docs' `WRITING.md`, "Visual forms".

## The form

- **The most efficient form, prose last** (the owner: "docs must be intuitive, not necessarily many words"). Rewrite
  every section a table, a support matrix (`<Matrix>`), a byte figure (`<Bytes>`), a Mermaid diagram, `<Steps>`,
  code-block tabs, a `TypeTable` or a `Files` tree shows faster; keep prose for why, trade-offs and caveats. Readers
  scan, and a paragraph of facts hides the one they look for. Round 105: the security page's list of rejected inputs
  became one default-versus-`Untrusted` matrix, the evolving-data and vs-memorypack ✓/✗ tables matrices, an ASCII
  `length | Id | CustomerId` comment a v1/v2 byte figure.
- **Re-read Fumadocs' component and MDX-plugin list for the site's version in every docs pass** (fumadocs-ui and
  fumadocs-core in its package.json, their `dist/components` and `dist/mdx-plugins`) and adopt what fits a prerendered
  site with Markdown copies: most of the value was installed and unused (round 105, 16.17.3: code-block tabs replaced
  34 `<Tabs>` that only wrapped fences; `remarkSteps` was rejected because it runs after `remarkHeading`, whose ids
  and TOC keep the `[step]` tag). Round 129 re-read 16.17.3 and adopted nothing (`remarkNpm` would serve one inline `npm install`, `InlineTOC`
  repeats the layout's TOC, `remarkBlockId` and `GithubInfo`, a network call at prerender, answer no reader's question);
  round 130 adopted `Tabs` with `id` and `forceMount`, code-block tabs and `Steps` inside a tab, and `Accordions` with
  `id` for the quick start's installation, and rejected TypeTable for packages, a Files tree for three DLLs and Cards as
  a platform picker. Closed accordions and inactive code-block tabs are still not prerendered (their text is in the .md
  copy and the search index); re-read when package.json's version changes.
- **Per-platform instructions are one `<Tabs groupId="platform" persist>` section, each tab self-contained**:
  requirements as a short list, then `<Steps>` (install, build, try it) with bold step leads, not headings (the TOC
  stays short); several install methods in code-block tabs; rarely needed detail in `<Accordions>` inside the tab.
  Give each `<Tab>` `forceMount` (Radix leaves an inactive tab out of the prerendered HTML without it) and an `id`, each
  `<Accordion>` an `id` and a `value`: a link to the id selects the tab or opens the accordion, so old headings' anchors
  survive, and `lint-links` checks them. Why: the owner found the quick start's installation "too messy" (round 130
  folded a tab block and a long "Installation Details" section into one tabbed section; nino.docs WRITING.md, Visual
  forms, carries the same rule for page authors).
- **Colour only in byte figures and matrices**: the five calm category tints (`--nino-cat-1` to `-5`, light and dark,
  fixed meanings) are allowed there and nowhere else, where the single accent stays (the owner's decision, round 105);
  a page's meaning never depends on colour alone (the Markdown copy has none).
- **Diagrams are Mermaid, drawn at build time** (EasyEcs docs' way: `rehypeMermaid` in Playwright's Chromium, cached
  per page by sha256 of Mermaid's version, the options and the diagrams, in memory and in `node_modules/.cache/mermaid`;
  the page loads no Mermaid, the Markdown copy keeps the source): small `LR` flowcharts of rounded nodes, never taller
  than about 420px at desktop width (a decision with a few outcomes is a table: round 105's diamond trees were taller
  than a screen), Chinese labels on Chinese pages, never hand-drawn SVG. The cache is what keeps local builds fast;
  Cloudflare Pages keeps no `node_modules/.cache` for a React Router site, so its builds draw every diagram (measured in
  round 105: nino.docs AGENTS.md, Build & Deploy).
- **Byte figures come from the specification's dumps** (Appendix A and the sections' examples, dumps of a real run),
  never from memory or worked out from the rules: a figure the spec has no dump for gets one in the spec first.

## Interactive components and data

- **Interactive components serve finding and comparing, from one data source, and read the same without JavaScript**
  (nino.docs `WRITING.md`, Visual forms: `AnalyzerRules`, the benchmark tables' ratio bars, `Release`,
  `Term`/`Glossary`, `BytePlayground`; when to use each). Each prerenders its full content and has a Markdown form from
  the same JSON. A NINO rule added or changed in Nino is a row of nino.docs' `src/data/analyzer-rules.json` (English
  and Chinese) in the same docs pass, as it is a section of `docs/diagnostics.md`; a new glossary term goes into
  `src/data/glossary.json`. Why: the owner asked why a filterable rules table, ratio bars, a changelog timeline,
  glossary hovers and a byte playground were left out (round 107); each answers a reader's question faster (find
  NINO007, see a ratio, scan a release, decode a word, see the bytes) and none duplicates the text.
- **Every byte the site shows is checked against what Nino writes**: nino.docs' `bun run check:bytes` finds each
  `<Bytes>` row and each byte-playground row in a hex dump of `docs/spec/wire-format.md` or in
  `tests/conformance/corpus/valid/*.bin` and fails on any other. Run it in the docs pass of any wire-format change
  (`wire-format` lists the docs). Evidence: round 107's first run found four figure rows, in each language, worked
  out from the rules (an evolving-data v1/v2 body, a struct-layouts tuple, polymorphism's sealed Circle) and let
  through as `derived`; round 120 serialized each with Nino (all four were right), put the missing dumps into the
  spec's A.2 and A.3, taught the check to read `\|` in the spec's tables, and removed the escape.
