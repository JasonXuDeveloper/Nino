# Information architecture: the sidebar, one home per fact, moving pages

Part of the `audit-docs` skill. The information-architecture checklist with the round that set it, and what moving,
renaming or merging a site page must update. Read it in every docs pass that adds, moves, merges or splits a page or a
section, reorders the sidebar, or renames an anchor. The checklist itself is nino.docs `WRITING.md`, "Information
architecture".

## The checklist

- **Run the information-architecture checklist in every docs pass** (nino.docs `WRITING.md`, "Information
  architecture": one home per fact, the sidebar as the reader's path with sections of about 3 to 7 and folders for
  bigger clusters, short-noun titles that match their links, common before rare within a page, a lead and next steps
  on every page, length limits, redirects for moved pages and anchors, no orphan pages or duplicate examples, the
  newcomer path from the home page, titles and descriptions that read alone in search and llms.txt, 390px width).
  Why: pages are added one feature at a time, and nobody sees the whole until the sidebar is too long to scan (the
  owner, round 107: "the left sidebar has a lot in it; is its order right?"). Evidence: round 107 found an Overview
  section of six pages before Quick Start and a Reference section of eleven mixing specs with benchmarks, and rebuilt
  the sidebar into seven sections of 3 to 7 with Guides collapsed; it merged v3-vs-v4 into whats-new (`#features`,
  redirects and an AnchorRedirect for `#runtime`), moved best practices' copy of the zero-allocation recipes to
  `zero-allocation#message-loop`, cut format versions' and limitations' repeats of whats-new, moved the glossary out
  of basic into its own page, and gave supported types and untrusted data next steps (both were dead ends).
- **Check the mechanical items with a script, both languages**: a page whose last `##` is not "Next Steps" /
  "Related" (下一步 / 相关页面) is a dead end, and a `<Card title>` that differs from its target page's `title` breaks
  "titles match their links". Round 129 found 23 dead ends in each language (eleven guides, the three benchmark pages,
  vs MemoryPack, limitations, analyzers, platforms, wire format, SIMD, format versions, migration, the FAQ) and five
  card titles that were not their page's ("Benchmarks",
  "Advanced Guides", "Constructors"); every one got cards on the sidebar path. Changelog, glossary and the home page
  are exempt (a timeline, a list drawn from data, a landing page).

## Renaming, merging or removing a site page

- **The old URL and its `.md` copy get a 301 in `public/_redirects`** (no `#fragment` in the target, so the browser
  keeps the reader's; renamed anchors get an `<AnchorRedirect>` on the target page, as v3-vs-v4's `#runtime` did in
  round 107) and an entry in `public/_routes.json`'s `exclude` (paths a function handles never reach the redirects).
- **`git mv` its `scripts/csharp-check/Context/<slug>.cs` and change its `namespace Docs.Page_<slug>`** (the check
  reports the page's types as missing otherwise).
- **Update links in v2/v3 pages that point at v4 and in Nino's README.md/README.zh.md** (perf -> benchmarks, round
  101; perf-runtimes and perf-unity -> benchmarks-runtimes and benchmarks-unity, round 102).
- **Grep `src/` and `scripts/` for the slug**: page URLs built in code (`bench-data.tsx` builds the Unity page's link)
  are not caught by the link checker.
