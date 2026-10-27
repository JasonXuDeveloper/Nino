# Nino conformance corpus

One set of inputs, written by .NET Nino, that every Nino runtime (.NET, TypeScript, Go, Rust) is tested against:

- `corpus/valid/`: values .NET writes. A runtime must read each to the expected value and write it back to the
  same bytes.
- `corpus/malicious/`: inputs no writer produces. A runtime must reject each with an error, without crashing, hanging
  or using memory out of proportion to the input.

`Nino.Conformance/` is the .NET program that writes the corpus (its sample types are in `Types.cs`, the cases in
`ValidCases.cs` and `MaliciousCases.cs`) and checks it. It is part of `Nino.slnx`, so the solution build compiles it; run it by path.

## Layout

```
corpus/
  index.json                 {"corpusVersion": 2, "valid": [names], "malicious": [names]}
  valid/<case>.bin           the bytes
  valid/<case>.json          what they are (below)
  malicious/<case>.bin       the bytes
  malicious/<case>.json      what they exercise (below)
  malicious/truncations.json valid cases whose every strict prefix must be rejected (generated, not stored)
```

Every case reads a whole input as one top-level value of the case's `type` (`NinoDeserializer.Deserialize<T>` in
.NET); the input is exactly one value, with nothing after it.

### `valid/<case>.json`

| Field | Meaning |
| --- | --- |
| `case` | the name, also the file name |
| `description` | what the case shows |
| `type` | the type the bytes are read as: its runtime-form name (`NinoTypeId.GetName`: `Namespace.Outer+Inner`, generic arguments in `<>` separated by `,`) |
| `features` | the wire features it exercises (tags, for filtering: `int`, `float`, `string`, `utf8`, `seq`, `map`, `grid`, `nullable`, `raw-struct`, `type-id`, `generic-type-id`, `version-tolerant`, `keyed`, `versioning`, `deferral`, `deferral-section`, `depth-limit`, `track-references`, `nino-collection`, ...) |
| `optional` | present when the case needs a feature a runtime may leave out: `track-references` (reference tracking) |
| `size` | the length of the `.bin` |
| `roundTrip` | whether writing the value read gives the `.bin` back. False only where the bytes are not what .NET writes for that type: data of another version of the type (`*-old-to-new`, `*-v1-to-v2`), or a form .NET reads but does not write (`string-utf16-header-for-latin1`) |
| `reencoded` | when `roundTrip` is false: the bytes (hex) .NET writes for the value read; a runtime must write exactly these |
| `layouts` | the `[NinoTypeLayout]` of every `[NinoType]` the value may contain, by name: the type, its members' types and elements, every subtype of a class or interface written behind a type id, and whatever an `object` member holds. The format is `NinoTypeLayoutAttribute.ToString()` (`src/Nino.Core/Attributes/NinoTypeLayoutAttribute.cs` documents the encodings). A generic type is listed once, as its definition (`Conformance.Box<T>`) |
| `typeIds` | the type id (hex) of each closed generic type in the value, which its definition's layout cannot give (`typeId=0x00000000` there): the FNV-1a hash of its name, as for any type |
| `value` | the value .NET reads from the bytes, as JSON (below) |

### The value as JSON

Lossless: every value is encoded so that no JSON parser rounds or rewrites it.

