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

- A single `T?` is a presence byte followed by the value when present.
- A `T?` array or list with unmanaged `T` uses the canonical (64-bit corefx) `Nullable<T>` layout: a
  presence byte, zero padding up to `T`'s alignment, then `T` (zero when null). On runtimes whose
  `Nullable<T>` matches, the block is copied as is (padding cleared). Elsewhere it is converted element
  by element: `NullableLayout<T>` probes the runtime's field offsets once (it builds `default(T)` as a
  `T?` and finds the flag byte) instead of assuming an order.
- The two encodings are not interchangeable. `int?[]` and `List<int?>` members use the fixed stride (here 8
  bytes per element: flag, 3 bytes of padding, value), while a member declared as an interface
  (`IList<int?>`, `IEnumerable<int?>`, `ICollection<int?>`...) writes each element on its own: 1 byte for null,
  1 + 4 bytes otherwise, whatever collection it holds. Changing a member between the two kinds changes the
  format.

### `ValueTuple` uses `LayoutKind.Auto`

`System.ValueTuple<...>` is `[StructLayout(LayoutKind.Auto)]` in every runtime, and each runtime places auto-layout
fields its own way: 64-bit CoreCLR puts the primitive items first, largest first, then the struct items
(`(int, long)` is the `long`, then the `int`); Mono and IL2CPP keep declaration order (`(byte, long, int)` is 24 bytes
there, 16 on CoreCLR); 32-bit runtimes align to 4 and pack into fewer bytes; .NET Framework reorders by its own rule.
Reference assemblies do not even show the attribute (they declare tuples sequential), which once made the generator
publish tuples inside raw structs in declaration order while .NET x64 wrote them reordered. So:

- On their own (values, members, elements, dictionary entries), `ValueTuple` and `KeyValuePair` are always written
  item by item, in declaration order and without padding, never as raw memory: arrays and lists of them with
  unmanaged items move between the runtime's layout and those packed bytes in blocks (vector shuffles).
- Inside a plain raw struct, they are part of its bytes, in the canonical layout (below): tuples placed by 64-bit
  CoreCLR's auto-layout rule, pairs sequential. Runtimes that lay them out otherwise convert.

### BCL structs

Before treating a BCL struct as raw, verify its layout on every runtime:

- `DateTime`, `TimeSpan`: one 8-byte field.
- `Guid`: `int, short, short, 8 bytes`, the same everywhere.
- `decimal`: 16 bytes with the same byte order everywhere, but CoreCLR declares
  `int _flags; uint _hi32; ulong _lo64` (alignment 8) while Mono/IL2CPP declare four ints (alignment 4).
  So `struct { int a; decimal d; }` puts `d` at 8 on CoreCLR and at 4 on Unity. Measured in CI: .NET Framework aligns
  `decimal` to 4 as well; Mono 6.8 and 6.14 (whose `decimal` overlays a `ulong`) to 8.
- `DateTimeOffset`: auto-layout, and different everywhere: .NET 10 has an `int` offset in minutes at 0 and the UTC
  date at 8; .NET 8 a `short` at 0 (the same place, other width); .NET Framework and Mono the date at 0 and a `short`
  at 8; 32-bit runtimes 12 bytes. On its own Nino writes it as UTC ticks plus offset minutes (10 bytes), never raw;
  inside a raw struct, as .NET 10 lays it out (16 bytes: `i32` minutes, 4 zero bytes, UTC ticks), so .NET 10 copies it
  and every other runtime converts (.NET 8 sign-extends its `short`).
- `Nullable<T>`, `ValueTuple`, `KeyValuePair`: never raw on their own (see above). A raw `[NinoType]` struct requires
  all of its fields to be raw, so a struct holding one of these is written member by member. A plain struct (no
  `[NinoType]`) is raw as a whole, with these inside it in the canonical layout: `Nullable<T>` and `KeyValuePair`
  sequential with their real fields, `ValueTuple` and `DateTimeOffset` as above. Where the runtime's layout of such a
  struct is not the canonical one, the offsets of its fields are read from an instance (`MeasureMemory`: tuples,
  `DateTimeOffset`, `Nullable` fields included) and each value is converted; where it is (64-bit CoreCLR), it is
  copied as it is.
