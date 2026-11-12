# Unity: Burst kernels and IL2CPP

Keep ISA selection in the registered Burst kernels for wider implementations
of `Narrow`, `Widen`, `Permute`, and `Interleave32`. These operations already
have `NinoKernels` ABI slots. Bind restricted tables to restricted entry points
so tests that cap the ISA exercise the intended path. The AVX2 interleave
takes eight pairs at a time and leaves the final four to the SSE2 loop.
Test one instruction set per `if`, its block only a call and a return, and
keep every kernel under `NinoBurst.SelfTest`: a combined NEON/SSE2 guard made
standalone Burst 1.8.21 emit `burst_abort` for Linux x64 (`97f686ff`) and Burst
2.0 (Unity 6000.6) abort every `Narrow` from 16 chars on Windows x64. The rule,
why and the CI rows per Burst major: `platforms`, references/mono-unity.md.
The locally inspected Burst 1.8.21 compiler lists AVX2, SSE2, and AArch64
targets and no AVX-512 target. Check the package version in Unity CI before
using a new intrinsic.

Keep validation separate from the Burst permute hook. It receives only the
validated block to move. If adding a strided flag validator, pass the exact
readable byte count and stride and report success or fallback. Let managed code
produce the existing diagnostic on fallback, and rebuild the Unity runtime DLL
when the public hook table changes.

Part of the `optimize` skill: what IL2CPP and Mono compile from Nino's generic code, how the Burst and WebGL
kernels are called and when they pay. Read it before changing a generic formatter, a call that IL2CPP runs per
value, a kernel hook or a kernel threshold. How to build and run Unity is `platforms` and `src/Nino.Unity/AGENTS.md`.

## Generic code under IL2CPP and Mono

- Give a generic formatter the formatter of each type parameter as a type parameter of its own (`Box_1Formatter<T,
  __F_T> where __F_T : struct, INinoFormatter<T>`), never `CachedFormatter<T>`: a closed type names its arguments'
  formatters, so a `T` member is `default(__F_T).Serialize`, a direct call IL2CPP specializes for the value-type
  formatter argument, where `CachedFormatter<T>` is a `VirtualActionInvoker` per value on IL2CPP (its C++ in
  `CachedFormatter_1_Deserialize`, unity.yml run 40818519412) and a virtual call on Mono and NativeAOT, which do not
  devirtualize `static readonly` fields. `[UnsafeAccessor]` externs then move to a class of their own with exactly
  the type's type parameters (the runtime rejects a declaring class with more: "Generic type constraints do not
  match"). Leave RyuJIT's inlining of a closed generic base's subtype switch (`__NinoDispatchN.__ReadSubtype`) alone:
  with per-formatter instances it inlines into the generic `Deserialize` of a box of `(class, int)` (237 to 559
  bytes, the same instructions per element). Keeping it out of line where two closed bases share code reproduced
  main's listings yet read boxes of `(class, int)` 3 to 8% slower on every CPU (run 41005255972), and `NoInlining` on
  every switch made every generic polymorphic deserialize 4 to 18% slower (it also stopped the switch inlining into
  the element loop over value types, `ReadEach` 375 to 124 bytes; run 40973200874). Inlined, the one loss is that
  case on Xeon 8573C, 3.6 to 4.9% (runs 40935400095, 40967495867): code placement, not executed work.
