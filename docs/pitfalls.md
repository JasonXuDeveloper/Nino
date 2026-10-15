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
versions. Nino never puts the in-memory `Nullable<T>` on the wire:

- A single `T?` is a has-value byte followed by the value when present.
- A `T?` array or list with unmanaged `T` uses a fixed stride: a has-value byte plus the raw value, which
  is zero for null elements. The fixed stride lets the count be validated up front and keeps the read loop
  branch-free.
- To fill memory, `NullableLayout<T>` probes the running runtime's field offsets once (it builds
  `default(T)` as a `T?` and finds the flag byte) instead of assuming one order.

### `ValueTuple` uses `LayoutKind.Auto`

`System.ValueTuple<...>` is `[StructLayout(LayoutKind.Auto)]` in corefx, so CoreCLR may reorder its fields
and the padding differs from the sequential layout older Mono uses. Raw tuples are not portable. This is
why `NinoTuple` exists, and why `ValueTuple` and `KeyValuePair` are always written member by member,
never as raw memory.

### BCL structs

Before treating a BCL struct as raw, verify its layout on every runtime. These match CoreCLR in the Mono
sources:

- `DateTime`: one `ulong`. On net8, structs that contain a `DateTime` keep the sequential layout.
- `decimal`: explicit offsets flags/hi/lo/mid at 0/4/8/12.
- `Guid`.
- `DateTimeOffset`: `_dateTime`, then `_offsetMinutes`.

### Alignment on 32-bit platforms

The raw layout of a struct depends on the alignment the platform gives each field:

| Platform | `long`/`double` alignment inside structs | `nint`/pointer size |
| --- | --- | --- |
| x64, arm64 (CoreCLR, Mono, IL2CPP) | 8 | 8 |
| armv7, wasm32 (IL2CPP/WebGL), Windows x86 MSVC (IL2CPP) | 8 | 4 |
| x86 CoreCLR / Mono managed layout | 4 | 4 |

So `struct { int a; long b; }` is 16 bytes on x64 and 12 bytes on x86 .NET/Mono. `nint`, `IntPtr` and
pointers change size on every 32-bit target. Raw copies of such types are only portable between platforms
with the same layout. MemoryPack has the same limitation.

Open item: define the wire layout as the 64-bit natural layout. Raw-copy only when the runtime layout
matches, which is a JIT-folded check, and convert field by field otherwise.

### Padding bytes are not deterministic

A raw copy includes padding. Padding usually stays zero (arrays are zeroed on allocation, and Nino writes
zero padding when it reads), but code that builds structs in uninitialized memory (`SkipLocalsInit`,
`stackalloc`, `Unsafe`) can leave garbage there. The data still reads back correctly, but two equal values
may serialize to different bytes. Hashing or diffing serialized output is affected.

### Reordering fields of a raw struct changes the format

A `[NinoType]` struct that qualifies for raw copying is written in memory order, padding included.
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
