# TypeScript runtime: file map

> Read when: reading or changing the TypeScript runtime (runtimes/typescript: src/nino.ts, its WebAssembly kernels, tests or benchmark).

The npm package `nino-serialization` (ES module, Node 15+ for the published build; tests on Node 22.18+ type
stripping): `src/nino.ts` is the whole runtime, embedded in nino-gen (`src/Nino.Codegen`) and written next to the
generated TypeScript. README.md documents it. `audit-runtimes` audits it, `audit-security` its reader.

```
package.json                 the package (exports dist/nino.js + .d.ts) and scripts: build, build:wasm, typecheck, test
                             (tsc, then the suite in Node, as a browser, and as a browser without WebAssembly)
package-lock.json            dev dependencies: typescript, @types/node, fast-check
tsconfig.json                type checks src, test and wasm (noEmit)
tsconfig.build.json          builds src/nino.ts into dist (npm run build)
README.md                    usage, the API, guarantees, tests, benchmarks
src/nino.ts                  the runtime: reader and writer, strings, collections, depth and deferral, type ids,
                             reference tracking; embeds the WebAssembly text kernels between wasm:begin / wasm:end
scripts/version.mjs          after tsc: writes the package version over the placeholder in dist
wasm/
  text.wat                   the WebAssembly kernels (SIMD, GC, JS String Builtins) for text <-> Nino's string bytes
  embed.ts                   npm run build:wasm: assembles text.wat with wasm-tools into src/nino.ts (base64 + SHA-256)
test/
  primitives.test.ts         every primitive's bytes and round trip
  strings.test.ts            both string encodings, Node's and the browser's paths
  collections.test.ts        arrays, sets, maps, nullables, the type id registry, serialize's shared buffers
  nesting.test.ts            deferral past 128 bodies, the 512 limit, cycles
  malicious.test.ts          truncations, forged lengths, bad presence bytes, unknown ids, fast-check mutations
  wasm.test.ts               the kernels against the JavaScript paths; text.wat matches what nino.ts embeds
  into.test.ts               deserializeInto: objects read in place, arrays, maps, polymorphic members, failed reads
  corpus.test.ts             the conformance corpus (conformance/corpus or NINO_CORPUS) over generated/Nino.Conformance.ts
  codecs.ts                  codecs written as nino-gen generates them, for the runtime's tests
  runtime.ts                 --import hook: NINO_TEST_RUNTIME=browser (no Buffer), NINO_WASM=0 (no kernels)
  generated/                 Nino.Conformance.ts (also as .TypedArrays.ts, --numeric-arrays typed) and
                             Nino.Codegen.Tests.ts: nino-gen's output for the corpus's types and
                             tests/Nino.Codegen.Tests/Shapes.cs (do not edit)
bench/                       the generated TypeScript against protobuf-es, msgpackr and JSON (tinybench) in Node
                             (node.ts) and headless Chromium (browser.mjs); Bench.csproj + Program.cs write the data in
                             .NET; proto/ and buf.*.yaml the protobuf messages; run.sh runs it all; own package.json
```

Generated files: `test/generated/*` is rewritten by `NINO_UPDATE_GENERATED=1 dotnet test tests/Nino.Codegen.Tests`;
the embedded module in `src/nino.ts` by `npm run build:wasm`.

## TypeScript runtime (runtimes/typescript)

`src/nino.ts` is the runtime Nino.Codegen embeds and nino-gen writes next to the generated TypeScript. The
reader takes hostile input as the Go and Rust ones do: check bounds and lengths before allocating, count depth as .NET
does and defer past it, never build objects whose keys come from the data on a plain object (`__proto__`): use `Map`.
Before committing: `npm ci && npm test` (type check and the `node:test` suite) in `runtimes/typescript`, and the
`Nino.Codegen.Integration.Tests` and `Nino.Codegen.Tests` .NET tests.
