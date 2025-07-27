<div align="center">

# Nino

**终极高性能C#二进制序列化库**

[![构建状态](https://img.shields.io/github/actions/workflow/status/JasonXuDeveloper/Nino/.github/workflows/ci.yml?branch=main&style=flat-square)](https://github.com/JasonXuDeveloper/Nino/actions)
[![许可证](https://img.shields.io/github/license/JasonXuDeveloper/Nino?style=flat-square)](https://github.com/JasonXuDeveloper/Nino/blob/main/LICENSE)
[![NuGet](https://img.shields.io/nuget/v/Nino?label=NuGet&style=flat-square&logo=nuget)](https://www.nuget.org/packages/Nino)
[![OpenUPM](https://img.shields.io/npm/v/com.jasonxudeveloper.nino?label=OpenUPM&style=flat-square&logo=unity&registry_uri=https://package.openupm.com)](https://openupm.com/packages/com.jasonxudeveloper.nino/)

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
- **集合类型**：任何`IEnumerable<T>`，包括`List<T>`、`Dictionary<TKey,TValue>`、`HashSet<T>`、`ConcurrentDictionary<TKey,TValue>`
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

## 📖 快速开始

### 安装

**标准.NET项目：**
```bash
dotnet add package Nino
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
