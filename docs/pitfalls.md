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
- A `T?` element of a sequence (an array, a list, a set, a queue, a collection interface, ...) with a raw
  `T` uses the canonical (64-bit corefx) `Nullable<T>` layout: a
  presence byte, zero padding up to `T`'s alignment, then `T` (zero when null). On runtimes whose
  `Nullable<T>` matches, the block is copied as is (padding cleared). Elsewhere it is converted element
  by element: `NullableLayout<T>` probes the runtime's field offsets once (it builds `default(T)` as a
  `T?` and finds the flag byte) instead of assuming an order.
- The two encodings are not interchangeable. The elements of every sequence (`int?[]`, `List<int?>`,
  `Queue<int?>`, `HashSet<int?>`, `IList<int?>`, `Memory<int?>`, `int?[,]`...) use the fixed stride (here 8 bytes
  per element: flag, 3 bytes of padding, value), so one sequence type can replace another without changing the
  bytes. A `T?` that is not the element of a sequence is written on its own: 1 byte for null, 1 + 4 bytes
  otherwise (a member, a dictionary's key or value, the item of a pair or a tuple, also inside a collection:
  `Dictionary<int, int?>`, `List<(int, int?)>`), and so is a `T?` whose `T` is not raw (`DateTimeOffset?`, a
  struct holding a reference). Up to 4.0.0-rc.10 only arrays and lists used the fixed stride: data of other
  sequences written then is not read now.

### `ValueTuple`, `DateTimeOffset`, `decimal` and other BCL structs differ by runtime

`ValueTuple` is `[StructLayout(LayoutKind.Auto)]` and every runtime places its items its own way; `DateTimeOffset`
has another field order or width on .NET 8, .NET Framework and Mono; `decimal` is aligned to 8 by CoreCLR and Mono
6.14 but to 4 by .NET Framework and IL2CPP; `nint` is 4 bytes on 32-bit hosts; the wide SIMD vectors are aligned
differently on Arm. [layouts.md](layouts.md) has the measured layout of each on every runtime CI runs. Reference
assemblies hide some of it (they declare tuples sequential), which once made the generator publish tuples inside raw
structs in declaration order while .NET x64 wrote them reordered. So Nino defines the bytes instead of copying the
host's memory:

- On their own (values, members, elements, dictionary entries), `ValueTuple`, `KeyValuePair` and `Nullable<T>` are
  written item by item, never as raw memory; `DateTimeOffset` as UTC ticks plus offset minutes (10 bytes).
- Inside a plain raw struct they take the canonical layout (spec §5.2: 64-bit CoreCLR's, with `DateTimeOffset` as
  .NET 10 lays it out, `nint`/`nuint`/pointers and function pointers as 8 bytes, the SIMD vectors aligned as on x64). A runtime whose
  layout is the canonical one copies the struct; every other one finds its field offsets once from an instance
  (`MeasureMemory`) and converts field by field. A raw `[NinoType]` struct requires all of its fields to be raw, so
  one holding a tuple, pair or nullable is written member by member.
