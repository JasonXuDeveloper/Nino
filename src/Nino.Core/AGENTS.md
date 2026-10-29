# Nino.Core: file map

> Read when: reading or changing anything in `src/Nino.Core` (the runtime: the public API, formatters, Reader/Writer, settings, polymorphism, pooling, the internal fast paths).

The runtime every generated formatter calls, packed as `Nino.Serialization`; netstandard2.0, netstandard2.1 (Unity,
.NET Framework: no hardware intrinsics), net8.0 and net10.0 (trimmable, NativeAOT compatible). The root CLAUDE.md's
"Machine code" and "Security" sections apply to every read and write path here. `audit-core` audits this directory,
`audit-security` its read paths, `optimize` their machine code.

When you add, rename or delete a file here, update its line (`tools/agents.sh --check` fails on a `.cs` file it does
not find named here).

## Root

```
NinoSerializer.cs            the public Serialize<T> / SerializeBoxed (to a byte[], an IBufferWriter<byte> or a Writer),
                             GetFixedSize<T>, IsSupported; byte[] results go through the scratch buffer (NinoScratch)
NinoDeserializer.cs          the public Deserialize<T> (a new value, or into an existing one by ref) / DeserializeBoxed,
                             from a span or a Reader
NinoSettings.cs              every process-wide setting (MaxDepth, MaxCollectionLength, MaxHashCollisions, Untrusted,
                             pool sizes, SetRecycler<T>), plain fields frozen on first use (NinoLimits)
NinoDataException.cs         the exception every check of the data throws (an InvalidOperationException)
NinoKernels.cs               NinoKernels: native Permute / Narrow / Widen kernels the netstandard builds call when one is
                             registered (Unity's Burst kernels), with their scalar forms; NinoKernelHook holds them
Properties.cs                [module: SkipLocalsInit]
```

## Attributes/

```
NinoTypeAttribute.cs         [NinoType]: marks a type for the generator (autoCollect, containNonPublicMembers,
                             allowInheritance, format, trackReferences)
NinoFormat.cs                NinoFormat: Positional, VersionTolerant, Keyed (also compiled into the generator)
NinoMemberAttribute.cs       [NinoMember(index)]: selects and orders members under [NinoType(false)]
NinoIgnoreAttribute.cs       [NinoIgnore]: skips a member a [NinoType] collects
NinoUtf8Attribute.cs         [NinoUtf8]: a string member written as UTF-8
NinoFormerNameAttribute.cs   [NinoFormerName]: a type's former name (its type id) or a keyed member's former names
NinoConstructorAttribute.cs  [NinoConstructor]: the constructor deserialization uses
NinoFactoryAttribute.cs      [NinoFactory]: a static method Nino calls to create an instance (a pool, ...)
NinoPooledAttribute.cs       [NinoPooled]: the generator adds ReturnToPool() and pool hooks to a sealed class
NinoCustomFormatterAttribute.cs  [NinoCustomFormatter(typeof(F))]: a member written by a formatter of the user's
NinoFormatterAttribute.cs    [assembly: NinoFormatter(typeof(F<>))]: registers a struct formatter for the generator and
                             the runtime resolver alike (rules shared through NinoFormatterBinding)
NinoCreatedAttribute.cs      [NinoCreated] on a formatter's type parameter: the formatter creates it (NinoActivator)
NinoRawAttribute.cs          [NinoRaw] on a formatter's type parameter: bound only to types written as raw bytes
NinoTypeLayoutAttribute.cs   [assembly: NinoTypeLayout]: the published wire layout of a [NinoType] (read by nino-gen,
                             the corpus); NinoHeader
```

## CompilerServices/ (public, for generated code)

```
FormatterInterfaces.cs       INinoBodyFormatter<T> (+ NinoBody flags), INinoRawBodyFormatter<T>, INinoTopFormatter<T>
                             (__Top twins), INinoFixedSizeFormatter<T>, INinoBlockFormatter<T>, INinoRawFormatter
NinoObject.cs                header handling of generated class formatters: null flag / type id / reference header, then
                             the body; the ...Raw forms write header and raw body into one reservation
NinoDirect.cs                the top-level calls with the formatter the generator resolved (what interceptors call)
NinoBlockMap.cs              NinoBlockMap (where a block's bytes lie in a value's memory) and NinoBlocks (constrained
                             calls on block formatters)
NinoPolymorphicOf.cs         NinoPolymorphic<TBase>: the registry of a base type's subtypes by type id
NinoRegistration.cs          [NinoGenerated] on an assembly and INinoRegistry (lazy registration by type id)
NinoSubtypeHandler.cs        NinoSubtypeHandler: writes and reads one registered type; the registry's entry
NinoEffectiveSettings.cs     the frozen settings, for Nino's own packages (Nino.Immutable)
NinoUnsafe.cs                unaligned little-endian memory access, block copies (vector loops, paired loads),
                             ReadBool, array allocation; used by Reader, Writer and generated code
```

