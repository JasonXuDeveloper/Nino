# Nino v4 Architecture

This document describes the v4 rewrite of Nino: the binary format, the runtime (`Nino.Core`), and the
incremental source generator (`Nino.Generator`). v4 is a major release and is **not** wire- or
API-compatible with v3 or earlier v4 betas.

## Goals

1. **Runtime performance**: match or beat MemoryPack on serialization and deserialization, with zero
   allocations beyond the deserialized objects themselves.
2. **Compile time**: a truly incremental generator. Editing one type re-generates only that type's file,
   and generated code size stays linear in the number of members.
3. **Less generated code**: collections, strings, nullable values, tuples and other built-in types are
   implemented once in `Nino.Core` as generic formatters. The generator only emits code for `[NinoType]`
   types and composes formatter type names for members.
4. **Thread safety**: every public API is safe to call concurrently. Mutable shared state is published
   copy-on-write; hot paths never write shared memory.
5. **Identical output on every target**: `netstandard2.0`, `netstandard2.1`, `net8.0` and `net10.0`
   produce byte-identical payloads. Each target uses its best available primitives.
6. **Ref deserialization that plays well with object pools**: existing instances are reused when possible,
   and any instance that is discarded is passed to `NinoRecycler`.

## Target frameworks

| Package | Targets |
| --- | --- |
| `Nino.Core` | `netstandard2.0`, `netstandard2.1`, `net8.0`, `net10.0` |
| `Nino.Generator` | `netstandard2.0` (Roslyn 4.3+) |

Generated code is compiled in the user's project, which may be Unity (C# 9). Generated code therefore only
uses C# 9 features: no static abstract interface members, no ref fields, no raw string literals, no
file-scoped namespaces. Zero-cost composition uses the struct-constrained generic pattern instead
(`where TFormatter : struct, INinoFormatter<T>`), which every runtime (CoreCLR, Mono, IL2CPP, HybridCLR)
specializes per value-type argument.

Platform-specific code lives behind `#if` in `Nino.Core` only. The fallback paths (`netstandard2.0`) use
`System.Memory` and `System.Runtime.CompilerServices.Unsafe` and are written to produce the same bytes.
The test-suite runs against each `Nino.Core` build and checks golden payloads.

## Binary format

All multi-byte values are **little-endian** and written unaligned (`Unsafe.WriteUnaligned`). Big-endian
hosts are not supported.

| Value | Encoding |
| --- | --- |
| Unmanaged value (`int`, `Guid`, unmanaged structs, enums) | raw bytes, `Unsafe.SizeOf<T>()` |
| `bool` | 1 byte (0/1) |
| `T?` (nullable value type) | 1 byte has-value flag, then the value if present |
| Collection / array / string length | `int32`; `-1` means `null` |
| `string` (default, UTF-16) | `int32` char count, then UTF-16 code units |
| `string` with `[NinoUtf8]` | `int32` byte count (`-1` = null), then UTF-8 bytes |
| Array / list of unmanaged `T` | length, then one block copy of the elements |
| Other collections | length, then each element |
| Dictionary | length, then key/value pairs |
| Multi-dimensional array `T[,..]` | `int32` rank-lengths (`-1` for null in the first slot), then elements in row-major order |

### Objects

| Declared type | Header |
| --- | --- |
| `struct` | none |
| `sealed class` | 1 byte: `0` = null, `1` = instance |
| non-sealed class, abstract class, interface | `int32` type id: `0` = null, otherwise the runtime type's id |

The **type id** is a stable 32-bit hash (FNV-1a) of the type's name in runtime form
(`Namespace.Outer+Inner`, generic arguments in angle brackets). `[NinoFormerName("Old.Name")]` substitutes
the hashed name, so types can be renamed or moved without breaking existing data.

After the header comes the **body**: the members in declaration order (or `[NinoMember(n)]` order /
`[NinoExplicitOrder]`), base-class members first. Consecutive unmanaged members are written as one block
(a single bounds check and reservation for the whole block).

A `[NinoType]` struct is written as its **raw memory** (one copy, and arrays/lists of it as one block) when it
is unmanaged, not version tolerant, uses the default sequential layout, has no custom member formatters, and
every instance field (including auto-property backing fields) is serialized. Nested `[NinoType]` struct fields
must qualify as well. The bytes are the in-memory layout, **padding included**, so reordering fields changes
the format; mark the struct `versionTolerant` (or ignore a field) to opt out. Otherwise structs are written
member by member.

### Collections

