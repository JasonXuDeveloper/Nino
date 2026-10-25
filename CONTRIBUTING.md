# Contributing to Nino

## Build and test

Requires the .NET 10 and .NET 8 SDKs.

```bash
dotnet build
dotnet test tests/Nino.UnitTests

# Every Nino.Core build must write identical bytes: run the suite against the netstandard builds too,
# and with interceptors off (the regular entry points must agree with the intercepted ones).
dotnet test tests/Nino.UnitTests -f net8.0 -p:NinoCoreTfm=netstandard2.0
dotnet test tests/Nino.UnitTests -f net8.0 -p:NinoCoreTfm=netstandard2.1
dotnet test tests/Nino.UnitTests -f net8.0 -p:NinoRegularCalls=true
dotnet test tests/Nino.UnitTests -f net8.0 -p:NinoGeneratorBuild=4.8   # the .NET 8 SDK generator build
```

CI (`.github/workflows/ci.yml`) runs the unit tests on net10.0, on Nino.Core
netstandard2.1 without interceptors (what Unity runs), on netstandard2.0 with the Roslyn 4.8 generator, on Arm64
(net10.0 and net8.0), on x64 without AVX-512, without AVX2 and without SIMD (`DOTNET_EnableAVX512=0`,
`DOTNET_EnableAVX2=0`, `DOTNET_EnableHWIntrinsic=0`) and on Windows, each configuration split over three runners
(everything but `ShapeMatrixTests`, and each half of its rows; "Build and Test" checks the three ran the whole suite,
`.github/scripts/test_shards.py`). Beside them it builds the whole solution and runs the ASP.NET Core tests and those
of Nino.Codegen (`Nino.AspNetCore.Tests`, `Nino.Codegen.Integration.Tests`, `Nino.Codegen.Tests`, Node 22.18+),
publishes `Nino.AotTests` trimmed and Native AOT, and runs `Nino.RuntimeTests` ("Runtime internals") on every runtime
whose collections Nino reads or builds through their fields: .NET on x64, Arm64, Windows x86 and Arm32 (under qemu),
Mono on x64 and Arm64 (also with `MONO_GC_DEBUG=verify-before-collections`), and .NET Framework 4.8 64-bit and 32-bit
(`-p:NinoX86=true` builds the 32-bit exe). Each must write .NET x64's bytes, see the same contents through the public
API and find every path meant for it (`FastPaths` in its `Program.cs`). Locally:

```bash
dotnet build tests/Nino.RuntimeTests -c Release
dotnet tests/Nino.RuntimeTests/bin/Release/net10.0/Nino.RuntimeTests.dll > dotnet.txt
mono tests/Nino.RuntimeTests/bin/Release/net472/Nino.RuntimeTests.exe > mono.txt
diff <(grep -v '^#' dotnet.txt) <(grep -v '^#' mono.txt)
```

`runtimes.yml` tests the Go (with and without `-tags purego`), Rust and TypeScript runtimes on x64 and on an Arm64
runner, and Go also on 386, Arm32 and big-endian s390x (the last two under qemu); the release's `report.yml`
benchmarks the three on x64 and Arm64. The
Unity tests (`src/Nino.Unity`, on Unity 2022.3 and the newest Unity 6 LTS, in the editor and in an IL2CPP player) run in `unity.yml`, one job at a time
and never cancelled while running, so CI holds at most one seat of the Unity license. Locally, the same split keeps a run in memory:
`--filter "FullyQualifiedName!~ShapeMatrixTests"`, then `--filter "FullyQualifiedName~ShapeMatrixTests"`.

