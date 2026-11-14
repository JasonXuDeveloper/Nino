# Nino.Unity: file map

> Read when: changing the Unity package (com.jasonxudeveloper.nino), its Burst kernels, the Unity tests or src/Nino.UnityTypes.

A Unity 2022.3 project (ProjectSettings/ProjectVersion.txt: 2022.3.62f2) holding the OpenUPM package
`com.jasonxudeveloper.nino` and the tests Unity runs on it. The package ships prebuilt DLLs: the netstandard2.1
Nino.Core, the Roslyn 4.3 generator, the code fixes and Nino.UnityTypes. Only the Unity tests touch it (paths below);
`audit-code` audits this directory; `optimize` applies to the Burst kernels.

```
.gitignore                   Unity's own ignores (Library/, Temp/, Logs/, ...); the root .gitignore does not cover them
.vsconfig                    Visual Studio's Unity workload
ProjectSettings/             the project's settings and Unity version; BurstAotSettings_StandaloneLinux64.json (the
                             Burst targets of the Linux player CI builds)
Packages/
  manifest.json              the test project's packages: Burst 1.8.29, test framework, performance testing, Rider
  packages-lock.json         their resolved versions
  com.jasonxudeveloper.nino/ the package OpenUPM publishes
    package.json             name, version (X.Y.Z-preview.N for a pre-release, set by bump_version.py), unity 2022.3
    Runtime/                 Nino.Core.dll (netstandard2.1), Nino.Generator.dll (Roslyn 4.3), Nino.Generator.CodeFixes.dll,
                             Nino.UnityTypes.dll; ThirdParty/: System.Runtime.CompilerServices.Unsafe (the only
                             assembly Nino.Core's netstandard2.1 build references besides netstandard)
    Burst/                   Nino.Burst, one partial class NinoBurst: Burst-compiled NinoKernels (AVX2 / SSSE3 /
                             AArch64 Neon with scalar branches), compiled only with com.unity.burst 1.8+ (NINO_BURST,
                             versionDefines; NINO_BURST_2 from 2.0, Unity 6000.6's built-in Burst); Nino.Core calls
                             Burst's function pointers directly (delegate* unmanaged)
      NinoBurst.cs           the platforms and call form (header comment), Register at startup (none registered where
                             the pointers cannot be called; a kernel that fails the self-test left out), Isa,
                             InstructionSet
      NinoBurst.SelfTest.cs  SelfTest: every kernel through its pointer on inputs that take every branch, byte for
                             byte against Nino.Core's scalar code; one that throws or differs is left out with a warning
                             naming it and the Burst version
      NinoBurst.Permute.cs   Permute (raw blocks by a byte table; 32-byte AVX2 and 16-byte paths)
      NinoBurst.Text.cs      Narrow, Widen (Latin-1 strings), NarrowAscii, WidenAscii ([NinoUtf8]'s leading ASCII)
      NinoBurst.Interleave.cs  Interleave32 (two int columns written as pairs)
      NinoBurst.BoolSlots.cs NormalizeBoolSlots (nullable bool slots, with a checked prefix return)
      NinoBurst.Vectors.cs   the 16-byte vector operations the kernels share, one instruction each per ISA; its header
                             holds the form every kernel keeps (one instruction set per test, a call and a return)
    WebGL/                   Nino.WebGL (WebGL players only): nino_wasm.c (the kernels in WebAssembly SIMD, a source
                             plugin Emscripten compiles into the player; nino_wasm_kernels hands out their pointers) and
                             NinoWasm.cs, which registers them when the player has SIMD (Unity 6's WebAssembly 2023, or
                             -msimd128 on 2022.3). WebGL only: IL2CPP and Mono players without Burst keep the scalar
                             paths (the owner's call: users install Burst)
Assets/Test/                 the tests (asmdefs: Test, Model, CrossRef, Editor.Tests, Runtime.Tests, Burst.Tests,
                             Burst.Editor.Tests, WebGL.Bench, WebGL.Bench.Editor)
  Data.cs                    the shared [NinoType]s (Test assembly)
  Editor/Model/, CrossRef/   [NinoType]s split across two assemblies (cross-assembly references)
  Editor/Tests/              edit-mode tests: primitives, NinoTypes, collections, UnityTypesTests (Nino.UnityTypes against
                             Unity's own classes), UsageTests (the v4 API through Unity's compiler and the 4.3 generator,
                             no interceptors), RuntimePathsInEditMode
  Runtime/                   RuntimePathsTests (CollectionViews, ObjectTypes.Retype, SortedSetNodes, ReadLinked,
                             ConcurrentTables against the public API), BclLayoutTests (BCL structs in raw structs, and
                             tuples and pairs, and the fixed lanes that replace a refused Vector<T>, against
                             CoreCLR's bytes in BclFixtures.cs, of the types in BclFixtureTypes.cs (both shared with
                             tests/Nino.RuntimeTests, which links them and rewrites BclFixtures.cs: --write-fixtures);
                             a [NinoRaw] formatter of the user's bound at run time by the marks its registry
                             registers; logs this runtime's layouts), FunctionPointerTests (C# 9 delegate* fields in a
                             raw struct: 8 bytes zero-extended from the registry's registration, a pointer taken with &
                             still callable after the round trip), RuntimeGenericTests (closed generic [NinoType]s
                             made only with MakeGenericType, through the boxed API: IL2CPP runs them by full generic
                             sharing), run in play mode and in the IL2CPP player
  Runtime/Burst/             BurstKernelTests (the kernels against the scalar paths, byte for byte; asserts the pointer
                             calls, that every kernel passed the self-test, and that the self-test leaves out a wrong
                             kernel, and in the editor a throwing one), BurstBenchmark (only in the release
                             benchmark's player: NINO_UNITY_BENCH) and its BenchClasses (SimpleClass, as in the .NET
                             suite)
  WebGL/                     WebGLBench (the WebGL player's checks of Nino.WebGL, timed with NINO_WEBGL_TIMING; a
                             coroutine, as the WebGL player collects garbage between frames); Editor/WebGLBenchBuild
                             builds that player (-ninoTiming for the timed build)
  Editor/Burst/              BurstKernelsInEditMode (the kernel tests under Burst's JIT), BenchmarkPlayerBuild (the
                             release player for -ninoBenchmark)
```