- A function pointer (`delegate*`) is a pointer: 8 bytes, zero-extended, on every runtime. Reflection reports its
  field differently (.NET 8+ a function pointer type; .NET Framework, Mono and IL2CPP an `IntPtr`, which would be
  sign-extended), so the generator, which sees the field's type, lists the `delegate*` fields of every struct the
  compilation can see in the assembly's registry (`NinoFunctionPointerFields`), whether or not anything names it:
  those declared in the project (also through a `using unsafe F = delegate*<...>;` alias, and the field a primary
  constructor captures), and those of every assembly it references, read from their metadata (private and internal
  structs and fields too). Layouts read field types through `NinoReflection.LayoutFieldType`, which asks that list.
  So a struct reached only through generic code (`Save<T>` calling `Serialize<T>`) is covered when it is declared
  in a project with Nino's generator or in an assembly such a project references. What stays out, because no
  generator compiles against its declaration:
  - a struct of an assembly that no assembly built with Nino's generator references at compile time, or whose
    referencing assemblies are not loaded yet when the struct is first serialized (a plugin or HybridCLR hot-update
    assembly loaded by name);
  - a field the compile-time metadata hides: a reference assembly that replaces private fields with placeholders
    (as .NET's own reference packs do; Roslyn's reference assemblies keep every struct field), or another version of
    the assembly at run time;
  - a struct another source generator adds to a project, reached only through generic code (Roslyn gives no
    generator the others' output); a project that references this one covers it.

  There .NET 8+ refuses the struct (no formatter), and the other runtimes sign-extend the field, which differs from
  the canonical bytes on a 32-bit host only for an address with its top bit set (64-bit hosts copy both the same
  way; a 32-bit host reading the canonical bytes of such an address rejects them as too large for the field). Add
  Nino's generator to the project that declares the struct, or reference that project from one that has it and is
  loaded by then (the entry assembly always is).
- `Vector<T>` is not serializable (next section).

### `Vector<T>` is not serializable

`Vector<T>` is 16, 32 or 64 bytes, chosen by the CPU (the spec's §3 lists which host has which; `DOTNET_MaxVectorTBitWidth`
can change it). Its declared fields are two `ulong`s whatever its size. No wire format can carry it between hosts: 32 bytes written on x64 do not fit a 16-byte `Vector<T>` on Arm64 or in
Unity. Nino up to rc.7 measured it by its fields and wrote 16 bytes, silently dropping the upper half on AVX2 machines.
Now no type that is or holds one is serializable (alone, in arrays, collections, tuples, `Nullable`, a type argument a
`[NinoType]` uses, a base type or the `IEnumerable<T>` of a collection, as in `class Lanes : List<Vector<int>>`, or a
field at any depth of a plain struct): the analyzer reports NINO029 on the member or the
`NinoSerializer`/`NinoDeserializer` call (not NINO007, though such a type has no formatter either), the generator emits
nothing for it, and the runtime throws `NotSupportedException` when the formatter is first resolved, or, for code that
skips the resolver (an `UnmanagedFormatter<T>` made by hand, `Writer.Write<T>`), when the value is written or read;
`NinoLayout<T>` refuses it in its converted paths, never in its static initializer, which Mono runs when a method
naming it is compiled.
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

    // Serialized: 16 floats (64 bytes of data) on every host. 16 is a multiple of Vector<float>.Count everywhere (4, 8 or 16).
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
`DOTNET_EnableAVX2=0` and `DOTNET_MaxVectorTBitWidth=512`, and `LaneParticle`, declared in
`src/Nino.Unity/Assets/Test/Runtime/BclFixtureTypes.cs` and compiled into `tests/Nino.RuntimeTests`, on every CI
runtime). CI runs the `DOTNET_MaxVectorTBitWidth=512` lines only on runners with AVX-512 (GitHub's Xeons); on an EPYC
7763 runner it skips them with a notice. A span is a ref struct, so `Vectors` could not be serialized anyway; `[NinoIgnore]` says it is meant. Data
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
into the output. Nino clears padding on write, and writes zeros for the value of every absent `Nullable` it copies
(a `T?[]` or `List<T?>` element, a `Nullable` field of a raw struct), which such memory fills with stale bytes just
as well: a rented or uninitialized `int?[]` holds whatever was there before in the value of each null. Blocks use a
masked copy (SIMD when accelerated) rather than a copy followed by masking, which would reload freshly stored bytes
and stall on store forwarding. Types without padding pay nothing: the check is a JIT-time constant.

### A raw struct's `bool` fields and `Nullable` flags are unchecked by default

Reading a raw struct copies its block, so the default mode does not check a `bool` field or a `Nullable` flag inside
it: the default mode copies raw blocks as they are, at no cost (the owner's decision, round 104). A byte other than 0 or 1 there is undefined
behavior to the JIT, not only a wrong value: a `long?` with a flag of 2 was boxed as null by one compiled body and as
a value by another, so the struct compared unequal to itself and was lost in a `Dictionary`. `NinoSettings.Untrusted`
rejects such bytes (the corpus's `presence-raw-*` cases are `untrusted`), and the docs tell users to read untrusted
data with it (spec §9.3; the site's security page). The Go, Rust and TypeScript runtimes always reject them.

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

### `WEAK_VERSION_TOLERANCE` was removed

Before 4.0 the symbol made every positional type of the assembly that defined it version tolerant. It does nothing
now, so a project that still defines it would write those types positionally and stop reading its stored data:
NINO032 is an error on each such type, and its code fix adds `format: NinoFormat.VersionTolerant`, which generates
the same code and the same bytes. A type that takes its `[NinoType]` from another assembly gets one of its own, with
the options it took.

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
