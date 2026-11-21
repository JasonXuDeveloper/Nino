# Runtime (`Nino.Core`)

Part of [the v4 architecture](../v4-design.md).

## Writer

`Writer` is a `ref struct` that writes into a contiguous `byte[]`, rented from `ArrayPool<byte>.Shared`
and owned by a `NinoArrayBufferWriter`.

* The hot path (`Write<T>`, `Reserve`) is a single length check plus an unaligned store. There is no
  interface call per write. Growth is a non-inlined cold path.
* Because the buffer is contiguous, back-patching (body lengths of version-tolerant types, counts of
  `IEnumerable<T>`) is always possible.
* Serializing to a `NinoArrayBufferWriter` writes in place. Into a foreign `IBufferWriter<byte>`, values whose
  size is known before writing (raw values, raw arrays and lists, fixed-size values; raw array slices and
  single-segment sequences in the netstandard2.1 and modern .NET builds; zero-based raw multidimensional arrays on
  modern .NET) are written in place too, into one `GetSpan` of their size; everything else goes into a thread-local
  scratch buffer and is copied once at the end.
* Serializing to a `Stream` (`Serialize(value, stream)`, `SerializeAsync`) writes into the same scratch buffer, then
  hands its array to the stream in one write, with no framing. An asynchronous write still pending when the call
  returns takes the scratch buffer's array with it (back to the pool when the write completes), and the scratch
  buffer continues with another of the same size: nothing is copied, and other calls on the thread are not held up.
  Deserializing from a `Stream` consumes it to its end before decoding, into an array rented from the shared pool
  (read in place instead: a `MemoryStream` whose buffer is exposed, an `UnmanagedMemoryStream` of exactly that type
  and a `MemoryMappedViewStream`, whose pointer, length and position are their own; a derived stream may override
  them and is copied) and then reads that span: every check of the data is the span
  overload's. The array grows by doubling as bytes arrive, and a seekable stream's `Length` sizes the first one only
  up to 1 MiB, so a stream makes Nino hold at most about twice what it delivered. The async forms return `ValueTask`
  in every build. All of this is `NinoStreams`.
* A UTF-8 string of at most 32 chars (`[NinoUtf8]`, .NET 8+) is narrowed or widened by Nino's own code (overlapping
  128-bit loads, then the Latin-1 widen) when every char is ASCII, and only otherwise by the runtime's `Utf8`
  transcoder; the reader checks each byte below 0x80 and rejects the rest as the transcoder does. The bytes are the
  same. The runtime's ReadyToRun CoreLib transcoder uses SSE encodings, and after Nino's own 256-bit code (an inlined
  UTF-16 string kernel) each call paid an AVX-SSE transition until the method tiered up: a class of a UTF-16 and a
  UTF-8 string took 177 ns to write at TC=0 against 25 ns with the members swapped, and a class of seven members with
  both 179 ns, 26 ns with Nino's own path (a word-at-a-time scalar loop there was slower than the transcoder at
  Tier1).

## Reader

`Reader` is a `ref struct` over a `ReadOnlySpan<byte>` with an integer position. Each read is one
unsigned bounds check plus an unaligned load. Every length read from the payload is validated against the
remaining bytes before anything is allocated, so malformed or hostile input cannot trigger huge allocations.

* A `ReadOnlySequence<byte>` (`Deserialize<T>(in ReadOnlySequence<byte>)`, also by `ref`), what a `PipeReader` or
  gRPC hands over, is read through the span overload: one segment in place, several copied once out of line
  (`NinoSequences`), onto the stack up to 1,024 bytes (`StackBytes`), else into a pooled array; a `byte[]` read
  from two segments (outside untrusted mode) is copied straight into the array it returns. Every check of the data
  is the span overload's.

## Formatters

```csharp
public interface INinoFormatter<T> : INinoFormatter
{
    void Serialize(T value, ref Writer writer);
    void Deserialize(ref Reader reader, ref T value);

    // Capabilities, with defaults that report none (hidden from IntelliSense):
    int FixedSize => -1;                                      // every value takes this many bytes
    void WriteFixed(in T value, ref byte destination) => ...; // throws: called only when FixedSize is not -1
    NinoBlockMap BlockMap() => null;                          // collections move values as blocks
    void ReadBlock(ref T value, ref byte source) => ...;      // throws: called only when BlockMap is not null
    // internal: SlotStride and the slot members of a raw T? (UnmanagedNullableFormatter); Size, WriteSized and
    // TryWriteExternal of collection formatters (top-level calls write into an array of the exact size, or straight
    // into a caller's IBufferWriter<byte>)
}

public interface INinoFormatter                               // what does not depend on T (hidden)
{
    int BodyTraits => NinoBodyTraits.Nests;                   // a body: opens class bodies? deferrable? dispatches?
    bool HasRunLoop => false;                                 // a polymorphic base's values in one loop over a run
    void WriteRun(NinoRun run, ref Writer writer) => ...;     // throws: only when HasRunLoop
    int PrefetchBytes => 0;                                   // long collections of a class prefetch its objects
    bool HasMemberPrefetch => false;                          // ... and the objects its members point to
    int PrefetchMembers(object value) => ...;                 // throws: only when HasMemberPrefetch
    // internal: HasSizedWrite, HasExternalWrite (the queries of Size, TryWriteExternal); HasCursor, WriteStrings,
    // ReadStrings (a run of pairs holding strings through the string run kernels)
}
```

