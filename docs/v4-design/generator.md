# Source generator

Part of [the v4 architecture](../v4-design.md).

## Pipeline

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
* A separate syntax provider finds `NinoSerializer`/`NinoDeserializer` calls that name a type (the type
  argument of `Serialize`, `Deserialize` and `GetFixedSize<T>`, the `typeof` argument of `SerializeBoxed`,
  `DeserializeBoxed` and `IsSupported`) and registers the closed formatters and generic `[NinoType]`s it
  reaches (e.g. `ImmutableArray<T>`, `List<MyStruct>`, `Box<int>`), so AOT runtimes never need
  `MakeGenericType` for them.
* The v3 generator combined everything with `CompilationProvider`, scanned every generic type syntax in
  the project and every `using` directive in every file, and re-emitted all types on every keystroke.

## Top-level calls and interceptors

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
`NinoGen.Interceptors.g.cs` redirects it to `Nino.Core.CompilerServices.NinoDirect`, which calls the formatter struct directly.
`NinoDeserializer.Deserialize<T>(ReadOnlySpan<byte>)` of a raw value is intercepted too, to
`NinoDirect.DeserializeRaw<T>`: one bounds check and one unaligned load with a single return, which the JIT keeps in
registers (the regular method's several returns go through a stack temp).
The `ReadOnlySequence<byte>` overloads are intercepted the same way, to `NinoDirect`'s sequence forms: one segment
is read in place through the span call, several are copied once out of line (`NinoSequences`): to the stack up to
1,024 bytes (`StackBytes`), else into a pooled array.
The `Stream` overloads (and `SerializeAsync` / `DeserializeAsync`) are intercepted to `NinoDirect`'s Stream forms,
which pass the I/O in `NinoStreams` (out of line, the async state machines included) a writer or reader struct that
names the resolved formatter (its `__Top` twin where it has one, and the `...Prefetched` forms for arrays and lists of
prefetching classes): the formatter is called directly, the I/O shared with the regular calls.
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
  (`FixedSize`, and `WriteFixed(in value, ref byte destination)`, which stores each member at a constant offset), and
  `NinoSerializer.Serialize(T)` returning `byte[]` is intercepted to `NinoDirect.SerializeFixed<T, TFormatter>`:
  it allocates the array at its final size and writes into it, with no writer, scratch buffer, growth checks or
  final copy. A null sealed class is its presence byte, `new byte[1]`. The regular call does the same through the
  cached formatter (`NinoFormatterBase<T>.FixedSize`, a constant check in Tier1 code), and
  `NinoSerializer.GetFixedSize<T>()` returns the size (raw values: their size; -1 when not fixed). The bytes are the
  regular writer's. Measured (x64, a 10-member struct of 30 bytes, `Serialize(T)` to `byte[]`): 216 → 108
  instructions per call. The general path still clears its writer twice (the prolog zeroes the address-exposed
  local, then the constructor's `this = default`): the constructor runs after a branch (the buffer type test or the
  scratch rent), outside the entry block where the JIT removes redundant zeroing, and storing each field instead
  measured no better (85/95 → 86/94 x64/ARM64 instructions).
* **Block formatters.** A fixed-size generated struct (above) whose bytes can all be found in its memory (every
  instance field a member, or an auto-property's backing field, each directly assignable, no `[NinoConstructor]`, and
  each member raw and portable, a value tuple or `KeyValuePair` of such items, or another such struct) also implements
  `INinoBlockFormatter<T>`, which extends `INinoFixedSizeFormatter<T>` with `BlockMap()` and `ReadBlock` (the model's
  `TypeModel.Block`, `TypeFacts.IsBlock`), as the tuple and pair formatters of Nino.Core do (their `FixedSize` is -1
  when an item is not raw). One write serves both: a block is the bytes of `WriteFixed`. Arrays, lists and spans of it then take the
  paths of `NinoBlockPlan<T, TFormatter>`: one bounds check for the collection, and where the block map gives every wire
  byte one memory byte (`NinoBlockMap.Runs`) the vector shuffles and permutes (AVX-512 VBMI groups and 64-byte
  permutes, AVX2 or SSSE3 lanes, Arm64 `tbl`, WebAssembly's swizzle; with VBMI, values of more than 16 bytes in
  memory, which its groups and permutes do not take, are written in the AVX2 lane plan's partial groups); elsewhere (no SIMD, a map that overlaps, Mono,
  .NET Framework and the netstandard builds of Nino.Core) the scalar loop of `WriteFixed`/`ReadBlock`, inlined, one
  value after another, unless kernels are registered (below). `BlockMap()` is built once per type from `NinoBlockMap` (`Raw<TItem>(memory, wire)` for a raw
  item, `Block<TItem, TFormatter>` for a nested block, whose map is merged); the memory offset of a field is
  `NinoBlocks.Offset`, of an auto-property's backing field (which generated code cannot name) `NinoBlocks.Find`, which
  stores a marked value through the property. An item with padding, references or a layout other than the canonical
  one fails the map, and the values are then moved one at a time. Nullable members are not blocks: a null value is one
  byte, so they are not of fixed size. The bytes are those of the values written one at a time.
* Reads of blocks keep every check a value's own read makes. The `bool` offsets of a block (`NinoBlockMap`, from its
  `bool` items) are checked over the whole collection before the vector paths move the bytes (`NinoChecks.Bools` with
  the block's stride, out of line, `NinoBlockCollections.CheckBools`): the shuffles copy bytes without looking at them, so a
  `(bool, int)[]` read as blocks accepted a bool byte of 2 on the vector paths and rejected it on the scalar ones.
  With `NinoSettings.Untrusted`, a block with values to check (`NinoLayout<T>.Checks`, a `DateTime` member) is read
  one value at a time through its formatter (`NinoBlockPlan<T, TFormatter>.ReadSize` is -1), as raw structs are.
* **Native AOT.** ILC compiles x64 code for its baseline (SSE2) and emits a later instruction set only inside that
  set's `IsSupported` test, which it compiles to a check at run time for the sets it may use when present (SSSE3 and
  SSE4.1 among them) and to false for the others (AVX and later), unless `IlcInstructionSet` raises the baseline. The plans of
  `NinoBlockPlan<T, TFormatter>` are chosen at run time from `Ssse3.IsSupported`, `Avx2.IsSupported` and so on, so every
  instruction a plan leads to is written as that set's own intrinsic under its own test
  (`NinoBlockPlan<T, TFormatter>.Shuffle`: `Ssse3.Shuffle`, `AdvSimd.Arm64.VectorTableLookup`, `PackedSimd.Swizzle`),
  never as a cross-platform API whose form ILC picks from the baseline: `Vector128.ShuffleNative` was compiled to its
  software form while the plan said SSSE3. The JIT folds each test to a constant, so its code is the same either way.
  `tests/Nino.AotTests` checks that the shuffle is `pshufb` under Native AOT. The AVX2 and AVX-512 paths run there
  only with `IlcInstructionSet` (`x86-64-v3`, `x86-64-v4`, `native`); Arm64's baseline has AdvSimd, so Arm64 takes
  every path.
