# Unity: Burst kernels and IL2CPP

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
