# Nino v4 benchmarks

Why v4: what it costs to build a project that uses Nino, and how fast serialization runs, compared with
v3 (3.3.13, the last release of the v3 API) and with [MemoryPack](https://github.com/Cysharp/MemoryPack) 1.22.0.

Every table here is the newest release's: its CI run (`report.yml`) benchmarks v4 against MemoryPack, each case's
Nino and MemoryPack runs on one runner (v3 3.3.13's port of the case too, directly before), and v3 3.3.13's source generator
against the release's on one more runner, puts the reports in the release notes, and writes these tables from them
(`benchmarks/reports/readme.py`), as it does for the README and the documentation site. Generated code sizes do not
depend on the machine; absolute times vary between machines, and the ratios are what to look at.

## Build time and generated code

Workload: a synthetic assembly of `N` abstract root classes, each with a chain of 8 derived classes, a sealed
leaf and a struct (`N × 11` types). Members are primitives, strings, `List<T>`, `Dictionary<int, string>`,
references to other `[NinoType]`s and private fields. The harness (`benchmarks/Nino.GeneratorBenchmark`, Roslyn
in process) runs the generator on the compilation, then emits the whole assembly; it also measures re-running the
generator after an edit. v3's generator and runtime are Nino 3.3.13 from nuget.org, v4's the release's; the two run
one after the other on one runner, and each time is the median of the runs after the warm-up runs (at least one,
and at least 60 s of them; the release notes' "Build: v3 3.3.13 vs v4" section names how many measured). Sizes are
millions of chars (source) and bytes (IL).
These tables come from <!--bench:doc.build.source-->the build section of [v4.0.0-rc.10](https://github.com/JasonXuDeveloper/Nino/releases/tag/v4.0.0-rc.10): AMD EPYC 9V45, 4 cores, .NET 10.0.15, Roslyn 4.3.0.0<!--/bench-->.

### 330 types (N = 30)

The generator rows are short (tenths of a second) and move between runs, so read them as "several times faster"
rather than as exact ratios.

<!--bench:doc.build.30-->
| | v3 (3.3.13) | v4 | v4 vs v3 |
|---|---:|---:|---:|
| Generated source | 18.7 MB (363,325 lines) | 1.53 MB (28,632 lines) | **12× smaller** |
| Compiling the generated code (emit) | 15.9 s | 0.30 s | **53× faster** |
| Running the generator | 0.49 s | 0.05 s | **9.9× faster** |
| Re-generating after editing a member | 0.42 s | 0.04 s | **10× faster** |
| Re-generating after editing a comment | 0.45 s | 0.04 s | **11× faster** |
| IL of the generated code | 2.21 MB | 0.80 MB | **2.8× smaller** |
<!--/bench-->

### 1,100 types (N = 100)

<!--bench:doc.build.100-->
| | v3 (3.3.13) | v4 | v4 vs v3 |
|---|---:|---:|---:|
| Generated source | 62.0 MB (1,204,235 lines) | 5.09 MB (95,552 lines) | **12× smaller** |
| Compiling the generated code (emit) | 193 s | 1.03 s | **188× faster** |
| Running the generator | 1.57 s | 0.19 s | **8.2× faster** |
| Re-generating after editing a member | 1.28 s | 0.14 s | **8.9× faster** |
| Re-generating after editing a comment | 1.26 s | 0.14 s | **9.0× faster** |
| IL of the generated code | 7.34 MB | 2.68 MB | **2.7× smaller** |
<!--/bench-->

v3's compile time grows much faster than the number of types (compare the emit rows: 3.3× the types); v4's grows
about linearly.

## Runtime

`benchmarks/Nino.Benchmark` (`ShortRun` job with 5 warm-up and 15 measured iterations, each case in 3 processes, server
GC), every benchmark class, v4 and MemoryPack 1.22.0 side by side, with MessagePack 3.1.12 and protobuf-net
3.4.40 wherever they can write the same data, and in `SimpleTest`, `PolymorphicTest`, `NullableTest`, `CollectionsTest`'s nullable
collections and `WorkloadTest` Google.Protobuf 3.36.2 and System.Text.Json (.NET 10): the release run spreads the classes over seventeen runners per architecture
and keeps every serializer's run of a case on one. v3 is Nino 3.3.13 from nuget.org, running a port of
every class (same data, same attributes), each on the runner of the same v4 cases directly before them, as far as
3.3.13 can express the case: it has no pool, cannot write a graph with a cycle (`ReferenceTest`) or a list that holds
itself, and has no formatter for `BigInteger`, `Uri`, `Version`, `StringBuilder`, `BitArray`, `CultureInfo`,
`TimeZoneInfo`, `Lazy<T>` or `ILookup`; those cases are n/a for v3.
These tables come from <!--bench:doc.source-->the performance report of [v4.0.0-rc.10](https://github.com/JasonXuDeveloper/Nino/releases/tag/v4.0.0-rc.10): BenchmarkDotNet 0.15.8, .NET 10.0.15, AMD EPYC 9V74 3.69 GHz; v3 3.3.13 from NuGet ran directly before v4 on the same machine<!--/bench-->.

Differences under 5% are reported as on par: MemoryPack runs in both sessions, and its two times show how much the
runner moved. A time under 1 ns (about three clock cycles) cannot be told apart from another: reading a 40-byte struct
measures anywhere from 0.05 to 0.8 ns in both libraries, once BenchmarkDotNet subtracts its empty-method overhead.
v4 under 1 ns against a measurable time is faster by more than that time / 1 ns ("> 1.4× faster": v4 took less than
1 ns, so the bound holds); both under it is on par. Multiples have one decimal below 10 and are whole numbers from 10. A dash
in a "vs" column means the case was not measured there, or v4 is not ahead.

Each library serializes to its own buffer writer: Nino to `NinoArrayBufferWriter`, MemoryPack and MessagePack to
`ArrayBufferWriter<byte>`, protobuf-net to a `MemoryStream` (its fastest writer), Google.Protobuf to
`ArrayBufferWriter<byte>` (its own messages, generated from the schema protobuf-net's contracts produce: the same
messages, subtypes as a `oneof` per level) and System.Text.Json UTF-8 through its source generator into a reused
`Utf8JsonWriter`. Into any other `IBufferWriter<byte>` Nino writes raw and fixed-size values in place too, and
everything else into a pooled buffer that it then copies over, one copy more than these tables measure.
Before the benchmarks run, `--verify` (`benchmarks/Nino.Benchmark/Harness/Verify.cs`) checks every case: each serialize case writes
its payload's bytes, and every deserialize case of a category reads back the same value, deep-compared. The data is
seeded, so every run serializes the same values.

### Charts

One chart per group of cases, each library's time per operation (shorter is faster) with its multiple of Nino v4's,
v3 3.3.13 n/a where it has no port of the case; every number is in the tables below.

<!--bench:doc.charts-->
<details open>
<summary><b>Real workloads</b></summary>

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="https://raw.githubusercontent.com/JasonXuDeveloper/Nino/main/docs/assets/benchmark-workloads-dark.svg">
  <source media="(prefers-color-scheme: light)" srcset="https://raw.githubusercontent.com/JasonXuDeveloper/Nino/main/docs/assets/benchmark-workloads.svg">
  <img src="https://raw.githubusercontent.com/JasonXuDeveloper/Nino/main/docs/assets/benchmark.png" alt="Real workloads: time per operation of Nino v4, v3 and every library, serialize and deserialize">
</picture>

</details>

<details open>
<summary><b>Basic types and structs</b></summary>

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="https://raw.githubusercontent.com/JasonXuDeveloper/Nino/main/docs/assets/benchmark-basic-dark.svg">
  <source media="(prefers-color-scheme: light)" srcset="https://raw.githubusercontent.com/JasonXuDeveloper/Nino/main/docs/assets/benchmark-basic.svg">
  <img src="https://raw.githubusercontent.com/JasonXuDeveloper/Nino/main/docs/assets/benchmark-basic.svg" alt="Basic types and structs: time per operation of Nino v4, v3 and every library, serialize and deserialize">
</picture>

</details>

<details open>
<summary><b>Polymorphism</b></summary>

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="https://raw.githubusercontent.com/JasonXuDeveloper/Nino/main/docs/assets/benchmark-polymorphism-dark.svg">
  <source media="(prefers-color-scheme: light)" srcset="https://raw.githubusercontent.com/JasonXuDeveloper/Nino/main/docs/assets/benchmark-polymorphism.svg">
  <img src="https://raw.githubusercontent.com/JasonXuDeveloper/Nino/main/docs/assets/benchmark-polymorphism.svg" alt="Polymorphism: time per operation of Nino v4, v3 and every library, serialize and deserialize">
</picture>

</details>

<details open>
<summary><b>Nullable values</b></summary>

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="https://raw.githubusercontent.com/JasonXuDeveloper/Nino/main/docs/assets/benchmark-nullable-dark.svg">
  <source media="(prefers-color-scheme: light)" srcset="https://raw.githubusercontent.com/JasonXuDeveloper/Nino/main/docs/assets/benchmark-nullable.svg">
  <img src="https://raw.githubusercontent.com/JasonXuDeveloper/Nino/main/docs/assets/benchmark-nullable.svg" alt="Nullable values: time per operation of Nino v4, v3 and every library, serialize and deserialize">
</picture>

</details>

<details open>
<summary><b>Collections and common types</b></summary>

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="https://raw.githubusercontent.com/JasonXuDeveloper/Nino/main/docs/assets/benchmark-collections-dark.svg">
  <source media="(prefers-color-scheme: light)" srcset="https://raw.githubusercontent.com/JasonXuDeveloper/Nino/main/docs/assets/benchmark-collections.svg">
  <img src="https://raw.githubusercontent.com/JasonXuDeveloper/Nino/main/docs/assets/benchmark-collections.svg" alt="Collections and common types: time per operation of Nino v4, v3 and every library, serialize and deserialize">
</picture>

</details>

<details open>
<summary><b>Reuse and pooling</b></summary>

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="https://raw.githubusercontent.com/JasonXuDeveloper/Nino/main/docs/assets/benchmark-reuse-dark.svg">
  <source media="(prefers-color-scheme: light)" srcset="https://raw.githubusercontent.com/JasonXuDeveloper/Nino/main/docs/assets/benchmark-reuse.svg">
  <img src="https://raw.githubusercontent.com/JasonXuDeveloper/Nino/main/docs/assets/benchmark-reuse.svg" alt="Reuse and pooling: time per operation of Nino v4, v3 and every library, serialize and deserialize">
</picture>

</details>

<details open>
<summary><b>Reference tracking and version tolerance</b></summary>

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="https://raw.githubusercontent.com/JasonXuDeveloper/Nino/main/docs/assets/benchmark-tracking-dark.svg">
  <source media="(prefers-color-scheme: light)" srcset="https://raw.githubusercontent.com/JasonXuDeveloper/Nino/main/docs/assets/benchmark-tracking.svg">
  <img src="https://raw.githubusercontent.com/JasonXuDeveloper/Nino/main/docs/assets/benchmark-tracking.svg" alt="Reference tracking and version tolerance: time per operation of Nino v4, v3 and every library, serialize and deserialize">
</picture>

</details>

<details open>
<summary><b>Others</b></summary>

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="https://raw.githubusercontent.com/JasonXuDeveloper/Nino/main/docs/assets/benchmark-others-dark.svg">
  <source media="(prefers-color-scheme: light)" srcset="https://raw.githubusercontent.com/JasonXuDeveloper/Nino/main/docs/assets/benchmark-others.svg">
  <img src="https://raw.githubusercontent.com/JasonXuDeveloper/Nino/main/docs/assets/benchmark-others.svg" alt="Others: time per operation of Nino v4, v3 and every library, serialize and deserialize">
</picture>

</details>
<!--/bench-->

### Real-world workloads

`WorkloadTest` (`benchmarks/Nino.Benchmark/Workloads`), 12 payloads with the problems real ones have:

| Workload | What it is | What it exercises |
|---|---|---|
| `MediaContent` | jvm-serializers' standard payload, with its standard values | small objects, enums, a string list |
| `StackOverflowPage` | 20 Stack Exchange API questions with answers, comments and users (the models of Jil's, MessagePack-CSharp's and MemoryPack's benchmarks) | nested lists, nullable dates, HTML bodies |
| `RpcCall` | a request and its response, the result an interface (`Ok<List<Item>>`, `NotFound`, `ValidationFailed`) | interface polymorphism, a generic subtype, `decimal`, `Guid` |
| `ProfileCache` | a profile the previous release cached, read by this one (a member removed, two added) | version tolerance: Nino's keyed format, MemoryPack's version-tolerant one |
| `EventStream` | 500 events of an event store: two levels of abstract classes (three families, eight events) in generic envelopes | deep hierarchies, generic subtypes of a non-generic base |
| `LogBatch` | 1,000 structured log events | strings, small dictionaries |
| `StateSnapshot` | a state-sync server's tick: 64 entities whose components are an interface, full or changed only, read into the client's snapshot | interface lists, deserializing into an existing object |
| `LockstepFrames` | a frame-sync broadcast: 10 frames of 8 players' input commands (an interface) | many tiny polymorphic messages |
| `AoiUpdate` | an MMO's area-of-interest update: 200 enter, leave, move, cast and buff events, and chat | an abstract base with five subtypes, structs |
| `WorldRegion` | an SLG map region: 64 x 64 tiles, cities, armies and marches, sharing their alliances | a large struct array, shared references |
| `CardBattleLog` | a card battle's replay: 300 actions in two levels of abstract classes, sharing the card definitions | deep hierarchies, shared references |
| `IdleSave` | an idle game's save from the previous release (members appended since), read into the save the game holds | big numbers, dictionaries, version tolerance, deserializing into an existing object |

Every library writes the same values through its fastest API: Google.Protobuf its own messages, generated from
`workloads.proto`, which protobuf-net's contracts of the same models produce (so both write the same messages: one
payload size), System.Text.Json UTF-8 through its source generator into a reused `Utf8JsonWriter`. Where a library
lacks a feature it takes its usual workaround: MessagePack and protobuf write a copy of a shared object for each
reference (System.Text.Json uses its `Preserve` references), and the libraries that cannot read into an existing
object (all but Nino and MemoryPack's overwrite) read a new one. These are the README's headline numbers. A ratio
in parentheses is how many times faster Nino is; a dash, that it is not ahead by more than 5%.

Serialize (to a reused buffer):

<!--bench:doc.table.workloads.serialize-->
| Workload | Nino | MemoryPack | MessagePack | Google.Protobuf | protobuf-net | System.Text.Json |
|---|---:|---:|---:|---:|---:|---:|
| `MediaContent` (jvm-serializers) | 74.1 ns | 187 ns (**2.5× faster**) | 404 ns (**5.5× faster**) | 332 ns (**4.5× faster**) | 753 ns (**10× faster**) | 660 ns (**8.9× faster**) |
| Stack Exchange API page, 20 questions | 18.1 µs | 28.0 µs (**1.5× faster**) | 91.5 µs (**5.0× faster**) | 139 µs (**7.7× faster**) | 169 µs (**9.3× faster**) | 379 µs (**21× faster**) |
| RPC call, interface result | 182 ns | 428 ns (**2.3× faster**) | 1.18 µs (**6.5× faster**) | 1.71 µs (**9.4× faster**) | 2.82 µs (**15× faster**) | 1.86 µs (**10× faster**) |
| Cached profile, previous version | 215 ns | 690 ns (**3.2× faster**) | 733 ns (**3.4× faster**) | 848 ns (**3.9× faster**) | 1.38 µs (**6.4× faster**) | 913 ns (**4.3× faster**) |
| Event store page, 500 events in generic envelopes | 39.1 µs | 117 µs (**3.0× faster**) | 228 µs (**5.8× faster**) | 395 µs (**10× faster**) | 559 µs (**14× faster**) | 526 µs (**13× faster**) |
| Log batch, 1,000 events | 70.5 µs | 175 µs (**2.5× faster**) | 258 µs (**3.7× faster**) | 450 µs (**6.4× faster**) | 499 µs (**7.1× faster**) | 428 µs (**6.1× faster**) |
| State-sync snapshot, in place | 1.11 µs | 5.95 µs (**5.4× faster**) | 13.3 µs (**12× faster**) | 17.1 µs (**15× faster**) | 47.2 µs (**43× faster**) | 145 µs (**131× faster**) |
| Lockstep, 10 frames of 8 players | 735 ns | 2.66 µs (**3.6× faster**) | 4.14 µs (**5.6× faster**) | 6.06 µs (**8.2× faster**) | 18.0 µs (**24× faster**) | 22.6 µs (**31× faster**) |
| MMO AOI update, 200 events | 923 ns | 3.53 µs (**3.8× faster**) | 9.35 µs (**10× faster**) | 12.1 µs (**13× faster**) | 37.7 µs (**41× faster**) | 124 µs (**134× faster**) |
| SLG map region, shared alliances | 3.08 µs | 6.47 µs (**2.1× faster**) | 54.2 µs (**18× faster**) | 106 µs (**34× faster**) | 225 µs (**73× faster**) | 419 µs (**136× faster**) |
| Card battle replay, shared cards | 4.30 µs | 19.2 µs (**4.5× faster**) | 49.7 µs (**12× faster**) | 54.3 µs (**13× faster**) | 124 µs (**29× faster**) | 144 µs (**33× faster**) |
| Idle save, previous version, in place | 390 ns | 1.36 µs (**3.5× faster**) | 3.70 µs (**9.5× faster**) | 9.88 µs (**25× faster**) | 18.2 µs (**47× faster**) | 26.5 µs (**68× faster**) |
<!--/bench-->

Deserialize (new instances):

<!--bench:doc.table.workloads.deserialize-->
| Workload | Nino | MemoryPack | MessagePack | Google.Protobuf | protobuf-net | System.Text.Json |
|---|---:|---:|---:|---:|---:|---:|
| `MediaContent` (jvm-serializers) | 232 ns | 322 ns (**1.4× faster**) | 783 ns (**3.4× faster**) | 542 ns (**2.3× faster**) | 1.28 µs (**5.5× faster**) | 2.10 µs (**9.0× faster**) |
| Stack Exchange API page, 20 questions | 63.7 µs | 81.1 µs (**1.3× faster**) | 204 µs (**3.2× faster**) | 180 µs (**2.8× faster**) | 244 µs (**3.8× faster**) | 637 µs (**10× faster**) |
| RPC call, interface result | 463 ns | 785 ns (**1.7× faster**) | 2.30 µs (**5.0× faster**) | 1.98 µs (**4.3× faster**) | 3.78 µs (**8.2× faster**) | 5.35 µs (**12× faster**) |
| Cached profile, previous version | 644 ns | 1.02 µs (**1.6× faster**) | 1.44 µs (**2.2× faster**) | 1.94 µs (**3.0× faster**) | 2.65 µs (**4.1× faster**) | 2.37 µs (**3.7× faster**) |
| Event store page, 500 events in generic envelopes | 130 µs | 184 µs (**1.4× faster**) | 565 µs (**4.3× faster**) | 511 µs (**3.9× faster**) | 908 µs (**7.0× faster**) | 911 µs (**7.0× faster**) |
| Log batch, 1,000 events | 222 µs | 327 µs (**1.5× faster**) | 566 µs (**2.6× faster**) | 703 µs (**3.2× faster**) | 888 µs (**4.0× faster**) | 1.02 ms (**4.6× faster**) |
| State-sync snapshot, in place | 2.26 µs | 6.22 µs (**2.8× faster**) | 27.8 µs (**12× faster**) | 32.8 µs (**15× faster**) | 79.8 µs (**35× faster**) | 205 µs (**91× faster**) |
| Lockstep, 10 frames of 8 players | 3.30 µs | 3.30 µs (on par) | 9.17 µs (**2.8× faster**) | 9.28 µs (**2.8× faster**) | 31.7 µs (**9.6× faster**) | 49.0 µs (**15× faster**) |
| MMO AOI update, 200 events | 3.48 µs | 4.66 µs (**1.3× faster**) | 21.9 µs (**6.3× faster**) | 22.2 µs (**6.4× faster**) | 56.8 µs (**16× faster**) | 168 µs (**48× faster**) |
| SLG map region, shared alliances | 13.2 µs | 18.7 µs (**1.4× faster**) | 147 µs (**11× faster**) | 254 µs (**19× faster**) | 300 µs (**23× faster**) | 971 µs (**73× faster**) |
| Card battle replay, shared cards | 8.40 µs | 18.8 µs (**2.2× faster**) | 88.1 µs (**10× faster**) | 95.6 µs (**11× faster**) | 183 µs (**22× faster**) | 287 µs (**34× faster**) |
| Idle save, previous version, in place | 758 ns | 2.67 µs (**3.5× faster**) | 6.90 µs (**9.1× faster**) | 24.1 µs (**32× faster**) | 26.6 µs (**35× faster**) | 52.0 µs (**69× faster**) |
<!--/bench-->

Payload (bytes):

<!--bench:doc.table.workloads.payload-->
| Workload | Nino | MemoryPack | MessagePack | Google.Protobuf | protobuf-net | System.Text.Json |
|---|---:|---:|---:|---:|---:|---:|
| `MediaContent` (jvm-serializers) | 267 B | 321 B | 230 B | 235 B | 235 B | 468 B |
| Stack Exchange API page, 20 questions | 123,333 B | 134,223 B | 115,778 B | 118,660 B | 118,660 B | 208,038 B |
| RPC call, interface result | 544 B | 621 B | 440 B | 490 B | 490 B | 953 B |
| Cached profile, previous version | 477 B | 591 B | 328 B | 378 B | 378 B | 612 B |
| Event store page, 500 events in generic envelopes | 81,058 B | 102,722 B | 89,450 B | 105,247 B | 105,247 B | 196,683 B |
| Log batch, 1,000 events | 142,317 B | 195,328 B | 137,809 B | 155,531 B | 155,531 B | 230,682 B |
| State-sync snapshot, in place | 5,172 B | 4,830 B | 5,192 B | 5,153 B | 5,153 B | 22,783 B |
| Lockstep, 10 frames of 8 players | 1,689 B | 1,441 B | 1,306 B | 2,195 B | 2,195 B | 7,436 B |
| MMO AOI update, 200 events | 8,237 B | 8,183 B | 7,967 B | 9,144 B | 9,144 B | 33,783 B |
| SLG map region, shared alliances | 51,697 B | 51,759 B | 35,226 B | 51,184 B | 51,184 B | 213,662 B |
| Card battle replay, shared cards | 7,099 B | 6,647 B | 19,197 B | 22,677 B | 22,677 B | 32,447 B |
| Idle save, previous version, in place | 3,446 B | 4,969 B | 2,881 B | 3,738 B | 3,738 B | 8,283 B |
<!--/bench-->

### Serialize (to a reused buffer writer)

<!--bench:doc.table.serialize-->
| Case | v4 | v3 | MemoryPack | MessagePack | Google.Protobuf | protobuf-net | System.Text.Json | v4 vs v3 | v4 vs MemoryPack |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| `SimpleClass` | 75.3 ns | 146 ns | 324 ns | 759 ns | 2.24 µs | 2.69 µs | 1.91 µs | **1.9× faster** | **4.3× faster** |
| `SimpleClass[100]` | 8.20 µs | 19.3 µs | 31.2 µs | 72.5 µs | 286 µs | 254 µs | 195 µs | **2.4× faster** | **3.8× faster** |
| Polymorphic class | 4.45 µs | 63.7 µs | 24.1 µs | 106 µs | 295 µs | 633 µs | 984 µs | **14× faster** | **5.4× faster** |
| Polymorphic class array | 38.3 µs | 414 µs | 239 µs | 767 µs | 2.11 ms | 3.74 ms | 6.02 ms | **11× faster** | **6.2× faster** |
| Polymorphic `Foo[]` | 4.46 µs | 60.4 µs | 23.8 µs | 104 µs | 250 µs | 628 µs | 973 µs | **14× faster** | **5.3× faster** |
| `SimpleStruct` (`int` + `DateTime`) | 2.50 ns | 2.47 ns | 4.09 ns | 49.4 ns | 34.9 ns | 109 ns | 72.5 ns | on par | **1.6× faster** |
| `SimpleStruct[100]` | 20.2 ns | 23.6 ns | 24.5 ns | 2.86 µs | 3.25 µs | 6.86 µs | 5.55 µs | **1.2× faster** | **1.2× faster** |
| `Vector4[10000]` | 2.83 µs | 2.83 µs | 2.84 µs | 141 µs | 137 µs | 501 µs | 3.31 ms | on par | on par |
| `NullableFields` (half null) | 32.5 ns | 16.5 ns | 29.8 ns | 161 ns | 70.1 ns | 349 ns | 579 ns | — | — |
| `NullableFields[100]` | 2.24 µs | 2.44 µs | 1.64 µs | 12.7 µs | 9.26 µs | 23.5 µs | 45.2 µs | **1.1× faster** | — |
| `SimpleStruct?` | 4.35 ns | 4.22 ns | 5.16 ns | 62.9 ns | 59.5 ns | 209 ns | 167 ns | on par | **1.2× faster** |
| `SimpleStruct?[100]`, every fifth null | 26.7 ns | 296 ns | 37.9 ns | 3.42 µs | 5.53 µs | 12.2 µs | 14.2 µs | **11× faster** | **1.4× faster** |
| `SimpleClass` reference, null | 3.50 ns | 2.55 ns | 19.6 ns | 26.1 ns | 4.35 ns | 68.9 ns | 20.7 ns | — | **5.6× faster** |
| `SimpleClass[100]`, every fifth null | 6.72 µs | 16.5 µs | 28.7 µs | 58.2 µs | 281 µs | 208 µs | 153 µs | **2.5× faster** | **4.3× faster** |
| `long?[1000]`, every fifth null | 186 ns | 2.07 µs | 216 ns | 3.63 µs | 10.9 µs | 24.6 µs | 22.0 µs | **11× faster** | **1.2× faster** |
| `List<int?>`, 1,000 | 95.0 ns | 2.21 µs | 117 ns | 3.95 µs | 10.2 µs | 22.9 µs | 22.0 µs | **23× faster** | **1.2× faster** |
| `decimal?[1000]` | 343 ns | 1.65 µs | 704 ns | 23.2 µs | 12.5 µs | 47.0 µs | 28.4 µs | **4.8× faster** | **2.1× faster** |
<!--/bench-->

`SimpleStruct` is declared with `[StructLayout(LayoutKind.Sequential, Pack = 4)]`: 12 bytes, no padding, so every
serializer copies it as one block. Without `Pack`, it is 16 bytes with 4 bytes of padding after the `int`. v4 zeroes
padding, so the output is deterministic and never carries leftover memory (an `ArrayPool` array or `stackalloc`
under `[SkipLocalsInit]` can hold anything there); v3 and MemoryPack copy whatever the padding contains. In an
earlier run with the padded struct, that masked copy made `SimpleStruct[100]` serialize take 25.9 ns against
21.4 ns for MemoryPack and 20.9 ns for v3. A single value's padding is cleared by one or two inlined constant stores.

### Deserialize (new instances)

<!--bench:doc.table.deserialize-->
| Case | v4 | v3 | MemoryPack | MessagePack | Google.Protobuf | protobuf-net | System.Text.Json | v4 vs v3 | v4 vs MemoryPack |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| `SimpleClass` | 259 ns | 397 ns | 518 ns | 1.31 µs | 2.66 µs | 4.59 µs | 5.69 µs | **1.5× faster** | **2.0× faster** |
| `SimpleClass[100]` | 27.3 µs | 42.2 µs | 49.8 µs | 142 µs | 346 µs | 464 µs | 645 µs | **1.5× faster** | **1.8× faster** |
| Polymorphic class | 12.8 µs | 27.8 µs | 24.1 µs | 199 µs | 330 µs | 774 µs | 1.12 ms | **2.2× faster** | **1.9× faster** |
| Polymorphic class array | 172 µs | 374 µs | 290 µs | 1.45 ms | 2.11 ms | 5.18 ms | 7.77 ms | **2.2× faster** | **1.7× faster** |
| Polymorphic `Foo[]` | 12.9 µs | 20.3 µs | 22.3 µs | 188 µs | 324 µs | 765 µs | 1.08 ms | **1.6× faster** | **1.7× faster** |
| `SimpleStruct` (`int` + `DateTime`) | < 1 ns | < 1 ns | < 1 ns | 64.2 ns | 52.2 ns | 163 ns | 162 ns | on par | on par |
| `SimpleStruct[100]` | 61.9 ns | 62.3 ns | 76.7 ns | 2.63 µs | 7.04 µs | 7.96 µs | 16.5 µs | on par | **1.2× faster** |
| `Vector4[10000]` | 104 µs | 108 µs | 108 µs | 301 µs | 541 µs | 598 µs | 3.94 ms | on par | on par |
| `NullableFields` (half null) | 20.8 ns | 17.6 ns | 35.5 ns | 248 ns | 113 ns | 388 ns | 609 ns | — | **1.7× faster** |
| `NullableFields[100]` | 3.39 µs | 2.13 µs | 2.85 µs | 26.6 µs | 15.4 µs | 24.1 µs | 58.0 µs | — | — |
| `SimpleStruct?` | 15.8 ns | 14.6 ns | 1.02 ns | 96.2 ns | 84.5 ns | 292 ns | 235 ns | — | — |
| `SimpleStruct?[100]`, every fifth null | 133 ns | 734 ns | 222 ns | 3.45 µs | 9.91 µs | 14.3 µs | 21.2 µs | **5.5× faster** | **1.7× faster** |
| `SimpleClass` reference, null | 2.67 ns | < 1 ns | 10.4 ns | 30.4 ns | 9.98 ns | 78.9 ns | 34.4 ns | — | **3.9× faster** |
| `SimpleClass[100]`, every fifth null | 23.3 µs | 33.6 µs | 39.7 µs | 113 µs | 242 µs | 370 µs | 507 µs | **1.4× faster** | **1.7× faster** |
| `long?[1000]`, every fifth null | 670 ns | 1.19 µs | 676 ns | 4.31 µs | 27.7 µs | 33.8 µs | 20.3 µs | **1.8× faster** | on par |
| `List<int?>`, 1,000 | 421 ns | 1.38 µs | 408 ns | 4.34 µs | 21.7 µs | 32.7 µs | 18.9 µs | **3.3× faster** | on par |
| `decimal?[1000]` | 1.29 µs | 2.06 µs | 1.63 µs | 29.8 µs | 25.7 µs | 47.3 µs | 39.4 µs | **1.6× faster** | **1.3× faster** |
<!--/bench-->


Deserializing classes is dominated by allocating the result objects, the same bytes for all three. To see how
much is left, an A/B harness built the same `SimpleClass` graph by hand (`new SimpleClass`, strings copied from
spans, the same arrays, list and dictionaries): in an earlier run that floor took 518–636 ns and v4 602–662 ns,
mostly the cost of decoding the UTF-8 member. v4 stays within reach of that floor while adding reference
tracking, reuse deserialization with pool recycling, version tolerance and payload validation, and it is ahead of
MemoryPack and v3. `Vector4[10000]` allocates a 160 KB array on the large object heap per call, so that row
mostly measures gen-2 collections.

### Deserialize into existing instances (pooling)

Nino can deserialize into an existing object graph (reuse deserialization) and take new instances from
`[NinoPooled]` pools; MemoryPack overwrites existing instances.

<!--bench:doc.table.reuse-->
| Case | Nino new | Nino reusing | MemoryPack new | MemoryPack overwrite | Nino reusing vs overwrite |
|---|---:|---:|---:|---:|---:|
| Pooled node | 245 ns | **72.7 ns** (0 B) | 326 ns | 276 ns (272 B) | **3.8× faster** |
| Pooled leaves, in place | 1.67 µs | **260 ns** (0 B) | 1.97 µs | 1.97 µs (12,800 B) | **7.6× faster** |
| Pooled leaves, pooled elements | 1.67 µs | **1.46 µs** (? B) | 1.97 µs | 1.97 µs (12,800 B) | **1.3× faster** |
| Pooled class | 259 ns | **190 ns** (0 B) | 519 ns | 413 ns (1,080 B) | **2.2× faster** |
<!--/bench-->

The "new" columns allocate the same bytes in both serializers. Reusing is where v4 pulls away: in-place reuse
allocates nothing, pooled nodes come back without allocating, and strings whose text did not change are kept
(the pooled class's strings were its 1,080 bytes per call before).

### Reference tracking, version tolerance, fixed-size types and common data

Each against the closest MemoryPack mode: `ReferenceTest` (a graph of 200 nodes with shared objects and cycles;
`trackReferences: true` against `GenerateType.CircularReference`), `VersionTolerantTest` (`SimpleClass`'s members;
`NinoFormat.VersionTolerant`, one length per object, against `GenerateType.VersionTolerant`, a length per member)
and `FixedSizeTest` (a 40-byte struct, the same members in a sealed class and 100 of the classes, through the
`byte[]` APIs of both, so allocating the result is part of every call). `CommonTypesTest` adds common type shapes
(`benchmarks/Nino.Benchmark/Fixtures/CommonTypes.cs`): a tree; a padded struct array; an array of structs of fixed size that are not raw (a value tuple member), which
are read and written as blocks; and a string dictionary. `CollectionsTest` covers collections of the base
class library: a list of tuples, an array of key-value pairs, and hashed, queued, linked, sorted and concurrent
collections. `BclTypesTest` covers the other base class library types both libraries have formatters for: `BigInteger`,
`Uri`, `Version`, `StringBuilder`, `BitArray`, `CultureInfo`, `TimeZoneInfo`, `Lazy<T>`, memory and sequences, read-only,
lookup and blocking collections, and the immutable collections. Its `TimeZoneInfo` case measures a design difference
more than speed: Nino writes a system time zone by its id and reads it back from the system's cached zones, while
MemoryPack writes and parses the zone's whole serialized string on every call.

<!--bench:doc.table.extra-->
| Case | Serialize v4 | MemoryPack | v4 vs MemoryPack | Deserialize v4 | MemoryPack | v4 vs MemoryPack |
|---|---:|---:|---:|---:|---:|---:|
| Object graph, 200 tracked nodes | 3.88 µs | 22.0 µs | **5.7× faster** | 4.53 µs | 15.6 µs | **3.4× faster** |
| `TolerantClass` | 79.1 ns | 408 ns | **5.2× faster** | 262 ns | 567 ns | **2.2× faster** |
| `TolerantClass[100]` | 8.39 µs | 39.7 µs | **4.7× faster** | 28.1 µs | 55.9 µs | **2.0× faster** |
| `FixedStruct` | 4.86 ns | 4.91 ns | on par | < 1 ns | < 1 ns | on par |
| `FixedClass` | 5.04 ns | 13.7 ns | **2.7× faster** | 4.48 ns | 11.9 ns | **2.7× faster** |
| `FixedClass[100]` | 236 ns | 923 ns | **3.9× faster** | 585 ns | 792 ns | **1.4× faster** |
| `TreeNode`, 1,365 nodes | 13.7 µs | 20.3 µs | **1.5× faster** | 50.8 µs | 68.2 µs | **1.3× faster** |
| `InventoryItem[1000]` (padded struct) | 87.2 ns | 130 ns | **1.5× faster** | 531 ns | 548 ns | on par |
| `Waypoint[1000]` (fixed size, not raw) | 329 ns | 354 ns | **1.1× faster** | 1.48 µs | 1.49 µs | on par |
| `Dictionary<string, string>`, 1,000 | 9.80 µs | 30.4 µs | **3.1× faster** | 39.0 µs | 61.3 µs | **1.6× faster** |
<!--/bench-->

Deserializing `FixedStruct` is one 40-byte copy, about a clock cycle in both. Serializing it is an allocation
and one 40-byte copy from the value straight into the array, the same machine code in both. Up to 4.0.0-rc.2,
`NinoSerializer.Serialize(T)` took the value by value, so a value read from a field (as in this benchmark) was
first copied to the stack, before the allocation; since 4.0.0-rc.3 it takes `in T`, as MemoryPack does.

`InventoryItem` holds one byte of padding per element (an `int`, a `short` and a `bool` in 8 bytes). v4 writes it as
zero, a masked copy where MemoryPack makes a plain one, which is the extra work in v4's time for that row.
MemoryPack's output carries whatever the memory held there: two `InventoryItem[2]` of equal values, one built in a
buffer that had held `0xCD` bytes, serialize to different bytes in MemoryPack 1.22.0 (`CD` in each padding byte)
and to the same bytes in v4. Likewise `List<(int, long)>` in `CollectionsTest` is 12 bytes per item in v4 and 16 in
MemoryPack, which copies the tuple's memory, padding included; that memory is 12 bytes on Windows x86 .NET (see
`docs/pitfalls.md`), and on Mono the items are in another order.

Neither serializer allocates on the serialize paths above (0 B per call), and deserialization allocates only
the result objects.

## Runtime optimizations

* **Struct formatters, inlined end to end.** Built-in and generated formatters are structs, and element
  formatters are type parameters (`ListFormatter<TList, T, TFormatter>`), so the JIT/AOT compiler inlines
  element serialization into the collection loop. Members call `default(TFormatter).Serialize(...)`: a
  direct call, no virtual dispatch.
* **Blocks instead of fields.** Consecutive unmanaged members are reserved and written as one block (one
  bounds check), unmanaged arrays, lists and raw `[NinoType]` structs are single block copies, and arrays and
  lists of fixed-size elements are written straight into a `byte[]` of their exact size, without a scratch buffer.
  A raw nullable reads its presence byte and its value under one bounds check, and multi-dimensional arrays take
  the block paths of flat ones.
* **Copies sized to the CPU.** Up to 64 bytes (on 64-bit runtimes) or 128 bytes (with 32-byte vectors) are copied
  inline, as overlapping loads and stores of the widest vector that fits (up to 64 bytes the same way in one call on
  32-bit x86, WebAssembly and .NET on Arm32, where unaligned 8-byte accesses cannot fault); up to 32 KB (64-byte
  vectors) or 16 KB (32-byte vectors, from 256 bytes) as vectors with aligned stores; on Arm64 up to 64 KB as pairs of
  16-byte loads and stores (`ldp`/`stp`), 64 bytes a step from an aligned source address, which beats the C library's
  `memcpy` there (with only 16-byte vectors on x64 with SSE and WebAssembly, `memmove` from 65 bytes); beyond, with
  `memmove`, which is several times slower than the vector loop at these sizes on some CPUs (GitHub's EPYC 7763
  runners). Reads of 6 KB or more into a new array use `memmove`, whose `rep movsb` writes fresh cache lines without
  reading them first, except on AMD CPUs, where it is slower than vector stores there too (1,000
  `KeyValuePair<int, int>` read in 418 ns instead of 465 ns on GitHub's EPYC 7763 runners); a list of up to 64 bytes
  read in place calls `memmove` directly, cheaper than the out-of-line copy for so few bytes.
* **Zeroing only where it pays.** A new array that a read fills whole, as one block copy or as blocks (padded tuples
  and block structs, their padding written as zero), is allocated uninitialized from 6 KB: below, the runtime's
  uninitialized allocation costs more than zeroing on GitHub's x64 and Arm64 runners. One filled value by value
  (`DateTimeOffset`s, values Untrusted mode checks a field at a time) is allocated zeroed. Slots a read overwrites
  are not cleared first, in new or existing lists and arrays.
* **Padded tuples as vector shuffles.** Arrays and lists of value tuples, `KeyValuePair`s and other padded values are
  written and read a group of values at a time: with AVX-512, by permutes across a vector; elsewhere, each 16-byte
  lane is shuffled from at most two 16-byte windows (`vpshufb` on AVX2 and SSSE3, `TBL` on Arm64, swizzle on
  WebAssembly), with the cheaper of an unaligned and a grid-aligned plan for each type. Without AVX-512 VBMI (and not on Arm64), a
  value whose exact group would take more than four vectors (5-byte blocks such as `(byte, int)`: 32 values in five
  32-byte vectors) moves in groups of as many values as one to four vectors hold (six `(byte, int)` a 32-byte vector,
  twelve in four 16-byte ones), the rest of the last vector rewritten by the next group, instead of one value at a
  time. The check of whether two windows share a 32-byte load is inlined: left a call in the AVX2 group loop, it
  spilled the vectors around each load, and AVX2 writes took twice as long as SSSE3's. On Arm64, a plan whose windows
  lie on the 16-byte grid loads each window of a group once, before any lookup, so the JIT gives them consecutive
  registers and every `TBL` takes one window or two consecutive ones as they are: four `(int, long)` are written from
  two `ldp` by three lookups and read from three loads by four, with no register copies (loaded for each lookup, the
  loads two lookups share took a copy each). Each loop step moves a whole group and advances two cursors, without an
  index multiply. Values of 8 bytes or less (`(byte, int)`, `(short, int)`) move eight at a time as one 64-byte
  permute (`vpermb` with AVX-512 VBMI); on Arm64 each 16 bytes written is one `TBL` over just the 16-byte quarters its
  bytes come from (one to four consecutive registers, each a micro-op on Neoverse cores), only those quarters are
  loaded and only the group's bytes stored, and the permute's indices stay in registers (a `Vector512` is a struct of
  four there, which as a parameter was copied through the stack every group). Where the stores are aligned, the
  unaligned head and tail of the output are covered by whole groups that overlap the body, so the time does not depend
  on where the buffer starts.
* **Layout flags folded by the JIT.** Whether a type's memory layout equals the wire layout, and whether it
  has padding, are `static readonly` values, which tiered compilation turns into constants: on 64-bit
  runtimes a raw struct costs a plain copy. Other runtimes (32-bit, old Mono) convert field by field into
  the same bytes.
* **SIMD masked copy for padding.** Padding is zeroed so output is deterministic. Arrays of padded structs
  are copied through a `Vector<T>` mask (AND with a repeated byte mask) in the same pass as the copy: no
  second pass, which would reload freshly stored bytes and stall on store forwarding. Stores are aligned to
  the vector width (the block usually follows a 4-byte length, so unaligned stores would split cache lines
  every other store), the loop is unrolled 4×, and the unaligned head and tail vectors overlap the body.
  On 100 padded `SimpleStruct`s (16 bytes each without `Pack`, 1.6 KB) this costs about 5 ns over a plain `memcpy`.
  The loop indexes with native-sized offsets, and on Arm64 loads and stores pairs of 16-byte vectors (`ldp`/`stp`), as the C library's `memcpy`
  does. A struct without padding, or one
  declared with `[StructLayout(LayoutKind.Sequential, Pack = 4)]`, skips the mask and is a plain block copy;
  `LayoutKind.Auto` structs are not raw at all and are written field by field.
* **Store-only padding for single values.** A single padded value is written, then its padding runs are
  zeroed with plain stores, never read back. The first two runs are `static readonly` offsets and lengths, so
  the JIT emits them as one or two constant stores, inlined.
* **Collection loops specialized on element types.** Pair loops of `Dictionary<TKey, TValue>` live in a helper
  generic over the key and value types only (the dictionary type itself would be a reference-type parameter,
  making the whole loop shared generic code); exact `Dictionary<,>` members get a formatter without the
  dictionary type parameter, which also shortens generated code. Raw `HashSet<T>` and `Dictionary<TKey, TValue>` are written from their
  entries array when none has been removed (the layouts of .NET 64-bit and 32-bit and of .NET Framework and Mono are
  checked at startup), without the enumerator's version check and field reloads on every element: as blocks of a
  struct whose memory is the entry (`SetEntryBlock`, `MapEntryBlock`), so the vector permutes that pack padded
  structs move several entries at a time, each key and value taken out of its entry (the netstandard builds: the
  registered kernels, else one at a time); with removed
  entries, or no layout recognized, through the enumerator, out of line. A new one that a read creates (default
  comparer) is filled the other way round: entries and buckets written as `Add` would, after the constructor or
  `EnsureCapacity` sized it, without `Add`'s comparer, free-list, resize and version checks per element; the bucket
  is .NET's fast modulo where the table keeps its multiplier, else the remainder, and the hashing (31-bit mask, bucket
  bias) is found at startup by filling a collection and checking it through the public API. Empty collections skip
  the out-of-line element loops altogether.
* **Specialized code behind shared generics.** `Serialize<T>` of an array is shared code (arrays are
  reference types), where each static access is a dictionary lookup. The raw array path looks up one object
  and lets the array formatter, compiled for the element type, do the write.
* **Rare paths out of line.** A top-level call inlines its whole formatter chain into one method, under one
  inlining budget, and the JIT compiles a branch whose condition is a method call even for the types where it is
  dead. So only the common path is inline: vector loops of block copies, copies of more than 64 bytes,
  element-by-element loops, the copy of a raw read, UTF-16 strings and recycling are calls (`NoInlining`), and checks
  made of several helper calls are one `static readonly` field. The JIT checks an inline against the budget by the
  callee's whole IL, and counts a method it inlines on its own judgement (on profile data), with everything inlined
  into it, against the caller's budget: so the formatters' and the generated code's entry points are
  `AggressiveInlining` too, and the larger helpers (the copy, Latin-1 widening and narrowing) are split into small
  parts. `benchmarks/asm/asm.py compare` lists every method whose machine code changed against a
  base commit, and the signs of a spent budget (`Reader:Skip` or `NinoUnsafe:Read` as calls, the cursor kept in
  memory), so a change cannot slow down the types it does not touch unnoticed.
* **No checks the JIT cannot drop.** Element loops (array and list fallbacks, in-place reads, the padding mask,
  recycling, pools, reference tables, immutable collections copied into rented arrays) walk a `ref` to the
  elements instead of indexing, so there is no bounds check per element, and no covariance check when storing
  into a `T[]` of reference types. Block sizes are read from a `static readonly` field in the out-of-line block
  loops, so their divisions and multiplications compile to shifts and adds. Length overflow checks stay: one
  per call, against malicious input.
* **Writer and reader.** `Writer` writes into one contiguous rented buffer (the hot path is a length check
  and an unaligned store; growth is out of line). `Reader` is a span with an index: one unsigned bounds
  check per read. Lengths from the payload are validated against the remaining bytes before allocating. Loops
  over elements work on a local copy of the reader or writer, so the cursor stays in a register, except where the
  elements' formatters take the writer's address anyway (strings, classes): those loops run on the caller's writer,
  without a copy in and out per collection.
* **No shared mutable state on hot paths.** Scratch buffers are thread-static and taken out of their slot
  while in use; formatter lookup is a static generic field; polymorphic tables are open-addressing arrays of
  16-byte entries (four per 64-byte cache line), at most half full, read lock free. Serializing threads never
  contend for a cache line.
* **Polymorphism by id.** Subtypes are identified by a 4-byte FNV-1a hash of their name, looked up in the
  table above; subtypes register lazily, in buckets, so startup does not touch unused types.
* **Top-level calls without lookups.** On Roslyn 4.8+ (.NET 8 SDK and later) the generator intercepts
  `NinoSerializer`/`NinoDeserializer` calls whose type it knows and calls the formatter directly, so a call
  costs what calling the formatter by hand costs (e.g. deserializing an open class 15.5 → 11.6 ns). Other
  calls are inlined into the caller with the formatter passed in, so shared generic code over reference types
  does not look it up twice.
* **Subtype dispatch in generated code.** A polymorphic base reads its subtypes' ids in a generated `switch`
  and calls their formatters directly; the registry is only the fallback for subtypes of other assemblies.
* **Pools without contention.** `NinoPool<T>` keeps a per-thread stack and a shared overflow; its hot fields
  sit on their own 64-byte cache line (an explicit-layout base class), so threads renting different types
  never share a line.
* **Strings in one pass.** Text whose chars are all at most U+00FF is narrowed to one byte a char with SIMD when
  written and widened straight into the new string when read; other text is one block copy of its UTF-16 chars.
  `[NinoUtf8]` members use the vectorized UTF-8 transcoder of the runtime.

## Reproducing

Build time and generated code (`benchmarks/Nino.GeneratorBenchmark`):

```sh
dotnet run -c Release --project benchmarks/Nino.GeneratorBenchmark -- 30 8          # this repository's generator
# another version: build its Nino.Generator and Nino.Core, then
dotnet run -c Release --project benchmarks/Nino.GeneratorBenchmark -- 30 8 \
    --generator <path>/Nino.Generator.dll --core <path>/Nino.Core.dll
# v3 3.3.13, as the release run does (the project's restore puts both packages in the NuGet cache)
dotnet run -c Release --project benchmarks/Nino.GeneratorBenchmark -- 30 8 --no-analyzers \
    --generator ~/.nuget/packages/nino.generator/3.3.13/analyzers/dotnet/cs/Nino.Generator.dll \
    --core ~/.nuget/packages/nino.serialization/3.3.13/lib/net8.0/Nino.Core.dll
```

Arguments are the number of root classes and the depth of each hierarchy; `--dump <dir>` writes the
generated files; `--layouts` adds a layout baseline of every type (the analyzer then checks each against it);
`--runs <n>` sets the number of runs (five by default); `--no-analyzers` skips the analysis; `--json <file>` writes
the medians, sizes and machine as JSON (the release run turns them into its build section with
`benchmarks/reports/generator_report.py`). Each line reports one run, the last the median of the runs after the
first.

Runtime (`benchmarks/Nino.Benchmark`, BenchmarkDotNet):

```sh
dotnet run -c Release --project benchmarks/Nino.Benchmark -- --filter '*'
dotnet run -c Release --project benchmarks/Nino.Benchmark -- --filter '*ReferenceTest*' '*VersionTolerantTest*' '*FixedSizeTest*'   # some classes only
```

The suite has twelve classes, all against MemoryPack 1.22.0, and against MessagePack and protobuf-net wherever they
can write the same data ([benchmarks/README.md](../benchmarks/README.md#messagepack-and-protobuf-net) lists the cases
they cannot; `SimpleTest`, `PolymorphicTest`, `NullableTest`, `CollectionsTest`'s nullable collections and `WorkloadTest`
also against Google.Protobuf and System.Text.Json): `SimpleTest`, `PolymorphicTest`, `NullableTest` (a class of eight
nullable members, half of them null, one and 100; a `SimpleStruct?`, `SimpleStruct?[100]`, a null `SimpleClass` and
`SimpleClass[100]` with nulls; MemoryPack through `SafeNullable<T>`), `GenericPolymorphicTest` (subtypes through a
non-generic base and through closed instances of a generic base, whose union MemoryPack registers at runtime), `PooledTest` and `PooledGraphTest` (reuse and
pooling), `ReferenceTest` (a graph with shared references and cycles: `trackReferences` against MemoryPack's
`GenerateType.CircularReference`), `VersionTolerantTest` (`SimpleClass`'s members as `NinoFormat.VersionTolerant`
against MemoryPack's `GenerateType.VersionTolerant`, one and 100) and `FixedSizeTest` (types of fixed size serialized
to a `byte[]` of exactly that size: an unmanaged struct, a sealed class of the same members and an array of 100 of
them), `CommonTypesTest` (players, orders, shapes, a tree, padded structs and a string dictionary), `CollectionsTest`
(collections of the base class library, and `long?[]`, `List<int?>` and `decimal?[]` with every fifth value null), `BclTypesTest` (other base class library types and the immutable
collections) and `ScatteredTest` (arrays of a million objects whose order in the array is not their order in memory,
which every other class's data, allocated in order and in the cache, never shows).
[benchmarks/README.md](../benchmarks/README.md) lists their cases.

The v3 numbers come from the v3 build of [`benchmarks/Nino.Benchmark`](../benchmarks/Nino.Benchmark)
(`NINO_BENCH_V3=true`, [`V3/`](../benchmarks/Nino.Benchmark/V3)), every class built against the `Nino` 3.3.13
package from nuget.org, each feature in v3's own way: what shares objects writes copies (v3 tracks no references),
version tolerance is v3's `WEAK_VERSION_TOLERANCE` in a project of its own, what v4 reads in place v3 reads anew (it
reads no object in place; a list keeps itself and gets new elements), and a type v3 writes no overload for goes
through one it does (a `Memory<int>` through its span, an immutable collection read as an array and built from it).
Each port's header lists what it does differently and what it leaves out, and why. [`benchmarks/run.sh`](../benchmarks/run.sh) runs the whole protocol (v3 directly before the full v4
suite) and prints v4 against v3. The release workflow runs the same protocol for every release and publishes the
result in the release notes ("v3 3.3.13 vs v4 (same machine)"), where the documentation site and these tables take
their v3 ratios from.
