---
name: optimize
description: "Nino's .NET and Unity optimization techniques, measured and kept, and how to read the machine code - the JIT's inlining budget, keeping the reader and writer in registers, direct calls instead of virtual dispatch, bounds checks, vector loops on x64 AVX-512/AVX2/SSSE3 and Arm64 tbl/ldp/stp, Unity's Burst kernels and IL2CPP's generated C++, the prefetch design of every runtime, asm.py and JitDisasm; Go, Rust and TypeScript are optimize-runtimes. Use when writing or reviewing a .NET hot path, before committing a change to a read or write path, when a .NET or Unity case got slower than main (compare the listings), or when a new trick is found (record it here). Triggers: optimize, hot path, inlining, AggressiveInlining, NoInlining, bounds check, RNGCHKFAIL, Unsafe.Add, GetArrayDataReference, Vector128/256/512, AVX-512, Avx2, AdvSimd, Ice Lake, vpermb, tbl, shuffle, permute, spills, asm.py, JitDisasm, Tier1, listing, virtual dispatch, megamorphic, prefetch, Burst kernel, IL2CPP C++, 变慢, 回归, 虚调用, 优化技巧, 性能优化, 边界检查, 机器码, 汇编, 内联, 向量化."
metadata:
  internal: true
---
# Optimization techniques

The tricks that made Nino fast, the ones that made it slower, and how to see which you have. This skill owns the
techniques for .NET and Unity and the prefetch design of every runtime; how to measure is `benchmark`, where the next
win is `audit-perf`, the platforms each technique must hold on are `platforms`, and the Go, Rust and TypeScript
techniques are `optimize-runtimes`.

## Before committing a read or write path

When a representative workload improves and a narrow small-input case slows, first isolate the large-input loop
or its dispatch and retest both. Compare Tier 0, Tier 1 and the caller before attributing a slowdown to the extra
instructions in the loop. Record the remaining tradeoff alongside the evidence.
When an experiment removes real work but the first A/B shows no gain, inspect the caller and loop for where the
cost moved, then test a materially different variant before shelving it. Repair build or test infrastructure
failures and rerun the relevant check before treating it as an outcome for the candidate.
For a sound byref loop with disappointing timings, compare the frame, spills, helper inlining and tier promotion
before changing the loop back. Try a different helper boundary or dispatch placement and measure the original
and revised callers under default tiering.

