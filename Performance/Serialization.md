# Serilization 性能报告

#### [**测试数据**](/Nino_Unity/Assets/Nino/Test/Data.cs)

*第一次序列化的时候，Nino会对类型进行缓存，达到预热效果，使得同一类型的第二次开始的序列化速度大幅度提升，其他库亦是如此*

## Unity平台性能测试

### 结论

> 数据越大，Nino的性能对比其他库就越强大，如果测试的数据很小，则与其他库差距不大，甚至会略微差于其他库

体积方面，Nino最小，MsgPack其次，其他库不尽人意

序列化速度方面，Nino Code Gen最快，MsgPack略慢一筹，Nino Reflection基本与Protobuf-net一致，其他库不尽人意（序列化小体积的数据，可能会比MsgPack略慢）

**反序列化速度方面，Nino Code Gen最快**，MsgPack略慢一筹，Nino Reflection略快于Protobuf-net，略微逊色于MongoDB.Bson，BinaryFormatter最糟糕

**GC方面，Nino Code Gen碾压全部其他库**，一览众山小，序列化的GC比其他库低几百到上千倍，反序列化的GC与MsgPack持平，是其他库的几十到几百分之一！

> 序列化下，Nino Code Gen的GC基本只有扩容和转二进制返回值时的GC，其他GC全无
>
> 反序列化下，Nino Code Gen和MsgPack基本持平，GC基本只有new对象的GC，转字符串的GC，以及解压的GC，无任何额外GC

### 易用性

Nino、BinaryFormatter、可以轻松用于Unity或其他C#平台（Mono以及IL2CPP平台），无需针对不同平台进行任何额外操作

MsgPack需要在IL2CPP平台（Unity和Xamarin）进行额外处理（防止AOT问题，需要预生成代码，不然会导致无法使用），该操作十分繁琐

Protobuf-net以及MongoDB.Bson在IL2CPP平台下，字典无法使用，这个是AOT问题，暂时没找到解决方案

### 备注

- 测试的时候[MsgPack有生成代码](/Nino_Unity/Assets/Nino/Test/MessagePackGenerated.cs)，所以不要说Nino生成代码后和其他库对比不公平
- 这里测试用的是MsgPack LZ4压缩，如果不开压缩的话，MsgPack的速度会快10%，但是体积则会变大很多（大概是Protobuf-net的体积的60%，即Nino的数倍）
- MsgPack之所以比较快是因为它用到了Emit以及生成了动态类型进行序列化（高效且低GC），但是在IL2CPP平台下，会遇到限制，所以上面才会提到MsgPack在IL2CPP平台使用起来很繁琐，Nino Code Gen这边是静态生成进行序列化（高效且低GC），即便不生成代码也不影响IL2CPP下使用，并且Nino生成的代码可以搭配ILRuntime或Huatuo技术实时热更
- Odin序列化性能不如MsgPack，故而Odin序列化性能不如Nino Code Gen

### 为什么Nino又小又快、还能易用且低GC

- GC优化
  - Nino实现了高性能动态扩容数组，通过这个功能可以**极大幅度**降低GC（可以从20MB降到100KB）
  - Nino底层用了对象池，实现了包括但不限于数据流、缓冲区等内容的复用，杜绝重复创建造成的开销
  - Nino在生成代码后，通过生成的代码，避免了装箱拆箱、反射字段造成的大额GC
  - Nino在序列化和反序列化的时候，会调用不造成高额GC的方法写入数据（例如不用Decimal.GetBits去取Decimall的二进制，这个每次请求会产生一个int数组的GC）
  - Nino改造了很多原生代码，实现了低GC（如Unity平台下DeflateStream的底层被彻底改造了，只剩下调用C++方法/Write时扩容/ToArray所造成的GC了，读和写的时候不需要申请io_buffer去读写导致gc，还可以直接无gc转Nino的动态扩容Buffer）
- 速度优化
  - Nino缓存了类型模型
  - Nino生成代码后直接调用了底层API，**大幅度**优化性能（速度快一倍以上）
  - Nino底层写入数据的方法经过测试，不比用指针这些不安全的写法写入的速度慢
- 体积优化
  - Nino写Int64和Int32的时候会考虑压缩，最高将8字节压缩到2字节
  - Nino采用了C#自带的DeflateStream去压缩数据，该库是目前C#众多压缩库里最高性能，较高压缩率的压缩方式，但是只能用DeflateStream去解压，所以在其他领域用处不大，在Nino这里起到了巨大的作用



### 体积（bytes）