Every contiguous collection (arrays, lists, spans, `Memory<T>`, `ArraySegment<T>`, `ImmutableArray<T>`, each half of a
`Queue<T>`'s ring, `Stack<T>`, and the entries of `HashSet<T>` and `Dictionary<TKey, TValue>`) writes its elements as a
run (`NinoRun`): the first value, the count, the stride between entries and how a free entry is told apart (a set and a
dictionary that removed elements; the rule is found at startup). One write of a run of any stride (`NinoRuns.Write`),
over `<T, TFormatter>` only, takes every one of them: raw elements and blocks as one block (a run of entries too, where
none is free), slots through the slot kernels, strings and pairs holding them through the string run kernels (below),
others value by value through the one loop of values (`NinoRuns.WriteLoop`: the step, the in-use test, the key and the
value, each tested against the constants its caller passes, so a run in a row compiles as a span's); a polymorphic
base's values through its generated `WriteRun`, the compare chain of its subtypes inline in a loop whose steps, in-use test, key write and deep
path are Nino.Core's. A root that is a class of its own tests its own type first in that chain, so its own values never
reach the registry. A class below the root has no loop of its own: its collections go value by value, its own type
tested inline and the root's dispatch after it, which measured faster than a loop per class (#355). A collection that is not one run is gathered a
chunk at a time into the same loops; a tree of class elements is walked over a struct holding one reference
(`NinoTreeWalk.Reference`), so the walk is not code shared over every class, which would take each element through a
lookup and an indirect call. A ring that wraps (a `Queue<T>`'s) is the same write in two segments, one reservation for both
where the elements have a fixed size; the run itself stays one segment, since every loop over it would read a second
segment's fields. A pair (a `KeyValuePair`, a value tuple, a generic struct) is one element whose formatter composes
its items (block, string items, dispatch bit), never a loop of its own. What a composite of any number of items does on
a run is written once (`NinoItems`), and a `KeyValuePair`, a value tuple of two to seven and a dictionary's entry forward
to it with their items' offsets: a run with a string among the items through the string run kernels, and a polymorphic second item
through its base's `WriteRun`: the run of those items (`NinoRun.Items`, the one way a run of items is made), each first
item at an offset from its second (`NinoRun.Key`) and written by the loop, the one place an item is written apart from
the other. The member is not generic, so one serves every collection and every
key written as its bytes or a string; any other key goes value by value (#355). Beside a string, the string run kernels
move a raw value, a block read as one, or a slot's value (a raw `T?` in its single value's bytes, a presence byte then
the value: `Dictionary<string, int?>`), each an item of the element (`NinoCursor.Item`). Each item's run item is
picked by JIT-time constants once per run and composed into one element (the first item at its offset, then each next
after it: `NinoItemAt`, `NinoItemChain`), so the kernel is instantiated over the items alone, never as code shared
over a class. A tuple of three or more walks a chain of its items (`NinoItem`, each node picking its own item: one
call per node and run where the code is shared over a class item); a pair picks both items in static methods
(`NinoItems.WritePair`), which shared code inlines whole, where a chain's node would be a call. Recycling a dropped
composite is each item's `NinoRecycler.Recycle`.

A collection's storage element is the element of its run, whatever else it holds. A `HashSet<T>`'s and a
`Dictionary<TKey, TValue>`'s entries are a run of the entries themselves, the stride the entry's size, each written by a
storage element formatter (`NinoSetEntryFormatter`, `NinoMapEntryFormatter`): the entry's wire bytes are one item (a set's value) or two (a
dictionary's key and value) at offsets measured on this runtime (CoreCLR moves references first: a
`Dictionary<string, int>`'s entry has its key at 0, the hash code at 8, the link at 12, the value at 16), and every
other byte (the hash code, the link, padding) is skipped. The formatter composes its items as a value tuple does, so
where they are raw or blocks the entry is a block whose map (`NinoBlockMap`) names only the items' bytes, and the
existing block kernels (`NinoBlockPlan`: AVX-512 groups, 16-byte lanes, 64-byte permutes, the netstandard2.1 build's
registered kernels) pack a run of entries into the wire's bytes and scatter the data back into new entries, with no code
of their own for entries; strings among the items are moved by the string run kernels at their offsets in the entry;
otherwise it is the items' run loop or formatter, through the entry. The formatter reads the entry's fields as fields,
never through a struct type parameter that reaches them: in code shared over a class (an entry holding a reference),
each call of such a struct is an indirect call the JIT does not inline, so one formatter serves entries with and
without references alike. A collection's own code is only its top layer: reaching the storage, the startup check that
measures where the fields are, and what it does after a fill (a table links its entries). Reads fill the same runs,
one loop per element kind: a run of values in a row (an array, the backing array of a list, whose count is set once it
is filled, a ring from its start, a span) through the copy, slot and block kernels or value by value; and the entries of
a new `HashSet<T>` or `Dictionary<TKey, TValue>` through one read for both (`NinoEntries`): a block scattered into the
entries by the block kernels (every byte written, the hash code and link 0), the string run kernels or value by value
into their places, then linked (below); into a collection that is not new, each entry read apart and added through one
store for both tables (`NinoTableAdds`), by the gather's read loops.

A collection that is not one contiguous run (a `SortedSet<T>` or `SortedDictionary<TKey, TValue>` tree, a
`LinkedList<T>` ring, a concurrent dictionary or queue, the immutable lists, sets, dictionaries, queues and stacks of
`Nino.Immutable`, a grouping, any other `ICollection<T>` or `IEnumerable<T>`) is gathered: its elements are walked into a
chunk of Nino's own per-thread buffer (`NinoGather`, 16 KB a chunk, pinned and 64-byte aligned for values without
references on .NET 8+, a few kept per element type for nesting, cleared of references when given back), and each chunk
is written by the same loop per element kind as an array of them; a collection never needs a buffer of its own size. A
collection says only how it is walked (an `INinoGatherSource<T>`, a struct, so every call is direct); the walk goes
through its fields where a startup check proved them, else through its own struct enumerator:
- every binary tree through one in-order walk (`NinoTreeWalk`): a node class with its two children at byte offsets, its
  subtrees ending at null or at the tree's empty node, walked recursively (the path to a node lives in the frames) and
  saved on a stack from the gather's buffer only when a chunk is full, to resume from there. A `SortedSet<T>`'s red-black tree (also under a `SortedDictionary`), an `ImmutableList<T>`'s and an
  `ImmutableSortedSet<T>`'s AVL trees take the element at an offset of each node (`NinoTreeItems<T>`); an
  `ImmutableSortedDictionary` makes each node's key and value its pair; an `ImmutableHashSet<T>` or
  `ImmutableDictionary`'s tree of hash buckets yields each bucket's first item and then the further items of keys that
  share its hash, an `ImmutableList`'s tree walked the same way, in the enumerator's order;
- a `LinkedList<T>`'s ring of nodes, a `ConcurrentDictionary`'s buckets along their chains (`ConcurrentTables.Pairs`, in
  its enumerator's order), an `ImmutableQueue<T>`'s two stacks of nodes (its front, then its back as the queue keeps it
  reversed; its struct enumerator for a back not reversed yet); an `ImmutableStack<T>`'s nodes, whose only walk is from
  its top while it is written bottom to top, walked once into chunks written from the last (`WriteReversed`);
- a collection that fits one chunk copied in by its `CopyTo`, else its enumerator a chunk at a time.

Values whose memory is their wire form are walked straight into the writer's buffer, a known count in one reservation,
an unknown one (a queue, a concurrent dictionary) a step at a time into its free space with the count patched after. The
walk checks the count and the collection's version against what it found, so a collection changed during the write
throws. `Nino.Immutable` reaches the gather through `CompilerServices` (`NinoGathered`, `INinoGatherSource<T>`,
`NinoTreeWalk`, `INinoTreeNodes<T>`, `NinoTreeItems<T>`, hidden from IntelliSense): it says how its trees are walked, Nino.Core how each
element is written. Reads fill a chunk through
the read loop and hand it over: a `LinkedList<T>` links each chunk's nodes and sets its fields once at the end, a
`SortedList<TKey, TValue>` splits each chunk into its two arrays (pairs whose memory is their wire form straight out of
the data, with no chunk between) and publishes its size once the keys are found in order under its comparer, the one
read for every pair (its write is the gather's zip, the interleave kernels taking pairs whose memory is their wire form
straight into the writer's buffer; the Arm64 fused split and order check of integer pairs and the `int` order check
are layers over the read, #350), a `PriorityQueue<TElement, TPriority>` (.NET 10) reads into its heap and publishes it once every
priority is at least its parent's, for any element and priority (#350); a collection that takes elements one at a time
(`Add`, `TryAdd`, `Push`, `Enqueue`) gets each chunk's, where the read loop moves several at once (raw values, slots,
blocks, strings), else one at a time. Until published, a collection holds none of what a rejected read filled. The
trees (`SortedSet<T>`, `SortedDictionary<TKey, TValue>`, the immutable collections) and the concurrent stack, queue and
bag, which are built in one go from all their elements, read the whole count (bounded by the bytes left) into an array
first.

Strings are an element kind of the bottom layer, as raw values are (`NinoStringRuns`, #355): a run of strings in the
default encoding or UTF-8, or of pairs with a string among their items and a string or a value of a fixed size (raw, or
a block read as one) as the other, each item at its offset in the element, is moved by kernels of its own that the top
layers call once per run under a constant test of the formatter's type (`NinoKind<TFormatter>.Strings`). The write
transcodes each string with the SIMD kernels into the free part of the buffer after one check of its longest form
against the bytes left, growing the buffer only where one does not fit (a first pass that summed the run's longest forms
to reserve them once took longer); the read checks each string's lengths against the bytes left (a header out of range
or not minimal, a length past the data, a UTF-8 string of more chars than bytes) before it allocates the string, so a
rejected read has allocated in proportion to the bytes it read (the run itself was sized by the data: a byte a string at
least), then transcodes it into its place (an unchanged one kept, `ReadAt`) or hands the element to a store (a
dictionary's adds, every element read into the one entry the store hands on, whose fields stay in its home on the stack;
a set that guards its own buckets adds each string in a loop of its own). A first pass that measured every length of
the run before reading any took longer (a chain of dependent loads per string). Their items (`NinoUtf16Item`, `NinoUtf8Item`, `NinoValueItem`, composed by
`NinoItems`) and stores are not generic over a class, so the kernels are never shared code: a collection of strings
is compiled once for every class (`__Canon`), and there a method of a struct type parameter is an indirect call the JIT
does not inline and a static of a generic class a helper call, where the kernels pay none per string. A pair kernel is
generic over its items only, so a `Dictionary<string, int>`'s is exact, a `Dictionary<string, string>`'s not generic at
all; a dictionary's adds go through a store generic over the side that is not a string. Every collection of strings
takes them (arrays, lists, gathered chunks, sets with free entries skipped, dictionaries, `KeyValuePair` runs through
their formatter's `WriteStrings`/`ReadStrings`, the pairs of a sorted or concurrent dictionary), with the per-pair
checks of the per-value loops: a null or repeated key, hash flooding through `HashTables`. A dictionary read again
merges in its own layer, each key read before its value's place is known (`NinoCursor.ReadAt`, which reads it through
the same items: one cursor read per item, #347).

Classes whose body is flat take a third kind of their own (`NinoFlatRuns`, #355). The generator marks a body flat
(`FlatRules`) when it is a positional, non-generic class without reference tracking, pooling, callbacks or a factory,
made by a constructor without arguments, with at least one string and at most twelve items (each is a level of
inlining, and the JIT inlines at most 20 deep), and every member a field generated code reads and assigns directly, of
a raw type, a bool or a string. Such a formatter writes no member-by-member body: it names its members once, as a
chain of items nested in type arguments (`NinoFlat<TLayout, TBody>` in `Nino.Core.CompilerServices`: `NinoFlatBlock`,
`NinoFlatValue`, `NinoFlatStruct`, `NinoFlatBool`, `NinoFlatString`, `NinoFlatUtf8`, ended by `NinoFlatEnd`), each
item reaching its field through a one-line accessor struct (`INinoFlatMember.Ref(object)`, a constant offset into the
object once inlined, and a literal `Size`) and the layout struct (`INinoFlatLayout`: create, exact type, type id).
`HasFlat` (read once per formatter into `NinoKind<TFormatter>.Flat`) sends a run of at least 16 such elements
(`NinoFlatRuns.MinRun`: below it the run's fixed cost, about 30 ns, outweighs what it saves) to
`WriteFlat`/`ReadFlat`, which run the string kernels' write loop and a read loop over the chain: one check of an
element's longest form against the bytes left, then every member straight into the buffer; a read goes into the object
an element holds (of exactly the type, its strings kept where unchanged) or a new one. A subtype, another type id, or
a run nested past `NinoDepth.Inline` goes through the formatter, out of line (an element holding an object once left
the run too, and a million shuffled items read into their objects took twice as long). Consecutive raw members the
class lays out next to each other in wire order are one block, copied at once, and a struct member is copied whole;
both hold only where `__Check`, a startup check on an uninitialized instance (`NinoFieldBlock.IsAt`, `ReadsAsIs` for
Untrusted mode's checks), proves the object's memory is the wire's, read once per call and never per element; where it
fails, a second chain with one item per member (`__Fields`) runs instead, which also writes one body. The bytes are
the member-by-member body's. A run of accessor structs, not a delegate or an offset table, keeps each member a
constant offset the JIT folds: a first form that read members through generic static fields spilled the loop to 165
stack operands (3,708 bytes of code for one write loop; 31 operands and 1,056 bytes now).

A formatter implements `Serialize` and `Deserialize`; a struct formatter that has a capability implements its members
too (generated formatters of fixed-size and block structs, of class bodies and of polymorphic bases, the tuple and
pair formatters). Calling a default on a struct boxes it (CoreCLR, Mono, IL2CPP), so Nino.Core calls only the queries
(`FixedSize`, `BlockMap`, `BodyTraits`, `HasRunLoop`, `PrefetchBytes`, `HasMemberPrefetch`, the
slot stride, `HasSizedWrite`, `HasExternalWrite`) on a formatter that may lack the capability, once per type into a
`static readonly` field (`NinoBlockPlan<T, TFormatter>.Size`, `NinoKind<T, TFormatter>`, `NinoKind<TFormatter>`),
and the other members only where that field says the
formatter has them: a constrained call to the struct's own method on every runtime, never an `is` test, a cast or a
boxed interface field. The body traits and the loop, prefetch and top-level write queries sit on the non-generic `INinoFormatter` so that
their fields are generic over the formatter alone: a loop over a class `T` is shared generic code, where a field of a class generic over `T` is a
lookup per collection, while the formatter is still its exact struct and its field folds. `BodyTraits` is the one
query also called per value, on an `INinoBodyFormatter<T>`, which always implements it. Two interfaces stay apart:
`INinoRawFormatter`, a marker with no members tested as a type (by reflection, and on .NET 8+ by a test of the struct
type parameter the JIT folds while importing, without boxing), and the internal raw-array formatter that
`NinoRawArray<T>` holds for the top-level calls of an array of raw elements, which are generic over the array type
alone (no formatter type parameter to call through), so its field is the boxed formatter itself, whose exact type Tier1
code knows.

**One element kind per type, decided once.** How a collection moves its elements is decided in one place,
`NinoKind` (`Internal/Collections/Traversal/NinoKind.cs`), never again in a collection formatter, by two classes keyed by
what decides. `NinoKind<TFormatter>`, keyed on the formatter alone, holds what the formatter says of every value:
`Traits` (`BodyTraits`, now a member of the non-generic `INinoFormatter`, with the `NinoBodyTraits.Dispatches` bit the
generator sets on a polymorphic base's formatter), `IsString` (a `typeof` of `StringFormatter`: the keys a polymorphic
value's run loop writes), `Cursor` (`HasCursor`), the run loop, the prefetch and the top-level write queries; its fields fold in code shared over a class element, where a field
of a class generic over the element is a lookup per collection. `NinoKind<T, TFormatter>` holds the kind of a value
type without references, in this order: raw (an `INinoRawFormatter` whose `NinoLayout` is canonical on this runtime,
`RawFormatter`'s check read as it is: being unmanaged is not enough), slot (a raw `T?`), block (a `BlockMap`: `DateTimeOffset`,
and composites whose items are raw or blocks, #348), else per value; with the stride of the block kinds and the read policies that
follow (`MinWire`, `CopiedWhole`, `ReadOverwrites`, `Heavy`, `HeavyHashed`). Its code is never shared, so it is read
behind `IsReferenceOrContainsReferences<T>()` (`NinoKind.Of`, `NinoKind.Stride`). Every trait is a query of the
formatter or a `typeof`, never an `is` test or a cast, and no member is generic (Unity 2022.3's IL2CPP fails on generic
default members, #405). The loops are one per direction over a storage view (`NinoRuns.WriteLoop`, `NinoRuns.ReadLoop`,
#355): a run's five fields do not fold in the JIT, so an entry point of values in a row passes the constants (the size
of `T`, no key, every entry in use) rather than reading them from the run, and the loop tests each piece against them
before calling it, since a call whose test folds inside it is still imported and spends the caller's inlining budget.

`Deserialize` takes the reader first, as every read API does, and the current value by `ref`. Passing `default` creates a new instance, and passing an
existing instance populates it in place when possible. One code path serves both `Deserialize` and ref
deserialization.

* **Built-in formatters** are structs in `Nino.Core`: `UnmanagedFormatter<T>`, `StringFormatter`,
  `Utf8StringFormatter` (hidden: members say `[NinoUtf8]`), `NullableFormatter<T, TF>`, `ArrayFormatter<T, TF>`,
  `UnmanagedArrayFormatter<T>`, `ListFormatter<TList, T, TF>`, `DictionaryFormatter<TKey, TValue, TFK, TFV>`,
  and so on. Element formatters are type parameters, so the JIT/AOT compiler inlines element
  (de)serialization into the collection loop.
  The tuple formatters (`ValueTupleFormatter<T1..Tn, TF1..TFn>`, `TupleFormatter<...>`, arity 1 to 7) and the
  multidimensional array formatters (`Array2DFormatter` to `Array4DFormatter`) are written by templates,
  `runtimes/csharp/tools/CodeGen` (`dotnet run --project runtimes/csharp/tools/CodeGen`), into `Formatters/TupleFormatters.g.cs` and
  `Formatters/MultiDimFormatters.g.cs`; unit tests compile the templates and fail when a file is out of date.
* **Generated formatters** are structs in `<Assembly>.NinoGen`, one per `[NinoType]`. Members call
  their formatter as `default(TFormatter).Serialize(...)`, which is a direct, inlinable call.
* **`CachedFormatter<T>`** is a struct that forwards to the formatter `NinoFormatterCache<T>` holds (an internal
  `NinoFormatterBase<T>` class wrapping the struct formatter). It is used for type parameters of generic methods, for
  `[NinoType]`s from other assemblies, for types that hold themselves, and for types resolved at runtime. It costs
  one virtual call.
* **Custom formatters**: `[NinoMemberFormatter(typeof(F))]`, where `F` is a struct implementing `INinoFormatter<T>`
  for exactly the member type (NINO008 otherwise), called as `default(F)` and inlined. Every formatter a user
  writes is a struct: `NinoFormatterBase<T>` is only the runtime dispatch carrier and is internal. There is no
  run-time registration API: formatters are declared with `[assembly: NinoFormatter]` or `[NinoMemberFormatter]`.

## Formatter registrations

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
     its raw bytes (stricter than C#'s unmanaged: no `Nullable`, tuples, `nint`, `DateTimeOffset`, or
     `[NinoType]` structs that are not raw). `UnmanagedFormatter`, `UnmanagedArrayFormatter`, `NullableArrayFormatter`,
     `UnmanagedListFormatter`, `NullableListFormatter` and `UnmanagedNullableFormatter` mark their element
     parameter. A plain `unmanaged`
     constraint, such as the one a generated formatter of `[NinoType] class Box<T> where T : unmanaged` copies from
     the type, binds any unmanaged type.
   * `[NinoCreated] TCollection`: the formatter creates instances through `NinoActivator<TCollection>.Create()`, so the
     bound type must be a non-abstract class with a public static `[NinoFactory]` factory or a public
     constructor callable without arguments (every parameter optional). See "User collections" below. A plain `new()`
     constraint is still honored for third-party formatters that call `new T()`.

   IL2CPP keeps no custom attributes on generic parameters, so there the runtime resolver reads these marks (and
   `unmanaged`) from `NinoParameterMarks`: Nino.Core registers its own formatters', and each generated registry
   registers, in its static constructor and before its assembly's registrations reach the resolver, those of every
   generic formatter the assembly's `[assembly: NinoFormatter]` attributes name.

Lookup order: registrations of the type's own definition (and of arrays, by rank) in `Priority` order, then raw
unmanaged values, then `[NinoType]`s, then registrations of a bare type parameter (`INinoFormatter<TCollection>`),
which match by their constraints (or the `targets` they list). NINO020 reports a registration that cannot be used.
The generator ignores the generated registrations of generic `[NinoType]`s: it names its own generated formatters
directly and reaches other assemblies' through `CachedFormatter<T>`, so only the runtime resolver closes them.

The runtime reads an assembly's registrations when it first meets one of its types, or when a lookup misses (then
every loaded assembly). A formatter-only library may not be loaded yet when only a runtime-closed generic needs it
(`Box<T>` with an `ImmutableArray<T>` member names no `Nino.Immutable` type), so each generated registry also calls
`NinoRegistry.EnsureInitialized` for every referenced assembly whose registrations its compilation saw.

## Formatter cache and registration

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
the `NinoSubtypes<TBase>` tables) drops the entries whose type mentions the assembly, including arrays and
generics built from its types. Generic statics closed over its types (`NinoFormatterCache<T>.Instance`) belong to
the collectible loader allocator and go with it. The net8.0/net10.0 builds of Nino.Core call `Remove` from the
context's `Unloading` event, hooked when the assembly registers; the netstandard builds cannot reference
`AssemblyLoadContext`, so there the caller calls it (`UnloadTests`).

## Polymorphism

Every `[NinoType]` registers one handler, once, in the global (`NinoSubtypes<object>`) registry, from the
generated code of its own assembly. Registration is decentralized, so the generator never scans referenced
assemblies. Each base type (non-sealed class or interface) has its own `NinoSubtypes<TBase>` table, which
adopts a subtype from the global registry on its first lookup after checking that it derives from `TBase`.
Registering per type instead of per (base, subtype) pair keeps generated code linear in the number of types
rather than in the sum of hierarchy depths, and avoids one generic instantiation per pair.

* **Generated dispatch (fast path)**: the topmost dispatching class of each hierarchy in an assembly (and each
  `[NinoType]` interface) gets two methods, emitted with the registry (`NinoGen.Dispatch.g.cs`), that list the
  subtypes declared in the same assembly (non-generic, not tracked). A class below the root that dispatches (not
  sealed) gets the same methods over its own subtypes when they are at most half of those of the nearest base with
  methods of its own; any other calls that base's (#355).
  * Serialize: a chain of `value.GetType() == typeof(Sub)` checks, each one method table compare, writing the
    subtype's id and body directly (up to 64 subtypes; beyond, the registry). With 20 subtypes this writes 1.3x
    as fast as the registry when the subtypes repeat in a fixed order, and 3x when they come in random order. The
    depth is checked once before the chain; a body too deep goes to the registry out of line with the writer by
    value (`NinoSubtypes<TBase>.SerializeDeep`), so the dispatch holds no second chain for it.
  * Deserialize: the depth checked once (`NinoObject.IsDeep`), then `switch (id)` over the subtype ids, each case a
    call to a one-line method of the root's formatter (`__ReadExact{n}`, inlined for a flat body, not inlined for
    one holding a class body, each case a tail call) that reads the subtype's body into a slot holding null or that
    subtype (`NinoObject.ReadExactShallow`). In that non-generic method it is not shared code; called from the
    switch it would be (the subtype is a class), taking a generic context in a register and a frame slot on every
    call. The rest goes to the registry (`NinoSubtypes<TBase>.DeserializeOther`), which reuses or recycles the
    instance and defers a deep body: a body too deep, null or an id the switch lacks after the switch, a slot
    holding another subtype from the case. The ids are hashes spread over 32 bits (the closest two of the
    benchmark's 22 switches 37.9 million apart), so Roslyn never finds a dense range to give a jump table: the IL is
    a binary search of compares, which the JIT, IL2CPP's C++ compiler and Mono keep as branches. Each case reading
    its own deep path, replacement and deferral, as before (#355's switch step), inlined 41 allocations and 20
    deferrals into `PolymorphicClassFormatter.ReadBody`, which then ran out of locals; the cases now hold only the
    body. In random order this reads 1.5x as fast as the registry, whose indirect call mispredicts; a perfect hash
    with a dense switch (an indirect jump) was measured too and mispredicts the same way. When the subtypes repeat
    in a fixed cycle the registry's indirect call is perfectly predicted and the switch is about 10% slower.
  * A generic base (a generic abstract class or interface, a generic class, or an abstract class nested in a
    generic type) is a root of its own. For each closed instance of it that subtypes in the same assembly derive
    from (`Box<int>` for `IntBox : Box<int>`), a static class `__NinoDispatch{n}` holds the same two methods as a
    non-generic root; the generic formatter's methods select it with `typeof(Box<T>) == typeof(Box<int>)` and pass
    the value or slot on as that type (`NinoObject.SameType`, `NinoObject.SameTypeSlot`, which reinterpret without a
    check). The JIT folds the type compare where the type arguments are value types; where they are reference types
    (shared code) it is one lookup in the generic context. The methods live in a non-generic class so that
    `ReadExactShallow` there is not shared code either. Other closed instances, and generic subtypes, go through the
    registry. Before, a generic base had no generated dispatch and went through the registry, which wrote and read an
    array of subtypes in random order several times slower than a non-generic base; now both take the same time
    (`GenericPolymorphicTest`).
  * A class below the root with methods of its own writes and reads through its own chain and switch, which hold
    only its subtypes, and falls back to its own registry (`NinoSubtypes<TMiddle>`); one without reads through its
    base's switch and then checks that the type the data named is one of its own (`NinoObject.EnsureSubtype`).
    Either way data naming a sibling type throws instead of being stored. Through the root's dispatch every subtype
    of the hierarchy is a compare or a case, which spent the budget of the bodies it was inlined into:
    `CustomerEventFormatter.Serialize` tested seven subtypes where two can be its values ("inline exceeds budget").
  * Each subtype costs three lines per hierarchy and as many per class above it with methods of its own. Halving
    the subtypes at each such level keeps this to n log n; methods for every dispatching class grow with the square
    of a chain's depth (`PolymorphicFooBase`'s `FooA` to `FooH`, 12 to 19 of its 20 subtypes each: the benchmark
    project's dispatch file 1.8x as large and the generator benchmark's analysis 1.5x as long, for no measured gain;
    `PolymorphicClassFormatter.ReadBody` still inlines `FooD`'s 20-case switch and runs out of locals, which costs
    nothing measurable). The dispatch grows the 330-type workload by 12%. Writes call `NinoObject.WriteExact` from
    the chain (inlined where the caller's budget allows, else shared code): a non-generic method per subtype there
    too wrote 4.5% fewer instructions for an array of 20 subtypes, but more where the bodies would have been inlined,
    and cost about 2.5% more compile time on the 330-type workload.
  * A subtype whose body is one raw block (a class that is not version tolerant, keyed or tracked, whose members
    are all raw values, see `INinoRawBodyFormatter<T>`) is written by `NinoObject.WriteExactRaw`: one capacity
    check (`Reserve`) for its type id and body together, then constant-offset stores, instead of one check for the
    id and another for the body. The same merge applies where the formatter writes its own subtype (`TryWriteOwnRaw`,
    `SerializeRaw`, `SerializeSealedRaw`: id or presence byte with the body) and to the top-level twins
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
     levels deep). A closed type that is a slot (`Shelf<string>`: abstract, an interface or a class that is not
     sealed) also brings the closed types of the generic `[NinoType]`s deriving from its definition, in this
     compilation or the definition's assembly, with the arguments the slot gives them (`ListShelf<T> : Shelf<T>`:
     `ListShelf<string>`; a definition whose constraints the arguments may not meet is left out): data another
     process wrote names them by id, though no code here does. They are cases of the same lazy by-id dispatch (`NinoGenUsages`), under the closed type's id
     (`[NinoFormerName]` types also under the name id): `NinoTypes.Register<Box<int>, Box_1Formatter<int>>(id)`, which
     registers the formatter and the subtype handler in one call, so AOT runtimes have the instantiation and a
     reader can map the id back to the type without having seen it. Closed types of a generic `[NinoType]` from
     another assembly (whose formatter is internal to it) are registered with `NinoPolymorphic.TryRegister(typeof(..))`.
  2. Writing an instance registers its runtime type: a lookup by type that misses closes the formatter definition
     (`MakeGenericType`) and registers it. A read by an unknown id, as a last resort, also checks the closed generic
     `[NinoType]`s whose formatter this process has resolved.
  3. `NinoPolymorphic.TryRegister(Type)` / `TryRegister<T>()` (true once the type is registered), for an instantiation a process reads but never names nor
     writes (created only from type parameters or by reflection, for example in another assembly). Such a type is
     otherwise an unknown id; this is the one case that needs a call at startup.
* Registration is re-entrant: a generated registry registers a closed generic whose formatter is internal to
  another assembly through `NinoPolymorphic.TryRegister(Type)`, which asks every registry for that type id again,
  including the one running. `NinoRegistry.RegisterType` keeps the types the thread is registering and returns at
  once for one of them, so the outer call closes the formatter itself (before, a non-generic base nested in a
  generic class, `Holder<long>.Base`, with subtypes in another assembly overflowed the stack on first use).
* Registries are immutable snapshots replaced copy-on-write, so concurrent readers never see a torn
  entry. (The v3 inline caches could pair one type's id with another type's delegate under contention.)

## Concurrency and cache lines

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
  own (`NinoPoolLine`, a struct field with an explicit layout and 64 bytes of padding on each side, so the line
  stays clear wherever the runtime places the struct among the pool's fields; a base class did not: CoreCLR put the
  derived class's `_count` directly after the base's last padding byte, on the line of `Filled` whenever the object's
  fields started on a 64-byte boundary), away from the shared stack's count and lock, which batch transfers write, and
  from neighbouring objects. A thread stack's count and generation, written on every take and add, are padded the
  same way (`NinoPoolThreadLine`), so another thread's stack never shares their line. Measured with 4 threads churning 100 instances each, the cost there is the lock
  itself; the padding is kept so the fast path never shares a line with a writer.
* No static field is written on a hot path: formatter instances, `NinoRecycler.PoolsInUse`
  are written once; scratch buffers and reference tables are thread-static.
* Raw-key/raw-value dictionaries, unmanaged arrays and `T?[]` read and write with one bounds check for the whole
  collection; masked copies (padding cleared) use vectors with aligned stores (see `NinoMask`).
* `bool` arrays and lists are block copies, like every raw array: bytes other than 0 or 1 (corrupt or foreign
  data) are not normalized. A fused copy-and-normalize pass (vector unsigned minimum with 1, 512/256-bit, unrolled)
  was measured and dropped: on par for short arrays but twice the time of the runtime's `memcpy` on long ones
  (10,000 bools: 79 ns → 155 ns), which streams faster than any load-min-store loop.

## Reuse deserialization and pooling

* `null` target: a new instance is created (from the pool for `[NinoPooled]` types, or through a
  `[NinoFactory]` factory; collections through `NinoActivator<T>`, see "User collections").
* Existing target of the right type: populated in place. Lists and arrays reuse elements by index.
  Dictionaries are merged by key: each value is read into the value its key already maps to, and keys missing
  from the data are removed.
* Strings are compared, not replaced: a string member, a list or array element, or the value of a key already in a
  dictionary keeps its string when the data holds the same chars, so an unchanged string costs no allocation
  (`Reader.ReadString(string current)` and `ReadUtf8(string current)`, which generated code passes the member's current
  value; the string run kernels compare each element and each value, and a dictionary's merge each value through
  `NinoCursor.ReadAt`). A `[NinoUtf8]` string is compared without decoding, so only ASCII text (as many bytes as chars)
  is kept; other UTF-8 text is decoded into a new string. A `null` slot, a new instance and a dictionary read from empty
  read new strings.
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
is cleared and refilled) are each recycled once with the element type (`DiscardAll`, `DrainAndDiscard`); an element
composed of items (a `KeyValuePair`, a value tuple: a dictionary's pairs, a priority queue's element and priority, a
list's pairs) recycles each item once instead, as its formatter composes it, whatever collection drops it. A `Tuple`
object composes its items' recycling too: dropped by a collection, replaced by a read or read as null, its items are
recycled once, unless a handler is registered for the tuple type (which then owns the tuple and its items) or a pool
takes it back. An instance dropped as a whole takes its elements
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
simply collected. Measured in [v4-benchmarks.md](../v4-benchmarks.md): a string-free graph deserializes 3x faster
than a fresh one with no allocation, and a list of 100 pooled elements twice as fast.

## Serialization callbacks

`[NinoOnSerializing]`, `[NinoOnSerialized]`, `[NinoOnDeserializing]` and `[NinoOnDeserialized]` (Nino.Core,
`Attributes/NinoCallbackAttributes.cs`; MemoryPack's four callbacks under Nino's prefix) mark methods of a
`[NinoType]` class or struct that the generated formatter calls directly: no delegate, interface or reflection, so they
work the same under IL2CPP, NativeAOT and HybridCLR. The decisions:

* **Where.** In the type's body (`WriteBody` / `ReadBody`, and every twin of it: `__Top`, `__Shallow`, the
  new-instance read), which every value of the type goes through: top level, member, collection element, a subtype in
  a base or interface slot (the dispatch calls the subtype's body), a body deferred past `NinoDepth.Inline`.
  `OnSerializing` before the first byte of the body (before a region's length), `OnSerialized` after its last;
  `OnDeserializing` before the first member is read, `OnDeserialized` after the last (after the region ends, after a
  re-created type is constructed). A type with callbacks therefore leaves the paths that write or read it without its
  body: it is never of fixed size (`TypeShape.HasCallbacks`, `TypeFacts.IsFixedSize`: no `WriteFixed`, no blocks in
  collections, `GetFixedSize` is -1) and a sealed class of raw members has no raw body (`TypeModel.RawBody`). The bytes
  are the same either way.
* **Instance or static.** `OnSerializing`, `OnSerialized` and `OnDeserialized` run on the value (instance methods,
  NINO042 otherwise). `OnDeserializing` may be static, called before the instance is taken or created, or an instance
  method, called on the instance the members are read into: the existing one when deserializing into a value, one
  from the pool of a `[NinoPooled]` type (whose `OnNinoPoolReturn` remains the place to reset it: the callbacks do not
  replace the pool hooks), or a new one. A type created from the members it reads (constructor parameters, `init` or
  `readonly` members, records with a primary constructor) has no instance before they are read: only a static one
  runs (NINO043). Static ones run before instance ones.
* **Hierarchy.** The callbacks of the type and of its `[NinoType]` base classes (the levels whose members it writes),
  base classes first, one method per callback and level (NINO041). A virtual callback overridden below is called once,
  through the virtual call; a callback a derived class hides with `new` is called through a cast to its class.
  Interfaces declare none (NINO040): a slot of an interface or abstract class calls the callbacks of the concrete
  type it holds.
* **Structs.** A struct's callbacks run on the copy being written (what `OnSerializing` changes is written, not kept)
  and on the value in its slot when read (what `OnDeserialized` sets is kept, as in an array element). A struct
  written as its raw memory runs no code at all, and an array of it is one copy: running callbacks would need other
  bytes, so callbacks there are an error (NINO044) instead of a silent change of the wire format.
* **Reference tracking.** An object written again as a back reference has no body, so its callbacks run once per call;
  read back, an object of a cycle may be reached by reference while its own body is still being read
  (`OnDeserialized` of the outer object runs after the inner one's).
* **Deferral.** A body deferred past `NinoDepth.Inline` (128 open bodies) is read after the bodies above it, with its
  callbacks: the `OnDeserialized` of an object above it may see it still empty.
* **Exceptions.** A callback's exception ends the call as it is (not wrapped); the per-thread state of the call is
  released as for any other exception, and the next call starts clean. `OnSerialized` / `OnDeserialized` do not run
  after an exception in the value.
* **Accessibility.** The formatter is a struct outside the type, so callbacks must be `public`, `internal` or
  `protected internal` (NINO039); `private` ones would need an accessor per method, which `internal` makes unneeded.
* **Zero cost.** A type without callbacks has `TypeModel.Callbacks == null` and its formatter is the same text as
  before the feature (the generated output of the unit tests and benchmarks compared byte for byte).
* **Other languages.** Callbacks are code, not bytes: the published layout (`[NinoTypeLayout]`) and nino-gen's output
  do not change, and the Go, Rust and TypeScript code nino-gen writes does not call them.

## Settings

Process-wide settings are set through one static class, `NinoSettings`, and must be **set before first use**: they
are plain fields read without synchronization, so they belong in startup code that runs before the first
serialization call on any thread.

| Setting | Meaning |
| --- | --- |
| `SetRecycler<T>(Action<T>)` | handler for instances dropped from slots declared as `T` (null removes it) |
| `PoolMaxRetained` | shared-stack size of pools created from now on (default 256) |
| `SetPoolMaxRetained<T>(int)` | shared-stack size of `T`'s pool |
| `MaxDepth` | open bodies (classes, and structs and collections that can hold themselves) a read or write may nest (default 512, at least 128) |
| `MaxCollectionLength` | the most elements a collection in the data may claim (default `int.MaxValue`; the bytes left bound it anyway) |
| `MaxHashCollisions` | the most keys a key read from the data may find in its hash bucket (default 100) |
| `Untrusted` | checks for data from someone who would craft it (default off; see below) |

`MaxDepth`, `MaxCollectionLength`, `MaxHashCollisions` and `Untrusted` are taken as constants by the read and write
paths: `NinoLimits` holds them in `static readonly` fields, frozen (`NinoSettings.Freeze`, under a lock) the first
time a path uses one, so Tier1 code folds every test of them, and setting one afterwards throws
`InvalidOperationException`. Mono, IL2CPP and the interpreters read the fields: a load and a compare.

A value read on a hot path stays next to the code that reads it, so the JIT sees a static field of the closed
generic type rather than a lookup: the recycler handler is stored in `NinoRecycler<T>` and a pool's size in
`NinoPool<T>`, and `NinoSettings` is the only code that writes them. Registrations (formatters, polymorphic types)
are made by generated code and are not settings.

## Untrusted input

Every read checks the data in every mode: bounds, lengths against the bytes left (and `MaxCollectionLength`), depth,
type ids, references, duplicate keys, `bool` and presence bytes of 0 or 1, regions, keyed entries that their member
fills exactly and sections of deferred bodies that match their region. Each failure is a `NinoDataException`
(`NinoThrow`, out of line).

Hash flooding (keys built to share a hash bucket, which makes each insert scan every key before it) is bounded in
every mode by `MaxHashCollisions`, at no cost where Nino fills the table: a read fills the entries of a new
`Dictionary<TKey, TValue>` or `HashSet<T>` through their run and `HashTables` (Nino.Core) links them into the buckets
in one pass, through a layout declared per runtime and checked at startup (as `CollectionViews` does); the walk of
the bucket it makes for duplicates counts, and a read rejected part way leaves the table empty. Where the
table adds a key itself (an existing table, a layout not found), `HashTables.Walk` walks the new key's
bucket afterwards, out of line (`HashTables.Plan` decides per key and value type whether a loop inserts or walks).
String keys of .NET's tables, which switch to a randomized comparer after 100 collisions, are left to them;
`ImmutableDictionary` and `ImmutableHashSet` count equal hash codes in the sorted hashes their trees are built from.
`ConcurrentDictionary` keeps its buckets in a private table: `ConcurrentTables` finds it, and the nodes of a bucket's
chain, through mirror classes per runtime (.NET 8+, Mono and Unity, .NET Framework) checked at startup. A new one is
built there, as `SortedSetNodes` builds a `SortedSet<T>`: created empty by its constructor from a collection that only
reports the count (sized and configured as before; where that constructor starts at 31 buckets, Mono and .NET
Framework, made with a bucket per pair), then each pair's node, made as `ObjectTypes` makes objects, put at the head
of its bucket without locks, as the constructor puts it, after the chain is searched for the key and counted. Its
constructor from the pairs leaves no point between two pairs to count at, and `TryAdd` takes a lock per pair; built
this way the read is faster than through the constructor on every runtime (no enumerator, lock or resize check per
pair). An existing one (or one whose build fails its startup check) is filled through `TryAdd`, and the new key's
bucket is walked after each add. Where no layout matches, the constructor fills it, uncounted.

`NinoSettings.Untrusted` adds what costs time on every read, all behind `NinoLimits.Untrusted`, which Tier1 code
folds, so trusted reads keep their code:

* **Seeded comparers** (`NinoSeededHash`): new dictionaries and hash sets of integer, `char`, `Guid` and `string`
  keys get a SipHash-1-3 comparer keyed per process, so keys cannot be chosen to collide at all.
* **Values copied as their bytes** are checked as their constructors would: `NinoLayout<T>.Checks` lists the offsets
  of each `bool`, `DateTime`, `decimal`, `DateOnly` and `TimeOnly` in `T`'s memory, and the raw paths
  (`NinoLayout<T>.CopiesAsIs` is false for such types) check them in the data (`NinoChecks`), out of line.
  Generated members of those types read through `ReadChecked`. Enums are not checked (flags combine; a newer
  version may add members), and `TimeSpan`, `Guid`, `char` and the numbers have no invalid values.
* **Trailing bytes**: the span APIs (`NinoDeserializer.Deserialize`, `NinoDirect`, the interceptors) read through
  `NinoUntrusted`, which reads with a `Reader` and throws when bytes are left after the value and its deferred
  bodies.
