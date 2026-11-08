<div align="center">

<img src="https://raw.githubusercontent.com/JasonXuDeveloper/Nino/main/docs/assets/logo.png" width="112" alt="Nino logo">

# Nino

**C# 最快的二进制序列化库。**<br>
源生成、零分配、线程安全，可放心读取不可信数据。<br>
支持 .NET、Unity、Godot 和 HybridCLR，Go、Rust 和 TypeScript 读写的也是同样的字节。

[![NuGet](https://img.shields.io/nuget/vpre/Nino?style=for-the-badge&logo=nuget&logoColor=white&label=NuGet&labelColor=004880&color=0078D4)](https://www.nuget.org/packages/Nino)
[![OpenUPM](https://img.shields.io/npm/v/com.jasonxudeveloper.nino?style=for-the-badge&logo=unity&logoColor=white&label=OpenUPM&labelColor=000000&color=3E4C59&registry_uri=https://package.openupm.com)](https://openupm.com/packages/com.jasonxudeveloper.nino/)
[![crates.io](https://img.shields.io/crates/v/nino-serialization?style=for-the-badge&logo=rust&logoColor=white&label=crates.io&labelColor=000000&color=CE422B)](https://crates.io/crates/nino-serialization)<br>
[![npm](https://img.shields.io/npm/v/nino-serialization/next?style=for-the-badge&logo=npm&logoColor=white&label=npm&labelColor=CB3837&color=E05D44)](https://www.npmjs.com/package/nino-serialization)
[![Go](https://img.shields.io/badge/Go-pkg.go.dev-00ADD8?style=for-the-badge&logo=go&logoColor=white&labelColor=007D9C)](https://pkg.go.dev/github.com/JasonXuDeveloper/Nino/runtimes/go/v4)<br>
[![Build](https://img.shields.io/github/actions/workflow/status/JasonXuDeveloper/Nino/ci.yml?branch=main&style=flat-square&logo=githubactions&logoColor=white&label=build)](https://github.com/JasonXuDeveloper/Nino/actions)
[![License](https://img.shields.io/github/license/JasonXuDeveloper/Nino?style=flat-square&color=blue)](https://github.com/JasonXuDeveloper/Nino/blob/main/LICENSE)
[![Docs](https://img.shields.io/badge/docs-nino.xgamedev.net-6E56CF?style=flat-square&logo=readthedocs&logoColor=white)](https://nino.xgamedev.net/zh)
[![Ask DeepWiki](https://img.shields.io/badge/Ask-DeepWiki-1F6FEB?style=flat-square)](https://deepwiki.com/JasonXuDeveloper/Nino)

[🌐 **官网**](https://nino.xgamedev.net/zh) ·
[🚀 **快速上手**](https://nino.xgamedev.net/zh/docs/v4/start) ·
[**v4 新特性**](https://nino.xgamedev.net/zh/docs/v4/whats-new) ·
[**性能**](https://nino.xgamedev.net/zh/docs/v4/benchmarks) ·
[**English**](https://github.com/JasonXuDeveloper/Nino/blob/main/README.md)

</div>

## 为什么选 Nino

- **最快。** 序列化比 MemoryPack **最高快 <!--bench:ser.max-->6.4<!--/bench--> 倍**，复用反序列化**最高快 <!--bench:reuse.inplace-->10.9<!--/bench--> 倍**。[性能](#性能)
- **可放心读取不可信数据。** 伪造的字节无法让进程崩溃、越界读取、分配不成比例的内存，也无法指定要创建的类型；所有无效输入都只抛出一种 `NinoDataException`。[不可信数据](https://nino.xgamedev.net/zh/docs/v4/security)
- **零分配。** 序列化到 `IBufferWriter<byte>`，反序列化到已有对象或池化对象：每次调用都不分配内存，线程安全，热路径上没有锁。[零分配](https://nino.xgamedev.net/zh/docs/v4/advanced/zero-allocation)
- **一切在编译期完成。** 没有反射、没有 IL emit、无需注册、无需初始化调用：IL2CPP、Native AOT 和 HybridCLR 热更都能直接用，编译比 v3 最多快 221 倍。[平台](https://nino.xgamedev.net/zh/docs/v4/platforms)
- **处处同一格式。** 每种运行时、每种 CPU 写出的字节都一样，Go、Rust 和 TypeScript 通过 `nino-gen` 为你的类型生成的代码也能读写。[其他语言](https://nino.xgamedev.net/zh/docs/v4/advanced/codegen)
- **类型丰富，数据可演进。** 多态无需标签，版本容错和键控类型，引用追踪，所有 BCL 集合。[支持的类型](https://nino.xgamedev.net/zh/docs/v4/types)
- **写代码时就发现错误。** 33 条分析器诊断，许多带一键修复。[分析器](https://nino.xgamedev.net/zh/docs/v4/analyzers)

## 安装

```bash
dotnet add package Nino --prerelease    # .NET 和 Godot：运行时和源生成器
openupm add com.jasonxudeveloper.nino   # Unity 2022.3+
```

Nino v4 是预发行版，所以必须加 `--prerelease`：不加时 NuGet 会安装 3.9.x，它读写不了 v4 的数据。可选包：`Nino.Immutable`（System.Collections.Immutable）、`Nino.AspNetCore`（MVC 格式化器，Minimal API 的请求体与结果）、`Nino.Codegen`（每次构建后把你的类型生成为 TypeScript、Go 或 Rust 代码；`Nino.Codegen.Tool` 是同样功能的 `nino-gen` 命令）。[.NET、Unity 和 Godot 的安装](https://nino.xgamedev.net/zh/docs/v4/start#installation-details)。

**AI 编程助手**：`npx skills add JasonXuDeveloper/Nino` 会安装 [`nino-serialization` skill](https://github.com/JasonXuDeveloper/Nino/blob/main/skills/nino-serialization/SKILL.md)（Claude Code、Codex、Cursor、Copilot 等都能用）；文档也有给模型读的版本：<https://nino.xgamedev.net/llms.txt>。

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
NinoDeserializer.Deserialize(bytes, ref copy); // 原地填充同一个实例
```

多态无需标签：以基类型声明的值，读回来仍是它的运行时类型。

```csharp
[NinoType] public abstract class Message { public int Seq; }
[NinoType] public sealed class Chat : Message { public string Text; }

byte[] bytes = NinoSerializer.Serialize<Message>(new Chat { Text = "hi" });
Message msg = NinoDeserializer.Deserialize<Message>(bytes); // 仍是 Chat
```

下一步：[第一个类型](https://nino.xgamedev.net/zh/docs/v4/start)、[数据演进](https://nino.xgamedev.net/zh/docs/v4/evolving-data)、[引用追踪](https://nino.xgamedev.net/zh/docs/v4/reference-tracking)、[对象池](https://nino.xgamedev.net/zh/docs/v4/pooling)和[高级用法](https://nino.xgamedev.net/zh/docs/v4/advanced)。

## 性能

<p align="center">
  <img src="https://raw.githubusercontent.com/JasonXuDeveloper/Nino/main/docs/assets/benchmark.png" alt="Nino v4、Nino v3 与 MemoryPack 的序列化/反序列化速度对比" width="760">
</p>

<sub>BenchmarkDotNet <!--bench:env.bdn-->0.15.8<!--/bench-->，<!--bench:env.runtime-->.NET 10.0.14<!--/bench-->，服务器 GC，<!--bench:env.cpu-->AMD EPYC 7763 2.72 GHz<!--/bench-->；Nino v4 与 MemoryPack 1.21.4 在同一次运行中测得，NuGet 上的 Nino 3.3.13 在同一台机器上<!--bench:v3.when-->紧接其前跑一次<!--/bench-->。数据大小基本一致（`SimpleClass`：v4 <!--bench:payload.v4-->1,069<!--/bench--> 字节，v3 1,152 字节，MemoryPack <!--bench:payload.memorypack-->1,148<!--/bench--> 字节）。全部用例与测试方法见 [docs/v4-benchmarks.md](https://github.com/JasonXuDeveloper/Nino/blob/main/docs/v4-benchmarks.md)；每个版本的发布说明里有该版本的实测数据。</sub>

全部用例、Arm64、MessagePack 和 protobuf-net：[.NET 基准测试](https://nino.xgamedev.net/zh/docs/v4/benchmarks)；[Unity 与 Burst](https://nino.xgamedev.net/zh/docs/v4/benchmarks-unity)；[Go、Rust 和 TypeScript](https://nino.xgamedev.net/zh/docs/v4/benchmarks-runtimes)。元组和结构体数组的搬运、字符串的转换使用 AVX-512、AVX2、SSSE3、Arm64 和 WebAssembly 的 SIMD（Unity 中用 Burst 内核），其他平台由可移植代码写出相同的字节：[SIMD](https://nino.xgamedev.net/zh/docs/v4/simd)。

## 适用场景

| | |
|---|---|
| ⚙️ **[服务与进程间通信](https://nino.xgamedev.net/zh/docs/v4/advanced/high-performance)** | 每次调用零分配，可安全并发使用；[ASP.NET Core](https://nino.xgamedev.net/zh/docs/v4/advanced/aspnetcore) 以 `application/x-nino` 读写请求体与响应。 |
| 🌐 **[RPC 与网络层](https://nino.xgamedev.net/zh/docs/v4/advanced/networking)** | 直接写进 `PipeWriter`，复用池化的消息；伪造的消息只会抛出异常，不会让服务端崩溃。 |
| 📨 **[消息队列](https://nino.xgamedev.net/zh/docs/v4/advanced/message-queues)** · 🗄️ **[缓存](https://nino.xgamedev.net/zh/docs/v4/advanced/caching)** · 💾 **[存储](https://nino.xgamedev.net/zh/docs/v4/advanced/persistence)** | 多态事件、小巧的数据，字段可以在版本之间增加、调整顺序和删除。 |
| 🧩 **[网页、Node、Go 与 Rust](https://nino.xgamedev.net/zh/docs/v4/advanced/codegen)** | 每次构建后生成类型和编解码器，浏览器和各种服务读写的字节与 .NET 相同。 |
| 🎮 **[Unity](https://nino.xgamedev.net/zh/docs/v4/advanced/unity)** | 2022.3+，Mono 和 IL2CPP，HybridCLR 无需初始化调用，Burst SIMD 内核，内置 `AnimationCurve` 和 `Gradient`。 |
| 👾 **[Godot 4（.NET）](https://nino.xgamedev.net/zh/docs/v4/advanced/godot)** | 直接引用 NuGet 包；`Vector3`、`Transform3D` 等数学结构体按原始内存直接拷贝。 |

## 与 MemoryPack 对比

MemoryPack 的 README 现在称自己是"仅用于可信数据的高性能序列化格式"（[MemoryPack README](https://github.com/Cysharp/MemoryPack#readme)）；Nino 正是为网络对端的消息和用户提供的文件这类数据设计的（[详情](https://nino.xgamedev.net/zh/docs/v4/vs-memorypack#untrusted-input)）。

| | Nino v4 | MemoryPack 1.21.4 |
|---|---|---|
| 不可信输入 | **设计目标**：分配和栈用量有上限，只有一种异常 | 按其 README，仅用于可信数据 |
| 序列化类和多态类型 | **快 <!--bench:ser.range-->4.0–6.4<!--/bench--> 倍** | |
| 反序列化类和多态类型 | **快 <!--bench:deser.range-->1.8–2.2<!--/bench--> 倍** | |
| 原始结构体数组 | 没有填充时至少持平（两者都是整块拷贝）；填充字节清零 | 原样复制填充字节，布局取决于平台 |
| 反序列化到已有对象 | **0 B，最多快 <!--bench:reuse.inplace-->10.9<!--/bench--> 倍**（复用反序列化） | 覆盖写入，有分配 |
| 多态 | 自动类型 ID，任何基类或接口，包括泛型基类 | 需要维护 `[MemoryPackUnion]` 标签，不支持泛型类型 |
| 键控成员、引用追踪、对象池 | ✅ | 有版本容错和循环引用，没有对象池 |
| Unity | ✅ 2022.3+，一个 OpenUPM 包，`netstandard2.0` 和 `2.1` | ✅ 2022.3.12f1+，NuGetForUnity 加一个 git 包，`netstandard2.1` |

基准测试时我们发现 MemoryPack 1.21.4 有三种类型无法正确往返（`BigInteger`、.NET 10 上的 `BitArray`、跨运行时的元组），Nino 按值写出它们。[完整对比](https://nino.xgamedev.net/zh/docs/v4/vs-memorypack)里有这些问题的细节、MemoryPack 有而 Nino 没有的功能，以及如何迁移。另有与 [MessagePack](https://nino.xgamedev.net/zh/docs/v4/vs-messagepack) 和[protobuf-net](https://nino.xgamedev.net/zh/docs/v4/vs-protobuf-net) 的对比。

## v4 对比 v3

v4 重写了运行时和生成器。**二进制格式与 v3 不兼容。**

| v4 对比 v3（3.3.13） | |
|---|---|
| 序列化 | <!--bench:v3.ser-->类**快 1.98 倍**，类数组**快 2.37 倍**，结构体数组**快 1.22 倍**<!--/bench--> |
| 反序列化 | <!--bench:v3.deser-->类**快 1.53 倍**，类数组**快 1.47 倍**，结构体持平，结构体数组**快 1.08 倍**<!--/bench-->，同时多了引用追踪、复用反序列化和数据校验 |
| 编译生成代码 | 330 个类型**快 51 倍**（42.9 秒 → 0.84 秒），1,100 个类型**快 221 倍**（462 秒 → 2.1 秒） |
| 运行生成器 | **快 3.1 倍**；改动后只重新生成该类型（**快 8.3 倍**） |
| 生成代码 | 源码**小 14 倍**，IL **小 5 倍** |

全部新特性见 [v4 新特性](https://nino.xgamedev.net/zh/docs/v4/whats-new)、[更新日志](https://nino.xgamedev.net/zh/docs/v4/changelog)和[迁移指南](https://nino.xgamedev.net/zh/docs/v4/migration)；内部设计见 [docs/v4-design.md](https://github.com/JasonXuDeveloper/Nino/blob/main/docs/v4-design.md)。

## 参与贡献

欢迎提交 Issue 和 Pull Request。构建、测试矩阵和发布流程见 [CONTRIBUTING.md](https://github.com/JasonXuDeveloper/Nino/blob/main/CONTRIBUTING.md)。

<div align="center">

MIT License · 作者 [JasonXuDeveloper](https://github.com/JasonXuDeveloper)

</div>