| .NET value | JSON |
| --- | --- |
| `null` (a class, string, collection or nullable) | `null` |
| `bool` | `true` / `false` |
| integers of any width, enums (their underlying value) | a number when \|n\| <= 2^53, else a decimal string (`"-9223372036854775808"`) |
| `char` | its UTF-16 code unit as a number (lone surrogates included) |
| `Half`, `float`, `double` | the IEEE bits as a hex string of 4, 8 or 16 digits (`"0x3ff0000000000000"`): NaN payloads, signs and `-0.0` are kept |
| `decimal` | `decimal.ToString(InvariantCulture)`, which keeps the scale (`"-1234.5600"`) |
| `Guid` | `"00112233-4455-6677-8899-aabbccddeeff"` (the wire holds .NET's byte order: the first three groups little-endian) |
| `DateTime` | `{"ticks": int, "kind": "Utc" \| "Local" \| "Unspecified"}` |
| `DateTimeOffset` | `{"utcTicks": int, "offsetMinutes": int}` |
| `TimeSpan`, `TimeOnly` | ticks (int) |
| `DateOnly` | its day number (int) |
| `BigInteger` | a decimal string |
| `Version` | `[major, minor, build, revision]` (unset parts are -1) |
| `string` | a JSON string, or `{"utf16": [code units]}` when it holds any surrogate (paired or not) |
| `byte[]`, `sbyte[]` | lowercase hex of the bytes (`"00ff"`; `sbyte[] {-128, -1}` is `"80ff"`). Other byte collections (`List<byte>`) are JSON arrays |
| arrays, lists, sets | a JSON array, in wire order |
| dictionaries | an array of `[key, value]` pairs, in wire order |
| tuples, `KeyValuePair` | an array of the items |
| multi-dimensional arrays | `{"lengths": [..], "items": [.. row-major]}` |
| a `[NinoType]` (class or struct) | an object: `"$type"` (the runtime type's name, as in `layouts`), then its members by C# name, in the order the layout lists them |
| a tracked object (`trackReferences`) | the first time: `"$id": n` before `"$type"`; afterwards `{"$ref": n}`. `n` counts tracked objects from 1 in document order, which is the order they are written, so it is the index the wire's back reference holds |

Collections keep wire order, so a set or a map compares as a sequence. Deep cases nest their JSON as deeply as the
data (up to about 1,000 levels in `deep-*`): raise the parser's depth limit (Rust's `serde_json` stops at 128 by
default).

### `malicious/<case>.json`

| Field | Meaning |
| --- | --- |
| `case`, `type`, `layouts`, `size`, `optional` | as for valid cases |
| `category` | `length`, `string`, `utf8`, `presence`, `type-id`, `depth`, `deferred`, `keyed`, `tolerant`, `duplicate`, `references`, `trailing`, and single cases for `decimal`, `datetime`, `dateonly`, `version` |
| `exercises` | what is wrong with the input |
| `expected` | `error`, or `error-or-value` for deep inline nesting outside any region, which .NET reads by design (as deferred bodies, docs/v4-design.md, "Deep nesting"): a runtime may read it or reject it, but must not use stack in proportion to the depth |
| `dotnet` | what .NET Nino does with it today, in a child process with a 512 MB GC heap limit: `{"outcome": "error", "exception": "InvalidOperationException"}`, `{"outcome": "returned"}` (accepted: a gap in .NET where `expected` is `error`), or `{"outcome": "crashed", "reason": "stack overflow"}` |

`dotnet` records the current behavior, not the requirement: where it is not an error, .NET is known to fall short of
`expected` (see "Known .NET gaps").

### `malicious/truncations.json`

`{"rule", "expected": "error", "prefixCount", "cases": [{"case", "type", "prefixes", "optional"?, "dotnet"}]}`: for
each listed valid case, every strict prefix of `valid/<case>.bin` (lengths 0 to size - 1) read as `type` must be an
error. Derive the inputs; they are not stored. `dotnet` counts the exceptions .NET throws over all prefixes.

## Using it from a runtime

For every valid case (skip the ones whose `optional` feature the runtime leaves out):

1. Decode `<case>.bin` as `type`; it must succeed and consume every byte.
2. Convert the result to the JSON above and compare it with `value` (structurally; object members by name).
3. Encode the result; the bytes must equal `<case>.bin` when `roundTrip` is true, else the hex in `reencoded`.

For every malicious case and every truncated prefix: decoding must fail with the runtime's ordinary error (no panic,
crash, stack overflow or hang), and memory use must stay bounded by a small multiple of the input: several cases
claim gigabytes in a few bytes. Run them with a timeout and, where the language allows, under a memory limit. Where
`expected` is `error-or-value`, a successful decode is accepted too.

A runtime that generates code per type (Go, Rust, TypeScript) generates it from the `layouts`; the type names the
cases use are those of `conformance/Nino.Conformance/Types.cs`.

## Regenerating and checking

```
dotnet run --project conformance/Nino.Conformance -c Release -- --out conformance/corpus
dotnet run --project conformance/Nino.Conformance -c Release -- --verify conformance/corpus
```

`--out` rewrites the whole corpus; it is deterministic, so an unchanged Nino gives identical files. It probes each
malicious input and the truncated prefixes in child processes of itself, so an input that crashes .NET does not stop
it. `--verify` checks a committed corpus against the current Nino: every valid `.bin` reads to its `value`, layouts
and type ids, and is written back as recorded; .NET still writes each case's bytes; and every malicious case and
truncation still gets the recorded `dotnet` outcome. The `conformance` job of `.github/workflows/runtimes.yml` runs
both (`--verify`, then `--out` to a temporary directory compared with `diff -r`), so a change to the wire format, a
layout, or how .NET handles a malicious input fails CI until the corpus is regenerated and committed.

## Versioning

`corpusVersion` in `index.json` changes when the file layout or the JSON schema changes incompatibly; a runtime's
test checks it. Changes to the bytes follow Nino's wire format: the corpus always holds what the Nino of the same
commit writes, and a commit that changes the wire format regenerates it (with a `Wire-Format:` trailer: AGENTS.md and the `wire-format` skill).
Cases are added freely; a case is renamed or removed only with a reason in the commit.

## Known .NET gaps

Recorded in each case's `dotnet` field (`returned` or `crashed` where `expected` is `error`); summarized here:

- `trailing-byte`: bytes after the top-level value are ignored (rejected with `NinoSettings.Untrusted`, which the
  corpus does not set).
- `decimal-bad-scale`, `datetime-ticks-past-max`, `dateonly-day-past-max`: raw values outside the type's range are
  copied as they are (rejected with `NinoSettings.Untrusted`).
