---
name: platforms
description: "Every platform Nino runs on and how to test each locally - x64 with AVX-512/AVX2/SSSE3 only, Arm64 and Arm32 under qemu (with the flags that keep qemu from crashing), 32-bit and big-endian, no SIMD, WebAssembly, Mono and the net472 build, .NET Framework, Unity IL2CPP and Burst, the netstandard builds and interceptors off. Use when a change adds a platform-specific path, when something fails on one platform only, or before landing a change to a read or write path. Triggers: qemu, Arm32, linux-arm, x86, crossgen2, unaligned, Vector<T>, s390x, big-endian, Mono, net472, IL2CPP, Unity, Burst, DOTNET_EnableHWIntrinsic, netstandard2.0, NinoRegularCalls, 平台, 跨平台, 32位, 大端."
metadata:
  internal: true
---

# Platforms

Every optimization has to hold on each platform Nino runs on: x64 with AVX-512, AVX2 or SSSE3 only, Arm64, Arm32 and
other 32-bit targets, WebAssembly and anything without SIMD (`DOTNET_EnableHWIntrinsic=0`), Mono and Unity (IL2CPP
included), .NET Framework. Fix a problem everywhere it occurs, not only in the case that showed it, and say in the
commit body which platforms a change speeds up and which keep the old path.

