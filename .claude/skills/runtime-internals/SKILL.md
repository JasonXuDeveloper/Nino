---
name: runtime-internals
description: "How Nino reads and builds a runtime's own objects through their fields instead of the public API - CollectionViews (List, HashSet, Dictionary layouts per runtime), mirror classes and ObjectTypes.Retype, ImmutableDictionary/ImmutableHashSet trees (HashTrees), SortedSet red-black trees, LinkedList rings, ConcurrentDictionary buckets - with startup checks, public-API fallbacks and the FastPaths tests. Use when adding or changing such a path, when a collection reads or writes wrong on Mono, IL2CPP, .NET Framework or 32-bit, or when a startup check fails. Triggers: CollectionViews, HashTrees, SortedSetNodes, ReadLinked, ConcurrentTables, Retype, mirror class, startup check, FastPaths, tests/Nino.RuntimeTests, Mono abort, SortedSet, ConcurrentDictionary, 读错, 启动检查, 运行时内部, 镜像类, 字段布局, 红黑树."
metadata:
  internal: true
---

# Runtime internals

Reading a collection through its public API (an enumerator) and building one through it (a builder's `Add` per
element, rehashing, rebalancing) cost a call or more per element, while its own fields can be read or filled in one
pass; the price is that every runtime lays those fields out its own way and may change them. So every such path proves its layout at startup and keeps the public API as the fallback, and is
tested on every runtime it can meet.

- Adopt a raw integer priority queue heap only after validating the runtime's node layout and four-way
  parent rule. Check the entire wire block and its priority order before copying, keep equal priorities in
  their original order, and leave custom comparers and capacity growth to public enqueue.
  For writes, reuse the validated node array and copy only the live count, with a version check around the
  copy. The comparer does not matter on write, because the bytes preserve the existing heap order.

## Reading a runtime's collections through their fields

A path through a runtime's internals (`CollectionViews`: the fields of `List<T>`, `HashSet<T>`,
`Dictionary<TKey, TValue>`, ...) declares a layout for each implementation (.NET 64-bit and 32-bit, .NET Framework,
Mono), checks it against an instance with known contents at startup and uses the public API when none matches. Keep
the fallback (the enumerator) out of line.

- Capture the published head of an exact CoreCLR `ConcurrentStack<T>` once when writing raw elements. On the
  checked .NET 8 and 10 layouts, popping and clearing replace the head and keep the published chain intact. Count
  that chain, reserve once and walk it again to write bottom to top. Check field shape and offsets before using
  the mirrors, and retain the public snapshot on other runtimes or with other formatters. The local Arm64
  8,192-element cases went from 98,632 B (`int`) and 393,496 B (`Guid`) to zero warmed allocation. See
  `ConcurrentStackNodes` and the compacting GC regression in `CollectionSerializationTests`.
  Its gate is `Environment.Version.Major >= 8` (the owner's call): field checks prove the layout, not the pop
  semantics, which were read in the .NET 8 source; `ConcurrentStack.cs` is the same file byte for byte in
  `release/8.0` to `release/11.0`. A runtime that changes them is caught only by the unit test
  `ConcurrentStackKeepsTheCapturedChainAcrossPopsAndCompaction` running on it, so a new .NET joins the unit tests'
  targets when it ships, and its `ConcurrentStack.cs` is diffed against `release/10.0` then. An explicit list of
  versions (8 or 10) had left .NET 9 and 11 on the allocating snapshot.
  Keep the independently obtained node reference in that probe: public enumeration witnesses values and order,
  and the mirror reference slot must be checked before the GC can see it as a reference. On .NET 10 an
  `UnsafeAccessorType` annotation for a private `Node` field rejects the required translated byref return with
  `NotSupportedException`. An unannotated `ref object` has a different field signature and throws
  `MissingFieldException`. Revisit when CoreCLR offers a type-checked field read that can return the reference by value.

## Building a runtime's objects without its public API

`Nino.Immutable/HashTrees.cs` (an `ImmutableDictionary` or `ImmutableHashSet` built as its tree, instead of one
builder `Add` at a time); `SortedSetNodes` in Nino.Core (a `SortedSet<T>`'s red-black tree); `ReadLinked` (a
`LinkedList<T>`'s ring of nodes); `ConcurrentTables.Builder` (a `ConcurrentDictionary`'s bucket chains). One way for
every runtime where one works, and a separate way per runtime (behind `#if` or a startup check) only where none does.
What works everywhere so far:

- **Mirror classes** that declare the fields of the real class in its order, read and written through `Unsafe.As`:
  every runtime lays out two classes with the same field list the same way. Check the field lists of every version of
  the library users may have (System.Collections.Immutable 1.5 to 10.0 have the same fields), and check the layout at
  startup against an instance with known contents, through the public API; use the public API when it fails. Run
  every such check through `ObjectTypes.Passes` (a check that throws fails; Nino.Core and Nino.Immutable both
  compile `ObjectTypes.cs`), never a `try`/`catch` of its own: round 129 merged seven copies of that wrapper
  (`HashTables`, `ConcurrentTables`, `CollectionViews`, `SortedSetNodes`, `ValueViews`, `HashTrees`, `SortedTrees`).
- **Never call an instance method on a mirrored object** (a `MemberwiseClone` helper in the mirror class): Mono and
  IL2CPP take a shared generic method's context from `this`'s class, and Mono aborts (an assertion, not an exception,
  so no `catch` helps). Static code only.
- **Allocate a mirror instance with `new` and set its type word** to the real type's (`ObjectTypes.Retype`): the same
  fields mean the same size and the same references for the GC. Faster than `RuntimeHelpers.GetUninitializedObject`
  (13 ns on .NET 10 but 77 on .NET 8, FormatterServices 59 on Mono) and needs nothing IL2CPP may lack. The type word
  sits where the reference points (CoreCLR: method table, one header word; Mono and IL2CPP: vtable or class, then the
  monitor, two words), found at startup; call `GetType()` on a retyped object out of line (`NoInlining`), or the JIT
  folds it to the type it allocated (Arm32 did).
- **Hash tables filled this way still count collisions** (`audit-security`: `HashTables`, `ConcurrentTables`).

## Proof

- Run it on .NET 8 and 10, the `net472` build under Mono (also `MONO_GC_DEBUG=verify-before-collections`), the
  `linux-arm` and `linux-arm64` builds under qemu (`platforms`), with a GC stress (forced compacting collections
  between builds, then every object checked).
- CI's "Runtime internals" job runs `tests/Nino.RuntimeTests` on .NET (x64, Arm64, Arm32 under qemu, Windows x86),
  Mono (x64, Arm64) and .NET Framework 4.8 (64-bit and 32-bit): give a new path a case there with known contents, and
  its startup flag a row in its `FastPaths`, so a runtime that falls back to the public API fails. Each runtime must
  write .NET x64's bytes; locally, diff the outputs (CONTRIBUTING.md).
