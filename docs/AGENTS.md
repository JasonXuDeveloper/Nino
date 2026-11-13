# Docs: file map

> Read when: changing the Markdown documentation in docs/ (the wire format spec, the design, codegen, diagnostics, pitfalls, benchmarks), or deciding where a piece of documentation belongs.

The repository's own Markdown documentation, read on GitHub and by contributors. The website
(https://nino.xgamedev.net, Fumadocs, English and Chinese) lives in the separate repository
`JasonXuDeveloper/Nino.Docs` (local clone `/workspace/nino.docs`, with its own CLAUDE.md); it takes its facts from
this repository and its numbers from the release's benchmark assets. `audit-docs` audits this directory and the site.

```
spec/
  README.md                  what the specification covers, its sources and its versioning (spec version per Nino
                             release; a change table)
  wire-format.md             the byte-by-byte specification of what Nino v4 writes and reads, for implementations in
                             other languages: primitives, strings, raw layouts, collections, [NinoType] objects,
                             the [NinoTypeLayout] grammar (§8), reader validation (§9), worked examples
v4-design.md                 the v4 architecture: goals, target frameworks and the contents of its parts (every
                             section, so a reference like "Deep nesting in docs/v4-design.md" lands)
v4-design/
  binary-format.md           the format's design and its reasons: strings, objects, collections, version tolerance,
                             keyed members, reference tracking, deep nesting (the bytes themselves are spec/)
  runtime.md                 Nino.Core: writer, reader, formatters and their registration, polymorphism, concurrency,
                             reuse and pooling, callbacks, settings, untrusted input
  generator.md               Nino.Generator: pipeline, interceptors, the API generated code calls, shared sources,
                             source layout, analyzer and code fixes, generated code per type
codegen.md                   nino-gen for contributors: where it lives, the pipeline (layouts, IR, targets, templates),
                             why each target writes what it does, adding a type, regenerating, tests; usage is the
                             site's codegen pages
diagnostics.md               every NINO diagnostic with its fix (the analyzer's help links point here), and nino-gen's
                             NINOGEN ones
pitfalls.md                  problems met building v4 (memory layouts across runtimes, the generator, benchmarking)
                             and what Nino does about them; read before changing the wire format or raw fast paths
layouts.md                   the layout of every unmanaged BCL struct on every runtime CI runs (size, field offsets),
                             against the canonical one, from tests/Nino.RuntimeTests' `# layout` lines
v4-benchmarks.md             build-time and runtime numbers against v3 3.3.13 and MemoryPack; the build and runtime
                             tables are written by benchmarks/reports/readme.py from a release's report
assets/                      logo.png (README); benchmark-<group>.svg and benchmark-<group>-dark.svg (the charts of
                             README and v4-benchmarks.md, one per group of cases, written by readme.py); benchmark.svg
                             and benchmark.png (the overview chart, the image for nuget.org, which shows no SVG; the
                             PNG rendered from it by report.yml, the first chart's fallback image)
```

## Rules for these files

- A change to the bytes Nino writes updates `spec/wire-format.md`, bumps its version and adds a row to the table in
  `spec/README.md`, in the commit with the `Wire-Format:` trailer. A check every reader must make goes into §9.
- A new diagnostic gets a `### NINOxxx` section in `diagnostics.md` (its id is the help link's anchor).
- Never type benchmark numbers into `v4-benchmarks.md`'s build or runtime tables or its charts: `benchmarks/reports/readme.py` writes
  them from a full run (benchmarks/AGENTS.md says which runs count).
- The website's pages are written from these files and the source (Nino.Docs' CLAUDE.md): a change users see here
  needs the site, and its changelog, brought in line too.