- Never cast `default(TFormatter)` to an interface on a path IL2CPP (or Mono) runs per value: IL2CPP compiles
  `((IFoo)default(TFormatter)).M()` as a GC `Box()` and an `InterfaceFuncInvoker` on every call, where RyuJIT removes
  the box and inlines the struct's method. In the netstandard builds take a boxed instance cached once in a `static
  readonly` field (`NinoBlockPlan<T, TFormatter>.Formatter`, `NinoSized.Of<T, TFormatter>`), and keep the cast under
  `#if NET8_0_OR_GREATER`. Evidence (IL2CPP C++, unity.yml run 40818519412, artifact `il2cpp-cpp-6000.3.27f1`):
  `NinoBlockCollections.WriteEach<(int, long), ...>` boxed the formatter and made an interface call per element whenever
  no Burst kernel ran, and `NinoSized.Is`/`Serialize` boxed one to three times per top-level `Serialize` (87 and 174
  `Box()` sites); after (run 40969941689, exp/devirt-unity) no `Box()` there, a static field load and one interface
  call. The .NET 8+ FullOpts listings stayed identical (1,048 methods). No call at all needs the loop inside the
  formatter (a public `INinoBlockFormatter` member, the owner's decision).

## Native kernels

- Use a prefix-returning kernel for data-dependent nullable-bool normalization, rather than extending the static
  `Permute` table with a mask it cannot express. Keep whole-run presence validation and overlap handling in Core;
  reject an invalid active value before storing its vector group, return the leading slot count, and let Core
  finish the scalar suffix with the same first error and partial writes. Keep the destination after the prefix
  untouched, so a group rejected at any lane can safely be retried. `ae4e5961` and `ed6b9311` add the optional
  `NormalizeBoolSlots` hook and its AVX2/SSE2/NEON implementation. Cached Burst 1.8.21 compiled the production source
  for AVX2, SSE2 and Arm64; the linked Arm64 code passed 620,928 cases across offsets 0–7, vector boundaries,
  dirty nulls, every invalid value position and untouched sentinels. That SDK exposes v128/v256, with no AVX-512 API;
  retain the managed .NET 8+ 512/256/128 implementation rather than inventing a Burst 512-bit path.
  Native throughput alone is not a call threshold: a warmed default-JIT netstandard probe including pinning and
  the indirect call found clear write gains from 32 slots and read gains from 16; write results at 16–17 were
  inconclusive. Use separate internal floors with the existing `MinBlocks` setting. Source plus destination take
  4 bytes per slot: test 16,383/16,384/16,385 slots around the 64 KiB budget, with no temporary buffer. These are
  local candidate measurements, not Unity IL2CPP or x64 runner throughput; full Unity integration remains for CI.
  Move layout checks, overlap checks and prefix completion into out-of-line helpers before judging the missing-hook
  cost. The first inline integration grew netstandard `Write`/`ReadPrechecked` to 543/423 B IL and 580/580 B Arm64
  Tier 1 entries. Splitting the native paths and sharing the scalar bool writer reduced them to 330/306 B IL and
  448/416 B entries (`102d994a`); the old scalar baseline was 353/285 B IL and 404/348 B. Keep both registered/unregistered
  controls: short-input timings still varied, so this establishes a smaller dispatch rather than a universal speedup.
  The standalone native entry is 200 B on Arm64, 283 B with AVX2 and 217 B with SSE2; x64 sizes are cross-compiled
  evidence, not throughput. The full FullOpts comparison against `50d6513d` flagged no regression among 1,101 methods.
- Hold native kernels as function pointers in static fields, never behind a virtual base: `NinoKernels` is a table of
  `delegate* unmanaged[Cdecl]` that `Register` copies into `NinoKernelHook`'s static fields, a missing pointer leaves
  its threshold at `int.MaxValue`, so a call site is one static load, a compare and a `calli` (IL2CPP: a C function
  pointer call), with no object, null check or vtable slot. The abstract class it replaced cost, in IL2CPP's C++ of
  `NarrowWords`, a load of the registered object, a `NullCheck` and `VirtualFuncInvoker3<...>::Invoke(7, ...)` into
  a `PointerKernels_Narrow` that then called the pointer (unity.yml run 40818519412, artifact `il2cpp-cpp-*`).
  Burst gives the pointers (`FunctionPointer<T>.Value`); per call they were 0.3 to 0.6 ns faster than `.Invoke`
  delegates (Narrow16 5.99 against 6.29 ns, Permute1 16.2 against 17.2 on 2022.3, run 40717516438), and the delegate
  fallback is gone: a table only `calli` can call holds nothing else. With the table registered, cases under every
  threshold were on par with no kernels (Latin-1 8 chars, one tuple, 15-char `[NinoUtf8]` text: within 2 %, IL2CPP
  x64, unity.yml run 40822269653).
- Put every `calli` in its own `NoInlining` helper (`NinoKernelHook.TryNarrow`, `NarrowAsciiPrefix`, ...) and keep the
  threshold compare in the caller: classic Mono never inlines a method that contains a `calli`, so a pointer call
  written into `NinoStrings.NarrowWords` stopped it inlining into `WriteLong` and every Latin-1 write paid a call with
  no kernel registered (Mono 6.8 local, 1000 chars 469 against 564 ns; on par once the call moved out, the listing of
  `WriteLong` showing `NarrowWords` inlined again with `MONO_VERBOSE_METHOD`).
- Set each kernel threshold from a sweep that forces the kernels from 1 value or 16 chars, per call site, with
  allocation kept out (`SerializeTo` into a reused writer, `GC.Collect` before each group, groups A B B A, the lower of
  each pair: `benchmark`). The sweep of unity.yml run 40735278928 (IL2CPP x64 players, 2022.3 and 6000.3): blocks of
  tuples gain from 1 to 2 values (1.04 to 1.30x at 1 to 2, 1.5 to 1.9x at 4); Latin-1 strings from 16 chars (1.24 to
  1.55x); text above U+00FF costs up to 10 % at any length (the failed narrowing), so a lower MinChars adds no new loss;
  the padded copy (`NinoLayout.Copy`, whose own path copies masked words) breaks even at 12 to 16 values, hence its own
  floor (`NinoKernelHook.CopyFloor`).
- Build only the mask a call needs: `PermuteVectors` built all sixteen 16-byte masks on the stack before its one-load,
  one-store loop, about 35 ns per call; the table itself as one mask moved the padded copy's break-even from about 24
  values to 12 (0.69 to 0.75x at 1 to 2 values before, 0.75 to 0.80x after; 1.06 to 1.09x at 16).
- On WebGL (wasm SIMD, `Nino.WebGL`) the same kernels pay off only for strings from 16 chars (1.07 to 1.45x, 2 to 10x
  from 1000) and the padded copy from 32 values (2.3 to 5.3x from 1000); blocks of tuples gained nothing consistent
  (Unity 6000.3 and 2022.3, headless Chrome, run 40735278928).
- IL2CPP class formatters: a `SimpleClass` read (3.4 us on 6000.3, 2.1 on 2022.3) is mostly objects and collections
  built, not Nino's code: per member, `Dictionary<int, string>`(10) 1.2 us, `Dictionary<int, int>`(10) 0.9 us (the
  `HashTables` fill is on there: `RuntimePathsTests` asserts every flag), a `List<class>`(10) about 0.1 us per new
  element, the 360-char UTF-8 string 0.65 us; Burst changes the class only through its strings (`Name` 1.2 to 1.4x). The
  generated C++ shows shared generic code where a type argument is a class (`ListFormatter<__Canon, ...>`: an
  `il2cpp_rgctx_method` lookup per call, `il2cpp_codegen_runtime_class_init_inline(Reader)` before Reader calls) but no
  per-element cost that a change in Nino removes was found; time classes of one member each (the run above did) before
  changing a formatter for IL2CPP.
