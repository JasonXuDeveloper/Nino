# Mono, Unity, IL2CPP and WebGL

Part of the `platforms` skill: what classic Mono's JIT accelerates, and what Unity's players (IL2CPP, HybridCLR,
WebGL) lack or do differently. Read it before a netstandard `Vector<T>` path, a static initializer of a generic
cache, a decision from attributes at run time, or a change to the Burst or WebGL kernels. Unity's own files and
tests: `src/Nino.Unity/AGENTS.md`; the kernels' techniques: `optimize`, references/unity.md.

## Classic Mono

- Gate every netstandard `Vector<T>` path per operation, never on `Vector.IsHardwareAccelerated` alone: classic Mono's
  JIT (mono/mono 6.x and Unity-Technologies/mono, `vector_t_intrinsics` in mono/mini/simd-intrinsics.c) accelerates
  only `Vector<T>`'s own operators (`+ - * / & | ^`), constructors (`T`, `T[]`), `Equals`, signed comparisons,
  `CopyTo`, `Zero`/`AllOnes`/`Count`, and only for the type in the System.Numerics.Vectors package (class-init.c marks
  `Vector`1` a SIMD type only in an assembly of that name), on x86 and x64 only. Static `Vector` methods that do not
  inline into those (`Narrow`, `Widen`, `ConditionalSelect`, `AndNot`, `~`) run the package's software form, and
  `Vector<T>` from Mono's own mscorlib (Unity's too) is never accelerated, though `IsHardwareAccelerated` was true on
  Unity 2022.3.47f1 and 6000.0.20f1 (UUM-77339; Unity reverted that change in its fork in February 2025). So the text
  kernels (`Narrow`/`Widen`) take `NinoUnsafe.NumericVectors` (.NET Core only) and the padding
  masks (loads, stores, `&`) `NinoUnsafe.VectorMasks` (also classic Mono with the package). Evidence, Mono 6.8 on x64
  (local): 1,024 chars narrowed in 5,400 ns with `Vector.Narrow` against 550 for the 8-byte loop, widened in 6,700
  against 400; `Vector.EqualsAll` inlines to `Equals` and is fast (the check before Narrow in 120 ns); an array of
  1,000 padded 16-byte structs serialized in 8.4 to 9.2 µs with `Vector<byte>` masks against 11.8 to 13.4 with ulongs
  (36 to 40 with `Unsafe.ReadUnaligned` loads, see `optimize`). Test a new operation on Mono with
  `MONO_VERBOSE_METHOD=<method> mono app.exe` (prints the IR, "SIMD intrinsic ..." per accelerated call, and the
  machine code through objdump) before gating it on.

## Unity and IL2CPP

- No module initializers on HybridCLR: registration goes through the generated registration calls.
- Never decide anything at run time from custom attributes on generic parameters: IL2CPP keeps none (Unity 2022.3 and
  6000.3 players: `typeof(UnmanagedFormatter<>).GetGenericArguments()[0].IsDefined(typeof(NinoRawAttribute))` is
  false, true in the editor's Mono), so the resolver bound the raw list formatter to `List<(byte, long, int)>` and the
  player could not read CoreCLR's bytes (unity.yml run 41270331318). `NinoParameterMarks` falls back to registered marks
  there: Nino.Core registers its own formatters', and each generated registry (its static constructor, before
  `NinoRegistry.Add` hands the assembly's `[assembly: NinoFormatter]` registrations to the resolver) those of the generic
  formatters its assembly registers (`FormatterRules.OwnMarks`; Unity's `UserRawFormatterIsBoundByItsMarks`). Any new
  mark on a type parameter goes into both. A check over attributes on types or members is fine.
- Never throw from the static initializer of a generic cache the top-level calls name (`NinoLayout<T>`, `NinoRaw<T>`,
  `NinoTopFormatter<T>`): Mono runs a `beforefieldinit` class's initializer when a method naming it is compiled, not
  when the field is read, so a refusal there escapes as `TypeInitializationException` from code that never took that
  branch. A `NotSupportedException` thrown by `NinoLayout.Create` for `Vector<T>` reached `NinoSerializer.Serialize`
  through `NinoRaw<T>`'s initializer on Mono 6.14 (`tests/Nino.RuntimeTests`, net472), while CoreCLR threw it from the
  resolver as meant. Give such a type a layout that is neither raw nor sized, and throw where its formatter is
  resolved (`NinoReflection.RefuseHostSizedVector`) and, for code that skips the resolver (a hand-made
  `UnmanagedFormatter<T>`, `Writer.Write<T>`), on the layout's converted paths behind a `static readonly bool`
  (`NinoLayout<T>.HoldsHostSizedVector`): a JIT constant, folded away on canonical types (`asm.py compare`: no method
  changed), where the layout of no size threw `NullReferenceException` before.
- IL2CPP has no `Reflection.Emit` and shares generic code: no runtime code generation, and the mirror-class rules of
  `runtime-internals` (static code only).
- WebGL had no Burst until Burst 1.8.26 (Web) and Unity 6000.6 (WebAssembly64), which run no kernel of Nino's (next
  bullet but one): Burst compiles jobs with LLVM into a native library the player calls, and the WebGL player (IL2CPP
  to C++, Emscripten to one wasm module) has no slot for it, so `[BurstCompile]` is ignored there and the code runs as
  IL2CPP output (Unity staff, discussions.unity.com/t/burst-for-webgl/849368, 2021-2024: not on the roadmap). IL2CPP has
  no `System.Runtime.Intrinsics` either. WebGL's kernels are therefore C (`Packages/.../WebGL/nino_wasm.c`, a source
  plugin Unity's Emscripten compiles into the player, called through `[DllImport("__Internal")]`), with SIMD only under
  `__wasm_simd128__`: Unity 6's "Target WebAssembly 2023" (`PlayerSettings.WebGL.wasm2023`, Unity 2023.2+) compiles the
  whole player, plugins included, with `-msimd128`. Unity 2022.3 has no such setting and supports Safari 15+ (its WebGL
  browser compatibility page), while wasm SIMD needs Safari 16.4 (caniuse "wasm-simd"; MDN browser-compat-data
  `webassembly.simd`): a 2022.3 player built with SIMD would not load there, so Nino never turns it on; a project may
  (`--compiler-flags=-msimd128` in Additional IL2CPP Arguments reaches the plugin's compile too). Write the C with
  Clang's vector extensions and `__builtin_wasm_*`, never `#include <wasm_simd128.h>`: 2022.3's Emscripten 3.1.8 sysroot
  has a wrapper of it whose `#include_next` finds nothing in Unity's LLVM, and the build fails (`fatal error:
  'wasm_simd128.h' file not found`, unity.yml run 40717516438); with vector extensions the 2022.3 player builds, loads
  in Chrome and passes (run 40735278928). The WebGL player collects garbage only between frames: a test or benchmark
  that allocates must yield a frame now and then (a coroutine), or the heap fills within one frame (`OOM`, then "memory
  access out of bounds").
- The Burst kernels (`src/Nino.Unity/Packages/com.jasonxudeveloper.nino/Burst/NinoBurst*.cs`) write the same bytes as
  Nino.Core; their golden-bytes tests (`Assets/Test/Runtime/Burst/BurstKernelTests.cs`) change with any wire-format
  change (`wire-format`).
- Treat every Burst version as a new platform: the same kernel source compiles differently from one Burst to the next,
  so test each in its own `if` per instruction set whose block only calls and returns (`NinoBurst.Vectors.cs`, the
  header: never `Arm.Neon.IsNeonSupported || X86.Sse2.IsSse2Supported`, never code after an ISA block that another
  path reaches), keep every kernel under `NinoBurst.SelfTest` (each one through its pointer against Nino.Core's scalar
  code at registration; one that throws or differs is left out with a warning naming it and the Burst version), and
  give each Burst major the package supports a row in unity.yml (`audit-ci`) before the package claims it. Why: Burst's
  `CheckIntrinsicUsageTransform` (identical in 1.8.29 and 2.0, decompiled from each) marks each block with the
  instruction set of the first IsXxxSupported edge that reaches it (its visited set compares any two known sets as
  equal, so a second edge is ignored) and replaces a block marked with a set the target lacks by `throw null`, which
  surfaces as `burst_abort` / "This Exception was thrown from a function compiled with Burst" (InvalidOperationException,
  no message). Under Unity 6000.6's built-in Burst 2.0 (`Editor/Data/Tools/Burst`, the compiler now a .NET 8 process
  `bcl.dll` where 1.8 compiled in the editor's Mono) the vector block of `Narrow`'s `(Neon || Sse2) && length >= 16`
  kept Neon on Windows x64: every [NinoType] string of 16 chars or more threw (rc.9 user report); Burst 1.8's editor
  passed the same code, and standalone Burst 1.8.21 had already aborted the old combined Interleave32 guard on Linux
  x64 (97f686ff). In players Burst aborts the process on a throw, so the self-test catches only wrong bytes there:
  the form and the CI rows are what keep throws out. Unity 6000.6 also turns Burst on for Web (WebAssembly64) and
  Burst 1.8.26 for Web players: the kernels' `InstructionSet` returns 0 there and nothing registers, Nino.WebGL's C
  kernels stay the Web path.
