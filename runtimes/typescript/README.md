# Nino for TypeScript

`src/nino.ts` is the TypeScript runtime of Nino: it reads and writes the bytes Nino writes in .NET (the format is
[docs/spec/wire-format.md](../../docs/spec/wire-format.md)). By default `nino-gen` writes it next to the classes
and codecs it generates for a project's `[NinoType]`s, after each build with the Nino.Codegen package
(`<NinoCodegen Include="typescript" />`) or from a built assembly with the Nino.Codegen.Tool command
(`nino-gen --lang ts`); see [docs/codegen.md](../../docs/codegen.md). Each release also publishes it to npm as
`nino-serialization`, with Nino's version: generate with `Runtime="nino-serialization"` (`--runtime
nino-serialization`) to import it from the package instead (`npm install nino-serialization`). The user
documentation is on the [docs site](https://nino.xgamedev.net/en/docs/v4/advanced/typescript).

```ts
import { deserialize, serialize } from "./nino";
import { PlayerCodec } from "./MyGame";

const bytes = serialize(PlayerCodec, player); // what NinoSerializer.Serialize<Player> writes in .NET
const back = deserialize(PlayerCodec, bytes);
```

## Guarantees

The reader takes untrusted input, as the .NET, Go and Rust readers do. Any data that is not a value of the type read
throws a `NinoError`, never another exception, and never reads out of bounds, recurses without a bound or allocates
more than the data holds:

- Every read is bounds-checked, and a length or count is checked against the bytes left before anything is
  allocated for it.
- Class bodies nested past 128 are deferred, as in .NET, so deep data costs a bounded stack; bodies that cannot be
  deferred, and structs that hold themselves, fail past 512.
- A slot written behind a type id reads only the ids of the types allowed there.
- Reference tracking (`trackReferences`, §7.9): a reference header other than -1, 0 or the number of an object read so
  far is rejected, as is a back reference to an object the slot does not allow, and a header of 0 after a type id
  (null is a type id of 0). Each object takes at least its 4 header bytes, so the table of objects grows with the
  data. The reader records an object as it creates it, before its body, so a body can hold it (a cycle); the writer
  numbers objects by identity.
- Maps are read into `Map`s, never plain objects, so keys such as `__proto__` are data.
- `bool`s and presence bytes are 0 or 1; UTF-8 strings are strict UTF-8 of the char count given.
- A section of deferred bodies is checked when its region is opened, before any member is read: only the outermost
  region holds one, with at least one entry, and its entries fill it exactly, keyed in increasing order by positions
  no later than the entry itself (§7.10.3).
- A member that runs past the end of its region, and a known keyed member whose value leaves bytes in its entry, are
  rejected (§7.6, §9.5).
- A key or element repeated in a map or set is rejected, as .NET's dictionaries and sets do (§9.8). The check is one
  size comparison after the map or set is read. Keys are compared as `Map` and `Set` compare them: numbers, `bigint`s,
  strings, booleans and null by value; keys read as objects (`DateTime` as a `Date`, structs, tuples) and `decimal`s
  that differ only in scale are not compared.
- Bytes after the value are left unread, as .NET does by default (§9.12).

A `DateTime` is read as a `Date`, which has no kind: the ticks are taken as UTC whatever the kind, so a value written
with `DateTimeKind.Unspecified` (or `Local`) reads as the UTC time with the same clock reading, and is written back
with `DateTimeKind.Utc`.

## Text in browsers: WebAssembly

Strings are most of the work of reading and writing many values. In Node (and Bun, and Deno with Node's `Buffer`) the
runtime moves them with `Buffer`'s native Latin-1 and UTF-16 methods. Elsewhere (browsers, Deno) it uses a small
WebAssembly module embedded in `nino.ts` (built from `wasm/text.wat`, about 1.3 KB) for the text JavaScript is slow
at, up to 4096 chars: UTF-16 text of more than 24 chars and Latin-1 text of 33 to 128 chars read, and text of more
than 64 chars written (ASCII of more than 1024 is left to `TextEncoder`). It reads and writes JavaScript strings through the JS String Builtins (`wasm:js-string`) and moves their
chars 8 or 16 at a time in SIMD registers, in one page of memory that never grows; the bytes are the same as the
JavaScript paths', which every other case takes.

The module is compiled synchronously the first time it is needed. An engine without WebAssembly SIMD, GC or the JS
String Builtins (Chromium before 130, Firefox before 134, Node 20 and older), or a page whose Content Security Policy
does not allow compiling WebAssembly (`'wasm-unsafe-eval'`), keeps the JavaScript paths without an error; a browser
may still report the blocked compilation as a CSP violation. `useWasm(false)` turns the module off, `useWasm(true)`
back on, and `useWasm()` tells whether text goes through it.

`npm run build:wasm` assembles `wasm/text.wat` (`wasm/build.ts`, no dependencies) into `nino.ts`; the tests check
that the two match.

## Tests

```sh
npm ci && npm test
```

type-checks and runs the `node:test` suite in `test/` three times: in Node, then as a browser runs it (the runtime
evaluated without `Buffer`, `NINO_TEST_RUNTIME=browser`, `test/runtime.ts`), with the WebAssembly module and without
it (`NINO_WASM=0`). `test/wasm.test.ts` compares the module's paths with the JavaScript ones for every length up to
256 and longer, with every kind of char at each position. `test/corpus.test.ts` runs the conformance corpus
(`conformance/corpus`, or the directory in `NINO_CORPUS`) over the code nino-gen generates for its types
(`test/generated`, checked by Nino.Codegen.Tests): every valid case read and written back byte for byte, every
malicious case and every truncated prefix of a valid case rejected with a `NinoError`. After a change to the runtime
that Nino.Codegen embeds, also run the `Nino.Codegen.Integration.Tests` .NET tests.

## Benchmarks

`bench/run.sh [scenario]` compares the generated code with Protocol Buffers (protobuf-es), MessagePack (msgpackr) and
`JSON` in Node and in headless Chromium, timed by tinybench, on data written by Nino in .NET (it needs the .NET 10 SDK
and, for Chromium, Playwright; the bench's own dependencies come from `bench/package-lock.json`, never this package's).
See benchmarks/README.md ("Runtimes outside .NET"). Compare before and after a change on one machine; do not publish
the numbers.
