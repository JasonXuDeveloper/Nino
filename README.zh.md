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

序列化比 MemoryPack **最高快 <!--bench:ser.max-->6.8<!--/bench--> 倍**，原地反序列化**最高快 <!--bench:reuse.inplace-->8.7<!--/bench--> 倍**<br>
热路径 **零 GC 分配** · 编译比 v3 **快 221 倍**

</div>

> [!IMPORTANT]
> **Nino 本来就是为读取不可信数据而设计的**：网络对端的消息、用户交给你的文件、请求体。
> 伪造的字节无法让进程崩溃，无法越过输入末尾读取，无法分配与自身大小不成比例的内存（一个集合在读到元素之前最多预先分配剩余字节数的 16 倍），
> 也无法指定要创建的类型：数据里只有你声明的子类型的类型 id，没有类型名。所有无效输入都抛出同一种异常，
> 即 `NinoDataException`（派生自 `InvalidOperationException`，消息以 `Nino:` 开头），在边界处一个 `catch` 就能处理。
> 为在哈希表中冲突而构造的键也会抛出异常（`NinoSettings.MaxHashCollisions`），`NinoSettings.MaxDepth` 和
> `MaxCollectionLength` 可以收紧限制。读取外部数据时，在启动时设置 `NinoSettings.Untrusted = true`：Nino 创建的字典和集合用每个进程随机生成的密钥对整数、`char`、`Guid` 和 `string` 键求哈希，
> `bool`、`DateTime`、`decimal`、`DateOnly` 和 `TimeOnly` 的值（包括原始结构体内部的）都会检查，值之后多余的字节会被拒绝；
> 关闭时（默认）这些都没有任何开销。
> 我们用变异 fuzz 和手工构造的数据检验了每个内置类型，发现的问题都已在 4.0.0-rc.7 修复。
> [Nino 检查什么、哪些仍需你的代码负责](https://nino.xgamedev.net/zh/docs/v4/security)。（MemoryPack 现在
> [建议只用于可信数据](https://github.com/Cysharp/MemoryPack#readme)；参见[对比](https://nino.xgamedev.net/zh/docs/v4/vs-memorypack#untrusted-input)。）

<p align="center">
  <img src="https://raw.githubusercontent.com/JasonXuDeveloper/Nino/main/docs/assets/benchmark.png" alt="Nino v4、Nino v3 与 MemoryPack 的序列化/反序列化速度对比" width="760">
</p>

<sub>BenchmarkDotNet <!--bench:env.bdn-->0.15.8<!--/bench-->，<!--bench:env.runtime-->.NET 10.0.13<!--/bench-->，服务器 GC，<!--bench:env.cpu-->INTEL XEON PLATINUM 8573C 3.50 GHz<!--/bench-->；Nino v4 与 MemoryPack 1.21.4 在同一次运行中测得，NuGet 上的 Nino 3.3.13 在同一台机器上<!--bench:v3.when-->紧接其前跑一次<!--/bench-->。数据大小基本一致（`SimpleClass`：v4 <!--bench:payload.v4-->1,104<!--/bench--> 字节，v3 1,152 字节，MemoryPack <!--bench:payload.memorypack-->1,148<!--/bench--> 字节）。全部用例与测试方法见 [docs/v4-benchmarks.md](https://github.com/JasonXuDeveloper/Nino/blob/main/docs/v4-benchmarks.md)；每个版本的发布说明里有该版本的实测数据。</sub>

## 为什么选 Nino

- **比 MemoryPack 更快。** 类和多态类型的序列化快 <!--bench:ser.range-->3.5–6.8<!--/bench--> 倍，反序列化快 <!--bench:deser.range-->1.3–1.9<!--/bench--> 倍；原始结构体数组两者都是整块拷贝，性能至少持平。
- **可读取不可信数据。** 伪造的字节无法让进程崩溃、无法分配与自身大小不成比例的内存，也无法让 Nino 创建你没有声明的类型；
  所有无效输入都抛出同一种 `NinoDataException`。[不可信数据](https://nino.xgamedev.net/zh/docs/v4/security)。
- **零分配。** 序列化到缓冲区（`IBufferWriter<byte>`）不分配内存。反序列化到已有对象或池化对象（`[NinoPooled]`、复用反序列化）同样不分配（内容不变的字符串直接保留），
  比 MemoryPack 的覆盖写入最多快 <!--bench:reuse.inplace-->8.7<!--/bench--> 倍。
- **一切在编译期完成。** 源生成器为每个类型生成格式化器：没有反射、没有 IL emit、无需注册、无需初始化调用，
  因此 IL2CPP、NativeAOT 和 HybridCLR 热更都能直接用；Nino.Core 带有裁剪与 Native AOT 标注。使用 .NET 8 及以上 SDK 时，调用会被拦截到确切的格式化器，没有虚调用。
- **编译快。** 生成器是真正增量的：每个类型一个文件，只重新生成你改动的类型。相比 v3，生成代码小 14 倍，编译快 51 倍（330 个类型）到 221 倍（1,100 个类型）。
- **线程安全。** 任意多个线程同时序列化、反序列化，热路径上没有锁。
- **多态无需标签。** 声明为基类或接口（抽象或非抽象、泛型或非泛型）的值会按运行时类型读回。
  类型 ID 由类型名算出，不用维护标签表，其他程序集里的子类（插件、HybridCLR 热更）也会自动找到。
  [多态](https://nino.xgamedev.net/zh/docs/v4/polymorphism)。
- **数据可以演进。** 版本兼容、keyed 成员、引用追踪（共享引用和循环引用），都按类型按需开启，不用的类型零开销。
- **处处同一格式。** `netstandard2.0`、`netstandard2.1`、`net8.0`、`net10.0` 在 CoreCLR、Mono 和 IL2CPP 上，64 位和 32 位、x64 和 Arm64 上写出的字节完全一致，Unity 客户端和 .NET 服务端可以直接互通。
- **你已在用的类型。** 基元类型、字符串、元组、所有 BCL 集合、`BigInteger`、`Uri`、`Version`、`Memory<T>`、`ILookup<,>`、`Lazy<T>` 等，
  所有不可变集合（`Nino.Immutable`），Unity 的 `AnimationCurve`、`Gradient` 和 `RectOffset`，你自己的集合类型，其他类型则通过可插拔的格式化器支持。
  [支持的类型](https://nino.xgamedev.net/zh/docs/v4/types)。
- **ASP.NET Core、TypeScript、Go 与 Rust。** [Nino.AspNetCore](https://nino.xgamedev.net/zh/docs/v4/advanced/aspnetcore) 为 `application/x-nino`
  提供 MVC 格式化器以及 Minimal API 的请求体和结果；[Nino.TypeScript](https://nino.xgamedev.net/zh/docs/v4/advanced/typescript)
  在每次构建后为你的类型生成 TypeScript 编解码器，浏览器和 Node 读写的字节与 .NET 相同；`nino-gen` 工具（`Nino.Codegen`）
  从构建好的程序集把你的类型生成为 TypeScript、Go 或 Rust 代码，配合 `runtimes/` 中的运行时使用（[docs/codegen.md](docs/codegen.md)）。
- **写代码时就发现错误。** 27 条分析器诊断（NINO001–NINO027），许多带一键修复。

## 适用场景

| | |
|---|---|
| ⚙️ **[服务与进程间通信](https://nino.xgamedev.net/zh/docs/v4/advanced/high-performance)** | 紧凑的二进制数据、每次调用零分配，可在 ASP.NET Core、后台任务和 Actor 中安全并发使用。[Nino.AspNetCore](https://nino.xgamedev.net/zh/docs/v4/advanced/aspnetcore) 在 MVC 和 Minimal API 中读写 `application/x-nino` 请求体与响应。 |
| 🧩 **[网页与 Node 客户端](https://nino.xgamedev.net/zh/docs/v4/advanced/typescript)** | [Nino.TypeScript](https://nino.xgamedev.net/zh/docs/v4/advanced/typescript) 在每次构建后为你的 `[NinoType]` 生成 TypeScript 类和编解码器，浏览器和 Node 读写的字节与 .NET 相同。 |
| 🌐 **[RPC 与网络层](https://nino.xgamedev.net/zh/docs/v4/advanced/networking)** | 直接序列化进 `IBufferWriter<byte>`（`PipeWriter`、池化缓冲区），从 `ReadOnlySpan<byte>` 反序列化，复用池化的消息对象。多态消息无需手写标签；伪造的消息只会抛出一种异常，不会让服务端崩溃。 |
| 📨 **[消息队列与事件流](https://nino.xgamedev.net/zh/docs/v4/advanced/message-queues)** | 多态事件类型共用一个基类型，数据小巧，生产者和消费者可以在不同时间升级。 |
| 🗄️ **[缓存](https://nino.xgamedev.net/zh/docs/v4/advanced/caching)** | 作为 Redis、`IDistributedCache`/`HybridCache` 或进程内缓存的值格式，跨部署修改结构也没问题。 |
| 💾 **[持久化与存储](https://nino.xgamedev.net/zh/docs/v4/advanced/persistence)** | 版本兼容和 keyed 成员让你在版本之间增加、调整顺序、删除字段；引用追踪保留共享引用和循环引用的对象图。 |
| 🎮 **[Unity](https://nino.xgamedev.net/zh/docs/v4/advanced/unity)** | Unity 2022.3+，Mono 和 IL2CPP，HybridCLR 热更程序集。`Vector3`、`Quaternion`、`Color32` 等非托管结构体按原始内存直接拷贝；`AnimationCurve`、`Gradient` 和 `RectOffset` 已内置。通过 OpenUPM 安装。 |
| 👾 **[Godot 4（.NET）](https://nino.xgamedev.net/zh/docs/v4/platforms)** | Godot 的 .NET 项目直接引用 NuGet 包。Godot 的数学结构体（`Vector3`、`Transform3D` 等）是非托管类型，按原始内存直接拷贝。 |

## 安装

> 第一次用 Nino？请看官网的 **[快速上手](https://nino.xgamedev.net/zh/docs/v4/start)**：.NET、Unity 和 Godot 的安装，第一个类型，以及基础用法。

```bash
dotnet add package Nino --prerelease            # .NET、Godot
dotnet add package Nino.Immutable --prerelease  # 可选：System.Collections.Immutable 的类型
dotnet add package Nino.AspNetCore --prerelease # 可选：ASP.NET Core MVC 格式化器与 Minimal API 请求体
dotnet add package Nino.TypeScript --prerelease # 可选：每次构建后生成供浏览器和 Node 使用的 TypeScript 编解码器
dotnet tool install -g Nino.Codegen --prerelease # 可选：nino-gen，把你的类型生成为 TypeScript、Go 或 Rust 代码
openupm add com.jasonxudeveloper.nino           # Unity（已包含生成器和 Unity 类的格式化器）
```

`Nino` 包会引入运行时（`Nino.Serialization`）和源生成器（`Nino.Generator`）。Nino v4 是预发行版，所以必须加
`--prerelease`：不加时 NuGet 会安装 3.9.x，它读写不了 v4 的数据。

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

**[在官网 nino.xgamedev.net 阅读文档 →](https://nino.xgamedev.net/zh/docs/v4/start)**，包括高性能序列化、网络层、ASP.NET Core、TypeScript 客户端、消息队列、缓存、持久化和 Unity 的
[高级用法](https://nino.xgamedev.net/zh/docs/v4/advanced)。

## Nino 与 MemoryPack

除了性能，两者最明显的区别是**不可信输入**。MemoryPack 的 README 现在称自己是"仅用于可信数据的高性能序列化格式"，
并且不再建议在"输入可能来自不可信方，例如公共网络端点或用户提供的文件"的场景使用（[MemoryPack README](https://github.com/Cysharp/MemoryPack#readme)）。
Nino 正是为这类输入设计的：长度和分配以收到的字节数为上限，栈有深度上限，数据里没有类型名，
所有无效输入都只抛出一种异常（[详情](https://nino.xgamedev.net/zh/docs/v4/vs-memorypack#untrusted-input)）。

| | Nino v4 | MemoryPack 1.21.4 |
|---|---|---|
| 不可信输入 | **设计目标**：分配和栈用量有上限，只有一种异常 | 按其 README，仅用于可信数据 |
| 序列化类和多态类型 | **快 <!--bench:ser.range-->3.5–6.8<!--/bench--> 倍** | |
| 反序列化类和多态类型 | **快 <!--bench:deser.range-->1.3–1.9<!--/bench--> 倍** | |
| 原始结构体数组 | 至少持平（两者都是整块拷贝）；v4 会清零填充字节，热点结构体建议声明为无填充 | 原样复制填充字节 |
| 反序列化到已有对象 | **0 B，最多快 <!--bench:reuse.inplace-->8.7<!--/bench--> 倍**（原地复用） | 覆盖写入，有分配 |
| 多态 | 自动类型 ID，任何基类或接口，包括泛型基类 | 需要维护 `[MemoryPackUnion]` 标签，只能用于接口和抽象类，不支持泛型类型 |
| keyed 成员、引用追踪、对象池 | ✅ | 有版本兼容和循环引用，没有对象池 |
| Unity | ✅ 2022.3+，一个 OpenUPM 包，`netstandard2.0` 和 `2.1`，HybridCLR 无需初始化调用 | ✅ 2022.3.12f1+，NuGetForUnity 加一个 git 包，`netstandard2.1` |

### 基准测试中发现的 MemoryPack 问题

计时前我们先让每个值在 MemoryPack 1.21.4 中往返一次，以下几种会失败（[详情](https://nino.xgamedev.net/zh/docs/v4/vs-memorypack#memorypack-bugs-found-while-benchmarking)）：

- **`BigInteger`**：255 字节以内的值读回来是另一个值，所有运行时都如此。它的格式化器在栈缓冲区上写的是
  `temp.Slice(written)`，本意是 `temp.Slice(0, written)`。
- **.NET 10 上的 `BitArray`**：写出的字节错误，读取时破坏堆内存并导致进程崩溃。格式化器按 .NET 8 的字段布局强转，而 .NET 10 改了布局。
- **跨运行时的非托管元组**：在 .NET 上写出的 `(1, 2L)` 在 Mono 和 Unity 中读回来是 `(2, 1)`，
  因为那里 `ValueTuple` 的布局不同，而 MemoryPack 按内存原样拷贝（包括填充字节）。

Nino 按值写出这三种类型，不依赖运行时的私有布局，在所有平台上都能正确往返。

MemoryPack 还支持 `System.Type`（Nino 有意不支持：由数据指定要创建的类型是一种攻击手段）。Nino.TypeScript 在每次构建后，
依据生成格式化器所用的布局生成 TypeScript，支持结构体和版本容错类型（[与 MemoryPack 的对比](https://nino.xgamedev.net/zh/docs/v4/advanced/typescript#compared-with-memorypack)，按其 README，MemoryPack 的 TypeScript 生成将在下个版本移除），
Nino.AspNetCore 提供 MVC 格式化器和 Minimal API 请求体；浏览器客户端也可以在 [.NET WebAssembly](https://nino.xgamedev.net/zh/docs/v4/advanced/browser) 上运行同一套 C# 类型。流、异步读写、多段 `ReadOnlySequence<byte>` 输入、回调和压缩，只需在 Nino 的 `IBufferWriter<byte>`
和 `ReadOnlySpan<byte>` API 外包几行代码，[完整对比](https://nino.xgamedev.net/zh/docs/v4/vs-memorypack)里有示例。
MemoryPack 的 Union 标签是 1 字节，Nino 的类型 ID 是 4 字节，这是有意的取舍：类型 ID 由类型本身算出，子类不用打标签，
可以定义在任何程序集里（插件、HybridCLR 热更程序集），也不会因为增删子类而重新编号；MemoryPack 的每个 Union 都要手动列出或注册子类。

## v4

v4 重写了运行时和生成器。**二进制格式与 v3 不兼容。**

| v4 对比 v3（3.3.13） | |
|---|---|
| 序列化 | <!--bench:v3.ser-->类**快 1.79 倍**，类数组**快 2.29 倍**，结构体**更快**（快到测不出，不到 1 ns），结构体数组**快 1.13 倍**<!--/bench--> |
| 反序列化 | <!--bench:v3.deser-->类**快 1.21 倍**，类数组**快 1.21 倍**，结构体持平，结构体数组**快 1.07 倍**<!--/bench-->，同时多了引用追踪、复用反序列化和数据校验 |
| 编译生成代码 | 330 个类型**快 51 倍**（42.9 秒 → 0.84 秒），1,100 个类型**快 221 倍**（462 秒 → 2.1 秒） |
| 运行生成器 | **快 3.1 倍**；改动后只重新生成该类型（**快 8.3 倍**） |
| 生成代码 | 源码**小 14 倍**，IL **小 5 倍** |

v4 新增：

- **不可信数据**：分配（在读到元素之前最多为剩余字节的 16 倍）和栈深度都有上限，所有无效输入只抛出一种
  `NinoDataException`，拒绝 Nino 从不写出的字节，限制哈希冲突，提供 `NinoSettings.MaxDepth`、`MaxCollectionLength`
  和 `Untrusted`，并用 fuzz 和手工构造的数据检验过。
- **格式**：版本容错类型和 keyed 类型、引用追踪，以及每个类型的线上布局以 `[assembly: NinoTypeLayout]` 元数据发布，
  构建之间字节一有变化就能发现。
- **分配**：复用反序列化到已有对象、`[NinoPooled]` 对象池、`[NinoFactory]`，以及可以从你自己的 `ArrayPool<byte>`
  租用数组的 `NinoArrayBufferWriter`。
- **类型**：可插拔格式化器（`[assembly: NinoFormatter(...)]`）、自定义集合类型、`BigInteger`、`Uri`、`Version`、
  `StringBuilder`、`BitArray`、`CultureInfo`、`TimeZoneInfo`、`Lazy<T>`、`Memory<T>`、`ReadOnlySequence<T>`、只读和并发集合、
  `ILookup`、`BlockingCollection<T>`、`PriorityQueue`（.NET 8+）、所有不可变集合（`Nino.Immutable`），以及 Unity 的
  `AnimationCurve`、`Gradient` 和 `RectOffset`（`Nino.UnityTypes`，包含在 Unity 包中）。
- **速度**：拦截器、内联的结构体格式化器、原始结构体的数组和集合、元组和 `KeyValuePair` 整块拷贝，以及 AVX-512、AVX2、
  Arm64 和 WebAssembly 上的 SIMD；每个版本都在 x64 和 Arm64 上跑基准测试。
- **平台**：所有目标框架、运行时和 CPU 字节一致，裁剪与 Native AOT 标注，程序集可随可回收的 `AssemblyLoadContext`
  卸载（.NET 8 之前调用 `NinoRegistry.Remove`），HybridCLR 无需初始化调用。
- **字符串**：所有字符都不超过 U+00FF（ASCII、Latin-1）的文本每个字符写一个字节，其他文本按 UTF-16 写出，
  `[NinoUtf8]` 可按成员写成 UTF-8。
- **集成**：Nino.AspNetCore（MVC 格式化器，Minimal API 的 `NinoBody<T>` 和 `NinoResults`）、Nino.TypeScript
  （每次构建后生成供浏览器和 Node 使用的 TypeScript 类和编解码器）和 `nino-gen`（Nino.Codegen：从构建好的程序集生成
  同样的类型和编解码器，语言可选 TypeScript、Go 或 Rust）。
- **工具**：增量生成器，以及 27 条带代码修复的分析器（NINO001–NINO027），Unity 中同样可用。

详见 [v4 新特性](https://nino.xgamedev.net/zh/docs/v4/whats-new)、
[更新日志](https://nino.xgamedev.net/zh/docs/v4/changelog)和
[迁移指南](https://nino.xgamedev.net/zh/docs/v4/migration)；内部设计见 [docs/v4-design.md](https://github.com/JasonXuDeveloper/Nino/blob/main/docs/v4-design.md)。

## 参与贡献

欢迎提交 Issue 和 Pull Request。构建、测试矩阵和发布流程见 [CONTRIBUTING.md](https://github.com/JasonXuDeveloper/Nino/blob/main/CONTRIBUTING.md)。

<div align="center">

[MIT License](https://github.com/JasonXuDeveloper/Nino/blob/main/LICENSE) · 作者 [JasonXuDeveloper](https://github.com/JasonXuDeveloper)

</div>
