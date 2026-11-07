# Direct calls and reuse branches outside .NET

Part of the `optimize-runtimes` skill: the owner's rule of no dispatch per element in Go, Rust and TypeScript, and
keeping the read-into paths' checks off the fresh read. Read it before writing a collection loop, a class body
call, a helper that takes a function, or a `read_into` / `deserializeInto` path.

## Dispatch

- Call the element's code directly in every collection loop, never through a func value, an interface method or a
  shared helper given a closure (the owner's rule: no virtual dispatch on a serialize or deserialize path). Go: a
  generic helper that takes `read func(*Reader) T` is not inlined, and GC-shape stenciling would pass a dictionary
  anyway, so each element is an indirect call the compiler can neither inline nor see through; a type parameter with
  methods does not help either (types of one shape share the dictionary's indirect call). nino-gen's Go helpers
  write their loops (`read_loop`, `write_loop` in `Templates/go/types.sbn`; `go tool objdump` shows `CALL ...ReadBag`
  where it was `CALL CX`). Runners (4225c20 against main 78ceaa1, go#12, 12 rounds a job, medians of jobs against
  main-vs-main floors within 0.98-1.02): a `string[]` read through `nino.ReadStrings` instead of `ReadSlice(r, 1,
  (*Reader).String)` took 0.74 of the time on EPYC 7763, 0.80 on 9V74, 0.82 on Xeon 8573C, 0.86 on 8370C, 0.91 on
  Neoverse-N2; mixed and [NinoUtf8] strings 0.95-0.97; 1M records read 0.92 on EPYC 7763; SimpleClass serialize
  0.94-0.97 on every CPU but EPYC 7763 (1.015, n=12, where the unchanged machine code of `WriteStrings` moved 1.008
  in the same jobs). One case held past its floor: 1M records serialize took 1.20 of the time on Xeon 8573C (4 jobs of 12 rounds,
  1.19-1.25, floor 0.975-0.996) while main with only the records loop written out took 0.955 (bcbbcbe, one job) and
  the change without it 1.02 (31b8e79); calling the class body directly removed it (below). A loss that holds is
  fixed, not filed as layout, however the controls point. Rust needs nothing of the kind (generics are monomorphized; `dyn` only on the
  deferral path past depth 128), nor does nino-gen's TypeScript (a function per collection shape, subtypes by a
  `switch` on the id or the constructor). Class bodies and dictionary key checks went the same way (414a0a28): `Reader`/`Writer.EnterBody` and `ExitBody` open a
  body in place and fall back to `ReadClassInto`/`WriteClass` from 128 open bodies, and `nino.CheckUniqueKeys` checks
  the pairs once; 1M records then read in 0.93-0.96 of the time on EPYC 7763, 9V45, 9V74 and Neoverse-N2, and the
  Xeon 8573C loss above went to 0.947 of the old base (one job of 12 rounds). Sets followed with `nino.CheckUniqueValues` (no bench case
  holds a set, so no runner number). Still dispatched by design: tracked classes, generic type arguments (Go `NinoArg`, TypeScript codec objects) and the struct-tag
  path (a plan of closures).
- Rust: a sealed class's forwarding methods with `#[inline]` (`nino::sealed_read!` / `sealed_write!`, as the derive
  had them and the template did not) read records-1000 2.0-4.3 % and wrote it 2.0-9.2 % faster on EPYC 7763, 9V45 and
  Neoverse-N2 (run 41042990902; floor -2.1 to +0.7 %).
  A raw struct's impls followed in round 129 (`nino::raw_read!` / `raw_write!`, the derive's code with `#[inline]`;
  the template's had none): in the release build of `tests/generated` the standalone raw `read`/`write` symbols are
  gone and their callers inline them (no runner number yet: the benches' `Point` uses the macros since).

## Reuse branches

- Keep reuse branches off the read path of a new value: share the body, not the dispatch. Reading into an existing
  value (`deserializeInto`, `ReadXInto`, `read_into`) shares each type's body with the plain read, but a member that
  holds nothing goes straight to the plain read (`v.m == null ? readX(r) : into`), and a slot's switch and a
  collection's loop keep a fresh form without the reuse checks. TypeScript with one switch and one helper for both
  (`read: (r) => XCodec.readInto(r, null)`, helpers taking an optional `cur`) took Player deserialize 4-9 % and Zoo
  7-8 % longer in Chromium (run 40961396786, n=9-30); with the fresh forms back, on par (40969410249). Reading a
  struct member into the instance its class's initializer already made, instead of a second one, made Mesh
  deserialize 8-24 % faster. Go kept its slots' and sealed classes' plain reads (with 414a0a28's direct body calls)
  and read lists through the into loop: on par (40976827882, geomean within 0.8 %); Rust's `read = Default +
  read_into` was on par too (40925188020).
