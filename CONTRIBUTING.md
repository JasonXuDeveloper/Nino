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

## Pull requests

Branch from `main`, keep a pull request to one change, add tests for behavior changes, and update the docs (README,
`docs/`) when users see the change. A change to the binary format must update the golden bytes tests on purpose.

## Releasing (maintainers)

Actions → **Release** → Run workflow on `main`, and enter the version (`4.0.0`, or `4.1.0-beta.1` with
`alpha`/`beta`/`rc`). The workflow bumps the version (`src/Directory.Build.props`, `src/Version.cs`, the Unity
`package.json`), builds and tests it, commits the Unity DLLs with the bump, tags it, publishes the NuGet packages
(Trusted Publishing, no API key) and creates the GitHub release with generated notes. Pre-releases also get the
`vX.Y.Z-preview.N` tag OpenUPM reads. If a step fails after tagging, fix the cause and re-run the failed job.
