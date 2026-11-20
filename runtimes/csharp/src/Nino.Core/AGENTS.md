# Nino.Core: file map

> Read when: reading or changing anything in `runtimes/csharp/src/Nino.Core` (the runtime: the public API, formatters, Reader/Writer, settings, polymorphism, pooling, the internal fast paths).

The runtime every generated formatter calls, packed as `Nino.Serialization`; netstandard2.1 (Unity and
Mono: no hardware intrinsics), net8.0 and net10.0 (trimmable, NativeAOT compatible).
`audit-core` audits this directory, `audit-security` its read paths, `optimize` their machine code.

When you add, rename or delete a file here, update its line (`tools/agents.sh --check` fails on a `.cs` file it does
not find named here).

## Root

```
NinoSerializer.cs            the public Serialize<T> / SerializeBoxed (to a byte[], an IBufferWriter<byte>, a Writer or
                             a Stream; SerializeAsync / SerializeBoxedAsync),
                             GetFixedSize<T>, IsSupported; byte[] results go through the scratch buffer (NinoScratch)
NinoDeserializer.cs          the public Deserialize<T> (a new value, or into an existing one by ref) / DeserializeBoxed,
                             from a span, a ReadOnlySequence<byte>, a Reader or a Stream (read to its end;
                             DeserializeAsync / DeserializeBoxedAsync)
NinoSettings.cs              every process-wide setting (MaxDepth, MaxCollectionLength, MaxHashCollisions, Untrusted,
                             pool sizes, SetRecycler<T>), plain fields frozen on first use (NinoLimits)
NinoDataException.cs         the exception every check of the data throws (an InvalidOperationException)
NinoNullable.cs              NinoNullable<T>: a nullable value whose memory is always clean (its presence a whole T), a
                             raw struct written as that memory everywhere (two T a value; the bytes of a T? run where T
                             is as large as its alignment)
NinoKernels.cs               NinoKernels: native Permute / Interleave32 / NormalizeBoolSlots / Narrow / Widen /
                             NarrowAscii / WidenAscii function pointers the netstandard2.1 build calls when registered (Unity's Burst and WebGL
                             kernels), with their scalar forms
Properties.cs                [module: SkipLocalsInit]
```

## Attributes/

```
NinoTypeAttribute.cs         [NinoType]: marks a type for the generator (autoCollect, includeNonPublicMembers,
                             inheritedBySubtypes, format, trackReferences)
NinoFormat.cs                NinoFormat: Positional, VersionTolerant, Keyed (also compiled into the generator)
NinoMemberAttribute.cs       [NinoMember(index)]: selects and orders members under [NinoType(false)]
NinoIgnoreAttribute.cs       [NinoIgnore]: skips a member a [NinoType] collects
NinoUtf8Attribute.cs         [NinoUtf8]: a string member written as UTF-8
NinoFormerNameAttribute.cs   [NinoFormerName]: a type's former name (its type id) or a keyed member's former names
NinoConstructorAttribute.cs  [NinoConstructor]: the constructor deserialization uses
NinoFactoryAttribute.cs      [NinoFactory]: a static method Nino calls to create an instance (a pool, ...)
NinoPooledAttribute.cs       [NinoPooled]: the generator adds ReturnToPool() and pool hooks to a sealed class
NinoCallbackAttributes.cs    [NinoOnSerializing], [NinoOnSerialized], [NinoOnDeserializing], [NinoOnDeserialized]:
                             methods the generated formatter calls around writing and reading a value
NinoMemberFormatterAttribute.cs  [NinoMemberFormatter(typeof(F))]: a member written by a formatter of the user's
NinoFormatterAttribute.cs    [assembly: NinoFormatter(typeof(F<>))]: registers a struct formatter for the generator and
                             the runtime resolver alike (rules shared through NinoFormatterBinding)
NinoCreatedAttribute.cs      [NinoCreated] on a formatter's type parameter: the formatter creates it (NinoActivator)
NinoRawAttribute.cs          [NinoRaw] on a formatter's type parameter: bound only to types written as raw bytes
NinoTypeLayoutAttribute.cs   [assembly: NinoTypeLayout]: the published wire layout of a [NinoType] (read by nino-gen,
                             the corpus); NinoHeader
NinoLayoutsAttribute.cs      [assembly: NinoLayouts(@"...")]: an assembly's layout baseline (TOML) the analyzer checks
                             changes against; [Conditional("NINO_LAYOUTS")], so never in metadata
```