## How CI runs it (.github/workflows/unity.yml)

- Runs only when what Unity loads changes (Nino.Core, the generator, the code fixes, Nino.UnityTypes, this project,
  the root build files). It builds the four DLLs and copies them into `Packages/com.jasonxudeveloper.nino/Runtime`
  first, so the tests run on the commit's code, not on the committed DLLs.
- One row per Burst major the package supports: Unity 2022.3.62f2 with Burst 1.8.29 (`Packages/manifest.json`) and
  6000.6.9f1, whose editor bundles Burst 2.0 as a built-in module (`Editor/Data/Tools/Burst`; the manifest's
  `com.unity.burst` resolves to the editor's 2.0.0 shim, so the same manifest serves both).
- Each job activates the Unity license, which has few seats: the matrix runs with `max-parallel: 1`, every run shares the concurrency group `unity-license` and is never cancelled while it runs
  (a cancelled job keeps its seat). Pull requests from forks and Dependabot get no secrets and skip it.
- Per version: the editor's play mode tests, then its edit mode tests (one step each, `testMode: playmode` and
  `editmode`, never `all`: see `audit-ci`), then an IL2CPP Linux player and its tests (`standalone`). Every Unity
  launch is bounded by `ENGINE_LAUNCH_WRAPPER` (`timeout`), every Unity step by `timeout-minutes`. Before every
  launch, `Delete Unity's stored process IDs` prints and deletes `Library/burst.pid` (with `Library/ilpp.pid`,
  `Library/MSBuild/buildhost.pid`, `Library/EditorInstance.json` and `Temp`): the 6000.6 editor kills the PID Burst
  2.0's compiler stored there, which the Library cache brings back from an earlier container and which can be a
  thread of the editor itself (`audit-ci`, "Bound every Unity launch").
  report.yml calls it with `benchmark: true` for a release: editor tests skipped, the player built as a release
  build with BurstBenchmark, its results uploaded as `unity-bench-<version>` for `benchmarks/reports/unity_report.py`.
