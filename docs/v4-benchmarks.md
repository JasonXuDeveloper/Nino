# Nino v4 benchmarks

Why v4: what it costs to build a project that uses Nino, and how fast serialization runs, compared with
v3 (3.3.13, the last v3 release) and with [MemoryPack](https://github.com/Cysharp/MemoryPack) 1.21.4.

Build-time numbers come from two runs, named in each table: the 330-type workload was measured again for this
revision on an Intel Xeon 2.10 GHz (4 cores, .NET SDK 10.0.402), the 1,100-type timings come from an earlier run on
an Intel Xeon 2.80 GHz (4 cores, AVX-512, Ubuntu 24.04, .NET SDK 10.0.402). Generated code sizes do not depend on
the machine and are those of this revision everywhere. Absolute times vary between machines; the ratios are what to
look at.

The runtime numbers below were measured on this revision, on the 2.10 GHz machine. Each release's CI run
(`report.yml`) publishes the same report, against MemoryPack and against v3 3.3.13 on the same runner, in the
release notes; the documentation site shows the newest one.

## Build time and generated code

Workload: a synthetic assembly of `N` abstract root classes, each with a chain of 8 derived classes, a sealed
leaf and a struct (`N × 11` types). Members are primitives, strings, `List<T>`, `Dictionary<int, string>`,
references to other `[NinoType]`s and private fields. The harness (Roslyn 4.3 in-process) runs the generator
on the compilation, then emits the whole assembly; it also measures re-running the generator after an edit.
v4 and v3 times are the median of the last three of five runs (v3 at N = 100: of three runs).

### 330 types (N = 30)

Measured for this revision, v3 and v4 on the same machine (Intel Xeon 2.10 GHz, 4 cores). Generator times on this
machine moved by up to 3× between sessions (the last runs are still warming up), so read the generator rows as
"several times faster" rather than as exact ratios.

| | v3 (3.3.13) | v4 | v4 vs v3 |
|---|---:|---:|---:|
| Generated source | 18.7 MB (363,233 lines) | 1.31 MB (23,215 lines) | **14× smaller** |
| Compiling the generated code (emit) | 43.9 s | 1.10 s | **40× faster** |
| Running the generator | 1.34 s | 0.32 s | **4.2× faster** |
| Re-generating after editing a member | 1.11 s | 0.09 s | **12× faster** |
| Re-generating after editing a comment | 1.06 s | 0.10 s | **11× faster** |
| IL of the generated code | 2.21 MB | 0.41 MB | **5.3× smaller** |

v4 times are the median of three sessions, each the median of its last three of five runs; v3 is the median of the
last three of five runs.

### 1,100 types (N = 100)

Sizes are this revision's. Times come from the earlier run on the Intel Xeon 2.80 GHz (an earlier v4 revision; v3 at
the median of three runs). For comparison, this revision on the 2.10 GHz machine took 2.2 s to emit, 0.48 s to run
the generator and 0.63 s / 0.41 s to re-generate after a member / comment edit (median of the last three of five
runs); v3 was not re-run at this size.

| | v3 (3.3.13) | v4 | v4 vs v3 |
|---|---:|---:|---:|
| Generated source | 62.0 MB (1,204,143 lines) | 4.37 MB (77,535 lines) | **14× smaller** |
| Compiling the generated code (emit), earlier run | 636 s | 2.2 s | **289× faster** |
| Running the generator, earlier run | 5.5 s | 0.68 s | **8.1× faster** |
| Re-generating after editing a member, earlier run | 4.1 s | 0.57 s | **7.2× faster** |
| Re-generating after editing a comment, earlier run | 4.1 s | 0.45 s | **9.0× faster** |
| IL of the generated code | 7.3 MB | 1.49 MB | **4.9× smaller** |

v3's compile time grows much faster than the number of types (in the earlier run, 3.3× the types took 11× the
compile time); v4 grows linearly, at about 4 KB of source and 2 to 3 ms of compile time per type.

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
* **Fewer members per type.** Each type's registration is emitted into the assembly's registry rather than into a
  method of its formatter, and a body formatter's two nesting flags are one property (`Traits`), so every formatter
  and twin declares fewer members for the compiler to bind and emit.

