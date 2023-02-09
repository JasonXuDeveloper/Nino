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
- MsgPack在某些情况下的体积会比Nino小，这是因为Nino针对Collection类型（Array、List等）做了多态支持，会占用额外的体积

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
  - Nino底层写入数据直接在操作了指针，以实现用最高的效率去序列化和反序列化
- 体积优化
  - Nino写Int64和Int32的时候会考虑压缩，最高将8字节压缩到2字节
  - Nino采用了C#自带的DeflateStream去压缩数据，该库是目前C#众多压缩库里最高性能，较高压缩率的压缩方式，但是只能用DeflateStream去解压，所以在其他领域用处不大，在Nino这里起到了巨大的作用



### 体积（bytes）

![i1](https://s1.ax1x.com/2022/06/15/XowpM4.png)

> 使用的是Zlib压缩模式
>
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





## 非Unity平台性能测试

> 注，此测试开启了原生压缩解压

``` ini
BenchmarkDotNet=v0.13.1, OS=macOS 13.0.1 (22A400) [Darwin 22.1.0]
Intel Core i9-8950HK CPU 2.90GHz (Coffee Lake), 1 CPU, 12 logical and 6 physical cores
.NET SDK=7.0.100
  [Host]   : .NET 6.0.12 (6.0.1222.56807), X64 RyuJIT
  ShortRun : .NET 6.0.12 (6.0.1222.56807), X64 RyuJIT

Job=ShortRun  Platform=AnyCpu  Runtime=.NET 6.0  
IterationCount=1  LaunchCount=1  WarmupCount=1  

```

| Method                            | Serializer          |                Mean |  Error |   DataSize |        Gen 0 |        Gen 1 |        Gen 2 |       Allocated |
| --------------------------------- | ------------------- | ------------------: | -----: | ---------: | -----------: | -----------: | -----------: | --------------: |
| **_PrimitiveBoolDeserialize**     | **MessagePack_Lz4** |       **222.29 ns** | **NA** |      **-** |   **0.0100** |        **-** |        **-** |        **64 B** |
| _PrimitiveBoolDeserialize         | MessagePack_NoComp  |            95.40 ns |     NA |          - |            - |            - |            - |               - |
| _PrimitiveBoolDeserialize         | ProtobufNet         |           440.50 ns |     NA |          - |       0.0138 |            - |            - |            88 B |
| _PrimitiveBoolDeserialize         | JsonNet             |         1,110.07 ns |     NA |          - |       0.9041 |       0.0095 |            - |         5,672 B |
| _PrimitiveBoolDeserialize         | BinaryFormatter     |         3,034.54 ns |     NA |          - |       0.6561 |       0.0038 |            - |         4,128 B |
| _PrimitiveBoolDeserialize         | DataContract        |         2,237.42 ns |     NA |          - |       0.6638 |       0.0076 |            - |         4,168 B |
| _PrimitiveBoolDeserialize         | Hyperion            |            99.29 ns |     NA |          - |       0.0305 |            - |            - |           192 B |
| _PrimitiveBoolDeserialize         | Jil                 |           106.66 ns |     NA |          - |       0.0204 |            - |            - |           128 B |
| _PrimitiveBoolDeserialize         | SpanJson            |            25.23 ns |     NA |          - |            - |            - |            - |               - |
| _PrimitiveBoolDeserialize         | UTF8Json            |            36.92 ns |     NA |          - |            - |            - |            - |               - |
| _PrimitiveBoolDeserialize         | FsPickler           |           612.75 ns |     NA |          - |       0.1631 |            - |            - |         1,024 B |
| _PrimitiveBoolDeserialize         | Ceras               |           123.39 ns |     NA |          - |            - |            - |            - |               - |
| _PrimitiveBoolDeserialize         | OdinSerializer_     |           456.07 ns |     NA |          - |            - |            - |            - |               - |
| _PrimitiveBoolDeserialize         | Nino_Zlib           |           143.87 ns |     NA |          - |            - |            - |            - |               - |
| _PrimitiveBoolDeserialize         | Nino_NoComp         |           138.92 ns |     NA |          - |            - |            - |            - |               - |
| **_PrimitiveBoolSerialize**       | **MessagePack_Lz4** |       **139.63 ns** | **NA** |    **1 B** |   **0.0153** |        **-** |        **-** |        **96 B** |
| _PrimitiveBoolSerialize           | MessagePack_NoComp  |           113.97 ns |     NA |        1 B |       0.0050 |            - |            - |            32 B |
| _PrimitiveBoolSerialize           | ProtobufNet         |           286.37 ns |     NA |        2 B |       0.0596 |            - |            - |           376 B |
| _PrimitiveBoolSerialize           | JsonNet             |           677.44 ns |     NA |        8 B |       0.4616 |       0.0029 |            - |         2,896 B |
| _PrimitiveBoolSerialize           | BinaryFormatter     |         2,117.54 ns |     NA |       53 B |       0.4883 |       0.0038 |            - |         3,072 B |
| _PrimitiveBoolSerialize           | DataContract        |           939.35 ns |     NA |       84 B |       0.2737 |            - |            - |         1,720 B |
| _PrimitiveBoolSerialize           | Hyperion            |           220.78 ns |     NA |        2 B |       0.0789 |            - |            - |           496 B |
| _PrimitiveBoolSerialize           | Jil                 |           139.97 ns |     NA |        5 B |       0.0267 |            - |            - |           168 B |
| _PrimitiveBoolSerialize           | SpanJson            |            78.98 ns |     NA |        5 B |       0.0050 |            - |            - |            32 B |
| _PrimitiveBoolSerialize           | UTF8Json            |            58.01 ns |     NA |        5 B |       0.0050 |            - |            - |            32 B |
| _PrimitiveBoolSerialize           | FsPickler           |           669.80 ns |     NA |       27 B |       0.1755 |            - |            - |         1,104 B |
| _PrimitiveBoolSerialize           | Ceras               |           502.23 ns |     NA |        1 B |       0.6609 |            - |            - |         4,152 B |
| _PrimitiveBoolSerialize           | OdinSerializer_     |           491.47 ns |     NA |        2 B |       0.0048 |            - |            - |            32 B |
| _PrimitiveBoolSerialize           | Nino_Zlib           |           174.55 ns |     NA |        1 B |       0.0050 |            - |            - |            32 B |
| _PrimitiveBoolSerialize           | Nino_NoComp         |           186.17 ns |     NA |        1 B |       0.0050 |            - |            - |            32 B |
| **_PrimitiveByteDeserialize**     | **MessagePack_Lz4** |       **213.45 ns** | **NA** |      **-** |   **0.0100** |        **-** |        **-** |        **64 B** |
| _PrimitiveByteDeserialize         | MessagePack_NoComp  |            96.05 ns |     NA |          - |            - |            - |            - |               - |
| _PrimitiveByteDeserialize         | ProtobufNet         |           423.92 ns |     NA |          - |       0.0138 |            - |            - |            88 B |
| _PrimitiveByteDeserialize         | JsonNet             |         1,132.96 ns |     NA |          - |       0.9098 |       0.0057 |            - |         5,720 B |
| _PrimitiveByteDeserialize         | BinaryFormatter     |         2,924.55 ns |     NA |          - |       0.6561 |       0.0038 |            - |         4,120 B |
| _PrimitiveByteDeserialize         | DataContract        |         2,188.99 ns |     NA |          - |       0.6561 |       0.0038 |            - |         4,136 B |
| _PrimitiveByteDeserialize         | Hyperion            |           100.53 ns |     NA |          - |       0.0305 |            - |            - |           192 B |
| _PrimitiveByteDeserialize         | Jil                 |           111.50 ns |     NA |          - |       0.0204 |            - |            - |           128 B |
| _PrimitiveByteDeserialize         | SpanJson            |            32.35 ns |     NA |          - |            - |            - |            - |               - |
| _PrimitiveByteDeserialize         | UTF8Json            |            42.23 ns |     NA |          - |            - |            - |            - |               - |
| _PrimitiveByteDeserialize         | FsPickler           |           648.26 ns |     NA |          - |       0.1612 |            - |            - |         1,016 B |
| _PrimitiveByteDeserialize         | Ceras               |           110.68 ns |     NA |          - |            - |            - |            - |               - |
| _PrimitiveByteDeserialize         | OdinSerializer_     |           473.18 ns |     NA |          - |            - |            - |            - |               - |
| _PrimitiveByteDeserialize         | Nino_Zlib           |           156.51 ns |     NA |          - |            - |            - |            - |               - |
| _PrimitiveByteDeserialize         | Nino_NoComp         |           154.53 ns |     NA |          - |            - |            - |            - |               - |
| **_PrimitiveByteSerialize**       | **MessagePack_Lz4** |        **91.96 ns** | **NA** |    **2 B** |   **0.0153** |        **-** |        **-** |        **96 B** |
| _PrimitiveByteSerialize           | MessagePack_NoComp  |            79.42 ns |     NA |        2 B |       0.0050 |            - |            - |            32 B |
| _PrimitiveByteSerialize           | ProtobufNet         |           234.74 ns |     NA |        3 B |       0.0598 |            - |            - |           376 B |
| _PrimitiveByteSerialize           | JsonNet             |           526.89 ns |     NA |        6 B |       0.4768 |       0.0010 |            - |         2,992 B |
| _PrimitiveByteSerialize           | BinaryFormatter     |         1,552.07 ns |     NA |       50 B |       0.4883 |       0.0038 |            - |         3,072 B |
| _PrimitiveByteSerialize           | DataContract        |           714.62 ns |     NA |       92 B |       0.2747 |            - |            - |         1,728 B |
| _PrimitiveByteSerialize           | Hyperion            |           166.53 ns |     NA |        2 B |       0.0789 |            - |            - |           496 B |
| _PrimitiveByteSerialize           | Jil                 |           122.58 ns |     NA |        3 B |       0.0420 |            - |            - |           264 B |
| _PrimitiveByteSerialize           | SpanJson            |            70.91 ns |     NA |        3 B |       0.0050 |            - |            - |            32 B |
| _PrimitiveByteSerialize           | UTF8Json            |            48.94 ns |     NA |        3 B |       0.0051 |            - |            - |            32 B |
| _PrimitiveByteSerialize           | FsPickler           |           527.07 ns |     NA |       24 B |       0.1745 |            - |            - |         1,096 B |
| _PrimitiveByteSerialize           | Ceras               |           383.86 ns |     NA |        1 B |       0.6614 |            - |            - |         4,152 B |
| _PrimitiveByteSerialize           | OdinSerializer_     |           336.23 ns |     NA |        2 B |       0.0048 |            - |            - |            32 B |
| _PrimitiveByteSerialize           | Nino_Zlib           |           138.62 ns |     NA |        1 B |       0.0050 |            - |            - |            32 B |
| _PrimitiveByteSerialize           | Nino_NoComp         |           134.93 ns |     NA |        1 B |       0.0050 |            - |            - |            32 B |
| **_PrimitiveCharDeserialize**     | **MessagePack_Lz4** |       **221.55 ns** | **NA** |      **-** |   **0.0100** |        **-** |        **-** |        **64 B** |
| _PrimitiveCharDeserialize         | MessagePack_NoComp  |            98.39 ns |     NA |          - |            - |            - |            - |               - |
| _PrimitiveCharDeserialize         | ProtobufNet         |           433.28 ns |     NA |          - |       0.0138 |            - |            - |            88 B |
| _PrimitiveCharDeserialize         | JsonNet             |         1,124.85 ns |     NA |          - |       0.9117 |       0.0095 |            - |         5,720 B |
| _PrimitiveCharDeserialize         | BinaryFormatter     |         3,230.59 ns |     NA |          - |       0.6561 |       0.0038 |            - |         4,120 B |
| _PrimitiveCharDeserialize         | DataContract        |         2,100.41 ns |     NA |          - |       0.6561 |       0.0038 |            - |         4,136 B |
| _PrimitiveCharDeserialize         | Hyperion            |           117.88 ns |     NA |          - |       0.0305 |            - |            - |           192 B |
| _PrimitiveCharDeserialize         | Jil                 |            84.29 ns |     NA |          - |       0.0050 |            - |            - |            32 B |
| _PrimitiveCharDeserialize         | SpanJson            |            42.41 ns |     NA |          - |            - |            - |            - |               - |
| _PrimitiveCharDeserialize         | UTF8Json            |            96.41 ns |     NA |          - |       0.0038 |            - |            - |            24 B |
| _PrimitiveCharDeserialize         | FsPickler           |           680.08 ns |     NA |          - |       0.1612 |            - |            - |         1,016 B |
| _PrimitiveCharDeserialize         | Ceras               |           137.17 ns |     NA |          - |            - |            - |            - |               - |
| _PrimitiveCharDeserialize         | OdinSerializer_     |           483.21 ns |     NA |          - |            - |            - |            - |               - |
| _PrimitiveCharDeserialize         | Nino_Zlib           |           145.60 ns |     NA |          - |            - |            - |            - |               - |
| _PrimitiveCharDeserialize         | Nino_NoComp         |           148.27 ns |     NA |          - |            - |            - |            - |               - |
| **_PrimitiveCharSerialize**       | **MessagePack_Lz4** |       **161.38 ns** | **NA** |    **1 B** |   **0.0153** |        **-** |        **-** |        **96 B** |
| _PrimitiveCharSerialize           | MessagePack_NoComp  |           132.48 ns |     NA |        1 B |       0.0050 |            - |            - |            32 B |
| _PrimitiveCharSerialize           | ProtobufNet         |           326.83 ns |     NA |        2 B |       0.0596 |            - |            - |           376 B |
| _PrimitiveCharSerialize           | JsonNet             |         1,062.09 ns |     NA |        6 B |       0.5169 |       0.0019 |            - |         3,248 B |
| _PrimitiveCharSerialize           | BinaryFormatter     |         2,388.57 ns |     NA |       50 B |       0.4883 |       0.0038 |            - |         3,072 B |
| _PrimitiveCharSerialize           | DataContract        |         1,110.20 ns |     NA |       75 B |       0.2728 |            - |            - |         1,712 B |
| _PrimitiveCharSerialize           | Hyperion            |           261.80 ns |     NA |        3 B |       0.0787 |            - |            - |           496 B |
| _PrimitiveCharSerialize           | Jil                 |           190.10 ns |     NA |        3 B |       0.0267 |            - |            - |           168 B |
| _PrimitiveCharSerialize           | SpanJson            |            99.50 ns |     NA |        3 B |       0.0050 |            - |            - |            32 B |
| _PrimitiveCharSerialize           | UTF8Json            |           124.84 ns |     NA |        3 B |       0.0088 |            - |            - |            56 B |
| _PrimitiveCharSerialize           | FsPickler           |           793.73 ns |     NA |       24 B |       0.1745 |            - |            - |         1,096 B |
| _PrimitiveCharSerialize           | Ceras               |           628.00 ns |     NA |        2 B |       0.6609 |            - |            - |         4,152 B |
| _PrimitiveCharSerialize           | OdinSerializer_     |           529.36 ns |     NA |        3 B |       0.0048 |            - |            - |            32 B |
| _PrimitiveCharSerialize           | Nino_Zlib           |           206.11 ns |     NA |        2 B |       0.0050 |            - |            - |            32 B |
| _PrimitiveCharSerialize           | Nino_NoComp         |           198.86 ns |     NA |        2 B |       0.0050 |            - |            - |            32 B |
| **_PrimitiveDateTimeDeserialize** | **MessagePack_Lz4** |       **269.62 ns** | **NA** |      **-** |   **0.0100** |        **-** |        **-** |        **64 B** |
| _PrimitiveDateTimeDeserialize     | MessagePack_NoComp  |           117.38 ns |     NA |          - |            - |            - |            - |               - |
| _PrimitiveDateTimeDeserialize     | ProtobufNet         |           490.25 ns |     NA |          - |       0.0134 |            - |            - |            88 B |
| _PrimitiveDateTimeDeserialize     | JsonNet             |         1,586.28 ns |     NA |          - |       0.9098 |       0.0057 |            - |         5,720 B |
| _PrimitiveDateTimeDeserialize     | BinaryFormatter     |         5,416.26 ns |     NA |          - |       0.9232 |       0.0076 |            - |         5,801 B |
| _PrimitiveDateTimeDeserialize     | DataContract        |         2,561.31 ns |     NA |          - |       0.6828 |       0.0076 |            - |         4,288 B |
| _PrimitiveDateTimeDeserialize     | Hyperion            |           129.34 ns |     NA |          - |       0.0305 |            - |            - |           192 B |
| _PrimitiveDateTimeDeserialize     | Jil                 |           282.59 ns |     NA |          - |       0.0267 |            - |            - |           168 B |
| _PrimitiveDateTimeDeserialize     | SpanJson            |           403.83 ns |     NA |          - |            - |            - |            - |               - |
| _PrimitiveDateTimeDeserialize     | UTF8Json            |           403.90 ns |     NA |          - |            - |            - |            - |               - |
| _PrimitiveDateTimeDeserialize     | FsPickler           |           887.31 ns |     NA |          - |       0.1631 |            - |            - |         1,024 B |
| _PrimitiveDateTimeDeserialize     | Ceras               |           318.30 ns |     NA |          - |            - |            - |            - |               - |
| _PrimitiveDateTimeDeserialize     | OdinSerializer_     |         1,145.84 ns |     NA |          - |       0.0153 |            - |            - |           104 B |
| _PrimitiveDateTimeDeserialize     | Nino_Zlib           |           145.17 ns |     NA |          - |            - |            - |            - |               - |
| _PrimitiveDateTimeDeserialize     | Nino_NoComp         |           157.65 ns |     NA |          - |            - |            - |            - |               - |
| **_PrimitiveDateTimeSerialize**   | **MessagePack_Lz4** |       **988.30 ns** | **NA** |    **6 B** |   **0.0153** |        **-** |        **-** |        **96 B** |
| _PrimitiveDateTimeSerialize       | MessagePack_NoComp  |           532.88 ns |     NA |        6 B |       0.0048 |            - |            - |            32 B |
| _PrimitiveDateTimeSerialize       | ProtobufNet         |           496.04 ns |     NA |        6 B |       0.0591 |            - |            - |           376 B |
| _PrimitiveDateTimeSerialize       | JsonNet             |         1,486.33 ns |     NA |       30 B |       0.4807 |            - |            - |         3,016 B |
| _PrimitiveDateTimeSerialize       | BinaryFormatter     |         3,234.85 ns |     NA |       78 B |       0.5798 |       0.0038 |            - |         3,656 B |
| _PrimitiveDateTimeSerialize       | DataContract        |         1,972.02 ns |     NA |      106 B |       0.3395 |            - |            - |         2,144 B |
| _PrimitiveDateTimeSerialize       | Hyperion            |           303.81 ns |     NA |       10 B |       0.0801 |            - |            - |           504 B |
| _PrimitiveDateTimeSerialize       | Jil                 |           790.26 ns |     NA |       22 B |       0.0668 |            - |            - |           424 B |
| _PrimitiveDateTimeSerialize       | SpanJson            |           557.45 ns |     NA |       27 B |       0.0086 |            - |            - |            56 B |
| _PrimitiveDateTimeSerialize       | UTF8Json            |           652.97 ns |     NA |       27 B |       0.0086 |            - |            - |            56 B |
| _PrimitiveDateTimeSerialize       | FsPickler           |         1,218.76 ns |     NA |       44 B |       0.1774 |            - |            - |         1,120 B |
| _PrimitiveDateTimeSerialize       | Ceras               |         1,003.29 ns |     NA |        8 B |       0.6599 |            - |            - |         4,152 B |
| _PrimitiveDateTimeSerialize       | OdinSerializer_     |         1,190.45 ns |     NA |       99 B |       0.0191 |            - |            - |           128 B |
| _PrimitiveDateTimeSerialize       | Nino_Zlib           |           213.88 ns |     NA |        8 B |       0.0050 |            - |            - |            32 B |
| _PrimitiveDateTimeSerialize       | Nino_NoComp         |           228.76 ns |     NA |        8 B |       0.0050 |            - |            - |            32 B |
| **_PrimitiveIntDeserialize**      | **MessagePack_Lz4** |       **235.56 ns** | **NA** |      **-** |   **0.0100** |        **-** |        **-** |        **64 B** |
| _PrimitiveIntDeserialize          | MessagePack_NoComp  |           109.81 ns |     NA |          - |            - |            - |            - |               - |
| _PrimitiveIntDeserialize          | ProtobufNet         |           447.41 ns |     NA |          - |       0.0138 |            - |            - |            88 B |
| _PrimitiveIntDeserialize          | JsonNet             |         1,261.46 ns |     NA |          - |       0.9079 |       0.0057 |            - |         5,696 B |
| _PrimitiveIntDeserialize          | BinaryFormatter     |         3,108.94 ns |     NA |          - |       0.6561 |       0.0038 |            - |         4,120 B |
| _PrimitiveIntDeserialize          | DataContract        |         2,131.66 ns |     NA |          - |       0.6561 |       0.0038 |            - |         4,136 B |
| _PrimitiveIntDeserialize          | Hyperion            |           118.17 ns |     NA |          - |       0.0305 |            - |            - |           192 B |
| _PrimitiveIntDeserialize          | Jil                 |           138.12 ns |     NA |          - |       0.0229 |            - |            - |           144 B |
| _PrimitiveIntDeserialize          | SpanJson            |            66.53 ns |     NA |          - |            - |            - |            - |               - |
| _PrimitiveIntDeserialize          | UTF8Json            |            65.58 ns |     NA |          - |            - |            - |            - |               - |
| _PrimitiveIntDeserialize          | FsPickler           |           621.94 ns |     NA |          - |       0.1612 |            - |            - |         1,016 B |
| _PrimitiveIntDeserialize          | Ceras               |           127.77 ns |     NA |          - |            - |            - |            - |               - |
| _PrimitiveIntDeserialize          | OdinSerializer_     |           476.63 ns |     NA |          - |            - |            - |            - |               - |
| _PrimitiveIntDeserialize          | Nino_Zlib           |           153.59 ns |     NA |          - |            - |            - |            - |               - |
| _PrimitiveIntDeserialize          | Nino_NoComp         |           152.27 ns |     NA |          - |            - |            - |            - |               - |
| **_PrimitiveIntSerialize**        | **MessagePack_Lz4** |       **102.46 ns** | **NA** |    **5 B** |   **0.0153** |        **-** |        **-** |        **96 B** |
| _PrimitiveIntSerialize            | MessagePack_NoComp  |            80.68 ns |     NA |        5 B |       0.0050 |            - |            - |            32 B |
| _PrimitiveIntSerialize            | ProtobufNet         |           211.90 ns |     NA |       11 B |       0.0610 |            - |            - |           384 B |
| _PrimitiveIntSerialize            | JsonNet             |           522.47 ns |     NA |       14 B |       0.4778 |       0.0019 |            - |         3,000 B |
| _PrimitiveIntSerialize            | BinaryFormatter     |         1,390.02 ns |     NA |       54 B |       0.4883 |       0.0038 |            - |         3,072 B |
| _PrimitiveIntSerialize            | DataContract        |           696.81 ns |     NA |       82 B |       0.2737 |            - |            - |         1,720 B |
| _PrimitiveIntSerialize            | Hyperion            |           161.08 ns |     NA |        5 B |       0.0789 |            - |            - |           496 B |
| _PrimitiveIntSerialize            | Jil                 |           107.79 ns |     NA |       11 B |       0.0458 |            - |            - |           288 B |
| _PrimitiveIntSerialize            | SpanJson            |            83.51 ns |     NA |       11 B |       0.0063 |            - |            - |            40 B |
| _PrimitiveIntSerialize            | UTF8Json            |            60.55 ns |     NA |       11 B |       0.0063 |            - |            - |            40 B |
| _PrimitiveIntSerialize            | FsPickler           |           453.62 ns |     NA |       28 B |       0.1760 |            - |            - |         1,104 B |
| _PrimitiveIntSerialize            | Ceras               |           323.80 ns |     NA |        5 B |       0.6614 |            - |            - |         4,152 B |
| _PrimitiveIntSerialize            | OdinSerializer_     |           295.59 ns |     NA |        5 B |       0.0048 |            - |            - |            32 B |
| _PrimitiveIntSerialize            | Nino_Zlib           |           134.57 ns |     NA |        5 B |       0.0050 |            - |            - |            32 B |
| _PrimitiveIntSerialize            | Nino_NoComp         |           119.82 ns |     NA |        5 B |       0.0050 |            - |            - |            32 B |
| **_PrimitiveLongDeserialize**     | **MessagePack_Lz4** |       **222.07 ns** | **NA** |      **-** |   **0.0100** |        **-** |        **-** |        **64 B** |
| _PrimitiveLongDeserialize         | MessagePack_NoComp  |           103.32 ns |     NA |          - |            - |            - |            - |               - |
| _PrimitiveLongDeserialize         | ProtobufNet         |           428.93 ns |     NA |          - |       0.0138 |            - |            - |            88 B |
| _PrimitiveLongDeserialize         | JsonNet             |         1,260.51 ns |     NA |          - |       0.9079 |       0.0057 |            - |         5,696 B |
| _PrimitiveLongDeserialize         | BinaryFormatter     |         2,981.59 ns |     NA |          - |       0.6561 |       0.0038 |            - |         4,120 B |
| _PrimitiveLongDeserialize         | DataContract        |         2,079.31 ns |     NA |          - |       0.6561 |       0.0038 |            - |         4,136 B |
| _PrimitiveLongDeserialize         | Hyperion            |           120.07 ns |     NA |          - |       0.0305 |            - |            - |           192 B |
| _PrimitiveLongDeserialize         | Jil                 |           172.65 ns |     NA |          - |       0.0253 |            - |            - |           160 B |
| _PrimitiveLongDeserialize         | SpanJson            |            88.73 ns |     NA |          - |            - |            - |            - |               - |
| _PrimitiveLongDeserialize         | UTF8Json            |            74.94 ns |     NA |          - |            - |            - |            - |               - |
| _PrimitiveLongDeserialize         | FsPickler           |           652.31 ns |     NA |          - |       0.1612 |            - |            - |         1,016 B |
| _PrimitiveLongDeserialize         | Ceras               |           125.82 ns |     NA |          - |            - |            - |            - |               - |
| _PrimitiveLongDeserialize         | OdinSerializer_     |           462.90 ns |     NA |          - |            - |            - |            - |               - |
| _PrimitiveLongDeserialize         | Nino_Zlib           |           156.28 ns |     NA |          - |            - |            - |            - |               - |
| _PrimitiveLongDeserialize         | Nino_NoComp         |           149.51 ns |     NA |          - |            - |            - |            - |               - |
| **_PrimitiveLongSerialize**       | **MessagePack_Lz4** |       **104.46 ns** | **NA** |    **9 B** |   **0.0166** |        **-** |        **-** |       **104 B** |
| _PrimitiveLongSerialize           | MessagePack_NoComp  |            88.07 ns |     NA |        9 B |       0.0063 |            - |            - |            40 B |
| _PrimitiveLongSerialize           | ProtobufNet         |           222.75 ns |     NA |       10 B |       0.0610 |            - |            - |           384 B |
| _PrimitiveLongSerialize           | JsonNet             |           595.36 ns |     NA |       22 B |       0.4787 |       0.0038 |            - |         3,008 B |
| _PrimitiveLongSerialize           | BinaryFormatter     |         1,561.98 ns |     NA |       58 B |       0.4902 |       0.0038 |            - |         3,080 B |
| _PrimitiveLongSerialize           | DataContract        |           763.44 ns |     NA |       92 B |       0.2747 |            - |            - |         1,728 B |
| _PrimitiveLongSerialize           | Hyperion            |           168.71 ns |     NA |        9 B |       0.0801 |            - |            - |           504 B |
| _PrimitiveLongSerialize           | Jil                 |           178.44 ns |     NA |       19 B |       0.0663 |            - |            - |           416 B |
| _PrimitiveLongSerialize           | SpanJson            |            84.74 ns |     NA |       19 B |       0.0076 |            - |            - |            48 B |
| _PrimitiveLongSerialize           | UTF8Json            |            82.10 ns |     NA |       19 B |       0.0076 |            - |            - |            48 B |
| _PrimitiveLongSerialize           | FsPickler           |           464.22 ns |     NA |       32 B |       0.1760 |            - |            - |         1,104 B |
| _PrimitiveLongSerialize           | Ceras               |           316.20 ns |     NA |        8 B |       0.6614 |            - |            - |         4,152 B |
| _PrimitiveLongSerialize           | OdinSerializer_     |           301.59 ns |     NA |        9 B |       0.0062 |            - |            - |            40 B |
| _PrimitiveLongSerialize           | Nino_Zlib           |           121.94 ns |     NA |        9 B |       0.0062 |            - |            - |            40 B |
| _PrimitiveLongSerialize           | Nino_NoComp         |           120.22 ns |     NA |        9 B |       0.0062 |            - |            - |            40 B |
| **_PrimitiveSByteDeserialize**    | **MessagePack_Lz4** |       **226.21 ns** | **NA** |      **-** |   **0.0100** |        **-** |        **-** |        **64 B** |
| _PrimitiveSByteDeserialize        | MessagePack_NoComp  |           107.57 ns |     NA |          - |            - |            - |            - |               - |
| _PrimitiveSByteDeserialize        | ProtobufNet         |           420.53 ns |     NA |          - |       0.0138 |            - |            - |            88 B |
| _PrimitiveSByteDeserialize        | JsonNet             |         1,173.78 ns |     NA |          - |       0.9098 |       0.0057 |            - |         5,720 B |
| _PrimitiveSByteDeserialize        | BinaryFormatter     |         2,973.26 ns |     NA |          - |       0.6561 |       0.0038 |            - |         4,120 B |
| _PrimitiveSByteDeserialize        | DataContract        |         2,034.18 ns |     NA |          - |       0.6561 |       0.0038 |            - |         4,136 B |
| _PrimitiveSByteDeserialize        | Hyperion            |            92.84 ns |     NA |          - |       0.0305 |            - |            - |           192 B |
| _PrimitiveSByteDeserialize        | Jil                 |           107.63 ns |     NA |          - |       0.0204 |            - |            - |           128 B |
| _PrimitiveSByteDeserialize        | SpanJson            |            39.98 ns |     NA |          - |            - |            - |            - |               - |
| _PrimitiveSByteDeserialize        | UTF8Json            |            45.22 ns |     NA |          - |            - |            - |            - |               - |
| _PrimitiveSByteDeserialize        | FsPickler           |           587.44 ns |     NA |          - |       0.1612 |            - |            - |         1,016 B |
| _PrimitiveSByteDeserialize        | Ceras               |           115.64 ns |     NA |          - |            - |            - |            - |               - |
| _PrimitiveSByteDeserialize        | OdinSerializer_     |           433.59 ns |     NA |          - |            - |            - |            - |               - |
| _PrimitiveSByteDeserialize        | Nino_Zlib           |           135.45 ns |     NA |          - |            - |            - |            - |               - |
| _PrimitiveSByteDeserialize        | Nino_NoComp         |           133.51 ns |     NA |          - |            - |            - |            - |               - |
| **_PrimitiveSByteSerialize**      | **MessagePack_Lz4** |        **84.05 ns** | **NA** |    **2 B** |   **0.0153** |        **-** |        **-** |        **96 B** |
| _PrimitiveSByteSerialize          | MessagePack_NoComp  |            72.41 ns |     NA |        2 B |       0.0050 |            - |            - |            32 B |
| _PrimitiveSByteSerialize          | ProtobufNet         |           209.75 ns |     NA |       11 B |       0.0610 |            - |            - |           384 B |
| _PrimitiveSByteSerialize          | JsonNet             |           467.49 ns |     NA |        7 B |       0.4768 |       0.0010 |            - |         2,992 B |
| _PrimitiveSByteSerialize          | BinaryFormatter     |         1,467.11 ns |     NA |       51 B |       0.4883 |       0.0038 |            - |         3,072 B |
| _PrimitiveSByteSerialize          | DataContract        |           649.26 ns |     NA |       77 B |       0.2728 |            - |            - |         1,712 B |
| _PrimitiveSByteSerialize          | Hyperion            |           153.24 ns |     NA |        2 B |       0.0789 |            - |            - |           496 B |
| _PrimitiveSByteSerialize          | Jil                 |           112.42 ns |     NA |        4 B |       0.0420 |            - |            - |           264 B |
| _PrimitiveSByteSerialize          | SpanJson            |            66.85 ns |     NA |        4 B |       0.0050 |            - |            - |            32 B |
| _PrimitiveSByteSerialize          | UTF8Json            |            47.29 ns |     NA |        4 B |       0.0051 |            - |            - |            32 B |
| _PrimitiveSByteSerialize          | FsPickler           |           469.70 ns |     NA |       25 B |       0.1760 |            - |            - |         1,104 B |
| _PrimitiveSByteSerialize          | Ceras               |           298.89 ns |     NA |        1 B |       0.6614 |            - |            - |         4,152 B |
| _PrimitiveSByteSerialize          | OdinSerializer_     |           282.31 ns |     NA |        2 B |       0.0048 |            - |            - |            32 B |
| _PrimitiveSByteSerialize          | Nino_Zlib           |           111.29 ns |     NA |        1 B |       0.0050 |            - |            - |            32 B |
| _PrimitiveSByteSerialize          | Nino_NoComp         |           119.20 ns |     NA |        1 B |       0.0050 |            - |            - |            32 B |
| **_PrimitiveShortDeserialize**    | **MessagePack_Lz4** |       **223.23 ns** | **NA** |      **-** |   **0.0100** |        **-** |        **-** |        **64 B** |
| _PrimitiveShortDeserialize        | MessagePack_NoComp  |           104.21 ns |     NA |          - |            - |            - |            - |               - |
| _PrimitiveShortDeserialize        | ProtobufNet         |           421.04 ns |     NA |          - |       0.0138 |            - |            - |            88 B |
| _PrimitiveShortDeserialize        | JsonNet             |         1,159.24 ns |     NA |          - |       0.9098 |       0.0057 |            - |         5,720 B |
| _PrimitiveShortDeserialize        | BinaryFormatter     |         2,926.90 ns |     NA |          - |       0.6561 |       0.0038 |            - |         4,120 B |
| _PrimitiveShortDeserialize        | DataContract        |         1,947.54 ns |     NA |          - |       0.6561 |       0.0038 |            - |         4,136 B |
| _PrimitiveShortDeserialize        | Hyperion            |           108.60 ns |     NA |          - |       0.0305 |            - |            - |           192 B |
| _PrimitiveShortDeserialize        | Jil                 |           105.69 ns |     NA |          - |       0.0204 |            - |            - |           128 B |
| _PrimitiveShortDeserialize        | SpanJson            |            47.09 ns |     NA |          - |            - |            - |            - |               - |
| _PrimitiveShortDeserialize        | UTF8Json            |            46.90 ns |     NA |          - |            - |            - |            - |               - |
| _PrimitiveShortDeserialize        | FsPickler           |           632.71 ns |     NA |          - |       0.1612 |            - |            - |         1,016 B |
| _PrimitiveShortDeserialize        | Ceras               |           111.61 ns |     NA |          - |            - |            - |            - |               - |
| _PrimitiveShortDeserialize        | OdinSerializer_     |           462.96 ns |     NA |          - |            - |            - |            - |               - |
| _PrimitiveShortDeserialize        | Nino_Zlib           |           138.76 ns |     NA |          - |            - |            - |            - |               - |
| _PrimitiveShortDeserialize        | Nino_NoComp         |           148.85 ns |     NA |          - |            - |            - |            - |               - |
| **_PrimitiveShortSerialize**      | **MessagePack_Lz4** |       **116.22 ns** | **NA** |    **3 B** |   **0.0153** |        **-** |        **-** |        **96 B** |
| _PrimitiveShortSerialize          | MessagePack_NoComp  |            88.65 ns |     NA |        3 B |       0.0050 |            - |            - |            32 B |
| _PrimitiveShortSerialize          | ProtobufNet         |           207.51 ns |     NA |        4 B |       0.0598 |            - |            - |           376 B |
| _PrimitiveShortSerialize          | JsonNet             |           528.23 ns |     NA |        8 B |       0.4768 |       0.0010 |            - |         2,992 B |
| _PrimitiveShortSerialize          | BinaryFormatter     |         1,404.88 ns |     NA |       52 B |       0.4883 |       0.0038 |            - |         3,072 B |
| _PrimitiveShortSerialize          | DataContract        |           722.85 ns |     NA |       80 B |       0.2728 |            - |            - |         1,712 B |
| _PrimitiveShortSerialize          | Hyperion            |           156.32 ns |     NA |        3 B |       0.0789 |            - |            - |           496 B |
| _PrimitiveShortSerialize          | Jil                 |           108.55 ns |     NA |        5 B |       0.0421 |            - |            - |           264 B |
| _PrimitiveShortSerialize          | SpanJson            |            72.73 ns |     NA |        5 B |       0.0050 |            - |            - |            32 B |
| _PrimitiveShortSerialize          | UTF8Json            |            45.00 ns |     NA |        5 B |       0.0051 |            - |            - |            32 B |
| _PrimitiveShortSerialize          | FsPickler           |           590.47 ns |     NA |       26 B |       0.1760 |            - |            - |         1,104 B |
| _PrimitiveShortSerialize          | Ceras               |           361.85 ns |     NA |        2 B |       0.6614 |            - |            - |         4,152 B |
| _PrimitiveShortSerialize          | OdinSerializer_     |           310.20 ns |     NA |        3 B |       0.0048 |            - |            - |            32 B |
| _PrimitiveShortSerialize          | Nino_Zlib           |           117.04 ns |     NA |        2 B |       0.0050 |            - |            - |            32 B |
| _PrimitiveShortSerialize          | Nino_NoComp         |           119.09 ns |     NA |        2 B |       0.0050 |            - |            - |            32 B |
| **_PrimitiveStringDeserialize**   | **MessagePack_Lz4** |       **941.14 ns** | **NA** |      **-** |   **0.0458** |        **-** |        **-** |       **288 B** |
| _PrimitiveStringDeserialize       | MessagePack_NoComp  |           219.75 ns |     NA |          - |       0.0353 |            - |            - |           224 B |
| _PrimitiveStringDeserialize       | ProtobufNet         |           553.94 ns |     NA |          - |       0.0496 |            - |            - |           312 B |
| _PrimitiveStringDeserialize       | JsonNet             |         1,501.53 ns |     NA |          - |       0.9384 |       0.0114 |            - |         5,896 B |
| _PrimitiveStringDeserialize       | BinaryFormatter     |         1,113.69 ns |     NA |          - |       0.4063 |       0.0019 |            - |         2,560 B |
| _PrimitiveStringDeserialize       | DataContract        |         2,620.38 ns |     NA |          - |       0.7401 |       0.0076 |            - |         4,664 B |
| _PrimitiveStringDeserialize       | Hyperion            |           237.31 ns |     NA |          - |       0.0825 |            - |            - |           520 B |
| _PrimitiveStringDeserialize       | Jil                 |           603.66 ns |     NA |          - |       0.1326 |            - |            - |           832 B |
| _PrimitiveStringDeserialize       | SpanJson            |           283.40 ns |     NA |          - |       0.0353 |            - |            - |           224 B |
| _PrimitiveStringDeserialize       | UTF8Json            |           475.72 ns |     NA |          - |       0.0353 |            - |            - |           224 B |
| _PrimitiveStringDeserialize       | FsPickler           |           761.37 ns |     NA |          - |       0.1974 |            - |            - |         1,240 B |
| _PrimitiveStringDeserialize       | Ceras               |           238.95 ns |     NA |          - |       0.0353 |            - |            - |           224 B |
| _PrimitiveStringDeserialize       | OdinSerializer_     |           542.05 ns |     NA |          - |       0.0353 |            - |            - |           224 B |
| _PrimitiveStringDeserialize       | Nino_Zlib           |        21,192.86 ns |     NA |          - |       0.0305 |            - |            - |           296 B |
| _PrimitiveStringDeserialize       | Nino_NoComp         |           265.80 ns |     NA |          - |       0.0353 |            - |            - |           224 B |
| **_PrimitiveStringSerialize**     | **MessagePack_Lz4** |       **855.98 ns** | **NA** |   **21 B** |   **0.0172** |        **-** |        **-** |       **112 B** |
| _PrimitiveStringSerialize         | MessagePack_NoComp  |           202.66 ns |     NA |      102 B |       0.0203 |            - |            - |           128 B |
| _PrimitiveStringSerialize         | ProtobufNet         |           476.26 ns |     NA |      102 B |       0.0744 |            - |            - |           472 B |
| _PrimitiveStringSerialize         | JsonNet             |         1,044.86 ns |     NA |      105 B |       0.4883 |       0.0019 |            - |         3,072 B |
| _PrimitiveStringSerialize         | BinaryFormatter     |         1,496.46 ns |     NA |      124 B |       0.3910 |       0.0019 |            - |         2,464 B |
| _PrimitiveStringSerialize         | DataContract        |         1,400.38 ns |     NA |      177 B |       0.2842 |            - |            - |         1,792 B |
| _PrimitiveStringSerialize         | Hyperion            |           365.16 ns |     NA |      102 B |       0.1106 |            - |            - |           696 B |
| _PrimitiveStringSerialize         | Jil                 |         1,176.22 ns |     NA |      102 B |       0.1431 |            - |            - |           904 B |
| _PrimitiveStringSerialize         | SpanJson            |           470.14 ns |     NA |      102 B |       0.0200 |            - |            - |           128 B |
| _PrimitiveStringSerialize         | UTF8Json            |           302.70 ns |     NA |      102 B |       0.0200 |            - |            - |           128 B |
| _PrimitiveStringSerialize         | FsPickler           |           921.01 ns |     NA |      127 B |       0.1907 |            - |            - |         1,200 B |
| _PrimitiveStringSerialize         | Ceras               |           674.61 ns |     NA |      101 B |       0.6762 |            - |            - |         4,248 B |
| _PrimitiveStringSerialize         | OdinSerializer_     |           602.30 ns |     NA |      206 B |       0.0362 |            - |            - |           232 B |
| _PrimitiveStringSerialize         | Nino_Zlib           |         8,281.73 ns |     NA |        9 B |            - |            - |            - |            72 B |
| _PrimitiveStringSerialize         | Nino_NoComp         |           348.39 ns |     NA |      203 B |       0.0367 |            - |            - |           232 B |
| **_PrimitiveUIntDeserialize**     | **MessagePack_Lz4** |       **222.22 ns** | **NA** |      **-** |   **0.0100** |        **-** |        **-** |        **64 B** |
| _PrimitiveUIntDeserialize         | MessagePack_NoComp  |            96.65 ns |     NA |          - |            - |            - |            - |               - |
| _PrimitiveUIntDeserialize         | ProtobufNet         |           419.57 ns |     NA |          - |       0.0138 |            - |            - |            88 B |
| _PrimitiveUIntDeserialize         | JsonNet             |         1,114.16 ns |     NA |          - |       0.9079 |       0.0057 |            - |         5,696 B |
| _PrimitiveUIntDeserialize         | BinaryFormatter     |         2,956.81 ns |     NA |          - |       0.6561 |       0.0038 |            - |         4,120 B |
| _PrimitiveUIntDeserialize         | DataContract        |         2,104.80 ns |     NA |          - |       0.6561 |       0.0038 |            - |         4,136 B |
| _PrimitiveUIntDeserialize         | Hyperion            |           112.56 ns |     NA |          - |       0.0305 |            - |            - |           192 B |
| _PrimitiveUIntDeserialize         | Jil                 |            99.79 ns |     NA |          - |       0.0191 |            - |            - |           120 B |
| _PrimitiveUIntDeserialize         | SpanJson            |            26.91 ns |     NA |          - |            - |            - |            - |               - |
| _PrimitiveUIntDeserialize         | UTF8Json            |            37.32 ns |     NA |          - |            - |            - |            - |               - |
| _PrimitiveUIntDeserialize         | FsPickler           |           669.42 ns |     NA |          - |       0.1612 |            - |            - |         1,016 B |
| _PrimitiveUIntDeserialize         | Ceras               |           118.94 ns |     NA |          - |            - |            - |            - |               - |
| _PrimitiveUIntDeserialize         | OdinSerializer_     |           474.02 ns |     NA |          - |            - |            - |            - |               - |
| _PrimitiveUIntDeserialize         | Nino_Zlib           |           149.60 ns |     NA |          - |            - |            - |            - |               - |
| _PrimitiveUIntDeserialize         | Nino_NoComp         |           146.86 ns |     NA |          - |            - |            - |            - |               - |
| **_PrimitiveUIntSerialize**       | **MessagePack_Lz4** |       **108.71 ns** | **NA** |    **1 B** |   **0.0153** |        **-** |        **-** |        **96 B** |
| _PrimitiveUIntSerialize           | MessagePack_NoComp  |           100.50 ns |     NA |        1 B |       0.0050 |            - |            - |            32 B |
| _PrimitiveUIntSerialize           | ProtobufNet         |           242.37 ns |     NA |        2 B |       0.0596 |            - |            - |           376 B |
| _PrimitiveUIntSerialize           | JsonNet             |           560.40 ns |     NA |        4 B |       0.4616 |       0.0029 |            - |         2,896 B |
| _PrimitiveUIntSerialize           | BinaryFormatter     |         1,756.38 ns |     NA |       55 B |       0.4883 |       0.0038 |            - |         3,072 B |
| _PrimitiveUIntSerialize           | DataContract        |           792.58 ns |     NA |       88 B |       0.2737 |            - |            - |         1,720 B |
| _PrimitiveUIntSerialize           | Hyperion            |           200.97 ns |     NA |        5 B |       0.0789 |            - |            - |           496 B |
| _PrimitiveUIntSerialize           | Jil                 |           134.25 ns |     NA |        1 B |       0.0408 |            - |            - |           256 B |
| _PrimitiveUIntSerialize           | SpanJson            |            69.38 ns |     NA |        1 B |       0.0050 |            - |            - |            32 B |
| _PrimitiveUIntSerialize           | UTF8Json            |            48.79 ns |     NA |        1 B |       0.0051 |            - |            - |            32 B |
| _PrimitiveUIntSerialize           | FsPickler           |           558.85 ns |     NA |       29 B |       0.1755 |            - |            - |         1,104 B |
| _PrimitiveUIntSerialize           | Ceras               |           401.06 ns |     NA |        1 B |       0.6614 |            - |            - |         4,152 B |
| _PrimitiveUIntSerialize           | OdinSerializer_     |           379.91 ns |     NA |        5 B |       0.0048 |            - |            - |            32 B |
| _PrimitiveUIntSerialize           | Nino_Zlib           |           152.39 ns |     NA |        2 B |       0.0050 |            - |            - |            32 B |
| _PrimitiveUIntSerialize           | Nino_NoComp         |           150.21 ns |     NA |        2 B |       0.0050 |            - |            - |            32 B |
| **_PrimitiveULongDeserialize**    | **MessagePack_Lz4** |       **217.94 ns** | **NA** |      **-** |   **0.0100** |        **-** |        **-** |        **64 B** |
| _PrimitiveULongDeserialize        | MessagePack_NoComp  |           104.13 ns |     NA |          - |            - |            - |            - |               - |
| _PrimitiveULongDeserialize        | ProtobufNet         |           422.62 ns |     NA |          - |       0.0138 |            - |            - |            88 B |
| _PrimitiveULongDeserialize        | JsonNet             |         1,826.04 ns |     NA |          - |       0.9613 |       0.0114 |            - |         6,032 B |
| _PrimitiveULongDeserialize        | BinaryFormatter     |         2,893.15 ns |     NA |          - |       0.6561 |       0.0038 |            - |         4,120 B |
| _PrimitiveULongDeserialize        | DataContract        |         2,594.89 ns |     NA |          - |       0.6790 |       0.0076 |            - |         4,264 B |
| _PrimitiveULongDeserialize        | Hyperion            |           126.66 ns |     NA |          - |       0.0305 |            - |            - |           192 B |
| _PrimitiveULongDeserialize        | Jil                 |           176.79 ns |     NA |          - |       0.0253 |            - |            - |           160 B |
| _PrimitiveULongDeserialize        | SpanJson            |            94.04 ns |     NA |          - |            - |            - |            - |               - |
| _PrimitiveULongDeserialize        | UTF8Json            |            95.72 ns |     NA |          - |            - |            - |            - |               - |
| _PrimitiveULongDeserialize        | FsPickler           |           633.73 ns |     NA |          - |       0.1612 |            - |            - |         1,016 B |
| _PrimitiveULongDeserialize        | Ceras               |           114.95 ns |     NA |          - |            - |            - |            - |               - |
| _PrimitiveULongDeserialize        | OdinSerializer_     |           490.62 ns |     NA |          - |            - |            - |            - |               - |
| _PrimitiveULongDeserialize        | Nino_Zlib           |           161.00 ns |     NA |          - |            - |            - |            - |               - |
| _PrimitiveULongDeserialize        | Nino_NoComp         |           148.49 ns |     NA |          - |            - |            - |            - |               - |
| **_PrimitiveULongSerialize**      | **MessagePack_Lz4** |       **129.70 ns** | **NA** |    **9 B** |   **0.0165** |        **-** |        **-** |       **104 B** |
| _PrimitiveULongSerialize          | MessagePack_NoComp  |           113.44 ns |     NA |        9 B |       0.0063 |            - |            - |            40 B |
| _PrimitiveULongSerialize          | ProtobufNet         |           284.05 ns |     NA |       11 B |       0.0610 |            - |            - |           384 B |
| _PrimitiveULongSerialize          | JsonNet             |           776.96 ns |     NA |       23 B |       0.4787 |       0.0038 |            - |         3,008 B |
| _PrimitiveULongSerialize          | BinaryFormatter     |         2,086.13 ns |     NA |       59 B |       0.4883 |       0.0038 |            - |         3,080 B |
| _PrimitiveULongSerialize          | DataContract        |         1,001.41 ns |     NA |      109 B |       0.2880 |            - |            - |         1,808 B |
| _PrimitiveULongSerialize          | Hyperion            |           234.26 ns |     NA |        9 B |       0.0801 |            - |            - |           504 B |
| _PrimitiveULongSerialize          | Jil                 |           249.99 ns |     NA |       20 B |       0.0663 |            - |            - |           416 B |
| _PrimitiveULongSerialize          | SpanJson            |           135.41 ns |     NA |       20 B |       0.0076 |            - |            - |            48 B |
| _PrimitiveULongSerialize          | UTF8Json            |           122.67 ns |     NA |       20 B |       0.0076 |            - |            - |            48 B |
| _PrimitiveULongSerialize          | FsPickler           |           639.48 ns |     NA |       33 B |       0.1764 |            - |            - |         1,112 B |
| _PrimitiveULongSerialize          | Ceras               |           458.00 ns |     NA |        8 B |       0.6614 |            - |            - |         4,152 B |
| _PrimitiveULongSerialize          | OdinSerializer_     |           429.83 ns |     NA |        9 B |       0.0062 |            - |            - |            40 B |
| _PrimitiveULongSerialize          | Nino_Zlib           |           175.49 ns |     NA |        9 B |       0.0062 |            - |            - |            40 B |
| _PrimitiveULongSerialize          | Nino_NoComp         |           169.52 ns |     NA |        9 B |       0.0062 |            - |            - |            40 B |
| **_PrimitiveUShortDeserialize**   | **MessagePack_Lz4** |       **242.71 ns** | **NA** |      **-** |   **0.0100** |        **-** |        **-** |        **64 B** |
| _PrimitiveUShortDeserialize       | MessagePack_NoComp  |           111.88 ns |     NA |          - |            - |            - |            - |               - |
| _PrimitiveUShortDeserialize       | ProtobufNet         |           427.94 ns |     NA |          - |       0.0138 |            - |            - |            88 B |
| _PrimitiveUShortDeserialize       | JsonNet             |         1,291.07 ns |     NA |          - |       0.9098 |       0.0057 |            - |         5,720 B |
| _PrimitiveUShortDeserialize       | BinaryFormatter     |         3,044.84 ns |     NA |          - |       0.6561 |       0.0038 |            - |         4,120 B |
| _PrimitiveUShortDeserialize       | DataContract        |         2,183.97 ns |     NA |          - |       0.6561 |       0.0038 |            - |         4,136 B |
| _PrimitiveUShortDeserialize       | Hyperion            |           119.12 ns |     NA |          - |       0.0305 |            - |            - |           192 B |
| _PrimitiveUShortDeserialize       | Jil                 |           114.56 ns |     NA |          - |       0.0204 |            - |            - |           128 B |
| _PrimitiveUShortDeserialize       | SpanJson            |            49.52 ns |     NA |          - |            - |            - |            - |               - |
| _PrimitiveUShortDeserialize       | UTF8Json            |            48.66 ns |     NA |          - |            - |            - |            - |               - |
| _PrimitiveUShortDeserialize       | FsPickler           |           639.76 ns |     NA |          - |       0.1612 |            - |            - |         1,016 B |
| _PrimitiveUShortDeserialize       | Ceras               |           109.37 ns |     NA |          - |            - |            - |            - |               - |
| _PrimitiveUShortDeserialize       | OdinSerializer_     |           484.77 ns |     NA |          - |            - |            - |            - |               - |
| _PrimitiveUShortDeserialize       | Nino_Zlib           |           150.96 ns |     NA |          - |            - |            - |            - |               - |
| _PrimitiveUShortDeserialize       | Nino_NoComp         |           155.35 ns |     NA |          - |            - |            - |            - |               - |
| **_PrimitiveUShortSerialize**     | **MessagePack_Lz4** |       **107.21 ns** | **NA** |    **3 B** |   **0.0153** |        **-** |        **-** |        **96 B** |
| _PrimitiveUShortSerialize         | MessagePack_NoComp  |            86.41 ns |     NA |        3 B |       0.0050 |            - |            - |            32 B |
| _PrimitiveUShortSerialize         | ProtobufNet         |           220.36 ns |     NA |        4 B |       0.0598 |            - |            - |           376 B |
| _PrimitiveUShortSerialize         | JsonNet             |           541.51 ns |     NA |        8 B |       0.4768 |       0.0010 |            - |         2,992 B |
| _PrimitiveUShortSerialize         | BinaryFormatter     |         1,578.58 ns |     NA |       53 B |       0.4883 |       0.0038 |            - |         3,072 B |
| _PrimitiveUShortSerialize         | DataContract        |           755.19 ns |     NA |       96 B |       0.2747 |            - |            - |         1,728 B |
| _PrimitiveUShortSerialize         | Hyperion            |           184.83 ns |     NA |        3 B |       0.0789 |            - |            - |           496 B |
| _PrimitiveUShortSerialize         | Jil                 |           124.34 ns |     NA |        5 B |       0.0420 |            - |            - |           264 B |
| _PrimitiveUShortSerialize         | SpanJson            |            75.39 ns |     NA |        5 B |       0.0050 |            - |            - |            32 B |
| _PrimitiveUShortSerialize         | UTF8Json            |            55.81 ns |     NA |        5 B |       0.0050 |            - |            - |            32 B |
| _PrimitiveUShortSerialize         | FsPickler           |           523.38 ns |     NA |       27 B |       0.1755 |            - |            - |         1,104 B |
| _PrimitiveUShortSerialize         | Ceras               |           399.69 ns |     NA |        2 B |       0.6614 |            - |            - |         4,152 B |
| _PrimitiveUShortSerialize         | OdinSerializer_     |           351.97 ns |     NA |        3 B |       0.0048 |            - |            - |            32 B |
| _PrimitiveUShortSerialize         | Nino_Zlib           |           143.31 ns |     NA |        2 B |       0.0050 |            - |            - |            32 B |
| _PrimitiveUShortSerialize         | Nino_NoComp         |           144.99 ns |     NA |        2 B |       0.0050 |            - |            - |            32 B |
| **AccessTokenDeserialize**        | **MessagePack_Lz4** |       **421.48 ns** | **NA** |      **-** |   **0.0176** |        **-** |        **-** |       **112 B** |
| AccessTokenDeserialize            | MessagePack_NoComp  |           274.66 ns |     NA |          - |       0.0076 |            - |            - |            48 B |
| AccessTokenDeserialize            | ProtobufNet         |           626.16 ns |     NA |          - |       0.0210 |            - |            - |           136 B |
| AccessTokenDeserialize            | JsonNet             |         3,042.56 ns |     NA |          - |       0.9193 |       0.0076 |            - |         5,768 B |
| AccessTokenDeserialize            | BinaryFormatter     |         4,737.86 ns |     NA |          - |       0.8316 |       0.0076 |            - |         5,240 B |
| AccessTokenDeserialize            | DataContract        |         6,715.16 ns |     NA |          - |       1.3733 |       0.0153 |            - |         8,632 B |
| AccessTokenDeserialize            | Hyperion            |           570.14 ns |     NA |          - |       0.0706 |            - |            - |           448 B |
| AccessTokenDeserialize            | Jil                 |           589.09 ns |     NA |          - |       0.0515 |            - |            - |           328 B |
| AccessTokenDeserialize            | SpanJson            |           232.51 ns |     NA |          - |       0.0076 |            - |            - |            48 B |
| AccessTokenDeserialize            | UTF8Json            |           490.31 ns |     NA |          - |       0.0076 |            - |            - |            48 B |
| AccessTokenDeserialize            | FsPickler           |           867.72 ns |     NA |          - |       0.1974 |            - |            - |         1,240 B |
| AccessTokenDeserialize            | Ceras               |           383.26 ns |     NA |          - |       0.0076 |            - |            - |            48 B |
| AccessTokenDeserialize            | OdinSerializer_     |         3,563.58 ns |     NA |          - |       0.0992 |            - |            - |           632 B |
| AccessTokenDeserialize            | Nino_Zlib           |        28,160.61 ns |     NA |          - |            - |            - |            - |           112 B |
| AccessTokenDeserialize            | Nino_NoComp         |           242.91 ns |     NA |          - |       0.0076 |            - |            - |            48 B |
| **AccessTokenSerialize**          | **MessagePack_Lz4** |       **779.79 ns** | **NA** |   **19 B** |   **0.0172** |        **-** |        **-** |       **112 B** |
| AccessTokenSerialize              | MessagePack_NoComp  |           279.68 ns |     NA |       19 B |       0.0076 |            - |            - |            48 B |
| AccessTokenSerialize              | ProtobufNet         |           713.87 ns |     NA |        6 B |       0.0591 |            - |            - |           376 B |
| AccessTokenSerialize              | JsonNet             |         2,280.40 ns |     NA |       82 B |       0.4997 |            - |            - |         3,152 B |
| AccessTokenSerialize              | BinaryFormatter     |         6,540.13 ns |     NA |      392 B |       0.7782 |       0.0076 |            - |         4,888 B |
| AccessTokenSerialize              | DataContract        |         3,534.70 ns |     NA |      333 B |       0.4272 |            - |            - |         2,680 B |
| AccessTokenSerialize              | Hyperion            |           515.55 ns |     NA |       69 B |       0.1040 |            - |            - |           656 B |
| AccessTokenSerialize              | Jil                 |         1,168.70 ns |     NA |       80 B |       0.1469 |            - |            - |           928 B |
| AccessTokenSerialize              | SpanJson            |           264.01 ns |     NA |       53 B |       0.0124 |            - |            - |            80 B |
| AccessTokenSerialize              | UTF8Json            |           433.28 ns |     NA |       79 B |       0.0162 |            - |            - |           104 B |
| AccessTokenSerialize              | FsPickler           |         1,154.64 ns |     NA |       67 B |       0.1907 |            - |            - |         1,200 B |
| AccessTokenSerialize              | Ceras               |         3,505.90 ns |     NA |       12 B |       0.6599 |            - |            - |         4,160 B |
| AccessTokenSerialize              | OdinSerializer_     |         4,889.66 ns |     NA |      440 B |       0.0763 |            - |            - |           512 B |
| AccessTokenSerialize              | Nino_Zlib           |         7,560.32 ns |     NA |        8 B |       0.0076 |            - |            - |            64 B |
| AccessTokenSerialize              | Nino_NoComp         |           318.75 ns |     NA |       13 B |       0.0062 |            - |            - |            40 B |
| **AccountMergeDeserialize**       | **MessagePack_Lz4** |       **425.76 ns** | **NA** |      **-** |   **0.0153** |        **-** |        **-** |        **96 B** |
| AccountMergeDeserialize           | MessagePack_NoComp  |           238.42 ns |     NA |          - |       0.0048 |            - |            - |            32 B |
| AccountMergeDeserialize           | ProtobufNet         |           666.40 ns |     NA |          - |       0.0191 |            - |            - |           120 B |
| AccountMergeDeserialize           | JsonNet             |         2,995.41 ns |     NA |          - |       0.9155 |       0.0076 |            - |         5,752 B |
| AccountMergeDeserialize           | BinaryFormatter     |         4,906.26 ns |     NA |          - |       0.7706 |       0.0076 |            - |         4,848 B |
| AccountMergeDeserialize           | DataContract        |         6,233.62 ns |     NA |          - |       1.9913 |       0.0534 |            - |        12,536 B |
| AccountMergeDeserialize           | Hyperion            |           620.24 ns |     NA |          - |       0.0687 |            - |            - |           432 B |
| AccountMergeDeserialize           | Jil                 |           558.99 ns |     NA |          - |       0.0467 |            - |            - |           296 B |
| AccountMergeDeserialize           | SpanJson            |           321.99 ns |     NA |          - |       0.0048 |            - |            - |            32 B |
| AccountMergeDeserialize           | UTF8Json            |           446.79 ns |     NA |          - |       0.0048 |            - |            - |            32 B |
| AccountMergeDeserialize           | FsPickler           |           847.69 ns |     NA |          - |       0.1955 |            - |            - |         1,232 B |
| AccountMergeDeserialize           | Ceras               |           349.59 ns |     NA |          - |       0.0048 |            - |            - |            32 B |
| AccountMergeDeserialize           | OdinSerializer_     |         2,996.86 ns |     NA |          - |       0.0916 |            - |            - |           576 B |
| AccountMergeDeserialize           | Nino_Zlib           |        27,947.87 ns |     NA |          - |            - |            - |            - |            96 B |
| AccountMergeDeserialize           | Nino_NoComp         |           214.83 ns |     NA |          - |       0.0050 |            - |            - |            32 B |
| **AccountMergeSerialize**         | **MessagePack_Lz4** |       **627.76 ns** | **NA** |   **18 B** |   **0.0172** |        **-** |        **-** |       **112 B** |
| AccountMergeSerialize             | MessagePack_NoComp  |           207.28 ns |     NA |       18 B |       0.0076 |            - |            - |            48 B |
| AccountMergeSerialize             | ProtobufNet         |           575.43 ns |     NA |        6 B |       0.0591 |            - |            - |           376 B |
| AccountMergeSerialize             | JsonNet             |         1,736.21 ns |     NA |       72 B |       0.5035 |       0.0019 |            - |         3,160 B |
| AccountMergeSerialize             | BinaryFormatter     |         4,198.66 ns |     NA |      250 B |       0.6104 |            - |            - |         3,872 B |
| AccountMergeSerialize             | DataContract        |         2,338.14 ns |     NA |      253 B |       0.3929 |            - |            - |         2,472 B |
| AccountMergeSerialize             | Hyperion            |           411.39 ns |     NA |       72 B |       0.0992 |            - |            - |           624 B |
| AccountMergeSerialize             | Jil                 |           886.63 ns |     NA |       70 B |       0.1144 |            - |            - |           720 B |
| AccountMergeSerialize             | SpanJson            |           228.71 ns |     NA |       69 B |       0.0153 |            - |            - |            96 B |
| AccountMergeSerialize             | UTF8Json            |           315.70 ns |     NA |       69 B |       0.0153 |            - |            - |            96 B |
| AccountMergeSerialize             | FsPickler           |         1,078.00 ns |     NA |       67 B |       0.1907 |            - |            - |         1,200 B |
| AccountMergeSerialize             | Ceras               |         3,006.87 ns |     NA |       11 B |       0.6599 |            - |            - |         4,160 B |
| AccountMergeSerialize             | OdinSerializer_     |         3,885.62 ns |     NA |      408 B |       0.0763 |            - |            - |           504 B |
| AccountMergeSerialize             | Nino_Zlib           |         7,221.10 ns |     NA |        8 B |       0.0076 |            - |            - |            64 B |
| AccountMergeSerialize             | Nino_NoComp         |           277.90 ns |     NA |       13 B |       0.0062 |            - |            - |            40 B |
| **AnswerDeserialize**             | **MessagePack_Lz4** |     **1,614.54 ns** | **NA** |      **-** |   **0.0324** |        **-** |        **-** |       **208 B** |
| AnswerDeserialize                 | MessagePack_NoComp  |           846.45 ns |     NA |          - |       0.0229 |            - |            - |           144 B |
| AnswerDeserialize                 | ProtobufNet         |         1,172.98 ns |     NA |          - |       0.0362 |            - |            - |           232 B |
| AnswerDeserialize                 | JsonNet             |        11,275.07 ns |     NA |          - |       0.9613 |            - |            - |         6,056 B |
| AnswerDeserialize                 | BinaryFormatter     |        13,850.84 ns |     NA |          - |       1.3885 |       0.0153 |            - |         8,784 B |
| AnswerDeserialize                 | DataContract        |        17,463.89 ns |     NA |          - |       2.1057 |       0.0305 |            - |        13,392 B |
| AnswerDeserialize                 | Hyperion            |           853.29 ns |     NA |          - |       0.0849 |            - |            - |           536 B |
| AnswerDeserialize                 | Jil                 |         3,426.28 ns |     NA |          - |       0.1869 |            - |            - |         1,184 B |
| AnswerDeserialize                 | SpanJson            |         1,262.68 ns |     NA |          - |       0.0229 |            - |            - |           144 B |
| AnswerDeserialize                 | UTF8Json            |         2,312.79 ns |     NA |          - |       0.0229 |            - |            - |           144 B |
| AnswerDeserialize                 | FsPickler           |         1,180.32 ns |     NA |          - |       0.2098 |            - |            - |         1,328 B |
| AnswerDeserialize                 | Ceras               |           525.16 ns |     NA |          - |       0.0229 |            - |            - |           144 B |
| AnswerDeserialize                 | OdinSerializer_     |        10,864.35 ns |     NA |          - |       0.3815 |            - |            - |         2,416 B |
| AnswerDeserialize                 | Nino_Zlib           |        25,319.28 ns |     NA |          - |       0.0305 |            - |            - |           216 B |
| AnswerDeserialize                 | Nino_NoComp         |           332.02 ns |     NA |          - |       0.0229 |            - |            - |           144 B |
| **AnswerSerialize**               | **MessagePack_Lz4** |     **1,427.47 ns** | **NA** |   **53 B** |   **0.0229** |        **-** |        **-** |       **144 B** |
| AnswerSerialize                   | MessagePack_NoComp  |           597.94 ns |     NA |       97 B |       0.0200 |            - |            - |           128 B |
| AnswerSerialize                   | ProtobufNet         |           985.32 ns |     NA |       30 B |       0.0629 |            - |            - |           400 B |
| AnswerSerialize                   | JsonNet             |         6,272.99 ns |     NA |      458 B |       1.1902 |       0.0153 |            - |         7,480 B |
| AnswerSerialize                   | BinaryFormatter     |        14,136.83 ns |     NA |     1117 B |       1.6785 |       0.0458 |            - |        10,552 B |
| AnswerSerialize                   | DataContract        |         7,149.27 ns |     NA |      883 B |       0.9155 |       0.0076 |            - |         5,768 B |
| AnswerSerialize                   | Hyperion            |           798.43 ns |     NA |      129 B |       0.1345 |            - |            - |           848 B |
| AnswerSerialize                   | Jil                 |         3,293.21 ns |     NA |      460 B |       0.4730 |            - |            - |         2,984 B |
| AnswerSerialize                   | SpanJson            |           809.78 ns |     NA |      353 B |       0.0610 |            - |            - |           384 B |
| AnswerSerialize                   | UTF8Json            |         1,446.97 ns |     NA |      455 B |       0.0763 |            - |            - |           480 B |
| AnswerSerialize                   | FsPickler           |         1,454.39 ns |     NA |      130 B |       0.2003 |            - |            - |         1,264 B |
| AnswerSerialize                   | Ceras               |         2,945.60 ns |     NA |       58 B |       0.6676 |            - |            - |         4,208 B |
| AnswerSerialize                   | OdinSerializer_     |         9,229.05 ns |     NA |     1584 B |       0.3052 |            - |            - |         1,968 B |
| AnswerSerialize                   | Nino_Zlib           |         7,715.57 ns |     NA |       14 B |            - |            - |            - |            72 B |
| AnswerSerialize                   | Nino_NoComp         |           482.84 ns |     NA |       64 B |       0.0134 |            - |            - |            88 B |
| **BadgeDeserialize**              | **MessagePack_Lz4** |       **455.06 ns** | **NA** |      **-** |   **0.0176** |        **-** |        **-** |       **112 B** |
| BadgeDeserialize                  | MessagePack_NoComp  |           312.77 ns |     NA |          - |       0.0076 |            - |            - |            48 B |
| BadgeDeserialize                  | ProtobufNet         |           462.74 ns |     NA |          - |       0.0215 |            - |            - |           136 B |
| BadgeDeserialize                  | JsonNet             |         3,398.97 ns |     NA |          - |       0.9079 |       0.0038 |            - |         5,720 B |
| BadgeDeserialize                  | BinaryFormatter     |         5,041.51 ns |     NA |          - |       0.8011 |       0.0076 |            - |         5,072 B |
| BadgeDeserialize                  | DataContract        |         6,171.20 ns |     NA |          - |       1.3351 |       0.0229 |            - |         8,400 B |
| BadgeDeserialize                  | Hyperion            |           538.43 ns |     NA |          - |       0.0696 |            - |            - |           440 B |
| BadgeDeserialize                  | Jil                 |           387.61 ns |     NA |          - |       0.0496 |            - |            - |           312 B |
| BadgeDeserialize                  | SpanJson            |           165.97 ns |     NA |          - |       0.0076 |            - |            - |            48 B |
| BadgeDeserialize                  | UTF8Json            |           384.26 ns |     NA |          - |       0.0076 |            - |            - |            48 B |
| BadgeDeserialize                  | FsPickler           |           831.36 ns |     NA |          - |       0.1955 |            - |            - |         1,232 B |
| BadgeDeserialize                  | Ceras               |           371.16 ns |     NA |          - |       0.0076 |            - |            - |            48 B |
| BadgeDeserialize                  | OdinSerializer_     |         3,030.07 ns |     NA |          - |       0.0877 |            - |            - |           568 B |
| BadgeDeserialize                  | Nino_Zlib           |        27,013.11 ns |     NA |          - |            - |            - |            - |           112 B |
| BadgeDeserialize                  | Nino_NoComp         |           227.73 ns |     NA |          - |       0.0076 |            - |            - |            48 B |
| **BadgeSerialize**                | **MessagePack_Lz4** |       **657.35 ns** | **NA** |    **9 B** |   **0.0162** |        **-** |        **-** |       **104 B** |
| BadgeSerialize                    | MessagePack_NoComp  |           248.79 ns |     NA |        9 B |       0.0062 |            - |            - |            40 B |
| BadgeSerialize                    | ProtobufNet         |           332.90 ns |     NA |        0 B |       0.0100 |            - |            - |            64 B |
| BadgeSerialize                    | JsonNet             |         1,698.29 ns |     NA |       74 B |       0.4845 |       0.0019 |            - |         3,048 B |
| BadgeSerialize                    | BinaryFormatter     |         4,346.21 ns |     NA |      278 B |       0.7172 |       0.0076 |            - |         4,504 B |
| BadgeSerialize                    | DataContract        |         2,264.94 ns |     NA |      250 B |       0.3357 |            - |            - |         2,120 B |
| BadgeSerialize                    | Hyperion            |           421.76 ns |     NA |       59 B |       0.1135 |            - |            - |           712 B |
| BadgeSerialize                    | Jil                 |           644.73 ns |     NA |       71 B |       0.1440 |            - |            - |           904 B |
| BadgeSerialize                    | SpanJson            |           142.45 ns |     NA |       28 B |       0.0088 |            - |            - |            56 B |
| BadgeSerialize                    | UTF8Json            |           176.76 ns |     NA |       71 B |       0.0153 |            - |            - |            96 B |
| BadgeSerialize                    | FsPickler           |           931.15 ns |     NA |       54 B |       0.1879 |            - |            - |         1,184 B |
| BadgeSerialize                    | Ceras               |         2,851.39 ns |     NA |        6 B |       0.6599 |            - |            - |         4,152 B |
| BadgeSerialize                    | OdinSerializer_     |         4,006.35 ns |     NA |      382 B |       0.0687 |            - |            - |           456 B |
| BadgeSerialize                    | Nino_Zlib           |         6,540.39 ns |     NA |        8 B |       0.0076 |            - |            - |            64 B |
| BadgeSerialize                    | Nino_NoComp         |           282.47 ns |     NA |        8 B |       0.0048 |            - |            - |            32 B |
| **CommentDeserialize**            | **MessagePack_Lz4** |       **581.24 ns** | **NA** |      **-** |   **0.0200** |        **-** |        **-** |       **128 B** |
| CommentDeserialize                | MessagePack_NoComp  |           431.68 ns |     NA |          - |       0.0100 |            - |            - |            64 B |
| CommentDeserialize                | ProtobufNet         |           620.94 ns |     NA |          - |       0.0238 |            - |            - |           152 B |
| CommentDeserialize                | JsonNet             |         5,062.82 ns |     NA |          - |       0.9155 |            - |            - |         5,784 B |
| CommentDeserialize                | BinaryFormatter     |         7,918.69 ns |     NA |          - |       0.9155 |            - |            - |         5,832 B |
| CommentDeserialize                | DataContract        |         9,067.15 ns |     NA |          - |       2.0142 |       0.0458 |            - |        12,728 B |
| CommentDeserialize                | Hyperion            |           600.20 ns |     NA |          - |       0.0725 |            - |            - |           456 B |
| CommentDeserialize                | Jil                 |           881.82 ns |     NA |          - |       0.0763 |            - |            - |           480 B |
| CommentDeserialize                | SpanJson            |           419.11 ns |     NA |          - |       0.0100 |            - |            - |            64 B |
| CommentDeserialize                | UTF8Json            |           775.36 ns |     NA |          - |       0.0095 |            - |            - |            64 B |
| CommentDeserialize                | FsPickler           |           899.46 ns |     NA |          - |       0.1984 |            - |            - |         1,248 B |
| CommentDeserialize                | Ceras               |           392.30 ns |     NA |          - |       0.0100 |            - |            - |            64 B |
| CommentDeserialize                | OdinSerializer_     |         4,992.43 ns |     NA |          - |       0.1678 |            - |            - |         1,080 B |
| CommentDeserialize                | Nino_Zlib           |        27,036.60 ns |     NA |          - |            - |            - |            - |           136 B |
| CommentDeserialize                | Nino_NoComp         |           245.21 ns |     NA |          - |       0.0100 |            - |            - |            64 B |
| **CommentSerialize**              | **MessagePack_Lz4** |       **738.03 ns** | **NA** |   **27 B** |   **0.0191** |        **-** |        **-** |       **120 B** |
| CommentSerialize                  | MessagePack_NoComp  |           314.42 ns |     NA |       27 B |       0.0086 |            - |            - |            56 B |
| CommentSerialize                  | ProtobufNet         |           552.27 ns |     NA |        6 B |       0.0591 |            - |            - |           376 B |
| CommentSerialize                  | JsonNet             |         2,723.65 ns |     NA |      151 B |       0.5264 |       0.0038 |            - |         3,312 B |
| CommentSerialize                  | BinaryFormatter     |         6,683.07 ns |     NA |      403 B |       0.7935 |       0.0076 |            - |         5,016 B |
| CommentSerialize                  | DataContract        |         3,265.22 ns |     NA |      361 B |       0.4272 |            - |            - |         2,696 B |
| CommentSerialize                  | Hyperion            |           495.14 ns |     NA |       76 B |       0.1154 |            - |            - |           728 B |
| CommentSerialize                  | Jil                 |         1,130.93 ns |     NA |      149 B |       0.1888 |            - |            - |         1,192 B |
| CommentSerialize                  | SpanJson            |           267.51 ns |     NA |      104 B |       0.0200 |            - |            - |           128 B |
| CommentSerialize                  | UTF8Json            |           437.83 ns |     NA |      148 B |       0.0277 |            - |            - |           176 B |
| CommentSerialize                  | FsPickler           |         1,072.74 ns |     NA |       71 B |       0.1907 |            - |            - |         1,200 B |
| CommentSerialize                  | Ceras               |         2,850.17 ns |     NA |       17 B |       0.6638 |            - |            - |         4,168 B |
| CommentSerialize                  | OdinSerializer_     |         5,334.21 ns |     NA |      708 B |       0.1373 |            - |            - |           880 B |
| CommentSerialize                  | Nino_Zlib           |         7,009.99 ns |     NA |       10 B |       0.0076 |            - |            - |            72 B |
| CommentSerialize                  | Nino_NoComp         |           316.11 ns |     NA |       20 B |       0.0076 |            - |            - |            48 B |
| **NestedDataDeserialize**         | **MessagePack_Lz4** | **5,955,406.29 ns** | **NA** |      **-** | **367.1875** | **281.2500** | **140.6250** | **2,030,432 B** |
| NestedDataDeserialize             | MessagePack_NoComp  |     5,701,349.30 ns |     NA |          - |     226.5625 |     109.3750 |            - |     1,440,094 B |
| NestedDataDeserialize             | ProtobufNet         |     5,934,018.99 ns |     NA |          - |     226.5625 |     109.3750 |            - |     1,442,234 B |
| NestedDataDeserialize             | JsonNet             |    47,828,036.45 ns |     NA |          - |     636.3636 |     181.8182 |            - |     4,668,442 B |
| NestedDataDeserialize             | BinaryFormatter     |    80,521,508.29 ns |     NA |          - |    2428.5714 |    1142.8571 |     428.5714 |    13,916,781 B |
| NestedDataDeserialize             | DataContract        |    49,279,548.91 ns |     NA |          - |     454.5455 |     181.8182 |            - |     3,075,429 B |
| NestedDataDeserialize             | Hyperion            |     7,032,226.12 ns |     NA |          - |     375.0000 |     187.5000 |            - |     2,401,190 B |
| NestedDataDeserialize             | Jil                 |    18,638,147.09 ns |     NA |          - |     593.7500 |     468.7500 |     250.0000 |     5,283,266 B |
| NestedDataDeserialize             | SpanJson            |     9,692,929.92 ns |     NA |          - |     218.7500 |     109.3750 |            - |     1,444,204 B |
| NestedDataDeserialize             | UTF8Json            |    18,934,927.81 ns |     NA |          - |     343.7500 |     125.0000 |      31.2500 |     2,121,539 B |
| NestedDataDeserialize             | FsPickler           |     5,792,169.12 ns |     NA |          - |     437.5000 |     351.5625 |     179.6875 |     2,383,467 B |
| NestedDataDeserialize             | Ceras               |     3,525,483.38 ns |     NA |          - |     226.5625 |     113.2813 |            - |     1,440,091 B |
| NestedDataDeserialize             | OdinSerializer_     |    49,474,874.45 ns |     NA |          - |    1090.9091 |     454.5455 |     181.8182 |     8,104,806 B |
| NestedDataDeserialize             | Nino_Zlib           |     2,998,791.60 ns |     NA |          - |     226.5625 |     113.2813 |            - |     1,443,155 B |
| NestedDataDeserialize             | Nino_NoComp         |     2,342,892.74 ns |     NA |          - |     226.5625 |     113.2813 |            - |     1,440,091 B |
| **NestedDataSerialize**           | **MessagePack_Lz4** | **3,026,814.81 ns** | **NA** | **2383 B** |        **-** |        **-** |        **-** |     **6,609 B** |
| NestedDataSerialize               | MessagePack_NoComp  |     3,005,729.12 ns |     NA |   590010 B |     136.7188 |     136.7188 |     136.7188 |       590,132 B |
| NestedDataSerialize               | ProtobufNet         |     5,107,883.46 ns |     NA |   630006 B |     515.6250 |     500.0000 |     500.0000 |     2,708,899 B |
| NestedDataSerialize               | JsonNet             |    31,175,129.78 ns |     NA |  1220025 B |    1281.2500 |     750.0000 |     750.0000 |     8,633,922 B |
| NestedDataSerialize               | BinaryFormatter     |    52,344,957.10 ns |     NA |   890394 B |    1800.0000 |     900.0000 |     900.0000 |    10,688,969 B |
| NestedDataSerialize               | DataContract        |    25,796,990.48 ns |     NA |  1520173 B |     843.7500 |     593.7500 |     593.7500 |     6,978,066 B |
| NestedDataSerialize               | Hyperion            |     6,917,247.12 ns |     NA |   710203 B |     742.1875 |     570.3125 |     570.3125 |     3,769,472 B |
| NestedDataSerialize               | Jil                 |    15,173,997.57 ns |     NA |  1310022 B |    1187.5000 |     921.8750 |     546.8750 |     8,007,938 B |
| NestedDataSerialize               | SpanJson            |    10,628,000.64 ns |     NA |  1310022 B |     187.5000 |     187.5000 |     187.5000 |     1,342,951 B |
| NestedDataSerialize               | UTF8Json            |    14,443,581.85 ns |     NA |  1310022 B |     843.7500 |     703.1250 |     703.1250 |     6,255,081 B |
| NestedDataSerialize               | FsPickler           |     6,138,351.23 ns |     NA |   690066 B |     742.1875 |     703.1250 |     687.5000 |     3,819,804 B |
| NestedDataSerialize               | Ceras               |     2,811,484.57 ns |     NA |   650009 B |     480.4688 |     460.9375 |     460.9375 |     2,736,927 B |
| NestedDataSerialize               | OdinSerializer_     |    30,184,617.06 ns |     NA |  1910351 B |     937.5000 |     562.5000 |     562.5000 |    10,876,392 B |
| NestedDataSerialize               | Nino_Zlib           |     5,436,515.48 ns |     NA |     3006 B |            - |            - |            - |         3,076 B |
| NestedDataSerialize               | Nino_NoComp         |     2,197,140.24 ns |     NA |   860016 B |     183.5938 |     183.5938 |     183.5938 |       860,165 B |



### 说明

非Unity平台下，ZLib压缩解压算法的耗时较大，导致Nino耗时比较高（如果不开压缩则Nino最快），但是数据大的时候序列化和反序列化Nino都会比其他方案快，于此同时Nino的GC也比其他方案小，基础类型甚至做到了无GC序列化反序列化

### 性能

- **Nino无论在任何平台，GC都把控的很出色**
- Nino在**Unity平台**是目前当之无愧的**性能最卓越**的二进制序列化库，且真正意义上实现了**全平台都低GC**，并且在**Unity平台下**的**序列化速度远超同类**，甚至**在非Unity平台的反序列化速度都比MsgPack快**
- Nino**补上Emit后**，**非Unity平台**的序列化性能和GC可以**与MsgPack持平**
- Nino提供不同的压缩方案后，能做到在Net Core下也高性能

### 体积

- Nino的体积依然是最小的（某些情况下比MsgPack LZ4大一点，因为有了多态支持），**体积**是MsgPack LZ4压缩的**一半**，是Protobuf的**数百分之一**

- 在**全部C#平台**下，Nino序列化结果的**体积**是**当之无愧最小**的，最利于数据存储和网络通信的。