- Then per version a WebGL player with SIMD (`webgl` job, after the tests, also `max-parallel: 1`), run in headless
  Chrome by `.github/scripts/webgl_run.mjs`: WebGLBench must report the kernels registered and no failure; the
  benchmark's run also times each case (artifact `webgl-<version>`).
- release.yml copies the Release DLLs into `Runtime/`, bumps `package.json`'s version and tags the OpenUPM release;
  its `unity` job (`unity-sign.yml`) signs the package with the UPM CLI and attaches the tarball OpenUPM publishes
  (`audit-ci`, "The signed Unity package").

## src/Nino.UnityTypes (no AGENTS.md of its own)

```
Nino.UnityTypes.csproj       netstandard2.1; ships only inside the Unity package (Runtime/Nino.UnityTypes.dll)
UnityFormatters.cs           AnimationCurveFormatter, GradientFormatter, RectOffsetFormatter: Unity classes written
                             through their public properties
UnityEngineStub/             UnityEngine.CoreModule.csproj + UnityEngine.cs: a reference assembly with Unity's name,
                             version 0.0.0.0 and the API those formatters use, for compiling outside Unity (never shipped)
```

## Rules for these files

- The DLLs under `Runtime/` carry the public API of this commit's sources: Unity compiles the package's own sources
  (Nino.Burst, Nino.WebGL), generated code and users' code against them, and a project that installs the package from
  the repository gets them as committed (after rc.7, Nino.Burst named `NinoKernels`, which rc.7's Nino.Core.dll did
  not have). `UnityPackageDllTests` compares the committed Nino.Core.dll (in the `-p:NinoCoreTfm=netstandard2.1` run)
  and Nino.UnityTypes.dll with the builds; bytes cannot be compared, since each DLL carries the commit it was built
  from. A change to their public API refreshes all four in its series, as release.yml's "Copy the Unity DLLs" does:
  `GITHUB_ACTIONS=true dotnet build -c Release`, then copy `src/Nino.Core/bin/Release/netstandard2.1/Nino.Core.dll`,
  `src/Nino.Generator/bin/Release/netstandard2.0/Nino.Generator.dll`,
  `src/Nino.Generator.CodeFixes/bin/Release/netstandard2.0/Nino.Generator.CodeFixes.dll` and
  `src/Nino.UnityTypes/bin/Release/netstandard2.1/Nino.UnityTypes.dll` into `Runtime/` (a `build(unity)` commit).
  `package.json`'s version changes only in the release's bump commit (`.github/scripts/bump_version.py`), which
  refreshes the DLLs too. CI copies fresh builds over them before every Unity test run, so the tests run on the
  commit's code either way.
- Unity's compiler loads the Roslyn 4.3 generator only: no interceptors, no `[UnsafeAccessor]` (private members need
  the type to be partial, NINO002).
- A new Unity test asmdef or Burst path keeps the scalar fallback: the kernels register only where Burst runs and
  the CPU has the instruction set (none on Armv7 or x86 without SSSE3; on WebGL, Nino.WebGL's only with wasm SIMD).
- Burst versions compile the same C# differently: a kernel keeps the form in `NinoBurst.Vectors.cs`'s header (one
  instruction set per `if`, never `IsNeonSupported || IsSse2Supported`; the block a call and a return), every kernel
  is covered by `NinoBurst.SelfTest`, and every Burst major the package supports has a row in unity.yml. A new Burst
  major gets its row (and green kernel tests) before the package claims it (`platforms`, Unity and Burst).
