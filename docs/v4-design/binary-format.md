# Binary format

Part of [the v4 architecture](../v4-design.md).

The normative, byte-exact specification of this format, for implementations in other languages, is [spec/wire-format.md](../spec/wire-format.md).

All multi-byte values are **little-endian** and written unaligned (`Unsafe.WriteUnaligned`). Big-endian
hosts are not supported: `Writer`/`Reader` throw `PlatformNotSupportedException`.

| Value | Encoding |
| --- | --- |
| Unmanaged value (`int`, `Guid`, unmanaged structs, enums) | raw bytes, `Unsafe.SizeOf<T>()` |
| `bool` | 1 byte (0/1) |
| `T?` (nullable value type) | a presence byte, then the value if present |
| Collection / array length | `int32`; `-1` means `null` |
| `string` (default) | header (1, 3 or 5 bytes, minimal: the value itself up to 127, else `0x80` and a `u16`, else `0x81` and a `u32`): `0` = null; `2n + 1` (odd, `1` = empty) then `n` Latin-1 bytes when every char is at most U+00FF; else `2n` then `n` UTF-16 code units (see below) |
| `string` with `[NinoUtf8]` | char count plus one as a header (`0` = null, `1` = empty; nothing follows either), then the byte count as a header and the UTF-8 bytes (see below) |
| Array / list of unmanaged `T` | length, then one block copy of the elements |
| `T?[]` / `List<T?>` of raw `T` | length, then each element in the canonical `Nullable<T>` layout (fixed stride: flag, padding, value); an `IList<T?>`/`IEnumerable<T?>` member writes each element as a single `T?` instead (see `docs/pitfalls.md`) |
| `NinoNullable<T>`, its arrays and lists | exactly the bytes of `T?`, `T?[]` and `List<T?>` (its memory is that stride wherever `T` is as large as its alignment, so the collections are one block copy) |
| Other collections | length, then each element |
| Dictionary | length, then key/value pairs |
| Multi-dimensional array `T[,..]` | `int32` rank-lengths (`-1` for null in the first slot), then elements in row-major order |

A default string is written in one byte a char when it can be: English, digits, identifiers, JSON keys and Western
European text are all Latin-1, and take half the bytes of UTF-16, while any other text keeps UTF-16 (never UTF-8,
whose decoding is a branchy pass and whose length differs from the char count). Writing narrows the chars with SIMD
and falls back to UTF-16 at the first char above U+00FF; reading widens the bytes with SIMD straight into the new
string. A `StringBuilder` is always written with UTF-16 chars (even header; an empty builder is the empty string),
read by the same reader: it cannot take chars without a copy, so widening would be a second pass on every read.

The header's first byte gives its size, as MessagePack's string markers do, so that short strings, the common case,
cost one byte of header: up to 63 chars in either form (three bytes up to 32,767, five above). Both forms of a string of `n` chars have headers of the same size, so the writer places
the chars right after a header whose size it knows from the length alone and narrows them there; the reader decodes a
one-byte header with one compare inline, and a longer one with one load at a constant offset after the first byte
(no loop over continuation bits, as LEB128 would need). The 1,000 mixed strings of the runtime benchmarks (ASCII,
Latin-1 and Cyrillic, CJK and emoji text in turn, 8 to 17 chars) took 23,848 bytes with a fixed `int32` header and
take 20,848 with this header (`[NinoUtf8]`: 27,884 and 21,884), against 20,883 for MessagePack (rmp-serde) and
21,880 for bincode.

A `[NinoUtf8]` string is written as:

* **char count plus one** `c + 1`, as a header, `c` its length in UTF-16 code units (`string.Length`). `0` is null and `1`
  the empty string; nothing follows either.
* if `c > 0`: the **byte count** `b`, as a header, then exactly `b` bytes, the string's UTF-8 encoding (an unpaired surrogate is
  written as U+FFFD, one code unit, so `c` is also the length of the string read back). `c` and `b` are at most
  2^31 - 1.

A UTF-16 code unit takes 1 to 3 bytes (a surrogate pair 4 bytes for 2 units), so every valid string has
`c <= b <= 3 * c`. For example `"é😀"` (3 code units: U+00E9 and a surrogate pair) is
`04` `06` `C3 A9 F0 9F 98 80`, and `null` is `00`.

The reader reads both counts (one byte each, inline, for up to 126 chars and 127 bytes), checks that `b` bytes
remain and that `c <= b` (which bounds the allocation by the data) before allocating the string at `c` chars, then
decodes exactly those `b` bytes into it in one pass and moves past them; it never reads beyond them. The data is rejected (`NinoDataException`) when it ends early,
when a count is out of range (`b > 3 * c` decodes to more chars than the string holds), or when the `b` bytes are not
strict UTF-8 (including overlong forms, encoded surrogates and a sequence cut by the end of the range) or do not
decode to exactly `c` chars. The char count lets the reader allocate before decoding, the byte count lets it decode
the bytes in a single call without finding their end first.

## Objects

| Declared type | Header |
| --- | --- |
| `struct` | none |
| `sealed class` | 1 byte: `0` = null, `1` = instance |
| non-sealed class, abstract class, interface | `int32` type id: `0` = null, otherwise the runtime type's id |

