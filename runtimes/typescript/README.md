# Nino for TypeScript

`src/nino.ts` is the TypeScript runtime of Nino: it reads and writes the bytes Nino writes in .NET (the format is
[docs/spec/wire-format.md](../../docs/spec/wire-format.md)). nino-gen writes it next to the classes and codecs it
generates for a project's `[NinoType]`s; each release also publishes it to npm as `nino-serialization`, with Nino's
version, which generated code imports instead with `Runtime="nino-serialization"` (`npm install nino-serialization`).
How to generate the code, the options, the type mapping, the generated API and what untrusted data may do are in the
guide: [TypeScript, Go and Rust](https://nino.xgamedev.net/en/docs/v4/advanced/codegen#typescript). This file is the
runtime's reference: its API, its implementation notes, tests and benchmarks (how nino-gen works:
[docs/codegen.md](../../docs/codegen.md)).

```ts
import { deserialize, serialize } from "./nino";
import { PlayerCodec } from "./MyGame";

const bytes = serialize(PlayerCodec, player); // what NinoSerializer.Serialize<Player> writes in .NET
const back = deserialize(PlayerCodec, bytes);
```

`deserialize` reads one value and leaves any bytes after it unread; `deserializeExact` also throws a `NinoError`
when bytes follow the value (Go's `DeserializeExact`, Rust's `deserialize_exact`).

`deserializeInto(codec, bytes, value)` reads into a value you already have, as `NinoDeserializer.Deserialize(data, ref
value)` does in .NET, and returns the value read (`value` itself, unless the data holds null or another class): nested
objects are read in place, arrays keep their storage and read each element into the old one at its index, maps and
sets are cleared and refilled, and a member declared as a class with subtypes or an interface is read into only when
the data holds its own class. Tracked objects and typed arrays are read new. If it throws, `value` holds what was read
before the error and the rest as it was. Generated codecs implement it as `NinoCodec.readInto`; a hand-written codec
without it is read new.

`serialize` returns a view of a 64 KiB (or larger) buffer that later results never overwrite, so each value costs no
`ArrayBuffer` of its own, but a new buffer every 64 KiB or so. Code that writes each value out before the next one (to
a socket or a file) can write into memory it owns instead, as `NinoSerializer.Serialize(value, bufferWriter)` does in
.NET (Go's `Append` and `Writer.Reset`, Rust's `serialize_into`):

```ts
import { NinoWriter, serializeInto } from "./nino";

const writer = new NinoWriter();          // reused: once large enough, writing allocates nothing
writer.reset();                           // empty it, keeping its buffer
serializeInto(PlayerCodec, player, writer); // appends; returns the value's size
ws.send(writer.written());                // a view of the writer's buffer: send it before writing again

const size = serializeInto(PlayerCodec, player, frame); // into a Uint8Array, from its start
if (size > frame.length) { /* did not fit: frame is unchanged; call again with `size` bytes */ }
```