- `nint`, `nuint` and pointers inside a raw struct are 8 bytes on the wire on every host (sign- or zero-extended on
  32-bit; a value that does not fit a 32-bit host's field is rejected there).
- `Vector<T>` is not serializable (next section).
- `Vector64/128/256/512<T>` are aligned to their size inside raw structs, as x64 CoreCLR does; Arm64 aligns
  `Vector256` and `Vector512` to 16 and Arm32 every one to 8, so a raw struct holding one after a smaller field is
  converted there.
- docs/layouts.md has the measured layout of every BCL struct on every runtime CI has.

### `Vector<T>` is not serializable

`Vector<T>` has the size of the CPU's vectors: 32 bytes on x64 with AVX2 or AVX-512 (the default; check
`DOTNET_PreferredVectorBitWidth` and `DOTNET_MaxVectorTBitWidth`, which can make it 64), 16 on Arm64, Arm32, .NET
Framework x86, Mono, IL2CPP (Unity, on x64 too) and x64 without AVX2. Its declared fields are two `ulong`s whatever its
size. No wire format can carry it between hosts: 32 bytes written on x64 do not fit a 16-byte `Vector<T>` on Arm64 or in
Unity. Nino up to rc.7 measured it by its fields and wrote 16 bytes, silently dropping the upper half on AVX2 machines.
Now no type that is or holds one is serializable (alone, in arrays, collections, tuples, `Nullable`, a type argument a
`[NinoType]` uses, a base type or the `IEnumerable<T>` of a collection, as in `class Lanes : List<Vector<int>>`, or a
field at any depth of a plain struct): the analyzer reports NINO029 on the member or the
`NinoSerializer`/`NinoDeserializer` call (not NINO007, though such a type has no formatter either), the generator emits
nothing for it, and the runtime throws `NotSupportedException` when the formatter is first resolved, or, for code that
skips the resolver (an `UnmanagedFormatter<T>` made by hand, `Writer.Write<T>`), when the value is written or read
(`NinoReflection.VariableVectorIn`, the generator's `RawRules.VariableVectorIn`; `NinoLayout<T>` refuses it in its
converted paths, never in its static initializer, which Mono runs when a method naming it is compiled).
`Vector64<T>` to `Vector512<T>` are fixed size and stay serializable; so does a member with a `[NinoMemberFormatter]`
of your own.

Store a fixed number of lanes instead and build the vectors from them, in members Nino does not serialize. Pick a
lane count that is a multiple of `Vector<T>.Count` on every host (16 floats or ints, 64 bytes: the widest `Vector<T>`),
so the view covers every lane everywhere; or process in `Vector<T>.Count` chunks and finish the rest lane by lane.

```csharp title="Particle.cs"
using System;
using System.Numerics;
using System.Runtime.InteropServices;
using Nino.Core;

[NinoType]
public sealed partial class Particle
{
    public int Id;

    // Serialized: 16 floats, 64 bytes on every host. 16 is a multiple of Vector<float>.Count everywhere (4, 8 or 16).
    public float[] Lanes = new float[16];

    // Not serialized: the same lanes as Vector<float>s, 4, 2 or 1 of them depending on the CPU.
    [NinoIgnore]
    public Span<Vector<float>> Vectors => MemoryMarshal.Cast<float, Vector<float>>(Lanes.AsSpan());

    // Not serialized: the first Vector<float>.Count lanes (4, 8 or 16 of them) as one vector.
    [NinoIgnore]
    public Vector<float> Head
    {
        get => Vectors[0];
        set => Vectors[0] = value;
    }

    public void Scale(float factor)
    {
        foreach (ref var vector in Vectors)
            vector *= factor;
    }
}
```

