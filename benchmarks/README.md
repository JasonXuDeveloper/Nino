# Benchmarks

`benchmarks/Nino.Benchmark` is the runtime benchmark of this repository (Nino v4 against MemoryPack, MessagePack and
protobuf-net).
`v3/` is the same `SimpleTest` (same data, same attributes) built against **Nino 3.3.13 from nuget.org**, the last v3
release, so v4 can be compared with v3 on the same machine; it compiles v4's `NinoJobAttribute.cs` and
`SimpleBenchmarkData.cs`, so the job and the data cannot drift apart. It is not part of `Nino.slnx`.

```sh
benchmarks/run.sh                          # v3, then the full v4 suite; about six hours
benchmarks/run.sh /path/to/out             # write the results somewhere else
QUICK=1 benchmarks/run.sh                  # development run, about 30 to 45 minutes (see below)
FILTER='*PooledTest*' benchmarks/run.sh    # only some v4 classes (v3 always runs SimpleTest's Nino and MemoryPack cases)
FILTER='*Vectors* *FixedStruct*' benchmarks/run.sh   # several patterns, separated by spaces
FILTER='*.Nino* *.MemoryPack*' benchmarks/run.sh    # Nino and MemoryPack only (231 of 413 cases)
```

`QUICK=1` is for trends and regressions while developing: it sets `NINO_BENCH_QUICK=1`, which switches every class's
`[NinoJob]` from the published job to 250 ms iterations, 3 warm-up iterations and 6 to 10 measured ones that stop once
the error is within 2% of the mean. Do not publish its numbers: the README, the docs site
and the release notes use the full protocol, which is what supports "x× faster" and "on par" (within 5%).

Requires the .NET 10 SDK and Python 3. The script prints `v4 vs v3` per case; the BenchmarkDotNet
reports are in `<out>/v3` and `<out>/v4`. Both runs use one project, `benchmarks/Nino.Benchmark`: built with
`NINO_BENCH_V3=true` (an environment variable, so the builds BenchmarkDotNet makes for its runs take it too), it
references Nino 3.3.13 from nuget.org instead of the repository's code, compiles only `V3/SimpleTest.cs` (the same
`SimpleTest`, ported to v3's API) with the shared data, job, columns and checks, and goes to `bin/v3` and `obj/v3`.
`--verify` (`benchmarks/Nino.Benchmark/Verify.cs`, run by `run.sh`, `report.yml` and the PR benchmark job, for v3 too)
checks every case: each serialize case writes its payload's bytes, and every case of a deserialize category reads back the
same value, deep-compared. The data is seeded (`BenchmarkRandom`), so every run serializes the same values. Nino
serializes to its `NinoArrayBufferWriter`, MemoryPack and MessagePack to `ArrayBufferWriter<byte>`, protobuf-net to
a `MemoryStream`: each to the writer it writes fastest, all reused.

The runtime tables in [docs/v4-benchmarks.md](../docs/v4-benchmarks.md) come from the newest release's report (see
below); the release notes of each release carry its numbers.

Every release runs the same protocol in CI: [`report.yml`](../.github/workflows/report.yml) runs the benchmark
classes in parallel on ten runners per architecture (SimpleTest split into its serialize and deserialize cases,
BclTypesTest, CollectionsTest and CommonTypesTest by type), every serializer's run of a case always on one, every
case in three processes, and
[`merge_reports.py`](merge_reports.py) merges their reports into one table, naming each class's runner under it.
The release notes get the short tables of [`compact_report.py`](compact_report.py) (Method, Mean, Ratio, Payload,
Allocated, one table per class), as a release body holds at most 125,000 characters; the full reports are attached
to the release. BenchmarkDotNet writes a report of one class without a Type column: both scripts then take the class
from the file name (`Nino.Benchmark.SimpleTest-report-github.md`), so keep that name when copying one.
Each of the two `SimpleTest` runners builds `v3/` and runs its half of it directly before the same half of v4, with the
same settings; the workflow adds a
**📊 v3 3.3.13 vs v4 (same machine)** section to the release notes, in front of the Performance Report: the
`compare_v3.py` table and the v3 runners (the v3 report, both halves merged, is attached). The documentation site takes its v4-vs-v3 ratios from that
section of the newest v4 release. A v3 failure only leaves the section out; re-running the workflow for the tag
adds whichever section is missing.