## CompilerServices/ (public, for generated code)

```
FormatterInterfaces.cs       INinoBodyFormatter<T> (+ NinoBodyTraits), INinoRawBodyFormatter<T>, INinoTopFormatter<T>
                             (__Top twins), INinoRawFormatter (a marker, tested as a type)
NinoPrefetched.cs            NinoPrefetched.At: the prefetch generated INinoFormatter.PrefetchMembers calls
NinoObject.cs                header handling of generated class formatters: presence byte / type id / reference header,
                             then the body; the ...Raw forms write header and raw body into one reservation
NinoFieldBlock.cs            generated raw bodies of classes: whether the fields lie in the object as the body lays them
                             out (checked once on an uninitialized instance), then one block copy
NinoDirect.cs                the top-level calls with the formatter the generator resolved (what interceptors call;
                             ...Prefetched for arrays and lists of prefetching classes (PrefetchBytes); the sequence and
                             Stream forms, the latter through NinoStreams)
NinoRun.cs                   NinoRun: the run cursor over any contiguous storage (first value, count, stride, the in-use
                             test of free entries; the key's offset and write only in the run of a pair's values) and the
                             one way a run of items is made (Items: a set's values, a pair's values with their keys), and
                             the pieces of a loop over it (Next, Skips, WriteKey, Value; WriteEach, the deep path,
                             through NinoRuns.WriteLoop);
                             what INinoFormatter.WriteRun takes
NinoBlockMap.cs              NinoBlockMap: where a block's bytes lie in a value's memory, built item by item (Item)
NinoBlocks.cs                NinoBlocks: the items of composites (ItemSize, WriteItem, ReadItem: raw, or a block through
                             constrained calls), and the offsets NinoBlockMap is built from
NinoEffectiveSettings.cs     the frozen settings, for Nino's own packages (Nino.Immutable)
NinoElement.cs               NinoElement.Write: one element written as Nino.Core's collections write it (a raw T? as a
                             slot), for Nino's own packages' walked collections (Nino.Immutable)
NinoUnsafe.cs                unaligned little-endian memory access, ReadBool, array allocation, type queries; used by
                             Reader, Writer and generated code
NinoUnsafe.Copy.cs           NinoUnsafe.Copy and its parts: block copies by size (overlapping moves, vector loops, Arm64
                             paired loads, Memmove beyond)
Registration/                what a generated registry calls at startup:
  NinoGeneratedAttribute.cs  [NinoGenerated]: marks an assembly with generated code, names its registry
  INinoRegistry.cs           INinoRegistry: an assembly's generated registry (lazy registration by type id)
  NinoTypes.cs               NinoTypes: how a generated registry registers a concrete [NinoType], its formatter and its
                             handler in one call (Register, RegisterFixed, RegisterTop, RegisterTracked)
  NinoSubtypes.cs            NinoSubtypes<TBase>: the registry of a base type's subtypes by type id
  NinoSubtypeHandler.cs      NinoSubtypeHandler: writes and reads one registered type; the registry's entry
  NinoParameterMarks.cs      what a formatter's type parameters require ([NinoRaw], unmanaged, [NinoCreated]): their
                             attributes, or where the runtime drops them (IL2CPP) the marks Nino.Core registers for its
                             own formatters and generated registries for the formatters they register (Register)
  NinoFunctionPointerFields.cs the delegate* fields of structs, by assembly and runtime name, as generated registries
                             register them (Register, merged across registries): the one source layouts take function
                             pointers from
  NinoLayoutOffsets.cs       NinoLayoutOffsets: the compiled offsets of the generic structs IL2CPP lays out in two ways,
                             as generated registries register them (the generator's SplitLayouts); NinoLayout measures
                             those structs from them
```

