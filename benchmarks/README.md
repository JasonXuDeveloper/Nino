# Benchmarks

`src/Nino.Benchmark` is the runtime benchmark of this repository (Nino v4 against MemoryPack and MessagePack).
`v3/` is the same `SimpleTest` (same data, same attributes) built against **Nino 3.3.13 from nuget.org**, the last v3
release, so v4 can be compared with v3 on the same machine. It is not part of `src/Nino.sln`.

```sh
benchmarks/run.sh                          # v3, then the full v4 suite; about 35 minutes
benchmarks/run.sh /path/to/out             # write the results somewhere else
QUICK=1 benchmarks/run.sh                  # development run, about 15 to 20 minutes (see below)
FILTER='*PooledTest*' benchmarks/run.sh    # only some v4 classes (v3 always runs SimpleTest's Nino and MemoryPack cases)
```

`QUICK=1` is for trends and regressions while developing: it sets `NINO_BENCH_QUICK=1`, which switches every class's
`[NinoJob]` from the published job to 250 ms iterations, 3 warm-up iterations and 6 to 10 measured ones that stop once
the error is within 2% of the mean. Do not publish its numbers: the README, the docs site
and the release notes use the full protocol, which is what supports "x× faster" and "on par" (within 5%).

Requires the .NET 10 SDK and Python 3. The script prints `v4 vs v3` per case; the BenchmarkDotNet
reports are in `<out>/v3` and `<out>/v4`. `dotnet benchmarks/v3/bin/Release/net10.0/Nino.Benchmark.dll
--verify` checks that both versions round-trip the benchmark data and prints the payload sizes.

The runtime tables in [docs/v4-benchmarks.md](../docs/v4-benchmarks.md) come from an earlier local run (that one ran v3
before and after v4); the release notes of each release carry its current numbers.

Every release runs the same protocol in CI: [`report.yml`](../.github/workflows/report.yml) runs the benchmark
classes in parallel on four runners, a class's Nino and MemoryPack cases always on one, and
[`merge_reports.py`](merge_reports.py) merges their reports into one table, naming each class's runner under it.
The `SimpleTest` runner builds `v3/` and runs it directly before its v4 run with the same settings; the workflow adds a
**📊 v3 3.3.13 vs v4 (same machine)** section to the release notes, in front of the Performance Report: the
`compare.py` table, then the v3 report. The documentation site takes its v4-vs-v3 ratios from that
section of the newest v4 release. A v3 failure only leaves the section out; re-running the workflow for the tag
adds whichever section is missing.

The README numbers and chart (`docs/assets/benchmark.svg` and `.png`) and the runtime tables of
`docs/v4-benchmarks.md` follow the newest v4 release too: for that release, `report.yml` runs
[`readme.py`](readme.py) on its reports, which rewrites everything between the `<!--bench:...-->` markers of
`README.md`, `README.zh.md` and `docs/v4-benchmarks.md` and the chart, then pushes them to `main`. After a full
local run, the same script updates them from your reports (it writes the SVG; render the PNG from `--png-html`):

```sh
python3 benchmarks/readme.py --v4 out/v4/results/*-joined-*-report-github.md \
  --v3 out/v3/results/*-report-github.md --png-html chart.html
```

Within 5% is written as "on par", and a case where v4 is slower is left out of the README's v3 sentence and is a
dash in the tables. A v4 time under 0.1 ns against a measurable one is "faster (too fast to measure)", with no
ratio.
Build-time numbers and everything outside the markers are edited by hand.

## Benchmark Classes

`src/Nino.Benchmark` runs these classes (BenchmarkDotNet categories in parentheses). Every one compares Nino with
MemoryPack; `SimpleTest` and `PolymorphicTest` add MessagePack. All of them land in one joined table, which is the
Performance Report of each release.