User collection types work without attributes: a class derived from `List<T>`, `HashSet<T>` or
`Dictionary<TKey, TValue>`, or implementing `IDictionary<TKey, TValue>`, `IProducerConsumerCollection<T>` or
`ICollection<T>`, with a public parameterless constructor, is written like the collection it extends. A
`[NinoType]` collection writes its elements first and then its own serialized members, so a collection
with extra state (for example a synchronized list) keeps both. Ref deserialization reuses the existing
collection instance.

### Version tolerance

A type marked `[NinoType(versionTolerant: true)]` (or every type when the project defines
`WEAK_VERSION_TOLERANCE`) prefixes its body with an `int32` byte length:

```
header | int32 bodyLength | members...
```

On read, the body is sliced. Members that are missing from the slice (old data) are set to their default,
and trailing bytes (new data read by old code) are skipped. Each nested tolerant object delimits itself, so
fields can be appended to any type at any depth, not just to the root.

## Runtime (`Nino.Core`)

### Writer

`Writer` is a `ref struct` that writes into a contiguous `byte[]`, rented from `ArrayPool<byte>.Shared`
and owned by a `NinoArrayBufferWriter`.

* The hot path (`Write<T>`, `Reserve`) is a single length check plus an unaligned store. There is no
  interface call per write. Growth is a non-inlined cold path.
* Because the buffer is contiguous, back-patching (body lengths of version-tolerant types, counts of
  `IEnumerable<T>`) is always possible.
* Serializing to a foreign `IBufferWriter<byte>` writes into a thread-local scratch buffer and copies once
  at the end. Serializing to a `NinoArrayBufferWriter` writes in place.

### Reader

`Reader` is a `ref struct` over a `ReadOnlySpan<byte>` with an integer position. Each read is one
unsigned bounds check plus an unaligned load. Every length read from the payload is validated against the
remaining bytes before anything is allocated, so malformed or hostile input cannot trigger huge allocations.

### Formatters

```csharp
public interface INinoFormatter<T>
{
    void Serialize(T value, ref Writer writer);
    void Deserialize(ref T value, ref Reader reader);
}
```

`Deserialize` takes the current value by `ref`. Passing `default` creates a new instance, and passing an
existing instance populates it in place when possible. One code path serves both `Deserialize` and ref
deserialization.

* **Built-in formatters** are structs in `Nino.Core`: `UnmanagedFormatter<T>`, `StringFormatter`,
  `Utf8StringFormatter`, `NullableFormatter<T, TF>`, `ArrayFormatter<T, TF>`,
  `UnmanagedArrayFormatter<T>`, `ListFormatter<T, TF>`, `DictionaryFormatter<TKey, TValue, TFK, TFV>`,
  and so on. Element formatters are type parameters, so the JIT/AOT compiler inlines element
  (de)serialization into the collection loop.
* **Generated formatters** are structs in `<Assembly>.NinoGen`, one per `[NinoType]`. Members call
  their formatter as `default(TFormatter).Serialize(...)`, which is a direct, inlinable call.
* **`CachedFormatter<T>`** is a struct that forwards to `NinoFormatterCache<T>.Instance` (a
  `NinoFormatter<T>` class). It is used for type parameters of generic `[NinoType]`s, for types from other
  assemblies, and for types resolved at runtime. It costs one virtual call.
* **Custom formatters**: `[NinoCustomFormatter(typeof(F))]`, where `F` is either a class deriving from
  `NinoFormatter<T>` (singleton, virtual call) or a struct implementing `INinoFormatter<T>` (inlined).
  Formatters for third-party types can be registered globally with `NinoFormatterCache<T>.Register`.

### Formatter cache and registration

`NinoFormatterCache<T>.Instance` is a static generic field, so lookup is free after the first use. It is
populated by:

1. Generated registration code (`[ModuleInitializer]` on .NET 5+, plus
   `RuntimeInitializeOnLoadMethod` / `InitializeOnLoadMethod` in Unity).
2. Lazy resolution on first access: built-in generic types (`List<T>`, `Dictionary<,>`, arrays, tuples,
   and so on) and generic `[NinoType]`s registered by generic type definition are constructed with
   `MakeGenericType`.

Registration is idempotent and thread safe: writers take a lock and publish with `Volatile.Write`, and
readers never lock.

### Polymorphism

Every `[NinoType]` registers one handler, once, in the global (`NinoPolymorphic<object>`) registry, from the
generated code of its own assembly. Registration is decentralized, so the generator never scans referenced
assemblies. Each base type (non-sealed class or interface) has its own `NinoPolymorphic<TBase>` table, which
adopts a subtype from the global registry on its first lookup after checking that it derives from `TBase`.
Registering per type instead of per (base, subtype) pair keeps generated code linear in the number of types
rather than in the sum of hierarchy depths, and avoids one generic instantiation per pair.