![i1](https://s1.ax1x.com/2022/06/15/XowpM4.png)

> Nino < MsgPack (LZ4 Compress) < Protobuf-net < BinaryFormatter < MongoDB.Bson
>
> 体积方面可以忽略是否预热

### 序列化速度（ms）

![i2](https://s1.ax1x.com/2022/06/15/XodP4f.png)

> Nino Code Gen < MsgPack (LZ4 Compress) < MongoDB.Bson < Nino Reflection < Protobuf-net < BinaryFormatter

### 反序列化速度（ms）

![i3](https://s1.ax1x.com/2022/06/29/jnsrWt.png)

> Nino Code Gen < MsgPack (LZ4 Compress) < MongoDB.Bson < Nino Reflection < Protobuf-net  < BinaryFormatter
>





## 非Unity平台性能测试（数据待更新）

``` ini
BenchmarkDotNet=v0.12.1, OS=macOS 12.0.1 (21A559) [Darwin 21.1.0]
Intel Core i9-8950HK CPU 2.90GHz (Coffee Lake), 1 CPU, 12 logical and 6 physical cores
.NET Core SDK=6.0.301
  [Host]   : .NET Core 5.0.11 (CoreCLR 5.0.1121.47308, CoreFX 5.0.1121.47308), X64 RyuJIT  [AttachedDebugger]
  ShortRun : .NET Core 5.0.11 (CoreCLR 5.0.1121.47308, CoreFX 5.0.1121.47308), X64 RyuJIT

Job=ShortRun  Platform=AnyCpu  Runtime=.NET Core 5.0  
IterationCount=1  LaunchCount=1  WarmupCount=1  

```

| Method                  |         Mean |  Error |      DataSize |        Gen 0 |        Gen 1 |        Gen 2 |      Allocated |
| ----------------------- | -----------: | -----: | ------------: | -----------: | -----------: | -----------: | -------------: |
| **MsgPackDeserialize**  | **3.575 ms** | **NA** |         **-** | **226.5625** | **113.2813** |        **-** | **1406.39 KB** |
| **MsgPackSerialize**    | **1.996 ms** | **NA** |   **3.36 KB** |        **-** |        **-** |        **-** |    **3.45 KB** |
| **NinoDeserialize**     | **2.963 ms** | **NA** |         **-** | **730.4688** | **726.5625** | **500.0000** | **3977.27 KB** |
| **NinoSerialize**       | **5.478 ms** | **NA** |   **1.79 KB** |        **-** |        **-** |        **-** |    **4.08 KB** |
| **ProtobufDeserialize** | **5.449 ms** | **NA** |         **-** | **273.4375** | **109.3750** |  **39.0625** | **1663.05 KB** |
| **ProtobufSerialize**   | **2.756 ms** | **NA** | **615.24 KB** | **660.1563** | **589.8438** | **578.1250** |  **3041.1 KB** |

>Nino在非Unity平台暂时没有优化解压带来的GC，如果优化了解压造成的GC，那么Nino反序列化时的GC与MsgPack持平（Unity平台下就是如此，甚至GC比MsgPack略低）

### 说明

非Unity平台下，测试序列化**一万个数据**时，Nino序列化速度对比MsgPack和Protobuf会比较慢，但是反序列化速度会略快

### 性能

- MsgPack和Protobuf在**非Unity环境用了Emit**，实现了高性能和低GC，在**Unity下**由于Emit技术的**限制**，相同测试下MsgPack和Protobuf的**GC是MB为单位**的，耗时也是Nino的数倍
- 由于Nino还**没实现Emit**技术的原因，在**非Unity平台**下，Nino序列化的速度**对比其他库较慢**，但是差距**不算太大，一万个数据相差几毫秒**
- **Nino无论在任何平台，GC都把控的很出色，可以在任何环境实现与MsgPack相差不大的GC（相差不到10%）**（除了反序列化因原理限制在非Unity平台下GC略高）
- Nino在**Unity平台**是目前当之无愧的**性能最卓越**的二进制序列化库，真正意义上实现了**全平台都低GC**，并且在**Unity平台下**的**序列化速度远超同类**，甚至**在非Unity平台的反序列化速度都比MsgPack快**
- Nino**补上Emit后**，**非Unity平台**的序列化性能和GC可以**与MsgPack持平**
- Nino**优化Net Core平台的解压流后**，**非Unity平台**的反序列化GC可以与**MsgPack持平**

### 体积

- Nino的体积依然是最小的，**体积**是MsgPack LZ4压缩的**一半**，是Protobuf的**数百分之一**

- 在**全部C#平台**下，Nino序列化结果的**体积**是**当之无愧最小**的，最利于数据存储和网络通信的。

