# Nino v4 benchmarks

Why v4: what it costs to build a project that uses Nino, and how fast serialization runs, compared with
v3 (3.3.13, the last release of the v3 API) and with [MemoryPack](https://github.com/Cysharp/MemoryPack) 1.21.4.

The build-time numbers were measured on one machine, an Intel Xeon 2.10 GHz (4 cores, AVX-512, .NET SDK 10.0.402);
generated code sizes do not depend on the machine. The runtime tables are those of the newest release: its CI run
(`report.yml`) benchmarks v4 against MemoryPack, each class on one runner (v3 3.3.13 on the `SimpleTest` runner),
puts the report in the release notes,
and writes these tables from it (`benchmarks/readme.py`), as it does for the README and the documentation site.
Absolute times vary between machines; the ratios are what to look at.

## Build time and generated code

Workload: a synthetic assembly of `N` abstract root classes, each with a chain of 8 derived classes, a sealed
leaf and a struct (`N × 11` types). Members are primitives, strings, `List<T>`, `Dictionary<int, string>`,
references to other `[NinoType]`s and private fields. The harness (Roslyn 4.3 in-process) runs the generator
on the compilation, then emits the whole assembly; it also measures re-running the generator after an edit.
Times are the median of the last three of five runs (v3 at N = 100: the median of three runs, which took
7.6 to 7.8 minutes each). v3 and v4 ran on the same machine (Intel Xeon 2.10 GHz, 4 cores), one after the other.

### 330 types (N = 30)

The generator rows are short (tenths of a second) and move between sessions on this machine, so read them as
"several times faster" rather than as exact ratios.

| | v3 (3.3.13) | v4 | v4 vs v3 |
|---|---:|---:|---:|
| Generated source | 18.7 MB (363,233 lines) | 1.31 MB (23,215 lines) | **14× smaller** |
| Compiling the generated code (emit) | 42.9 s | 0.84 s | **51× faster** |
| Running the generator | 1.01 s | 0.32 s | **3.1× faster** |
| Re-generating after editing a member | 1.03 s | 0.12 s | **8.3× faster** |
| Re-generating after editing a comment | 1.01 s | 0.13 s | **7.8× faster** |
| IL of the generated code | 2.21 MB | 0.42 MB | **5.3× smaller** |

### 1,100 types (N = 100)

| | v3 (3.3.13) | v4 | v4 vs v3 |
|---|---:|---:|---:|
| Generated source | 62.0 MB (1,204,143 lines) | 4.38 MB (77,535 lines) | **14× smaller** |
| Compiling the generated code (emit) | 462 s | 2.1 s | **221× faster** |
| Running the generator | 4.6 s | 0.42 s | **11× faster** |
| Re-generating after editing a member | 3.5 s | 0.48 s | **7.4× faster** |
| Re-generating after editing a comment | 4.0 s | 0.44 s | **9.1× faster** |
| IL of the generated code | 7.3 MB | 1.50 MB | **4.9× smaller** |

v3's compile time grows much faster than the number of types (3.3× the types took 11× the compile time); v4 grows
about linearly, at about 4 KB of source and 2 to 3 ms of compile time per type.

## Runtime

`src/Nino.Benchmark` (`ShortRun` job with 5 warm-up and 15 measured iterations, server GC), all eight benchmark
classes in one session, v4 and MemoryPack 1.21.4 side by side. v3 is Nino 3.3.13 from nuget.org, running a port of
the same `SimpleTest` (same data, same attributes) on the same runner directly before the v4 session; it has no
polymorphic, pooling, reference, version-tolerant or fixed-size benchmarks. These tables come from
<!--bench:doc.source-->the performance report of [v4.0.0-rc.5](https://github.com/JasonXuDeveloper/Nino/releases/tag/v4.0.0-rc.5): BenchmarkDotNet 0.15.8, .NET 10.0.13, AMD EPYC 7763 2.62 GHz; v3 3.3.13 from NuGet ran directly before v4 on the same machine<!--/bench-->.

Differences under 5% are reported as on par: MemoryPack runs in both sessions, and its two times show how much the
runner moved. A time under 0.1 ns (a third of a clock cycle) cannot be told apart from an empty method: v4 under it
against a measurable time is faster, too fast to measure, with no ratio. A dash in a "vs" column means the case was
not measured there, or v4 is not ahead.

### Serialize (to a reused buffer writer)

<!--bench:doc.table.serialize-->
| Case | v4 | MemoryPack | v3 | v4 vs MemoryPack | v4 vs v3 |
|---|---:|---:|---:|---:|---:|
| `SimpleClass` | 107 ns | 383 ns | 179 ns | **3.57× faster** | **1.67× faster** |
| `SimpleClass[100]` | 11.9 µs | 38.7 µs | 25.9 µs | **3.25× faster** | **2.17× faster** |
| Polymorphic class | 6.86 µs | 35.4 µs | — | **5.17× faster** | — |
| Polymorphic class array | 79.4 µs | 420 µs | — | **5.29× faster** | — |
| Polymorphic `Foo[]` | 6.64 µs | 50.9 µs | — | **7.67× faster** | — |
| `SimpleStruct` (`int` + `DateTime`) | 4.53 ns | 5.09 ns | 3.51 ns | **1.12× faster** | — |
| `SimpleStruct[100]` | 22.2 ns | 21.1 ns | 20.3 ns | on par | — |
| `Vector4[10000]` | 3.56 µs | 3.40 µs | 3.23 µs | on par | — |
<!--/bench-->

`SimpleStruct` is declared with `[StructLayout(LayoutKind.Sequential, Pack = 4)]`: 12 bytes, no padding, so every
serializer copies it as one block. Without `Pack`, it is 16 bytes with 4 bytes of padding after the `int`. v4 zeroes
padding, so the output is deterministic and never carries leftover memory (an `ArrayPool` array or `stackalloc`
under `[SkipLocalsInit]` can hold anything there); v3 and MemoryPack copy whatever the padding contains. In an
earlier run with the padded struct, that masked copy made `SimpleStruct[100]` serialize take 25.9 ns against
21.4 ns for MemoryPack and 20.9 ns for v3. A single value's padding is cleared by one or two inlined constant stores.

### Deserialize (new instances)

<!--bench:doc.table.deserialize-->
| Case | v4 | MemoryPack | v3 | v4 vs MemoryPack | v4 vs v3 |
|---|---:|---:|---:|---:|---:|
| `SimpleClass` | 439 ns | 729 ns | 544 ns | **1.66× faster** | **1.24× faster** |
| `SimpleClass[100]` | 46.3 µs | 73.1 µs | 58.7 µs | **1.58× faster** | **1.27× faster** |
| Polymorphic class | 16.4 µs | 28.8 µs | — | **1.75× faster** | — |
| Polymorphic class array | 239 µs | 367 µs | — | **1.53× faster** | — |
| Polymorphic `Foo[]` | 15.1 µs | 28.0 µs | — | **1.86× faster** | — |
| `SimpleStruct` (`int` + `DateTime`) | 0.77 ns | 1.47 ns | 0.70 ns | **1.90× faster** | — |
| `SimpleStruct[100]` | 86.5 ns | 98.2 ns | 86.7 ns | **1.14× faster** | on par |
| `Vector4[10000]` | 118 µs | 120 µs | 123 µs | on par | on par |
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
| Pooled node | 276 ns | **91.1 ns** (0 B) | 315 ns | 259 ns (170 B) | **2.85× faster** |
| Pooled leaves, in place | 1.84 µs | **280 ns** (0 B) | 1.90 µs | 2.21 µs (12,800 B) | **7.90× faster** |
| Pooled leaves, pooled elements | 1.84 µs | **1.51 µs** (856 B) | 1.90 µs | 2.21 µs (12,800 B) | **1.47× faster** |
| Pooled class | 458 ns | **296 ns** (0 B) | 732 ns | 622 ns (1,080 B) | **2.11× faster** |
<!--/bench-->

The "new" columns allocate the same bytes in both serializers. Reusing is where v4 pulls away: in-place reuse
allocates nothing, pooled nodes come back without allocating, and strings whose text did not change are kept
(the pooled class's strings were its 1,080 bytes per call before).

### Reference tracking, version tolerance, fixed-size types and common data

Each against the closest MemoryPack mode: `ReferenceTest` (a graph of 200 nodes with shared objects and cycles;
`trackReferences: true` against `GenerateType.CircularReference`), `VersionTolerantTest` (`SimpleClass`'s members;
`NinoFormat.VersionTolerant`, one length per object, against `GenerateType.VersionTolerant`, a length per member)
and `FixedSizeTest` (a 40-byte struct, the same members in a sealed class and 100 of the classes, through the
`byte[]` APIs of both, so allocating the result is part of every call). `CommonTypesTest` adds data shaped like
that of common applications (`src/Nino.Benchmark/CommonTypes.cs`): players with a struct list, a string dictionary,
bytes and strings; orders with `Guid`, `DateTimeOffset`, `decimal` and a list of classes; shapes through an abstract
base; a tree; a padded struct array; and a string dictionary. `CollectionsTest` covers collections of the base
class library: a list of tuples, an array of key-value pairs, and hashed, queued, linked, sorted and concurrent
collections. `BclTypesTest` covers the other base class library types both libraries have formatters for: `BigInteger`,
`Uri`, `Version`, `StringBuilder`, `BitArray`, `CultureInfo`, `TimeZoneInfo`, `Lazy<T>`, memory and sequences, read-only,
lookup and blocking collections, and the immutable collections.

<!--bench:doc.table.extra-->
| Case | Serialize v4 | MemoryPack | v4 vs MemoryPack | Deserialize v4 | MemoryPack | v4 vs MemoryPack |
|---|---:|---:|---:|---:|---:|---:|
| Object graph, 200 tracked nodes | 2.77 µs | 16.2 µs | **5.84× faster** | 2.97 µs | 10.9 µs | **3.68× faster** |
| `TolerantClass` | 114 ns | 520 ns | **4.55× faster** | 441 ns | 763 ns | **1.73× faster** |
| `TolerantClass[100]` | 12.4 µs | 51.7 µs | **4.15× faster** | 46.2 µs | 75.9 µs | **1.64× faster** |
| `FixedStruct` | 9.24 ns | 9.39 ns | on par | 0.39 ns | 1.03 ns | **2.63× faster** |
| `FixedClass` | 9.38 ns | 24.2 ns | **2.58× faster** | 9.34 ns | 21.6 ns | **2.31× faster** |
| `FixedClass[100]` | 799 ns | 1.75 µs | **2.19× faster** | 1.09 µs | 1.29 µs | **1.19× faster** |
| `List<Player>`, 100 | 9.83 µs | 25.4 µs | **2.59× faster** | 31.0 µs | 44.0 µs | **1.42× faster** |
| `List<Order>`, 100 | 9.70 µs | 15.3 µs | **1.58× faster** | 28.7 µs | 34.0 µs | **1.18× faster** |
| `List<Shape>`, 1,000 (abstract base) | 1.84 µs | 23.3 µs | **12.65× faster** | 10.2 µs | 16.2 µs | **1.59× faster** |
| `TreeNode`, 1,365 nodes | 11.1 µs | 20.1 µs | **1.81× faster** | 48.8 µs | 66.7 µs | **1.37× faster** |
| `InventoryItem[1000]` (padded struct) | 90.0 ns | 94.3 ns | on par | 434 ns | 448 ns | on par |
| `Dictionary<string, string>`, 1,000 | 7.65 µs | 31.6 µs | **4.13× faster** | 37.5 µs | 59.1 µs | **1.58× faster** |
<!--/bench-->

Deserializing `FixedStruct` is one 40-byte copy, about a clock cycle in both. Serializing it is an allocation
and one 40-byte copy from the value straight into the array, the same machine code in both. Up to 4.0.0-rc.2,
`NinoSerializer.Serialize(T)` took the value by value, so a value read from a field (as in this benchmark) was
first copied to the stack, before the allocation; since 4.0.0-rc.3 it takes `in T`, as MemoryPack does.

Neither serializer allocates on the serialize paths above (0 B per call), and deserialization allocates only
the result objects.

## Runtime optimizations

* **Struct formatters, inlined end to end.** Built-in and generated formatters are structs, and element
  formatters are type parameters (`ListFormatter<TList, T, TFormatter>`), so the JIT/AOT compiler inlines
  element serialization into the collection loop. Members call `default(TFormatter).Serialize(...)`: a
  direct call, no virtual dispatch.
* **Blocks instead of fields.** Consecutive unmanaged members are reserved and written as one block (one
  bounds check), unmanaged arrays, lists and raw `[NinoType]` structs are single block copies, and arrays of
  raw elements are written to `byte[]` results allocated at their exact size, without zero-initialization.
* **Layout flags folded by the JIT.** Whether a type's memory layout equals the wire layout, and whether it
  has padding, are `static readonly` values, which tiered compilation turns into constants: on 64-bit
  runtimes a raw struct costs a plain copy. Other runtimes (32-bit, old Mono) convert field by field into
  the same bytes.
* **SIMD masked copy for padding.** Padding is zeroed so output is deterministic. Arrays of padded structs
  are copied through a `Vector<T>` mask (AND with a repeated byte mask) in the same pass as the copy: no
  second pass, which would reload freshly stored bytes and stall on store forwarding. Stores are aligned to
  the vector width (the block usually follows a 4-byte length, so unaligned stores would split cache lines
  every other store), the loop is unrolled 4×, and the unaligned head and tail vectors overlap the body.
  On 100 padded `SimpleStruct`s (16 bytes each without `Pack`, 1.6 KB) this costs about 5 ns over a plain `memcpy`. A struct without padding, or one
  declared with `[StructLayout(LayoutKind.Sequential, Pack = 4)]`, skips the mask and is a plain block copy;
  `LayoutKind.Auto` structs are not raw at all and are written field by field.
* **Store-only padding for single values.** A single padded value is written, then its padding runs are
  zeroed with plain stores, never read back. The first two runs are `static readonly` offsets and lengths, so
  the JIT emits them as one or two constant stores, inlined.
* **Collection loops specialized on element types.** Pair loops of `Dictionary<TKey, TValue>` live in a helper
  generic over the key and value types only (the dictionary type itself would be a reference-type parameter,
  making the whole loop shared generic code); exact `Dictionary<,>` members get a formatter without the
  dictionary type parameter, which also shortens generated code.
* **Specialized code behind shared generics.** `Serialize<T>` of an array is shared code (arrays are
  reference types), where each static access is a dictionary lookup. The raw array path looks up one object
  and lets the array formatter, compiled for the element type, do the write.
* **Writer and reader.** `Writer` writes into one contiguous rented buffer (the hot path is a length check
  and an unaligned store; growth is out of line). `Reader` is a span with an index: one unsigned bounds
  check per read. Lengths from the payload are validated against the remaining bytes before allocating.
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

Build time and generated code (`src/Nino.GeneratorBenchmark`):

```sh
dotnet run -c Release --project src/Nino.GeneratorBenchmark -- 30 8          # this repository's generator
# another version: build its Nino.Generator and Nino.Core, then
dotnet run -c Release --project src/Nino.GeneratorBenchmark -- 30 8 \
    --generator <path>/Nino.Generator.dll --core <path>/Nino.Core.dll
```

Arguments are the number of root classes and the depth of each hierarchy; `--dump <dir>` writes the
generated files. Each line reports one of five runs.

Runtime (`src/Nino.Benchmark`, BenchmarkDotNet):

```sh
dotnet run -c Release --project src/Nino.Benchmark -- --filter '*' --launchCount 3 --warmupCount 5 --iterationCount 15
dotnet run -c Release --project src/Nino.Benchmark -- --filter '*ReferenceTest*' '*VersionTolerantTest*' '*FixedSizeTest*' \
    --launchCount 3 --warmupCount 5 --iterationCount 15                                  # some classes only
```

The suite has nine classes, all against MemoryPack 1.21.4 (the tables above come from the first four):
`SimpleTest` and `PolymorphicTest` (also against MessagePack), `PooledTest` and `PooledGraphTest` (reuse and
pooling), `ReferenceTest` (a graph with shared references and cycles: `trackReferences` against MemoryPack's
`GenerateType.CircularReference`), `VersionTolerantTest` (`SimpleClass`'s members as `NinoFormat.VersionTolerant`
against MemoryPack's `GenerateType.VersionTolerant`, one and 100) and `FixedSizeTest` (types of fixed size serialized
to a `byte[]` of exactly that size: an unmanaged struct, a sealed class of the same members and an array of 100 of
them), `CommonTypesTest` (players, orders, shapes, a tree, padded structs and a string dictionary), `CollectionsTest`
(collections of the base class library) and `BclTypesTest` (other base class library types and the immutable
collections).
[benchmarks/README.md](../benchmarks/README.md) lists their cases.

The v3 numbers come from [`benchmarks/v3`](../benchmarks/v3), the same `SimpleTest` built against the `Nino` 3.3.13
package from nuget.org. [`benchmarks/run.sh`](../benchmarks/run.sh) runs the whole protocol (v3, the full v4 suite,
v3 again) and prints v4 against both v3 runs. The release workflow runs the same protocol on its CI runner for every
release and publishes the result in the release notes ("v3 3.3.13 vs v4 (same machine)"); once the newest v4
release carries that section, the documentation site shows its ratios instead of the numbers above.