* **Serialize**: if `value.GetType() == typeof(TBase)`, write the base body directly. Otherwise look up the
  runtime type in a lock-free open-addressing table and call the subtype's body serializer through its
  handler.
* **Deserialize**: read the type id and look it up in a second open-addressing table keyed by id. Unknown
  ids trigger lazy initialization of not-yet-loaded generated registries before failing.
* Registries are immutable snapshots replaced copy-on-write, so concurrent readers never see a torn
  entry. (The v3 inline caches could pair one type's id with another type's delegate under contention.)

### Concurrency and cache lines

Cache-line reasoning assumes 64-byte lines.

* No hot path writes shared memory, so serializing threads never contend for a line. Scratch buffers are
  `[ThreadStatic]` and are taken out of their slot while in use, so re-entrant calls are safe. There is no
  global "default buffer" guarded by `Interlocked` (v3 bounced that line between all serializing threads).
* Formatter lookup is a static generic field (`NinoFormatterCache<T>.Instance`), written once at
  registration and only read afterwards.
* Polymorphic tables use 16-byte entries with the key inline (four per line) and stay at most half full,
  so a lookup normally reads one line plus the handler it dispatches to.
* Mutable registration state (`_count`, locks) is only touched during registration, which happens at
  startup, so no padding is needed to protect the read-only tables from false sharing.
* Consecutive unmanaged members are reserved and written as one block, and unmanaged arrays, lists and raw
  structs are single block copies, so the writer streams through the buffer line by line.

### Ref deserialization and pooling

* `null` target: a new instance is created (or `[NinoRefDeserialization]` factory is called).
* Existing target of the right type: populated in place. Collections are cleared and refilled. Lists
  reuse elements by index.
* Types whose members cannot be assigned after construction (constructor parameters, `init`-only, or
  `readonly` members) are re-created, and the old instance is recycled.
* Whenever an existing non-null instance is dropped (payload is `null`, list shrinks, array reallocates,
  polymorphic type changes), it is passed to `NinoRecycler<TDeclared>.Recycle`. That lets object pools take
  it back, which fixes the leak where a pooled `List<T>` assigned in a factory was simply overwritten with
  `null`.

## Source generator

### Pipeline

```
ForAttributeWithMetadataName("Nino.Core.NinoTypeAttribute") ─┐
CreateSyntaxProvider(class/struct/record with base list)      ├─► TypeModel (equatable) ─► per-type source
                                    (inherited NinoType)      ┘          │
                                                                         └─► formatter names ─► Collect ─► registry source
CompilationProvider.Select(GeneratorOptions)  ──────────────────────────────┘ (assembly name, symbols, Unity)
```

* All symbol work happens inside the transform, which converts symbols into an **equatable model** made
  only of strings, primitives and `EquatableArray<T>`. Nothing downstream holds a symbol, a syntax node or
  the `Compilation`, so Roslyn's caching cuts off unchanged types.
* Each type is emitted into its own file. Editing a member of `Foo` re-emits only `Foo.g.cs`.
* The registry stage consumes only formatter names, so it only re-runs when types are added, removed or
  renamed.
* A separate syntax provider finds `NinoSerializer`/`NinoDeserializer` calls whose type argument is a
  built-in generic (e.g. `ImmutableArray<T>`, `List<MyStruct>`) and registers those closed formatters, so
  AOT runtimes never need `MakeGenericType` for them.
* The v3 generator combined everything with `CompilationProvider`, scanned every generic type syntax in
  the project and every `using` directive in every file, and re-emitted all types on every keystroke.

### Generated code per type

* `struct <Name>Formatter : INinoFormatter<T>` with `Serialize`, `Deserialize`, `WriteBody`, `ReadBody`.
* Generic `[NinoType]`s produce generic formatters (one per definition, not one per closed type).
  Type-parameter members use `CachedFormatter<T>`.
* Private members use `[UnsafeAccessor]` when the compilation targets .NET 8+, and generated `partial`
  accessors otherwise.
* Classes whose members are all directly accessible also get static `WriteMembers`/`ReadMembers`. A subtype
  whose leading members are exactly its base's members calls the base's methods instead of repeating them, so
  each inherited member is compiled once per hierarchy rather than once per subtype (about a third less code for
  deep hierarchies). The bytes are unchanged. Delegation is skipped when raw members on both sides of the
  boundary would otherwise be written as one block, since splitting that block costs an extra reservation.
* Preprocessor-dependent choices (`WEAK_VERSION_TOLERANCE`, `NET8_0_OR_GREATER`) are resolved at
  generation time from the compilation's parse options, so generated code contains no `#if` branches.

For a hierarchy of 220 types (20 roots × depth 8), v3 generated about 911,000 lines (47 MB) because every
polymorphic base inlined the full body of every subtype twice. v4 emits each body once.
