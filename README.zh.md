<div align="center">

<img src="https://raw.githubusercontent.com/JasonXuDeveloper/Nino/main/docs/assets/logo.png" width="112" alt="Nino logo">

# Nino

**C# 最快的二进制序列化库。**<br>
通用、源生成、零分配、线程安全：适用于 .NET 上的服务、RPC、<br>
消息、缓存和存储，并支持 Unity、Godot 和 HybridCLR。

[![Build](https://img.shields.io/github/actions/workflow/status/JasonXuDeveloper/Nino/ci.yml?branch=main&style=flat-square)](https://github.com/JasonXuDeveloper/Nino/actions)
[![NuGet](https://img.shields.io/nuget/v/Nino?label=NuGet&style=flat-square&logo=nuget)](https://www.nuget.org/packages/Nino)
[![OpenUPM](https://img.shields.io/npm/v/com.jasonxudeveloper.nino?label=OpenUPM&style=flat-square&logo=unity&registry_uri=https://package.openupm.com)](https://openupm.com/packages/com.jasonxudeveloper.nino/)
[![License](https://img.shields.io/github/license/JasonXuDeveloper/Nino?style=flat-square)](https://github.com/JasonXuDeveloper/Nino/blob/main/LICENSE)
[![Ask DeepWiki](https://img.shields.io/badge/Ask-DeepWiki-blue?style=flat-square)](https://deepwiki.com/JasonXuDeveloper/Nino)

[🌐 **官网**](https://nino.xgamedev.net/zh) ·
[🚀 **快速上手**](https://nino.xgamedev.net/zh/docs/v4/start) ·
[**v4 新特性**](https://nino.xgamedev.net/zh/docs/v4/whats-new) ·
[**性能**](https://nino.xgamedev.net/zh/docs/v4/perf) ·
[**对比 MemoryPack**](https://nino.xgamedev.net/zh/docs/v4/vs-memorypack) ·
[**English**](https://github.com/JasonXuDeveloper/Nino/blob/main/README.md)

序列化比 MemoryPack **最高快 <!--bench:ser.max-->5.8<!--/bench--> 倍**，原地反序列化快 **<!--bench:reuse.inplace-->10.7<!--/bench--> 倍**<br>
热路径 **零 GC 分配** · 编译比 v3 **快 221 倍**

</div>

<p align="center">
  <img src="https://raw.githubusercontent.com/JasonXuDeveloper/Nino/main/docs/assets/benchmark.png" alt="Nino v4、Nino v3 与 MemoryPack 的序列化/反序列化速度对比" width="760">
</p>

<sub>BenchmarkDotNet <!--bench:env.bdn-->0.15.8<!--/bench-->，<!--bench:env.runtime-->.NET 10.0.13<!--/bench-->，服务器 GC，<!--bench:env.cpu-->AMD EPYC 7763 3.10 GHz<!--/bench-->；Nino v4 与 MemoryPack 1.21.4 在同一次运行中测得，NuGet 上的 Nino 3.3.13 在同一台机器上<!--bench:v3.when-->紧接其前跑一次<!--/bench-->。数据大小基本一致（`SimpleClass`：v4 <!--bench:payload.v4-->1,150<!--/bench--> 字节，v3 1,152 字节，MemoryPack <!--bench:payload.memorypack-->1,148<!--/bench--> 字节）。全部用例与测试方法见 [docs/v4-benchmarks.md](https://github.com/JasonXuDeveloper/Nino/blob/main/docs/v4-benchmarks.md)；每个版本的发布说明里有该版本的实测数据。</sub>

## 为什么选 Nino

- **比 MemoryPack 更快。** 类和多态类型的序列化快 <!--bench:ser.range-->3.1–5.8<!--/bench--> 倍，反序列化快 <!--bench:deser.range-->1.4–1.9<!--/bench--> 倍；原始结构体数组两者都是整块拷贝，性能持平。
- **零分配。** 序列化到缓冲区（`IBufferWriter<byte>`）不分配内存。反序列化到已有对象或池化对象（`[NinoPooled]`、复用反序列化）同样不分配（内容不变的字符串直接保留），
  比 MemoryPack 的覆盖写入最多快 <!--bench:reuse.inplace-->10.7<!--/bench--> 倍。
- **一切在编译期完成。** 源生成器为每个类型生成格式化器：没有反射、没有 IL emit、无需注册、无需初始化调用，
  因此 IL2CPP、NativeAOT 和 HybridCLR 热更都能直接用。使用 .NET 8 及以上 SDK 时，调用会被拦截到确切的格式化器，没有虚调用。
- **编译快。** 生成器是真正增量的：每个类型一个文件，只重新生成你改动的类型。相比 v3，生成代码小 14 倍，编译快 51 倍（330 个类型）到 221 倍（1,100 个类型）。
- **线程安全。** 任意多个线程同时序列化、反序列化，热路径上没有锁。
- **数据可以演进。** 版本兼容、keyed 成员、引用追踪（共享引用和循环引用），都按类型按需开启，不用的类型零开销。
- **处处同一格式。** `netstandard2.0`、`netstandard2.1`、`net8.0`、`net10.0` 写出的字节完全一致，Unity 客户端和 .NET 服务端可以直接互通。
- **写代码时就发现错误。** 26 条分析器诊断（NINO001–NINO026），许多带一键修复。

## 适用场景

| | |
|---|---|
| ⚙️ **[服务与进程间通信](https://nino.xgamedev.net/zh/docs/v4/advanced/high-performance)** | 紧凑的二进制数据、每次调用零分配，可在 ASP.NET Core、后台任务和 Actor 中安全并发使用。 |
| 🌐 **[RPC 与网络层](https://nino.xgamedev.net/zh/docs/v4/advanced/networking)** | 直接序列化进 `IBufferWriter<byte>`（`PipeWriter`、池化缓冲区），从 `ReadOnlySpan<byte>` 反序列化，复用池化的消息对象。多态消息无需手写标签。 |
| 📨 **[消息队列与事件流](https://nino.xgamedev.net/zh/docs/v4/advanced/message-queues)** | 多态事件类型共用一个基类型，数据小巧，生产者和消费者可以在不同时间升级。 |
| 🗄️ **[缓存](https://nino.xgamedev.net/zh/docs/v4/advanced/caching)** | 作为 Redis、`IDistributedCache`/`HybridCache` 或进程内缓存的值格式，跨部署修改结构也没问题。 |
| 💾 **[持久化与存储](https://nino.xgamedev.net/zh/docs/v4/advanced/persistence)** | 版本兼容和 keyed 成员让你在版本之间增加、调整顺序、删除字段；引用追踪保留共享引用和循环引用的对象图。 |
| 🎮 **[Unity](https://nino.xgamedev.net/zh/docs/v4/advanced/unity)** | Unity 2022.3+，Mono 和 IL2CPP，HybridCLR 热更程序集。`Vector3`、`Quaternion`、`Color32` 等非托管结构体按原始内存直接拷贝。通过 OpenUPM 安装。 |
| 👾 **[Godot 4（.NET）](https://nino.xgamedev.net/zh/docs/v4/platforms)** | Godot 的 .NET 项目直接引用 NuGet 包。Godot 的数学结构体（`Vector3`、`Transform3D` 等）是非托管类型，按原始内存直接拷贝。 |

## 安装

> 第一次用 Nino？请看官网的 **[快速上手](https://nino.xgamedev.net/zh/docs/v4/start)**：.NET、Unity 和 Godot 的安装，第一个类型，以及基础用法。

```bash
dotnet add package Nino --prerelease            # .NET、Godot
dotnet add package Nino.Immutable --prerelease  # 可选：ImmutableArray<T> / ImmutableList<T>
openupm add com.jasonxudeveloper.nino           # Unity
```

## 快速开始

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

### 多态，无需标签

```csharp
[NinoType] public abstract class Message { public int Seq; }
[NinoType] public sealed class Ping : Message { public long SentAt; }
[NinoType] public sealed class Chat : Message { public string Text; }

byte[] bytes = NinoSerializer.Serialize<Message>(new Chat { Text = "hi" }); // 以基类型声明：写入类型 ID
Message msg = NinoDeserializer.Deserialize<Message>(bytes);               // 读回来仍是 Chat：由类型 ID 决定子类
```

通过共同的 `[NinoType]` 基类型（而不是 `object`）序列化和反序列化：生成代码按类型 ID 分派，复用反序列化和对象池也都能用。
`SerializeBoxed`/`DeserializeBoxed` 只用于类型在运行时才以 `Type` 形式给出、且与其他类型没有共同 `[NinoType]` 基类型的情况。

### 零分配网络层

```csharp
// 序列化进 socket 管道：从每线程的池化缓冲区拷贝一次，不分配 byte[]。
NinoSerializer.Serialize(msg, pipeWriter);

// 复用池化实例（连同它们的列表），而不是每次新建。
[NinoType, NinoPooled]
public sealed partial class ReadingBatch
{
    public long Sequence;
    public List<SensorReading> Readings;

    private static partial IEnumerable<ReadingBatch> OnNinoPoolWarmup() => Enumerable.Range(0, 16).Select(_ => new ReadingBatch());
    private partial void OnNinoPoolReturn() { }
}

var batch = NinoDeserializer.Deserialize<ReadingBatch>(span); // 从池中取出
// ... 处理 ...
batch.ReturnToPool();
```

### 会变化的数据

```csharp
[NinoType(format: NinoFormat.Keyed)] // 按成员名匹配：可以增加、调整顺序、删除、重命名成员
public class Profile { [NinoFormerName("Mail")] public string Email; public string Name; }

[NinoType(trackReferences: true)]    // 共享引用和循环引用在往返后保持不变
public class Node { public string Name; public List<Node> Links; }
```

**[在官网 nino.xgamedev.net 阅读文档 →](https://nino.xgamedev.net/zh/docs/v4/start)**，包括高性能序列化、网络层、消息队列、缓存、持久化和 Unity 的
[高级用法](https://nino.xgamedev.net/zh/docs/v4/advanced)。

## Nino 与 MemoryPack

| | Nino v4 | MemoryPack 1.21.4 |
|---|---|---|
| 序列化类和多态类型 | **快 <!--bench:ser.range-->3.1–5.8<!--/bench--> 倍** | |
| 反序列化类和多态类型 | **快 <!--bench:deser.range-->1.4–1.9<!--/bench--> 倍** | |
| 原始结构体数组 | 持平（两者都是整块拷贝）；v4 会清零填充字节，热点结构体建议声明为无填充 | 原样复制填充字节 |
| 反序列化到已有对象 | **0 B，最多快 <!--bench:reuse.inplace-->10.7<!--/bench--> 倍**（原地复用） | 覆盖写入，有分配 |
| 多态 | 自动类型 ID | 需要维护 `[MemoryPackUnion]` 标签 |
| keyed 成员、引用追踪、对象池 | ✅ | 有版本兼容和循环引用，没有对象池 |
| Unity | 2022.3+，`netstandard2.0` 和 `2.1`，HybridCLR | `netstandard2.1` |

MemoryPack 内置支持的类型更多（`BigInteger`、`Uri`、全部不可变集合、`AnimationCurve` 等 Unity 类），还有 TypeScript 生成和
ASP.NET Core 格式化器。Nino 不生成 TypeScript：浏览器客户端在 .NET WebAssembly 上运行同一套 C# 类型（构建成 `browser-wasm`
应用，通过 `[JSExport]` 供 JavaScript 调用，字节与服务端相同；[指南](https://nino.xgamedev.net/zh/docs/v4/advanced/browser)）。流、异步读写、多段 `ReadOnlySequence<byte>` 输入、回调和压缩，只需在 Nino 的 `IBufferWriter<byte>`
和 `ReadOnlySpan<byte>` API 外包几行代码，[完整对比](https://nino.xgamedev.net/zh/docs/v4/vs-memorypack)里有示例。
MemoryPack 的 Union 标签是 1 字节，Nino 的类型 ID 是 4 字节，这是有意的取舍：类型 ID 由类型本身算出，子类不用打标签，
可以定义在任何程序集里（插件、HybridCLR 热更程序集），也不会因为增删子类而重新编号；MemoryPack 的每个 Union 都要手动列出或注册子类。

## v4

v4 重写了运行时和生成器。**二进制格式与 v3 不兼容。**

| v4 对比 v3（3.3.13） | |
|---|---|
| 序列化 | <!--bench:v3.ser-->类**快 1.69 倍**，类数组**快 1.89 倍**，结构体数组**快 1.25 倍**<!--/bench--> |
| 反序列化 | <!--bench:v3.deser-->类持平，类数组**快 1.15 倍**，结构体**快 1.07 倍**，结构体数组持平<!--/bench-->，同时多了引用追踪、复用反序列化和数据校验 |
| 编译生成代码 | 330 个类型**快 51 倍**（42.9 秒 → 0.84 秒），1,100 个类型**快 221 倍**（462 秒 → 2.1 秒） |
| 运行生成器 | **快 3.1 倍**；改动后只重新生成该类型（**快 8.3 倍**） |
| 生成代码 | 源码**小 14 倍**，IL **小 5 倍** |

v4 新增：拦截器、keyed 成员、引用追踪、`[NinoPooled]` 对象池、可插拔格式化器（`[assembly: NinoFormatter(...)]`）、
自定义集合类型、分析器与代码修复，以及所有目标框架字节一致。详见 [v4 新特性](https://nino.xgamedev.net/zh/docs/v4/whats-new)
和[迁移指南](https://nino.xgamedev.net/zh/docs/v4/migration)；内部设计见 [docs/v4-design.md](https://github.com/JasonXuDeveloper/Nino/blob/main/docs/v4-design.md)。

## 参与贡献

欢迎提交 Issue 和 Pull Request。构建、测试矩阵和发布流程见 [CONTRIBUTING.md](https://github.com/JasonXuDeveloper/Nino/blob/main/CONTRIBUTING.md)。

<div align="center">

[MIT License](https://github.com/JasonXuDeveloper/Nino/blob/main/LICENSE) · 作者 [JasonXuDeveloper](https://github.com/JasonXuDeveloper)

</div>