The runtimes outside .NET get a section each after all of those: **🌐 TypeScript runtime benchmark**
(`runtimes/typescript/bench`, Node), **🐹 Go runtime benchmark** (`runtimes/go/bench`) and **🦀 Rust runtime
benchmark** (`runtimes/rust/bench`), each against the serializers of its language, run on one runner from the tag's
sources and formatted by [`runtimes_report.py`](runtimes_report.py). Their headings and tables have none of the
words the readers of the .NET sections look for ("Performance Report", "Method", "Mean"), and a language whose
benchmark fails is left out without touching the rest. The same three run on an Arm64 runner too and follow as
**🌐 Arm64: TypeScript runtime benchmark**, **🐹 Arm64: Go runtime benchmark** and **🦀 Arm64: Rust runtime
benchmark** (`runtimes_report.py --arch arm64`: the same tables; no Arm64 heading contains an x64 one, which the docs
site finds by its first occurrence).

The last section, **🎮 Unity benchmark (Burst, IL2CPP)**, times Nino in a Unity IL2CPP player without and with the
Burst kernels (`Nino.Burst`), for each Unity version `unity.yml` tests: `report.yml` calls `unity.yml` with
`benchmark: true` for the tag, which builds the test player without the Development flag
(`Test.Editor.Burst.BenchmarkPlayerBuild`, from `-ninoBenchmark`) with `Test.Runtime.Burst.BurstBenchmark` in it. The
benchmark measures each case with the Unity Performance Testing package (`Measure.Method`, one sample group without
the kernels and one with them, plus "SSSE3 only" on AVX2 CPUs), whose results and run metadata (Unity version, backend,
CPU, packages) land in the player's NUnit results; the player adds a `NINO_UNITY_ENV` line (the kernels' instruction
set) and the editor a `NINO_UNITY_BUILD` line (IL2CPP's settings and Burst's x64 targets, which the package does not
record). [`unity_report.py`](unity_report.py) turns them into a line per Unity version naming the player and the
machine and a table per version (tested by `test_unity_report.py` on `fixtures/unity-bench-*/`). It waits for the Unity
license's seat like every Unity run, and a failure leaves only this section out (the run shows it as failed).

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
dash in the tables. A time under 1 ns (about three clock cycles) cannot be told apart from another: a v4 time under
it against a measurable one is "faster (too fast to measure)", with no ratio, and both under it are on par.
Build-time numbers and everything outside the markers are edited by hand.

The scripts that read reports (`readme.py`, `ab.py`, `merge_reports.py`, `compact_report.py`, `compare_v3.py`,
`members/table.py`) share one parser, unit table and floor: [`bdn.py`](bdn.py).

## Benchmark Classes

