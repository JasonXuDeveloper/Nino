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
6. **Reuse deserialization that plays well with object pools**: existing instances are reused when possible,
   and any instance that is discarded is passed to `NinoRecycler`.

## Target frameworks

| Project | Targets |
| --- | --- |
| `Nino.Core` (NuGet package `Nino.Serialization`) | `netstandard2.0`, `netstandard2.1`, `net8.0`, `net10.0` |
| `Nino.Immutable` | `netstandard2.0`, `net8.0` |
| `Nino.UnityTypes` (Unity package only) | `netstandard2.1` |
| `Nino.AspNetCore` | `net8.0`, `net10.0` |
| `Nino.TypeScript` (`nino-ts`, a build tool) | `net8.0`, rolled forward to the SDK's runtime |
| `Nino.Generator` | `netstandard2.0` (Roslyn 4.3+): generator, analyzer, suppressor |
| `Nino.Generator.CodeFixes` | `netstandard2.0` (Roslyn 4.3+ Workspaces): code fixes, IDE only |

The generator and the code fixes ship in the `Nino.Generator` package, built three times from the same sources: against Roslyn 4.3
(`analyzers/dotnet/roslyn4.3/cs`), 4.8 and 4.12 (`roslyn4.8`, `roslyn4.12`). NuGet loads the newest build the
compiler can run. Roslyn 4.3 is the oldest compiler Unity supports, so the 4.3 build, which is also what Unity
uses, sticks to 4.3 APIs. The 4.8 and 4.12 builds add only call interceptors (see
[Top-level calls and interceptors](#top-level-calls-and-interceptors)); everything else is shared.

The `Nino` package references `Nino.Serialization` and `Nino.Generator`; `Nino.Immutable` (`netstandard2.0`, `net8.0`)
adds formatters for the `System.Collections.Immutable` types (arrays, lists, sets, sorted sets, queues,
stacks, dictionaries and sorted dictionaries).

`Nino.AspNetCore` adds MVC input and output formatters (`application/x-nino`) and Minimal API bodies and results,
reading request bodies from the request's `PipeReader` and serializing responses into its `PipeWriter`;
it asks `NinoSerializer.IsSupported(Type)` which types to claim. `Nino.TypeScript` is a build package: after
`CoreCompile` its targets run `nino-ts` out of process, which reads the `[assembly: NinoTypeLayout]`s of the built
assembly and its references with `MetadataLoadContext` (see [Generated code per type](#generated-code-per-type)) and
writes `<Assembly>.ts` and the `nino.ts` runtime, each only when its text changes.

Generated code is compiled in the user's project, which may be Unity (C# 9). Generated code therefore only
uses C# 9 features: no static abstract interface members, no ref fields, no raw string literals, no
file-scoped namespaces. Zero-cost composition uses the struct-constrained generic pattern instead
(`where TFormatter : struct, INinoFormatter<T>`), which every runtime (CoreCLR, Mono, IL2CPP, HybridCLR)
specializes per value-type argument.

Platform-specific code lives behind `#if` in `Nino.Core` only. The fallback paths (`netstandard2.0`) use
`System.Memory` and `System.Runtime.CompilerServices.Unsafe` and are written to produce the same bytes.
The test-suite runs against each `Nino.Core` build and checks golden payloads.

## Binary format

The normative, byte-exact specification of this format, for implementations in other languages, is [spec/wire-format.md](spec/wire-format.md).

All multi-byte values are **little-endian** and written unaligned (`Unsafe.WriteUnaligned`). Big-endian
hosts are not supported: `Writer`/`Reader` throw `PlatformNotSupportedException`.

| Value | Encoding |
| --- | --- |
| Unmanaged value (`int`, `Guid`, unmanaged structs, enums) | raw bytes, `Unsafe.SizeOf<T>()` |
| `bool` | 1 byte (0/1) |
| `T?` (nullable value type) | 1 byte has-value flag, then the value if present |
| Collection / array length | `int32`; `-1` means `null` |
| `string` (default) | `int32` header: `-1` = null, `0` = empty; `~n` (≤ -2) then `n` Latin-1 bytes when every char is at most U+00FF; else `n` then `n` UTF-16 code units (see below) |
| `string` with `[NinoUtf8]` | `int32` char count (`-1` = null, `0` = empty; nothing follows either), then `int32` byte count and the UTF-8 bytes (see below) |
| Array / list of unmanaged `T` | length, then one block copy of the elements |
| `T?[]` / `List<T?>` of raw `T` | length, then each element in the canonical `Nullable<T>` layout (fixed stride: flag, padding, value); an `IList<T?>`/`IEnumerable<T?>` member writes each element as a single `T?` instead (see `docs/pitfalls.md`) |
| Other collections | length, then each element |
| Dictionary | length, then key/value pairs |
| Multi-dimensional array `T[,..]` | `int32` rank-lengths (`-1` for null in the first slot), then elements in row-major order |

A default string is written in one byte a char when it can be: English, digits, identifiers, JSON keys and Western
European text are all Latin-1, and take half the bytes of UTF-16, while any other text keeps UTF-16 (never UTF-8,
whose decoding is a branchy pass and whose length differs from the char count). Writing narrows the chars with SIMD
and falls back to UTF-16 at the first char above U+00FF; reading widens the bytes with SIMD straight into the new
string. A `StringBuilder` is always written with UTF-16 chars (positive header), read by the same reader: it cannot
take chars without a copy, so widening would be a second pass on every read.

A `[NinoUtf8]` string is written as:

* `int32` **char count** `c`: its length in UTF-16 code units (`string.Length`, as in v3). `-1` is null and `0` the
  empty string; nothing follows either. Any other negative count is invalid.
* if `c > 0`: `int32` **byte count** `b`, then exactly `b` bytes, the string's UTF-8 encoding (an unpaired surrogate is
  written as U+FFFD, one code unit, so `c` is also the length of the string read back).

A UTF-16 code unit takes 1 to 3 bytes (a surrogate pair 4 bytes for 2 units), so every valid string has
`c <= b <= 3 * c`. For example `"é😀"` (3 code units: U+00E9 and a surrogate pair) is
`03 00 00 00` `06 00 00 00` `C3 A9 F0 9F 98 80`, and `null` is `FF FF FF FF`.

The reader loads both counts at once, checks that `b` bytes remain and that `c <= b` (which bounds the allocation by
the data) before allocating the string at `c` chars, then decodes exactly those `b` bytes into it in one pass and
moves past them; it never reads beyond them. The data is rejected (`InvalidOperationException`) when it ends early,
when a count is out of range (`b > 3 * c` decodes to more chars than the string holds), or when the `b` bytes are not
strict UTF-8 (including overlong forms, encoded surrogates and a sequence cut by the end of the range) or do not
decode to exactly `c` chars. The char count lets the reader allocate before decoding, the byte count lets it decode
the bytes in a single call without finding their end first.

### Objects

| Declared type | Header |
| --- | --- |
| `struct` | none |
| `sealed class` | 1 byte: `0` = null, `1` = instance |
| non-sealed class, abstract class, interface | `int32` type id: `0` = null, otherwise the runtime type's id |

The **type id** is a stable 32-bit hash (FNV-1a) of the type's name in runtime form
(`Namespace.Outer+Inner`, generic arguments in angle brackets). `[NinoFormerName("Old.Name")]` substitutes
the hashed name, so types can be renamed or moved without breaking existing data. The hash is implemented
once (`Nino.Core/Internal/NinoHash.cs`) and that file is also compiled into the generator, so ids computed at
compile time and at run time cannot diverge.

After the header comes the **body**: the members in declaration order (or `[NinoMember(n)]` order), base-class
members first. Consecutive unmanaged members are written as one block
(a single bounds check and reservation for the whole block).

The serialized members are the type's fields and properties (its own and its `[NinoType]` bases'):

* Public ones, and non-public ones with `containNonPublicMembers` (private members of a base class are never
  part of a subtype). Properties need a getter and a setter (`set` or `init`).
* Get-only auto-properties (`public int Id { get; }`) only when a constructor (or `[NinoConstructor]` factory)
  takes them: a parameter of the same name, ignoring case, or a name the `[NinoConstructor]` lists. Nothing
  else can restore them, so otherwise they are skipped.
* Explicit interface implementations (`int IHasId.Id { get; set; }`) never, even with
  `containNonPublicMembers`: they have no name generated code can use on the type. A `[NinoType]` interface
  serializes its own members through the implementing type's members of the same name.
* An `override` is the member it overrides, collected once with the base. A member hiding a base member with
  `new` is another member: both are serialized, the base one first. Generated code reaches the hidden one
  through a cast (`((Base)value).X`), or `[UnsafeAccessor]` when it is not public; without either (a
  non-public hidden member of a generic type, or before .NET 8) it is skipped with warning NINO022. In keyed
  types, a hidden member that is serialized next to its hiding member has the key of `Base.X`.

A `[NinoType]` struct is written as its **raw memory** (one copy, and arrays/lists of it as one block) when it
is unmanaged, not version tolerant, has sequential layout (`Pack` and `Size` allowed), has no custom member formatters, and
every instance field (including auto-property backing fields) is serialized. Nested `[NinoType]` struct fields
must qualify as well. A struct of another assembly is judged the same whether it is referenced as source or as
metadata: Roslyn links a metadata field only to events, so a backing field with no `AssociatedSymbol` is matched
to its property by its name, `<Name>k__BackingField` (issue #200, `CrossAssemblyRawTests`). `[StructLayout]` and `[FieldOffset]` are
not custom attributes in metadata, so for a type of a referenced assembly the generator reads them from its type and
field tables (`RawLayout`, issue #202). The bytes are the canonical (64-bit CoreCLR) layout with padding zeroed, converted on
runtimes that lay the type out differently (see `docs/pitfalls.md`), so reordering fields changes the format; give the struct `format: NinoFormat.VersionTolerant` (or ignore a field) to opt out. Otherwise structs are written
member by member.

### Collections

User collection types work without attributes: a class derived from `List<T>`, `HashSet<T>`, `Queue<T>`,
`Stack<T>` or `Dictionary<TKey, TValue>`, or implementing `IDictionary<TKey, TValue>`,
`IProducerConsumerCollection<T>` or `ICollection<T>`, is written exactly like the collection it extends (a
`List<int>` subclass is one block copy, like `List<int>`). A `[NinoType]` collection writes its elements first and
then its own serialized members, so a collection with extra state (for example a synchronized list) keeps both.
Reuse deserialization reuses the existing collection instance (and so its comparer).

#### User collections

Nino creates a collection only when the slot is `null`, through `NinoActivator<T>.Create()`:

* A public static `[NinoFactory]` method declared on the type, returning it and callable without
  arguments (ET's pooled `static ListComponent<T> Create()`), wins; then a public constructor callable without
  arguments, whose optional parameters take their defaults (ET's `MultiMap(int maxPoolCount = 0)`). A type whose
  C# 11 `required` members that constructor does not set (`[SetsRequiredMembers]`) needs the factory.
* The generator registers the creator of every closed collection type it binds to a `[NinoCreated]` formatter
  parameter, as `NinoActivator<T>.TryRegister(static () => new T())` (or `T.Create()`), in the same lazy by-type-id
  dispatch as the closed formatters (`NinoGenUsages`): nothing runs at startup, and AOT runtimes call compiled code.
  A plain `List<T>` is created directly (with its capacity) and gets no creator. Types the generator never saw
  (bound at runtime, elements of runtime-bound formatters) are resolved by reflection on first use, in the same
  order, and cached as a delegate: `Activator.CreateInstance<T>` for a parameterless constructor (what `new T()`
  compiled to before), a delegate over the factory, or `Invoke` with the default arguments.
* A factory may hand out a pooled instance that is not empty: it is treated like an existing instance (stale
  elements are dropped and recycled), so the result holds exactly the data. A factory returning `null` is a clear
  `InvalidOperationException`.
* Nino uses the public collection API: `Add`/`Clear` of an `ICollection<T>` (so `Collection<T>` hooks such as
  `InsertItem` run), the indexer of an `IDictionary<TKey, TValue>`, `Enqueue`, `Push`, `TryAdd`. `List<T>`,
  `HashSet<T>` and `Dictionary<TKey, TValue>` subclasses are filled through the base type, so members hidden with
  `new` (ET's `SyncList`) are not called.
* What is not supported is reported, never guessed: a collection Nino cannot create (abstract, constructor with
  required arguments, non-public constructor, required members not set), a struct collection, a non-generic
  collection (`ArrayList`) and a type implementing `ICollection<T>` for two different
  `T` (ambiguous in both resolvers, whatever the interface order) have no formatter: the member is skipped with
  NINO007, and the runtime resolver finds no formatter either. A collection whose `IsReadOnly` is true is a clear
  `InvalidOperationException` when deserialized into. The same key (or set element) twice in the data, under the
  reader's comparer, is a clear `InvalidOperationException` when a dictionary or `HashSet<T>` is filled from empty;
  merging into a non-empty dictionary in place does not look for repeated keys (the last value wins).
* The declared type decides what is created: a `List<int>` member holding a `ListComponent<int>` is read back as a
  `List<int>` (reuse deserialization keeps the `ListComponent<int>` it holds). Collections are values: the same
  instance in two members is written twice and read as two instances (`trackReferences` tracks `[NinoType]`
  objects, not collections).

### Version tolerance

A type marked `[NinoType(format: NinoFormat.VersionTolerant)]` (or every positional type when the project defines
`WEAK_VERSION_TOLERANCE`) writes its body as one region: one `int32` byte length, then the members of its whole
hierarchy (each `[NinoType]` class from the root down to the type itself), root first:

```
header | int32 length | members of every level..., root first
```

On read the region is sliced once. Members missing from it (old data: the region ends early) keep their current
value when reading in place, or their default, and trailing bytes (new data read by old code) are skipped. So
members can be appended to the **most derived type**, and each nested tolerant object delimits itself, so this holds
at any depth.

* One length per object, whatever the depth of the hierarchy (earlier v4 builds wrote one length per level:
  `header | { int32 levelLength | level members... } per level`). Example, a sealed `Leaf : Base` with
  `Base { int A; }` and `Leaf { int B; }`, `A = 1, B = 7` (after the not-null flag `01`):

  ```
  per level (old):   01 | 04000000 01000000 | 04000000 07000000
  one region (now):  01 | 08000000 01000000 07000000
  ```

* **Appending to a base class is not tolerated.** The members of every level share the region, so a member appended
  to a base lands before the derived members and shifts them: an old reader reads the new base member's bytes as
  its first derived member (then skips the rest), and a new reader reads old data's first derived member as the new
  base member. Only the members of the type itself (the end of the region) can be appended; keyed members tolerate
  the rest (see below). There is no diagnostic: the generator sees one version of the code, so it could only flag
  every tolerant class that has tolerant `[NinoType]` subclasses (a possible info-level analyzer), not an actual
  append.
* Not tolerated either: inserting, removing or reordering members, and adding or removing a `[NinoType]` class in
  the hierarchy. Use keyed members for those.
* A type without members still writes its length (4 bytes), so its first member can be added later. Members of a
  `[NinoType]` interface lead the members of the first class implementing it, so marker interfaces cost nothing;
  members appended to an interface are not tolerated.
* Positional types without version tolerance are unaffected: no lengths at all.
* A derived type that delegates to its base's `WriteMembers`/`ReadMembers` opens the region itself, calls the base's
  members, then writes or reads its own inside the same region: the members are positional, so they line up with an
  inline layout. On read every member is preceded by an end-of-region check (`Reader.Eof`), which ends the body.

### Keyed members

A type marked `[NinoType(format: NinoFormat.Keyed)]` writes each member with a key and a length, so members can be removed,
reordered or inserted freely (MessagePack's `[Key("name")]`), mainly for data authored in the Unity editor:

```
header | int32 bodyLength | { int32 key, int32 length, value }*
```

* The key is the FNV-1a hash of the member name, computed by the generator, so no strings are written.
* Writing stays straight-line code with constant keys; consecutive unmanaged members share one reservation.
* Reading first assumes the declaration order (data written by the same schema): each unmanaged member is
  matched with one 8-byte compare of (key, length), others with a 4-byte key compare. At the first mismatch it
  falls back to a loop that dispatches any key with `switch (key)`; unknown keys are skipped by length, and
  members that are missing keep their current value (in place) or their default (re-created types).
* Keyed types are never raw structs and do not delegate members to or from base classes, since each member
  is self-describing. Polymorphism works as usual: the type id comes before the body.
* Cost: 8 bytes per member. On an 8-member class (net8, no tiering) serialize takes about 1.3x and
  deserialize about 1.2x the positional time, while staying faster than MemoryPack's positional mode.
* A member-name hash collision within one type is a compile-time error (NINO014), so it cannot corrupt data.
* Renamed members: `[NinoFormerName("Old")]` on a field or property (once per earlier name) adds the former
  name's key as an extra `case` of the fallback `switch`; writes and the in-order path use the current name only,
  so members without the attribute cost nothing and renamed ones only reach the fallback when reading old data.
  Former names take part in the collision check (NINO014). Positional and version-tolerant types write no
  names, so there the attribute is a warning (NINO024).

### Reference tracking

A class marked `[NinoType(trackReferences: true)]` (and every subtype of it) is written behind an `int32`
reference header, so shared references and cycles round-trip as the same object graph:

```
int32 ref: 0 = null | n > 0 = the n-th object already written in this call | -1 = new object, then its usual encoding
```

* For a sealed class the new-object encoding is the body alone (the header already says it is not null); for
  other classes it is the type id and the body, so polymorphism works as usual.
* The header belongs to the object, not to the slot: a tracked object held by a slot whose declared type is not
  tracked (`object`, or a base class or interface without `trackReferences`) is written as
  `int32 type id | int32 ref | body if new`, since the reader learns that the object is tracked from the type id.
  A back reference there is the type id and the index (8 bytes); the index must name an object of that type. Both
  forms share one table, so an object keeps its identity across slots of any declared type, and cycles through
  `object` or interface members resolve. The dispatch handler of a tracked type is a separate class
  (`TrackedSubtypeHandler`), so untracked types pay nothing for this.
* The writer keeps an identity map (object to index) and the reader an index to object list. Both are rented
  from a per-thread cache on the first tracked object and returned when the top-level call ends, so calls
  without tracked objects touch nothing and tracked calls do not allocate in steady state. Both are per call,
  so concurrent calls stay independent.
* Cycles: the generated reader records the instance right after creating it (`Reader.SetReference`), before
  reading its members, so a back reference inside its own members resolves. Types that must be re-created
  from constructor arguments or read-only members cannot be tracked (error NINO016); `trackReferences` on a
  struct is ignored with a warning.
* Bodies read through a region (version tolerant or keyed) hand their table back to the parent reader
  (`Reader.EndRegion`), so references stay shared across regions.
* Reuse deserialization reuses instances for new objects as usual; a back reference replaces the current value
  without recycling it, since the old value may still be referenced elsewhere in the graph.
* Cost (net8, an 8-member sealed class): about 28 ns per top-level call for renting the tables, plus 4 bytes
  and one identity lookup per object. A list repeating one object 100 times is written in a fraction of the
  bytes and time of the untracked list.

### Deep nesting

Class bodies are written and read by nested calls, so a recursive type (a linked list, a tree) would use stack in
proportion to the depth of its data: a long list overflows the stack on write, and a payload that nests one level
every few bytes overflows it on read, which ends the process. Instead, the writer and the reader count the class
bodies open (`Depth`; struct bodies do not count, since a struct cannot contain itself). A body that would open
below 128 open bodies is **deferred**: the writer queues the object and writes nothing for its body yet; the
reader, which counts the same way, creates the instance (or reuses the one in the slot), puts it in the slot and
queues it. The queued bodies are written and read later from a shallow point, each starting a fresh count, so the
stack stays bounded whatever the depth, and data cannot make it deeper. Data shallower than that is unchanged:
deferral adds no bytes and no marker.

Where the deferred bodies go:

* **Outside any region** (positional types): after the top-level object, once every body around it has closed,
  in the order they were deferred. Bodies deferred while writing those are appended to the queue.
* **Inside a region** (a version-tolerant body, a keyed body or member): at the end of the **outermost** region,
  as a section of `(int32 key, int32 length, body)` entries followed by the section's offset in the region:

  ```
  int32 (regionLength | 0x80000000) | members... | { int32 key, int32 length, body }* | int32 sectionOffset
  ```

  The key is where the deferral happened, relative to the region's start. A reader that skips part of the region
  (members it does not know, trailing members of a newer version) never queued the objects in it, so it matches
  its queue against the keys in order and skips the other entries by their length. The reader's region covers
  the members only, so the section is never read as members. Only the outermost region holds a section: writing
  the section of an inner region would nest a flush inside the bodies being written, and the stack would grow
  again. A region without deferred bodies is written as before.

Limits and checks:

* Classes built by their constructor (records with a primary constructor, init-only or read-only members) cannot
  be put in a slot before their members are read, and classes compared by value (records, `GetHashCode`
  overrides, `IComparable`) must not be put in a hashed or sorted collection before then, so these are never
  deferred: past 128 they nest inline, and past 512 open bodies writing or reading throws.
* An untracked cycle would be written forever (each pass defers the next). The writer compares each deferred
  object with the deferred object whose body it was found in, and with the one at the last power-of-two length of
  that chain (Brent's algorithm), so a cycle throws, pointing at `trackReferences`, after a number of steps
  proportional to its length. Tracked objects are written once, so tracked cycles never reach this.
* A tracked object that is deferred is recorded (`Reader.SetReference`) when it is created, so back references
  to it may come before its body.
* The queues are rented from a per-thread cache on the first deferral and returned when the top-level call ends:
  reading a 100,000-node list into the same objects again allocates nothing.
* Data nested inline deeper than 128 is not what a writer produces; inside a region it is rejected (the reader
  expects a section), outside one it is read as deferred bodies, all with a bounded stack.

Cost: the count is one compare and one increment per class body. Versioned and keyed bodies call
`BeginRegion`/`EndRegion` (`ReadRegion`/`EndRegion`), which also count regions.

Most types cannot nest deeply, and for them the generator removes even that. It computes, from symbols only, how
many class bodies below its own a value of each type can open (`TypeFacts.DepthBound`): a sealed class counts one
level, a struct none, a built-in collection, nullable or tuple counts what its elements count. Recursive types,
non-sealed classes and interfaces (a subtype may come from anywhere), generic types, types generated in other
assemblies, custom formatters and `object` are unbounded. A sealed class or struct whose bound is below 128 (and
that is not tracked) gets a `__Shallow` twin of its formatter: the same bytes, with no depth counting and no deferral
checks, its members written and read through their own twins. A nesting sealed class checks the depth once on
entry and takes the twin when the whole bound fits below 128; otherwise it takes the regular path, so deferral
still works wherever the data actually gets deep.

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
  The tuple formatters (`ValueTupleFormatter<T1..Tn, TF1..TFn>`, `TupleFormatter<...>`, arity 1 to 7) are
  written by a template, `tools/CodeGen` (`dotnet run --project tools/CodeGen`), into
  `Formatters/TupleFormatters.g.cs`; a unit test compiles the template and fails when the file is out of date.
* **Generated formatters** are structs in `<Assembly>.NinoGen`, one per `[NinoType]`. Members call
  their formatter as `default(TFormatter).Serialize(...)`, which is a direct, inlinable call.
* **`CachedFormatter<T>`** is a struct that forwards to the formatter `NinoFormatterCache<T>` holds (an internal
  `NinoFormatter<T>` class wrapping the struct formatter). It is used for type parameters of generic `[NinoType]`s,
  for types from other assemblies, and for types resolved at runtime. It costs one virtual call.
* **Custom formatters**: `[NinoCustomFormatter(typeof(F))]`, where `F` is a struct implementing `INinoFormatter<T>`
  for exactly the member type (NINO008 otherwise), called as `default(F)` and inlined. Every formatter a user
  writes is a struct: `NinoFormatter<T>` is only the runtime dispatch carrier and is internal. There is no
  run-time registration API: formatters are declared with `[assembly: NinoFormatter]` or `[NinoCustomFormatter]`.

### Formatter registrations

Which formatter a type gets is not a table in the generator. Apart from what is built in (strings, `object`,
raw unmanaged values, `[NinoType]`s), every formatter is declared on its assembly:

```csharp
[assembly: NinoFormatter(typeof(QueueFormatter<,>))]                       // INinoFormatter<Queue<T>>
[assembly: NinoFormatter(typeof(ListFormatter<,,>), Priority = 40)]        // INinoFormatter<TList>, [NinoCreated] TList : List<T>
[assembly: NinoFormatter(typeof(InterfaceListFormatter<,,>), typeof(IList<>), typeof(IEnumerable<>))]
```

Nino.Core declares its BCL formatters this way (`Formatters/BuiltInFormatters.cs`; `PriorityQueue<,>` only in its `net8.0`
and `net10.0` builds, since netstandard and Unity have none), the `Nino.Immutable` package declares the immutable
collections, `Nino.UnityTypes` (in the Unity package only) Unity's `AnimationCurve`, `Gradient` and `RectOffset`, generated code declares the formatter definition of each generic `[NinoType]`
(`[assembly: NinoFormatter(typeof(NinoGen.Box_1Formatter<>))]`, which implements `INinoFormatter<Box<T>>` in exactly
the type's parameters), and any library or application can declare formatters for its own or third-party types. The generator (`FormatterRules`, `FormatterResolver`) and the runtime
(`NinoFormatterResolver`, for type parameters of generic `[NinoType]`s) apply the same registrations with the same
rules, and a test checks that both write the same bytes:

1. A formatter implements `INinoFormatter<P>` once; `P` is written in its type parameters. Matching `P` against
   the type binds the parameters that occur in it.
2. A type constraint of a bound parameter (`where TList : List<T>`) is matched against the bound type, its base
   types or its interfaces, binding the parameters it mentions (`T`).
3. A parameter constrained to `INinoFormatter<E>` becomes the formatter resolved for `E`, so element formatters
   compose and inline.
4. Every constraint is checked with its C# meaning (`unmanaged` is C#'s unmanaged).
5. Two markers on type parameters state what the formatter does with the bound type, and both resolvers check them
   identically:
   * `[NinoRaw] T` (with `where T : unmanaged`): values are copied as raw memory, so `T` must be a type Nino writes as
     its raw bytes (stricter than C#'s unmanaged: no `Nullable`, tuples, `nint`, `DateTimeOffset`, or `[NinoType]`
     structs that are not raw). `UnmanagedFormatter`, `UnmanagedArrayFormatter`, `NullableArrayFormatter`,
     `UnmanagedListFormatter` and `NullableListFormatter` mark their element parameter. A plain `unmanaged`
     constraint, such as the one a generated formatter of `[NinoType] class Box<T> where T : unmanaged` copies from
     the type, binds any unmanaged type.
   * `[NinoCreated] TCollection`: the formatter creates instances through `NinoActivator<TCollection>.Create()`, so the
     bound type must be a non-abstract class with a public static `[NinoFactory]` factory or a public
     constructor callable without arguments (every parameter optional). See "User collections" below. A plain `new()`
     constraint is still honored for third-party formatters that call `new T()`.

Lookup order: registrations of the type's own definition (and of arrays, by rank) in `Priority` order, then raw
unmanaged values, then `[NinoType]`s, then registrations of a bare type parameter (`INinoFormatter<TCollection>`),
which match by their constraints (or the `targets` they list). NINO020 reports a registration that cannot be used.
The generator ignores the generated registrations of generic `[NinoType]`s: it names its own generated formatters
directly and reaches other assemblies' through `CachedFormatter<T>`, so only the runtime resolver closes them.

The runtime reads an assembly's registrations when it first meets one of its types, or when a lookup misses (then
every loaded assembly). A formatter-only library may not be loaded yet when only a runtime-closed generic needs it
(`Box<T>` with an `ImmutableArray<T>` member names no `Nino.Immutable` type), so each generated registry also calls
`NinoRegistry.EnsureInitialized` for every referenced assembly whose registrations its compilation saw.

### Formatter cache and registration

`NinoFormatterCache<T>.Instance` is a static generic field, so lookup is free after the first use. It is
populated by:

1. Generated registration code, on demand (including the closed generic `[NinoType]`s the compilation names, see
   "Polymorphism"). Each assembly carries `[assembly: NinoGenerated(typeof(Registry))]`.
   Nothing has to call it: the first lookup that meets a type of that assembly (or, for polymorphic reads, an
   unknown type id, which scans the loaded assemblies once per newly loaded assembly) runs the registry's
   static constructor. `[ModuleInitializer]` (.NET 5+) and `RuntimeInitializeOnLoadMethod` /
   `InitializeOnLoadMethod` (Unity) only make this eager, and keep the registry rooted for the IL2CPP linker.
   Hot-update runtimes such as HybridCLR, which `Assembly.Load` an assembly without running any of those entry
   points, therefore need no manual `Init()` call (covered by `HotUpdateAssemblyTests`). The registry itself
   registers nothing yet. A lookup that misses asks the
   registries for the type's id: the plain name hash for by-type lookups (no attribute reflection) or the wire
   id for polymorphic reads; `[NinoFormerName]` types are listed under both. The dispatch is a switch over
   small bucket methods of about eight ids, because the JIT resolves every call in a method it compiles: one
   switch over every type would load every formatter on the first lookup. Startup no longer grows with the
   number of types (1100 types: 309 ms → 67 ms process time on .NET 8, against 43 ms for an empty app).
2. Lazy resolution on first access: built-in generic types (`List<T>`, `Dictionary<,>`, arrays, tuples,
   and so on) and generic `[NinoType]`s are constructed with `MakeGenericType` from the `[assembly: NinoFormatter]`
   registrations (see above). The generated registrations of closed generic `[NinoType]`s (item 1) only name
   the same closed formatters ahead of time, for AOT runtimes and reads by type id.

Registration is idempotent and thread safe: the first registration of a type wins (published with a compare-exchange),
and readers never lock. A registered formatter never changes, except when its assembly is removed.

`NinoRegistry.Remove(assembly)` lets a collectible `AssemblyLoadContext` unload an assembly with generated code
(issue #199). Every static cache keyed by `Type` (the registries, the resolver's scanned assemblies and
`[assembly: NinoFormatter]` rules, `NinoFormatterCache` registrations, boxed formatters, type ids, raw layouts and
the `NinoPolymorphic<TBase>` tables) drops the entries whose type mentions the assembly, including arrays and
generics built from its types. Generic statics closed over its types (`NinoFormatterCache<T>.Instance`) belong to
the collectible loader allocator and go with it. The net8.0/net10.0 builds of Nino.Core call `Remove` from the
context's `Unloading` event, hooked when the assembly registers; the netstandard builds cannot reference
`AssemblyLoadContext`, so there the caller calls it (`UnloadTests`).

### Polymorphism

Every `[NinoType]` registers one handler, once, in the global (`NinoPolymorphic<object>`) registry, from the
generated code of its own assembly. Registration is decentralized, so the generator never scans referenced
assemblies. Each base type (non-sealed class or interface) has its own `NinoPolymorphic<TBase>` table, which
adopts a subtype from the global registry on its first lookup after checking that it derives from `TBase`.
Registering per type instead of per (base, subtype) pair keeps generated code linear in the number of types
rather than in the sum of hierarchy depths, and avoids one generic instantiation per pair.

* **Generated dispatch (fast path)**: the topmost dispatching class of each hierarchy in an assembly (and each
  `[NinoType]` interface) gets two methods, emitted with the registry (`NinoGen.Dispatch.g.cs`), that list the
  subtypes declared in the same assembly (non-generic, not tracked). Classes below the root call the root's.
  * Serialize: a chain of `value.GetType() == typeof(Sub)` checks, each one method table compare, writing the
    subtype's id and body directly (up to 64 subtypes; beyond, the registry). With 20 subtypes this writes 1.3x
    as fast as the registry when the subtypes repeat in a fixed order, and 3x when they come in random order.
  * Deserialize: `switch (id)` over the subtype ids, each case a tail call to a one-line method of the root's
    formatter (`__ReadExact{n}`, not inlined, so the switch stays small) that checks the instance in the slot and
    reads the subtype's body. In that non-generic method `NinoObject.ReadExact` is not shared code; called from the
    switch it would be (the subtype is a class), taking a generic context in a register and a frame slot on every
    call. The ids are hashes, so the switch is a binary search of conditional branches. In random
    order this reads 1.5x as fast as the registry, whose indirect call mispredicts; a perfect hash with a dense
    switch (an indirect jump) was measured too and mispredicts the same way. When the subtypes repeat in a fixed
    cycle the registry's indirect call is perfectly predicted and the switch is about 10% slower.
  * A generic base (a generic abstract class or interface, a generic class, or an abstract class nested in a
    generic type) is a root of its own. For each closed instance of it that subtypes in the same assembly derive
    from (`Box<int>` for `IntBox : Box<int>`), a static class `__NinoDispatch{n}` holds the same two methods as a
    non-generic root; the generic formatter's methods select it with `typeof(Box<T>) == typeof(Box<int>)` and pass
    the value or slot on as that type (`NinoObject.SameType`, `NinoObject.SameTypeSlot`, which reinterpret without a
    check). The JIT folds the type compare where the type arguments are value types; where they are reference types
    (shared code) it is one lookup in the generic context. The methods live in a non-generic class so that
    `ReadExact` there is not shared code either. Other closed instances, and generic subtypes, go through the
    registry. Before, a generic base had no generated dispatch and went through the registry, which wrote and read an
    array of subtypes in random order several times slower than a non-generic base; now both take the same time
    (`GenericPolymorphicTest`).
  * A class below the root reads through the root's switch and then checks that the type the data named is one
    of its own (`NinoObject.EnsureSubtype`), so data naming a sibling type throws instead of being stored.
  * Each subtype costs three lines per hierarchy (not per ancestor), so generated code stays linear in the number
    of types: the 330-type workload grows by 12%. Writes call `NinoObject.WriteExact` from the chain (inlined where
    the caller's budget allows, else shared code): a non-generic method per subtype there too wrote 4.5% fewer
    instructions for an array of 20 subtypes, but more where the bodies would have been inlined, and cost about
    2.5% more compile time on the 330-type workload.
  * A subtype whose body is one raw block (a class that is not version tolerant, keyed or tracked, whose members
    are all raw values, see `INinoRawBodyFormatter<T>`) is written by `NinoObject.WriteExactRaw`: one capacity
    check (`Reserve`) for its type id and body together, then constant-offset stores, instead of one check for the
    id and another for the body. The same merge applies where the formatter writes its own subtype (`TryWriteOwnRaw`,
    `SerializeRaw`, `SerializeSealedRaw`: id or null flag with the body) and to the top-level twins
    (`SerializeTopRaw`). The bytes are the same; the deep path (nesting at `NinoDepth.Inline`) is unchanged.
* **Serialize (registry)**: other subtypes (other assemblies, generic subtypes, tracked types, more than 64): look up
  the runtime type in a lock-free open-addressing table and call the subtype's body serializer through its
  handler.
* **Deserialize (registry)**: the switch's default case reads the type id's handler from a second
  open-addressing table keyed by id. Unknown ids trigger lazy initialization of not-yet-loaded generated
  registries before failing.
* **Generic `[NinoType]`s** have one formatter definition and no registration of their own, so each closed type
  (`Box<int>`) is registered by id like a non-generic type, from three sources:
  1. The generator registers every closed generic `[NinoType]` its compilation names: member types (including
     element and type arguments, `List<Box<int>>`), base types (`class IntBox : Box<int>`), type arguments of
     `NinoSerializer`/`NinoDeserializer` calls, and recursively the members and bases of those closed types (four
     levels deep). They are cases of the same lazy by-id dispatch (`NinoGenUsages`), under the closed type's id
     (`[NinoFormerName]` types also under the name id): `NinoFormatterCache<Box<int>>.TryRegister<Box_1Formatter<int>>()`
     and `NinoPolymorphic.Register<Box<int>, Box_1Formatter<int>>(id)`, so AOT runtimes have the instantiation and a
     reader can map the id back to the type without having seen it. Closed types of a generic `[NinoType]` from
     another assembly (whose formatter is internal to it) are registered with `NinoPolymorphic.Register(typeof(..))`.
  2. Writing an instance registers its runtime type: a lookup by type that misses closes the formatter definition
     (`MakeGenericType`) and registers it. A read by an unknown id, as a last resort, also checks the closed generic
     `[NinoType]`s whose formatter this process has resolved.
  3. `NinoPolymorphic.Register(Type)` / `Register<T>()`, for an instantiation a process reads but never names nor
     writes (created only from type parameters or by reflection, for example in another assembly). Such a type is
     otherwise an unknown id; this is the one case that needs a call at startup.
* Registration is re-entrant: a generated registry registers a closed generic whose formatter is internal to
  another assembly through `NinoPolymorphic.Register(Type)`, which asks every registry for that type id again,
  including the one running. `NinoRegistry.RegisterType` keeps the types the thread is registering and returns at
  once for one of them, so the outer call closes the formatter itself (before, a non-generic base nested in a
  generic class, `Holder<long>.Base`, with subtypes in another assembly overflowed the stack on first use).
* Registries are immutable snapshots replaced copy-on-write, so concurrent readers never see a torn
  entry. (The v3 inline caches could pair one type's id with another type's delegate under contention.)

### Concurrency and cache lines

Cache-line reasoning assumes 64-byte lines.

* No hot path writes shared memory, so serializing threads never contend for a line. Scratch buffers are
  `[ThreadStatic]` and are taken out of their slot while in use, so re-entrant calls are safe. There is no
  global "default buffer" guarded by `Interlocked` (v3 bounced that line between all serializing threads).
* Formatter lookup is a static generic field (`NinoFormatterCache<T>.Instance`), written once at
  registration and only read afterwards.
* Polymorphic tables use 16-byte entries with the key inline next to the handler (four per line) and stay at
  most half full, so a lookup normally reads one line, which also holds the handler it dispatches to. Packing the
  keys in an array of their own (16 ids per line) and comparing them four at a time (`Vector128`) was measured
  and dropped: a hit then reads a second line for the handler, and at this load factor the vector setup costs
  more than the one or two scalar compares a probe takes (`object[]` of 1,000 polymorphic items: 40 µs → 45 µs).
* Mutable registration state (`_count`, locks) is only touched during registration, which happens at
  startup, so no padding is needed to protect the read-only tables from false sharing.
* Consecutive unmanaged members are reserved and written as one block, and unmanaged arrays, lists and raw
  structs are single block copies, so the writer streams through the buffer line by line. Each member of a block
  is stored at the block's reference plus a constant offset, folded into the store's address: `NinoUnsafe`'s
  offset overloads (`Write`, `WriteIn`, `WriteValue(In)`, `Read`) add the offset in the access itself rather than
  pass `Unsafe.Add(ref block, offset)` on to the overload without one, where an offset that is not an IL constant
  (it follows a struct, `n + NinoUnsafe.SizeOf<S>()`) made the reference a temporary and cost an `lea`/`add` per
  member (x64 and ARM64).
* `NinoPool<T>` reads its generation (and warmup flag) on every take and add. They sit on a 64-byte line of their
  own (`NinoPoolLine`, a non-generic explicit-layout base class, since generic classes cannot have one: 56 bytes
  of padding before and after), away from the shared stack's count and lock, which batch transfers write, and
  from neighbouring objects. Measured with 4 threads churning 100 instances each, the cost there is the lock
  itself; the padding is kept so the fast path never shares a line with a writer.
* No static field is written on a hot path: formatter instances, `NinoRecycler.PoolsInUse`
  are written once; scratch buffers and reference tables are thread-static.
* Raw-key/raw-value dictionaries, unmanaged arrays and `T?[]` read and write with one bounds check for the whole
  collection; masked copies (padding cleared) use vectors with aligned stores (see `NinoMask`).
* `bool` arrays and lists are block copies, like every raw array: bytes other than 0 or 1 (corrupt or foreign
  data) are not normalized. A fused copy-and-normalize pass (vector unsigned minimum with 1, 512/256-bit, unrolled)
  was measured and dropped: on par for short arrays but twice the time of the runtime's `memcpy` on long ones
  (10,000 bools: 79 ns → 155 ns), which streams faster than any load-min-store loop.

### Reuse deserialization and pooling

* `null` target: a new instance is created (from the pool for `[NinoPooled]` types, or through a
  `[NinoFactory]` factory; collections through `NinoActivator<T>`, see "User collections").
* Existing target of the right type: populated in place. Lists and arrays reuse elements by index.
  Dictionaries are merged by key: each value is read into the value its key already maps to, and keys missing
  from the data are removed.
* Strings are compared, not replaced: a string member, a list or array element, or the value of a key already in a
  dictionary keeps its string when the data holds the same chars, so an unchanged string costs no allocation
  (`Reader.ReadString(string current)` and `ReadUtf8(string current)`, which generated code passes the member's
  current value; `ReadStrings` compares each element). A `[NinoUtf8]` string is compared without decoding, so only
  ASCII text (as many bytes as chars) is kept; other UTF-8 text is decoded into a new string. A `null` slot, a new
  instance and a dictionary read from empty (`ReadStringPairs`) read new strings.
* Types whose members cannot be assigned after construction (constructor parameters, `init`-only, get-only or
  `readonly` members) are re-created, and the old instance is recycled. So are types with C# 11 `required`
  members whose parameterless constructor is not `[SetsRequiredMembers]`: a `[SetsRequiredMembers]` constructor
  is used when one matches, else the object initializer sets the required members.
* Whenever an existing non-null instance is dropped (payload is `null`, list shrinks, array reallocates,
  dictionary key removed, polymorphic type changes), it is passed, exactly once, to
  `NinoRecycler<TDeclared>.Recycle`: the handler registered with `NinoSettings.SetRecycler<TDeclared>`, else
  `ReturnToPool()` for `INinoPooled` instances (`[NinoPooled]` types, and pooled user collections that implement
  the interface themselves, such as ET's `ListComponent<T>`), else nothing (the garbage collector takes it). That
  fixes the leak where a pooled `List<T>` assigned in a factory was simply overwritten with `null`. Collections walk
  the elements they drop only when that can do something: a handler is registered, the element type implements
  `INinoPooled`, or (for a non-sealed element type) pooled instances exist, which a `NinoPool<T>` or a
  `NinoActivator<T>` of an `INinoPooled` type signals (`NinoRecycler.PoolsInUse`). Ownership is exclusive: an
  instance aliased by two slots that both drop it is recycled once per slot, so pooled types should guard against
  a double return (as `NinoPool<T>` does).

Who recycles what is decided in one place, the primitives of `NinoRecycler`; formatters never call `Recycle`
on a slot themselves. A formatter's `Deserialize(ref value)` owns the instance in `value` and leaves it in exactly
one state:

| State | Primitive | Used by |
| --- | --- | --- |
| reused in place | none | every formatter, when the instance fits |
| handed on | `Reuse<TDeclared, TConcrete>(ref value)`: returns the instance if it is a `TConcrete` (that formatter now owns it), else recycles it as `TDeclared` and clears the slot | interface collections (read into `List<T>`, `HashSet<T>`, `Dictionary<,>`), `NinoObject` and polymorphic reads (by exact runtime type) |
| replaced | `Replace(ref value, replacement)`: stores the new instance, then recycles the old one | tuples, reallocated arrays, generated code for re-created (constructor / `init`) class types |
| discarded | `Discard(ref value)`: the data holds `null` | every reference formatter, `T?` |

Elements a collection drops (the tail of a shrinking list or array, the contents of a set, queue, stack or
dictionary that is cleared and refilled, dictionary entries whose key is gone, the pairs of a priority queue that
is cleared and refilled) are each recycled once with the element type (`DiscardAll`, `DiscardPairs`,
`DrainAndDiscard`); for dictionaries and priority queues both halves of each pair are. An instance dropped as a whole takes its elements
with it: they are not recycled separately. `RecycleOwnershipTests` pins these counts for every formatter family.

`[NinoPooled]` on a sealed, in-place `[NinoType]` class makes the generator add `ReturnToPool()` and two
partial methods the class must implement (the compiler and NINO019 report them; a code fix adds both):
`private static partial IEnumerable<T> OnNinoPoolWarmup()`, run once before the first deserialization of the
type, whose instances go straight into the pool, and `private partial void OnNinoPoolReturn()`, run by
`ReturnToPool()`. Wherever Nino creates an instance of the type (top level, member, element) it first takes
one from the pool and deserializes into it in place, reusing its arrays, lists, dictionaries and nested
objects.

`NinoPool<T>.Shared` holds a locked shared stack (`MaxRetained`, default 256, set through `NinoSettings`). Each thread also keeps a stack
of up to 64 instances in a `[ThreadStatic]` field of the pooled type itself: a thread-static of a generic
class is reached through shared generic code with a run-time lookup for reference types, one in the user's
type is a direct thread-local access. A full thread stack moves half of itself to the shared stack and an
empty one takes half a stack back, one lock per batch. `Clear()` bumps a generation that thread stacks check.
Returning an instance twice throws (a per-instance flag, reset when rented); one that is never returned is
simply collected. Measured in [v4-benchmarks.md](v4-benchmarks.md): a string-free graph deserializes 3x faster
than a fresh one with no allocation, and a list of 100 pooled elements twice as fast.

### Settings

Process-wide settings are set through one static class, `NinoSettings`, and must be **set before first use**: they
are plain fields read without synchronization, so they belong in startup code that runs before the first
serialization call on any thread.

| Setting | Meaning |
| --- | --- |
| `SetRecycler<T>(Action<T>)` | handler for instances dropped from slots declared as `T` (null removes it) |
| `DefaultPoolMaxRetained` | shared-stack size of pools created from now on (default 256) |
| `SetPoolMaxRetained<T>(int)` | shared-stack size of `T`'s pool |

A value read on a hot path stays next to the code that reads it, so the JIT sees a static field of the closed
generic type rather than a lookup: the recycler handler is stored in `NinoRecycler<T>` and a pool's size in
`NinoPool<T>`, and `NinoSettings` is the only code that writes them. Registrations (formatters, polymorphic types)
are made by generated code and are not settings.

## Source generator

### Pipeline

```
ForAttributeWithMetadataName("Nino.Core.NinoTypeAttribute") ───────────────┐
CreateSyntaxProvider(names + base names, syntax only) ─► Collect ─┐         ├─► TypeModel (equatable) ─► per-type source
referenced NinoType names (cached per reference set) ─────────────┴► candidates ┘          │
                                    (inherited NinoType)                                   │
                                                                         └─► formatter names ─► Collect ─► registry source
CompilationProvider.Select(ProjectFacts)  ──────────────────────────────────┘ (Nino.Core referenced, options, Unity)
```

* All symbol work happens inside the transform, which converts symbols into an **equatable model** made
  only of strings, primitives and `EquatableArray<T>`. Nothing downstream holds a symbol, a syntax node or
  the `Compilation`, so Roslyn's caching cuts off unchanged types.
* Types that inherit `[NinoType]` without the attribute are found from syntax alone: the closure of type
  names whose base list names a NinoType (declared in source, or in a referenced assembly that references
  Nino.Core). Only those candidates are bound, so types unrelated to Nino (every `MonoBehaviour` in a Unity
  project) cost a few microseconds of syntax per edit instead of a semantic lookup each. Source NinoTypes are
  named by `ForAttributeWithMetadataName`, so an aliased attribute (`using NT = Nino.Core.NinoTypeAttribute;`)
  still makes its subtypes candidates. The NinoTypes of a referenced assembly are collected once per assembly
  symbol, which unchanged references keep across edits and share between the projects of a solution.
* Roslyn re-runs every `[NinoType]` transform on every compilation change, since semantics may have
  changed anywhere. Models are therefore cached across compilations (`ModelCache`): a model is reused while
  every syntax tree it depends on (its type, bases, interfaces, containing types, member types and their
  fields, `typeof` attribute arguments) is still part of the compilation, and the references, file set and
  preprocessor symbols are unchanged. Edited files get new tree instances, so only the models that can see
  an edit are rebuilt. Entries whose trees left the compilation (types renamed or deleted while typing) are
  swept whenever a project's entry count doubles, so the cache stays bounded without a per-edit scan. Each type's direct dependencies are looked up once per compilation and shared by all
  models that reach it, so collecting them does not measurably slow a full build. With 1,100 NinoTypes in 1,200 files, an edit went from about 350 ms to about
  100 ms, close to the 60-100 ms a no-op generator with the same attribute lookup costs.
* Everything read from the whole compilation (whether it references Nino.Core, the registry's options, the
  referenced NinoTypes, whether NativeAOT's attributes exist) is one `ProjectFacts` step, which runs on every
  change; each part is selected from it, so what uses one part runs again only when that part changes.
* The top-level `NinoSerializer`/`NinoDeserializer` calls are bound again on every change too. What a call
  registers and how it is intercepted is kept by file and position (`ModelCache.GetOrBuildCall`) and reused
  while its tree, the method it binds to and the trees its type argument reaches are unchanged.
* The generator reports no diagnostics; the analyzer does (below). Models therefore hold no locations, and
  an edit that only moves code (a line inserted above a type) leaves every model equal and re-emits nothing.
* Roslyn matches the values of a step to the previous run's by position, not by value: a type inserted ahead
  of others (earlier in its file) shifts them, and they reach the output step again with equal models. Their
  files come from `EmittedFormatters`, which keeps recent ones by model value: the same text, not emitted
  again, and the same instance, so the compiler keeps the tree it parsed.
* Every output is registered with `RegisterImplementationSourceOutput`: user code never references generated
  code, so the IDE skips the emitters while typing and only builds run them.
* Facts shared between types (each type's serialized members, raw layout, whether its formatter can be
  delegated to, resolved formatters, type ids) are memoized per `Compilation` in `TypeFacts` and shared by every
  transform, analyzer and suppressor action on it, so a deep hierarchy collects each level's members once rather
  than once per subtype, and a member type such as `List<int>` is resolved once per compilation. `TypeFacts` also
  resolves the Nino.Core attribute types once (`NinoSymbols`), so attributes are compared by symbol everywhere.
* `TypeFacts` is the per-compilation entry point and holds focused parts: `NinoSymbols`, `Visibility` (what
  generated code can name and assign), `MemberCache` (each type's members, collected by `MemberCollector`),
  `RawRules` (which values are written as raw bytes) and `FormatterResolver`. `MemberCollector` finds a type's
  members and, for the analyzer, what is wrong with their annotations (NINO008-NINO013) in the same pass, so the
  rules are written once; model builds skip those checks. `ConstructionPlanner` picks constructors,
  `FormatterResolver` names formatters (from the `[assembly: NinoFormatter]` registrations, `FormatterRules`),
  `ModelBuilder` assembles the `TypeModel`, and the emitters (`Generation/Emit`) only read models: none of them reports diagnostics except through a `DiagnosticSink`.
* Each type is emitted into its own file. Editing a member of `Foo` re-emits only `Foo.g.cs`.
* The registry stage consumes only formatter names, so it only re-runs when types are added, removed or
  renamed.
* A separate syntax provider finds `NinoSerializer`/`NinoDeserializer` calls whose type argument is a
  built-in generic (e.g. `ImmutableArray<T>`, `List<MyStruct>`) and registers those closed formatters, so
  AOT runtimes never need `MakeGenericType` for them.
* The v3 generator combined everything with `CompilationProvider`, scanned every generic type syntax in
  the project and every `using` directive in every file, and re-emitted all types on every keystroke.

### Top-level calls and interceptors

The public `NinoSerializer` / `NinoDeserializer` generic methods are `AggressiveInlining` and take the formatter
from `NinoFormatterCache<T>` in the caller, where `T` is usually exact: a reference-type `T` would otherwise make
the method shared generic code, with a generic dictionary lookup for the formatter (serializing a small sealed
class: 22 ns → 10 ns). What remains is one virtual call through the cached formatter.

The boxed calls for a type known only at run time, `NinoSerializer.SerializeBoxed(value, type)` and
`NinoDeserializer.DeserializeBoxed(data, type)` (plus writer, buffer writer, reader and reuse overloads), take the
`Type` explicitly and write exactly what the generic call with `T` = `type` writes, null and subtypes included. They
look up a cached `BoxedFormatter<T>` per type (a dictionary lookup) that forwards to the generic call. There is no
`Serialize(object)`: it would compete with `Serialize<object>` in overload resolution and silently pick a different
format. So `Serialize(value)` with `value` declared as `object`, which in the v4 betas called `Serialize(object)` and
wrote the value as its runtime type, now binds `Serialize<object>`: it accepts only `[NinoType]` values, writes a type
id before the body, dispatches through the global registry (`ObjectFormatter`) and is read back with
`Deserialize<object>`. `SerializeBoxed(value, value.GetType())` writes what the beta overload wrote.

The boxed API is for types known only at run time, as a `Type`, that share no common `[NinoType]` base
(reflection-driven tooling over arbitrary types). RPC or message dispatch, plugin systems and editor tooling
should give their types a common `[NinoType]` base class or interface and call `Serialize<TBase>` /
`Deserialize<TBase>`: the generated switch dispatches on the type id, which is faster than the global registry,
and reuse deserialization and pooling work as usual (0 GC).

On compilers that load the generator's Roslyn 4.8 or 4.12 build (.NET 8 SDK and later), the generator also intercepts each such call whose type
argument is known at the call site and whose formatter it resolves (not raw values or arrays of raw elements,
which the regular calls already copy directly, and not types it can only reach through `CachedFormatter<T>`):
`NinoGen.Interceptors.g.cs` redirects it to `Nino.Core.NinoDirect`, which calls the formatter struct directly.
`NinoDeserializer.Deserialize<T>(ReadOnlySpan<byte>)` of a raw value is intercepted too, to
`NinoDirect.DeserializeRaw<T>`: one bounds check and one unaligned load with a single return, which the JIT keeps in
registers (the regular method's several returns go through a stack temp).
The bytes are the same: formatters are only declared at compile time, so intercepted and regular calls never
disagree, and `NinoDirect` calls the formatter with no check. Regular calls read the formatter once, on the first use
of `T`, into a `static readonly` field (`NinoTopFormatter<T>`), so Tier1 code knows its exact class and calls it
directly instead of virtually.
Measured on .NET 8 (small types, ns per call, regular → intercepted): deserializing a non-sealed class
15.5 → 11.6, a sealed class 29.5 → 24, a struct with a string 16.7 → 15.0.

* Packaging: the same sources are compiled three times, by `Nino.Generator` (Roslyn 4.3,
  `analyzers/dotnet/roslyn4.3/cs`, also what Unity uses), `Nino.Generator.Roslyn4.8` (defines `ROSLYN4_8`,
  `analyzers/dotnet/roslyn4.8/cs`) and `Nino.Generator.Roslyn4.12` (defines `ROSLYN4_8` and `ROSLYN4_12`,
  `analyzers/dotnet/roslyn4.12/cs`). NuGet picks the newest build the compiler can load: Roslyn 4.12+ (.NET 9 SDK
  and later) locates calls with the stable `[InterceptsLocation(version, data)]`; Roslyn 4.8-4.11 (.NET 8 SDK) only
  know the preview form, `[InterceptsLocation(filePath, line, column)]` (path of the syntax tree, 1-based position
  of the method name); older compilers keep the 4.3 build. Which build a project gets depends on its compiler, not
  its target framework: a netstandard library built with a current SDK is intercepted too (interceptors are a
  compile-time feature).
* Always on: interceptors need their namespace listed in `InterceptorsNamespaces` (Roslyn 4.12+) or
  `InterceptorsPreviewNamespaces` (Roslyn 4.8-4.11), and the package's `build/Nino.Generator.props` (and
  `buildTransitive`) appends `NinoGen` to both. The generator emits nothing when the namespace is not listed
  (a project that overwrites the property instead of appending to it), and the regular calls write the same
  bytes.
* C# 11+ projects get a file-local `InterceptsLocationAttribute` and interceptor class; older language versions
  get internal ones.
* Calls in generic code (`T` a type parameter) are left alone: the interceptor would be shared code too.
* **Fixed-size types.** A generated struct, or sealed class, whose members are all unmanaged portable values or
  such structs (not raw itself, not version tolerant, keyed, tracked or a collection, no custom formatters) is
  written in the same number of bytes for every value. Its formatter also implements `INinoFixedSizeFormatter<T>`
  (`FixedSize`, and `WriteFixed(value, ref byte destination)`, which stores each member at a constant offset), and
  `NinoSerializer.Serialize(T)` returning `byte[]` is intercepted to `NinoDirect.SerializeFixed<T, TFormatter>`:
  it allocates the array at its final size and writes into it, with no writer, scratch buffer, growth checks or
  final copy. A null sealed class is its null flag, `new byte[1]`. The regular call does the same through the
  cached formatter (`NinoFormatter<T>.FixedSize`, a constant check in Tier1 code), and
  `NinoSerializer.GetFixedSize<T>()` returns the size (raw values: their size; -1 when not fixed). The bytes are the
  regular writer's. Measured (x64, a 10-member struct of 30 bytes, `Serialize(T)` to `byte[]`): 216 → 108
  instructions per call. The general path still clears its writer twice (the prolog zeroes the address-exposed
  local, then the constructor's `this = default`): the constructor runs after a branch (the buffer type test or the
  scratch rent), outside the entry block where the JIT removes redundant zeroing, and storing each field instead
  measured no better (85/95 → 86/94 x64/ARM64 instructions).
* A sealed class with a small body that opens no class body (at most eight members, one or two of which call
  formatters) also gets a `__Top` twin of its formatter, whose body is inlined into the caller, so the reader or writer
  the top-level call creates stays in registers. Calls that create their reader or writer use it, for the type itself
  and for the elements of a built-in collection of it (`List<Small>`, `Small[]`, `Dictionary<int, Small>`): no class
  body is open around those either, and the twin writes the same bytes wherever none is. A collection then reads and
  writes each element inline in its loop, instead of calling the element's body.
* The top-level calls that create their writer (`Serialize(T)` to `byte[]`, to an `IBufferWriter<byte>` that is a
  `NinoArrayBufferWriter`) create it in a method of the formatter (`INinoTopFormatter<T>.Write(value, buffer)`) rather
  than taking the address of a writer created by the caller. For a twin whose body is not inlined
  (`__WriteTop`, a `NoInlining` method of the formatter) the writer then stays in registers through the whole body,
  and the JIT knows it starts empty (no region open, no reference context), so closing regions and releasing
  references fold away; a writer whose address is passed to a body is zeroed, stored and loaded back from memory
  around every call. The regular calls do the same through `NinoFormatter<T>.Write` (the cached formatter of a type
  with a twin is a `TopStructFormatter` registered by `NinoFormatterCache<T>.TryRegisterTop`). Measured (x64, a
  version-tolerant class of five members, two of them inherited, `Serialize(T)` to `byte[]`): 183 → 121
  instructions per call (with the merged region below). netstandard2.0 (span and position) takes the same route.
  The top-level reads of a new value (`Deserialize<T>(data)`) do the same through `INinoTopFormatter<T>.Read(data)`
  (`NinoDirect.DeserializeTop`, `NinoFormatter<T>.Read`): a twin whose body is not inlined reads in `__ReadTop`,
  which creates the reader and calls the body that reads a new instance, an inlinable method (`__ReadBodyInline`, or
  `__ReadBodyFreshInline`) that the out-of-line body also calls on its local copy of the reader. Measured (x64, the
  same class, `Deserialize<T>(byte[])`): 220 → 139 instructions per call. Pooled types keep the regular route
  (their new value comes from the pool first).
* A version-tolerant body whose first members are raw values (written by the body itself, not by a base's
  `WriteMembers`) reserves the region's length and those members together (`Writer.BeginRegion(size, out
  lengthPosition)`): one capacity check instead of two.
* Body formatters report whether they open class bodies and can be read later in one `Traits` property
  (`NinoBody.Nests | NinoBody.Deferrable`, constants the JIT folds), not two, and the registration of each type
  (`TryRegister`/`TryRegisterTop`/`TryRegisterFixed` and `NinoPolymorphic.Register`) is emitted straight into the
  assembly's registry instead of a `__Register()` method per formatter called from it: fewer members per type for
  the compiler to bind and emit (see `v4-benchmarks.md`, compile time).

### The API generated code calls

Generated formatters call Nino.Core directly: `Writer`/`Reader`, the built-in formatter structs, `NinoObject`
(class headers and subtype dispatch), `NinoDirect` (intercepted top-level calls), `NinoPolymorphic`, `NinoRegistry`,
`NinoFormatterCache`, `NinoActivator`, `NinoRecycler` and `NinoUnsafe`. The members that exist only for generated code
(`NinoObject`, `NinoDirect`, `NinoUnsafe`, `NinoRegistry.Add`, `INinoRegistry`, `INinoBodyFormatter<T>`,
`INinoTopFormatter<T>`, `INinoRawBodyFormatter<T>`, `NinoBody`,
`INinoRawFormatter`, `NinoPolymorphic<TBase>`, `NinoPolymorphic.Register<TSub, TFormatter>`/`RegisterTracked`,
`NinoFormatterCache<T>.TryRegister`/`TryRegisterTop`/`TryRegisterFixed`, the region, reference and unchecked value members of
`Writer`/`Reader`, `[NinoGenerated]`, the pool's per-thread stack) are hidden from IntelliSense
(`[EditorBrowsable(Never)]`); they stay public because generated code lives in the user's assembly. There is deliberately no forwarding facade in front of them: every call it added is a
real call under Mono's interpreter and in HybridCLR hot-update code, which do not inline.

### Sources shared with the runtime

Rules that must agree between compile time and run time live once, in Nino.Core, and are compiled into the
generator as well (linked files, `NINO_GENERATOR` switches their namespace):

* `Internal/NinoHash.cs`: the FNV-1a hash of type ids and keyed member ids.
* `IO/NinoWire.cs`: the wire constants generated code writes (null type id, length and key sizes...).
* `Internal/NinoLayoutCore.cs`: the struct layout algorithm (leaves, alignment caps, `Pack`, `Size`, explicit
  offsets). The runtime runs it over `System.Type` to get the wire layout and this runtime's memory layout; the
  generator runs it over `ITypeSymbol` under the 64-bit and 32-bit models to decide whether a struct's memory is
  the same everywhere (and so can be written inline as raw bytes). Each side supplies a small view of its type
  system (`INinoLayoutView<TType>`).
* `Internal/NinoFormatterBinding.cs`: how a formatter rule binds to a type (unify the rule's pattern with the
  type or one of its supertypes, check the formatter's type parameter constraints, substitute). The runtime
  resolver runs it over `System.Type`, the generator over `ITypeSymbol` (`INinoTypeSystem<TType>`), so a type
  resolves to the same formatter whichever side picks it.

### Source layout

* Nino.Core: `NinoSerializer`/`NinoDeserializer` and `NinoSettings` at the root; `Attributes/` (everything users
  put on types), `IO/` (`Writer`, `Reader`, buffers, wire constants), `Formatters/` (the formatter interface,
  cache, resolver and every built-in formatter), `Polymorphism/` (type ids, registry, subtype dispatch,
  reference tables), `Pooling/` (`NinoPool`, `NinoRecycler`, `NinoActivator`) and `Internal/` (unsafe helpers,
  layout, thread caches, the shared sources above). Namespaces stay flat (`Nino.Core`) so user code needs one
  `using`.
* Nino.Generator: `Analysis/` (analyzer, suppressor, diagnostics), `Common/` (equatable collections, names and
  `[NinoType]` lookup shared by the analyzer, generator and code fixes), `Generation/` (symbols to models) and
  `Generation/Emit/` (models to source).

### Analyzer, suppressor and code fixes

* `NinoAnalyzer` reports every diagnostic ([docs/diagnostics.md](diagnostics.md)): attribute misuse, and
  whatever building a type's model finds, through the same `ModelBuilder` the generator uses with a
  `DiagnosticSink`. It skips generated code, does nothing in compilations without Nino.Core, compares
  attributes by symbol (the `TypeFacts` of the compilation, shared with model builds), and runs one symbol action
  per type (not one per partial declaration). Diagnostics appear as you type and can be configured in `.editorconfig`.
* `NinoSuppressor` hides "never used / never assigned / make readonly" warnings (CS0169, CS0414, CS0649,
  IDE0044, IDE0051, IDE0052) on private members Nino serializes: on .NET 8+ they are reached through
  `[UnsafeAccessor]`, which the compiler cannot see.
* Code fixes live in their own assembly, since they need `Microsoft.CodeAnalysis.Workspaces`, which the
  command-line compiler and Unity do not load. The provider maps each diagnostic id to its fixes, edits through
  `DocumentEditor`, leaves formatting to the code action cleanup and supports Fix All. It compiles in the
  analyzer's `Analysis/NinoDiagnostics.cs`, `Common/NinoNames.cs` and `Common/PoolHooks.cs`, so ids and pool hook names have one definition.
* Descriptors are static and shared (`NinoDiagnostics`), with help links and release tracking
  (`AnalyzerReleases.*.md`). Analyzer projects enforce ordinal string comparisons (CA1309/CA1310).

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
* `[assembly: NinoTypeLayout(typeof(T), format, header, trackReferences, raw, typeId, "members", Deferrable = ...)]`
  publishes what the formatter writes (issue #197): whether a body nested past `NinoDepth.Inline` is deferred (the
  formatter's `NinoBody.Deferrable` trait, which readers in other languages need), and each member as
  `name:type:encoding` (keyed members add `:key=0x...`).
  The encoding describes the bytes, not the formatter class (`WireEncoding`, computed by `FormatterResolver` next
  to each formatter): `string`, `utf8`, `object`, `type(T)` for a `[NinoType]`, `nullable(E)`, `seq(E)`,
  `map(K,V)`, `pair(K,V)`, `tuple(E,...)`, `grid(rank,E)`, a few named BCL encodings (`datetimeoffset`, `bigint`,
  ...) and `formatter(F)` for a custom or third-party formatter, so a tool such as the TypeScript generator reads
  and writes the bytes without knowing Nino.Core's formatters. A value copied as raw memory is its canonical layout
  from `NinoLayoutCore`: `raw(n)` for a primitive, `raw(n){name:type@offset:encoding,...}` for a struct
  (issue #202), so a field, `Pack`, `Size` or explicit offset anywhere inside a raw value shows, `[NinoType]` or
  not. It comes from the same
  `TypeModel` as the formatter, so it changes exactly when the bytes do, and is the same in every target framework
  (`LayoutMetadataTests`).
* The model leaves a name empty where it does not apply (a base, collection or dispatch root formatter, a
  constructor). The emitters take such a name through `TypeModel.Need`, which throws when it is empty: the rule that
  chose the branch is wrong. `NinoGenerator` turns any exception while emitting a type into one `#error` naming the
  type and the cause, instead of a generator warning and a missing formatter, or code that does not compile.
* `ShapeMatrixTests` generates every combination of the ways a hierarchy can be declared (base kind, type parameters,
  nesting, format, reference tracking, a type between base and subtypes, subtypes in the base's assembly or in
  another one that references it as metadata) with every kind of subtype, compiles it with the generator and
  round-trips each subtype through each type above it, `object` and the boxed API. A feature that adds a way to declare
  a type adds a dimension there.
* Preprocessor-dependent choices (`WEAK_VERSION_TOLERANCE`, `NET8_0_OR_GREATER`) are resolved at
  generation time from the compilation's parse options, so generated code contains no `#if` branches.

For a hierarchy of 220 types (20 roots × depth 8), v3 generated about 911,000 lines (47 MB) because every
polymorphic base inlined the full body of every subtype twice. v4 emits each body once.