## Formatters/

```
NinoFormatter.cs             INinoFormatter<T> (Serialize, Deserialize; capability members with defaults: FixedSize,
                             WriteFixed, BlockMap, ReadBlock, internal slots with a slot's single value at a reference
                             (WriteAt, ReadAt), and a composite's ItemsMayRecycle, RecycleItems) and the non-generic
                             INinoFormatter it
                             extends (BodyTraits; the run loop of polymorphic bases: HasRunLoop, WriteRun; the prefetch of long
                             collections of classes: PrefetchBytes, HasMemberPrefetch, PrefetchMembers; internal
                             HasSizedWrite, HasExternalWrite, HasCursor with WriteStrings, ReadStrings: a run of pairs
                             holding strings through the string run kernels; RecyclesItems);
                             NinoFormatterBase<T> and its struct wrappers (StructFormatter, FixedStructFormatter,
                             TopStructFormatter, NestingFormatter), RawFormatter, NinoSized, INinoRawArrayFormatter, CachedFormatter<T>
NinoFormatterCache.cs        NinoFormatterCache<T>: the formatter of T (registered by generated code, else resolved);
                             NinoRaw<T>, NinoTopFormatter<T>, NinoRawArray<T> as static readonly fields;
                             NinoMissingFormatter<T> (the never-null stand-in of a type without one)
NinoFormatterResolver.cs     builds formatter types by reflection for unregistered types, applying the
                             [NinoFormatter] registrations with the generator's rules
BuiltInFormatters.cs         the [assembly: NinoFormatter] table of the BCL formatters below (generator and resolver read it)
BoxedFormatter.cs            BoxedFormatter: NinoFormatterCache<T> for a runtime Type (the Boxed APIs)
PrimitiveFormatters.cs       UnmanagedFormatter<T> (raw bytes), RawValueFormatter<T>, IntPtr / UIntPtr (8 bytes),
                             DateTimeOffset, String (Latin-1 or UTF-16), Utf8String, Nullable,
                             UnmanagedNullableFormatter (a raw T?: one presence byte and the value alone, a slot of
                             a fixed size as the element of any collection), ObjectFormatter
Collections/                the collection formatters, a folder per kind:
  Arrays/
    ArrayFormatter.cs        general T[] and reuse
    UnmanagedArrayFormatter.cs contiguous unmanaged arrays
    NullableArrayFormatter.cs raw T?[] at a fixed stride
    ArraySegmentFormatter.cs array segment read and write
    MultiDim.cs              multidimensional array shape and lengths
    BitArrayFormatter.cs     bit-packed arrays
  Sequences/
    ListFormatters.cs        general, unmanaged block and raw nullable lists
    InterfaceFormatters.cs   sequence and set interfaces
    CollectionFormatter.cs   ICollection<T> enumeration and refill
    HashSetFormatter.cs      HashSet and derived sets: its entries as a run, written, and read through NinoEntries
    QueueFormatter.cs        Queue and derived queues, with a checked backing ring
    StackFormatter.cs        Stack and derived stacks, bottom-to-top wire order
    PriorityQueueFormatter.cs PriorityQueue (net8.0 and net10.0 builds only)
  Dictionaries/
    DictionaryFormatter.cs   Dictionary and derived types
    DictionaryLikeFormatter.cs any IDictionary implementation
    InterfaceDictionaryFormatter.cs dictionary interface values
    KeyValuePairFormatter.cs standalone pairs and their block shape
    LookupFormatters.cs      groupings, lookups and their concrete backing types
  Concurrent/
    ProducerConsumerFormatter.cs concurrent collection snapshots and refill
    ConcurrentStackFormatter.cs concrete stack refill for the producer/consumer format
    BlockingCollectionFormatter.cs blocking collection snapshots and refill
MultiDimFormatters.g.cs      T[,], T[,,], T[,,,] (generated by runtimes/csharp/tools/CodeGen; do not edit)
TupleFormatters.g.cs         ValueTuple and Tuple of 1 to 8 items, blocks where their items are, their items recycled
                             (a Tuple's too), a value tuple of two to seven composed on a run from its items as a pair
                             is (NinoItems) (generated by
                             runtimes/csharp/tools/CodeGen; do not edit)
Bcl/                         standalone BCL formatters, one file per type:
  BigIntegerFormatter.cs, UriFormatter.cs, VersionFormatter.cs, StringBuilderFormatter.cs,
  CultureInfoFormatter.cs, TimeZoneInfoFormatter.cs, LazyFormatter.cs
Buffers/                     MemoryFormatters.cs (Memory and ReadOnlyMemory), ReadOnlySequenceFormatter.cs
```

