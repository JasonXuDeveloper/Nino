# Benchmarks

`src/Nino.Benchmark` is the runtime benchmark of this repository (Nino v4 against MemoryPack and MessagePack).
`v3/` is the same `SimpleTest` (same data, same attributes) built against **Nino 3.3.13 from nuget.org**, the last v3
release, so v4 can be compared with v3 on the same machine. It is not part of `src/Nino.sln`.

```sh
benchmarks/run.sh                 # v3, then the full v4 suite, then v3 again; about 40 minutes
benchmarks/run.sh /path/to/out    # write the results somewhere else
```

Requires the .NET 10 SDK and Python 3. The script prints `v4 vs v3` per case for both v3 runs; the BenchmarkDotNet
reports are in `<out>/v3a`, `<out>/v4` and `<out>/v3b`. `dotnet benchmarks/v3/bin/Release/net10.0/Nino.Benchmark.dll
--verify` checks that both versions round-trip the benchmark data and prints the payload sizes.

The numbers in [docs/v4-benchmarks.md](../docs/v4-benchmarks.md) were measured this way.

## Member by Member

`members/` takes `SimpleClass` apart: each member type (`int`/`Guid`/`DateTime` header, UTF-16 and UTF-8 strings,
`int[]`, `List<DateTime>`, both dictionaries) is its own type, plus struct and `Vector4` arrays, built once against
Nino 3.3.13 and once against this repository. When a case of the full run is slower, this shows which member is.

```sh
benchmarks/members/run.sh                     # v3, v4, v3 again, in-process; a few minutes
FILTER='*DMap*' benchmarks/members/run.sh     # only some cases
FILTER='*DStr*' DISASM=1 benchmarks/members/run.sh   # also the JIT's code, in results/asm-*.txt
```
