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
  To run the unit suite under qemu-arm, call its `[TestMethod]`s by reflection from a small program run by the Arm32
  `dotnet` (`dotnet test` starts its host with the machine's own runtime); leave out the Roslyn compilations,
  too slow emulated: `Generator` and `ShapeMatrixTests` (in `Corner/` but namespace `Nino.UnitTests`, so a filter on
  `Corner` misses it: it ran over 50 minutes and looked like a hang; analyzer and code fix tests take 9 minutes).
  Under `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1` only the two `BclTypeTests` culture tests fail (and
  `IssueTestTemplate`, an abstract class the runner cannot create). Print each class as it starts, so a slow one
  shows by name.
- Gate every netstandard `Vector<T>` path on `NinoUnsafe.NumericVectors`, never on `Vector.IsHardwareAccelerated`
  alone: Mono 6.8 on x64 reports it true with the System.Numerics.Vectors package Nino.Core brings (false with
  Mono's own assembly), yet runs `Vector<T>` several times slower than scalar code (`Vector.Narrow` of 1,024 chars
  5,478 ns against 2,838; `NinoMask` padding of 1,000 `Padded` structs 36 to 40 µs against 11 to 13 with ulongs).
  The gate adds the runtime: `FrameworkDescription` starting with ".NET" (.NET Framework, .NET Core; Mono and
  IL2CPP say "Mono").

Why the qemu flags:
- Under qemu-arm (Arm32), set `DOTNET_EnableWriteXorExecute=0`: with W^X the runtime writes dynamic methods
  (`Expression.Compile`, reflection invoke stubs) through a second mapping qemu-user does not see, and they crash.
  Set `DOTNET_INTERNAL_ThreadSuspendInjection=0` too: qemu (8.2 and 10.2) aborts in about one run in four
  (`thumb_tr_translate_insn: Assertion (dc->base.pc_next & 1) == 0`) when the GC's suspension signal lands.
- To run tests under qemu-aarch64, pass `-cpu neoverse-n1` (listings can keep the default): qemu 8.2's default CPU
  (`max`) has LRCPC2, whose `ldapur`/`stlur` (what the optimizing JIT emits for `Volatile.Read`/`Write` of a field) it
  emulates wrong. With or without Nino's code, runs crashed (`ToImmutableDictionary`), or a static field written by
  `Volatile.Write` read as never written elsewhere; `DOTNET_TieredCompilation=0` moves the failures, it does not
  avoid them.

## Unity and IL2CPP

- No module initializers on HybridCLR: registration goes through the generated registration calls.
- IL2CPP has no `Reflection.Emit` and shares generic code: no runtime code generation, and the mirror-class rules of
  `runtime-internals` (static code only).
- WebGL has no Burst: Burst compiles jobs with LLVM into a native library the player calls, and the WebGL player
  (IL2CPP to C++, Emscripten to one wasm module) has no slot for it, so `[BurstCompile]` is ignored there and the code
  runs as IL2CPP output (Unity staff, discussions.unity.com/t/burst-for-webgl/849368, 2021-2024: not on the roadmap).
  IL2CPP has no `System.Runtime.Intrinsics` either, so WebGL takes the scalar paths; the only SIMD there is what
  Emscripten auto-vectorizes when the project enables the WebAssembly 2023 feature set (wasm SIMD128).
- The Burst kernels (`src/Nino.Unity/Packages/com.jasonxudeveloper.nino/Burst/NinoBurst.cs`) write the same bytes as
  Nino.Core; their golden-bytes tests (`Assets/Test/Runtime/Burst/BurstKernelTests.cs`) change with any wire-format
  change (`wire-format`).