The TypeScript runtime's WebAssembly kernels (`runtimes/typescript/wasm/text.wat`) are assembled into `nino.ts` by
`npm run build:wasm` with [wasm-tools](https://github.com/bytecodealliance/wasm-tools) (`cargo install --locked
wasm-tools`, or the binary of a release on the `PATH`); only a change to `text.wat` needs it (see
[runtimes/typescript/README.md](runtimes/typescript/README.md)).

The workflows pin every action to a commit SHA with its version in a comment (`uses: actions/checkout@<sha> # v7.0.1`);
Dependabot updates both, all actions in one pull request. `actionlint` (with `.github/actionlint.yaml`) checks them.

A change to a read or write path (Nino.Core, Nino.Immutable, the code the generator emits) also needs
`python3 benchmarks/asm.py compare origin/main` ([benchmarks/README.md](benchmarks/README.md)), which lists every
method whose machine code changed.

* `src/Nino.Core/Formatters/TupleFormatters.g.cs` and `MultiDimFormatters.g.cs` are generated: after changing
  `tools/CodeGen/TupleFormattersTemplate.cs` or `MultiDimFormattersTemplate.cs`, run
  `dotnet run --project tools/CodeGen` from the repository root.
* Runtime benchmarks: `dotnet run -c Release --project benchmarks/Nino.Benchmark`. Generator (compile time) benchmarks:
  `dotnet run -c Release --project benchmarks/Nino.GeneratorBenchmark`.
* How the runtime and the generator fit together: [docs/v4-design.md](docs/v4-design.md).

## Performance work

* Optimize from the JIT's Tier1 machine code for **both x64 and ARM64**, then confirm with timings. One machine shows
  both: `benchmarks/cross-jit.sh` installs the other architecture's JIT, and `DISASM=1 benchmarks/members/run.sh`
  writes both listings for the member cases ([benchmarks/README.md](benchmarks/README.md#machine-code-for-x64-and-arm64)).
* ARM64 is a load/store architecture: what x64 does in one instruction on memory is a load, an operation and a store
  there, and 64-bit constants and addresses take several instructions. Keep hot state in registers:
  * never pass the address of a `Reader`, a `Writer` or a value to a call that is not inlined (pass a copy and write
    back only the fields that change), or the JIT keeps all its fields in memory;
  * keep rare paths (replaced formatters, deferral, growth, recycling, transcoding) out of line, so they neither use
    up the caller's inlining budget nor add locals (past the JIT's tracking limit, locals are zeroed in the prolog);
  * do not inline a generic method that needs a runtime lookup into shared code on a hot path.
* Timings on a shared or cloud machine vary by ±15%: interleave runs (base, new, base, new) and read the code before
  trusting a difference. The wire format must not change unless that is the point: compare the bytes.

## Pull requests

Branch from `main`, keep a pull request to one change, add tests for behavior changes, and update the docs (README,
`docs/`) when users see the change. A change to the binary format must update the golden bytes tests on purpose.

## Releasing (maintainers)

Actions → **Release** → Run workflow on `main`, and enter the version (`4.0.0`, or `4.1.0-beta.1` with
`alpha`/`beta`/`rc`). The workflow bumps the version of every package (`.github/scripts/bump_version.py`:
`Directory.Build.props`, the Unity `package.json`, the Rust crates, the npm package), builds and tests it, commits
the Unity DLLs with the bump, tags it (`vX.Y.Z`, and `runtimes/go/vX.Y.Z` for the Go module), publishes the NuGet
packages, the crates `nino-serialization-derive` and `nino-serialization` and the npm package `nino-serialization`
(all through Trusted Publishing, no stored key; an npm pre-release under the dist-tag `next`) and creates the GitHub
release with generated notes. Pre-releases also get the `vX.Y.Z-preview.N` tag OpenUPM reads. If a step fails after
tagging, fix the cause and re-run the failed job: each publish skips what its registry already has.

Trusted Publishing is configured on a package that exists, so the first version of each crate and of the npm package
is published by hand (`cargo publish -p nino-serialization-derive`, then `-p nino-serialization`, in `runtimes/rust`;
`npm run build && npm publish --access public --tag next` in `runtimes/typescript`, with the repository's `LICENSE`
copied there), then each gets the trusted publisher JasonXuDeveloper/Nino, workflow `release.yml`.
