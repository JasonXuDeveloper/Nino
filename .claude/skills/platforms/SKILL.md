---
name: platforms
description: "Every platform Nino runs on and how to test each locally - x64 with AVX-512/AVX2/SSSE3 only, Arm64 and Arm32 under qemu (with the flags that keep qemu from crashing), 32-bit and big-endian, no SIMD, WebAssembly, Mono and the net472 build, Unity IL2CPP and Burst, the netstandard2.1 build and interceptors off. Use when a change adds a platform-specific path, when something fails on one platform only, or before landing a change to a read or write path. Triggers: qemu, Arm32, linux-arm, x86, crossgen2, unaligned, Vector<T>, s390x, big-endian, Mono, net472, IL2CPP, Unity, Burst, DOTNET_EnableHWIntrinsic, netstandard2.1, netstandard2.0, .NET Framework, MissingMethodException, NinoRegularCalls, 平台, 跨平台, 32位, 大端."
metadata:
  internal: true
---

# Platforms

Every optimization has to hold on each platform Nino runs on: x64 with AVX-512, AVX2 or SSSE3 only, Arm64, Arm32 and
other 32-bit targets, WebAssembly and anything without SIMD (`DOTNET_EnableHWIntrinsic=0`), Mono and Unity (IL2CPP
included). .NET Framework is not one: the runtime packages target netstandard2.1, net8.0 and net10.0 (the owner
dropped netstandard2.0 in rc.11; only the Roslyn components, generator, analyzer and code fixes, stay netstandard2.0,
as Roslyn hosts require). Fix a problem everywhere it occurs, not only in the case that showed it, and say in the
commit body which platforms a change speeds up and which keep the old path.

