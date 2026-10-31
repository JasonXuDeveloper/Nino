# Docs: file map

> Read when: changing the Markdown documentation in docs/ (the wire format spec, the design, codegen, diagnostics, pitfalls, benchmarks), or deciding where a piece of documentation belongs.

The repository's own Markdown documentation, read on GitHub and by contributors. The website
(https://nino.xgamedev.net, Fumadocs, English and Chinese) lives in the separate repository
`JasonXuDeveloper/Nino.Docs` (local clone `/workspace/nino.docs`, with its own CLAUDE.md); it takes its facts from
this repository and its numbers from the release body. `audit-docs` audits this directory and the site.

```
spec/
  README.md                  what the specification covers, its sources and its versioning (spec version per Nino
                             release; a change table)
  wire-format.md             the byte-by-byte specification of what Nino v4 writes and reads, for implementations in
                             other languages: primitives, strings, raw layouts, collections, [NinoType] objects,
                             the [NinoTypeLayout] grammar (§8), reader validation (§9), worked examples
v4-design.md                 the v4 architecture: goals, target frameworks, binary format, Nino.Core, the generator
codegen.md                   nino-gen for contributors: where it lives, the pipeline (layouts, IR, targets, templates),
                             why each target writes what it does, adding a type, regenerating, tests; usage is the
                             site's codegen page
diagnostics.md               every NINO diagnostic with its fix (the analyzer's help links point here), and nino-gen's
                             NINOGEN ones
pitfalls.md                  problems met building v4 (memory layouts across runtimes, the generator, benchmarking)
                             and what Nino does about them; read before changing the wire format or raw fast paths
v4-benchmarks.md             build-time and runtime numbers against v3 3.3.13 and MemoryPack; the runtime tables are
                             written by benchmarks/readme.py from a release's report
assets/                      logo.png (README); benchmark.svg (the README chart, written by readme.py) and
                             benchmark.png (rendered from it by report.yml: nuget.org shows no SVG)
```

## Rules for these files

- A change to the bytes Nino writes updates `spec/wire-format.md`, bumps its version and adds a row to the table in
  `spec/README.md`, in the commit with the `Wire-Format:` trailer. A check every reader must make goes into §9.
- A new diagnostic gets a `### NINOxxx` section in `diagnostics.md` (its id is the help link's anchor).
- Never type benchmark numbers into `v4-benchmarks.md`'s runtime tables or the chart: `benchmarks/readme.py` writes
  them from a full run (benchmarks/AGENTS.md says which runs count).
- The website's pages are written from these files and the source (Nino.Docs' CLAUDE.md): a change users see here
  needs the site, and its changelog, brought in line too.