The bytes are the same whatever `Vector<float>.Count` is (`tests/Nino.UnitTests/Layout/VectorTTests.cs` with
`DOTNET_EnableAVX2=0` and `DOTNET_MaxVectorTBitWidth=512`, and `LaneParticle` in `tests/Nino.RuntimeTests` on every CI
runtime). A span is a ref struct, so `Vectors` could not be serialized anyway; `[NinoIgnore]` says it is meant. Data
read from an untrusted source may hold another number of lanes: check `Lanes.Length` before using the view. A fixed
buffer or `Vector128<T>`/`Vector256<T>` fields (.NET only) serve as storage too.

### Alignment on 32-bit platforms

The raw layout of a struct depends on the alignment the platform gives each field:

| Platform | Alignment of 8-byte fields inside structs | `nint`/pointer size |
| --- | --- | --- |
| x64, arm64 (CoreCLR, Mono, IL2CPP) | 8 | 8 |
| armv7 (CoreCLR, IL2CPP), wasm32 (IL2CPP/WebGL), Windows x86 MSVC (IL2CPP) | 8 | 4 |
| Windows x86 CoreCLR (.NET 10) | 8 for a `long` or `double` field and for a field of a sequential struct that holds one (`TimeSpan`, `decimal`, `Nullable<long>`, your own structs); 4 for a field of an auto-layout struct (`DateTime`, `ValueTuple`, `DateTimeOffset`) and for every 8-byte field inside one | 4 |
| x86 Mono, Linux x86 CoreCLR | not verified | 4 |

The CoreCLR rows are the layouts crossgen2 10.0.14 compiles against for each target (`Unsafe.SizeOf<T>()`
folds to a constant in its listing): on Windows x86, `struct { int a; long b; }` is 16 bytes as on x64 and
linux-arm, but `struct { int a; DateTime b; }` is 12 (16 on x64) and `(int, long)` is 12 (16 on x64 and
linux-arm). crossgen2 has no Linux x86 JIT, and 32-bit Mono was not run, so those are not verified. MemoryPack
copies such values as they are, so its data is not portable between these platforms.

Nino's wire layout for raw types is the **canonical** layout: the one 64-bit CoreCLR uses (primitives aligned
to their size, `decimal` aligned to 8, `Int128` to 16, tuples by its auto-layout rule, `DateTimeOffset` as .NET 10
lays it out; spec §5.2). `NinoLayout` measures the runtime once with probe structs
(`{byte; long}`, `{byte; DateTime}`, `{byte; decimal}` and `{byte; Int128}`) and derives each type's memory layout
from its fields:

- Where it equals the canonical layout (every 64-bit runtime except for structs with a misaligned
  `decimal` on IL2CPP and .NET Framework, and most 32-bit ones), values are copied as raw memory. The check is a
  `static readonly` flag the JIT folds away.
- Elsewhere, values are converted field by field and the bulk raw paths are skipped.
- .NET Framework lays out a sequential struct that holds a `DateTime` as auto-layout (`{ byte; DateTime; int }`
  puts the `int` first, 16 bytes on x64), which no alignment model describes: there (and wherever the model misses
  a type's size) the offsets are read from an instance, each field's value copied out by reflection from bytes set
  to their own offsets.
- `nint`/`nuint` are always written as 8 bytes, inside raw structs too: where this host's field is 4 bytes, the
  conversion sign- or zero-extends it, and rejects what does not fit.
- The 64-bit path pays nothing for the others: whether a type is copied or converted is a `static readonly` bool
  (`NinoLayout<T>.IsCanonical`, `CopiesAsIs`, `HasPadding`, `Size`) computed once before Tier1, which the JIT folds,
  so the Tier1 code of a raw array, list or value on x64 holds no layout check at all (checked with JitDisasm:
  `NinoLayout<T>.Size` compiles to `mov eax, 32`). Mono, IL2CPP, NativeAOT and Tier0 read the flag.

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
Reordering its fields or changing its `StructLayout` breaks existing data. Give it `format: NinoFormat.VersionTolerant`
(or ignore a field) to opt out of raw copying.

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
