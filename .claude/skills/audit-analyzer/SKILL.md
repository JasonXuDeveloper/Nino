---
name: audit-analyzer
description: "Audit Nino's diagnostics (NINO rules in src/Nino.Generator/Analysis), the suppressor and the code fixes - missing rules for declarations that cannot work or that cost, false positives, messages that name the member and the fix, one diagnostic per mistake, a code fix wherever the fix is mechanical, tests per rule, the analyzer's build cost, and the docs that list the rules. Use when adding or changing a rule or code fix, when users hit a runtime error a rule could have caught, or when a rule fires wrongly. Triggers: NinoAnalyzer, NinoDiagnostics, NinoSuppressor, NinoCodeFixProvider, NINO0xx, NINOGEN, AnalyzerReleases, AnalyzerTests, CodeFixTests, docs/diagnostics.md, false positive, warning, 分析器, 诊断, 误报, 代码修复."
metadata:
  internal: true
---

# Analyzer audit

Files: `src/Nino.Generator/Analysis` (`NinoAnalyzer.cs`, `NinoDiagnostics.cs`, `NinoSuppressor.cs`),
`src/Nino.Generator.CodeFixes/NinoCodeFixProvider.cs`, `AnalyzerReleases.Shipped.md` / `Unshipped.md`; the list users
read is `docs/diagnostics.md` and the site's analyzers page. nino-gen's NINOGEN rules belong to `audit-runtimes`.

## Coverage

- Every declaration the generator cannot serialize gets a rule (Error when it cannot work, Warning when it works but
  costs or is likely wrong), so a user never meets "no formatter" at run time for something the compiler could see.
- Every pattern that works but costs (a member that defeats raw copying, a format that adds bytes nobody needs, a
  pooled type without its hooks) gets a Warning that names the cheaper form.
- Mistakes from the FAQ and the pitfalls pages (`docs/pitfalls.md`, the site's FAQ) that no rule catches are findings.

## Principles

- **The analyzer and the generator share one decision**: what the generator skips is exactly what the analyzer
  reports (the same model rules and `NinoSymbols` helpers); a rule never re-derives it.
- **One mistake, one diagnostic**: when two rules fire on the same code, the one whose fix removes the other's case
  reports it.
- **Messages** name the type or member and what to write instead; placeholders are all filled.
- **A code fix for every rule whose fix is mechanical and always correct**; otherwise the message says what to do.
- **Ids are stable**; a removed id is never reused; new ids are taken at landing (two branches can pick the same).
- **Cost**: rules run on every build and keystroke. `EnableConcurrentExecution`, no analysis of generated code,
  symbols resolved once per compilation, cheapest rejection first (syntax kind and names before the semantic
  model), no walk that grows with the compilation per callback; measure with GeneratorBenchmark's analyze case.
- The suppressor hides only compiler warnings Nino's generated code makes wrong (documented in the analyzers page).

- The code standard (`audit-code`: DRY, decoupled, clear) applies to these files, and redundancy in them
  (duplicated logic, dead code, two ways to do one job) is part of this audit; the sweep across layers is
  `audit-redundancy`.

## Tests: every rule, every edge

`tests/Nino.UnitTests/Generator/AnalyzerTests.cs` and `CodeFixTests.cs`:
- each NINO id appears in a test that expects it and one that expects it not to fire; each code fix has a case per
  form it rewrites, its output compiles and reports nothing, and applying it twice changes nothing;
- edge cases where they apply: generic, nested and partial types, records and structs, inheritance across
  assemblies, properties with and without setters, `init`, constructors and factories, conditional compilation;
- exemplary code (samples in tests, docs fences) reports nothing.

## Done means

Rule and fix with tests, `AnalyzerReleases.Unshipped.md`, `docs/diagnostics.md` and the site's analyzers page in both
languages (docs to-dos for the docs pass), the public skill's diagnostics section, a changelog entry.
