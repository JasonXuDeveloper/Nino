# Pitfalls

Problems met while building Nino v4, why they matter, and what Nino does about them. Read this before
changing the wire format, the raw-memory fast paths or the generator pipeline.

## Wire format and memory layout

Nino copies unmanaged values as raw memory wherever it can. That is only correct when every runtime that
reads the data lays the type out the same way. CoreCLR, Mono (Unity editor, Unity Mono builds) and IL2CPP
(Unity players, where the C++ compiler decides the layout) do not always agree.

### `Nullable<T>` field order differs between runtimes

- corefx / CoreCLR, and Unity 2021.3+ (its Mono uses the corefx source): `bool hasValue; T value;`
- Old Mono (Unity 2019.4 and earlier): `T value; bool has_value;`, value first.

Copying a `T?` as raw memory (what MemoryPack does) therefore produces different bytes on old Unity
versions. Nino defines the bytes instead:

- A single `T?` is a has-value byte followed by the value when present.
- A `T?` array or list with unmanaged `T` uses the canonical (64-bit corefx) `Nullable<T>` layout: a
  has-value byte, zero padding up to `T`'s alignment, then `T` (zero when null). On runtimes whose
  `Nullable<T>` matches, the block is copied as is (padding cleared). Elsewhere it is converted element
  by element: `NullableLayout<T>` probes the runtime's field offsets once (it builds `default(T)` as a
  `T?` and finds the flag byte) instead of assuming an order.
- The two encodings are not interchangeable. `int?[]` and `List<int?>` members use the fixed stride (here 8
  bytes per element: flag, 3 bytes of padding, value), while a member declared as an interface
  (`IList<int?>`, `IEnumerable<int?>`, `ICollection<int?>`...) writes each element on its own: 1 byte for null,
  1 + 4 bytes otherwise, whatever collection it holds. Changing a member between the two kinds changes the
  format.

### `ValueTuple` uses `LayoutKind.Auto`

`System.ValueTuple<...>` is `[StructLayout(LayoutKind.Auto)]` in corefx, so CoreCLR may reorder its fields
and the padding differs from the sequential layout older Mono uses. Raw tuples are not portable. This is
why `NinoTuple` exists, and why `ValueTuple` and `KeyValuePair` are always written member by member,
never as raw memory.

### BCL structs

Before treating a BCL struct as raw, verify its layout on every runtime:

- `DateTime`, `TimeSpan`: one 8-byte field.
- `Guid`: `int, short, short, 8 bytes`, the same everywhere.
- `decimal`: 16 bytes with the same byte order everywhere, but CoreCLR declares
  `int _flags; uint _hi32; ulong _lo64` (alignment 8) while Mono/IL2CPP declare four ints (alignment 4).
  So `struct { int a; decimal d; }` puts `d` at 8 on CoreCLR and at 4 on Unity.
- `DateTimeOffset`: `DateTime` plus a `short`, so 6 bytes of padding and a different size on x86. Nino writes
  it as UTC ticks plus offset minutes (10 bytes), never raw.
- `Nullable<T>`, `ValueTuple`, `KeyValuePair`: never raw (see above). A raw `[NinoType]` struct requires all
  of its fields to be raw, so a struct holding one of these is written member by member.

### Alignment on 32-bit platforms

The raw layout of a struct depends on the alignment the platform gives each field:

| Platform | `long`/`double` alignment inside structs | `nint`/pointer size |
| --- | --- | --- |
| x64, arm64 (CoreCLR, Mono, IL2CPP) | 8 | 8 |
| armv7, wasm32 (IL2CPP/WebGL), Windows x86 MSVC (IL2CPP) | 8 | 4 |
| x86 CoreCLR / Mono managed layout | 4 | 4 |

So `struct { int a; long b; }` is 16 bytes on x64 and 12 bytes on x86 .NET/Mono. MemoryPack copies such
structs as they are, so its data is not portable between these platforms.

Nino's wire layout for raw types is the **canonical** layout: the one 64-bit CoreCLR uses (primitives aligned
to their size, `decimal` aligned to 8). `NinoLayout` measures the runtime once with two probe structs
(`{byte; long}` and `{byte; decimal}`) and derives each type's memory layout from its fields:

- Where it equals the canonical layout (every 64-bit runtime except for structs with a misaligned
  `decimal` on Mono/IL2CPP, and most 32-bit ones), values are copied as raw memory. The check is a
  `static readonly` flag the JIT folds away.
- Elsewhere, values are converted field by field and the bulk raw paths are skipped.
- `nint`/`nuint` are always written as 8 bytes.

The generator only puts struct members into unmanaged blocks when their layout is the same under every
model, so generated code never depends on the runtime's layout.

### Padding bytes

A raw copy includes padding. Padding usually stays zero, but code that builds structs in uninitialized
memory (`SkipLocalsInit`, `stackalloc`, `GC.AllocateUninitializedArray`, native memory) can leave stale
data there. Copied as is, two equal values could serialize to different bytes, and stale memory would leak
into the output. Nino clears padding on write. Blocks use a masked copy (SIMD when accelerated) rather
than a copy followed by masking, which would reload freshly stored bytes and stall on store forwarding.
Types without padding pay nothing: the check is a JIT-time constant.

### Reordering fields of a raw struct changes the format

A `[NinoType]` struct that qualifies for raw copying is written in its canonical memory order.
Reordering its fields or changing its `StructLayout` breaks existing data. Mark it `versionTolerant` (or
ignore a field) to opt out of raw copying.

## Source generator

### Roslyn re-runs every transform on each edit

`ForAttributeWithMetadataName` caches by syntax node, but its transform still runs again for every
attributed type whenever the compilation changes. Even a whitespace edit in an unrelated file means a new
`Compilation`. With 1,100 types that was ~350ms per keystroke. `ModelCache` reuses a model while every
syntax tree it depends on is still part of the compilation and the project state (assembly name,
references, file set, preprocessor symbols) is unchanged. That brings an edit down to ~80–130ms.

Direct dependencies are collected once per compilation and memoized. Collecting them lazily made the first
edit cost ~1.1s.

### Equatable models only

Incremental steps compare their outputs with `Equals`. Anything holding an `ISymbol`, `Compilation`,
`SyntaxNode` or a plain array defeats the cache and makes every step re-run. Models use records with
`EquatableArray<T>`.

### Unused labels and warnings in generated code

Generated code is compiled with the user's warning settings, which may include warnings as errors. Only
emit labels such as `__end` and `__keyed` when they are jumped to, and avoid any construct that warns in
some configurations.

### `WEAK_VERSION_TOLERANCE` is per assembly

The symbol only applies to types in the assembly that defines it. Types from referenced assemblies keep
their own format, including raw copying.

### Base-class members break raw blocks

Consecutive unmanaged members are written as one block. Members delegated to a base type's formatter end
the block, so inheritance can split what looks like one contiguous run of fields.

## Benchmarking

### Tier-0 versus Tier-1 code

With tiered compilation, short runs measure Tier-0 or instrumented code, where calls such as
`Unsafe.Read`, `Nullable` constructors and `static readonly` checks are not inlined or folded. Compare with
`DOTNET_TieredCompilation=0` (full opts at once) or with long BenchmarkDotNet runs. Read disassembly only
from listings marked `Tier1` or `FullOpts`.

### Allocation dominates small collection benchmarks

Deserializing a 10,000-element `int?[]` spends a large part of its time allocating and zeroing the array.
Measure the element loop separately before optimizing it.