## IO/

```
Reader.cs                    ref struct Reader (partial, a file per concern): its fields, constructors and the cursor
                             (the only code that reads the position fields, per target)
Reader.References.cs         back references (ReadReference, SetReference) and the depth of struct bodies (EnterStruct)
Reader.Regions.cs            slices and length-prefixed regions, deferred bodies and the sections after them
Reader.Values.cs             position, blocks, unmanaged values, lengths checked before allocating, presence, raw bytes
Reader.Strings.cs            strings (Latin-1 or UTF-16 through NinoStrings, UTF-8), shared or reused; ReadUtf8At, the
                             cursor form of a UTF-8 read
Writer.cs                    ref struct Writer (partial, as Reader): writes into a NinoArrayBufferWriter (Flush when
                             done); its fields, the cursor, Flush and reserving buffer space (growing it)
Writer.References.cs         back references (WriteReference) and the depth of struct bodies (EnterStruct)
Writer.Regions.cs            patched lengths, regions and deferred bodies
Writer.Values.cs             unmanaged values, lengths and presence, raw bytes, reservations for counted elements
Writer.Strings.cs            strings (NinoStrings) and UTF-8; Utf8MaxSize and WriteUtf8At, the cursor form of a UTF-8 write
NinoArrayBufferWriter.cs     the pooled, growable IBufferWriter<byte> Writer writes into
NinoWire.cs                  the wire constants (null length, presence bytes, headers; also compiled into the generator)
NinoDepth.cs                 depth bounds: bodies past Inline (128) open bodies are deferred; the limit is MaxDepth
NinoDeferral.cs              deferred bodies of one call (NinoDeferredBody<T, TFormatter>, NinoDeferrals)
NinoCallContext.cs           NinoReadContext / NinoWriteContext: back references and deferrals of one call, rented per thread
```

## Polymorphism/

```
NinoPolymorphic.cs           NinoPolymorphic.TryRegister; SubtypeHandler / TrackedSubtypeHandler (created by NinoTypes)
NinoRegistry.cs              NinoRegistry: runs the generated registries (module initializers, Unity hooks, lazy
                             lookups; re-entrant), Remove for unloaded assemblies
NinoTypeId.cs                NinoTypeId: FNV-1a type ids of runtime-form type names ([NinoFormerName] replaces it)
NinoReferences.cs            reference tables of one call (write: identity hash, read: by number) for trackReferences
NinoUnknownIds.cs            type ids a polymorphic read did not find, cached so a repeated unknown id costs nothing
```

## Pooling/

