<div align="center">

<img src="https://raw.githubusercontent.com/JasonXuDeveloper/Nino/main/docs/assets/logo.png" width="112" alt="Nino logo">

# Nino

**The fastest binary serializer for C#.**<br>
General-purpose, source-generated, zero-allocation and thread-safe: for services, RPC,<br>
messaging, caching and storage on .NET, with Unity, Godot and HybridCLR support.

[![Build](https://img.shields.io/github/actions/workflow/status/JasonXuDeveloper/Nino/ci.yml?branch=main&style=flat-square)](https://github.com/JasonXuDeveloper/Nino/actions)
[![NuGet](https://img.shields.io/nuget/v/Nino?label=NuGet&style=flat-square&logo=nuget)](https://www.nuget.org/packages/Nino)
[![OpenUPM](https://img.shields.io/npm/v/com.jasonxudeveloper.nino?label=OpenUPM&style=flat-square&logo=unity&registry_uri=https://package.openupm.com)](https://openupm.com/packages/com.jasonxudeveloper.nino/)
[![License](https://img.shields.io/github/license/JasonXuDeveloper/Nino?style=flat-square)](https://github.com/JasonXuDeveloper/Nino/blob/main/LICENSE)
[![Ask DeepWiki](https://img.shields.io/badge/Ask-DeepWiki-blue?style=flat-square)](https://deepwiki.com/JasonXuDeveloper/Nino)

[🌐 **Website**](https://nino.xgamedev.net/en) ·
[🚀 **Getting Started**](https://nino.xgamedev.net/en/docs/v4/start) ·
[**What's new in v4**](https://nino.xgamedev.net/en/docs/v4/whats-new) ·
[**Benchmarks**](https://nino.xgamedev.net/en/docs/v4/perf) ·
[**vs MemoryPack**](https://nino.xgamedev.net/en/docs/v4/vs-memorypack) ·
[**中文**](https://github.com/JasonXuDeveloper/Nino/blob/main/README.zh.md)

**Up to <!--bench:ser.max-->6.8<!--/bench-->× faster** serialization and **up to <!--bench:reuse.inplace-->8.7<!--/bench-->× faster** in-place deserialization than MemoryPack<br>
**Zero GC** allocations on hot paths · **221× faster** builds than v3

</div>

> [!IMPORTANT]
> **Nino is built to read untrusted data**: messages from network peers, files users hand you, request bodies.
> Crafted bytes cannot crash the process, read past the input, allocate out of proportion to their size (a
> collection allocates at most 16× the bytes left ahead of its elements) or name a type to create: the data holds
> type ids of your declared subtypes, never type names. Every invalid input throws one exception,
> `NinoDataException` (an `InvalidOperationException`, message starting `Nino:`), so a single `catch` at the boundary
> handles it. Keys built to collide in a hash table throw too (`NinoSettings.MaxHashCollisions`), and
> `NinoSettings.MaxDepth` and `MaxCollectionLength` tighten the limits. For data from outside, set
> `NinoSettings.Untrusted = true` at startup: the dictionaries and sets Nino creates hash integer, `char`, `Guid` and
> `string` keys with a per-process secret, `bool`, `DateTime`, `decimal`, `DateOnly` and `TimeOnly` values are checked
> even inside raw structs, and bytes after the value are rejected; with it off (the default) none of this costs anything.
> Fuzzed and hand-crafted payloads were run against every built-in type, and what they found is fixed in 4.0.0-rc.7.
> [What Nino checks, and what stays your code's job](https://nino.xgamedev.net/en/docs/v4/basic#invalid-data). (MemoryPack now
> [recommends itself for trusted data only](https://github.com/Cysharp/MemoryPack#readme); see the [comparison](https://nino.xgamedev.net/en/docs/v4/vs-memorypack#untrusted-input).)

<p align="center">
  <img src="https://raw.githubusercontent.com/JasonXuDeveloper/Nino/main/docs/assets/benchmark.png" alt="Nino v4 vs Nino v3 vs MemoryPack: serialize and deserialize speed" width="760">
</p>

<sub>BenchmarkDotNet <!--bench:env.bdn-->0.15.8<!--/bench-->, <!--bench:env.runtime-->.NET 10.0.13<!--/bench-->, server GC, <!--bench:env.cpu-->INTEL XEON PLATINUM 8573C 3.50 GHz<!--/bench-->; Nino v4 and MemoryPack 1.21.4 in one run, Nino 3.3.13 from NuGet <!--bench:v3.when-->directly before it<!--/bench--> on the same machine. Payloads are about the same size (`SimpleClass`: <!--bench:payload.v4-->1,104<!--/bench--> bytes in v4, 1,152 in v3, <!--bench:payload.memorypack-->1,148<!--/bench--> in MemoryPack). Every case and the method: [docs/v4-benchmarks.md](https://github.com/JasonXuDeveloper/Nino/blob/main/docs/v4-benchmarks.md); the notes of each release carry that release's numbers.</sub>

## Why Nino

- **Faster than MemoryPack.** Serializing classes and polymorphic types is <!--bench:ser.range-->3.5–6.8<!--/bench-->× faster. Deserializing them is
  <!--bench:deser.range-->1.3–1.9<!--/bench-->× faster, and raw struct arrays are at least on par (a block copy in both).
- **Safe for untrusted data.** Crafted bytes cannot crash the process, allocate out of proportion to their size or make
  Nino create a type you did not declare; every invalid input throws one `NinoDataException`.
  [Invalid data](https://nino.xgamedev.net/en/docs/v4/basic#invalid-data).
- **Zero allocation.** Serializing into a buffer (`IBufferWriter<byte>`) allocates nothing. Deserializing into existing or pooled objects
  (`[NinoPooled]`, reuse deserialization into existing objects) allocates nothing either (a string whose text is unchanged is kept),
  and is up to <!--bench:reuse.inplace-->8.7<!--/bench-->× faster than MemoryPack's overwrite mode.
- **Everything happens at compile time.** A source generator writes each formatter. There is no reflection,
  no IL emit, no registration and no init call, so it works on IL2CPP, NativeAOT and HybridCLR hot updates;
  Nino.Core is annotated for trimming and Native AOT. With the .NET 8 SDK or later, calls are intercepted to the
  exact formatter, with no virtual call.
- **Fast to build.** The generator is incremental: one file per type, and only the type you edit is
  re-generated. Compared with v3, the generated code is 14× smaller and compiles 51× (330 types) to 221× (1,100 types) faster.
- **Thread-safe.** Serialize and deserialize from any number of threads, with no locks on the hot path.
- **Polymorphism without tags.** A value declared as a base class or interface (abstract or not, generic or not)
  is read back as its runtime type. Type ids are derived from type names, so there is no tag table to maintain, and
  subtypes in other assemblies (plugins, HybridCLR hot updates) are found on their own.
  [Polymorphism](https://nino.xgamedev.net/en/docs/v4/polymorphism).
- **Data that evolves.** Version tolerance, keyed members, and reference tracking for shared references and
  cycles. All are opt-in per type, so types that don't use them pay nothing.
- **One format everywhere.** `netstandard2.0`, `netstandard2.1`, `net8.0` and `net10.0` write identical bytes on
  CoreCLR, Mono and IL2CPP, 64-bit and 32-bit, x64 and Arm64, so a Unity client and a .NET server talk to each other directly.
- **The types you already use.** Primitives, strings, tuples, every BCL collection, `BigInteger`, `Uri`, `Version`,
  `Memory<T>`, `ILookup<,>`, `Lazy<T>` and more, every immutable collection (`Nino.Immutable`), Unity's `AnimationCurve`,
  `Gradient` and `RectOffset`, your own collection types, and anything else through a pluggable formatter.
  [Supported types](https://nino.xgamedev.net/en/docs/v4/types).
- **ASP.NET Core, TypeScript, Go and Rust.** [Nino.AspNetCore](https://nino.xgamedev.net/en/docs/v4/advanced/aspnetcore)
  adds MVC formatters and Minimal API bodies and results for `application/x-nino`;
  [Nino.TypeScript](https://nino.xgamedev.net/en/docs/v4/advanced/typescript) generates TypeScript codecs for your types
  after each build, so browsers and Node read and write the same bytes as .NET; and the `nino-gen` tool (`Nino.Codegen`)
  writes your types as TypeScript, Go or Rust from a built assembly, for the runtimes in `runtimes/`
  ([docs/codegen.md](docs/codegen.md)).
- **Mistakes caught while you type.** 27 analyzer diagnostics (NINO001–NINO027), many with a one-click fix.

## Built for

| | |
|---|---|
| ⚙️ **[Services & IPC](https://nino.xgamedev.net/en/docs/v4/advanced/high-performance)** | Compact binary payloads, no per-call allocations, and safe concurrent use from ASP.NET Core, workers and actors. [Nino.AspNetCore](https://nino.xgamedev.net/en/docs/v4/advanced/aspnetcore) reads and writes `application/x-nino` bodies in MVC and Minimal APIs. |
| 🧩 **[Web & Node clients](https://nino.xgamedev.net/en/docs/v4/advanced/typescript)** | [Nino.TypeScript](https://nino.xgamedev.net/en/docs/v4/advanced/typescript) generates TypeScript classes and codecs for your `[NinoType]`s after each build, so browsers and Node read and write the same bytes as .NET. |
| 🌐 **[RPC & networking](https://nino.xgamedev.net/en/docs/v4/advanced/networking)** | Serialize straight into an `IBufferWriter<byte>` (a `PipeWriter`, a pooled buffer), deserialize from a `ReadOnlySpan<byte>`, and reuse pooled message objects. Polymorphic messages need no tags, and a crafted message throws one exception instead of taking the server down. |
| 📨 **[Message queues & event streams](https://nino.xgamedev.net/en/docs/v4/advanced/message-queues)** | Polymorphic event types behind one base type, small payloads, and formats that let producers and consumers upgrade at different times. |
| 🗄️ **[Caching](https://nino.xgamedev.net/en/docs/v4/advanced/caching)** | Values for Redis, `IDistributedCache`/`HybridCache` or in-process caches, with schema changes across deployments. |
| 💾 **[Persistence & storage](https://nino.xgamedev.net/en/docs/v4/advanced/persistence)** | Version tolerance and keyed members let you add, reorder and remove fields across releases. Reference tracking keeps object graphs with shared references and cycles. |
| 🎮 **[Unity](https://nino.xgamedev.net/en/docs/v4/advanced/unity)** | Unity 2022.3+, Mono and IL2CPP, HybridCLR hot-update assemblies. `Vector3`, `Quaternion`, `Color32` and other unmanaged structs are copied as raw memory; `AnimationCurve`, `Gradient` and `RectOffset` are built in. Install from OpenUPM. |
| 👾 **[Godot 4 (.NET)](https://nino.xgamedev.net/en/docs/v4/platforms)** | A regular NuGet package for Godot's .NET projects. Godot's math structs (`Vector3`, `Transform3D`, …) are unmanaged and copied as raw memory. |

## Install

> New to Nino? Follow **[Getting Started](https://nino.xgamedev.net/en/docs/v4/start)** on the website: installation for .NET, Unity and Godot, your first type, and the basics.

```bash
dotnet add package Nino --prerelease            # .NET, Godot
dotnet add package Nino.Immutable --prerelease  # optional: System.Collections.Immutable types
dotnet add package Nino.AspNetCore --prerelease # optional: ASP.NET Core MVC formatters and Minimal API bodies
dotnet add package Nino.TypeScript --prerelease # optional: TypeScript codecs for browsers and Node, after each build
dotnet tool install -g Nino.Codegen --prerelease # optional: nino-gen, your types as TypeScript, Go or Rust
openupm add com.jasonxudeveloper.nino           # Unity (generator and Unity class formatters included)
```

`Nino` brings in the runtime (`Nino.Serialization`) and the source generator (`Nino.Generator`). Nino v4 is a
prerelease, so `--prerelease` is required: without it NuGet installs 3.9.x, which cannot read or write v4 data.

## Quick start

```csharp
using Nino.Core;

[NinoType]
public class Order
{
    public long Id;
    public string Customer;
    public List<OrderLine> Lines;
}

byte[] bytes = NinoSerializer.Serialize(order);
Order copy = NinoDeserializer.Deserialize<Order>(bytes);
```

### Polymorphism without tags

```csharp
[NinoType] public abstract class Message { public int Seq; }
[NinoType] public sealed class Ping : Message { public long SentAt; }
[NinoType] public sealed class Chat : Message { public string Text; }

byte[] bytes = NinoSerializer.Serialize<Message>(new Chat { Text = "hi" }); // declared as the base: a type id is written
Message msg = NinoDeserializer.Deserialize<Message>(bytes);               // a Chat again: the type id picks the subclass
```

Serialize and deserialize through the common `[NinoType]` base (not `object`): generated code dispatches on the type id,
and reuse deserialization and pools work with it. `SerializeBoxed`/`DeserializeBoxed` exist only for a type known
only as a `Type` at run time that shares no `[NinoType]` base with the others.

### Zero-allocation networking

```csharp
// Serialize into the socket pipe: one copy from a pooled per-thread buffer, no byte[] allocated.
NinoSerializer.Serialize(msg, pipeWriter);

// Reuse pooled instances (and their lists) instead of allocating new ones.
[NinoType, NinoPooled]
public sealed partial class ReadingBatch
{
    public long Sequence;
    public List<SensorReading> Readings;

    private static partial IEnumerable<ReadingBatch> OnNinoPoolWarmup() => Enumerable.Range(0, 16).Select(_ => new ReadingBatch());
    private partial void OnNinoPoolReturn() { }
}

var batch = NinoDeserializer.Deserialize<ReadingBatch>(span); // taken from the pool
// ... process it ...
batch.ReturnToPool();
```

### Data that changes over time

```csharp
[NinoType(format: NinoFormat.Keyed)] // members are matched by name: add, reorder, remove or rename them
public class Profile { [NinoFormerName("Mail")] public string Email; public string Name; }

[NinoType(trackReferences: true)]    // shared references and cycles survive a round trip
public class Node { public string Name; public List<Node> Links; }
```

**[Read the documentation on nino.xgamedev.net →](https://nino.xgamedev.net/en/docs/v4/start)**, including
[advanced guides](https://nino.xgamedev.net/en/docs/v4/advanced) for high-performance serialization,
networking, ASP.NET Core, TypeScript clients, message queues, caching, persistence and Unity.

## Nino vs MemoryPack

Besides speed, the clearest difference is **untrusted input**. MemoryPack's README now calls it "a high-performance
serialization format for trusted data only" and no longer recommends it "where the input can come from an untrusted
party, such as public network endpoints or user-supplied files" ([MemoryPack README](https://github.com/Cysharp/MemoryPack#readme)). Nino is built for that
input: lengths and allocation are bounded by the bytes received, the stack by a depth limit, the data names no types,
and every invalid input throws one exception ([details](https://nino.xgamedev.net/en/docs/v4/vs-memorypack#untrusted-input)).

| | Nino v4 | MemoryPack 1.21.4 |
|---|---|---|
| Untrusted input | **built for it**: bounded allocation and stack, one exception type | trusted data only, per its README |
| Serialize classes and polymorphic types | **<!--bench:ser.range-->3.5–6.8<!--/bench-->× faster** | |
| Deserialize classes and polymorphic types | **<!--bench:deser.range-->1.3–1.9<!--/bench-->× faster** | |
| Raw struct arrays | at least on par (a block copy in both); v4 zeroes padding, so declare hot structs without it | copies padding as is |
| Deserialize into existing objects | **0 B, up to <!--bench:reuse.inplace-->8.7<!--/bench-->× faster** (in place) | overwrite mode, allocates |
| Polymorphism | automatic type ids, any base class or interface, generic bases included | `[MemoryPackUnion]` tags you maintain, on interfaces and abstract classes, not on generic types |
| Keyed members, reference tracking, object pools | ✅ | version tolerance and circular references, no pools |
| Unity | ✅ 2022.3+, one OpenUPM package, `netstandard2.0` and `2.1`, HybridCLR without an init call | ✅ 2022.3.12f1+, NuGetForUnity plus a git package, `netstandard2.1` |

### MemoryPack bugs we found while benchmarking

We round-trip every value through MemoryPack 1.21.4 before timing it. These fail ([details](https://nino.xgamedev.net/en/docs/v4/vs-memorypack#memorypack-bugs-found-while-benchmarking)):

- **`BigInteger`** of up to 255 bytes reads back as another value, on every runtime: its formatter writes
  `temp.Slice(written)` of its stack buffer where `temp.Slice(0, written)` is meant.
- **`BitArray` on .NET 10** is written wrong, and reading it corrupts the heap and crashes the process: the formatter
  reinterprets it with .NET 8's field layout, which .NET 10 changed.
- **Tuples of unmanaged items between runtimes**: `(1, 2L)` written on .NET reads back as `(2, 1)` on Mono and in
  Unity, because `ValueTuple` is laid out differently there and MemoryPack copies its memory, padding included.

Nino writes all three by their values, not by the runtime's private layout, so they round-trip everywhere.

MemoryPack still has `System.Type` (left out of Nino on purpose: data that names the types to create is an attack
vector). Nino.TypeScript generates TypeScript from the layouts the formatters are generated from, after each build,
structs and version-tolerant types included
([compared with MemoryPack's](https://nino.xgamedev.net/en/docs/v4/advanced/typescript#compared-with-memorypack),
which its README says will be removed in its next release),
and Nino.AspNetCore adds MVC formatters and Minimal API bodies; browser clients can also run the same C# types on
[.NET WebAssembly](https://nino.xgamedev.net/en/docs/v4/advanced/browser). Streams, async I/O, multi-segment
`ReadOnlySequence<byte>` input, callbacks and compression are a few lines around Nino's `IBufferWriter<byte>` and
`ReadOnlySpan<byte>` APIs; the [full comparison](https://nino.xgamedev.net/en/docs/v4/vs-memorypack) has examples.
MemoryPack's union tag is 1 byte where Nino writes a 4-byte type id, and that is a trade: the id is derived from the
type, so subtypes need no tags, can be added in any assembly (a plugin or a HybridCLR hot update) and never renumber,
while every MemoryPack union lists or registers its subtypes by hand.

## v4

v4 is a rewrite of the runtime and the generator. **Its binary format is not compatible with v3.**

| v4 vs v3 (3.3.13) | |
|---|---|
| Serialize | <!--bench:v3.ser-->**1.79× faster** (class), **2.29× faster** (class array), **faster** (struct, too fast to measure: under 1 ns), **1.13× faster** (struct array)<!--/bench--> |
| Deserialize | <!--bench:v3.deser-->**1.21× faster** (class), **1.21× faster** (class array), on par (struct), **1.07× faster** (struct array)<!--/bench-->, while adding reference tracking, reuse deserialization and payload validation |
| Compiling the generated code | **51× faster** at 330 types (42.9 s → 0.84 s), **221× faster** at 1,100 types (462 s → 2.1 s) |
| Running the generator | **3.1× faster**; after an edit, only that type is re-generated (**8.3× faster**) |
| Generated code | **14× smaller** source, **5× smaller** IL |

New in v4, through 4.0.0-rc.7:

- **Untrusted data** (rc.7): bounded allocation (at most 16× the bytes left ahead of the elements) and stack depth,
  one `InvalidOperationException` for every invalid input, checked with fuzzed and hand-crafted payloads.
- **Formats**: version-tolerant and keyed types, reference tracking, and the wire layout of every type published as
  `[assembly: NinoTypeLayout]` metadata, so a change in the bytes shows up between builds.
- **Allocation**: reuse deserialization into existing objects, `[NinoPooled]` pools, `[NinoFactory]`, and
  `NinoArrayBufferWriter`, which can rent from your own `ArrayPool<byte>`.
- **Types**: pluggable formatters (`[assembly: NinoFormatter(...)]`), user collection types, `BigInteger`, `Uri`,
  `Version`, `StringBuilder`, `BitArray`, `CultureInfo`, `TimeZoneInfo`, `Lazy<T>`, `Memory<T>`, `ReadOnlySequence<T>`,
  read-only and concurrent collections, `ILookup`, `BlockingCollection<T>`, `PriorityQueue` (.NET 8+), every immutable
  collection (`Nino.Immutable`) and Unity's `AnimationCurve`, `Gradient` and `RectOffset` (`Nino.UnityTypes`, in the
  Unity package).
- **Speed**: interceptors, inlined struct formatters, block copies for arrays and collections of raw structs,
  tuples and `KeyValuePair`s, and SIMD on AVX-512, AVX2, Arm64 and WebAssembly; releases are benchmarked on x64 and Arm64.
- **Platforms**: identical bytes on every target framework, runtime and CPU, trimming and Native AOT annotations,
  assemblies that unload with a collectible `AssemblyLoadContext` (`NinoRegistry.Remove` before .NET 8), and
  HybridCLR without an init call.
- **Strings**: text whose chars are all at most U+00FF (ASCII, Latin-1) is written in one byte a char, other text as
  UTF-16, and `[NinoUtf8]` writes UTF-8 per member.
- **Integrations**: Nino.AspNetCore (MVC formatters, Minimal API `NinoBody<T>` and `NinoResults`), Nino.TypeScript
  (TypeScript classes and codecs for browsers and Node, generated after each build) and `nino-gen` (Nino.Codegen: the
  same types and codecs in TypeScript, Go or Rust, from a built assembly).
- **Tooling**: an incremental generator and 27 analyzers (NINO001–NINO027) with code fixes, in Unity too.

See [what's new](https://nino.xgamedev.net/en/docs/v4/whats-new), the
[changelog](https://nino.xgamedev.net/en/docs/v4/changelog) and the
[migration guide](https://nino.xgamedev.net/en/docs/v4/migration); internals are in
[docs/v4-design.md](https://github.com/JasonXuDeveloper/Nino/blob/main/docs/v4-design.md).

## Contributing

Issues and pull requests are welcome. [CONTRIBUTING.md](https://github.com/JasonXuDeveloper/Nino/blob/main/CONTRIBUTING.md) covers building, the test matrix and
releasing.

<div align="center">

[MIT License](https://github.com/JasonXuDeveloper/Nino/blob/main/LICENSE) · Made by [JasonXuDeveloper](https://github.com/JasonXuDeveloper)

</div>
