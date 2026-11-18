# TypeScript runtime: file map

> Read when: reading or changing the TypeScript runtime (runtimes/typescript: src/runtime/, its single-file bundle, WebAssembly kernels, tests or benchmark).

The npm package `nino-serialization` (ES module, Node 15+ for the published build; tests on Node 22.18+ type
stripping): edit the modules in `src/runtime/`. The checked-in bundle `src/nino.ts` is the file nino-gen embeds and
writes next to generated TypeScript. The npm package compiles that bundle to `dist/nino.js` and its declarations.
README.md documents it. `audit-runtimes` audits it, `audit-security` its reader.

```
package.json                 the package (exports dist/nino.js + .d.ts) and scripts: build, build:bundle, build:wasm, typecheck, test
                             (tsc, then the suite in Node, as a browser, and as a browser without WebAssembly)
package-lock.json            dev dependencies: typescript, @types/node, fast-check
tsconfig.json                type checks src, test and wasm (noEmit)
tsconfig.build.json          builds src/nino.ts into dist (npm run build)
LICENSE                      MIT license text included in the npm package
README.md                    usage, the API, guarantees, tests, benchmarks
src/
  nino.ts                    generated single-file bundle: embedded by nino-gen, compiled for npm (do not edit)
  runtime/                   authoritative modules; index.ts declares the bundle's public API via named re-exports
    api.ts                   codecs, nullable types, serialize and deserialize entry points, ninoVersion
    reader.ts, writer.ts     wire I/O, collection and reference state, reader scratch buffer and writer slab
    deferrals.ts             shallow-stack reading and writing of nested bodies
    error.ts                 shared NinoError
    keys.ts                  hash-flooding guard
    numbers.ts               typed arrays, endianness, shared buffer for numeric blocks
  grid.ts                    rectangular array shape and dimension limits
    scalars.ts               date and GUID constants
    subtypes.ts              registry by type id and by class
    text.ts                  Node and JavaScript text paths, Wasm text kernels and loader
scripts/bundle.mjs           npm run build:bundle: flattens the modules into src/nino.ts; build and test check it
scripts/version.mjs          after tsc: writes the package version over the placeholder in dist
wasm/
  text.wat                   the WebAssembly kernels (SIMD, GC, JS String Builtins) for text <-> Nino's string bytes
  embed.ts                   npm run build:wasm: assembles text.wat with wasm-tools into src/runtime/text.ts (base64 + SHA-256)
test/
  primitives.test.ts         every primitive's bytes and round trip
  strings.test.ts            both string encodings, Node's and the browser's paths
  collections.test.ts        arrays, sets, maps, nullables, the type id registry, serialize's shared buffers
  nesting.test.ts            deferral past 128 bodies, the 512 limit, cycles
  malicious.test.ts          truncations, forged lengths, bad presence bytes, unknown ids, fast-check mutations
  flooding.test.ts           hash flooding: number and bigint keys V8 puts in one bucket, rejected by NinoKeyGuard
  wasm.test.ts               the kernels against the JavaScript paths; text.wat matches what nino.ts embeds
  into.test.ts               deserializeInto: objects read in place, arrays, maps, polymorphic members, failed reads
  corpus.test.ts             the conformance corpus (tests/conformance/corpus or NINO_CORPUS) over generated/Nino.Conformance.ts
  shapes.test.ts             generated/Nino.Codegen.Tests.ts (Shapes.cs) against bytes .NET writes
  codecs.ts                  codecs written as nino-gen generates them, for the runtime's tests
  runtime.ts                 --import hook: NINO_TEST_RUNTIME=browser (no Buffer), NINO_WASM=0 (no kernels)
  generated/                 Nino.Conformance.ts (also as .TypedArrays.ts, --numeric-arrays typed) and
                             Nino.Codegen.Tests.ts: nino-gen's output for the corpus's types and
                             runtimes/csharp/tests/Nino.Codegen.Tests/Shapes.cs (do not edit)
bench/                       the .NET suite's cases (runtimes/csharp/benchmarks) in the generated TypeScript against
                             protobuf-es, msgpackr and JSON (tinybench) in Node (node.ts) and headless Chromium
                             (browser.mjs); bench.ts finds each case's codec by its .NET type (ninoCodecOf);
                             generate.sh writes out/data (the cases' bytes), gen/model.ts (nino-gen on the schema) and
                             gen/pb (the suite's .proto files, buf.gen.yaml), all ignored; run.sh runs it all; own
                             package.json
```

Generated files: `test/generated/*` is rewritten by `NINO_UPDATE_GENERATED=1 dotnet test runtimes/csharp/tests/Nino.Codegen.Tests`;
the embedded kernels in `src/runtime/text.ts` and the bundle by `npm run build:wasm`.

## TypeScript runtime (runtimes/typescript)

Run `npm run build:bundle` after editing a module. Commit the bundle alongside its source so building Nino.Codegen
requires only .NET. Keep internal imports relative and named.
The reader takes hostile input as the Go and Rust ones do: check bounds and lengths before allocating, count depth
as .NET does and defer past it, and use `Map` for keys from the data (`__proto__`).
Before committing: `npm ci && npm test` (type check and the `node:test` suite) in `runtimes/typescript`, and the
`Nino.Codegen.Integration.Tests` and `Nino.Codegen.Tests` .NET tests.
