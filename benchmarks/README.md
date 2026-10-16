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

Every release runs the same protocol in CI: [`report.yml`](../.github/workflows/report.yml) builds `v3/`, runs it
before and after the v4 suite in the same job on the same runner with the same settings, and adds a
**📊 v3 3.3.13 vs v4 (same machine)** section to the release notes, in front of the Performance Report: the
`compare.py` table for each v3 run, then the v3 reports. The documentation site takes its v4-vs-v3 ratios from that
section of the newest v4 release. A v3 failure only leaves the section out; re-running the workflow for the tag
adds whichever section is missing.

## Member by Member

`members/` takes `SimpleClass` apart: each member type (`int`/`Guid`/`DateTime` header, UTF-16 and UTF-8 strings,
`int[]`, `List<DateTime>`, both dictionaries) is its own type, plus struct and `Vector4` arrays, built once against
Nino 3.3.13 and once against this repository. When a case of the full run is slower, this shows which member is.

```sh
benchmarks/members/run.sh                     # v3, v4, v3 again, in-process; a few minutes
FILTER='*DMap*' benchmarks/members/run.sh     # only some cases
FILTER='*DStr*' DISASM=1 benchmarks/members/run.sh   # also the JIT's code for x64 and arm64
```

## Machine Code for x64 and ARM64

A slower case is found in the JIT's Tier1 code, and it has to be read for **both x64 and ARM64**: what costs one
instruction on x64 (`cmp dword ptr [mem], 128`, `inc dword ptr [mem]`) is a load, an operation and a store on ARM64,
so a value kept in memory instead of a register (a `Reader`/`Writer` whose address reaches a call that is not
inlined, a `ref` local, a struct with too many live fields) costs more there.

`cross-jit.sh` installs the JIT for the other architecture (arm64 on an x64 machine, x64 on Apple silicon) from
Microsoft's crossgen2 package into the .NET 10 runtime. With it every method is compiled for both, so one machine
shows both listings:

```sh
JIT=$(benchmarks/cross-jit.sh)
DOTNET_AltJit='*' DOTNET_AltJitName=$JIT DOTNET_JitDisasm='Nino*' dotnet <app>.dll > out.txt   # stdout, not
python3 benchmarks/members/split-asm.py out.txt asm      # DOTNET_JitStdOutFile; writes asm-x64.txt, asm-arm64.txt
```

`DISASM=1` on `members/run.sh` does this for the member cases. The cross JIT's code is never run, and the timings of
a run with it are meaningless. Its ARM64 code is the generic Unix ABI; Apple's differs only in calls with stack
arguments.