Prefer one path for every platform. A platform-specific fast path (an intrinsic, `UnsafeAccessor` on .NET 8+,
`allows ref struct` on .NET 9+, a runtime's internal layout) is allowed only behind `#if` or a startup check, with
the shared path kept as the fallback; say in a comment which platforms take it, and test that both paths write and
read the same bytes (run the suite once with the fast path off: `DOTNET_EnableHWIntrinsic=0`, the netstandard2.1
build of Nino.Core, Mono).

- Give a public member one signature in every build of Nino.Core, or leave it out of the build that cannot have it:
  never another return type in netstandard2.1 than in net8.0. Why: NuGet hands a .NET application the net8.0 or
  net10.0 build while a netstandard library it references was compiled against the netstandard2.1 one, so the library
  calls a method that does not exist at run time (`MissingMethodException`); a member left out fails at the library's
  compile time instead, and can be added later without a break. Evidence: rc.10's async Stream overloads were
  `ValueTask` and left out of the then netstandard2.0 build (no `ValueTask` there) rather than typed `Task`.
- In Nino.Core, never index a `ReadOnlySpan<T>` (`span[i]`, or `foreach`, which the compiler lowers to the indexer):
  take `ref MemoryMarshal.GetReference(span)` and `Unsafe.Add`. Why: the netstandard2.1 build calls the indexer with
  `modreq(InAttribute)` on its `ref readonly` return, and stock Mono 6.8's mscorlib (Ubuntu's `mono-complete`)
  declares it without one, so the call throws `MissingMethodException` there. Evidence: rc.11, the first Mono run of
  the netstandard2.1 build, failed 27 `Around<(...)>[]` cases in `Writer.WriteUnmanagedRaw` and would have failed
  `NinoSequences`' header read; find such calls with `monodis Nino.Core.dll | grep 'ReadOnlySpan`1.*get_Item'`.
  `Span<T>`'s indexer returns a plain `ref` and is safe.
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
| x64 ISAs | `DOTNET_EnableAVX512=0`, `DOTNET_EnableAVX2=0`, `DOTNET_EnableHWIntrinsic=0` on the test run (this last one leaves .NET 10's baseline `X86Base` supported: an unguarded `X86Base.CpuId` ran under it and failed only on Arm64, round 129; an x86-only call is proven guarded only on Arm64) |
| netstandard2.1 build, interceptors off, Roslyn 4.8 | `-f net8.0 -p:NinoCoreTfm=netstandard2.1`, `-p:NinoRegularCalls=true`, `-p:NinoGeneratorBuild=4.8` (`verify`) |
| Arm64 | build `-r linux-arm64`, run under `qemu-aarch64-static -cpu neoverse-n1` |
| Arm32 | build `-r linux-arm`, run under `qemu-arm-static` with `DOTNET_EnableWriteXorExecute=0` and `DOTNET_INTERNAL_ThreadSuspendInjection=0` |
| Mono | the `net472` build of `tests/Nino.RuntimeTests` under `mono` (also `MONO_GC_DEBUG=verify-before-collections`): compiled against Mono's class libraries (`/usr/lib/mono/4.5/`, `-p:MonoLibraries=` elsewhere) and the netstandard2.1 builds, made only where they are installed (`apt-get install mono-complete`) |
| Mono 32-bit (x86) | Mono 6.12's i386 debs extracted, run through their own `ld-linux.so.2` with `MONO_PATH` (references/layouts.md, the `delegate*` bullet) |
| .NET on Windows x64 and x86 | CI's "Runtime internals" job (the net10.0 build, with an x86 .NET runtime beside the SDK) |
| Go 386, Arm32, s390x (big-endian) | `runtimes.yml`; locally `GOARCH=386`, `GOARCH=arm` / `s390x` under qemu |
| Rust Arm64 (NEON kernels) | no cross C toolchain needed: `rustup target add aarch64-unknown-linux-musl`, then `CARGO_TARGET_AARCH64_UNKNOWN_LINUX_MUSL_LINKER=rust-lld CARGO_TARGET_AARCH64_UNKNOWN_LINUX_MUSL_RUNNER="qemu-aarch64 -cpu neoverse-n1" cargo test --release --target aarch64-unknown-linux-musl --all-features` (debug: the simd tests ran over an hour; release, all tests, ~5 min); `cargo +nightly miri test --lib --target aarch64-unknown-linux-gnu simd` for the unsafe code; asm with `cargo rustc --release --lib --target aarch64-unknown-linux-gnu -- --emit asm`, cycles with `llvm-mca -mcpu=neoverse-n2` over the executed instruction stream (`optimize-runtimes`, references/strings.md, the per-char decoder bullet) |
| Unity (editor tests, IL2CPP player, Burst) | `unity.yml` only (one license seat: `audit-ci`); `src/Nino.Unity/AGENTS.md` |


## Traps, by topic

Each line is the rule; the reference file holds it in full with the evidence. Read the file for the platform you are
about to run or change: most of these cost a session before they were written down.

- **Emulated and cross-compiled targets** ([references/emulation.md](references/emulation.md)): prove a 32-bit path by
  its listing, not by a run under qemu; take x86 listings and layouts from crossgen2 with the target's CoreLib; run
  the unit suite under qemu-arm by reflection, without the Roslyn tests; set `DOTNET_EnableWriteXorExecute=0` and
  `DOTNET_INTERNAL_ThreadSuspendInjection=0` under qemu-arm, `GOMAXPROCS=1` for Go there, `-cpu neoverse-n1` under
  qemu-aarch64, plus the libssl, ICU and jitwarm settings.
- **Struct layouts and vectors** ([references/layouts.md](references/layouts.md)): take a BCL struct's layout from
  docs/layouts.md, never from memory or reference assemblies; check every CI runtime against CoreCLR x64's bytes
  (`BclFixtures.cs`) and `NinoLayout` against random shapes; never measure a vector by its declared fields, and test
  every `Vector<T>` width on one machine; never read a pointer field by reflection; take what the generator sees
  (`delegate*` fields) from generated code, not from reflection, and read field types through
  `NinoReflection.LayoutFieldType`; run 32-bit Mono from extracted debs.
- **Mono, Unity, IL2CPP and WebGL** ([references/mono-unity.md](references/mono-unity.md)): gate every netstandard
  `Vector<T>` operation on what Mono accelerates, not on `IsHardwareAccelerated`; no module initializers on HybridCLR;
  no run-time decisions from attributes on generic parameters (IL2CPP keeps none); never throw from a generic cache's
  static initializer; no runtime code generation under IL2CPP; WebGL has no Burst (C kernels, SIMD only under
  `__wasm_simd128__`, vector extensions instead of `wasm_simd128.h`); the Burst kernels write Nino.Core's bytes.