Prefer one path for every platform. A platform-specific fast path (an intrinsic, `UnsafeAccessor` on .NET 8+,
`allows ref struct` on .NET 9+, a runtime's internal layout) is allowed only behind `#if` or a startup check, with
the shared path kept as the fallback; say in a comment which platforms take it, and test that both paths write and
read the same bytes (run the suite once with the fast path off: `DOTNET_EnableHWIntrinsic=0`, the `netstandard`
builds of Nino.Core, Mono).

- Under NativeAOT, emit every instruction a run-time plan leads to as that set's own intrinsic, under its own
  `X.IsSupported` test in the method that uses it (`if (Ssse3.IsSupported) return Ssse3.Shuffle(...)`), and give the
  test a fallback that is not a call. Why: ILC compiles x64 for SSE2 and emits a later set only behind its test (a
  check at run time for SSSE3 to SSE4.x, false for AVX and later unless `IlcInstructionSet` raises the baseline),
  while a cross-platform API (`Vector128.ShuffleNative`, `Vector512` operations) takes the form of the baseline; the
  JIT folds the test, so its code does not change. Evidence: plans chosen from `Ssse3.IsSupported` (true at run
  time) led to `Vector128.ShuffleNative`, compiled to its software form: 1,000 `(int, long)` written in 25 us
  against 126 ns under the JIT, read in 18 us against 1.9 us (local, AVX-512 Xeon). Naming `Ssse3.Shuffle` with a
  call fallback still spilled every vector around the call (2.7 us); with `return value` as the never-taken
  fallback, 1.9 us, the JIT's listings unchanged on every ISA. Find such fallbacks in a NativeAOT binary: calls from
  `Nino_Core_*` symbols to `S_P_CoreLib_System_Runtime_Intrinsics_*` (`objdump -d` with the `.dbg` file's symbols);
  dead branches of another architecture show too. `tests/Nino.AotTests` checks the shuffle is `pshufb`. Arm64 is not
  affected (AdvSimd is in its baseline); an Arm64 object can be built on x64 (`dotnet publish -r linux-arm64` stops
  at the link, `obj/.../native/*.o` is there for `llvm-objdump`). What is left: under NativeAOT a `static readonly`
  field a static constructor computes is a load, not a constant, so the plans' offsets are loaded per group and
  `Lanes16` stays a call; the lane paths ran about twice as long as the JIT's with the same instruction sets.

## Running each locally

| Platform | How |
|---|---|
| x64 ISAs | `DOTNET_EnableAVX512=0`, `DOTNET_EnableAVX2=0`, `DOTNET_EnableHWIntrinsic=0` on the test run |
| netstandard builds, interceptors off, Roslyn 4.8 | `-f net8.0 -p:NinoCoreTfm=netstandard2.0` / `2.1`, `-p:NinoRegularCalls=true`, `-p:NinoGeneratorBuild=4.8` (`verify`) |
| Arm64 | build `-r linux-arm64`, run under `qemu-aarch64-static -cpu neoverse-n1` |
| Arm32 | build `-r linux-arm`, run under `qemu-arm-static` with `DOTNET_EnableWriteXorExecute=0` and `DOTNET_INTERNAL_ThreadSuspendInjection=0` |
| Mono | the `net472` build of `tests/Nino.RuntimeTests` under `mono` (also `MONO_GC_DEBUG=verify-before-collections`) |
| .NET Framework 4.8, Windows x86 | CI's "Runtime internals" job (`-p:NinoX86=true` builds the 32-bit exe) |
| Go 386, Arm32, s390x (big-endian) | `runtimes.yml`; locally `GOARCH=386`, `GOARCH=arm` / `s390x` under qemu |
| Rust Arm64 (NEON kernels) | no cross C toolchain needed: `rustup target add aarch64-unknown-linux-musl`, then `CARGO_TARGET_AARCH64_UNKNOWN_LINUX_MUSL_LINKER=rust-lld CARGO_TARGET_AARCH64_UNKNOWN_LINUX_MUSL_RUNNER="qemu-aarch64 -cpu neoverse-n1" cargo test --release --target aarch64-unknown-linux-musl --all-features` (debug: the simd tests ran over an hour; release, all tests, ~5 min); `cargo +nightly miri test --lib --target aarch64-unknown-linux-gnu simd` for the unsafe code; asm with `cargo rustc --release --lib --target aarch64-unknown-linux-gnu -- --emit asm`, cycles with `llvm-mca -mcpu=neoverse-n2` over the executed instruction stream (`optimize-runtimes`, references/strings.md, the per-char decoder bullet) |
| Unity (editor tests, IL2CPP player, Burst) | `unity.yml` only (one license seat: `audit-ci`); `src/Nino.Unity/AGENTS.md` |

- Prove a 32-bit path by its listing, not by a run: qemu-user does not fault on alignment and its timings are
  noise (one build varied 2 to 3 times run to run). Why the listing is enough: CoreCLR on Arm32 compiles an
  unaligned load or store of 8 bytes or more to `ldr`/`str` of 4 bytes (`vmov` for a double; `vldr`/`vstr`/`stm`
  only on object fields and the frame), and x86 and wasm never fault, so `NinoUnsafe` takes its 64-bit paths there
  (`UnalignedWide32`), while Mono and IL2CPP on 32-bit Arm may emit `ldrd`/`vldr` and keep the byte copies. Check
  an Arm32 listing for those instructions on a buffer address (`DOTNET_TieredCompilation=0 DOTNET_JitDisasm=...
  DOTNET_JitStdOutFile=...` under qemu). Linux has no x86 .NET: take an x86 listing from crossgen2
  (`microsoft.netcore.app.crossgen2.linux-x64`, `--targetos:windows --targetarch:x86 --inputbubble`,
  `--codegenopt JitDisasm=...`) with the `System.Private.CoreLib.dll` of `microsoft.netcore.app.runtime.win-x86` as
  reference (with x64's, `IntPtr.Size` folds to 8); it is R2R code, so a `static readonly` field is a load there.
- Measure a struct layout of a target you cannot run the same way, and never assume one rule per field size: a
  `[NoInlining]` method `ref T F(ref S s) => ref s.B` compiles to `lea eax, [ecx+offset]` and `Unsafe.SizeOf<S>()` to
  `mov eax, size` (crossgen2 with `--parallelism 1`, or the listings interleave; `--inputbubble --inputbubbleref` with
  the target's CoreLib so BCL sizes fold). Evidence: Windows x86 .NET 10 aligns `long`, `double` and sequential
  structs holding one to 8 but auto-layout structs (`DateTime`, `ValueTuple`, `DateTimeOffset`) and the 8-byte fields
  inside them to 4; `NinoLayout` once took `{byte; long}` for `DateTime` too and copied `{ int; DateTime }` as 12
  bytes of x86 memory (fixed by a `{byte; DateTime}` probe). How a listing proves a platform test folds is `optimize`'s.
- Take the layout of a BCL struct on a runtime from docs/layouts.md (the measured tables, from the `# layout:` lines
  `tests/Nino.RuntimeTests` prints on every CI runtime), never from memory or from reference assemblies: these declare
  tuples sequential, so the generator once published raw structs' tuples in declaration order while .NET x64 wrote
  them reordered (`RawLayout` now forces `Auto` for `System.ValueTuple`). The canonical layout is the spec's §5.2;
  a runtime that differs measures and converts (`NinoLayout`, `MeasureMemory`, which also reads `Nullable` fields;
  `KeepsDeclarationOrder` detects .NET Framework's reordering).
- Check that every CI runtime writes and reads CoreCLR x64's bytes for each BCL struct: `BclStructs.cs` against the
  one fixture file `src/Nino.Unity/Assets/Test/Runtime/BclFixtures.cs`, which Unity's `BclLayoutTests` read too, in
  play mode and the IL2CPP player.
- Check a change to `NinoLayout` / `NinoLayoutCore` against random shapes, not only the listed ones: generate nested
  value tuples of every leaf (with `Nullable`, `KeyValuePair`, `DateTimeOffset`, `nint`) inside a sequential struct,
  fill them through reflection, `NinoSerializer.Serialize` them and diff the hex across runtimes (.NET 10, .NET 8 and
  Mono's net472 build on x64, the self-contained `linux-arm64` publish under qemu); in-process, `NinoLayout.Compute`
  with `measured: true` must give the model's result for every shape on x64. Round 104: 5,000 shapes wrote the same
  bytes on all four, and the measured path threw for a top-level `T?` (created as null by `Activator`, so
  `MeasureMemory` takes its flag and value from `NullableValue`).
- Never measure a vector by its declared fields: `Vector<T>`'s are two `ulong`s whatever its size (16, 32 or 64
  bytes, chosen by the CPU; the spec's §3 lists which host has which), and Nino did, writing 16 bytes and dropping the
  upper half on AVX2 hosts. A type whose size depends on the host is never serialized (`wire-format` owns that rule;
  NINO029 is `audit-analyzer`'s). `Vector64` to `Vector512` are fixed size: aligned 8/16/32/64 on x64, 16/16/16 on
  Arm64, 8 on Arm32 (`NinoLeaf.Vector`).
- Test every `Vector<T>` width on one x64 machine: `DOTNET_EnableAVX2=0` (16 bytes), the default (32),
  `DOTNET_MaxVectorTBitWidth=512` (64); `tests/Nino.RuntimeTests` prints `# Vector<float>.Count`. CI covers all three:
  the no-AVX2 unit tests (16), the default (32), and 64 in the net10.0 unit-test job (`VectorTTests`) and the Linux
  runtime-internals job (Nino.RuntimeTests, which must print Count 16). GitHub's x64 runners are Xeon 6973P-C
  (AVX-512) or EPYC 7763 (none): a check that needs AVX-512 asks `.github/scripts/has_avx512.sh` (F, BW, CD, DQ, VL,
  as .NET needs) and posts a notice instead of failing without it.
- Never read a pointer field by reflection: `FieldInfo.GetValue` on `IntPtr`'s `void*` crashed Mono 6.8 with SIGSEGV.
- Read every field type a layout needs through `NinoReflection.LayoutFieldType`: a function pointer (`delegate*`) is a
  pointer, 8 bytes zero-extended as the generator publishes it, but .NET 8 and later report the field as a function
  pointer type (`IsFunctionPointer`; `IsClass` true, `IsValueType` false, no base type), which `NinoLayout` took for
  unsupported and copied as it lay in memory (4 bytes on 32-bit), and .NET Framework and .NET 7 report `IntPtr`
  (sign-extended), which only the field's signature tells apart (`Module.ResolveSignature` of its FieldDef token,
  ELEMENT_TYPE_FNPTR 0x1B after any modifier; Mono resolves only StandAloneSig tokens, IL2CPP none, so there it stays
  an `IntPtr`). Mono 6.8 overflows its stack loading any struct with a `delegate*` field, even one only a generated
  registry names: tests/Nino.RuntimeTests declares its case in net10.0 only (r105: ok on x64 and on Arm32 under qemu).
- Run the unit suite under qemu-arm by calling its `[TestMethod]`s by reflection from a small program run by the
  Arm32 `dotnet` (`dotnet test` starts its host with the machine's own runtime); leave out the Roslyn compilations,
  too slow emulated: `Generator` and `ShapeMatrixTests` (in `Corner/` but namespace `Nino.UnitTests`, so a filter on
  `Corner` misses it: it ran over 50 minutes and looked like a hang; analyzer and code fix tests take 9 minutes).
  Under `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1` only the two `BclTypeTests` culture tests fail (and
  `IssueTestTemplate`, an abstract class the runner cannot create). Print each class as it starts, so a slow one
  shows by name.
- Gate every netstandard `Vector<T>` path per operation, never on `Vector.IsHardwareAccelerated` alone: classic Mono's
  JIT (mono/mono 6.x and Unity-Technologies/mono, `vector_t_intrinsics` in mono/mini/simd-intrinsics.c) accelerates
  only `Vector<T>`'s own operators (`+ - * / & | ^`), constructors (`T`, `T[]`), `Equals`, signed comparisons,
  `CopyTo`, `Zero`/`AllOnes`/`Count`, and only for the type in the System.Numerics.Vectors package (class-init.c marks
  `Vector`1` a SIMD type only in an assembly of that name), on x86 and x64 only. Static `Vector` methods that do not
  inline into those (`Narrow`, `Widen`, `ConditionalSelect`, `AndNot`, `~`) run the package's software form, and
  `Vector<T>` from Mono's own mscorlib (Unity's too) is never accelerated, though `IsHardwareAccelerated` was true on
  Unity 2022.3.47f1 and 6000.0.20f1 (UUM-77339; Unity reverted that change in its fork in February 2025). So the text
  kernels (`Narrow`/`Widen`) take `NinoUnsafe.NumericVectors` (.NET Framework and .NET Core only) and the padding
  masks (loads, stores, `&`) `NinoUnsafe.VectorMasks` (also classic Mono with the package). Evidence, Mono 6.8 on x64
  (local): 1,024 chars narrowed in 5,400 ns with `Vector.Narrow` against 550 for the 8-byte loop, widened in 6,700
  against 400; `Vector.EqualsAll` inlines to `Equals` and is fast (the check before Narrow in 120 ns); an array of
  1,000 padded 16-byte structs serialized in 8.4 to 9.2 µs with `Vector<byte>` masks against 11.8 to 13.4 with ulongs
  (36 to 40 with `Unsafe.ReadUnaligned` loads, see `optimize`). Test a new operation on Mono with
  `MONO_VERBOSE_METHOD=<method> mono app.exe` (prints the IR, "SIMD intrinsic ..." per accelerated call, and the
  machine code through objdump) before gating it on.

Why the qemu flags:
- Under qemu-arm (Arm32), set `DOTNET_EnableWriteXorExecute=0`: with W^X the runtime writes dynamic methods
  (`Expression.Compile`, reflection invoke stubs) through a second mapping qemu-user does not see, and they crash.
  Set `DOTNET_INTERNAL_ThreadSuspendInjection=0` too: qemu (8.2 and 10.2) aborts in about one run in four
  (`thumb_tr_translate_insn: Assertion (dc->base.pc_next & 1) == 0`) when the GC's suspension signal lands.
- Run Go's Arm32 tests under qemu-arm with `GOMAXPROCS=1`: with several Ps, Go 1.27.3's runtime crashed in the
  garbage collector's lock-free lists (`runtime.(*lfstack).pop` SIGSEGV at address 0, during any test) in 2 to 5 of
  8 runs with qemu 8.2 and the runners' qemu, and main's code before the run that showed it (f618b973) crashed too
  (1 of 10, more allocation only made it likelier); Go 1.24 crashed in 0 of 8, `GOMAXPROCS=1` in 0 of 18, `GOARM=6`
  did not help (4 of 8). runtimes.yml sets it on that step (r101).
- To run tests under qemu-aarch64, pass `-cpu neoverse-n1` (listings can keep the default): qemu 8.2's default CPU
  (`max`) has LRCPC2, whose `ldapur`/`stlur` (what the optimizing JIT emits for `Volatile.Read`/`Write` of a field) it
  emulates wrong. With or without Nino's code, runs crashed (`ToImmutableDictionary`), or a static field written by
  `Volatile.Write` read as never written elsewhere; `DOTNET_TieredCompilation=0` moves the failures, it does not
  avoid them. Nino.Benchmark under qemu-aarch64 also needs the arm64 `libssl.so.3` and `libcrypto.so.3` in
  `/usr/aarch64-linux-gnu/lib`, else it aborts with "No usable version of libssl" (round 101). A self-contained Arm64
  publish run with `qemu-aarch64-static -cpu neoverse-n1 -L /usr/aarch64-linux-gnu` aborts at startup without an
  arm64 libicu ("Couldn't find a valid ICU package"): set `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1`
  (tests/Nino.RuntimeTests ran ALL OK so, Vector<T> round). For Tier1 listings with tiering on under qemu, the jitwarm hook's 8 rounds of 200 ms reach no Tier1 at all (every listing Tier0):
  give it about 30 rounds of 1.5 s, and skip classes whose static constructor throws, as `BclTypesTest` does under
  `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1` (`en-us`), which otherwise aborts the process (round 102).

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
- WebGL has no Burst: Burst compiles jobs with LLVM into a native library the player calls, and the WebGL player (IL2CPP
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
- The Burst kernels (`src/Nino.Unity/Packages/com.jasonxudeveloper.nino/Burst/NinoBurst.cs`) write the same bytes as
  Nino.Core; their golden-bytes tests (`Assets/Test/Runtime/Burst/BurstKernelTests.cs`) change with any wire-format
  change (`wire-format`).