A change to a read or write path (Nino.Core, Nino.Immutable, the code the generator emits) can slow cases it does not
touch: everything a top-level call reaches is inlined into one method, under one inlining budget. Before committing
one, run `python3 benchmarks/asm.py compare origin/main` (about two minutes, see benchmarks/README.md) and explain or
fix every method it lists; on a branch whose `origin/main` has moved since it started, compare its base instead
(`git merge-base HEAD origin/main`), or the diff holds other commits' changes too (round 129). To prove the code unchanged, dump both sides with `DOTNET_TieredCompilation=0` (the
variable around `asm.py dump [--ref REF] --runs 1 --asm FILE`) and compare the listings per method: FullOpts code
has no profile, so it repeats exactly, while Tier1 listed 7 of 2,356 methods in code a change did not reach.
`--ref REF` builds REF's own benchmarks: when the two sides' cases differ (a new case), the interceptors' numbers
shift and the diff lists unrelated `SerializeNNN`; dump the base from a worktree holding the head's
`benchmarks/Nino.Benchmark`. A listing of several runs or threads can interleave two methods: compare the methods a
case reaches, normalized (addresses, labels, `align [N bytes]` padding, Arm64 `movz`/`movk` address immediates).
For the Arm64 side run `Nino.Benchmark.dll` directly with the cross JIT (benchmarks/README.md), `DOTNET_STARTUP_HOOKS`
set to `jitwarm`'s build and `NINO_JIT_FILTER` to the case: `DOTNET_AltJit` around `asm.py` reaches its `dotnet
build`, which fails (round 105).

## The techniques, by topic

Each line is the rule; the reference file holds it in full with why and the runner numbers that set it. Read the file
for the topic you touch before changing code there: most of these were learned by a slower commit, and the evidence
says which CPUs and cases moved.

- **The JIT** ([references/jit.md](references/jit.md)): keep rare or large paths out of line (`NoInlining`), platform
  branches in the out-of-line part, and decide options at generation time, never as a run-time branch on the default
  path (the root's budget is sized from its IL); never pass `ref reader` / `ref writer` (or call an out-of-line
  `Reader`/`Writer` instance method) where it is not inlined; mark every small helper `AggressiveInlining`; add nothing
  to a method whose inlinee sits at the edge of the budget; write a platform test as `A || B` of branch-free operands at
  each site; fold JIT-time checks into one `static readonly`; keep locals few; a smaller listing is not a faster one;
  fast modulo on x64 only; keep a hot field off other threads' cache lines with a padded struct field; code run once
  per type at startup calls nothing generic over the formatter.
- **Dispatch** ([references/dispatch.md](references/dispatch.md)): no virtual dispatch on a serialize or deserialize
  path where a direct form exists (the owner's rule); call a formatter known only at run time through a `static
  readonly` field read directly, never through `??`, and keep it never null; the inventory of the indirect calls that
  stay (another assembly's `[NinoType]`, shared generic code, the netstandard block loops, user-extensible points, one
  per top-level call in shared code) and why.
- **Bounds checks and vector loops** ([references/vectors.md](references/vectors.md)): index through a ref to the first
  element and `Unsafe.Add` behind the one length check; bump `ref` cursors and count down; take `Vector128` parts out
  of the loop on Arm64; consecutive `tbl` registers; `Unsafe.As` for `Vector<T>` in the netstandard builds; checks of
  strided bytes and value ranges with accumulators and subtract-and-OR; strided runtime entries through the block path;
  per-CPU gates by CPUID model (Ice Lake), per-vendor block copies, Arm64 lane groups on the 16-byte grid; allocate
  uninitialized only when every byte is written; nothing added to `ArrayFormatter` / `ListFormatter.Deserialize`;
  research the microarchitecture before probing a gap on one CPU, and the Ice Lake attempts not to retry.
- **Unity, Burst and IL2CPP** ([references/unity.md](references/unity.md)): a generic formatter takes its type
  parameters' formatters as type parameters; never cast `default(TFormatter)` to an interface per value; native
  kernels as function pointers in static fields, each `calli` in its own `NoInlining` helper; thresholds from a sweep;
  build only the mask a call needs; where the WebGL kernels pay; what an IL2CPP class read costs.
- **Prefetch, every runtime** ([references/prefetch.md](references/prefetch.md)): decided at generation time for
  references in contiguous containers, how each platform prefetches, and every attempt in .NET, Go and Rust with why it
  did or did not land (.NET as the opt-in `NinoPrefetchLargeCollections`, Rust gated on the last-level cache, Go not
  landed).

## Recording a technique

A technique found and measured, faster or slower, goes into the reference file of its topic as one bullet, written as
AGENTS.md "Keep what you learn" says (rule, why, evidence: case, CPUs, runner numbers, commit or run). A new topic gets
a file and a line above. Go, Rust and TypeScript techniques go to `optimize-runtimes`.

## Reading the listings

- On an Arm64 Mac, compare x64 instructions through the matching cross JIT in a private .NET runtime copy.
  Use the listing to check instructions and code size. Use an actual x64 runner for throughput claims.
  A short-copy change (88315b4c) grew `ToArray` from 205 to 412 x64 bytes, or 478 with AVX2 and AVX-512
  disabled, while the x64 serialization caller's normalized listing stayed identical.


- x64: `--asm` of `benchmarks/asm.py`, once per ISA (`DOTNET_EnableAVX512=0`, `DOTNET_EnableAVX2=0`).
- NativeAOT: ILC compiles with the JIT, which takes the same options as `--codegenopt`: publish
  `benchmarks/Nino.Benchmark -r linux-x64` with `PublishAot` set in a props file passed as
  `-p:CustomBeforeMicrosoftCommonProps=` (conditioned on the project name, or the netstandard references get it) and
  `<IlcArg Include="--codegenopt:JitDisasm=*" />`, `--codegenopt:JitStdOutFile=<file>` and `--parallelism:1` in one
  passed as `-p:CustomAfterMicrosoftCommonTargets=`, with `<TrimmerRootAssembly Include="Nino.Benchmark" />` (7
  minutes, 570 MB of listings for `*`; `IlcGenerateMapFile`/`IlcGenerateMstatFile` add the map). Direct calls show as
  `call Name(...)`; `call [rax+0x..]Type:Method` is virtual, `call [r11]Iface:Method` an interface dispatch cell,
  `test reg, 2` before `call [reg-0x02]` a fat pointer of shared generic code, and `lea rax, [(reloc ..)]` then
  `call rax` a helper. Walk the direct calls from each case to find what it reaches. Its `--verify` stops in
  MessagePack's setup (not AOT-safe), so time a scratch program instead.
- Arm64 from an x64 machine: `benchmarks/cross-jit.sh` installs the other architecture's JIT, and
  `DISASM=1 benchmarks/members/run.sh` writes both listings for the member cases (that JIT only lists: with
  `DOTNET_AltJit='*'` around `asm.py`, the build and the benchmark died with SIGSEGV); or build for `linux-arm64` and
  run under `qemu-aarch64-static` with
  `DOTNET_TieredCompilation=0 DOTNET_JitDisasm='<method>' DOTNET_JitStdOutFile=<file>` (the code paths only a type
  outside the benchmark suite takes, such as `(byte, int)` for the 64-byte permutes, need a small program of their own).
  A clean loop is loads (`ldp`), lookups, stores (`stp`) and the cursor adds; `stp`/`ldp` to `[fp, ...]`, `mul` or
  `mov v..` in it are the signs above.
- The signs in any listing: `Reader:Skip`, `Reader:CopyTo` or `NinoUnsafe:Read` as calls (the budget ran out);
  `NinoReadContext:Return` or `NinoWriteContext:Return` calls and `[rbp-0x..]` loads and stores of the cursor (the
  reader or writer was not kept in registers).
- The block order of a Tier1 method varies between dumps of the same code (profile data): `asm.py` dumps of
  `--filter TupleList` showed `NinoBlockCollections.WriteEach<(int, long)>` with its permute loop moved, dumps of
  `--filter SerializeTupleList` the same listing on both sides. Before calling a listing changed, dump both sides
  again with the narrowest filter and compare the instructions with addresses and labels normalized.
- Timings on a shared machine vary by ±15 %: read the code before trusting a local difference, and take the decision
  from runners (`benchmark`). The wire format must not change unless that is the point: compare the bytes.

## Owned elsewhere

- `NinoSettings.Untrusted` tests stay a single `if` on a frozen field with the work out of line (`audit-security`).
- Paths through a runtime's internals: `runtime-internals`. Platform matrix and emulators: `platforms`; a vector
  path chosen by `X.IsSupported` uses X's own intrinsics under that test, or NativeAOT runs it in software
  (`platforms`).
- Go, Rust and TypeScript: `optimize-runtimes` (the techniques: inlining and GC, bounds checks, Rust's fixed-size
  blocks, strings, dispatch, Go's block checks); their rules: `audit-runtimes` and `runtimes/*/AGENTS.md`.
- Generated code's listings: `audit-generated-code`.
