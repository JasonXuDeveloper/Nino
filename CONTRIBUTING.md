# Contributing to Nino

## Build and test

Requires the .NET 10 and .NET 8 SDKs.

```bash
cd src
dotnet build
dotnet test Nino.UnitTests

# Every Nino.Core build must write identical bytes: run the suite against the netstandard builds too,
# and with interceptors off (the regular entry points must agree with the intercepted ones).
dotnet test Nino.UnitTests -f net8.0 -p:NinoCoreTfm=netstandard2.0
dotnet test Nino.UnitTests -f net8.0 -p:NinoCoreTfm=netstandard2.1
dotnet test Nino.UnitTests -f net8.0 -p:NinoRegularCalls=true
dotnet test Nino.UnitTests -f net8.0 -p:NinoGeneratorBuild=4.8   # the .NET 8 SDK generator build
```

CI runs the same, plus the Unity tests (`src/Nino.Unity`, Unity 2022.3).

* `src/Nino.Core/Formatters/TupleFormatters.g.cs` is generated: after changing
  `tools/CodeGen/TupleFormattersTemplate.cs`, run `dotnet run --project tools/CodeGen` from the repository root.
* Runtime benchmarks: `dotnet run -c Release --project src/Nino.Benchmark`. Generator (compile time) benchmarks:
  `dotnet run -c Release --project src/Nino.GeneratorBenchmark`.
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
`alpha`/`beta`/`rc`). The workflow bumps the version (`src/Directory.Build.props`, `src/Version.cs`, the Unity
`package.json`), builds and tests it, commits the Unity DLLs with the bump, tags it, publishes the NuGet packages
(Trusted Publishing, no API key) and creates the GitHub release with generated notes. Pre-releases also get the
`vX.Y.Z-preview.N` tag OpenUPM reads. If a step fails after tagging, fix the cause and re-run the failed job.