The **type id** is a stable 32-bit hash (FNV-1a) of the type's name in runtime form
(`Namespace.Outer+Inner`, generic arguments in angle brackets). `[NinoFormerName("Old.Name")]` substitutes
the hashed name, so types can be renamed or moved without breaking existing data. The hash is implemented
once (`Nino.Core/Internal/Metadata/NinoHash.cs`) and that file is also compiled into the generator, so ids computed at
compile time and at run time cannot diverge.

After the header comes the **body**: the members in declaration order (or `[NinoMember(n)]` order), base-class
members first. Consecutive unmanaged members are written as one block
(a single bounds check and reservation for the whole block).

The serialized members are the type's fields and properties (its own and its `[NinoType]` bases'):

* Public ones, and non-public ones with `includeNonPublicMembers` (private members of a base class are never
  part of a subtype). Properties need a getter and a setter (`set` or `init`).
* Get-only auto-properties (`public int Id { get; }`) only when a constructor (or `[NinoConstructor]` factory)
  takes them: a parameter of the same name, ignoring case, or a name the `[NinoConstructor]` lists. Nothing
  else can restore them, so otherwise they are skipped.
* Explicit interface implementations (`int IHasId.Id { get; set; }`) never, even with
  `includeNonPublicMembers`: they have no name generated code can use on the type. A `[NinoType]` interface
  serializes its own members through the implementing type's members of the same name.
* An `override` is the member it overrides, collected once with the base. A member hiding a base member with
  `new` is another member: both are serialized, the base one first. Generated code reaches the hidden one
  through a cast (`((Base)value).X`), or `[UnsafeAccessor]` when it is not public; without either (a
  non-public hidden member of a generic type before .NET 9, or before .NET 8) it is skipped with warning NINO022. In keyed
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

## Collections

User collection types work without attributes: a class derived from `List<T>`, `HashSet<T>`, `Queue<T>`,
`Stack<T>` or `Dictionary<TKey, TValue>`, or implementing `IDictionary<TKey, TValue>`,
`IProducerConsumerCollection<T>` or `ICollection<T>`, is written exactly like the collection it extends (a
`List<int>` subclass is one block copy, like `List<int>`). A `[NinoType]` collection writes its elements first and
then its own serialized members, so a collection with extra state (for example a synchronized list) keeps both.
Reuse deserialization reuses the existing collection instance (and so its comparer).

### User collections

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
  reader's comparer, is a `NinoDataException` when a dictionary or `HashSet<T>` is filled from empty;
  merging into a non-empty dictionary in place does not look for repeated keys (the last value wins). A key that
  finds more than `NinoSettings.MaxHashCollisions` keys in its hash bucket is a `NinoDataException` too (see
  [Untrusted input](runtime.md#untrusted-input)).
* The declared type decides what is created: a `List<int>` member holding a `ListComponent<int>` is read back as a
  `List<int>` (reuse deserialization keeps the `ListComponent<int>` it holds). Collections are values: the same
  instance in two members is written twice and read as two instances (`trackReferences` tracks `[NinoType]`
  objects, not collections).

## Version tolerance

A type marked `[NinoType(format: NinoFormat.VersionTolerant)]` writes its body as one region: one `int32` byte
length, then the members of its whole hierarchy (each `[NinoType]` class from the root down to the type itself), root
first:

```
header | int32 length | members of every level..., root first
```

On read the region is sliced once. Members missing from it (old data: the region ends early) keep their current
value when reading in place, or their default, and trailing bytes (new data read by old code) are skipped. So
members can be appended to the **most derived type**, and each nested tolerant object delimits itself, so this holds
at any depth.

* One length per object, whatever the depth of the hierarchy (earlier v4 builds wrote one length per level:
  `header | { int32 levelLength | level members... } per level`). Example, a sealed `Leaf : Base` with
  `Base { int A; }` and `Leaf { int B; }`, `A = 1, B = 7` (after the presence byte `01`):

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

## Keyed members

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

## Reference tracking

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

## Deep nesting

Class bodies are written and read by nested calls, so a recursive type (a linked list, a tree) would use stack in
proportion to the depth of its data: a long list overflows the stack on write, and a payload that nests one level
every few bytes overflows it on read, which ends the process. Instead, the writer and the reader count the class
bodies open (`Depth`). A struct cannot contain itself, but it can hold itself through a collection, a nullable, a
`Lazy` or an interface, and a collection type can hold itself (`class Tree : List<Tree>`): no class body counts
those, so their bodies call `EnterStruct`/`ExitStruct` on the reader and on the writer (generated for such structs,
`NestingFormatter` for such collections), which count them with the class bodies against `NinoSettings.MaxDepth`
and check the stack every 16 levels. Reading too deep is a `NinoDataException`; writing too deep (or a collection
that contains itself) an `InvalidOperationException`, before the stack runs out. A body that would open
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
  deferred: past 128 they nest inline, and past `NinoSettings.MaxDepth` (512) open bodies, the counted struct bodies
  included, writing or reading throws.
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
