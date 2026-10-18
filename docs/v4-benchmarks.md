# Nino v4 benchmarks

Why v4: what it costs to build a project that uses Nino, and how fast serialization runs, compared with
v3 (3.3.13, the last release of the v3 API) and with [MemoryPack](https://github.com/Cysharp/MemoryPack) 1.21.4.

The build-time numbers were measured on one machine, an Intel Xeon 2.10 GHz (4 cores, AVX-512, .NET SDK 10.0.402);
generated code sizes do not depend on the machine. The runtime tables are those of the newest release: its CI run
(`report.yml`) benchmarks v4 against MemoryPack and v3 3.3.13 on one runner, puts the report in the release notes,
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
<!--bench:doc.source-->the performance report of [v4.0.0-rc.4](https://github.com/JasonXuDeveloper/Nino/releases/tag/v4.0.0-rc.4): BenchmarkDotNet 0.15.8, .NET 10.0.13, INTEL XEON PLATINUM 8573C 3.10 GHz; v3 3.3.13 from NuGet ran directly before v4 on the same machine<!--/bench-->.

Differences under 5% are reported as on par: MemoryPack runs in both sessions, and its two times show how much the
runner moved. A time under 0.1 ns (a third of a clock cycle) cannot be told apart from an empty method: v4 under it
against a measurable time is faster, too fast to measure, with no ratio. A dash in a "vs" column means the case was
not measured there, or v4 is not ahead.

### Serialize (to a reused buffer writer)

<!--bench:doc.table.serialize-->
| Case | v4 | MemoryPack | v3 | v4 vs MemoryPack | v4 vs v3 |
|---|---:|---:|---:|---:|---:|
| `SimpleClass` | 87.7 ns | 296 ns | 133 ns | **3.38× faster** | **1.51× faster** |
| `SimpleClass[100]` | 9.97 µs | 31.4 µs | 19.3 µs | **3.15× faster** | **1.94× faster** |
| Polymorphic class | 6.62 µs | 21.3 µs | — | **3.22× faster** | — |
| Polymorphic class array | 59.5 µs | 229 µs | — | **3.86× faster** | — |
| Polymorphic `Foo[]` | 5.69 µs | 21.5 µs | — | **3.78× faster** | — |
| `SimpleStruct` (`int` + `DateTime`) | 0.82 ns | 2.78 ns | 1.46 ns | **3.39× faster** | **1.78× faster** |
| `SimpleStruct[100]` | 14.2 ns | 14.6 ns | 13.6 ns | on par | on par |
| `Vector4[10000]` | 3.90 µs | 3.91 µs | 3.90 µs | on par | on par |
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
| `SimpleClass` | 444 ns | 577 ns | 474 ns | **1.30× faster** | **1.07× faster** |
| `SimpleClass[100]` | 45.4 µs | 57.2 µs | 49.7 µs | **1.26× faster** | **1.10× faster** |
| Polymorphic class | 17.6 µs | 24.9 µs | — | **1.41× faster** | — |
| Polymorphic class array | 265 µs | 348 µs | — | **1.31× faster** | — |
| Polymorphic `Foo[]` | 16.7 µs | 24.4 µs | — | **1.46× faster** | — |
| `SimpleStruct` (`int` + `DateTime`) | 0.39 ns | 0.39 ns | 0.41 ns | on par | **1.06× faster** |
| `SimpleStruct[100]` | 86.6 ns | 97.6 ns | 86.3 ns | **1.13× faster** | on par |
| `Vector4[10000]` | 106 µs | 109 µs | 103 µs | on par | on par |
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
| Pooled node | 536 ns | **185 ns** (0 B) | 548 ns | 427 ns (0 B) | **2.30× faster** |
| Pooled leaves, in place | 3.29 µs | **468 ns** (0 B) | 3.33 µs | 3.75 µs (12,800 B) | **8.02× faster** |
| Pooled leaves, pooled elements | 3.29 µs | **2.27 µs** (0 B) | 3.33 µs | 3.75 µs (12,800 B) | **1.65× faster** |
| Pooled class | 447 ns | **220 ns** (0 B) | 578 ns | 487 ns (1,080 B) | **2.21× faster** |
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
base; a tree; a padded struct array; and a string dictionary.

<!--bench:doc.table.extra-->
| Case | Serialize v4 | MemoryPack | v4 vs MemoryPack | Deserialize v4 | MemoryPack | v4 vs MemoryPack |
|---|---:|---:|---:|---:|---:|---:|
| Object graph, 200 tracked nodes | 4.47 µs | 23.3 µs | **5.22× faster** | 5.16 µs | 16.5 µs | **3.20× faster** |
| `TolerantClass` | 85.9 ns | 376 ns | **4.37× faster** | 431 ns | 629 ns | **1.46× faster** |
| `TolerantClass[100]` | 9.64 µs | 39.2 µs | **4.06× faster** | 46.0 µs | 64.4 µs | **1.40× faster** |
| `FixedStruct` | 8.98 ns | 13.0 ns | **1.45× faster** | < 0.1 ns | 0.39 ns | **faster** (too fast to measure) |
| `FixedClass` | 9.93 ns | 18.5 ns | **1.86× faster** | 7.87 ns | 18.6 ns | **2.36× faster** |
| `FixedClass[100]` | 634 ns | 1.43 µs | **2.26× faster** | 1.04 µs | 1.16 µs | **1.12× faster** |
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
* **UTF-16 strings by default** (one block copy); `[NinoUtf8]` members use the vectorized UTF-8 transcoder
  of the runtime.

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
dotnet run -c Release --project src/Nino.Benchmark -- --filter '*' --launchCount 1 --warmupCount 5 --iterationCount 15
dotnet run -c Release --project src/Nino.Benchmark -- --filter '*ReferenceTest*' '*VersionTolerantTest*' '*FixedSizeTest*' \
    --launchCount 1 --warmupCount 5 --iterationCount 15                                  # some classes only
```

The suite has eight classes, all against MemoryPack 1.21.4 (the tables above come from the first four):
`SimpleTest` and `PolymorphicTest` (also against MessagePack), `PooledTest` and `PooledGraphTest` (reuse and
pooling), `ReferenceTest` (a graph with shared references and cycles: `trackReferences` against MemoryPack's
`GenerateType.CircularReference`), `VersionTolerantTest` (`SimpleClass`'s members as `NinoFormat.VersionTolerant`
against MemoryPack's `GenerateType.VersionTolerant`, one and 100) and `FixedSizeTest` (types of fixed size serialized
to a `byte[]` of exactly that size: an unmanaged struct, a sealed class of the same members and an array of 100 of
them), and `CommonTypesTest` (players, orders, shapes, a tree, padded structs and a string dictionary).
[benchmarks/README.md](../benchmarks/README.md) lists their cases.

The v3 numbers come from [`benchmarks/v3`](../benchmarks/v3), the same `SimpleTest` built against the `Nino` 3.3.13
package from nuget.org. [`benchmarks/run.sh`](../benchmarks/run.sh) runs the whole protocol (v3, the full v4 suite,
v3 again) and prints v4 against both v3 runs. The release workflow runs the same protocol on its CI runner for every
release and publishes the result in the release notes ("v3 3.3.13 vs v4 (same machine)"); once the newest v4
release carries that section, the documentation site shows its ratios instead of the numbers above.
