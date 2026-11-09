# Direct calls instead of dispatch (.NET, NativeAOT, IL2CPP, Mono)

Part of the `optimize` skill. The owner's rule is no virtual dispatch on a serialize or deserialize path where a
direct form exists; this file holds how the JIT devirtualizes, the inventory of indirect calls left and why each
stays, and what was measured. Read it when a listing shows `call [rax+0x..]`, `call [r11]` or `call rax`, or when
adding a formatter lookup, a delegate or an interface call to a hot path. Go, Rust and TypeScript:
`optimize-runtimes`, references/dispatch.md.

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
  (3) The netstandard builds' block loops (`NinoBlockCollections.WriteValue`/`ReadValue` through
  `NinoBlockPlan.Formatter`): one interface call per element where no Burst kernel runs (IL2CPP, Mono, .NET
  Framework); the direct form puts the loop in the formatter, which the owner declined. (4) The user-extensible points
  above. Per top-level call, not per value: calls the interceptors leave alone (generic methods, another assembly's
  types, Unity's Roslyn 4.3, raw values and arrays of raw elements) read `NinoTopFormatter<T>`, `NinoRaw<T>` and
  `NinoRawArray<T>` as loads, so an array of raw elements is one `INinoRawArrayFormatter` interface call per call
  under NativeAOT, and the dead virtual branches stay in the code; generic `[NinoType]`s use no `CachedFormatter`
  (Unity kernels and IL2CPP).
- Split exact BCL collection paths from interface snapshots when a concrete API does the same work. Test the
  runtime type before calling the concrete API so a subclass's interface implementation still runs. In
  `ProducerConsumerFormatter.Serialize` (6415b715), the exact `ConcurrentQueue<T>` fixed-width path calls
  `ConcurrentQueue<T>.ToArray` directly and keeps the other snapshots out of line. On local Arm64 FullOpts,
  the variable-width queue entry and its helper together went from 2,340 to 1,336 bytes. Fixed-width queues
  still allocate the bulk snapshot.
- Refill exact concurrent stacks through `TryPop` and `Push`, and use one `Clear` when no old item needs
  recycling. Bind stacks to their own formatter and leave subclasses on their interface (1e45ed16).
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