```
NinoActivator.cs             NinoActivator<T>: creates the instances Nino needs (a registered creator: the [NinoFactory]
                             method, else new T(); resolved by reflection for types generated code never saw)
NinoPool.cs                  INinoPooled, NinoPool<T> (per-thread stacks over a shared one); NinoPoolLine and
                             NinoPoolThreadLine, the padded structs that keep its hot fields on a cache line of their own
NinoRecycler.cs              NinoRecycler<T> / NinoRecycler: where reuse deserialization sends the instances it drops,
                             and the ownership rules of a formatter's Deserialize(ref T); the elements a collection drops,
                             a composite's items recycled as its formatter composes them (DiscardAll, MayRecycle<T,
                             TFormatter>, Recycle<T, TFormatter>)
```

## Internal/

### Internal/Collections/Storage/

```
CollectionFields.cs          those fields at the offsets CollectionViews found (SetEntry, MapEntry with their items'
                             offsets, LinkedNode), the in-use test of a set's or a dictionary's entries (InUse), their
                             runs (Run: in use, or new ones a read fills), whether their entries are blocks on this runtime
                             (EntryBlocks, the startup proof), where a KeyValuePair keeps its items (PairLayout, PairMirror)
NinoStorageElement.cs        the storage elements (#355): a set's and a dictionary's entry as the element of its run, its
                             value, or its key and value, at their offsets, the rest of its stride skipped
                             (NinoSetEntryFormatter, NinoMapEntryFormatter: the entry's fields read as fields, also in
                             code shared over a class): a block where its items are, so the block kernels move a run of
                             them; NinoMemoryItem, an item moved as its memory (a set's slots)
CollectionViews.cs           field layouts of List, Stack, Queue, LinkedList, HashSet, Dictionary, SortedList, SortedSet,
                             SortedDictionary per runtime (offsets, the first candidate that passes), as static fields;
                             how a set and a dictionary mark free entries (SetFree, MapFree)
CollectionViews.Mirrors.cs   the candidates: mirror classes per runtime, the fields by name (UnsafeAccessor, net10.0)
CollectionViews.Checks.cs    the startup checks of each candidate against an instance with known contents
ConcurrentStackNodes.cs      read-only CoreLib ConcurrentStack node snapshots for raw writes, startup-checked
ListAccess.cs                a List<T>'s elements as a span (CollectionsMarshal, its fields, or copies)
PriorityQueueHeap.cs         a .NET 10 PriorityQueue's heap as one run of pairs, written and read in place for any
                             element and priority (Write, Adopt: published once in heap order)
SortedSetNodes.cs            SortedSet<T> red-black trees built in one pass (SortedSetTrees, node layouts), and walked in
                             order a chunk at a time for the gather (Walk)
NinoCollectionViews.cs       reads into the backing storage shared by Queue and Stack formatters
```

### Internal/Collections/Hashing/

```
ConcurrentTables.cs          the same for ConcurrentDictionary (mirror classes per runtime, Builder)
ConcurrentTables.Checks.cs   its startup checks (the layout, the hash mask, chains, a built table)
HashTables.cs                links the entries a read filled into a new HashSet / Dictionary (Link, Unlink on a
                             rejected read), counting collisions against MaxHashCollisions; Plan, Insert, Added, Walk; one
                             core for both tables over the entry mirrors (Fields, the null-key policy)
HashTables.Checks.cs         its startup checks (the hashing of each implementation, chains, references and strings)
```

### Internal/Collections/Traversal/