* **Kernels for the netstandard builds.** The netstandard builds have no hardware intrinsics, so on Unity (Mono,
  IL2CPP, HybridCLR) and .NET Framework the block paths above, the masked copy of raw structs with padding
  (`NinoMask.Copy`) and the narrowing and widening of Latin-1 text (`NinoStrings.NarrowWords`/`WidenWords`) are scalar,
  except where `System.Numerics.Vector<T>` is fast for the operation. The text uses `Vector.Narrow`/`Widen` on .NET
  Framework and .NET Core where the JIT accelerates them (`NinoUnsafe.NumericVectors`), never on Mono: classic Mono's
  JIT (mono/mono 6.x, Unity's fork) accelerates only `Vector<T>`'s own operators, constructors, `Equals` and `CopyTo`,
  and runs `Narrow` and `Widen` in software, over ten times slower than the scalar loops. The masked copy uses loads,
  stores and `&` of `Vector<byte>` there and also on classic Mono with the System.Numerics.Vectors package
  (`NinoUnsafe.VectorMasks`), where that `Vector<T>` is compiled to SSE; never where `Vector<T>` is Mono's own (in
  mscorlib, Unity included), which no Mono JIT accelerates.
  `NinoKernels` (public, netstandard builds only) lets a host register native kernels for them, as a table of
  `delegate* unmanaged[Cdecl]` function pointers with two thresholds: `Permute` (a byte table per value, 0xFF for a
  zero byte; strides 1 to 64), `Interleave32` (a raw `SortedList`'s four-byte keys and values as wire pairs, from 16
  pairs), `NormalizeBoolSlots` (`bool?` slots checked and normalized, from 16 slots when reading and 32 when
  writing), `Narrow`, `Widen`, and for `[NinoUtf8]` strings `NarrowAscii` and `WidenAscii`, which
  move the leading ASCII text and return how much there was (the encoder and the strict decoder do the rest from the
  first other char or byte, which starts a char, so what is accepted and rejected does not change). `Register` copies
  each pointer into a static field of `NinoKernelHook` and sets its threshold (`MinBlocks` values or `MinChars`
  chars; int.MaxValue for a null pointer, which is never called): a call site tests one static int, then loads the
  pointer, tests it for null and calls it (`calli`), with no object and no virtual call. Nino calls them from the paths
  that are already out of line and checks bools before a kernel reads blocks. The tables are built once per type, in
  unmanaged memory. A kernel must write the bytes of its scalar reference (`PermuteScalar`, `Interleave32Scalar`,
  `NormalizeBoolSlotsScalar`, `NarrowScalar`, `WidenScalar`, `NarrowAsciiScalar`, `WidenAsciiScalar`); `KernelHookTests` registers those references through the
  hook ([UnmanagedCallersOnly]). The Unity package registers Burst-compiled kernels (`Nino.Burst`, compiled only with
  `com.unity.burst` installed): SSSE3 on x86 and x64, Neon on AArch64, Burst's function pointers handed over as they
  are; WebGL players built with SIMD register the same kernels in C (`Nino.WebGL`, `nino_wasm.c`, whose
  `nino_wasm_kernels` hands out their pointers once). Armv7 and IL2CPP or Mono players without Burst register none
  and keep the scalar paths. A player's kernels are compiled ahead of time for the CPU targets in Burst's AOT settings
  (on x64 by default SSE2 and SSE4, chosen at startup), so each intrinsic sits under its own `IsXxxSupported` test
  (Burst error BC1200 otherwise); a CPU without SSSE3 registers none.
  The .NET 8 and 10 builds have no hook (`#if !NET8_0_OR_GREATER`).
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
  around every call. The regular calls do the same through `NinoFormatterBase<T>.Write` (the cached formatter of a type
  with a twin is a `TopStructFormatter` registered by `NinoTypes.RegisterTop`). Measured (x64, a
  version-tolerant class of five members, two of them inherited, `Serialize(T)` to `byte[]`): 183 → 121
  instructions per call (with the merged region below). netstandard2.0 (span and position) takes the same route.
  The top-level reads of a new value (`Deserialize<T>(data)`) do the same through `INinoTopFormatter<T>.Read(data)`
  (`NinoDirect.DeserializeTop`, `NinoFormatterBase<T>.Read`): a twin whose body is not inlined reads in `__ReadTop`,
  which creates the reader and calls the body that reads a new instance, an inlinable method (`__ReadBodyInline`, or
  `__ReadBodyFreshInline`) that the out-of-line body also calls on its local copy of the reader. Measured (x64, the
  same class, `Deserialize<T>(byte[])`): 220 → 139 instructions per call. Pooled types keep the regular route
  (their new value comes from the pool first). A null value is handled in the inlined `Write` and `Read` themselves
  (`NinoDirect.WriteNull`, a presence byte 0 returned as null), so neither creates a writer or reader out of line
  for it; `__ReadTop` is static (an instance method out of line takes the twin's address).
* A version-tolerant body whose first members are raw values (written by the body itself, not by a base's
  `WriteMembers`) reserves the region's length and those members together (`Writer.BeginRegion(size, out
  lengthPosition)`): one capacity check instead of two.
* Body formatters report whether they open class bodies and can be read later in one `Traits` property
  (`NinoBodyTraits.Nests | NinoBodyTraits.Deferrable`, constants the JIT folds), not two, and the registration of
  each concrete type is one call emitted straight into the assembly's registry
  (`NinoTypes.Register`/`RegisterFixed`/`RegisterTop`/`RegisterTracked<T, TFormatter>(id)`, which publishes the
  formatter and the subtype handler; an abstract type or interface `NinoFormatterCache<T>.TryRegister<TFormatter>()`)
  instead of a `__Register()` method per formatter called from it: fewer members per type for the compiler to bind and
  emit (see `v4-benchmarks.md`, compile time). Each `NinoTypes` method builds the wrapper and the handler itself and
  calls nothing generic over the formatter, so one method per type is compiled at startup instead of two; the
  registry's source is about a third smaller.

## The API generated code calls

Generated formatters call Nino.Core directly: `Writer`/`Reader`, the built-in formatter structs, `NinoObject`
(class headers and subtype dispatch), `NinoDirect` (intercepted top-level calls), `NinoPolymorphic`, `NinoRegistry`,
`NinoFormatterCache`, `NinoActivator`, `NinoRecycler` and `NinoUnsafe`. The types that exist only for generated code
(and Nino's own packages: Nino.Immutable, Nino.UnityTypes) live in the namespace `Nino.Core.CompilerServices`, folder
`CompilerServices/`: `NinoObject`, `NinoDirect`, `NinoUnsafe`, `INinoBodyFormatter<T>`, `INinoTopFormatter<T>`,
`INinoRawBodyFormatter<T>`, `INinoFixedSizeFormatter<T>`, `INinoBlockFormatter<T>` with `NinoBlockMap` and `NinoBlocks`,
`INinoRawFormatter`, `NinoBodyTraits`, `NinoTypes` (the registry's one call per concrete type), `NinoSubtypes<TBase>`
and the `NinoSubtypeHandler` it returns, `[NinoGenerated]`, `INinoRegistry` and `NinoEffectiveSettings`. The name
says who calls them: user code never needs a `using` for it, generated code names them with `global::`, and a
namespace of their own keeps them out of completion lists and away from user types of the same name (Nino.AspNetCore's
`NinoBody<T>`). `Nino.Core.Internal` is a different thing: types that are not public at all. The members of public
types that exist only for generated code (`NinoRegistry.Add`,
`NinoFormatterCache<T>.TryRegister<TFormatter>`, `NinoActivator<T>.TryRegister`, the region,
reference and unchecked value members of `Writer`/`Reader`, the pool's per-thread stack) stay where they are, and
all of them, with the types above, are hidden from IntelliSense (`[EditorBrowsable(Never)]`); they stay public
because generated code lives in the user's assembly. There is deliberately no forwarding facade in front of them:
every call it added is a real call under Mono's interpreter and in HybridCLR hot-update code, which do not inline.

## Sources shared with the runtime

Rules that must agree between compile time and run time live once, in Nino.Core, and are compiled into the
generator as well (linked files, `NINO_GENERATOR` switches their namespace):

* `Internal/Metadata/NinoHash.cs`: the FNV-1a hash of type ids and keyed member ids.
* `IO/NinoWire.cs`: the wire constants generated code writes (null type id, length and key sizes...).
* `Internal/Layout/NinoLayoutCore.cs`: the struct layout algorithm (leaves, alignment caps, `Pack`, `Size`, explicit
  offsets). The runtime runs it over `System.Type` to get the wire layout and this runtime's memory layout; the
  generator runs it over `ITypeSymbol` under the 64-bit and 32-bit models to decide whether a struct's memory is
  the same everywhere (and so can be written inline as raw bytes). Each side supplies a small view of its type
  system (`INinoLayoutView<TType>`).
* `Internal/Metadata/NinoFormatterBinding.cs`: how a formatter rule binds to a type (unify the rule's pattern with the
  type or one of its supertypes, check the formatter's type parameter constraints, substitute). The runtime
  resolver runs it over `System.Type`, the generator over `ITypeSymbol` (`INinoTypeSystem<TType>`), so a type
  resolves to the same formatter whichever side picks it.

## Source layout

* Nino.Core: `NinoSerializer`/`NinoDeserializer`, `NinoSettings` and `NinoDataException` at the root;
  `Attributes/` (everything users put on types, members and formatter type parameters), `IO/` (`Writer`,
  `Reader`, buffers, wire constants, depth bounds, deferred bodies and the per-call contexts), `Formatters/` (the
  formatter interface, cache, resolver and every built-in formatter), `Polymorphism/` (type ids, registry, subtype
  dispatch, reference tables), `Pooling/` (`NinoPool`, `NinoRecycler`, `NinoActivator`), `CompilerServices/` (the API only
  generated code calls, namespace `Nino.Core.CompilerServices`, see above) and `Internal/` (layout, limits, untrusted
  reads, thread caches, the shared sources above). User code needs one `using Nino.Core`.
* Nino.Generator: `Analysis/` (analyzer, suppressor, diagnostics), `Common/` (equatable collections, names and
  `[NinoType]` lookup shared by the analyzer, generator and code fixes), `Generation/` (symbols to models) and
  `Generation/Emit/` (models to source).

## Analyzer, suppressor and code fixes

* `NinoAnalyzer` reports every diagnostic ([docs/diagnostics.md](../diagnostics.md)): attribute misuse, and
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

## Generated code per type

* `struct <Name>Formatter : INinoFormatter<T>` with `Serialize`, `Deserialize`, `WriteBody`, `ReadBody`.
* Generic `[NinoType]`s produce generic formatters (one per definition, not one per closed type) that take the
  formatter of each type parameter too: `Box<T>`'s is `Box_1Formatter<T, __F_T> where __F_T : struct,
  INinoFormatter<T>`, and a member of type `T` calls `default(__F_T)`. A closed type names its arguments' formatters
  (`Box_1Formatter<int, UnmanagedFormatter<int>>`), chosen as for a member of that type, so every member is a direct
  call on every runtime (`CachedFormatter<T>` is a virtual call that only RyuJIT devirtualizes); the runtime resolver
  binds them the same way for closed types the generator did not see.
* Private members use `[UnsafeAccessor]` when the compilation targets .NET 8+ (generic types: .NET 9+, declared
  in a class next to the generic formatter, `__NinoAccessors_<Formatter><T...>`, whose type parameters are exactly
  the type's, as the runtime requires; a member of a generic base qualifies when the
  base's type arguments are the type's first type parameters in order), and generated `partial` accessors
  otherwise. The form is chosen from the compilation's preprocessor symbols (`NET8_0_OR_GREATER`,
  `NET9_0_OR_GREATER`); the output has no `#if`.
