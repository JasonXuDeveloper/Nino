# Nino v4 benchmarks

Why v4: what it costs to build a project that uses Nino, and how fast serialization runs, compared with
v3 (3.3.13, the last v3 release) and with [MemoryPack](https://github.com/Cysharp/MemoryPack) 1.21.4.

Build-time numbers were measured on an Intel Xeon 2.80 GHz (4 cores, AVX-512, Ubuntu 24.04, .NET SDK
10.0.402); runtime numbers on an Intel Xeon 2.10 GHz (4 cores, .NET 10.0.13). Absolute times vary between
machines; the ratios are what to look at.

## Build time and generated code

Workload: a synthetic assembly of `N` abstract root classes, each with a chain of 8 derived classes, a sealed
leaf and a struct (`N × 11` types). Members are primitives, strings, `List<T>`, `Dictionary<int, string>`,
references to other `[NinoType]`s and private fields. The harness (Roslyn 4.3 in-process) runs the generator
on the compilation, then emits the whole assembly; it also measures re-running the generator after an edit.
v4 and v3 times are the median of the last three of five runs (v3 at N = 100: of three runs).

### 330 types (N = 30)

| | v3 (3.3.13) | v4 | v4 vs v3 |
|---|---:|---:|---:|
| Generated source | 18.7 MB (363,233 lines) | 1.15 MB (21,679 lines) | **16× smaller** |
| Compiling the generated code (emit) | 56.0 s | 1.27 s | **44× faster** |
| Running the generator | 1.11 s | 0.14 s | **7.9× faster** |
| Re-generating after editing a member | 1.31 s | 0.19 s | **6.9× faster** |
| Re-generating after editing a comment | 1.11 s | 0.19 s | **5.8× faster** |
| IL of the generated code | 2.2 MB | 0.37 MB | **6.0× smaller** |

### 1,100 types (N = 100)

| | v3 (3.3.13) | v4 | v4 vs v3 |
|---|---:|---:|---:|
| Generated source | 62.0 MB (1,204,143 lines) | 3.9 MB (72,429 lines) | **16× smaller** |
| Compiling the generated code (emit) | 636 s | 2.2 s | **289× faster** |
| Running the generator | 5.5 s | 0.68 s | **8.1× faster** |
| Re-generating after editing a member | 4.1 s | 0.57 s | **7.2× faster** |
| Re-generating after editing a comment | 4.1 s | 0.45 s | **9.0× faster** |
| IL of the generated code | 7.3 MB | 1.3 MB | **5.6× smaller** |

v3's compile time grows much faster than the number of types (3.3× the types took 11× the compile time); v4
grows linearly, at about 3.5 KB of source and 2 ms of compile time per type.

### Why v4 builds faster

* **One file per type, one body per type.** A base type dispatches to its subtypes through a registry
  instead of inlining their bodies, and a subtype whose leading members are its base's calls the base's
  `WriteMembers`/`ReadMembers` instead of repeating them.
* **Truly incremental generator.** Each `[NinoType]` becomes an equatable model inside its own transform
  (`ForAttributeWithMetadataName`), so editing one type re-emits only that type's file, and an edit that
  does not change any model (a comment, a method body) re-emits nothing. Models are also cached across
  compilations while none of the syntax trees they depend on changed.
* **No whole-compilation scans.** Types that inherit `[NinoType]` are found from syntax first; only
  candidates are bound.
* **Nothing runs in the IDE that the IDE does not need.** All generated sources are implementation outputs,
  which IDEs skip while you type. Diagnostics come from the analyzer instead of the generator, so models carry no
  source locations and moving code around re-emits nothing.
* **Work shared across types.** Each type's serialized members, raw layout, resolved member formatters and type
  ids are memoized once per compilation and shared by the generator and the analyzer, instead of being
  recomputed for every type that reaches them.
* **Less to compile per member.** Long formatter types are aliased once per file; consecutive unmanaged
  members are written as one reserved block; built-in formatters used by many types are registered once
  per assembly; trivial forwarders carry no `MethodImpl` attributes; generated names are built by hand
  instead of through `SymbolDisplay`.

## Runtime

`src/Nino.Benchmark` (BenchmarkDotNet 0.15.2, `ShortRun` job with 5 warm-up and 15 measured iterations, server
GC) on .NET 10.0.13, Intel Xeon 2.10 GHz, 4 cores. All four benchmark classes ran in one session, v4 and
MemoryPack 1.21.4 side by side. v3 is Nino 3.3.13 from nuget.org, running a port of the same `SimpleTest`
(same data, same attributes) directly before and directly after the v4 session on the same machine; the v3
column is the mean of those two runs. v3 has no polymorphic or pooling benchmarks.

How much to trust a difference: MemoryPack ran in all three sessions, and its times moved by up to 5% between
them on everything except the sub-nanosecond `SimpleStruct` deserialize; v3's own two runs differ by up to
7% on `SimpleClass` deserialize. Differences under about 5% are noise. Payload sizes are about the same
(`SimpleClass`: 1,146 bytes in v4, 1,152 in v3, 1,148 in MemoryPack).

### Serialize (to a reused buffer writer)

| Case | v4 | MemoryPack | v3 | v4 vs MemoryPack | v4 vs v3 |
|---|---:|---:|---:|---:|---:|
| `SimpleClass` | 123 ns | 333 ns | 158 ns | **2.70× faster** | **1.28× faster** |
| `SimpleClass[100]` | 15.5 µs | 37.8 µs | 23.0 µs | **2.44× faster** | **1.48× faster** |
| Polymorphic class | 8.52 µs | 26.0 µs | — | **3.06× faster** | — |
| Polymorphic class array | 68.6 µs | 267 µs | — | **3.89× faster** | — |
| Polymorphic `Foo[]` | 7.05 µs | 25.5 µs | — | **3.62× faster** | — |
| `SimpleStruct` (`int` + `DateTime`) | 1.02 ns | 3.10 ns | 1.18 ns | **3.03× faster** | 1.15× faster |
| `SimpleStruct[100]` | 25.9 ns | 21.4 ns | 20.9 ns | 0.83× | 0.81× |
| `Vector4[10000]` | 4.38 µs | 4.49 µs | 4.44 µs | on par | on par |

`SimpleStruct` has 4 bytes of padding. v4 zeroes it, so the output is deterministic and never carries
leftover memory; v3 and MemoryPack copy whatever the padding contains. For a single value this is one or two
inlined constant stores. For `SimpleStruct[100]` (1.6 KB) the masked copy costs about 5 ns over a plain
`memcpy`, which is why v4 is behind there. Structs without padding (`Vector4[10000]`) are one block copy, as
in the other two.

### Deserialize (new instances)

| Case | v4 | MemoryPack | v3 | v4 vs MemoryPack | v4 vs v3 |
|---|---:|---:|---:|---:|---:|
| `SimpleClass` | 658 ns | 777 ns | 654 ns | **1.18× faster** | on par |
| `SimpleClass[100]` | 72.3 µs | 79.5 µs | 67.7 µs | **1.10× faster** | 0.94× |
| Polymorphic class | 29.1 µs | 33.9 µs | — | **1.17× faster** | — |
| Polymorphic class array | 392 µs | 461 µs | — | **1.18× faster** | — |
| Polymorphic `Foo[]` | 28.6 µs | 38.1 µs | — | **1.33× faster** | — |
| `SimpleStruct[100]` | 176 ns | 185 ns | 181 ns | 1.05× faster | on par |
| `Vector4[10000]` | 125 µs | 125 µs | 127 µs | on par | on par |

Deserializing classes is dominated by allocating the result objects, the same bytes for all three. To see how
much is left, an A/B harness on the build machine built the same `SimpleClass` graph by hand (`new SimpleClass`,
strings copied from spans, the same arrays, list and dictionaries): that floor took 518–636 ns and v4 took
602–662 ns, mostly the cost of decoding the UTF-8 member. v4 stays within reach of that floor while adding
reference tracking, reuse deserialization with pool recycling, version tolerance and payload validation, and
it is ahead of MemoryPack. Against v3, which does none of these, deserialization is on par (`SimpleClass[100]`
is 6% slower, at the edge of the noise). `Vector4[10000]` allocates a 160 KB array on the large object heap
per call, so that row mostly measures gen-2 collections.

### Deserialize into existing instances (pooling)

Nino can deserialize into an existing object graph (reuse deserialization) and take new instances from
`[NinoPooled]` pools; MemoryPack overwrites existing instances.

| Case | Nino new | Nino reusing | MemoryPack new | MemoryPack overwrite | Nino reusing vs overwrite |
|---|---:|---:|---:|---:|---:|
| Pooled node | 736 ns | **224 ns** (0 B) | 720 ns | 576 ns (1,024 B) | **2.58× faster** |
| Pooled leaves, in place | 4.73 µs | **789 ns** (0 B) | 4.40 µs | 5.11 µs (12,800 B) | **6.48× faster** |
| Pooled leaves, pooled elements | 4.73 µs | 3.57 µs (856 B) | 4.40 µs | 5.11 µs (12,800 B) | **1.43× faster** |
| Pooled class | 716 ns | 552 ns (1,080 B) | 794 ns | 616 ns (1,080 B) | 1.12× faster |

The "new" columns allocate the same bytes in both serializers and differ by noise. Reusing is where v4 pulls
away: in-place reuse allocates nothing, and pooled nodes come back without allocating.

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
  The result costs the same as a plain `memcpy`.
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
```

The v3 numbers come from a copy of `SimpleTest.cs` and `SimpleBenchmarkData.cs` that references the `Nino`
3.3.13 package from nuget.org instead of this repository, run with the same arguments.
