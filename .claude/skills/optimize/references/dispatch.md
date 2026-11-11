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

- Write each subtype inline in a generated dispatch (`DispatchEmitter.EmitBase`), nesting ones included; never move
  them into `NoInlining` `__WriteExact<n>` methods to keep the dispatch small. Why: the call per element costs more than
  the repeated depth handling saves. Evidence: a70aab2c outlined nesting subtypes in dispatches of four or more; the
  case's code fell from 1,934 to 958 bytes, yet the runner A/B of the rc.10 review head against main measured
  GenericSealedClassTupleBase serialize at 0.78 to 0.86x on Neoverse-N2 (two jobs, not in the floor) with nothing won
  on x64 beyond the noise; reverted.