Node 24 starts a full garbage collection (a pause that grows with the heap) for every 64 MB of
new `ArrayBuffer`s that no scavenge freed in between; a reused writer allocates none, so it never starts one. Writing
into a `Uint8Array` copies the value once, from a writer the runtime reuses; a `NinoWriter` is written in place.
Since the next `reset` writes over the bytes `written()` views, hand them only to a call that copies them before it
returns (a browser `WebSocket.send`, `fs.writeSync`), or wait until the write is done (a Node stream's `write` callback).

## Typed arrays

With nino-gen's `NumericArrays="typed"` (the option, its types and its trade-off per engine are in the guide's
[Typed Arrays](https://nino.xgamedev.net/en/docs/v4/advanced/codegen#typed-arrays)), generated code reads and writes
sequences of fixed-size numbers through `NinoReader.numbers(Int32Array)` and `NinoWriter.numbers(values)`. Their bytes
on the wire are the typed array's own little-endian bytes, so those over 64 bytes are copied in one block, with no loop
over the elements (a big-endian engine swaps each element's bytes after the copy); shorter ones are read element by
element. The reader copies from an aligned view of the data when the data is aligned to the elements, else from its
bytes. What it returns never refers to the data: typed arrays of 65 bytes to 8 KB are views of a shared 64 KB buffer
(as Node's small `Buffer`s share a pool, since an `ArrayBuffer` for each costs more than the copy).

## Guarantees

The reader takes untrusted input: what every runtime guarantees (bounds, depth, type ids, strict bytes, trailing bytes)
is in the guide's [Untrusted Data](https://nino.xgamedev.net/en/docs/v4/advanced/codegen#untrusted-data), and each
check is in the specification's §9. Every failure is a `NinoError`, never another exception. Specific to this runtime:

- A tracked class in a slot that does not track must not have a reference header of 0 after its type id (null is a
  type id of 0). The reader records an object as it creates it, before its body; the writer numbers objects by
  identity.
- A section of deferred bodies is checked when its region is opened, before any member is read (§7.10.3).
- A repeated key or element in a map or set (§9.8) is found with one size comparison after it is read.
- Hash flooding (§9.8): V8 hashes strings with a per-process seed but numbers and `bigint`s without one, so for a map
  or set of more than 128 number or `bigint` keys, generated code passes each key through `NinoKeyGuard`, which counts
  the keys of each bucket of the table V8 ends with and throws when a key would find more than 100 keys in its bucket
  (the smaller tables on the way stay within O(n log n) probes). JavaScriptCore (Safari, Bun) and SpiderMonkey
  (Firefox) hash numbers their own way, where it bounds V8's buckets, not theirs. A map or set of 128 keys or fewer is
  not guarded: no key there costs more than 128 probes.

## Text in browsers: WebAssembly

In browsers and Deno, `nino.ts` moves longer text through a small WebAssembly module embedded in it (built from
`wasm/text.wat`), which reads and writes JavaScript strings through the JS String Builtins (`wasm:js-string`); in Node
it uses `Buffer`'s native methods. Which text takes it, the engines that support it, the Content Security Policy it
needs and `useWasm` are in the guide's [Text in Browsers](https://nino.xgamedev.net/en/docs/v4/advanced/codegen#text-in-browsers).
The module is compiled synchronously the first time it is needed, and its paths write the same bytes as the
JavaScript ones.

`npm run build:wasm` assembles `wasm/text.wat` with [wasm-tools](https://github.com/bytecodealliance/wasm-tools)
(`wasm-tools parse`, then `strip --all`) and writes the module into `nino.ts`, with the SHA-256 of the `text.wat` it
came from (`wasm/embed.ts`). Install wasm-tools with `cargo install --locked wasm-tools`, or put the binary of a
[release](https://github.com/bytecodealliance/wasm-tools/releases) on the `PATH`. Only a change to `text.wat` needs
it: the tests check the hash, and rebuild and compare the module byte for byte where wasm-tools is installed.

## Tests

```sh
npm ci && npm test
```

type-checks and runs the `node:test` suite in `test/` three times: in Node, then as a browser runs it (the runtime
evaluated without `Buffer`, `NINO_TEST_RUNTIME=browser`, `test/runtime.ts`), with the WebAssembly module and without
it (`NINO_WASM=0`). `test/flooding.test.ts` reads sets and maps of keys V8 puts in one bucket. `test/wasm.test.ts` compares the module's paths with the JavaScript ones for every length up to
256 and longer, with every kind of char at each position. `test/corpus.test.ts` runs the conformance corpus
(`conformance/corpus`, or the directory in `NINO_CORPUS`) over the code nino-gen generates for its types
(`test/generated`, checked by Nino.Codegen.Tests): every valid case read and written back byte for byte, every
malicious case and every truncated prefix of a valid case rejected with a `NinoError`. After a change to the runtime
that Nino.Codegen embeds, also run the `Nino.Codegen.Integration.Tests` .NET tests.

The runtime itself (the package's `dist`, ES2022) runs on Node 15 and later, as `engines` says; the tests need
Node 22.18 or later, which runs their TypeScript as it is (type stripping).

## Benchmarks

`bench/run.sh [scenario]` compares the generated code with Protocol Buffers (protobuf-es), MessagePack (msgpackr) and
`JSON` in Node and in headless Chromium, timed by tinybench, on data written by Nino in .NET (it needs the .NET 10 SDK
and, for Chromium, Playwright; the bench's own dependencies come from `bench/package-lock.json`, never this package's).
Each pass takes the next of 64 variants of a scenario's data, since V8 reuses work on an input it has just read (a
repeated JSON text parses many times faster), and each serializer runs with only its own data live (a full GC marks
what is live, and V8 starts one for every 64 MB of new `ArrayBuffer`s no scavenge freed). See benchmarks/README.md ("Runtimes outside .NET"). Compare before and after a change on one machine; do not publish
the numbers.
