<div align="center">

<img src="https://raw.githubusercontent.com/JasonXuDeveloper/Nino/main/docs/assets/logo.png" width="112" alt="Nino logo">

# Nino

**The fastest binary serializer for C#.**<br>
Source-generated, zero-allocation and thread-safe, for Unity, Godot and .NET servers.

[![Build](https://img.shields.io/github/actions/workflow/status/JasonXuDeveloper/Nino/ci.yml?branch=main&style=flat-square)](https://github.com/JasonXuDeveloper/Nino/actions)
[![NuGet](https://img.shields.io/nuget/v/Nino?label=NuGet&style=flat-square&logo=nuget)](https://www.nuget.org/packages/Nino)
[![OpenUPM](https://img.shields.io/npm/v/com.jasonxudeveloper.nino?label=OpenUPM&style=flat-square&logo=unity&registry_uri=https://package.openupm.com)](https://openupm.com/packages/com.jasonxudeveloper.nino/)
[![License](https://img.shields.io/github/license/JasonXuDeveloper/Nino?style=flat-square)](https://github.com/JasonXuDeveloper/Nino/blob/main/LICENSE)
[![Ask DeepWiki](https://deepwiki.com/badge.svg)](https://deepwiki.com/JasonXuDeveloper/Nino)

[🌐 **Website**](https://nino.xgamedev.net/en) ·
[🚀 **Getting Started**](https://nino.xgamedev.net/en/docs/v4/start) ·
[**What's new in v4**](https://nino.xgamedev.net/en/docs/v4/whats-new) ·
[**Benchmarks**](https://nino.xgamedev.net/en/docs/v4/perf) ·
[**vs MemoryPack**](https://nino.xgamedev.net/en/docs/v4/vs-memorypack) ·
[**中文**](https://github.com/JasonXuDeveloper/Nino/blob/main/README.zh.md)

</div>

| **4.7×** | **9.0×** | **0 GC** | **289×** |
|:---:|:---:|:---:|:---:|
| faster serialization than MemoryPack<br>(polymorphic arrays) | faster than MemoryPack's overwrite<br>when deserializing into existing objects | zero GC allocations when serializing<br>or deserializing into pooled objects | faster to compile than v3<br>(1,100 types: 636 s → 2.2 s) |

<p align="center">
  <img src="https://raw.githubusercontent.com/JasonXuDeveloper/Nino/main/docs/assets/benchmark.png" alt="Nino v4 vs Nino v3 vs MemoryPack: serialize and deserialize speed" width="760">
</p>

<sub>BenchmarkDotNet 0.15.2, .NET 10.0.13, server GC, Intel Xeon 2.10 GHz; Nino v4 and MemoryPack 1.21.4 in one run, Nino 3.3.13 from NuGet directly before and after it on the same machine. Payloads are about the same size (`SimpleClass`: 1,150 bytes in v4, 1,152 in v3, 1,148 in MemoryPack). Every case and the method: [docs/v4-benchmarks.md](https://github.com/JasonXuDeveloper/Nino/blob/main/docs/v4-benchmarks.md); the notes of each release carry that release's numbers.</sub>

## Why Nino

- **Faster than MemoryPack.** Serializing classes and polymorphic types is 3.2–4.7× faster. Deserializing them is
  1.2–1.7× faster, and raw struct arrays are close (a block copy in both).
- **Zero allocation.** Serializing allocates nothing. Deserializing into existing or pooled objects
  (`[NinoPooled]`, reuse deserialization into existing objects) allocates nothing either, and is up to 9.0× faster than MemoryPack's
  overwrite mode.
- **Everything happens at compile time.** A source generator writes each formatter. There is no reflection,
  no IL emit, no registration and no init call, so it works on IL2CPP, NativeAOT and HybridCLR hot updates.
  With the .NET 8 SDK or later, calls are intercepted to the exact formatter, with no virtual call.
- **Fast to build.** The generator is incremental: one file per type, and only the type you edit is
  re-generated. Compared with v3, the generated code is 14× smaller and compiles 40× faster.
- **Thread-safe.** Serialize and deserialize from any number of threads, with no locks on the hot path.
- **Data that evolves.** Version tolerance, keyed members, and reference tracking for shared references and
  cycles. All are opt-in per type, so types that don't use them pay nothing.
- **One format everywhere.** `netstandard2.0`, `netstandard2.1`, `net8.0` and `net10.0` write identical bytes,
  so a Unity client and a .NET server talk to each other directly.
- **Mistakes caught while you type.** 25 analyzer diagnostics (NINO001–NINO025), many with a one-click fix.

## Built for

| | |
|---|---|
| 🎮 **[Unity](https://nino.xgamedev.net/en/docs/v4/advanced/unity)** | Unity 2022.3+, Mono and IL2CPP, HybridCLR hot-update assemblies. `Vector3`, `Quaternion`, `Color32` and other unmanaged structs are copied as raw memory. Install from OpenUPM. |
| 👾 **[Godot 4 (.NET)](https://nino.xgamedev.net/en/docs/v4/platforms)** | A regular NuGet package for Godot's .NET projects. Godot's math structs (`Vector3`, `Transform3D`, …) are unmanaged and copied as raw memory. |
| 🌐 **[Game servers & networking](https://nino.xgamedev.net/en/docs/v4/advanced/networking)** | Serialize straight into an `IBufferWriter<byte>` (a `PipeWriter`, a pooled buffer), deserialize from a `ReadOnlySpan<byte>`, and reuse pooled message objects. Polymorphic messages need no tags. |
| 💾 **[Save games & persistence](https://nino.xgamedev.net/en/docs/v4/advanced/persistence)** | Version tolerance and keyed members let you add, reorder and remove fields across releases. Reference tracking keeps object graphs with shared references and cycles. |
| ⚙️ **[Services, caches & IPC](https://nino.xgamedev.net/en/docs/v4/advanced/high-performance)** | Compact binary payloads, no per-call allocations, and safe concurrent use from ASP.NET Core, workers and actors. |

## Install

> New to Nino? Follow **[Getting Started](https://nino.xgamedev.net/en/docs/v4/start)** on the website: installation for .NET, Unity and Godot, your first type, and the basics.

```bash
dotnet add package Nino                # .NET, Godot
dotnet add package Nino.Immutable      # optional: ImmutableArray<T> / ImmutableList<T>
openupm add com.jasonxudeveloper.nino  # Unity
```

## Quick start

```csharp
using Nino.Core;

[NinoType]
public class Player
{
    public int Id;
    public string Name;
    public List<Item> Inventory;
}

byte[] bytes = NinoSerializer.Serialize(player);
Player copy = NinoDeserializer.Deserialize<Player>(bytes);
```

### Polymorphism without tags

```csharp
[NinoType] public abstract class Message { public int Seq; }
[NinoType] public sealed class Move : Message { public Vector3 To; }
[NinoType] public sealed class Chat : Message { public string Text; }

Message msg = NinoDeserializer.Deserialize<Message>(bytes); // a Move or a Chat
```

### Zero-allocation networking

```csharp
// Write directly into the socket pipe: no intermediate byte[].
NinoSerializer.Serialize(msg, pipeWriter);

// Reuse pooled instances (and their lists) instead of allocating new ones.
[NinoType, NinoPooled]
public sealed partial class Snapshot
{
    public int Tick;
    public List<EntityState> Entities;

    private static partial IEnumerable<Snapshot> OnNinoPoolWarmup() => Enumerable.Range(0, 16).Select(_ => new Snapshot());
    private partial void OnNinoPoolReturn() { }
}

var snapshot = NinoDeserializer.Deserialize<Snapshot>(span); // taken from the pool
// ... apply it ...
snapshot.ReturnToPool();
```

### Data that changes over time

```csharp
[NinoType(format: NinoFormat.Keyed)] // members are matched by name: add, reorder, remove or rename them
public class SaveGame { [NinoFormerName("Lvl")] public int Level; public string Hero; }

[NinoType(trackReferences: true)]    // shared references and cycles survive a round trip
public class Node { public string Name; public List<Node> Links; }
```

**[Read the documentation on nino.xgamedev.net →](https://nino.xgamedev.net/en/docs/v4/start)**, including
[advanced guides](https://nino.xgamedev.net/en/docs/v4/advanced) for high-performance serialization,
networking, persistence and Unity.

## Nino vs MemoryPack

| | Nino v4 | MemoryPack 1.21.4 |
|---|---|---|
| Serialize classes and polymorphic types | **3.2–4.7× faster** | |
| Deserialize classes and polymorphic types | **1.2–1.7× faster** | |
| Raw struct arrays | close (a block copy in both); v4 zeroes padding, so declare hot structs without it | copies padding as is |
| Deserialize into existing objects | **0 B, up to 9.0× faster** (in place) | overwrite mode, allocates |
| Polymorphism | automatic type ids | `[MemoryPackUnion]` tags you maintain |
| Keyed members, reference tracking, object pools | ✅ | version tolerance and circular references, no pools |
| Unity | 2022.3+, `netstandard2.0` and `2.1`, HybridCLR | `netstandard2.1` |

MemoryPack still has more built-in types (`BigInteger`, `Uri`, every immutable collection, Unity classes such as
`AnimationCurve`), TypeScript generation and ASP.NET Core formatters. Streams, async I/O, multi-segment
`ReadOnlySequence<byte>` input, callbacks and compression are a few lines around Nino's `IBufferWriter<byte>` and
`ReadOnlySpan<byte>` APIs; the [full comparison](https://nino.xgamedev.net/en/docs/v4/vs-memorypack) has examples.
MemoryPack's union tag is 1 byte where Nino writes a 4-byte type id, and that is a trade: the id is derived from the
type, so subtypes need no tags, can be added in any assembly (a plugin or a HybridCLR hot update) and never
renumber, while every MemoryPack union lists or registers its subtypes by hand.

## v4

v4 is a rewrite of the runtime and the generator. **Its binary format is not compatible with v3.**

| v4 vs v3 (3.3.13) | |
|---|---|
| Serialize | **1.50× faster** (class), **2.14× faster** (class array), on par on struct arrays |
| Deserialize | **1.08× faster** (class), **1.09× faster** (class array), on par on raw arrays, while adding reference tracking, reuse deserialization and payload validation |
| Compiling the generated code | **40× faster** at 330 types (44 s → 1.1 s), **289× faster** at 1,100 types (636 s → 2.2 s) |
| Running the generator | **4.2× faster**; after an edit, only that type is re-generated (**12× faster**) |
| Generated code | **14× smaller** source, **5× smaller** IL |

New in v4: interceptors, keyed members, reference tracking, `[NinoPooled]` pools, pluggable formatters
(`[assembly: NinoFormatter(...)]`), user collection types, analyzers with code fixes, and identical bytes on every
target framework. See [what's new](https://nino.xgamedev.net/en/docs/v4/whats-new) and the
[migration guide](https://nino.xgamedev.net/en/docs/v4/migration); internals are in
[docs/v4-design.md](https://github.com/JasonXuDeveloper/Nino/blob/main/docs/v4-design.md).

## Contributing

Issues and pull requests are welcome. [CONTRIBUTING.md](https://github.com/JasonXuDeveloper/Nino/blob/main/CONTRIBUTING.md) covers building, the test matrix and
releasing.

<div align="center">

[MIT License](https://github.com/JasonXuDeveloper/Nino/blob/main/LICENSE) · Made by [JasonXuDeveloper](https://github.com/JasonXuDeveloper)

</div>