```
NinoCollections.cs           shared collection helpers (rented reads, enumerable writes through the gather, AddAll)
NinoKind.cs                  the one element-kind decision every collection reads: NinoKind<TFormatter> (what a formatter
                             says of every value, read once: Traits, IsString, Strings, Cursor, the run loop, prefetch,
                             the sized and external top-level writes), NinoKind<T, TFormatter> (NinoElementKind: raw,
                             slot, block or per value, with Stride, MinWire, CopiedWhole, ReadOverwrites, Heavy,
                             HeavyHashed), NinoKind.Of / Stride (read behind the no-references guard)
NinoRuns.cs                  the one write of a run (NinoRun) of any stride and the one read of values in a row, over
                             <T, TFormatter> only (Write, Read: arrays, lists, spans, a ring in two segments, a set's or a
                             dictionary's entries, each the element), each kind by its kernel, the rest through the
                             one loop per direction (WriteLoop, ReadLoop; WriteEach, ReadEach out of line); strings and
                             pairs holding them handed to the string run kernels, one call out of line (WriteStrings,
                             ReadStrings); a polymorphic base's run loop (KeyOf: the keys a pair's run writes)
NinoItems.cs                 what a composite of any number of items (a KeyValuePair, a value tuple of two to seven, a
                             dictionary's entry) does on a run, composed once from its items at their offsets: the
                             cursor form (Part, Pair, Cursor; one pick of an item's run item, Then, composed into one
                             element of the string run kernels, NinoItemAt then NinoItemChain; a chain of items,
                             NinoItem ... NinoItemsEnd, walked by Write and Read; a pair entered by WritePair and
                             ReadPair, its first item picked statically, as code shared over a class needs) and the run
                             loop of a polymorphic second item (RunLoop, WriteRun); recycling is NinoRecycler's per item
NinoEntries.cs               the one read of a set's and a dictionary's entries (NinoEntries: a block scattered into new
                             entries, or value by value through NinoRuns.ReadLoop, filled then linked, or each added
                             through one store, NinoTableAdds, by the gather's read loops), over the collection's own
                             layer (INinoHashTable: SetTable, MapTable; their static Add, which the string kernels'
                             stores call)
NinoCursor.cs                which values the string run kernels move (Own: strings by their formatter's type, pairs by
                             INinoFormatter.HasCursor; Item: a value read as one or a slot's value) and the
                             cursor read of one item through the string kernels' items (ReadAt: a dictionary's merge)
NinoElements.cs              the read policies of array-like formatters (capacity budgets, ReadGrowing, EnsureLength,
                             NewOrSame/Publish; MinWireSize, CopiedWhole, ReadOverwrites from NinoKind), the one-block
                             write and the one-value write and read of a sequence not held in a run
NinoNesting.cs               which types can hold themselves through collections (they count depth per level)
NinoPrefetch.cs              software prefetch for long top-level collections of scattered objects: the gate (formatter,
                             length against the last-level cache, allocation order sampled), the loops in runs, the
                             out-of-line helper per run; Threshold and ReverseReadIsLocal of a formatter's PrefetchBytes
NinoDictionary.cs            the dictionary's top layer: its entries reached and written, read through NinoEntries
                             (pairs holding strings through the string run kernels at the key's and value's offsets
                             in the entry: ReadStrings, FillStrings), or pairs merged one at a time into an existing
                             one; at the bottom its entries are elements of NinoRuns' loops (NinoMapEntryFormatter)
NinoNullables.cs             fixed-stride nullable encoding and the host layout probe
BclCollections.cs            runtime-owned collections using their checked views, gathered: SortedSet's tree, LinkedList's
                             ring (Ring) and its nodes linked as read (Links)
BclDictionaries.cs           runtime-owned concurrent and sorted dictionaries, gathered; SortedList written by one zip
                             (Zip, the interleave kernels as its layer) and read by one split into its two arrays (Split;
                             pairs whose memory is their wire form straight out of the data, SplitInPlace) and
                             published once in order
```

### Internal/Blocks/

