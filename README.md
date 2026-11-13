<div align="center">

<img src="https://raw.githubusercontent.com/JasonXuDeveloper/Nino/main/docs/assets/logo.png" width="112" alt="Nino logo">

# Nino

**The fastest binary serializer for C#.**<br>
Source-generated, zero-allocation, thread-safe and safe on untrusted input.<br>
For .NET, Unity (Mono and IL2CPP), Godot, Native AOT and HybridCLR, with Go, Rust and TypeScript writing and reading the identical bytes.

[![NuGet](https://img.shields.io/nuget/vpre/Nino?style=for-the-badge&logo=nuget&logoColor=white&label=NuGet&labelColor=004880&color=0078D4)](https://www.nuget.org/packages/Nino)
[![OpenUPM](https://img.shields.io/npm/v/com.jasonxudeveloper.nino?style=for-the-badge&logo=unity&logoColor=white&label=OpenUPM&labelColor=000000&color=3E4C59&registry_uri=https://package.openupm.com)](https://openupm.com/packages/com.jasonxudeveloper.nino/)
[![crates.io](https://img.shields.io/crates/v/nino-serialization?style=for-the-badge&logo=rust&logoColor=white&label=crates.io&labelColor=000000&color=CE422B)](https://crates.io/crates/nino-serialization)<br>
[![npm](https://img.shields.io/npm/v/nino-serialization/next?style=for-the-badge&logo=npm&logoColor=white&label=npm&labelColor=CB3837&color=E05D44)](https://www.npmjs.com/package/nino-serialization)
[![Go](https://img.shields.io/badge/Go-pkg.go.dev-00ADD8?style=for-the-badge&logo=go&logoColor=white&labelColor=007D9C)](https://pkg.go.dev/github.com/JasonXuDeveloper/Nino/runtimes/go/v4)<br>
[![Build](https://img.shields.io/github/actions/workflow/status/JasonXuDeveloper/Nino/ci.yml?branch=main&style=for-the-badge&logo=githubactions&logoColor=white&label=Build&labelColor=24292F)](https://github.com/JasonXuDeveloper/Nino/actions)
[![License](https://img.shields.io/github/license/JasonXuDeveloper/Nino?style=for-the-badge&logo=opensourceinitiative&logoColor=white&label=License&labelColor=24292F&color=3DA639)](https://github.com/JasonXuDeveloper/Nino/blob/main/LICENSE)
[![Docs](https://img.shields.io/badge/Docs-nino.xgamedev.net-6E56CF?style=for-the-badge&logo=readthedocs&logoColor=white&labelColor=24292F)](https://nino.xgamedev.net/en)
[![DeepWiki](https://img.shields.io/badge/DeepWiki-Ask-1F6FEB?style=for-the-badge&logo=bookstack&logoColor=white&labelColor=24292F)](https://deepwiki.com/JasonXuDeveloper/Nino)

[**Website**](https://nino.xgamedev.net/en) ·
[**Getting started**](https://nino.xgamedev.net/en/docs/v4/start) ·
[**What's new in v4**](https://nino.xgamedev.net/en/docs/v4/whats-new) ·
[**Benchmarks**](https://nino.xgamedev.net/en/docs/v4/benchmarks) ·
[**中文**](https://github.com/JasonXuDeveloper/Nino/blob/main/README.zh.md)

</div>

## Why Nino

- **Fastest.** On 12 real-world workloads it serializes **<!--bench:ser.range-->—<!--/bench-->×** faster than MemoryPack,
  **<!--bench:msgpack.ser.range-->—<!--/bench-->×** than MessagePack, **<!--bench:protobuf.ser.range-->—<!--/bench-->×** than protobuf-net,
  **<!--bench:gprotobuf.ser.range-->—<!--/bench-->×** than Google.Protobuf and **<!--bench:json.ser.range-->—<!--/bench-->×** than
  System.Text.Json. [Benchmarks](#benchmarks)
- **SIMD everywhere, Burst in Unity.** Hand-written vector paths for AVX-512, AVX2, SSSE3, Arm64 and WebAssembly move tuples,
  padded structs and strings; Burst kernels in Unity, vector kernels in Go, Rust and TypeScript, and a portable
  fallback that writes the same bytes. [SIMD](https://nino.xgamedev.net/en/docs/v4/simd)
- **Safe on untrusted input.** Crafted bytes cannot crash the process, read past the input, allocate out of proportion
  or name a type to create; every invalid input throws one `NinoDataException`. [Untrusted data](https://nino.xgamedev.net/en/docs/v4/security)
- **Zero allocation on hot paths.** Serialize into a reused `IBufferWriter<byte>`, deserialize into existing or pooled
  objects; thread-safe, no locks on the hot path. [Zero allocation](https://nino.xgamedev.net/en/docs/v4/advanced/zero-allocation)
- **Everything at compile time.** Generated formatters read and write your members directly: no reflection over them,
  no IL emit, no registration; runs on IL2CPP, Native AOT and HybridCLR hot updates. [Platforms](https://nino.xgamedev.net/en/docs/v4/platforms)
- **The same bytes everywhere.** Every runtime and CPU writes identical bytes, and so do Go, Rust and TypeScript,
  which read and write your types through the code `nino-gen` generates. [Other languages](https://nino.xgamedev.net/en/docs/v4/advanced/codegen)
- **Rich types that evolve.** Polymorphism without tags, version-tolerant and keyed types, reference tracking,
  serialization callbacks, every BCL collection. [Supported types](https://nino.xgamedev.net/en/docs/v4/types)
- **Mistakes caught as you type.** 44 analyzer diagnostics, many with a one-click fix, and a
  [layout baseline](https://nino.xgamedev.net/en/docs/v4/evolving-data#layout-baseline) that flags any change to a saved
  or sent type that old data or old builds could not read. [Analyzers](https://nino.xgamedev.net/en/docs/v4/analyzers)

## Install

### .NET and Godot

```bash
dotnet add package Nino --prerelease
```

The runtime and the source generator, for .NET Standard 2.0 to .NET 11, Native AOT and Godot 4. v4 is a prerelease:
without `--prerelease` NuGet installs 3.9.x, which cannot read v4 data. Add-ons: `Nino.Immutable`, `Nino.AspNetCore`, and `Nino.Codegen`, which writes your types as Go, Rust or TypeScript
after each build ([guide](https://nino.xgamedev.net/en/docs/v4/advanced/codegen)).

### Unity

```bash
openupm add com.jasonxudeveloper.nino
```

Unity 2022.3+, Mono and IL2CPP, HybridCLR. [Unity guide](https://nino.xgamedev.net/en/docs/v4/advanced/unity)

### Go

```bash
go get github.com/JasonXuDeveloper/Nino/runtimes/go/v4
```

Go 1.22+, no dependencies. Use the code `Nino.Codegen` writes, or map your own structs with struct tags.
[Go guide](https://nino.xgamedev.net/en/docs/v4/advanced/go)

### Rust

```bash
cargo add nino-serialization
```

Rust 1.88+. Use the code `Nino.Codegen` writes, or derive on your own structs with `--features derive`.
[Rust guide](https://nino.xgamedev.net/en/docs/v4/advanced/rust)

### TypeScript

```bash
npm install nino-serialization
```

Node.js and browsers. Optional: `Nino.Codegen` can write the runtime next to the code it generates.
[TypeScript guide](https://nino.xgamedev.net/en/docs/v4/advanced/typescript)

## Quick start

```csharp
using Nino.Core;

[NinoType]
public sealed class Order
{
    public long Id;
    public string Customer;
    public List<OrderLine> Lines;
}

byte[] bytes = NinoSerializer.Serialize(order);
Order copy = NinoDeserializer.Deserialize<Order>(bytes);
NinoDeserializer.Deserialize(bytes, ref copy); // fills the same instance in place
```

Polymorphism needs no tags: a value declared as its base is read back as its runtime type.

```csharp
[NinoType] public abstract class Message { public int Seq; }
[NinoType] public sealed class Chat : Message { public string Text; }

byte[] bytes = NinoSerializer.Serialize<Message>(new Chat { Text = "hi" });
Message msg = NinoDeserializer.Deserialize<Message>(bytes); // a Chat again
```

Next: [your first types](https://nino.xgamedev.net/en/docs/v4/start),
[evolving data](https://nino.xgamedev.net/en/docs/v4/evolving-data),
[reference tracking](https://nino.xgamedev.net/en/docs/v4/reference-tracking),
[pooling](https://nino.xgamedev.net/en/docs/v4/pooling) and the
[advanced guides](https://nino.xgamedev.net/en/docs/v4/advanced).

## AI coding assistants

```bash
npx skills add JasonXuDeveloper/Nino
```

The [`nino-serialization` skill](https://github.com/JasonXuDeveloper/Nino/blob/main/skills/nino-serialization/SKILL.md)
teaches Claude Code, Codex, Cursor, Copilot and other agents to declare `[NinoType]`s, pick the right format and fix
every NINO diagnostic. The docs are also in [llms.txt](https://nino.xgamedev.net/llms.txt) and
[llms-full.txt](https://nino.xgamedev.net/llms-full.txt).

## Benchmarks

| 12 real-world workloads | Serialize | Deserialize |
|---|---|---|
| vs MemoryPack | **<!--bench:ser.range-->—<!--/bench-->× faster** | **<!--bench:deser.range-->—<!--/bench-->× faster** |
| vs MessagePack | **<!--bench:msgpack.ser.range-->—<!--/bench-->× faster** | **<!--bench:msgpack.deser.range-->—<!--/bench-->× faster** |
| vs protobuf-net | **<!--bench:protobuf.ser.range-->—<!--/bench-->× faster** | **<!--bench:protobuf.deser.range-->—<!--/bench-->× faster** |
| vs Google.Protobuf | **<!--bench:gprotobuf.ser.range-->—<!--/bench-->× faster** | **<!--bench:gprotobuf.deser.range-->—<!--/bench-->× faster** |
| vs System.Text.Json | **<!--bench:json.ser.range-->—<!--/bench-->× faster** | **<!--bench:json.deser.range-->—<!--/bench-->× faster** |
| Into existing objects | — | **0 B, up to <!--bench:reuse.inplace-->9.5<!--/bench-->× faster** than MemoryPack |

The workloads: jvm-serializers' `MediaContent` and a Stack Exchange API page (the models of MessagePack's and
MemoryPack's benchmarks); an RPC call whose result is an interface, a cached profile written by the previous version,
an event store page of two-level event hierarchies in generic envelopes, and a log batch; and what games send: a
state-sync snapshot read into the client's copy, lockstep input frames, an MMO AOI update, an SLG map region and a card
battle replay that share objects, and an idle game's save from the previous version. Each library uses its fastest
API (Google.Protobuf its own generated messages, System.Text.Json source-generated UTF-8) and, where it lacks a
feature, its usual workaround (copies for shared objects, a new instance instead of reading into one). A range covers
the workloads Nino wins by more than 5%; every time is in the docs.

<!--bench:charts-->
<details open>
<summary><b>Basic types and structs</b></summary>

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="https://raw.githubusercontent.com/JasonXuDeveloper/Nino/main/docs/assets/benchmark-basic-dark.svg">
  <source media="(prefers-color-scheme: light)" srcset="https://raw.githubusercontent.com/JasonXuDeveloper/Nino/main/docs/assets/benchmark-basic.svg">
  <img src="https://raw.githubusercontent.com/JasonXuDeveloper/Nino/main/docs/assets/benchmark.png" alt="Basic types and structs: time per operation of Nino v4, v3 and every library, serialize and deserialize">
</picture>

</details>

<details open>
<summary><b>Polymorphism</b></summary>

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="https://raw.githubusercontent.com/JasonXuDeveloper/Nino/main/docs/assets/benchmark-polymorphism-dark.svg">
  <source media="(prefers-color-scheme: light)" srcset="https://raw.githubusercontent.com/JasonXuDeveloper/Nino/main/docs/assets/benchmark-polymorphism.svg">
  <img src="https://raw.githubusercontent.com/JasonXuDeveloper/Nino/main/docs/assets/benchmark-polymorphism.svg" alt="Polymorphism: time per operation of Nino v4, v3 and every library, serialize and deserialize">
</picture>

</details>

<details open>
<summary><b>Collections and common types</b></summary>

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="https://raw.githubusercontent.com/JasonXuDeveloper/Nino/main/docs/assets/benchmark-collections-dark.svg">
  <source media="(prefers-color-scheme: light)" srcset="https://raw.githubusercontent.com/JasonXuDeveloper/Nino/main/docs/assets/benchmark-collections.svg">
  <img src="https://raw.githubusercontent.com/JasonXuDeveloper/Nino/main/docs/assets/benchmark-collections.svg" alt="Collections and common types: time per operation of Nino v4, v3 and every library, serialize and deserialize">
</picture>

</details>

<details open>
<summary><b>Reuse and pooling</b></summary>

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="https://raw.githubusercontent.com/JasonXuDeveloper/Nino/main/docs/assets/benchmark-reuse-dark.svg">
  <source media="(prefers-color-scheme: light)" srcset="https://raw.githubusercontent.com/JasonXuDeveloper/Nino/main/docs/assets/benchmark-reuse.svg">
  <img src="https://raw.githubusercontent.com/JasonXuDeveloper/Nino/main/docs/assets/benchmark-reuse.svg" alt="Reuse and pooling: time per operation of Nino v4, v3 and every library, serialize and deserialize">
</picture>

</details>

<details open>
<summary><b>Reference tracking and version tolerance</b></summary>

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="https://raw.githubusercontent.com/JasonXuDeveloper/Nino/main/docs/assets/benchmark-tracking-dark.svg">
  <source media="(prefers-color-scheme: light)" srcset="https://raw.githubusercontent.com/JasonXuDeveloper/Nino/main/docs/assets/benchmark-tracking.svg">
  <img src="https://raw.githubusercontent.com/JasonXuDeveloper/Nino/main/docs/assets/benchmark-tracking.svg" alt="Reference tracking and version tolerance: time per operation of Nino v4, v3 and every library, serialize and deserialize">
</picture>

</details>

<details open>
<summary><b>Others</b></summary>

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="https://raw.githubusercontent.com/JasonXuDeveloper/Nino/main/docs/assets/benchmark-others-dark.svg">
  <source media="(prefers-color-scheme: light)" srcset="https://raw.githubusercontent.com/JasonXuDeveloper/Nino/main/docs/assets/benchmark-others.svg">
  <img src="https://raw.githubusercontent.com/JasonXuDeveloper/Nino/main/docs/assets/benchmark-others.svg" alt="Others: time per operation of Nino v4, v3 and every library, serialize and deserialize">
</picture>

</details>
<!--/bench-->

<sub>BenchmarkDotNet <!--bench:env.bdn-->0.15.8<!--/bench-->, <!--bench:env.runtime-->.NET 10.0.14<!--/bench-->, server GC, <!--bench:env.cpu-->Intel Xeon 6973P-C 3.54 GHz<!--/bench-->, on GitHub's runners; all libraries in one run, Nino 3.3.13 from NuGet <!--bench:v3.when-->directly before it<!--/bench--> on the same machine. The Stack Exchange page takes <!--bench:payload.v4-->—<!--/bench--> bytes in Nino, <!--bench:payload.memorypack-->—<!--/bench--> in MemoryPack, <!--bench:payload.msgpack-->—<!--/bench--> in MessagePack, <!--bench:payload.protobuf-->—<!--/bench--> in protobuf (both libraries) and <!--bench:payload.json-->—<!--/bench--> in JSON. Method and every case: [docs/v4-benchmarks.md](https://github.com/JasonXuDeveloper/Nino/blob/main/docs/v4-benchmarks.md).</sub>

More: [every .NET case and Arm64](https://nino.xgamedev.net/en/docs/v4/benchmarks),
[Unity with Burst](https://nino.xgamedev.net/en/docs/v4/benchmarks-unity) and
[Go, Rust and TypeScript](https://nino.xgamedev.net/en/docs/v4/benchmarks-runtimes).

## Built for

- **[Services & IPC](https://nino.xgamedev.net/en/docs/v4/advanced/high-performance)**: no per-call allocations, safe concurrent use; [ASP.NET Core](https://nino.xgamedev.net/en/docs/v4/advanced/aspnetcore) bodies as `application/x-nino`.
- **[RPC & networking](https://nino.xgamedev.net/en/docs/v4/advanced/networking)**: straight into a `PipeWriter` and out of a `PipeReader`'s `ReadOnlySequence<byte>`, pooled messages, and a crafted message throws instead of taking the server down.
- **[Message queues](https://nino.xgamedev.net/en/docs/v4/advanced/message-queues)**, **[caching](https://nino.xgamedev.net/en/docs/v4/advanced/caching)** and **[storage](https://nino.xgamedev.net/en/docs/v4/advanced/persistence)**: polymorphic events, small payloads, fields added, reordered and removed across releases.
- **[Web, Node, Go & Rust](https://nino.xgamedev.net/en/docs/v4/advanced/codegen)**: types and codecs generated after each build, writing and reading the same bytes as .NET.
- **[Unity](https://nino.xgamedev.net/en/docs/v4/advanced/unity)**: Mono and IL2CPP, HybridCLR without an init call, Burst SIMD kernels, `AnimationCurve` and `Gradient` built in.
- **[Godot 4 (.NET)](https://nino.xgamedev.net/en/docs/v4/advanced/godot)**: a regular NuGet package; `Vector3`, `Transform3D` and other math structs copied as raw memory.

## Compared with MemoryPack

MemoryPack describes itself as "for trusted data only" ([its README](https://github.com/Cysharp/MemoryPack#readme));
Nino is built for data from network peers and user files.

| | Nino v4 | MemoryPack 1.22.0 |
|---|---|---|
| Untrusted input | **built for it**: bounded allocation and stack, one exception type | trusted data only |
| Deserialize into existing objects | **0 B** (in place) | clears collections and allocates their elements again |
| Polymorphism | automatic type ids, any base class or interface, generic bases included | `[MemoryPackUnion]` tags you maintain, not on generic types |
| Keyed members, reference tracking, object pools | all three | version tolerance and circular references, no pools |
| Unity | 2022.3+, one OpenUPM package | 2022.3.12f1+, NuGetForUnity plus a git package |

The [full comparison](https://nino.xgamedev.net/en/docs/v4/vs-memorypack) covers the types MemoryPack does not
round-trip and how to switch; also [vs MessagePack](https://nino.xgamedev.net/en/docs/v4/vs-messagepack) and
[vs protobuf-net](https://nino.xgamedev.net/en/docs/v4/vs-protobuf-net).

## v4 vs v3

v4 rewrote the runtime and the generator; **its binary format is not compatible with v3.**

| v4 vs v3 (3.3.13) | |
|---|---|
| Serialize | <!--bench:v3.ser-->**2.11× faster** (class), **2.49× faster** (class array), **1.37× faster** (struct), on par (struct array)<!--/bench--> |
| Deserialize | <!--bench:v3.deser-->**1.55× faster** (class), **1.55× faster** (class array), on par (struct), on par (struct array)<!--/bench--> |
| Build | <!--bench:v3.build-->generated code compiles **221× faster** at 1,100 types (462 s → 2.1 s); **14× smaller** source<!--/bench--> |

[What's new in v4](https://nino.xgamedev.net/en/docs/v4/whats-new) · [Changelog](https://nino.xgamedev.net/en/docs/v4/changelog) · [Migration guide](https://nino.xgamedev.net/en/docs/v4/migration)

## Contributing

Issues and pull requests are welcome; see [CONTRIBUTING.md](https://github.com/JasonXuDeveloper/Nino/blob/main/CONTRIBUTING.md).
Report security issues privately as [SECURITY.md](https://github.com/JasonXuDeveloper/Nino/blob/main/SECURITY.md) describes.

## License

[MIT](https://github.com/JasonXuDeveloper/Nino/blob/main/LICENSE) © [JasonXuDeveloper](https://github.com/JasonXuDeveloper)
