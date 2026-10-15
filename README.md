<div align="center">

# Nino

**Ultimate high-performance binary serialization library for C#**

[![Build Status](https://img.shields.io/github/actions/workflow/status/JasonXuDeveloper/Nino/.github/workflows/ci.yml?branch=main&style=flat-square)](https://github.com/JasonXuDeveloper/Nino/actions)
[![License](https://img.shields.io/github/license/JasonXuDeveloper/Nino?style=flat-square)](https://github.com/JasonXuDeveloper/Nino/blob/main/LICENSE)
[![NuGet](https://img.shields.io/nuget/v/Nino?label=NuGet&style=flat-square&logo=nuget)](https://www.nuget.org/packages/Nino)
[![OpenUPM](https://img.shields.io/npm/v/com.jasonxudeveloper.nino?label=OpenUPM&style=flat-square&logo=unity&registry_uri=https://package.openupm.com)](https://openupm.com/packages/com.jasonxudeveloper.nino/)
[![Ask DeepWiki](https://deepwiki.com/badge.svg)](https://deepwiki.com/JasonXuDeveloper/Nino)

[🌐 **Official Website**](https://nino.xgamedev.net/en/) • [📚 **Documentation**](https://nino.xgamedev.net/en/doc/start) • [🚀 **Performance**](https://nino.xgamedev.net/en/perf/micro) • [🇨🇳 **中文**](README.zh.md)

*Fast, flexible, and effortless C# binary serialization*

</div>

---

## ✨ Why Choose Nino?

<table>
<tr>
<td width="25%" align="center">
  <h3>🔧 Seamless Integration</h3>
  <p>Leverages C# Source Generators for automatic compile-time code generation. Zero manual setup required.</p>
</td>
<td width="25%" align="center">
  <h3>⚡ Blazing Performance</h3>
  <p>Engineered for high-throughput, low-latency scenarios with minimal GC pressure and memory allocation.</p>
</td>
<td width="25%" align="center">
  <h3>🎮 Unity Compatible</h3>
  <p>Works seamlessly with Unity projects and native Unity data types like Vector3 and Quaternion.</p>
</td>
<td width="25%" align="center">
  <h3>🛠️ Advanced Features</h3>
  <p>Handles complex scenarios like polymorphism, versioning, custom constructors, and private member serialization.</p>
</td>
</tr>
</table>

---

## 🎯 Core Features

### 🚀 **Performance & Reliability**
- **High-Speed Serialization**: Consistently ranks among the fastest C# binary serializers
- **Low Memory Footprint**: Minimal GC pressure and memory allocation
- **By-Reference Deserialization**: Deserialize directly into existing objects to eliminate allocation overhead
- **Thread-Safe Operations**: Fully concurrent serialization/deserialization without external locking
- **Data Integrity**: Built-in type checking ensures data consistency

### 🧩 **Comprehensive Type Support**
- **Primitives & Built-ins**: Full support for all C# primitive types (`int`, `float`, `DateTime`, etc.)
- **Modern C# Features**: `records`, `record structs`, `structs`, `classes`, and generics
- **Collections**: Any `IEnumerable<T>` including `List<T>`, `Dictionary<TKey,TValue>`, `HashSet<T>`, `ConcurrentDictionary<TKey,TValue>`
- **Advanced Generics**: Complex nested types like `Dictionary<string, List<CustomType[]>>`
- **Value Types**: `ValueTuple`, `Tuple`, `KeyValuePair<TKey,TValue>`, `Nullable<T>`

### 🎮 **Unity & Cross-Platform**
- **Unity Native Types**: `Vector3`, `Quaternion`, `Matrix4x4`, and other Unity-specific data types
- **Cross-Assembly Support**: Serialize types across different .NET assemblies and projects
- **Platform Agnostic**: Works seamlessly across different .NET implementations

### ⚙️ **Advanced Control**
- **Polymorphism**: Interface and abstract class serialization with type preservation
- **Custom Constructors**: `[NinoConstructor]` for immutable types and factory patterns
- **Versioning & Migration**: `[NinoMember]` ordering and `[NinoFormerName]` for backward compatibility
- **Privacy Control**: `[NinoType(true)]` to include private/protected members
- **Selective Serialization**: `[NinoIgnore]` to exclude specific fields
- **String Optimization**: `[NinoUtf8]` for efficient UTF-8 string handling

---

## 🆕 What's New in v4

v4 is a major rewrite. **Its binary format is not compatible with v3 or earlier v4 betas.**

- **Truly incremental source generator**: one file per `[NinoType]`, equatable models, no whole-compilation
  scans. Editing a type re-generates only that type, and generated code is a fraction of v3's.
- **Faster runtime**: struct-composed formatters the JIT/AOT compiler inlines, raw memory copies for
  unmanaged `[NinoType]` structs and their arrays/lists, no allocations beyond the result objects.
- **Thread safe and cache friendly**: lock-free, copy-on-write registries; no shared mutable state on hot paths.
- **One format everywhere**: `netstandard2.0`, `netstandard2.1`, `net8.0` and `net10.0` produce identical bytes.
- **Pooled deserialization**: mark a class `[NinoPooled]` and Nino deserializes into instances
  you hand back with `ReturnToPool()`, reusing their lists, arrays and dictionaries. Dropped
  instances (payload is `null`, list shrinks, type changes) go back to their pool or to `NinoRecycler<T>`.
- **No init calls**: generated registries are found lazily, so hot-update assemblies (HybridCLR) just work.
- **Opt-in version tolerance**: `[NinoType(versionTolerant: true)]` lets you append members at any depth.
- **User collections**: types derived from `List<T>`, `HashSet<T>`, `Dictionary<TKey, TValue>` and friends.

See [docs/v4-design.md](docs/v4-design.md) for the format and architecture, and
[docs/v4-benchmarks.md](docs/v4-benchmarks.md) for build-time and runtime numbers against v3 and MemoryPack.

---

## 📖 Quick Start

### Installation

**Standard .NET Projects:**
```bash
dotnet add package Nino
```

**Unity Projects (via OpenUPM):**
```bash
openupm add com.jasonxudeveloper.nino
```

### Basic Usage

```csharp
[NinoType]
public class GameData
{
    public int Score;
    public string PlayerName;
    public DateTime LastPlayed;
}

// Serialize
var data = new GameData { Score = 1000, PlayerName = "Player1", LastPlayed = DateTime.Now };
byte[] bytes = NinoSerializer.Serialize(data);

// Deserialize
var restored = NinoDeserializer.Deserialize<GameData>(bytes);
```

### Pooled deserialization

```csharp
[NinoType, NinoPooled]
public sealed partial class Packet
{
    public int Id;
    public List<int> Values;

    // Required (the analyzer offers a fix that adds both).
    static partial void OnNinoPoolWarmup() { for (int i = 0; i < 16; i++) new Packet().ReturnToPool(); }
    partial void OnNinoPoolReturn() { /* clear state Nino does not deserialize */ }
}

var packet = NinoDeserializer.Deserialize<Packet>(bytes); // reuses a pooled instance (and its list) if there is one
// ... use it ...
packet.ReturnToPool();                                      // returning it twice throws
```

**[📚 Full Documentation →](https://nino.xgamedev.net/en/doc/start)**

---

## 📊 Performance

Nino consistently delivers exceptional performance across various scenarios. See detailed benchmarks and comparisons with other popular serialization libraries.

**[🚀 View Benchmarks →](https://nino.xgamedev.net/en/perf/micro)**

For v4: [build time, generated code size and runtime vs v3 and MemoryPack](docs/v4-benchmarks.md).

---

## 🤝 Community & Support

<div align="center">

[![GitHub Issues](https://img.shields.io/github/issues/JasonXuDeveloper/Nino?style=flat-square)](https://github.com/JasonXuDeveloper/Nino/issues)
[![GitHub Stars](https://img.shields.io/github/stars/JasonXuDeveloper/Nino?style=flat-square)](https://github.com/JasonXuDeveloper/Nino/stargazers)
[![GitHub Forks](https://img.shields.io/github/forks/JasonXuDeveloper/Nino?style=flat-square)](https://github.com/JasonXuDeveloper/Nino/network)

**[🐛 Report Issues](https://github.com/JasonXuDeveloper/Nino/issues)** • **[💡 Feature Requests](https://github.com/JasonXuDeveloper/Nino/issues)** • **[🔀 Contribute](https://github.com/JasonXuDeveloper/Nino/pulls)**

</div>

---

<div align="center">

**Made with ❤️ by [JasonXuDeveloper](https://github.com/JasonXuDeveloper)**

*Licensed under [MIT License](LICENSE)*

</div>
