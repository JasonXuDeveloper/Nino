# Nino wire format specification

[wire-format.md](wire-format.md) specifies, byte by byte, what Nino v4 writes and reads. It is the contract for
Nino implementations in languages other than C# (Go, Rust, TypeScript, ...). Someone who has only that document
should be able to build a reader and a writer that exchange data with the .NET implementation.

## What it covers

- Conventions: byte order, integers, null markers, lengths.
- Primitives and BCL types (`decimal`, `Guid`, `DateTime`, `DateTimeOffset`, `BigInteger`, `Version`, ...).
- Both string encodings: the default Latin-1/UTF-16 encoding and `[NinoUtf8]`.
- Raw structs and their canonical layout.
- Collections, tuples and nullables, including the fixed-stride nullable arrays.
- `[NinoType]` objects:
  - headers and type ids;
  - member order;
  - the positional, version-tolerant and keyed formats;
  - polymorphism and reference tracking;
  - deferred bodies of deeply nested data.
- The `[assembly: NinoTypeLayout]` metadata, with an EBNF grammar of its text form. This is what a code generator in
  another language reads.
- The checks a reader of untrusted data must make, each with what the .NET reader does today.
- Known irregularities and open points.
- Worked examples, as hex dumps produced by Nino.

It does not cover custom formatters (`formatter(F)` in a layout), whose bytes belong to their authors, or Nino's
APIs, pooling and performance, none of which affect the bytes.

## Sources

Every rule comes from the source of `Nino.Core` and `Nino.Generator` at the commit that last changed the
specification. The tricky cases were checked by serializing values with Nino and reading the bytes. Anything that
could not be checked that way is marked **(unverified)** in the text. Where the source and this specification
disagree, the difference is a bug in one of them: report it.

## Versioning

- The specification's version is `major.minor`, stated at the top of `wire-format.md`.
  - The major version is Nino's major version: this document is `4.x`, and a Nino v5 format starts at `5.0`.
  - The minor version goes up with each change to the bytes within Nino v4, and with each normative clarification
    (a new MUST, or a changed one).
  - Editorial changes keep the version.
- Payloads carry no version number. Writers and readers agree on the format through the Nino version and the type
  definitions (or their published layouts).
- A change to the bytes Nino writes is made in a commit with a `Wire-Format:` trailer, which reaches the release
  notes. Such a commit updates `wire-format.md` and adds a row below.

| Spec | Nino | Change |
| --- | --- | --- |
| 4.0 (draft) | 4.0.0-rc.7 | First version. |
