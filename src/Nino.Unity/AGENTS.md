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
                             Nino.UnityTypes.dll; ThirdParty/: System.Buffers, System.Memory,
                             System.Runtime.CompilerServices.Unsafe
    Burst/                   Nino.Burst: NinoBurst.cs registers Burst-compiled NinoKernels (Permute, Narrow, Widen,
                             NarrowAscii, WidenAscii; AVX2 / SSSE3 / AArch64 Neon with scalar branches) at startup;
                             compiled only with com.unity.burst 1.8+ (NINO_BURST, versionDefines); Nino.Core calls
                             Burst's function pointers directly (delegate* unmanaged), none registered where that fails
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
                             ConcurrentTables against the public API), run in play mode and in the IL2CPP player
  Runtime/Burst/             BurstKernelTests (the kernels against the scalar paths, byte for byte; asserts the pointer
                             calls), BurstBenchmark (only in the release benchmark's player: NINO_UNITY_BENCH) and its
                             BenchClasses (SimpleClass, as in the .NET suite)
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
- Each job activates the Unity license, which has few seats: the matrix (Unity 2022.3.62f2 and 6000.3.27f1) runs
  with `max-parallel: 1`, every run shares the concurrency group `unity-license` and is never cancelled while it runs
  (a cancelled job keeps its seat). Pull requests from forks and Dependabot get no secrets and skip it.
- Per version: the editor's play mode tests, then its edit mode tests (one step each, `testMode: playmode` and
  `editmode`, never `all`: see `audit-ci`), then an IL2CPP Linux player and its tests (`standalone`). Every Unity
  launch is bounded by `ENGINE_LAUNCH_WRAPPER` (`timeout`), every Unity step by `timeout-minutes`.
  report.yml calls it with `benchmark: true` for a release: editor tests skipped, the player built as a release
  build with BurstBenchmark, its results uploaded as `unity-bench-<version>` for `benchmarks/unity_report.py`.
- Then per version a WebGL player with SIMD (`webgl` job, after the tests, also `max-parallel: 1`), run in headless
  Chrome by `.github/scripts/webgl_run.mjs`: WebGLBench must report the kernels registered and no failure; the
  benchmark's run also times each case (artifact `webgl-<version>`).
- release.yml copies the Release DLLs into `Runtime/`, bumps `package.json`'s version and tags the OpenUPM release.

## src/Nino.UnityTypes (no AGENTS.md of its own)

```
Nino.UnityTypes.csproj       netstandard2.1; ships only inside the Unity package (Runtime/Nino.UnityTypes.dll)
UnityFormatters.cs           AnimationCurveFormatter, GradientFormatter, RectOffsetFormatter: Unity classes written
                             through their public properties
UnityEngineStub/             UnityEngine.CoreModule.csproj + UnityEngine.cs: a reference assembly with Unity's name,
                             version 0.0.0.0 and the API those formatters use, for compiling outside Unity (never shipped)
```

## Rules for these files

- The DLLs under `Runtime/` and `package.json`'s version change only in the release's bump commit (release.yml,
  `.github/scripts/bump_version.py`); CI copies fresh builds over them before every test run, so a change to the
  code needs no DLL commit.
- Unity's compiler loads the Roslyn 4.3 generator only: no interceptors, no `[UnsafeAccessor]` (private members need
  the type to be partial, NINO002).
- A new Unity test asmdef or Burst path keeps the scalar fallback: the kernels register only where Burst runs and
  the CPU has the instruction set (none on Armv7 or x86 without SSSE3; on WebGL, Nino.WebGL's only with wasm SIMD).