```
NinoBlockBytes.cs            a value's bytes matched to its block's, built once per type for the plans
NinoBlockCollections.cs      collections of block values (INinoFormatter.BlockMap): whole-block reads and writes, value
                             by value or through a kernel; the vector paths (.NET 8+) in the files below
NinoBlockCollections.Groups.cs     AVX-512: groups by two-source permutes, aligned loads (Ice Lake), pairs (PerPair)
NinoBlockCollections.Lanes.cs      groups moved as 16-byte lanes (AVX2, SSSE3, Arm64, Wasm), as LanePlan describes
NinoBlockCollections.Permute64.cs  values moved by one 64-byte permute each step (AVX-512 VBMI, Arm64 table lookups)
NinoBlockPlan.cs             NinoBlockPlan<T, TFormatter>: the per-type block plan (the formatter's FixedSize and
                             BlockMap read once), its flags and tables as static
                             fields in initialization order (KernelTables in the netstandard2.1 build)
NinoBlockPlan.Groups.cs      its AVX-512 group tables (Aligned, ReadPlan, GroupReads)
NinoBlockPlan.Lanes.cs       its lane plan (LanePlan)
NinoLanes.cs                 how LanePlan moves a group: shuffles within 16-byte lanes (SSSE3, AVX2, Arm64, Wasm)
```

### Internal/Layout/

```
NinoLayout.cs                canonical (64-bit CoreCLR) layout of raw structs, BCL structs inside them included (tuples,
                             DateTimeOffset, nint): the runtime's probes, Info, padding masks; NinoLayout<T>
NinoLayout.Checks.cs         the fields of a raw type Untrusted mode checks (ValueChecks)
NinoLayout.Convert.cs        conversion field by field where the runtime differs (x86, Mono, IL2CPP)
NinoLayout.Measure.cs        offsets read from an instance (MeasureMemory)
NinoLayoutCore.cs            the struct layout algorithm shared with the generator (RawLayout): NinoLeaf, NinoLayoutModel
                             (one runtime's rules; Canonical), Measure, CoreCLR's auto layout (PlaceAuto)
NinoMask.cs                  NinoMask: padding, and Nullable values without one, cleared while copying values to the wire
                             (vector masks, 8-byte words; CopyCleared: both in one pass with AVX-512 VBMI and GFNI)
ObjectFields.cs              an object's fields at byte offsets
ObjectTypes.cs               mirror-class instances retyped to the runtime's class (also compiled into Nino.Immutable)
ValueViews.cs                where value types' fields are on this runtime (DateTimeOffset: UTC ticks, minutes and their
                             width, found through its public API)
```

### Internal/Simd/

```
NinoSimd.cs                  managed SIMD CPU policy (HasArm64, Prefer512, AMD and Ice Lake) and shared vector helpers
NinoSimd.Text.cs             managed Latin-1 narrowing, widening, and comparison kernels
NinoSimd.Text.Legacy.cs      netstandard2.1 numeric-vector text kernels
NinoSimd.Pairs.cs            packed pair interleave, deinterleave and ordering checks, including the Burst hook
NinoVectors.cs               INinoVectors: the byte-vector operations of each width (128, 256, 512; Vector<byte> in the
                             netstandard2.1 build) that the masked copy (NinoMask) and the flag checks (NinoChecks) share
NinoKernelHook.cs            registered native function pointers and call thresholds for Burst and WebGL
```

### Internal/Validation/

```
NinoChecks.cs                checks of values Nino never writes (bools other than 0 or 1) where each costs least
NinoLimits.cs                the settings read paths check, frozen as static readonly fields
NinoSeededHash.cs            SipHash-1-3 comparers for the tables Nino creates in Untrusted mode
NinoThrow.cs                 throw helpers, out of line (NinoDataException for the data, InvalidOperationException else)
NinoUntrusted.cs             the top-level reads in NinoSettings.Untrusted mode (trailing bytes, value checks)
NinoElementLayouts.cs        NinoElementLayouts<T>: on IL2CPP, arrays Nino creates of a struct whose run-time element is
                             smaller than its compiled sizeof (Pack in generic structs, #378, measured) get spare
                             elements and their length set back, so compiled code never writes past them
NinoValueChecks.cs           the fields of a raw type Untrusted mode checks (bool, DateTime, decimal, DateOnly, TimeOnly,
                             DateTimeOffset, a NinoNullable<T>'s presence and absent value), checked a field at a time
                             over a run of values, as vectors with periodic masks on .NET 8+
```

