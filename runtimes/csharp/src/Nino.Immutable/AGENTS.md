# Nino.Immutable: file map

> Read when: reading or changing the System.Collections.Immutable formatters (package Nino.Immutable).

netstandard2.1 and net8.0 (trimmable, NativeAOT compatible) library, packed as `Nino.Immutable`: formatters for the
eight immutable collections, registered with `[assembly: NinoFormatter]` so generated code and the runtime resolver
bind them. Writes the bytes of the matching mutable collection. `audit-core` audits it; the
tree builders follow `runtime-internals` (building a runtime's own objects).

```
Nino.Immutable.csproj        targets, the Nino.Core reference (-p:NinoCoreTfm=netstandard2.1 picks a build),
                             System.Collections.Immutable on netstandard2.1, links Nino.Core's Internal/Layout/ObjectTypes.cs
ImmutableFormatters.cs       the [assembly: NinoFormatter] list of the formatters below
ImmutableCollections.cs      what the formatters share: T[] reads of the collections built in one go, duplicate keys
                             rejected (Distinct, AddAll)
Formatters/                  one formatter per collection, each written through Nino.Core's gather (NinoGathered): its
                             tree or its struct enumerator walked a chunk at a time; ImmutableArrayFormatter.cs (as T[],
                             wrapped without a copy), ImmutableListFormatter.cs, ImmutableHashSetFormatter.cs,
                             ImmutableSortedSetFormatter.cs, ImmutableQueueFormatter.cs (its nodes, the count
                             patched), ImmutableStackFormatter.cs (its nodes, written reversed),
                             ImmutableDictionaryFormatter.cs, ImmutableSortedDictionaryFormatter.cs
HashTrees.cs                 ImmutableDictionary / ImmutableHashSet built directly as their tree through mirror classes
                             (DictionaryTree, SetTree), checked at startup, the builder's Add otherwise; walked for a
                             write in the enumerator's order, each bucket's first item then the further ones of keys that
                             share its hash (HashTree.Items, through NinoTreeWalk)
SortedTrees.cs               ImmutableSortedDictionary / ImmutableSortedSet built as their AVL tree in one pass from
                             items in order (SortedDictionaryTree, SortedSetTree); their trees and ImmutableList's walked
                             in order for a write (ListNode: the node ImmutableList and ImmutableSortedSet share,
                             ListTree, SortedDictionaryTree.Pairs)
StackNodes.cs                ImmutableStack / ImmutableQueue walked through their nodes for a write (mirror classes
                             StackNode, QueueView, checked at startup; StackItems): a queue's front stack, then its back
                             one as the queue keeps it reversed; their enumerators (Pop and Peek per element) for a back
                             not reversed yet and where the check fails
Properties.cs                [module: SkipLocalsInit]
```

## Rules for these files

- Read paths reach Nino.Core only through its public and `CompilerServices` API (`NinoEffectiveSettings` for the
  frozen settings); `ObjectTypes.cs` is the one file compiled in. Every collection is written through the gather
  (`NinoGathered.Write`, `WriteReversed`): a formatter says only how its collection is walked (a tree through
  `NinoTreeWalk` with its mirror's offsets, linked nodes through their mirror, else its struct enumerator, never
  `IEnumerable<T>`), never how an element
  is written, so each element kind takes Nino.Core's loop for it (`optimize`).
- A startup check builds its collections from enumerables, never through a `params` call (`ImmutableList.Create(a, b)`):
  the compiler binds that to the `ReadOnlySpan<T>` overload of the newer System.Collections.Immutable it compiles
  against, which Mono's lacks, and the check then fails there with `MissingMethodException`.
- The mirror classes must match the fields of every System.Collections.Immutable version users may have (1.5 to
  10.0 share them); `Usable` falls back to the builders when the layout check fails, and `runtimes/csharp/tests/Nino.RuntimeTests`
  has a `FastPaths` row for `HashTrees.Usable`, `SortedTrees.Usable` and `StackNodes.Usable`, so a runtime that falls back fails there.
