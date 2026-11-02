# The inlining budget and GC outside .NET

Part of the `optimize-runtimes` skill: V8's and LLVM's inlining limits, and what V8's GC and typed arrays cost.
Read it before growing a TypeScript or Rust hot helper, or when a TypeScript case got slower in a large heap.

- Outside .NET the same budget exists: V8 inlines only functions under 460 bytes of bytecode, so a TypeScript read
  helper that grows past it (`otherString` at 574 bytes) becomes a call on every use; split the rare path out. Rust:
  when a shape helps one target and hurts another, put it behind `cfg(target_arch)` and keep the generic loop for
  every other target (the map read taking one check per entry is x86/x86_64 only: on Arm64 it held the key/value pair
  in a SIMD register across the hash call and Neoverse-N2 ran 3 to 4 % slower). Test both paths (`platforms`).
- TypeScript output is the bytes it writes: a slower serialize in a large heap is often GC, not the loop. Before changing
  a write loop for a case that got slower, run it with `--trace-gc` and read whose pauses land in its task. Every new
  `ArrayBuffer` counts as external memory, and Node 24 starts a full GC for each 64 MB of them that no scavenge
  freed (`benchmark`, "Keep only the running serializer's data live"). Its cost grows with the live heap, and no
  code in `nino.ts` avoids it while each result is new memory. Two Mesh write loops measured no faster on runners
  (test branch ef4917d, run 40815167424, against Nino's own loop in the same job):
  writing each `Vec3` into a `Float32Array` scratch, then one `set` into the output, measured 0.455-0.53 against
  0.49-0.51 ms in Chromium (a wash within the 0.93-1.03 floor), and in Node 1.1-1.3 ms against 0.50-0.62 ms
  because its task met more of the GC pauses. Loading element i+8's `x` to start its miss early measured 0.49-0.55
  against 0.49-0.51 ms. Reading the same graph without writing took only 0.11-0.17 ms. Gains there come from fewer
  objects and less GC, not from the loop.
- Count a typed array's backing store as an allocation in V8: give each typed array over 64 bytes one of its own only
  when it must outlive the call, and never touch `.buffer` of one of 64 bytes or less. Those V8 keeps on its heap, and
  `.buffer` moves it off-heap: copying 4 int64s through it read Event 9-32 times slower than an element loop (test
  branch 97deeca, run 40817550027). Above 64 bytes a fresh `ArrayBuffer` per array cost more than the copy: views of a
  shared 64 KB slab (as Node pools small `Buffer`s) read Samples 0.31-0.78x the time of an own buffer each (f85f4e7),
  and locally `slice()`, `set()` into `new Float64Array(n)` and a DataView loop into one were all 2x the slab in
  Chromium and 4-5x in Node. Even with the slab, Chromium read 3 x 1,000 numbers 2.0-3.3 times slower into typed arrays
  than into `number[]` (Node 0.55-0.85x; run 40865735513, d07506b), so typed arrays are nino-gen's opt-in
  `--numeric-arrays typed`, not its default: serialize was faster in both engines (Samples 0.35-0.77x).