* Classes whose members are all directly accessible also get static `WriteMembers`/`ReadMembers`. A subtype
  whose leading members are exactly its base's members calls the base's methods instead of repeating them, so
  each inherited member is compiled once per hierarchy rather than once per subtype (about a third less code for
  deep hierarchies). The bytes are unchanged. Delegation is skipped when raw members on both sides of the
  boundary would otherwise be written as one block, since splitting that block costs an extra reservation.
* `[assembly: NinoTypeLayout(typeof(T), format, header, trackReferences, raw, typeId, "members", Deferrable = ...)]`
  publishes what the formatter writes (issue #197): whether a body nested past `NinoDepth.Inline` is deferred (the
  formatter's `NinoBodyTraits.Deferrable` trait, which readers in other languages need), and each member as
  `name:type:encoding` (keyed members add `:key=0x...`).
  The encoding describes the bytes, not the formatter class (`WireEncoding`, computed by `FormatterResolver` next
  to each formatter): `string`, `utf8`, `object`, `type(T)` for a `[NinoType]`, `nullable(E)`, `seq(E)`,
  `map(K,V)`, `pair(K,V)`, `tuple(E,...)`, `grid(rank,E)`, a few named BCL encodings (`datetimeoffset`, `bigint`,
  ...) and `formatter(F)` for a custom or third-party formatter, so a tool such as `nino-gen` ([codegen.md](../codegen.md))
  reads and writes the bytes without knowing Nino.Core's formatters. A value copied as raw memory is its canonical layout
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
* Preprocessor-dependent choices (`NET8_0_OR_GREATER`, `NET9_0_OR_GREATER`) are resolved at
  generation time from the compilation's parse options, so generated code contains no `#if` branches.

For a hierarchy of 220 types (20 roots × depth 8), v3 generated about 911,000 lines (47 MB) because every
polymorphic base inlined the full body of every subtype twice. v4 emits each body once.
