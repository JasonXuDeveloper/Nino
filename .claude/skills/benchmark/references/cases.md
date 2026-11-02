# Benchmark cases outside .NET: what the TypeScript bench taught

Part of the `benchmark` skill: why the TypeScript bench rotates its inputs, keeps only one serializer's data live and
reads new runtime APIs off the module namespace, with the runner numbers behind each. Read it before adding or
changing a case in `runtimes/*/bench`, or when a TypeScript number moves without a code change.

- Rotate the inputs of any microbenchmark that parses (deserialize, `JSON.parse`, decoders): prepare K variants of the
  same shape and size with different content (K=64 in `runtimes/typescript/bench`), give every serializer all of them
  before the timing, and let each iteration take the next one round robin, for serialize and deserialize alike;
  allocate the variants outside the timed region. Why: V8 reuses work for a JSON text it just parsed (hidden-class
  transitions, internalized keys), so one repeated input flatters JSON: a Lookup-like text took 0.21 ms a parse
  repeated, 7.8 to 8.4 ms with 200 different texts in turn. On runners (19801e3, landed as 81d0700, against main, run 40792547994, medians
  of 3 to 60 rounds a CPU, head-vs-head floors within 1-12 %) Lookup's JSON deserialize went from 0.72x Nino's time to
  8.97x on EPYC 7763 in Node (0.58x to 6.85x in Chromium), and from 0.62-0.79x to 8.5-9.5x on every other CPU
  (Neoverse-N2 0.76x to 8.48x); Strings ASCII barely moved (2.11x to 2.27x in Node on EPYC 7763). Rotation also takes
  the inputs out of the cache, which every serializer pays, but less than it looked: reading Mesh's 40,000 `Vec3`s
  without writing took 0.074 to 0.17 ms a pass on EPYC 7763 in Node (0.06 to 0.12 in Chromium; test branch run
  40815167424). Most of what Nino's serialize lost came from the heap the rotation kept live (next bullet).
- Keep only the running serializer's data live: make each serializer's variants just before its tasks and drop them
  after (`runtimes/typescript/bench/bench.ts`, `make`), never every serializer's at once. Why: a full GC marks
  everything live, and Node 24 (V8 13.6) starts one for every 64 MB of new `ArrayBuffer`s that no scavenge freed
  in between (the external memory soft limit, `kExternalAllocationSoftLimit`; a scavenge without pointer compression
  comes only after 128 MB of young backing stores, or when the heap's new space fills). A serializer that writes its
  output into `ArrayBuffer`s and allocates little else on the heap, such as Nino, therefore paid for marking four
  copies of 64 variants: Nino's Mesh serialize had four pauses of 175 to 195 ms in its 1.1 s task (`--trace-gc
  --trace-incremental-marking`, "Start (external memory pressure)", 554 MB live), and with rotation it went from 0.19
  to 1.15 ms on EPYC 7763 while JSON, whose output strings drive scavenges, did not move. Node 22 (V8 12.4) did not do
  this. With each serializer's own data only (0c5ad99 against main's bench 78ceaa1, run 40816601617, both lines
  same against same, floors 0.92-1.16): Node Mesh serialize 1.22 to 0.41 ms on EPYC 7763 (n=48), 0.57 to 0.31 on
  9V45, 1.20 to 0.42 on Neoverse-N2 (n=90); Samples 0.60-0.65x, Player 0.75-0.90x, Lookup 0.80-0.90x on every CPU,
  in Chromium too (Mesh 0.86-0.98x); the competitors gained too where they write `ArrayBuffer`s (Protobuf Mesh
  serialize 0.64x on EPYC 7763 in Node, msgpackr Lookup and Strings 0.4-0.5x in Chromium); JSON within 0.85 to 1.08x.
- Read a runtime API newer than the bench off the module namespace (`nino.serializeInto` from `import * as nino`),
  and skip its task when it is undefined: runtime-ab runs the head's `bench.ts` against each side's own `nino.ts`, so
  a named import of a new export fails esbuild on every line whose base predates it. The `serializeInto` task (fed673be)
  does this; its own numbers come from a line with the same commit on both sides. Such a line also measures the other
  tasks with and without the new one running between them: judge it against a null line (the head with the export
  hidden, against the head), never against the base alone. With identical `serialize` code, running `serializeInto`
  made the next `serialize` timings slower on every CPU (Zoo +5 to +15 %, Samples and Mesh up to +10 % in run
  40864160156 against main, the same in the null line 0b46472 of run 40925188020).