## Formatters/

```
NinoFormatter.cs             INinoFormatter<T>; NinoFormatter<T> and its struct wrappers (StructFormatter,
                             FixedStructFormatter, TopStructFormatter, NestingFormatter), RawFormatter,
                             INinoSizedFormatter / NinoSized, CachedFormatter<T>
NinoFormatterCache.cs        NinoFormatterCache<T>: the formatter of T (registered by generated code, else resolved);
                             NinoRaw<T>, NinoTopFormatter<T>, NinoRawArray<T> as static readonly fields;
                             NinoMissingFormatter<T> (the never-null stand-in of a type without one)
NinoFormatterResolver.cs     builds formatter types by reflection for unregistered types, applying the
                             [NinoFormatter] registrations with the generator's rules
BuiltInFormatters.cs         the [assembly: NinoFormatter] table of the BCL formatters below (generator and resolver read it)
BoxedFormatter.cs            BoxedFormatter: NinoFormatterCache<T> for a runtime Type (the Boxed APIs)
PrimitiveFormatters.cs       UnmanagedFormatter<T> (raw bytes), RawValueFormatter<T>, IntPtr / UIntPtr (8 bytes),
                             DateTimeOffset, String (Latin-1 or UTF-16), Utf8String, Nullable, ObjectFormatter
ArrayFormatters.cs           T[] (general, unmanaged block, raw T?[] at a fixed stride), ArraySegment; NinoNullables,
                             NullableLayout, ArrayShape / MultiDim
MultiDimFormatters.g.cs      T[,], T[,,], T[,,,] (generated by tools/CodeGen; do not edit)
TupleFormatters.g.cs         ValueTuple and Tuple of 1 to 8 items (generated by tools/CodeGen; do not edit)
ListFormatters.cs            List<T> and derived (general, unmanaged block, raw T? stride)
CollectionFormatters.cs      ICollection<T>, HashSet, Queue, Stack, IProducerConsumerCollection (Concurrent*);
                             NinoCollectionViews (reads into a Stack's or Queue's backing array)
DictionaryFormatters.cs      Dictionary and derived, any IDictionary, dictionary interfaces, KeyValuePair
InterfaceFormatters.cs       IEnumerable/IList/ICollection/IReadOnly* (read as List<T>), ISet/IReadOnlySet (HashSet<T>)
BclCollectionPaths.cs        BclCollections / BclDictionaries: the runtime's own collections through their methods or
                             fields (CollectionViews), with the general path's bytes; ReadLinked (LinkedList ring)
BclTypeFormatters.cs         BigInteger, Uri, Version, StringBuilder, BitArray, CultureInfo, TimeZoneInfo, Lazy,
                             Memory / ReadOnlyMemory / ReadOnlySequence, IGrouping, ILookup, BlockingCollection
PriorityQueueFormatter.cs    PriorityQueue (net8.0 and net10.0 builds only)
```

## IO/

```
Reader.cs                    ref struct Reader: bounds-checked reads, lengths checked before allocating, depth,
                             regions and deferred sections, strings
Writer.cs                    ref struct Writer: writes into a NinoArrayBufferWriter (Flush when done)
NinoArrayBufferWriter.cs     the pooled, growable IBufferWriter<byte> Writer writes into
NinoWire.cs                  the wire constants (null length, presence bytes, headers; also compiled into the generator)
NinoDepth.cs                 depth bounds: bodies past Inline (128) open bodies are deferred; the limit is MaxDepth
NinoDeferral.cs              deferred bodies of one call (NinoDeferredBody<T, TFormatter>, NinoDeferrals)
NinoCallContext.cs           NinoReadContext / NinoWriteContext: back references and deferrals of one call, rented per thread
```

## Polymorphism/

```
NinoPolymorphic.cs           NinoPolymorphic.Register; SubtypeHandler / TrackedSubtypeHandler
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
                             and the ownership rules of a formatter's Deserialize(ref T)
```

