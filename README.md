<div align="center">

# Nino

**Ultimate high-performance binary serialization library for C#**

[![Build Status](https://img.shields.io/github/actions/workflow/status/JasonXuDeveloper/Nino/.github/workflows/ci.yml?branch=main&style=flat-square)](https://github.com/JasonXuDeveloper/Nino/actions)
[![License](https://img.shields.io/github/license/JasonXuDeveloper/Nino?style=flat-square)](https://github.com/JasonXuDeveloper/Nino/blob/main/LICENSE)
[![NuGet](https://img.shields.io/nuget/v/Nino?label=NuGet&style=flat-square&logo=nuget)](https://www.nuget.org/packages/Nino)
[![OpenUPM](https://img.shields.io/npm/v/com.jasonxudeveloper.nino?label=OpenUPM&style=flat-square&logo=unity&registry_uri=https://package.openupm.com)](https://openupm.com/packages/com.jasonxudeveloper.nino/)

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

**[📚 Full Documentation →](https://nino.xgamedev.net/en/doc/start)**

---

## 📊 Performance

Nino consistently delivers exceptional performance across various scenarios. See detailed benchmarks and comparisons with other popular serialization libraries.

**[🚀 View Benchmarks →](https://nino.xgamedev.net/en/perf/micro)**

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
