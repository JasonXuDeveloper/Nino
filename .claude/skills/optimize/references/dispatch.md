# Direct calls instead of dispatch (.NET, NativeAOT, IL2CPP, Mono)

Part of the `optimize` skill. The owner's rule is no virtual dispatch on a serialize or deserialize path where a
direct form exists; this file holds how the JIT devirtualizes, the inventory of indirect calls left and why each
stays, and what was measured. Read it when a listing shows `call [rax+0x..]`, `call [r11]` or `call rax`, or when
adding a formatter lookup, a delegate or an interface call to a hot path. Go, Rust and TypeScript:
`optimize-runtimes`, references/dispatch.md.

- Keep the existing `IBufferWriter<byte>` overload for reference-type writers. A plain
  interface constraint accepts both classes and structs, but a shared reference-type
  instantiation still keeps interface dispatch. The actual raw-int serializer and the
  monomorphic interface probe already guarded and inlined `ArrayBufferWriter<byte>`
  in Tier 1. Both generic class probes still had interface fallback calls on Arm64 and
  in the x64 guest. Four M4 rounds with one concrete class took 2.23–2.41 ns through
  the interface, 2.77–2.90 ns through the generic ref path and 2.72–2.74 ns through
  the generic value path. The mixed receiver probe varied. If a mutable struct writer needs a typed entry, carry it by `ref`
  through the calls that advance it. The probe's constrained struct path inlined both
  methods and allocated zero bytes. Converting the struct to an interface every call
  allocated 32 bytes, while a generic by-value call updated only the copy's position.
  Prototype this at the output boundary: Nino's own writer already bypasses interface
  calls, and external writers receive a scratch copy for general values. Any public
  two-type-parameter overload also needs source generator coverage because interception
  currently requires one type argument.
  Compare a borrowed struct cursor with one existing `Writer` per batch before moving
  the pooled array out of `NinoArrayBufferWriter`. A probe kept the rent and return in a
  separate lease and advanced a struct cursor by reference. Four M4 rounds at 1,024
  raw ints took 0.57–0.60 ns/int with direct Nino owner writes, 0.27–0.30 with one existing
  `Writer` and one flush, 1.03–1.14 with the borrowed array and index, and 0.76–0.87 through
  its constrained interface. The borrowed struct did not beat the existing writer at
  eight or 64 items either. A single int was within local variation. Steady allocations
  were zero for every path, and grow-from-empty and reuse produced the same bytes.
  The pooled lease needs one active borrower because growth replaces its array. The
  useful distinction in this probe is the existing byref cursor and the flush boundary.

- Call a formatter known only at run time through a `static readonly` field read directly, never through
  `field ?? fallback` or a local that merges two sources: Tier1 knows the exact class of an initialized class's
  `static readonly` object and calls (and inlines) its overrides, but a merge loses it, and the JIT then loads the
  field, compares its method table and keeps a virtual call behind the compare. Keep such a field never null (a
  stand-in object whose calls report the error, `NinoMissingFormatter<T>`) instead of checking it. Evidence:
  `NinoTopFormatter<T>.Get` was `Formatter ?? NinoFormatterCache<T>.Instance`; every `CachedFormatter<T>` call (24
  sites in the benchmark, the `ProbeBox<T>` subtypes) was such a guard and virtual call, none after (6d711cb:
  GenericUnmanagedTupleBase deserializes in 0.86-0.94 of the time on EPYC 7763, 9V45, 9V74 and Neoverse-N2, floor
  0.99-1.01); a
  scratch program shows the same for any `static readonly` base-class field read with `??`. Find them with
  `call [rax+0x..]Nino...:Serialize` after a `cmp qword ptr [rdi], <method table>` in a Tier1 listing.
