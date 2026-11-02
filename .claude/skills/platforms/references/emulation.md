# Emulated and cross-compiled targets

Part of the `platforms` skill: proving 32-bit and x86 code by its listing, measuring a layout of a target you
cannot run, running the unit suite under qemu-arm, and the qemu and runtime flags that keep emulated runs from
crashing. Read it before running anything under qemu or taking a listing for Arm32, Arm64 or x86.

## Listings instead of runs

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
  bytes of x86 memory (fixed by a `{byte; DateTime}` probe). How a listing proves a platform test folds is `optimize`'s (references/jit.md, "Tests that must fold").

## Running under qemu

- Run the unit suite under qemu-arm by calling its `[TestMethod]`s by reflection from a small program run by the
  Arm32 `dotnet` (`dotnet test` starts its host with the machine's own runtime); leave out the Roslyn compilations,
  too slow emulated: `Generator` and `ShapeMatrixTests` (in `Corner/` but namespace `Nino.UnitTests`, so a filter on
  `Corner` misses it: it ran over 50 minutes and looked like a hang; analyzer and code fix tests take 9 minutes).
  Under `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1` only the two `BclTypeTests` culture tests fail (and
  `IssueTestTemplate`, an abstract class the runner cannot create). Print each class as it starts, so a slow one
  shows by name.

## Why the qemu flags

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
