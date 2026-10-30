# Nino.Immutable: file map

> Read when: reading or changing the System.Collections.Immutable formatters (package Nino.Immutable).

netstandard2.0 and net8.0 (trimmable, NativeAOT compatible) library, packed as `Nino.Immutable`: formatters for the
eight immutable collections, registered with `[assembly: NinoFormatter]` so generated code and the runtime resolver
bind them. Writes the bytes of the matching mutable collection. `audit-core` audits it; the
tree builders follow `runtime-internals` (building a runtime's own objects).

```
Nino.Immutable.csproj        targets, the Nino.Core reference (-p:NinoCoreTfm=netstandard2.0 / 2.1 picks a build),
                             System.Collections.Immutable on netstandard2.0, links Nino.Core's Internal/ObjectTypes.cs
ImmutableFormatters.cs       the [assembly: NinoFormatter] list and the formatters: ImmutableArray (as T[], wrapped
                             without a copy), ImmutableList, ImmutableHashSet, ImmutableSortedSet, ImmutableQueue,
                             ImmutableStack, ImmutableDictionary, ImmutableSortedDictionary
HashTrees.cs                 ImmutableDictionary / ImmutableHashSet built directly as their tree through mirror classes
                             (DictionaryTree, SetTree), checked at startup, the builder's Add otherwise
SortedTrees.cs               ImmutableSortedDictionary / ImmutableSortedSet built as their AVL tree in one pass from
                             items in order (SortedDictionaryTree, SortedSetTree)
Properties.cs                [module: SkipLocalsInit]
```

## Rules for these files

- Read paths reach Nino.Core only through its public and `CompilerServices` API (`NinoEffectiveSettings` for the
  frozen settings); `ObjectTypes.cs` is the one file compiled in.
- The mirror classes must match the fields of every System.Collections.Immutable version users may have (1.5 to
  10.0 share them); `Usable` falls back to the builders when the layout check fails, and `tests/Nino.RuntimeTests`
  has a `FastPaths` row for `HashTrees.Usable` and `SortedTrees.Usable`, so a runtime that falls back fails there.