- No virtual dispatch on a serialize or deserialize path where a direct form exists (the owner's rule): formatters are
  structs passed as constrained type arguments, generated code names them directly, collections test their concrete
  types (`List<T>`, `T[]`) before their interfaces, and the one indirect call a polymorphic or user-extensible point
  needs (an unknown subtype through `NinoSubtypeHandler`, a recycler or `[NinoFactory]` delegate, a user collection's
  enumerator) sits behind the generated exact-type checks, out of line. What is left in the Tier1 listings and why is
  the inventory of the 6d711cb round: subtype handlers after the exact-type dispatch (cold), `NinoActivator<T>`
  delegates for user-derived collection types only, BCL internals (`ArrayPool`, string comparers inside `Dictionary`),
  calls through a shared generic's dictionary (`T` a class: `CORINFO_HELP_RUNTIMEHANDLE_METHOD` then `call rax`), and
  the enumerator of a collection declared as an interface whose object is neither a list nor an array. `SortedSet`,
  `LinkedList`, `SortedDictionary`, `SortedList` and `ConcurrentBag` read into null are created by the activator's
  delegate (`Func<T>.Invoke` once per collection; `HashSet`, `Queue`, `Stack`, `List` and `Dictionary` test their type
  and call `new`). Creating them with `new` behind `typeof(TCollection) == typeof(SortedSet<T>)` (`Unsafe.As` back,
  since a cast is a `CORINFO_HELP_CHKCASTANY` call in shared code; 4a7fc8e0) was measured and not landed: the sorted
  and concurrent reads were on par, but 1,000-node `LinkedList<int>` reads, about 8 us of allocation, took 1.023 and
  1.027 times as long on EPYC 7763 in two runs (n=48 each, floors 0.995 and 0.994) and 1.022 and 1.012 on 9V45 (floors
  1.009 and 0.984): .NET A/B runs 41211412057 and 41216990263. The one delegate call per collection is worth less than
  that; try again only with the creation out of the method that reads the elements. Under NativeAOT, IL2CPP and Mono a
  `static readonly` field is a load, not a constant (ILC does not preinitialize `NinoTopFormatter<T>.Formatter`: its
  initializer runs the registry), so the call through it is virtual. The round 104 inventory beyond the JIT's
  (NativeAOT listings of the whole suite, SKILL.md "Reading the listings"; IL2CPP C++ of unity.yml run 40969941689): per value,
  (1) `CachedFormatter<T>` of a `[NinoType]` generated in another assembly: a virtual call on
  NativeAOT, a `VirtualActionInvoker` on IL2CPP, a virtual call on Mono; locally under NativeAOT 1,000 classes of
  another assembly as an array member serialized in 1.5 times and read in 1.37 times the time of the same class
  declared in the calling assembly, on par under the JIT (local numbers, to choose only). The direct form names the
  other assembly's formatter, which is `internal`. The owner decided to keep it (round 104): `InternalsVisibleTo`
  needs the referenced assembly to name its referencers, so it helps only one author's assemblies, never a library;
  public formatters would add generated types to every user's public API; and nothing reaches a type from an
  assembly loaded at run time (HybridCLR hot updates), which only polymorphism or the registry sees. The docs site's
  build configuration page advises declaring heavily serialized types in the serializing assembly instead. The benchmark suite has no such member (its DLL
  names `CachedFormatter` nowhere). A collection that holds itself without a `[NinoType]` body (`SelfTree : List<SelfTree>`,
  `Map : Dictionary<string, Map>`) was in this row too and is fixed: the generator writes a struct per such type
  (`NinoGen.Nesting.g.cs`, `FormatterResolver.GeneratedNestingFormatter`: `EnterStruct`, the type's formatter, `ExitStruct`),
  and that formatter's elements name the struct, since a named struct can name itself where a type argument cannot.
  `CachedFormatter<T>` was not devirtualized by the JIT there either: `T` is a class, so
  `ListFormatter<__Canon, __Canon, CachedFormatter<__Canon>>` is shared code everywhere, a dictionary call per
  element (`call [r12-2]`), the generic static base helper and a virtual jump, and `RawFormatter.Is` /
  `FixedSize` stayed calls per list; NativeAOT's level went from 826 bytes with four indirect calls to one 309-byte
  method calling itself directly. On runners (.NET A/B run 41253800850, `CollectionsTest.*SelfTree`, 5,461
  lists, head 70fe1544 against its bench base, base-vs-base floors 0.99-1.01, MemoryPack's rows 0.99-1.01 as the null
  control) serialize took 0.35 (EPYC 7763, n=36) to 0.42 (Xeon 8573C) of the time and deserialize 0.52 (Neoverse-N2)
  to 0.58 (EPYC 9V74), the JIT included. (2) Shared generic code where a formatter's type argument holds a class
  (`NinoElements.ReadEach` over `Box<(class, int)>`): the element's `Deserialize` is found in the generic dictionary,
  a fat-pointer call per element on NativeAOT, `call rax` at Tier1, `il2cpp_rgctx_method` on IL2CPP: no direct form.
  (3) Gone (#346): the netstandard builds' block loops called each value through `NinoBlockPlan.Formatter` (a boxed
  `INinoBlockFormatter`), and slots through `NinoSlots.Formatter`; the block and slot members are now members of
  `INinoFormatter<T>` with defaults reporting none, read as queries once per type, so each value is a constrained call
  on the struct on every target. The element and pair loops of polymorphic bases and the flat-body test followed
  (#346 step 2): `WriteElements` and `BodyTraits` are members of `INinoFormatter<T>`, `HasElementLoop`, `HasPairLoop`
  and the pair writes of the non-generic `INinoFormatter` it extends, where the netstandard builds called a boxed
  `INinoElementsFormatter<T>` field per collection and the .NET 8+ builds tested `is INinoElementsFormatter<T>` and
  `is INinoPairsFormatter`. Step 3 moved the rest a formatter's struct type parameter reaches: the prefetch of long
  collections (`PrefetchBytes`, `HasMemberPrefetch`, `PrefetchMembers` on `INinoFormatter`, read by
  `NinoPrefetch.Of<TFormatter>`; `Ahead` had cast `default(TFormatter)` to `INinoPrefetchFormatter` per run) and the
  sized and external top-level writes (`NinoTopWrites<TFormatter>`; the netstandard builds had called a boxed
  `INinoSizedFormatter<T>` / `INinoExternalFormatter<T>` field per top-level call). What stays is not a formatter type
  parameter: `INinoRawArrayFormatter` (below) and the marker `INinoRawFormatter`, tested as a type. (4) The
  user-extensible points
  above. Per top-level call, not per value: calls the interceptors leave alone (generic methods, another assembly's
  types, Unity's Roslyn 4.3, raw values and arrays of raw elements) read `NinoTopFormatter<T>`, `NinoRaw<T>` and
  `NinoRawArray<T>` as loads, so an array of raw elements is one `INinoRawArrayFormatter` interface call per call
  under NativeAOT, and the dead virtual branches stay in the code; generic `[NinoType]`s use no `CachedFormatter`
  (Unity kernels and IL2CPP).
- Split exact BCL collection paths from interface snapshots when a concrete API does the same work. Test the
  runtime type before calling the concrete API so a subclass's interface implementation still runs. In
  `ProducerConsumerFormatter.Serialize` (afc6ee93), the exact `ConcurrentQueue<T>` fixed-width path calls
  `ConcurrentQueue<T>.ToArray` directly and keeps the other snapshots out of line. On local Arm64 FullOpts,
  the variable-width queue entry and its helper together went from 2,340 to 1,336 bytes. Fixed-width queues
  still allocate the bulk snapshot.
- Reuse concurrent queue construction for blocking collection reads. Rent the temporary array for the
  bulk path, and construct empty and single-item queues directly. Renting for a single item made the
  raw case slower and allocated 8 B more. The direct path recovered the local single-int timing and
  saved 32 B. The 1,024-item int case saved 4,080 B and was faster or within noise in each process-per-build
  round. At one item the Arm64 Tier 1 caller and helper totaled 1,216 bytes against 1,200 before, while
  the x64 cross listing had a smaller caller with the work moved into a helper. Check tiering before
  interpreting the reference case: the initial string base listing did not reach Tier 1.
  For two to four items, directly enqueuing individual reads or batching into an inline array saved
  40 B for ints and 72 B for strings. The inline array restored one bulk read, but two-string reads
  still slowed in every local round, and the enqueue path still uses synchronization per element.
  Keep the measured empty and single-item path and the existing bulk constructor for larger reads.
- Clear the full rented reference array on return from concurrent collection construction. Clearing only
  the used prefix left a sentinel from an earlier renter in the unused suffix of the same shared buffer.
  The 65-item ownership probe returned a clean prefix in both builds and a clean suffix only with
  full clearing. The prefix variant had no repeatable throughput gain for string collections at 65,
  513, 1,024 or 4,097 items. Its Arm64 Tier 1 constructor helper grew from 2,616 to 3,792 bytes.
- Refill exact concurrent stacks through `TryPop` and `Push`, and use one `Clear` when no old item needs
  recycling. Bind stacks to their own formatter and leave subclasses on their interface (62d85a1b).
  Adding the stack branch to the shared producer/consumer method changed the queue's inlining budget. The split
  restored its original IL and normalized Arm64 FullOpts listing.
  Local Arm64 Tier 1 used one release store for the clear, with the read and push inlined. Time each collection
  in its own process and verify Tier 1 in the listing before comparing: the first run warmed the old interface
  loop but was still timing instrumented Tier 0 in the new helper. The all-collection constrained-adapter
  prototype grew the code and did not consistently improve the mixed cases.
- Do not inline a generic method that needs a runtime lookup into shared code on a hot path.
- Leave the one indirect call a top-level call makes in shared generic code (T a class, the caller not inlined with T
  exact: `NinoDeserializer.Deserialize<__Canon>`'s `INinoRawArrayFormatter.Read`, `NinoTopFormatter<T>.Formatter`'s
  virtual members): which formatter runs is known only from the generic dictionary there, so every form (an interface,
  a virtual on an abstract class, a `delegate*` in a static field) is an indirect call, once per top-level call and not
  per element, and callers with T exact (interceptors, Tier1 code that inlines the call) read the `static readonly`
  field as its exact type and inline it. A per-element or per-value interface or virtual call where the type is known
  is the one to remove (round 103: kept after review; no form without an indirect call exists).

- Read a capability query a loop over a class element asks into a `static readonly` field of a class generic over the
  formatter alone (`NinoLoops<TFormatter>`, whose queries sit on the non-generic `INinoFormatter` for it), never over
  `<T, TFormatter>`, and ask it before any path of the method that returns earlier. Why: with `T` a class the loop is
  shared generic code, where a class generic over `T` is a dictionary lookup and a static base helper per collection,
  while the formatter is still its exact struct; and a field the JIT has not seen initialized when Tier1 compiles the
  caller stays a helper call, with the default the formatter lacks (boxed, a call) left in the code beside it.
  Evidence (#346 step 2, `asm.py compare origin/main`): the queries as fields of `<T, TFormatter>` classes listed 29
  methods: every `ArrayFormatter<__Canon, F>` (strings, `[NinoType]` classes) gained the lookup and a call to the
  default `WriteElements`, and `Dictionary<int, string>` members (`SimpleClass`, `TolerantClass`, `AppData.Lookup`, up
  to 30 % more code and 18 more stack accesses) kept `CORINFO_HELP_GET_NONGCSTATIC_BASE`, a box and the default pair
  writes, the pair query sitting after the string pairs' early return so never initialized; with `NinoLoops` asked
  first only the renamed `WriteElements` calls and, in code fully shared over the formatter too (`Box<(class, int)>`),
  one lookup per collection beside the per-element indirect call it already makes.
- Write each subtype inline in a generated dispatch (`DispatchEmitter.EmitBase`), nesting ones included; never move
  them into `NoInlining` `__WriteExact<n>` methods to keep the dispatch small. Why: the call per element costs more than
  the repeated depth handling saves. Evidence: a70aab2c outlined nesting subtypes in dispatches of four or more; the
  case's code fell from 1,934 to 958 bytes, yet the runner A/B of the rc.10 review head against main measured
  GenericSealedClassTupleBase serialize at 0.78 to 0.86x on Neoverse-N2 (two jobs, not in the floor) with nothing won
  on x64 beyond the noise; reverted. Keep the dispatch's own size too: it is inlined into collection loops only
  while the JIT's heuristics accept it, so a check added to `__WriteSubtype` itself costs every element a call.
  Evidence: 341560ef asked `NinoObject.IsDeep` once at the top and wrote each subtype without its deep path (the
  deep chain out of line, writer by value); the array loop stopped inlining the dispatch (`NinoElements.Write` 2,489
  to 153 bytes plus a call) and run 43174700489 measured GenericIntBase serialize at 0.84x on EPYC and 0.89x on
  Neoverse-N2, the generic tuple bases 0.87x on x64, though NonGenericBase gained 1.34x; `AggressiveInlining` on it
  kept the loop inline but spilled it and grew PolymorphicClassArray's methods up to 9x. Reverted (02b9a84c).
  Mark `__WriteSubtype` of a closed generic base's `__NinoDispatch<n>` `AggressiveInlining`, and with that the
  depth asked once does pay: without the mark that dispatch sits one inlining level below the base formatter's
  selection and the loop calls it per element (the 0.84x above), while the non-generic dispatch stays inline.
  Evidence: fb34c84d (both loops 1,416 bytes with the dispatch inline, main 2,489; PolymorphicFoo's 20-subtype
  dispatch 4,696 bytes, main 7,567); run 43212065213 against main (6 rounds, 8 x64 jobs and Neoverse-N2): serialize
  GenericIntBase 1.27 to 1.46x and NonGenericBase 1.19 to 1.49x on every CPU, PolymorphicClassArray, FooArray and
  every deserialize on par; GenericSealedClassTupleBase serialize 1.04 to 1.16x except 0.93x on EPYC 9V45 and 9V74,
  CPUs whose floor reaches 0.87 there. Landed in 558b2a59.

- Inside a large body whose inlining budget is spent, a list of a polymorphic base calls the dispatch per element
  and the dispatch tail-jumps to the subtype's body (two calls an element; `CharacterSaveData`'s `Bag`). Moving that
  loop into a `NoInlining` helper with the writer by reference (`NinoElements.WriteDispatched`, the dispatch inlined
  there: one call an element) was not landed: the budget it freed went elsewhere and the `ActiveBuff[]` member after
  it then called `WritesOnLocal`, `IsEmpty`, `GetReference` and `Serialize` per element, the whole on par (1.011,
  local Xeon, six alternated rounds). Revisit only with every member's loop out of the outer body.
- Keep the write dispatch a chain of exact type checks; never replace it with a jump table through a hash of the type
  handle (the method table pointer read from the object, a multiply, a shift, one compare, then a `switch`). Why: the
  chain's conditional branches predict well even in a random order of subtypes, while the table's indirect jump
  mispredicts once the case bodies are real, and the chain's compares are not where the time goes. Evidence (#305,
  1,000 values of LuminPack's 20-subtype `IFoo` list, local x64): a standalone probe with tiny bodies showed the table
  at 3.5 µs against 4.7 (fixed order) and 3.8 against 4.9 (random), but the generated dispatch measured 6.4 against 6.9
  in a fixed order and 16.0 against 6.5 in a random one; `Type.GetTypeHandle(value)` instead of the header read is a
  call (random 10 µs even in the probe). Writing only the first subtype (3.9 µs) against only the 19th (6.9) did not
  change with the table, so that difference is the 19th subtype's larger body, not its place in the chain: close the
  gap in the bodies (`WriteRawBody`), not in the dispatch. The read side's switch on ids measured the same way
  (`DispatchEmitter.EmitReadSwitch`). Nor through a table of delegates, one generated lambda per subtype found by that hash (what injecting a
  virtual method gives LuminPack without making the user's classes partial): 5.0 to 5.7 us for the same list,
  fixed order, since each element pays a delegate call and the writer's cursor goes back to memory across it
  (local x64, after 98518d32). Nor through a virtual method generated into partial classes (LuminPack's form: the base declares it, each
  subtype overrides it with its id and raw body), though the owner would accept requiring `partial` for a gain: on
  the same list, bytes identical, the chain took 4.93 us in a fixed order and 4.84 shuffled, the virtual calls 5.15
  and 6.95 (LuminPack 4.40): the indirect call mispredicts once the order is mixed and the writer leaves registers
  across it (local x64, 13b9a7b3). LuminPack's lead is not its dispatch.
- Keep each non-flat subtype of a generated read switch a direct call (`__ReadExactN`, `NoInlining`, the reader by
  reference); never send them, with the slot-holds-another-type and too-deep cases, through one out-of-line complete
  read that takes the reader by value (`ReadSubtypeOutOfLine` through a constrained `INinoSubtypeDispatch` call).
  Why: the by-value copy, the constrained call and a second switch on the id cost more than keeping the loop's reader
  in registers saves, and the loop it was for did not get faster. Evidence (#305, 31ecdf6a's dispatch, run
  44255510084 against main c2c6920b, 3 rounds, EPYC 7763 and Neoverse-N2): GenericSealedClassTupleBase read
  0.68–0.81x, GenericOpenClassTupleBase 0.73–0.80x, WorkloadTest CardBattleLog 0.74–0.79x, PolymorphicFooArray and
  PolymorphicClass on par (0.96–1.06x); not landed.

- Write an array or a list of a polymorphic base through the base's generated element loop
  (`INinoFormatter<T>.WriteElements` where `HasElementLoop`, read once into `NinoLoops<TFormatter>.Elements`;
  `DispatchEmitter.EmitWriteElements`): the chain of type checks inline in
  the loop, the writer in a local, the depth checked once before it. Why: written one element at a time through
  `Serialize`, each element called the dispatch (too large to inline into the collection loop) with the writer in
  memory; that call, not the chain or the tag, was LuminPack's whole lead. Evidence (#305, LuminPack's `List<IFoo>`
  of 1,000 values of 20 subtypes, local x64, bytes identical): a hand-written loop 3.85 us, the same chain called per
  element with the writer by reference 4.90-5.05, Nino before e178937c 5.0, after 3.7-3.9 (LuminPack 4.4-4.7). Not
  the tag: a 1-byte tag in place of the 4-byte id wrote in 3.80-3.93 against 3.77-3.99. Not the writer's bookkeeping:
  an end pointer in place of `Writer._avail` (one store fewer per write) left the list at 4.9-5.0, the same.
  Keep the loop in generated code; never move it into Nino.Core as a loop calling a generated single-element write
  marked `AggressiveInlining`: the JIT refused to inline a 20-subtype chain even so (a call per element again, 4.9 us),
  and a test for a generic interface (`default(TFormatter) is INinoElementsFormatter<T>`, removed in #346) per element in a loop over
  a reference type is shared code that looks the interface up and boxes each time: 20 us for the same list (a
  non-generic interface folds). Dictionaries: the next bullet.
  Generic bases keep the element loop out too: the same loop in each closed dispatch class (`__NinoDispatch<n>`),
  selected once per collection, wrote GenericSealedClassTupleBase 22 % and GenericOpenClassTupleBase 12 % slower
  (7.2 to 8.8 us, 7.5 to 8.4; two rounds, local x64), their subtypes each holding a class whose nested writes, all
  inlined into one loop, ran out of registers; the other generic bases moved within noise. The closed dispatch stays
  marked to inline into the collection loop, as before.
- Write a `Dictionary<TKey, Base>` of a polymorphic base through the base's generated pair loop
  (`INinoFormatter.HasPairLoop`, read once into `NinoLoops<TFormatter>.Pairs`; `DispatchEmitter.EmitWriteElements`,
  generated for .NET 8+ only): one
  generic loop `__WritePairs<TKey, TKeyFormatter>` in the generated struct, reached through non-generic members of
  the interface per kind of key (`WritePairs32`, `WritePairs64`, `WriteStringPairs`; 4- and 8-byte primitive and enum keys
  are read as `int` and `long`, `WordKey<T>`), the dictionary's entries walked in place
  (`NinoDictionaryEntries.InUse`: a reference and a count, no enumerator), the writer in a local. Why: in Nino.Core
  the loop is shared code over the class value, so each value was a call into the dispatch with the writer in memory,
  and nothing generic can be called on the formatter from there without a lookup. Keep every part, each measured
  (#308, LuminPack's IFoo of 20 subtypes, values FooA-FooD, a class holding the dictionary, local Emerald Rapids,
  alternating processes, `ser` minimum ns at 1 / 4 / 12 / 32 / 100 pairs; LuminPack in the same process):
  base 18 / 31 / 66 / 150 / 460 for int keys, 20 / 36 / 81 / 195 / 590 for string keys; LuminPack 16 / 24 / 51 / 100 /
  290 and 23 / 47 / 128 / 287 / 816. Lost before this form: a generated `WriteFrom<TSource>` over a struct source of
  pairs written by Nino.Core (shared over the value, a lookup per call: 12 pairs 143-147 ns against 58-72), and a
  non-generic `WriteEntries(ref first, count, stride, offsets)` over the entries' memory, raw keys as bytes (the
  layout's static readonly fields in shared code and the interface call: one pair 14.3 to 22.5 ns, 12 pairs 59 to 72).
  (1) A generic interface method (`WritePairs<TKey, TKeyFormatter>(object, ref
  Writer)`) lost: the JIT does not devirtualize a generic virtual method even on a boxed exact struct
  (`CORINFO_HELP_NEWSFAST` then `CORINFO_HELP_VIRTUAL_FUNC_PTR` and `call rax` in the caller, which knew the type):
  35 / 49 / 94 / 215 / 693; keep the interface's methods non-generic and the generic loop behind them. (2) The
  generated loop over `foreach (var pair in dictionary)` kept `Enumerator.MoveNext` a call per pair (shared code over
  the value, not inlined) and the writer in memory: 27 / 43 / 87 / 213 / 682, slower than base. (3) The entries
  through a span (`foreach (ref readonly var pair in span)`): 18 / 26 / 53 / 120 / 355. (4) The chain's fallback for
  a subtype it does not know called the registry with `ref writer`, which exposed the local's address and kept its
  fields in memory; `writer = __WriteSubtypeDeep(value, writer)` (by value, as the deep path already was) put the
  cursor and the free space in registers: 16 / 24 / 44 / 93 / 275. Keep it to the pair loop: in the element loops of
  arrays and lists (`WriteElements`) the same fallback cut PolymorphicFooArray's loop from 295 stack accesses to 59
  and was on par locally, yet runner A/B run 43849791156 (main f7beb993 against 71be18c9, 3 rounds) measured
  `NinoSerializePolymorphicFooArray` (20 subtypes, nine levels deep) 12 % slower on Neoverse-N2 and 5 % on EPYC 9V74,
  `NinoSerializePolymorphicClass` (its 1,000-element list) 4-8 % slower on 7763, 9V74 and N2, and
  `NinoSerializeNonGenericBase` (8 subtypes) 6 % slower on 9V74 though 22-23 % faster on Xeon 8573C and 6975P (a
  guess, not measured: those CPUs forward the writer's stores cheaply, and the loop with it in registers spilled the
  value instead). The element loops went back to main's code (the registry called with `ref writer`); a list loop
  with the writer in registers is a separate experiment, judged per CPU. (5) The enumerator path (removed entries, bodies too
  deep) out of line in `__WritePairsThroughDispatch`: on par, 6,848 bytes of loop against 7,135. (6) A reference and
  a count in place of the span, whose index kept a bounds check and spilled: 15 / 23 / 39-41 / 80-82 / 237-245 for
  int keys, 17-18 / 26-27 / 53-55 / 120-122 / 354-437 for string keys, ahead of LuminPack at every size (at 4 int
  pairs 23.0-23.7 against 24.9). Cost: the loop is instantiated per kind of key (6,783 bytes at Tier1 for `int`, 7,103
  for `string`, against 1,056 and 1,381 for the shared loop plus the 4,806-byte `__WriteSubtype` it called), and
  `NinoGen.Dispatch.g.cs` grew from 24.8 to 34.0 KB for the 20 subtypes; the pair loops are under
  `#if NET8_0_OR_GREATER` in the generated code, since IL2CPP compiles every method of a type and Nino.Core's
  netstandard2.1 build (Unity, Mono) keeps its own loop. Other keys (structs, `short`, `Guid`, UTF-8 strings) keep
  the shared loop. Reads were left as they are: a hand-written exact loop (`TryAdd`, the generated `Deserialize`
  inline) read no faster than Nino's shared one (12 pairs 322 against 321 ns, 100 pairs 2,282 against 2,163), so the
  shared code's lookups are not the read's cost; LuminPack reads 5-10 % faster from about 4 pairs on (100 int pairs
  2,015 against 2,163-2,200) by filling the entries of a new dictionary in the loop and building its buckets after it
  in one pass.

