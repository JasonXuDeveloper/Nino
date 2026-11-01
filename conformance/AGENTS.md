# Conformance corpus: file map

> Read when: changing the wire format, a layout, a malicious-input check or a sample type, or making a runtime read the shared conformance corpus.

One set of inputs written by .NET Nino that every runtime (.NET, Go, Rust, TypeScript) is tested against: valid
values to read and write back byte for byte, malicious inputs to reject. README.md is the specification of the files
and of what a runtime must do with them. `audit-security` audits the malicious cases, `audit-runtimes` the runtimes'
use of it.

```
README.md                    the corpus's format (valid / malicious JSON fields, truncations, the value as JSON), how a
                             runtime uses it, regeneration, versioning, known .NET gaps
.gitattributes               *.bin binary (never converted or diffed as text)
corpus/
  index.json                 corpusVersion and the names of every valid and malicious case
  valid/<case>.bin, .json    a value .NET writes, and its type, features, layouts, value and round-trip bytes
  malicious/<case>.bin, .json  an input no writer produces: category, what it exercises, expected (error or
                             error-or-value) and what .NET does today
  malicious/truncations.json   valid cases whose every strict prefix must be rejected (prefixes derived, not stored)
Nino.Conformance/            the net10.0 program that writes (--out) and checks (--verify) the corpus
  Types.cs                   the sample types (namespace Conformance); renaming or reordering changes the corpus
  ValidCases.cs              the valid cases
  MaliciousCases.cs          the malicious cases
  Probe.cs                   runs an input in a child process (512 MB heap limit) and records what .NET did
  Json.cs                    values as the corpus's lossless JSON; the layouts of the sample types
  Program.cs                 --out <dir> / --verify <dir>
```

## Who reads it

- .NET: `Nino.Conformance --verify`; `tests/Nino.Codegen.Tests` references the project to generate the corpus's
  types into every language.
- Go: `runtimes/go/internal/corpustest` over `internal/sample` and `internal/generated`.
- Rust: `runtimes/rust/tests/conformance.rs`, `generated.rs`, `derive.rs` (`NINO_CORPUS`; `tests/conformance/gen.py`
  reads the layouts).
- TypeScript: `runtimes/typescript/test/corpus.test.ts` (`NINO_CORPUS` or `conformance/corpus`).
- CI: the integrations job of `.github/workflows/ci.yml` runs `--verify`, then `--out` to a temporary directory
  compared with `diff -r`, on every .NET change; `.github/workflows/runtimes.yml` runs the runtimes against the
  committed corpus only when it or `runtimes/` change, and sets `NINO_CORPUS`, so a missing corpus fails them.

## Rules for these files

- The corpus is always what the Nino of the same commit writes: after a change to the bytes, a layout, a sample type
  or how .NET handles a malicious input, regenerate it
  (`dotnet run --project conformance/Nino.Conformance -c Release -- --out conformance/corpus`) and commit it with the
  change (a `Wire-Format:` trailer when the bytes change).
- `--out` is deterministic: an unchanged Nino rewrites identical files. Cases are added freely; a case is renamed or
  removed only with a reason in the commit. `corpusVersion` changes only with an incompatible change of the file
  layout or the JSON schema.
- A new malicious category that every runtime must reject goes into `docs/spec/wire-format.md` §9 too.