### Internal/Buffers/

```
NinoScratch.cs               the per-thread scratch buffer of the top-level calls (Rent / Return also for NinoStreams)
NinoSequences.cs             the pooled contiguous copy of a multi-segment ReadOnlySequence<byte> the sequence
                             overloads read from
NinoStreams.cs               the I/O of every Stream overload: writes from the scratch buffer (a pending async write
                             takes its array), reads to the end into a pooled array grown as bytes arrive (bytes
                             already in memory in place: a MemoryStream's exposed buffer, an UnmanagedMemoryStream, a
                             memory-mapped view); INinoStreamReader
NinoThreadCache.cs           helpers for the per-thread caches
NinoGather.cs                the gather of collections that are not one run (trees, linked lists, enumerators): their
                             elements walked into a chunk of Nino's own per-thread buffer (NinoGather<T>: 16 KB chunks,
                             pinned and 64-byte aligned without references, kept for nesting), each chunk written by the
                             run loops (Write, WriteCopied); reads fill a chunk through the read loop and hand it to the
                             collection (Read, ReadAll); INinoGatherSource / INinoGatherTarget, Enumerated, Adds
```

### Internal/Metadata/

```
NinoFormatterBinding.cs      binding of [NinoFormatter] registrations, shared with the generator (INinoTypeSystem)
NinoHash.cs                  FNV-1a behind type ids and member keys (also compiled into the generator)
NinoReflection.cs            what Nino.Core reads by reflection with the generator's rules (NinoType, raw, tracking,
                             a struct field's type as layouts see it: a registered delegate* as UIntPtr)
NinoUnload.cs                hooks that forget an unloaded assembly's types (NinoRegistry.Remove)
```

### Internal/Text/

```
NinoStrings.cs               the default string encoding (header, then Latin-1 or UTF-16 chars), FastAllocateString;
                             UTF-8 ([NinoUtf8]) is in Reader / Writer
NinoStringRuns.cs            the string run kernels (#355): a run of strings, or of pairs holding one, at their offsets
                             in each element, written into the free space with one check per element and read with
                             each string's lengths checked before it is allocated; items not generic over a class
                             (NinoUtf16Item, NinoUtf8Item, NinoValueItem, NinoSlotItem: a raw T?'s single value;
                             a composite's element is NinoItems'), stores (InPlace, a set's and a dictionary's adds)
                             and a set's read (ReadSet, with an add loop of its own where the set guards its buckets)
NinoStrings.Latin1.cs        Latin-1 chars narrowed, widened, appended and compared (the scalar and word paths; the
                             vector kernels are NinoSimd.Text)
```

## Rules for these files

- `Formatters/*.g.cs` are written by `runtimes/csharp/tools/CodeGen` from its templates: change the template and run
  `dotnet run --project runtimes/csharp/tools/CodeGen`; `TupleFormattersGenerationTests` / `MultiDimFormattersGenerationTests` fail
  while a file is stale.
- `NinoHash.cs`, `NinoLayoutCore.cs`, `NinoWire.cs`, `NinoFormat.cs` and `NinoFormatterBinding.cs` are compiled into
  the generator too (netstandard2.0, `NINO_GENERATOR` picks its namespace; `Generator.Build.props`), `ObjectTypes.cs`
  into Nino.Immutable: keep them free of what those builds lack.
- A formatter for a BCL type is a struct declared in `BuiltInFormatters.cs` with `[assembly: NinoFormatter]`, so the
  generator and the runtime resolver bind it the same way; never special-case it in one of them.
- Internals are visible to `Nino.UnitTests` and `Nino.UnityTypes` only (the csproj); other packages go through
  `CompilerServices` (`NinoEffectiveSettings`).
