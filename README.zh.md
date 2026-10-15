<div align="center">

# Nino

**终极高性能C#二进制序列化库**

[![构建状态](https://img.shields.io/github/actions/workflow/status/JasonXuDeveloper/Nino/.github/workflows/ci.yml?branch=main&style=flat-square)](https://github.com/JasonXuDeveloper/Nino/actions)
[![许可证](https://img.shields.io/github/license/JasonXuDeveloper/Nino?style=flat-square)](https://github.com/JasonXuDeveloper/Nino/blob/main/LICENSE)
[![NuGet](https://img.shields.io/nuget/v/Nino?label=NuGet&style=flat-square&logo=nuget)](https://www.nuget.org/packages/Nino)
[![OpenUPM](https://img.shields.io/npm/v/com.jasonxudeveloper.nino?label=OpenUPM&style=flat-square&logo=unity&registry_uri=https://package.openupm.com)](https://openupm.com/packages/com.jasonxudeveloper.nino/)
[![Ask DeepWiki](https://deepwiki.com/badge.svg)](https://deepwiki.com/JasonXuDeveloper/Nino)

[🌐 **官方网站**](https://nino.xgamedev.net/zh/) • [📚 **文档**](https://nino.xgamedev.net/zh/doc/start) • [🚀 **性能测试**](https://nino.xgamedev.net/zh/perf/micro) • [🇺🇸 **English**](README.md)

*快速、灵活、简单易用的C#二进制序列化* • **QQ群：[1054653934](https://qm.qq.com/q/cvalwQWRvU)**

</div>

---

## ✨ 为什么选择 Nino？

<table>
<tr>
<td width="25%" align="center">
  <h3>🔧 无缝集成</h3>
  <p>利用C# Source Generator技术实现编译时自动代码生成，零手动配置。</p>
</td>
<td width="25%" align="center">
  <h3>⚡ 极致性能</h3>
  <p>专为高吞吐量、低延迟场景设计，拥有极低的GC压力和内存分配。</p>
</td>
<td width="25%" align="center">
  <h3>🎮 Unity兼容</h3>
  <p>与Unity项目无缝协作，支持Vector3、Quaternion等Unity原生数据类型。</p>
</td>
<td width="25%" align="center">
  <h3>🛠️ 高级特性</h3>
  <p>处理复杂场景：多态、版本控制、自定义构造函数和私有成员序列化。</p>
</td>
</tr>
</table>

---

## 🎯 核心功能

### 🚀 **性能与可靠性**
- **高速序列化**：在C#二进制序列化库中始终名列前茅
- **低内存占用**：极低的GC压力和内存分配
- **引用反序列化**：直接反序列化到现有对象中，消除分配开销
- **线程安全操作**：完全支持并发序列化/反序列化，无需外部锁定
- **数据完整性**：内置类型检查确保数据一致性

### 🧩 **全面的类型支持**
- **基础类型**：完全支持所有C#基础类型（`int`、`float`、`DateTime`等）
- **现代C#特性**：`records`、`record structs`、`structs`、`classes`和泛型
- **集合类型**：任何`IEnumerable<T>`，包括`List<T>`、`Dictionary<TKey,TValue>`、`HashSet<T>`、`ConcurrentDictionary<TKey,TValue>`，以及 .NET 6+ 上的`PriorityQueue<TElement,TPriority>`
- **不可变集合**：安装 `Nino.Immutable` 包即可支持`ImmutableArray<T>`、`ImmutableList<T>`
- **高级泛型**：复杂嵌套类型如`Dictionary<string, List<CustomType[]>>`
- **值类型**：`ValueTuple`、`Tuple`、`KeyValuePair<TKey,TValue>`、`Nullable<T>`

### 🎮 **Unity与跨平台**
- **Unity原生类型**：`Vector3`、`Quaternion`、`Matrix4x4`和其他Unity特定数据类型
- **跨程序集支持**：在不同.NET程序集和项目间序列化类型
- **平台无关**：在不同.NET实现间无缝工作

### ⚙️ **高级控制**
- **多态支持**：接口和抽象类序列化，保留类型信息
- **自定义构造函数**：`[NinoConstructor]`用于不可变类型和工厂模式
- **版本控制与迁移**：`[NinoMember]`排序和`[NinoFormerName]`向后兼容
- **隐私控制**：`[NinoType(true)]`包含私有/受保护成员
- **选择性序列化**：`[NinoIgnore]`排除特定字段
- **字符串优化**：`[NinoUtf8]`高效UTF-8字符串处理

---

## 🆕 v4 新特性

v4 是一次大版本重写，**二进制格式与 v3 及之前的 v4 beta 不兼容。**

- **真正的增量源生成器**：每个 `[NinoType]` 一个文件，可比较的模型，不再扫描整个编译。修改一个类型只会重新生成该类型，
  生成代码量只有 v3 的一小部分。
- **更快的运行时**：基于结构体组合的格式化器，可被 JIT/AOT 内联；非托管 `[NinoType]` 结构体及其数组/列表直接内存拷贝；
  除结果对象外零分配。
- **线程安全、缓存友好**：无锁、写时复制的注册表；热路径上没有共享可变状态。
- **全平台同一格式**：`netstandard2.0`、`netstandard2.1`、`net8.0`、`net10.0` 输出完全相同的字节。
- **池化反序列化**：给类加上 `[NinoPooled]`，Nino 会反序列化到你用 `ReturnToPool()` 归还的实例里，
  复用其中的列表、数组和字典。被丢弃的实例（数据为 `null`、列表变短、类型变化）会回到对象池或交给
  `NinoSettings.SetRecycler<T>` 注册的处理函数。
- **无需手动初始化**：生成的注册表按需懒加载，HybridCLR 等热更程序集无需调用任何 Init。
- **可插拔的 formatter**：`[assembly: NinoFormatter(typeof(MyFormatter<,>))]` 即可让 Nino 支持新类型（自己的或第三方的）。类型参数和元素 formatter 自动推导，生成代码直接调用，运行时也使用同一份声明。Nino 自带的 BCL formatter 也是这样声明的。
- **可选的版本兼容**：`[NinoType(versionTolerant: true)]` 允许在类型本身或任意基类追加成员，嵌套深度不限。
- **自定义集合**：支持派生自 `List<T>`、`HashSet<T>`、`Dictionary<TKey, TValue>` 等的类型。

格式与架构详见 [docs/v4-design.md](docs/v4-design.md)。

---

## 📖 快速开始

### 安装

**标准.NET项目：**
```bash
dotnet add package Nino
# 可选：支持 ImmutableArray<T> / ImmutableList<T>
dotnet add package Nino.Immutable
```

**Unity项目（通过OpenUPM）：**
```bash
openupm add com.jasonxudeveloper.nino
```

### 基本用法

```csharp
[NinoType]
public class GameData
{
    public int Score;
    public string PlayerName;
    public DateTime LastPlayed;
}

// 序列化
var data = new GameData { Score = 1000, PlayerName = "Player1", LastPlayed = DateTime.Now };
byte[] bytes = NinoSerializer.Serialize(data);

// 反序列化
var restored = NinoDeserializer.Deserialize<GameData>(bytes);
```

### 池化反序列化

```csharp
[NinoType, NinoPooled]
public sealed partial class Packet
{
    public int Id;
    public List<int> Values;

    // 必须实现（analyzer 提供一键补全）。预热返回的实例直接放进池子。
    private static partial IEnumerable<Packet> OnNinoPoolWarmup() => Enumerable.Range(0, 16).Select(_ => new Packet());
    private partial void OnNinoPoolReturn() { /* 清理 Nino 不会反序列化的状态 */ }
}

var packet = NinoDeserializer.Deserialize<Packet>(bytes); // 池里有实例就复用（连同它的 List）
// ... 使用 ...
packet.ReturnToPool();                                      // 重复归还会抛异常
```

**[📚 完整文档 →](https://nino.xgamedev.net/zh/start)**

---

## 📊 性能表现

Nino在各种场景下都能提供卓越的性能表现。查看详细的基准测试和与其他流行序列化库的对比。

**[🚀 查看基准测试 →](https://nino.xgamedev.net/zh/perf/micro)**

---

## 🤝 社区与支持

<div align="center">

[![GitHub Issues](https://img.shields.io/github/issues/JasonXuDeveloper/Nino?style=flat-square)](https://github.com/JasonXuDeveloper/Nino/issues)
[![GitHub Stars](https://img.shields.io/github/stars/JasonXuDeveloper/Nino?style=flat-square)](https://github.com/JasonXuDeveloper/Nino/stargazers)
[![GitHub Forks](https://img.shields.io/github/forks/JasonXuDeveloper/Nino?style=flat-square)](https://github.com/JasonXuDeveloper/Nino/network)

**[🐛 报告问题](https://github.com/JasonXuDeveloper/Nino/issues)** • **[💡 功能建议](https://github.com/JasonXuDeveloper/Nino/issues)** • **[🔀 参与贡献](https://github.com/JasonXuDeveloper/Nino/pulls)**

**QQ群：[1054653934](https://qm.qq.com/q/cvalwQWRvU)**

</div>

---

<div align="center">

**Made with ❤️ by [JasonXuDeveloper](https://github.com/JasonXuDeveloper)**

*基于 [MIT 许可证](LICENSE)*

</div>
