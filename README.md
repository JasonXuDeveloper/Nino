<div align="center">

<img src="docs/assets/logo.png" width="112" alt="Nino logo">

# Nino

**The fastest binary serializer for C#.**<br>
Source-generated, zero-allocation and thread-safe, for Unity, Godot and .NET servers.

[![Build](https://img.shields.io/github/actions/workflow/status/JasonXuDeveloper/Nino/ci.yml?branch=main&style=flat-square)](https://github.com/JasonXuDeveloper/Nino/actions)
[![NuGet](https://img.shields.io/nuget/v/Nino?label=NuGet&style=flat-square&logo=nuget)](https://www.nuget.org/packages/Nino)
[![OpenUPM](https://img.shields.io/npm/v/com.jasonxudeveloper.nino?label=OpenUPM&style=flat-square&logo=unity&registry_uri=https://package.openupm.com)](https://openupm.com/packages/com.jasonxudeveloper.nino/)
[![License](https://img.shields.io/github/license/JasonXuDeveloper/Nino?style=flat-square)](LICENSE)
[![Ask DeepWiki](https://deepwiki.com/badge.svg)](https://deepwiki.com/JasonXuDeveloper/Nino)

[**Documentation**](https://nino.xgamedev.net/en/docs/v4/start) ·
[**What's new in v4**](https://nino.xgamedev.net/en/docs/v4/whats-new) ·
[**Benchmarks**](https://nino.xgamedev.net/en/docs/v4/perf) ·
[**vs MemoryPack**](https://nino.xgamedev.net/en/docs/v4/vs-memorypack) ·
[**中文**](README.zh.md)

</div>

| **4.3×** | **4.9×** | **0 B** | **44×** |
|:---:|:---:|:---:|:---:|
| faster serialization than MemoryPack<br>(polymorphic arrays) | faster than MemoryPack's overwrite<br>when deserializing into existing objects | allocated when serializing,<br>or deserializing into pooled objects | faster to compile than v3<br>(16× less generated code) |

<p align="center">
  <img src="docs/assets/benchmark.svg" alt="Nino v4 vs Nino v3 vs MemoryPack: serialize and deserialize speed" width="760">
</p>

<sub>BenchmarkDotNet 0.15.2, .NET 10, Intel Xeon 2.80 GHz, same machine and payloads of the same size (`SimpleClass`: 1,146 bytes for Nino, 1,148 for MemoryPack). Every case and the method: [docs/v4-benchmarks.md](docs/v4-benchmarks.md).</sub>

## Why Nino

- **Faster than MemoryPack.** Serializing is 2.5–4.3× faster. Deserializing classes and polymorphic types is
  1.16–1.38× faster, and raw struct arrays are on par.
- **Zero allocation.** Serializing allocates nothing. Deserializing into existing or pooled objects
  (`[NinoPooled]`, `ref` deserialization) allocates nothing either, and is up to 4.9× faster than MemoryPack's
  overwrite mode.
- **Everything happens at compile time.** A source generator writes each formatter. There is no reflection,
  no IL emit, no registration and no init call, so it works on IL2CPP, NativeAOT and HybridCLR hot updates.
  With the .NET 8 SDK or later, calls are intercepted to the exact formatter, with no virtual call.
- **Fast to build.** The generator is incremental: one file per type, and only the type you edit is
  re-generated. Compared with v3, the generated code is 16× smaller and compiles 44× faster.
- **Thread-safe.** Serialize and deserialize from any number of threads, with no locks on the hot path.
- **Data that evolves.** Version tolerance, keyed members, and reference tracking for shared references and
  cycles. All are opt-in per type, so types that don't use them pay nothing.
- **One format everywhere.** `netstandard2.0`, `netstandard2.1`, `net8.0` and `net10.0` write identical bytes,
  so a Unity client and a .NET server talk to each other directly.
- **Mistakes caught while you type.** 22 analyzer diagnostics (NINO001–NINO022), many with a one-click fix.

## Built for

| | |
|---|---|
| 🎮 **Unity** | Unity 2022.3+, Mono and IL2CPP, HybridCLR hot-update assemblies. `Vector3`, `Quaternion`, `Color32` and other unmanaged structs are copied as raw memory. Install from OpenUPM. |
| 👾 **Godot 4 (.NET)** | A regular NuGet package for Godot's .NET projects. Godot's math structs (`Vector3`, `Transform3D`, …) are unmanaged and copied as raw memory. |
| 🌐 **Game servers & networking** | Serialize straight into an `IBufferWriter<byte>` (a `PipeWriter`, a pooled buffer), deserialize from a `ReadOnlySpan<byte>`, and reuse pooled message objects. Polymorphic messages need no tags. |
| 💾 **Save games & persistence** | Version tolerance and keyed members let you add, reorder and remove fields across releases. Reference tracking keeps object graphs with shared references and cycles. |
| ⚙️ **Services, caches & IPC** | Compact binary payloads, no per-call allocations, and safe concurrent use from ASP.NET Core, workers and actors. |

## Install

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
[NinoType(keyed: true)]            // members are matched by name: add, reorder or remove them
public class SaveGame { public int Level; public string Hero; }

[NinoType(trackReferences: true)]  // shared references and cycles survive a round trip
public class Node { public string Name; public List<Node> Links; }
```

**[Read the documentation →](https://nino.xgamedev.net/en/docs/v4/start)**, including
[advanced guides](https://nino.xgamedev.net/en/docs/v4/advanced) for high-performance serialization,
networking, persistence and Unity.

## Nino vs MemoryPack

| | Nino v4 | MemoryPack 1.21.4 |
|---|---|---|
| Serialize | **2.5–4.3× faster** | |
| Deserialize classes and polymorphic types | **1.16–1.38× faster** | |
| Deserialize raw struct arrays | on par | on par |
| Deserialize into existing objects | **0 B, up to 4.9× faster** (pooled) | overwrite mode, allocates |
| Polymorphism | automatic type ids | `[MemoryPackUnion]` tags you maintain |
| Keyed members, reference tracking, object pools | ✅ | version tolerance and circular references, no pools |
| Unity | 2022.3+, `netstandard2.0` and `2.1`, HybridCLR | `netstandard2.1` |

MemoryPack still does things Nino doesn't: more built-in types (`BigInteger`, `Uri`, every immutable collection,
Unity classes such as `AnimationCurve`), `Stream` and async APIs, multi-segment `ReadOnlySequence<byte>` input,
serialization callbacks, built-in Brotli compression, TypeScript generation and ASP.NET Core formatters. Its
union tag is 1 byte where Nino writes a 4-byte type id. Most have a workaround; the
[full comparison](https://nino.xgamedev.net/en/docs/v4/vs-memorypack) lists them.

## v4

v4 is a rewrite of the runtime and the generator. **Its binary format is not compatible with v3.**

| v4 vs v3 (3.3.13) | |
|---|---|
| Serialize | **up to 1.73× faster** (class 1.73×, class array 1.29×, struct 1.35×) |
| Deserialize | **1.19–1.22× faster** on classes, on par on raw arrays |
| Compiling the generated code (330 types) | **44× faster** (56 s → 1.3 s) |
| Running the generator | **7.9× faster**; after an edit, only that type is re-generated |
| Generated code | **16× smaller** source, **6× smaller** IL |

New in v4: interceptors, keyed members, reference tracking, `[NinoPooled]` pools, pluggable formatters
(`[assembly: NinoFormatter(...)]`), user collection types, analyzers with code fixes, and identical bytes on every
target framework. See [what's new](https://nino.xgamedev.net/en/docs/v4/whats-new) and the
[migration guide](https://nino.xgamedev.net/en/docs/v4/migration); internals are in
[docs/v4-design.md](docs/v4-design.md).

## Contributing

Issues and pull requests are welcome. [CONTRIBUTING.md](CONTRIBUTING.md) covers building, the test matrix and
releasing.

<div align="center">

[MIT License](LICENSE) · Made by [JasonXuDeveloper](https://github.com/JasonXuDeveloper)

</div>