`benchmarks/Nino.Benchmark` runs these classes (BenchmarkDotNet categories in parentheses). Every one compares Nino with
MemoryPack, and with MessagePack and protobuf-net wherever they can write the same data (see
[MessagePack and protobuf-net](#messagepack-and-protobuf-net)). All of them land in one joined table, which is the
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
| `CommonTypesTest` | Data shaped like that of common applications (`CommonTypes.cs`), to a reused buffer writer and into new instances: `List<Player>` (100), `List<Order>` (100), `List<Shape>` (1,000 through an abstract base), a `TreeNode` tree (1,365 nodes), `InventoryItem[1000]` (a padded struct), `Waypoint[1000]` (a struct of fixed size that is not raw: a value tuple member) and `Dictionary<string, string>` (1,000) (`PlayersSerialize`, `OrdersSerialize`, `ShapesSerialize`, `TreeSerialize`, `InventorySerialize`, `WaypointsSerialize`, `StringDictionarySerialize` and the `Deserialize` ones) |
| `CollectionsTest` | Collections of the base class library, 1,000 elements each, to a reused buffer writer and into new instances: `List<(int, long)>`, `KeyValuePair<int, int>[]`, `HashSet<int>`, `Dictionary<int, int>`, `Queue<int>`, `LinkedList<int>`, `SortedSet<int>`, `SortedDictionary<int, int>`, `SortedList<int, int>` and `ConcurrentDictionary<int, int>` (`TupleListSerialize`, `KeyValuePairsSerialize`, `HashSetSerialize`, `IntDictionarySerialize`, `QueueSerialize`, `LinkedListSerialize`, `SortedSetSerialize`, `SortedDictionarySerialize`, `SortedListSerialize`, `ConcurrentDictionarySerialize` and the `Deserialize` ones) |
| `BclTypesTest` | Base class library types beyond the common collections, to a reused buffer writer and into new instances: `BigInteger` (3^1000), `Uri`, `Version`, `StringBuilder` (1,000 chars), `BitArray` (1,000 bits), `CultureInfo`, `TimeZoneInfo`, `Lazy<string>`, and with 1,000 elements `Memory<int>`, `ReadOnlySequence<int>`, `ReadOnlyCollection<int>`, `ILookup<int, int>`, `BlockingCollection<int>` and the immutable collections of Nino.Immutable (`ImmutableHashSet`, `ImmutableSortedSet`, `ImmutableQueue`, `ImmutableStack`, `ImmutableDictionary`, `ImmutableSortedDictionary`) (`BigIntegerSerialize`, ..., `ImmutableSortedDictionarySerialize` and the `Deserialize` ones). Nino only for `BigInteger` and `BitArray`: MemoryPack 1.21.4 reads back another `BigInteger` than it wrote (up to 255 bytes), and on .NET 10 writes a `BitArray` wrong and crashes reading one |
| `ScatteredTest` | Arrays of 1,000,000 objects whose order in the array is not their order in memory (shuffled after allocation), as objects created and replaced over a program's life are: `Shape[]` through the abstract base and `ScatteredItem[]` (an int, a string allocated in an order of its own, a long) to a reused buffer writer, the items read into an existing shuffled array; the same shapes in allocation order, and 8,192 of them in order, which stay in the cache (`ScatteredShapesSerialize`, `InOrderShapesSerialize`, `HotShapesSerialize`, `ScatteredItemsSerialize`, `ScatteredItemsDeserializeInto`). Every other class allocates its objects in order and fits in the cache |
| `UntrustedTest` | Reads with `NinoSettings.Untrusted` on (set before first use; the class runs in a process of its own): `List<CheckedSample>` of 1,000 raw structs whose `DateTime` and `bool` fields Nino checks before copying them (`UntrustedListDeserialize`). Against MemoryPack only, which copies them unchecked; under `--verify` the setting is already frozen and the case reads in trusted mode |

Method names are unique across classes (`<Serializer><Serialize|Deserialize><Case>`, e.g. `NinoSerializeTolerantClass`,
`MemoryPackDeserializeGraph`, `ProtobufSerializePlayers`; the serializers are `Nino`, `MemoryPack`, `MessagePack` and
`Protobuf`): the documentation site and the `Payload` column key rows by method name alone.

### MessagePack and protobuf-net

MessagePack 3 (MessagePack-CSharp) and protobuf-net 3 read and write the same values as Nino and MemoryPack, through
the same types: `[MessagePackObject]` with integer `[Key]`s and `[Union]` for subtypes, `[ProtoContract]` with
`[ProtoMember]` and `[ProtoInclude]`. Each uses the API its documentation recommends for speed:

- MessagePack: `MessagePackSerializer.Serialize(IBufferWriter<byte>, value)` into a reused `ArrayBufferWriter<byte>`
  and `Deserialize<T>(ReadOnlyMemory<byte>)`, with the default options, whose resolver takes the formatters
  MessagePack's source generator (in MessagePackAnalyzer) writes for the benchmark types at compile time. The immutable
  collections go through `ImmutableCollectionResolver` (the MessagePack.ImmutableCollection package).
- protobuf-net: `Serializer.Serialize(Stream, value)` into a reused `MemoryStream` and
  `Serializer.Deserialize<T>(ReadOnlySpan<byte>)`, on the default runtime model, which compiles a serializer for each
  type on first use (protobuf-net 3.4's compile-time model is a preview, `[Experimental]`, and is meant for native AOT
  and start-up time). `ProtobufModel.cs` adds what attributes cannot say: the fields of `Vector3` and `Vector4`, a
  surrogate for `DateTimeOffset` (ticks and offset), which protobuf-net has no serializer for, and, in
  `GenericPolymorphicTest`, the subtypes of each closed generic base. Its stream writer is its fastest: it writes a
  nested message first and its length after, where its `IBufferWriter<byte>` writer measures each nested message
  before writing it (slower in the cases compared, and it allocates). `FixedSizeTest` measures `byte[]` APIs:
  MessagePack's `Serialize(value)`; protobuf-net has none, so it writes to a reused stream and copies the bytes into
  a new array.

A case a library cannot express with the same data has no method for it, never one with other data:

| Class | No MessagePack | No protobuf-net |
|---|---|---|
| `ReferenceTest` | no reference tracking: shared objects and cycles cannot be written | the same (protobuf-net 3 does not implement `AsReference`: a compile error) |
| `PooledTest`, `PooledGraphTest` | the overwrite and pooled cases: it reads new instances only | the same: deserializing into an instance merges (appends to its lists) instead of overwriting it |
| `GenericPolymorphicTest` | the five generic bases: its unions are attributes on the base, which cannot name other subtypes for each type argument | — |
| `CommonTypesTest` | — | `Tree`: protobuf cannot tell an empty list from none, and the leaves' empty child lists read back as null |
| `BclTypesTest` | `CultureInfo`, `TimeZoneInfo`, `BlockingCollection<T>`: it throws on them | everything but `Uri` and the immutable collections: no serializer for `BigInteger`, `Version`, `StringBuilder`, `BitArray`, `CultureInfo`, `TimeZoneInfo`, `Lazy<T>`, `Memory<T>`, `ReadOnlySequence<T>`, `ReadOnlyCollection<T>`, `ILookup<,>` or `BlockingCollection<T>` |
| `ScatteredTest` | `ScatteredItemsDeserializeInto`: it reads new instances only | the same: deserializing into an instance merges instead of overwriting it |
| `UntrustedTest` | the case: it times Nino's checks of untrusted data, which MessagePack and protobuf-net do not make either; MemoryPack is the unchecked copy to compare with | the same |

The formats differ in what they write, and the Payload column shows it: MessagePack writes a `Guid` as a 36-character
string and a `DateTime` as a UTC timestamp (it reads a local time back in UTC: the same instant), protobuf-net nests
one message per level of a class hierarchy and writes a `DateTime` as its own `bcl.DateTime` message.

```sh
cd benchmarks/Nino.Benchmark
dotnet run -c Release -- --filter '*'   # the published settings (NinoJobAttribute: 3 launches, 5 warmups, 15 iterations)
dotnet run -c Release -- --filter '*FixedSizeTest*' --launchCount 1 --warmupCount 1 --iterationCount 3   # a quick look
```

`CpuProbe.cs` holds probes, classes named `*Probe` that only a filter naming them runs (never `'*'`, and
`report.yml` leaves them out): `NinoUnsafe`'s block copies against Memmove by size, alignment and distance, on jobs
of their own (AVX-512 on, off and with 256-bit vectors preferred; one job on Arm64). Nino.Core picks its copy by CPU
from what they measured; run them again on a CPU those choices were not measured on:

```sh
dotnet run -c Release -- --filter '*CopyProbe*' '*NewArrayCopyProbe*'   # x64
dotnet run -c Release -- --filter '*Copy128Probe*'                      # Arm64
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

### What a change did to the code: `asm.py`

`asm.py` compares the Tier1 code of every Nino case, and of the generated formatters and Nino.Core methods they
call, between two commits, in about two minutes (BenchmarkDotNet needs half an hour and moves by 15% between runs on
a shared machine; the JIT's code barely moves):

```sh
python3 benchmarks/asm.py compare v4.0.0-rc.6             # that tag against the working tree
python3 benchmarks/asm.py dump --ref main base.json       # or one side at a time,
python3 benchmarks/asm.py dump --asm head.asm head.json   # keeping the listing,
python3 benchmarks/asm.py diff base.json head.json        # and compared
```

It runs `Nino.Benchmark` of each side with the startup hook in `jitwarm/`, which calls every case until it is compiled
at Tier1 (the code BenchmarkDotNet times: everything a top-level call reaches is inlined into the case), and lists
the methods whose code grew by more than 15% and 64 bytes or that access the stack more often (a reader, a writer or
a cursor left in memory), with the calls each makes that it did not make before. What the JIT inlines also depends on
profile data gathered while the process runs, so each side runs three times and keeps each method's leanest code; a
method listed alone, in code the change does not reach, is that variation (dump again). Run both sides on one machine; set
`DOTNET_EnableAVX512=0` for the code of a machine without AVX-512 (.NET 10 ignores `DOTNET_EnableAVX512F`), and
`DOTNET_EnableAVX2=0` for SSSE3-only code. It shows x64 (or whatever the machine is); the
cross JIT above shows ARM64.

## Runtimes outside .NET

Each runtime is benchmarked with its language's standard tool, against Protocol Buffers and MessagePack in every
case, plus each language's usual serializers. Every case has a value of the same data for each serializer (the
protobuf messages mirror the types field for field), and every serializer is checked to read back what it wrote
before it is timed. `report.yml`'s `runtimes` job runs each as below on one x64 runner and on one Arm64 runner, and
`runtimes_report.py` turns its output into the release's sections: a row per case and operation, a column per
serializer (Nino first), and the encoded sizes. Local numbers are for comparing changes back to back on one machine; never publish them.

The string cases are the same in all three: 1,000 short ASCII strings (`"string 0"` to `"string 999"`), and 1,000
strings of ASCII, Latin-1 (`"crème brûlée i"`, one byte a char in Nino) and other text (`"строка 字符串 🎉 i"`,
UTF-16 in Nino, with a char outside the BMP) in turn. TypeScript rotates these through 63 more variants of the
same lengths and scripts (below).

### TypeScript

`runtimes/typescript/bench/` measures the TypeScript that nino-gen generates against Protocol Buffers (protobuf-es,
code generated from `proto/bench.proto`), MessagePack (msgpackr) and `JSON`, in Node and in headless Chromium
(Playwright), timed by [tinybench](https://github.com/tinylibs/tinybench) (warm-up, then samples for a set time; the
mean, its relative margin of error, p75/p99 and passes per second):

```sh
runtimes/typescript/bench/run.sh           # every scenario
runtimes/typescript/bench/run.sh lookup    # the scenarios whose data file starts with "lookup"
NINO_BENCH_TIME=300 runtimes/typescript/bench/run.sh strings   # 300 ms per task instead of 1,000
```

`Program.cs` declares the types and writes each scenario's values, serialized by Nino in .NET, to
`bench/out/data`; `nino-gen --lang ts` generates their TypeScript into `bench/gen`. Before a scenario is timed, every
value is read and written back in TypeScript and must give exactly the bytes .NET wrote, and every protobuf message
must read back equal. A task is one pass over all of a scenario's values. Nino has a third task, `serializeInto`: the
same values written with `serializeInto` into one `NinoWriter` reset before each value, as an application that sends
each value before the next one does; it allocates no `ArrayBuffer` once the writer is large enough, where `serialize`
takes a new 64 KiB (or larger) buffer as its results fill the last one.

Inputs rotate: `Program.cs` writes each scenario in 64 variants (the first is the original data; the others have their
own random seed and scramble the fixed text letter for letter within its script, so shapes, lengths and encodings stay
the same), every serializer gets all 64 ready before the timing, and each pass of every task, serialize and
deserialize, takes the next variant in turn. Repeating one input lets V8 reuse work: a repeated JSON text parses along
the hidden-class transitions and internalized keys of the previous parse (a Lookup-like text took 0.21 ms a parse
repeated, 7.8 to 8.4 ms with 200 different texts in turn), which made `JSON.parse` look several times faster than it
is. The sizes are a variant's mean. Holding every variant takes about 1.3 GB in Node. MessagePack (standard maps, no msgpackr
records) gets the values Nino read, as they are (classes and `Map`s as maps, `bigint`s as 64-bit integers, read back
as `bigint`); JSON gets them as plain JSON (64-bit integers as strings, maps as objects) and is not converted back to
`bigint` or `Map`, so it does less work than Nino. Neither carries the type of a polymorphic value (`Zoo`, `Player.Pet`),
which Nino and protobuf (a `oneof`) do. The bench's dependencies are in its own `package.json` and lockfile
(`npm ci`, which run.sh does); `npm run generate:proto` regenerates `proto/bench_pb.ts` (buf and protoc-gen-es, no
protoc). It needs the .NET 10 SDK, Node 22.18+ (which runs TypeScript directly) and, for the Chromium run, Playwright
with Chromium; the browser gets the bench bundled by esbuild. The release's section is the Node run
(`NINO_BENCH_JSON`).

### Go

`runtimes/go/bench` (a module of its own: the runtime's has no dependencies) uses `testing.B` (`b.Loop`,
`ReportAllocs`, `SetBytes`, and the encoded size as the `wire-B` metric), summarized by benchstat:

```sh
cd runtimes/go/bench
go test -run='^$' -bench=. -benchmem -count=6 . | tee bench.txt
go run golang.org/x/perf/cmd/benchstat@latest bench.txt
go test -run='^$' -bench='/^strings' -benchmem -count=6 .   # one case: Benchmark{Serialize,Deserialize}/<case>/<serializer>
```

The cases are the .NET benchmark's `SimpleClass`, a list of 100 of them and the two string cases; the serializers
are codecs in the shape nino-gen generates (`Nino`), the struct tags (`NinoTags`), Protocol Buffers
(google.golang.org/protobuf, `benchpb/bench.proto`, `MarshalAppend` into a reused buffer), MessagePack
(vmihailenco/msgpack, one reused encoder), encoding/json and gob. `TestRoundTrip` checks each and logs the sizes.
`benchpb/bench.pb.go` is generated: `protoc --go_out=. --go_opt=paths=source_relative benchpb/bench.proto`, or
`npx @bufbuild/buf generate` with `protoc-gen-go` on `PATH` (see the `.proto`). The release job runs six counts and
`benchstat -format csv`.

### Rust

`runtimes/rust/bench` uses criterion: a group per case, with the number of values as its throughput, `black_box` on
every input and output, against Protocol Buffers (prost, messages derived in `compare.rs`: no protoc), MessagePack
(rmp-serde), bincode, postcard and serde_json:

```sh
cd runtimes/rust
cargo bench -p nino-bench                          # every group
cargo bench -p nino-bench -- strings-mixed-1000    # one group
```

It prints each case's encoded sizes; the release job keeps that output and criterion's estimates
(`target/criterion/**/new/estimates.json`) for the section.
