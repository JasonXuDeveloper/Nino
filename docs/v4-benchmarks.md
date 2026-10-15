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
Times are the third of three runs.

### 330 types (N = 30)

| | v3 (`main`) | v4 | v4 vs v3 |
|---|---:|---:|---:|
| Generated source | 70.6 MB (1,366,440 lines) | 0.98 MB (20,259 lines) | **72× smaller** |
| Compiling the generated code (emit) | 35.9 s | 0.89 s | **40× faster** |
| Running the generator | 4.2 s | 0.32 s | **13× faster** |
| Re-generating after editing a member | 3.7 s | 0.20 s | **18× faster** |
| Re-generating after editing a comment | 5.2 s | 0.16 s | **32× faster** |
| IL of the generated code | 4.9 MB | 0.32 MB | **15× smaller** |

### 1,100 types (N = 100)

| | v3 (`main`) | v4 | v4 vs v3 |
|---|---:|---:|---:|
| Generated source | 236 MB (4,553,540 lines) | 3.3 MB (67,369 lines) | **72× smaller** |
| Compiling the generated code (emit) | 314 s | 1.8 s | **179× faster** |
| Running the generator | 30.8 s | 0.51 s | **60× faster** |
| Re-generating after editing a member | 7.8 s | 0.59 s | **13× faster** |
| Re-generating after editing a comment | 16.7 s | 0.42 s | **40× faster** |
| IL of the generated code | 16.3 MB | 1.2 MB | **14× smaller** |

v3's cost grows faster than the number of types (3.3× the types took 8.8× the compile time), because
every polymorphic base inlined the bodies of all its subtypes. v4 grows linearly: about 3 KB of source and
1.6 ms of compile time per type.

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
* **Less to compile per member.** Long formatter types are aliased once per file; consecutive unmanaged
  members are written as one reserved block; built-in formatters used by many types are registered once
  per assembly; trivial forwarders carry no `MethodImpl` attributes; generated names are built by hand
  instead of through `SymbolDisplay`.

## Runtime

`src/Nino.Benchmark` (BenchmarkDotNet 0.15.2, 15 iterations, server GC) on .NET 10.0.13, with v4, v3
(`main`) and MemoryPack 1.21.4 measured on the same machine in back-to-back runs. Payload sizes are about the
same for all three (e.g. `SimpleClass`: 1,146 bytes in v4, 1,148 in MemoryPack).

### Serialize (to a reused buffer writer)

| Case | v3 | v4 | MemoryPack | v4 vs v3 | v4 vs MemoryPack |
|---|---:|---:|---:|---:|---:|
| `SimpleClass` | 230 ns | 157 ns | 418 ns | **1.47× faster** | **2.67× faster** |
| `SimpleClass[100]` | 23.5 µs | 17.0 µs | 41.8 µs | **1.39× faster** | **2.46× faster** |
| Polymorphic class | 26.5 µs | 13.3 µs | 29.6 µs | **2.00× faster** | **2.23× faster** |
| Polymorphic class array | 235 µs | 115 µs | 390 µs | **2.04× faster** | **3.39× faster** |
| Polymorphic `Foo[]` | 23.0 µs | 11.8 µs | 32.1 µs | **1.94× faster** | **2.71× faster** |
| `SimpleStruct` (`int` + `DateTime`) | 3.6 ns | 2.0 ns | 5.0 ns | **1.8× faster** | **2.5× faster** |
| `SimpleStruct[100]` | 32.0 ns | 28.5 ns | 27.3 ns | 1.12× faster | 0.96× (on par) |
| `Vector4[10000]` | 3.89 µs | 3.86 µs | 3.80 µs | on par | on par |

`SimpleStruct` has 4 bytes of padding. v4 zeroes it, so the output is deterministic; v3 and MemoryPack copy
whatever the padding contains. With the masked SIMD copy this costs nothing measurable on arrays, and a
single value's padding is cleared by one or two inlined constant stores. (`SimpleStruct` was re-measured after
that change; the other rows are from the run above.)

### Deserialize (new instances)

| Case | v3 | v4 | MemoryPack | v4 vs MemoryPack |
|---|---:|---:|---:|---:|
| `SimpleClass` | 708 ns | 790 ns | 1,197 ns | **1.51× faster** |
| `SimpleClass[100]` | 81.8 µs | 88.2 µs | 103.0 µs | **1.17× faster** |
| Polymorphic class | 35.3 µs | 32.5 µs | 42.2 µs | **1.30× faster** |
| Polymorphic class array | 471 µs | 434 µs | 512 µs | **1.18× faster** |
| Polymorphic `Foo[]` | 28.2 µs | 30.5 µs | 40.3 µs | **1.32× faster** |
| `SimpleStruct[100]` | 193 ns | 204 ns | 249 ns | **1.22× faster** |
| `Vector4[10000]` | 122 µs | 122 µs | 121 µs | on par |

Deserialization is dominated by allocating the result objects, which is identical for all three (same bytes
allocated per call), and server-GC noise is about ±7% here. To see how much is left, the A/B harness also
builds the same object graph by hand (`new SimpleClass`, strings copied from spans, the same arrays, list and
dictionaries filled with the same entries): that floor takes 518–636 ns, and v4 deserializes `SimpleClass` in
602–662 ns, within 5–15% of it (the difference is mostly decoding the UTF-8 member). v3 sits at the same
place. Deserializing classes is therefore bound by allocation, not by the serializer: v4 matches v3 while
adding reference tracking, ref deserialization with pool recycling, version tolerance and payload validation,
and stays 1.2–1.5× ahead of MemoryPack.

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
* **UTF-16 strings by default** (one block copy); `[NinoUtf8]` members use the vectorized UTF-8 transcoder
  of the runtime.

## Reproducing

Build time and generated code (`tools/GeneratorBenchmark`, not part of `Nino.sln`):

```sh
dotnet run -c Release --project tools/GeneratorBenchmark -- 30 8          # this repository's generator
# another version: build its Nino.Generator and Nino.Core, then
dotnet run -c Release --project tools/GeneratorBenchmark -- 30 8 \
    --generator <path>/Nino.Generator.dll --core <path>/Nino.Core.dll
```

Arguments are the number of root classes and the depth of each hierarchy; `--dump <dir>` writes the
generated files. Each line reports one of three runs; the numbers above are from the third.

Runtime (`src/Nino.Benchmark`, BenchmarkDotNet):

```sh
dotnet run -c Release --project src/Nino.Benchmark -- --filter '*Nino*' '*MemoryPack*' --iterationCount 15 --warmupCount 5
```