| Class | What it measures |
|---|---|
| `SimpleTest` | `SimpleClass`, `SimpleClass[100]`, `SimpleStruct`, `SimpleStruct[100]`, `Vector4[10000]`: serialize to a reused buffer writer, deserialize new instances (`SimpleClassSerialize`, ..., `VectorsDeserialize`) |
| `PolymorphicTest` | Polymorphic class, polymorphic class array, polymorphic `Foo[]` (`PolymorphicClassSerialize`, ...) |
| `PooledTest` | `SimpleClass`'s members pooled with `[NinoPooled]`, against MemoryPack's overwrite (`PooledDeserialize`) |
| `PooledGraphTest` | Pooled node and leaves: new, reused in place, pooled elements, against MemoryPack's overwrite (`Plain`, `Node`, `Leaves`) |
| `ReferenceTest` | A 200-node graph with shared tags, shared peers and cycles: `[NinoType(trackReferences: true)]` against MemoryPack's `GenerateType.CircularReference` (`GraphSerialize`, `GraphDeserialize`) |
| `VersionTolerantTest` | `SimpleClass`'s members as `NinoFormat.VersionTolerant` against MemoryPack's `GenerateType.VersionTolerant`, one and 100 (`TolerantClassSerialize`, `TolerantClassesSerialize`, `TolerantClassDeserialize`, `TolerantClassesDeserialize`) |
| `FixedSizeTest` | Types of fixed size (`NinoSerializer.GetFixedSize<T>()` is not -1), serialized to a new `byte[]` of exactly that size: a 40-byte unmanaged struct, a sealed class of the same members, and an array of 100 of the classes (`FixedStructSerialize`, `FixedClassSerialize`, `FixedClassesSerialize` and the `Deserialize` ones) |
| `CommonTypesTest` | Data shaped like that of common applications (`CommonTypes.cs`), to a reused buffer writer and into new instances: `List<Player>` (100), `List<Order>` (100), `List<Shape>` (1,000 through an abstract base), a `TreeNode` tree (1,365 nodes), `InventoryItem[1000]` (a padded struct) and `Dictionary<string, string>` (1,000) (`PlayersSerialize`, `OrdersSerialize`, `ShapesSerialize`, `TreeSerialize`, `InventorySerialize`, `StringDictionarySerialize` and the `Deserialize` ones) |
| `CollectionsTest` | Collections of the base class library, 1,000 elements each, to a reused buffer writer and into new instances: `List<(int, long)>`, `KeyValuePair<int, int>[]`, `HashSet<int>`, `Queue<int>`, `LinkedList<int>`, `SortedList<int, int>` and `ConcurrentDictionary<int, int>` (`TupleListSerialize`, `KeyValuePairsSerialize`, `HashSetSerialize`, `QueueSerialize`, `LinkedListSerialize`, `SortedListSerialize`, `ConcurrentDictionarySerialize` and the `Deserialize` ones) |
| `BclTypesTest` | Base class library types beyond the common collections, to a reused buffer writer and into new instances: `BigInteger` (3^1000), `Uri`, `Version`, `StringBuilder` (1,000 chars), `BitArray` (1,000 bits), `CultureInfo`, `TimeZoneInfo`, `Lazy<string>`, and with 1,000 elements `Memory<int>`, `ReadOnlySequence<int>`, `ReadOnlyCollection<int>`, `ILookup<int, int>`, `BlockingCollection<int>` and the immutable collections of Nino.Immutable (`ImmutableHashSet`, `ImmutableSortedSet`, `ImmutableQueue`, `ImmutableStack`, `ImmutableDictionary`, `ImmutableSortedDictionary`) (`BigIntegerSerialize`, ..., `ImmutableSortedDictionarySerialize` and the `Deserialize` ones) |

Method names are unique across classes (`<Serializer><Serialize|Deserialize><Case>`, e.g. `NinoSerializeTolerantClass`,
`MemoryPackDeserializeGraph`): the documentation site and the `Payload` column key rows by method name alone.

```sh
cd src/Nino.Benchmark
dotnet run -c Release -- --filter '*' --launchCount 1 --warmupCount 5 --iterationCount 15   # the published settings
dotnet run -c Release -- --filter '*FixedSizeTest*' --launchCount 1 --warmupCount 1 --iterationCount 3   # a quick look
```

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
