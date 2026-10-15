# Nino v4 benchmarks

Why v4: what it costs to build a project that uses Nino, and how fast serialization runs, compared with
v3 (the `main` branch before v4) and with [MemoryPack](https://github.com/Cysharp/MemoryPack) 1.21.4.

All numbers were measured on one machine (Intel Xeon 2.80 GHz, 4 cores, AVX-512, Ubuntu 24.04, .NET SDK
10.0.402). Absolute times vary between machines; the ratios are what to look at.

## Build time and generated code

Workload: a synthetic assembly of `N` abstract root classes, each with a chain of 8 derived classes, a sealed
leaf and a struct (`N × 11` types). Members are primitives, strings, `List<T>`, `Dictionary<int, string>`,
references to other `[NinoType]`s and private fields. The harness (Roslyn 4.3 in-process) runs the generator
on the compilation, then emits the whole assembly; it also measures re-running the generator after an edit.
v4 times are the median of the last three of five runs; v3 times are the third of three runs.

### 330 types (N = 30)

| | v3 (`main`) | v4 | v4 vs v3 |
|---|---:|---:|---:|
| Generated source | 70.6 MB (1,366,440 lines) | 1.15 MB (21,679 lines) | **61× smaller** |
| Compiling the generated code (emit) | 35.9 s | 1.27 s | **28× faster** |
| Running the generator | 4.2 s | 0.14 s | **30× faster** |
| Re-generating after editing a member | 3.7 s | 0.19 s | **20× faster** |
| Re-generating after editing a comment | 5.2 s | 0.19 s | **28× faster** |
| IL of the generated code | 4.9 MB | 0.37 MB | **13× smaller** |

### 1,100 types (N = 100)

| | v3 (`main`) | v4 | v4 vs v3 |
|---|---:|---:|---:|
| Generated source | 236 MB (4,553,540 lines) | 3.9 MB (72,429 lines) | **61× smaller** |
| Compiling the generated code (emit) | 314 s | 2.2 s | **145× faster** |
| Running the generator | 30.8 s | 0.68 s | **45× faster** |
| Re-generating after editing a member | 7.8 s | 0.57 s | **14× faster** |
| Re-generating after editing a comment | 16.7 s | 0.45 s | **38× faster** |
| IL of the generated code | 16.3 MB | 1.3 MB | **12× smaller** |

v3's cost grows faster than the number of types (3.3× the types took 8.8× the compile time), because
every polymorphic base inlined the bodies of all its subtypes. v4 grows linearly: about 3.5 KB of source and
2 ms of compile time per type (the generated subtype dispatch added since the first measurement costs a few
hundred bytes per type and saves a registry lookup per polymorphic read).

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

`src/Nino.Benchmark` (BenchmarkDotNet 0.15.2, server GC) on .NET 10.0.13, v4 and MemoryPack 1.21.4 in the
same run (15 iterations; the rows marked * were re-run with 20 iterations, as the first run was disturbed).
The v4 vs v3 column comes from an earlier back-to-back run of v4 and `main` on the same machine. Payload sizes
are about the same (e.g. `SimpleClass`: 1,146 bytes in v4, 1,148 in MemoryPack).

### Serialize (to a reused buffer writer)

| Case | v4 | MemoryPack | v4 vs MemoryPack | v4 vs v3 |
|---|---:|---:|---:|---:|
| `SimpleClass` | 151 ns | 450 ns | **2.97× faster** | 1.47× faster |
| `SimpleClass[100]` | 17.0 µs | 48.7 µs | **2.86× faster** | 1.39× faster |
| Polymorphic class | 11.5 µs | 31.5 µs | **2.72× faster** | 2.00× faster |
| Polymorphic class array | 89.6 µs | 388 µs | **4.33× faster** | 2.04× faster |
| Polymorphic `Foo[]` | 9.9 µs | 31.4 µs | **3.15× faster** | 1.94× faster |
| `SimpleStruct` (`int` + `DateTime`) | 2.3 ns | 5.8 ns | **2.50× faster** | 1.8× faster |
| `SimpleStruct[100]` * | 28.6 ns | 30.6 ns | 1.07× faster | 1.12× faster |
| `Vector4[10000]` * | 4.06 µs | 4.10 µs | on par | on par |

`SimpleStruct` has 4 bytes of padding. v4 zeroes it, so the output is deterministic; v3 and MemoryPack copy
whatever the padding contains. With the masked SIMD copy this costs nothing measurable on arrays, and a
single value's padding is cleared by one or two inlined constant stores.

### Deserialize (new instances)

| Case | v4 | MemoryPack | v4 vs MemoryPack |
|---|---:|---:|---:|
| `SimpleClass` | 964 ns | 1,129 ns | **1.17× faster** |
| `SimpleClass[100]` | 106 µs | 134 µs | **1.26× faster** |
| Polymorphic class * | 36.3 µs | 44.8 µs | **1.23× faster** |
| Polymorphic class array | 406 µs | 562 µs | **1.38× faster** |
| Polymorphic `Foo[]` | 37.0 µs | 43.1 µs | **1.16× faster** |
| `SimpleStruct[100]` * | 295 ns | 295 ns | on par |
| `Vector4[10000]` * | 173 µs | 147 µs | 0.85× |

`Vector4[10000]` allocates a 160 KB array on the large object heap per call, so it measures gen-2 collections
more than either serializer (the two runs gave 0.96× and 0.85× with ±15% error); the copy itself is one
`memcpy` in both.

### Deserialize into existing instances (pooling)

Nino can deserialize into an existing object graph (`ref` deserialization) and take new instances from
`[NinoPooled]` pools; MemoryPack overwrites existing instances.

| Case | Nino new | Nino reusing | MemoryPack new | MemoryPack overwrite |
|---|---:|---:|---:|---:|
| Pooled node | 873 ns | **280 ns** (0 B) | 952 ns | 727 ns (1,024 B) |
| Pooled leaves, in place | 5.23 µs | **1.18 µs** (0 B) | 5.38 µs | 5.80 µs (12,800 B) |
| Pooled leaves, pooled elements | 5.23 µs | 3.82 µs (856 B) | 5.38 µs | 5.80 µs (12,800 B) |
| Pooled class | 780 ns | 623 ns (1,080 B) | 1,044 ns | 857 ns (1,080 B) |

Deserialization is dominated by allocating the result objects, which is identical for all three (same bytes
allocated per call), and server-GC noise is about ±7% here. To see how much is left, the A/B harness also
builds the same object graph by hand (`new SimpleClass`, strings copied from spans, the same arrays, list and
dictionaries filled with the same entries): that floor takes 518–636 ns, and v4 deserializes `SimpleClass` in
602–662 ns, within 5–15% of it (the difference is mostly decoding the UTF-8 member). v3 sits at the same
place. Deserializing classes is therefore bound by allocation, not by the serializer: v4 matches v3 while
adding reference tracking, ref deserialization with pool recycling, version tolerance and payload validation,
and stays ahead of MemoryPack.

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
dotnet run -c Release --project src/Nino.Benchmark -- --filter '*Nino*' '*MemoryPack*' --iterationCount 15 --warmupCount 5
```