## Runtime

`src/Nino.Benchmark` (BenchmarkDotNet 0.15.2, `ShortRun` job with 5 warm-up and 15 measured iterations, server
GC) on .NET 10.0.13, Intel Xeon 2.10 GHz, 4 cores, run with `benchmarks/run.sh` on this revision. All seven
benchmark classes ran in one session, v4 and MemoryPack 1.21.4 side by side. v3 is Nino 3.3.13 from nuget.org,
running a port of the same `SimpleTest` (same data, same attributes) directly before and directly after the v4
session on the same machine; the v3 column is the mean of those two runs. v3 has no polymorphic, pooling,
reference, version-tolerant or fixed-size benchmarks. Each release's notes carry the same report from a CI runner.

How much to trust a difference: MemoryPack ran in all three sessions, and its times moved by 0.84–1.44× between
them, and v3's own two runs differ by up to 16% (`SimpleClass` deserialize: 624 and 538 ns). This machine is
shared and noisy, so differences under about 10% are noise. Payload sizes are about the same (`SimpleClass`:
1,150 bytes in v4, 1,152 in v3, 1,148 in MemoryPack).

### Serialize (to a reused buffer writer)

| Case | v4 | MemoryPack | v3 | v4 vs MemoryPack | v4 vs v3 |
|---|---:|---:|---:|---:|---:|
| `SimpleClass` | 94.2 ns | 321 ns | 142 ns | **3.40× faster** | **1.50× faster** |
| `SimpleClass[100]` | 10.4 µs | 33.1 µs | 22.3 µs | **3.18× faster** | **2.14× faster** |
| Polymorphic class | 6.82 µs | 24.4 µs | — | **3.58× faster** | — |
| Polymorphic class array | 58.9 µs | 278 µs | — | **4.72× faster** | — |
| Polymorphic `Foo[]` | 6.16 µs | 23.5 µs | — | **3.82× faster** | — |
| `SimpleStruct` (`int` + `DateTime`) | 1.57 ns | 2.75 ns | 1.58 ns | **1.76× faster** | on par |
| `SimpleStruct[100]` | 14.1 ns | 15.7 ns | 14.8 ns | 1.11× faster | on par |
| `Vector4[10000]` | 4.27 µs | 4.21 µs | 4.32 µs | on par | on par |

`SimpleStruct` is declared with `[StructLayout(LayoutKind.Sequential, Pack = 4)]`: 12 bytes, no padding, so every
serializer copies it as one block. Without `Pack`, it is 16 bytes with 4 bytes of padding after the `int`. v4 zeroes
padding, so the output is deterministic and never carries leftover memory (an `ArrayPool` array or `stackalloc`
under `[SkipLocalsInit]` can hold anything there); v3 and MemoryPack copy whatever the padding contains. In an
earlier run with the padded struct, that masked copy made `SimpleStruct[100]` serialize take 25.9 ns against
21.4 ns for MemoryPack and 20.9 ns for v3. A single value's padding is cleared by one or two inlined constant stores.

### Deserialize (new instances)

| Case | v4 | MemoryPack | v3 | v4 vs MemoryPack | v4 vs v3 |
|---|---:|---:|---:|---:|---:|
| `SimpleClass` | 539 ns | 707 ns | 581 ns | **1.31× faster** | 1.08× faster |
| `SimpleClass[100]` | 56.7 µs | 67.8 µs | 61.8 µs | **1.20× faster** | 1.09× faster |
| Polymorphic class | 22.6 µs | 37.6 µs | — | **1.66× faster** | — |
| Polymorphic class array | 306 µs | 412 µs | — | **1.35× faster** | — |
| Polymorphic `Foo[]` | 22.0 µs | 29.7 µs | — | **1.35× faster** | — |
| `SimpleStruct[100]` | 125 ns | 134 ns | 121 ns | 1.08× faster | on par |
| `Vector4[10000]` | 118 µs | 112 µs | 113 µs | on par | on par |

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