## Internal/

```
NinoThrow.cs                 throw helpers, out of line (NinoDataException for the data, InvalidOperationException else)
NinoLimits.cs                the settings read paths check, frozen as static readonly fields
NinoUntrusted.cs             the top-level reads in NinoSettings.Untrusted mode (trailing bytes, value checks)
NinoChecks.cs                checks of values Nino never writes (bools other than 0 or 1) where each costs least
NinoSeededHash.cs            SipHash-1-3 comparers for the tables Nino creates in Untrusted mode
HashTables.cs                fills new HashSet / Dictionary entries and buckets directly, counting collisions
                             against MaxHashCollisions (Plan, TryFill, Insert, Added, Walk)
ConcurrentTables.cs          the same for ConcurrentDictionary (mirror classes per runtime, Builder)
NinoNesting.cs               which types can hold themselves through collections (they count depth per level)
NinoHash.cs                  FNV-1a behind type ids and member keys (also compiled into the generator)
NinoLayout.cs                canonical (64-bit CoreCLR) layout of raw structs, padding masks, conversion where the
                             runtime differs (x86, Mono, IL2CPP); NinoLayout<T>, NinoMask
NinoLayoutCore.cs            the struct layout algorithm shared with the generator (RawLayout)
NinoReflection.cs            what Nino.Core reads by reflection with the generator's rules (NinoType, raw, tracking)
NinoFormatterBinding.cs      binding of [NinoFormatter] registrations, shared with the generator (INinoTypeSystem)
NinoElements.cs              shared element loops of array-like formatters, capacity budgets (HeavyElements, HeavyHashed)
NinoCollections.cs           shared collection helpers (rented reads, pair loops, enumerable writes)
NinoBlock.cs                 collections of INinoBlockFormatter values: whole-block reads and writes, vector groups
NinoBlockPlan.cs             NinoBlock<T, TFormatter>: the per-type block plan (ReadPlan, LanePlan, KernelTables)
NinoBlockBytes.cs            a value's bytes matched to its block's, built once per type for the plans
NinoLanes.cs                 how LanePlan moves a group: shuffles within 16-byte lanes (SSSE3, AVX2, Arm64, Wasm)
NinoStrings.cs               the default string encoding (header, then Latin-1 or UTF-16 chars), FastAllocateString;
                             UTF-8 ([NinoUtf8]) is in Reader / Writer
NinoScratch.cs               the per-thread scratch buffer of the top-level calls
NinoThreadCache.cs           helpers for the per-thread caches
NinoUnload.cs                hooks that forget an unloaded assembly's types (NinoRegistry.Remove)
CollectionViews.cs           field layouts of List, Stack, Queue, LinkedList, HashSet, Dictionary, SortedList, SortedSet,
                             SortedDictionary per runtime, checked at startup against known contents
CollectionFields.cs          those fields at the offsets CollectionViews found (SetEntry, MapEntry, LinkedNode)
ListAccess.cs                a List<T>'s elements as a span (CollectionsMarshal, its fields, or copies)
ObjectFields.cs              an object's fields at byte offsets
ObjectTypes.cs               mirror-class instances retyped to the runtime's class (also compiled into Nino.Immutable)
SortedSetNodes.cs            SortedSet<T> red-black trees built in one pass (SortedSetTrees, node layouts)
ValueViews.cs                where value types' fields are on this runtime (DateTimeOffset)
```

## Rules for these files

- `Formatters/*.g.cs` are written by `tools/CodeGen` from its templates: change the template and run
  `dotnet run --project tools/CodeGen`; `TupleFormattersGenerationTests` / `MultiDimFormattersGenerationTests` fail
  while a file is stale.
- `NinoHash.cs`, `NinoLayoutCore.cs`, `NinoWire.cs`, `NinoFormat.cs` and `NinoFormatterBinding.cs` are compiled into
  the generator too (netstandard2.0, `NINO_GENERATOR` picks its namespace; `Generator.Build.props`), `ObjectTypes.cs`
  into Nino.Immutable: keep them free of what those builds lack.
- A formatter for a BCL type is a struct declared in `BuiltInFormatters.cs` with `[assembly: NinoFormatter]`, so the
  generator and the runtime resolver bind it the same way; never special-case it in one of them.
- Internals are visible to `Nino.UnitTests` and `Nino.UnityTypes` only (the csproj); other packages go through
  `CompilerServices` (`NinoEffectiveSettings`).
