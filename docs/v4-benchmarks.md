# Nino v4 benchmarks

Why v4: what it costs to build a project that uses Nino, and how fast serialization runs, compared with
v3 (3.3.13, the last release of the v3 API) and with [MemoryPack](https://github.com/Cysharp/MemoryPack) 1.21.4.

The build-time numbers were measured on one machine, an Intel Xeon 2.10 GHz (4 cores, AVX-512, .NET SDK 10.0.402);
generated code sizes do not depend on the machine. The runtime tables are those of the newest release: its CI run
(`report.yml`) benchmarks v4 against MemoryPack, each case's Nino and MemoryPack runs on one runner (v3 3.3.13 on the `SimpleTest` runner),
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

`src/Nino.Benchmark` (`ShortRun` job with 5 warm-up and 15 measured iterations, each case in 3 processes, server
GC), all ten benchmark classes, v4 and MemoryPack 1.21.4 side by side: the release run spreads the classes over five
runners per architecture and keeps each case's Nino and MemoryPack runs on one. v3 is Nino 3.3.13 from nuget.org, running a port of
the same `SimpleTest` (same data, same attributes) on the same runner directly before the v4 session; it has no
polymorphic, pooling, reference, version-tolerant or fixed-size benchmarks. These tables come from
<!--bench:doc.source-->the performance report of [v4.0.0-rc.6](https://github.com/JasonXuDeveloper/Nino/releases/tag/v4.0.0-rc.6): BenchmarkDotNet 0.15.8, .NET 10.0.13, AMD EPYC 7763 2.59 GHz; v3 3.3.13 from NuGet ran directly before v4 on the same machine<!--/bench-->.

Differences under 5% are reported as on par: MemoryPack runs in both sessions, and its two times show how much the
runner moved. A time under 1 ns (about three clock cycles) cannot be told apart from another: reading a 40-byte struct
measures anywhere from 0.05 to 0.8 ns in both libraries, once BenchmarkDotNet subtracts its empty-method overhead.
v4 under 1 ns against a measurable time is faster, too fast to measure, with no ratio; both under it is on par. A dash
in a "vs" column means the case was not measured there, or v4 is not ahead.

Each library serializes to its own buffer writer: Nino to `NinoArrayBufferWriter`, MemoryPack and MessagePack to
`ArrayBufferWriter<byte>`. Nino writes in place only into its own; into any other `IBufferWriter<byte>` it writes a
pooled buffer and then copies it over, one copy more than these tables measure.
Before the benchmarks run, `--verify` (`src/Nino.Benchmark/Verify.cs`) checks every case: each serialize case writes
its payload's bytes, and every deserialize case of a category reads back the same value, deep-compared. The data is
seeded, so every run serializes the same values.

### Serialize (to a reused buffer writer)

<!--bench:doc.table.serialize-->
| Case | v4 | MemoryPack | v3 | v4 vs MemoryPack | v4 vs v3 |
|---|---:|---:|---:|---:|---:|
| `SimpleClass` | 102 ns | 396 ns | 186 ns | **3.88× faster** | **1.82× faster** |
| `SimpleClass[100]` | 11.7 µs | 39.6 µs | 25.3 µs | **3.37× faster** | **2.16× faster** |
| Polymorphic class | 6.64 µs | 35.7 µs | — | **5.38× faster** | — |
| Polymorphic class array | 77.5 µs | 339 µs | — | **4.38× faster** | — |
| Polymorphic `Foo[]` | 6.91 µs | 38.7 µs | — | **5.60× faster** | — |
| `SimpleStruct` (`int` + `DateTime`) | 4.54 ns | 5.14 ns | 3.62 ns | **1.13× faster** | — |
| `SimpleStruct[100]` | 21.9 ns | 22.5 ns | 22.5 ns | on par | on par |
| `Vector4[10000]` | 3.31 µs | 3.44 µs | 3.31 µs | on par | on par |
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
| `SimpleClass` | 463 ns | 739 ns | 537 ns | **1.60× faster** | **1.16× faster** |
| `SimpleClass[100]` | 48.2 µs | 75.1 µs | 57.4 µs | **1.56× faster** | **1.19× faster** |
| Polymorphic class | 17.8 µs | 30.2 µs | — | **1.69× faster** | — |
| Polymorphic class array | 258 µs | 382 µs | — | **1.48× faster** | — |
| Polymorphic `Foo[]` | 15.5 µs | 28.9 µs | — | **1.87× faster** | — |
| `SimpleStruct` (`int` + `DateTime`) | < 1 ns | 1.46 ns | < 1 ns | **faster** (too fast to measure) | on par |
| `SimpleStruct[100]` | 85.4 ns | 101 ns | 86.1 ns | **1.18× faster** | on par |
| `Vector4[10000]` | 124 µs | 121 µs | 121 µs | on par | on par |
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
| Pooled node | 581 ns | **206 ns** (0 B) | 687 ns | 597 ns (1,024 B) | **2.91× faster** |
| Pooled leaves, in place | 3.17 µs | **486 ns** (0 B) | 3.86 µs | 5.59 µs (12,800 B) | **11.50× faster** |
| Pooled leaves, pooled elements | 3.17 µs | **3.47 µs** (856 B) | 3.86 µs | 5.59 µs (12,800 B) | **1.61× faster** |
| Pooled class | 470 ns | **298 ns** (0 B) | 708 ns | 606 ns (1,080 B) | **2.03× faster** |
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
lookup and blocking collections, and the immutable collections. Its `TimeZoneInfo` case measures a design difference
more than speed: Nino writes a system time zone by its id and reads it back from the system's cached zones, while
MemoryPack writes and parses the zone's whole serialized string on every call.

<!--bench:doc.table.extra-->
| Case | Serialize v4 | MemoryPack | v4 vs MemoryPack | Deserialize v4 | MemoryPack | v4 vs MemoryPack |
|---|---:|---:|---:|---:|---:|---:|
| Object graph, 200 tracked nodes | 5.63 µs | 29.7 µs | **5.27× faster** | 6.11 µs | 20.3 µs | **3.32× faster** |
| `TolerantClass` | 107 ns | 513 ns | **4.78× faster** | 506 ns | 814 ns | **1.61× faster** |
| `TolerantClass[100]` | 12.4 µs | 52.6 µs | **4.26× faster** | 52.9 µs | 84.2 µs | **1.59× faster** |
| `FixedStruct` | 9.36 ns | 9.19 ns | on par | < 1 ns | 1.04 ns | **faster** (too fast to measure) |
| `FixedClass` | 10.3 ns | 24.9 ns | **2.42× faster** | 10.4 ns | 22.7 ns | **2.19× faster** |
| `FixedClass[100]` | 748 ns | 1.74 µs | **2.33× faster** | 1.11 µs | 1.34 µs | **1.21× faster** |
| `List<Player>`, 100 | 10.6 µs | 26.0 µs | **2.45× faster** | 32.0 µs | 44.9 µs | **1.40× faster** |
| `List<Order>`, 100 | 9.71 µs | 15.5 µs | **1.60× faster** | 25.9 µs | 32.9 µs | **1.27× faster** |
| `List<Shape>`, 1,000 (abstract base) | 1.84 µs | 22.9 µs | **12.46× faster** | 11.0 µs | 16.1 µs | **1.47× faster** |
| `TreeNode`, 1,365 nodes | 15.9 µs | 20.6 µs | **1.29× faster** | 70.3 µs | 73.8 µs | on par |
| `InventoryItem[1000]` (padded struct) | 90.7 ns | 93.2 ns | on par | 448 ns | 460 ns | on par |
| `Dictionary<string, string>`, 1,000 | 8.61 µs | 32.7 µs | **3.80× faster** | 40.5 µs | 63.4 µs | **1.56× faster** |
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
  bounds check), unmanaged arrays, lists and raw `[NinoType]` structs are single block copies, and arrays and
  lists of fixed-size elements are written straight into a `byte[]` of their exact size, without a scratch buffer.
  A raw nullable reads its has-value byte and its value under one bounds check, and multi-dimensional arrays take
  the block paths of flat ones.
* **Copies sized to the CPU.** Up to 64 bytes (on 64-bit runtimes) or 128 bytes (with 32-byte vectors) are copied
  inline, as overlapping loads and stores of the widest vector that fits; up to 32 KB (64-byte vectors) or 16 KB
  (32-byte vectors, from 256 bytes) as vectors with aligned stores (with only 16-byte vectors, on Arm64, SSE only and
  WebAssembly, `memmove` from 65 bytes); beyond, with `memmove`, which is far slower on some
  CPUs (8,000 bytes in 236 ns on GitHub's EPYC 9V74 runners and 2.8 µs on their EPYC 7763 ones, against 83 to 85 ns for
  the vector loop). Reads of 6 KB or more use
  `memmove`, whose `rep movsb` writes fresh cache lines without reading them first.
* **Zeroing only where it pays.** A new array that one block copy fills is allocated uninitialized from 6 KB: below,
  the runtime's uninitialized allocation costs more than zeroing (on GitHub's runners, 1.1x the time at 4 KB, 1.4x to
  2.2x at 2 KB).
  One filled by vector shuffles or value by value (padded tuples, `DateTimeOffset`s) is allocated zeroed: the
  clearing brings its lines into the cache, where each store into an uninitialized line first reads it from
  memory (1,000 `(int, long)` into a new list in 1.5 µs instead of 2.7 µs). Slots a read overwrites are not cleared
  first, in new or existing lists and arrays.
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
  on where the buffer starts (1,000 `(int, long)` in 122 to 130 ns at any alignment instead of 123 to 149, AVX-512
  Xeon).
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
  The loop indexes with native-sized offsets (1,000 8-byte structs with a padding byte in 66 ns instead of 74 with
  64-byte vectors), and on Arm64 loads and stores pairs of 16-byte vectors (`ldp`/`stp`), as the C library's `memcpy`
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
  checked at startup), without the enumerator's version check and field reloads on every element; with removed
  entries, or no layout recognized, through the enumerator, out of line.
* **Specialized code behind shared generics.** `Serialize<T>` of an array is shared code (arrays are
  reference types), where each static access is a dictionary lookup. The raw array path looks up one object
  and lets the array formatter, compiled for the element type, do the write.
* **Rare paths out of line.** A top-level call inlines its whole formatter chain into one method, under one
  inlining budget, and the JIT compiles a branch whose condition is a method call even for the types where it is
  dead. So only the common path is inline: vector loops of block copies, element-by-element loops, the copy of a
  raw read, UTF-16 strings and recycling are calls (`NoInlining`), and checks made of several helper calls are one
  `static readonly` field. `benchmarks/asm.py compare` lists every method whose machine code changed against a
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

The suite has ten classes, all against MemoryPack 1.21.4:
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
package from nuget.org. [`benchmarks/run.sh`](../benchmarks/run.sh) runs the whole protocol (v3 directly before the full v4
suite) and prints v4 against v3. The release workflow runs the same protocol for every release and publishes the
result in the release notes ("v3 3.3.13 vs v4 (same machine)"), where the documentation site and these tables take
their v3 ratios from.
