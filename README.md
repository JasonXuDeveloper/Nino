<div align="center">

<img src="https://raw.githubusercontent.com/JasonXuDeveloper/Nino/main/docs/assets/logo.png" width="112" alt="Nino logo">

# Nino

**The fastest binary serializer for C#.**<br>
Source-generated, zero-allocation, thread-safe and safe on untrusted input.<br>
For .NET, Unity, Godot and HybridCLR, with the same bytes in Go, Rust and TypeScript.

[![Build](https://img.shields.io/github/actions/workflow/status/JasonXuDeveloper/Nino/ci.yml?branch=main&style=flat-square&label=build)](https://github.com/JasonXuDeveloper/Nino/actions)
[![NuGet](https://img.shields.io/nuget/vpre/Nino?label=NuGet&style=flat-square&logo=nuget)](https://www.nuget.org/packages/Nino)
[![OpenUPM](https://img.shields.io/npm/v/com.jasonxudeveloper.nino?label=OpenUPM&style=flat-square&logo=unity&registry_uri=https://package.openupm.com)](https://openupm.com/packages/com.jasonxudeveloper.nino/)
[![crates.io](https://img.shields.io/crates/v/nino-serialization?label=crates.io&style=flat-square&logo=rust)](https://crates.io/crates/nino-serialization)
[![npm](https://img.shields.io/npm/v/nino-serialization/next?label=npm&style=flat-square&logo=npm)](https://www.npmjs.com/package/nino-serialization)
[![Go Reference](https://img.shields.io/badge/-Go-00ADD8?style=flat-square&logo=go&logoColor=white)](https://pkg.go.dev/github.com/JasonXuDeveloper/Nino/runtimes/go/v4)

[🌐 **Website**](https://nino.xgamedev.net/en) ·
[🚀 **Getting Started**](https://nino.xgamedev.net/en/docs/v4/start) ·
[**What's new in v4**](https://nino.xgamedev.net/en/docs/v4/whats-new) ·
[**Benchmarks**](https://nino.xgamedev.net/en/docs/v4/benchmarks) ·
[**中文**](https://github.com/JasonXuDeveloper/Nino/blob/main/README.zh.md)

</div>

## Why Nino

- **Fastest.** Up to **<!--bench:ser.max-->6.4<!--/bench-->× faster** serialization than MemoryPack, and up to
  **<!--bench:reuse.inplace-->10.9<!--/bench-->× faster** deserialization into existing objects. [Benchmarks](#benchmarks)
- **Safe on untrusted input.** Crafted bytes cannot crash the process, read past the input, allocate out of proportion
  or name a type to create; every invalid input throws one `NinoDataException`. [Untrusted data](https://nino.xgamedev.net/en/docs/v4/security)
- **Zero allocation.** Serialize into an `IBufferWriter<byte>`, deserialize into existing or pooled objects: nothing
  allocated per call, thread-safe, with no locks on the hot path. [Zero allocation](https://nino.xgamedev.net/en/docs/v4/advanced/zero-allocation)
- **Everything at compile time.** No reflection, no IL emit, no registration, no init call: works on IL2CPP, Native AOT
  and HybridCLR hot updates, and compiles up to 221× faster than v3. [Platforms](https://nino.xgamedev.net/en/docs/v4/platforms)
- **One format everywhere.** Identical bytes on every runtime and CPU, and from Go, Rust and TypeScript through the
  code `nino-gen` writes for your types. [Other languages](https://nino.xgamedev.net/en/docs/v4/advanced/codegen)
- **Rich types that evolve.** Polymorphism without tags, keyed and version-tolerant types, reference tracking, every
  BCL collection. [Supported types](https://nino.xgamedev.net/en/docs/v4/types)
- **Mistakes caught as you type.** 33 analyzer diagnostics, many with a one-click fix. [Analyzers](https://nino.xgamedev.net/en/docs/v4/analyzers)

## Install

```bash
dotnet add package Nino --prerelease    # .NET and Godot: the runtime and the generator
openupm add com.jasonxudeveloper.nino   # Unity 2022.3+
```

Nino v4 is a prerelease, so `--prerelease` is required: without it NuGet installs 3.9.x, which cannot read or write v4
data. Optional packages: `Nino.Immutable` (System.Collections.Immutable), `Nino.AspNetCore` (MVC formatters, Minimal API
bodies and results), `Nino.Codegen` (your types as TypeScript, Go or Rust after each build; `Nino.Codegen.Tool` is the
same as the `nino-gen` command). [Installation for .NET, Unity and Godot](https://nino.xgamedev.net/en/docs/v4/start#installation-details).

**AI coding assistants**: `npx skills add JasonXuDeveloper/Nino` installs the
[`nino-serialization` skill](https://github.com/JasonXuDeveloper/Nino/blob/main/skills/nino-serialization/SKILL.md) (Claude Code, Codex, Cursor, Copilot and other
agents); the docs are also at https://nino.xgamedev.net/llms.txt.

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

## Benchmarks

<p align="center">
  <img src="https://raw.githubusercontent.com/JasonXuDeveloper/Nino/main/docs/assets/benchmark.png" alt="Nino v4 vs Nino v3 vs MemoryPack: serialize and deserialize speed" width="760">
</p>

<sub>BenchmarkDotNet <!--bench:env.bdn-->0.15.8<!--/bench-->, <!--bench:env.runtime-->.NET 10.0.14<!--/bench-->, server GC, <!--bench:env.cpu-->AMD EPYC 7763 2.72 GHz<!--/bench-->; Nino v4 and MemoryPack 1.21.4 in one run, Nino 3.3.13 from NuGet <!--bench:v3.when-->directly before it<!--/bench--> on the same machine. Payloads are about the same size (`SimpleClass`: <!--bench:payload.v4-->1,069<!--/bench--> bytes in v4, 1,152 in v3, <!--bench:payload.memorypack-->1,148<!--/bench--> in MemoryPack). Every case and the method: [docs/v4-benchmarks.md](https://github.com/JasonXuDeveloper/Nino/blob/main/docs/v4-benchmarks.md); the notes of each release carry that release's numbers.</sub>

Every case, Arm64, MessagePack and protobuf-net: [.NET benchmarks](https://nino.xgamedev.net/en/docs/v4/benchmarks);
[Unity with Burst](https://nino.xgamedev.net/en/docs/v4/benchmarks-unity);
[Go, Rust and TypeScript](https://nino.xgamedev.net/en/docs/v4/benchmarks-runtimes). Arrays of tuples and structs and
string conversion use AVX-512, AVX2, SSSE3, Arm64 and WebAssembly SIMD (Burst kernels in Unity), with portable code
writing the same bytes elsewhere: [SIMD](https://nino.xgamedev.net/en/docs/v4/simd).

## Built for

| | |
|---|---|
| ⚙️ **[Services & IPC](https://nino.xgamedev.net/en/docs/v4/advanced/high-performance)** | No per-call allocations, safe concurrent use; [ASP.NET Core](https://nino.xgamedev.net/en/docs/v4/advanced/aspnetcore) bodies as `application/x-nino`. |
| 🌐 **[RPC & networking](https://nino.xgamedev.net/en/docs/v4/advanced/networking)** | Straight into a `PipeWriter`, pooled messages, and a crafted message throws instead of taking the server down. |
| 📨 **[Message queues](https://nino.xgamedev.net/en/docs/v4/advanced/message-queues)** · 🗄️ **[Caching](https://nino.xgamedev.net/en/docs/v4/advanced/caching)** · 💾 **[Storage](https://nino.xgamedev.net/en/docs/v4/advanced/persistence)** | Polymorphic events, small payloads, and formats that let fields be added, reordered and removed across releases. |
| 🧩 **[Web, Node, Go & Rust](https://nino.xgamedev.net/en/docs/v4/advanced/codegen)** | Types and codecs generated after each build, so browsers and services read and write the same bytes as .NET. |
| 🎮 **[Unity](https://nino.xgamedev.net/en/docs/v4/advanced/unity)** | 2022.3+, Mono and IL2CPP, HybridCLR without an init call, Burst SIMD kernels, `AnimationCurve` and `Gradient` built in. |
| 👾 **[Godot 4 (.NET)](https://nino.xgamedev.net/en/docs/v4/advanced/godot)** | A regular NuGet package; `Vector3`, `Transform3D` and other math structs are copied as raw memory. |

## Compared with MemoryPack

MemoryPack's README now calls it "a high-performance serialization format for trusted data only"
([MemoryPack README](https://github.com/Cysharp/MemoryPack#readme)); Nino is built for data from network peers and
user files ([details](https://nino.xgamedev.net/en/docs/v4/vs-memorypack#untrusted-input)).

| | Nino v4 | MemoryPack 1.21.4 |
|---|---|---|
| Untrusted input | **built for it**: bounded allocation and stack, one exception type | trusted data only, per its README |
| Serialize classes and polymorphic types | **<!--bench:ser.range-->4.0–6.4<!--/bench-->× faster** | |
| Deserialize classes and polymorphic types | **<!--bench:deser.range-->1.8–2.2<!--/bench-->× faster** | |
| Raw struct arrays | at least on par without padding (a block copy in both); padding zeroed | padding copied as is, in the platform's layout |
| Deserialize into existing objects | **0 B, up to <!--bench:reuse.inplace-->10.9<!--/bench-->× faster** (in place) | overwrite mode, allocates |
| Polymorphism | automatic type ids, any base class or interface, generic bases included | `[MemoryPackUnion]` tags you maintain, not on generic types |
| Keyed members, reference tracking, object pools | ✅ | version tolerance and circular references, no pools |
| Unity | ✅ 2022.3+, one OpenUPM package, `netstandard2.0` and `2.1` | ✅ 2022.3.12f1+, NuGetForUnity plus a git package, `netstandard2.1` |

While benchmarking we found three types MemoryPack 1.21.4 does not round-trip (`BigInteger`, `BitArray` on .NET 10,
tuples between runtimes); Nino writes them by value. The [full comparison](https://nino.xgamedev.net/en/docs/v4/vs-memorypack)
covers them, what MemoryPack has that Nino does not, and how to switch. Also compared:
[MessagePack](https://nino.xgamedev.net/en/docs/v4/vs-messagepack) and
[protobuf-net](https://nino.xgamedev.net/en/docs/v4/vs-protobuf-net).

## v4 vs v3

v4 is a rewrite of the runtime and the generator. **Its binary format is not compatible with v3.**

| v4 vs v3 (3.3.13) | |
|---|---|
| Serialize | <!--bench:v3.ser-->**1.98× faster** (class), **2.37× faster** (class array), **1.22× faster** (struct array)<!--/bench--> |
| Deserialize | <!--bench:v3.deser-->**1.53× faster** (class), **1.47× faster** (class array), on par (struct), **1.08× faster** (struct array)<!--/bench-->, while adding reference tracking, reuse deserialization and payload validation |
| Compiling the generated code | **51× faster** at 330 types (42.9 s → 0.84 s), **221× faster** at 1,100 types (462 s → 2.1 s) |
| Running the generator | **3.1× faster**; after an edit, only that type is re-generated (**8.3× faster**) |
| Generated code | **14× smaller** source, **5× smaller** IL |

Everything new: [what's new in v4](https://nino.xgamedev.net/en/docs/v4/whats-new), the
[changelog](https://nino.xgamedev.net/en/docs/v4/changelog) and the
[migration guide](https://nino.xgamedev.net/en/docs/v4/migration); internals are in
[docs/v4-design.md](https://github.com/JasonXuDeveloper/Nino/blob/main/docs/v4-design.md).

## Contributing

Issues and pull requests are welcome. [CONTRIBUTING.md](https://github.com/JasonXuDeveloper/Nino/blob/main/CONTRIBUTING.md) covers building, the test matrix and
releasing.

<div align="center">

[MIT License](https://github.com/JasonXuDeveloper/Nino/blob/main/LICENSE) · Made by [JasonXuDeveloper](https://github.com/JasonXuDeveloper) · [Ask DeepWiki](https://deepwiki.com/JasonXuDeveloper/Nino)

</div>