| Case | Nino new | Nino reusing | MemoryPack new | MemoryPack overwrite | Nino reusing vs overwrite |
|---|---:|---:|---:|---:|---:|
| Pooled node | 636 ns | **219 ns** (0 B) | 650 ns | 495 ns (1,024 B) | **2.26× faster** |
| Pooled leaves, in place | 3.99 µs | **511 ns** (0 B) | 4.09 µs | 4.61 µs (12,800 B) | **9.02× faster** |
| Pooled leaves, pooled elements | 3.99 µs | 2.69 µs (856 B) | 4.09 µs | 4.61 µs (12,800 B) | **1.71× faster** |
| Pooled class | 531 ns | 462 ns (1,080 B) | 710 ns | 573 ns (1,080 B) | **1.24× faster** |

The "new" columns allocate the same bytes in both serializers. Reusing is where v4 pulls away: in-place reuse
allocates nothing, and pooled nodes come back without allocating.

### Reference tracking, version tolerance and fixed-size types

Each against the closest MemoryPack mode: `ReferenceTest` (a graph of 200 nodes with shared objects and cycles;
`trackReferences: true` against `GenerateType.CircularReference`), `VersionTolerantTest` (`SimpleClass`'s members;
`NinoFormat.VersionTolerant`, one length per object, against `GenerateType.VersionTolerant`, a length per member)
and `FixedSizeTest` (a 40-byte struct, the same members in a sealed class and 100 of the classes, through the
`byte[]` APIs of both, so allocating the result is part of every call).

| Case | Serialize v4 | MemoryPack | v4 vs MemoryPack | Deserialize v4 | MemoryPack | v4 vs MemoryPack |
|---|---:|---:|---:|---:|---:|---:|
| Object graph, 200 tracked nodes | 5.32 µs | 24.9 µs | **4.67× faster** | 6.49 µs | 19.3 µs | **2.97× faster** |
| `TolerantClass` | 92.5 ns | 415 ns | **4.49× faster** | 538 ns | 784 ns | **1.46× faster** |
| `TolerantClass[100]` | 10.7 µs | 41.1 µs | **3.85× faster** | 55.6 µs | 77.2 µs | **1.39× faster** |
| `FixedStruct` | 12.1 ns | 11.5 ns | on par | — | — | — |
| `FixedClass` | 12.7 ns | 25.2 ns | **1.99× faster** | 11.6 ns | 25.0 ns | **2.15× faster** |
| `FixedClass[100]` | 897 ns | 1.78 µs | **1.99× faster** | 1.35 µs | 1.40 µs | on par |

Deserializing `FixedStruct` is one 40-byte copy, under a nanosecond in both, and not shown. Serializing it compiles
to the same machine code in both when the value is a local or a parameter (an allocation and one 40-byte copy);
the benchmark reads it from a static field, and `NinoSerializer.Serialize(T)` takes the value by value where
`MemoryPackSerializer.Serialize(in T)` takes a reference, so Nino copies it to the stack first.

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
  On `SimpleStruct[100]` (1.6 KB) this costs about 5 ns over a plain `memcpy`. A struct without padding, or one
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

The suite has seven classes, all against MemoryPack 1.21.4 (the tables above come from the first four):
`SimpleTest` and `PolymorphicTest` (also against MessagePack), `PooledTest` and `PooledGraphTest` (reuse and
pooling), `ReferenceTest` (a graph with shared references and cycles: `trackReferences` against MemoryPack's
`GenerateType.CircularReference`), `VersionTolerantTest` (`SimpleClass`'s members as `NinoFormat.VersionTolerant`
against MemoryPack's `GenerateType.VersionTolerant`, one and 100) and `FixedSizeTest` (types of fixed size serialized
to a `byte[]` of exactly that size: an unmanaged struct, a sealed class of the same members and an array of 100 of
them). [benchmarks/README.md](../benchmarks/README.md) lists their cases.

The v3 numbers come from [`benchmarks/v3`](../benchmarks/v3), the same `SimpleTest` built against the `Nino` 3.3.13
package from nuget.org. [`benchmarks/run.sh`](../benchmarks/run.sh) runs the whole protocol (v3, the full v4 suite,
v3 again) and prints v4 against both v3 runs. The release workflow runs the same protocol on its CI runner for every
release and publishes the result in the release notes ("v3 3.3.13 vs v4 (same machine)"); once the newest v4
release carries that section, the documentation site shows its ratios instead of the numbers above.
