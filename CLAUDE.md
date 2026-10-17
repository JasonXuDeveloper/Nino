# CLAUDE.md

Guidance for Claude Code when working in this repository.

## Benchmarks

- During development, run benchmarks in quick mode: `QUICK=1 benchmarks/run.sh`, with `FILTER='*SimpleTest*'`
  (any BenchmarkDotNet filter) to run only the cases you are working on. Quick mode measures each case for 6 to 10
  iterations, stopping once its error is under 2%, so it takes about 15–20 minutes for everything.
- Both modes run v3 3.3.13 (`benchmarks/v3`, from NuGet) once, directly before v4, on the same machine; MemoryPack runs
  in both, so its two results show how much the machine moved.
- Quick numbers are for comparing your own changes back to back on one machine. Never publish them: the numbers
  in README.md, docs/v4-benchmarks.md, the chart and the release notes come from a full `benchmarks/run.sh` run
  (or the release workflow), whose job id stays `ShortRun-.NET 10.0`.
- See benchmarks/README.md for the options.
