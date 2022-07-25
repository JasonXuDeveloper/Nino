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





## 非Unity平台性能测试

> 注，此测试开启了原生压缩解压

``` ini
BenchmarkDotNet=v0.13.1, OS=macOS Monterey 12.0.1 (21A559) [Darwin 21.1.0]
Intel Core i9-8950HK CPU 2.90GHz (Coffee Lake), 1 CPU, 12 logical and 6 physical cores
.NET SDK=6.0.301
  [Host]   : .NET 5.0.11 (5.0.1121.47308), X64 RyuJIT
  ShortRun : .NET 5.0.11 (5.0.1121.47308), X64 RyuJIT

Job=ShortRun  Platform=AnyCpu  Runtime=.NET 5.0  
IterationCount=1  LaunchCount=1  WarmupCount=1  

```

| Method                            | Serializer         |                Mean |  Error |   DataSize |        Gen 0 |        Gen 1 |        Gen 2 |       Allocated |
| --------------------------------- | ------------------ | ------------------: | -----: | ---------: | -----------: | -----------: | -----------: | --------------: |
| **_PrimitiveBoolDeserialize**     | **MessagePack_v2** |       **128.26 ns** | **NA** |      **-** |   **0.0100** |        **-** |        **-** |        **64 B** |
| _PrimitiveBoolDeserialize         | ProtobufNet        |           276.87 ns |     NA |          - |       0.0138 |            - |            - |            88 B |
| _PrimitiveBoolDeserialize         | JsonNet            |           583.41 ns |     NA |          - |       0.9069 |       0.0067 |            - |         5,696 B |
| _PrimitiveBoolDeserialize         | BinaryFormatter    |         5,388.11 ns |     NA |          - |       0.6561 |            - |            - |         4,128 B |
| _PrimitiveBoolDeserialize         | DataContract       |         1,239.11 ns |     NA |          - |       0.6638 |       0.0076 |            - |         4,168 B |
| _PrimitiveBoolDeserialize         | Hyperion           |            58.63 ns |     NA |          - |       0.0306 |            - |            - |           192 B |
| _PrimitiveBoolDeserialize         | Jil                |            63.47 ns |     NA |          - |       0.0204 |            - |            - |           128 B |
| _PrimitiveBoolDeserialize         | SpanJson           |            14.85 ns |     NA |          - |            - |            - |            - |               - |
| _PrimitiveBoolDeserialize         | UTF8Json           |            25.71 ns |     NA |          - |            - |            - |            - |               - |
| _PrimitiveBoolDeserialize         | FsPickler          |           375.99 ns |     NA |          - |       0.1631 |            - |            - |         1,024 B |
| _PrimitiveBoolDeserialize         | Ceras              |            64.77 ns |     NA |          - |            - |            - |            - |               - |
| _PrimitiveBoolDeserialize         | OdinSerializer_    |           292.43 ns |     NA |          - |            - |            - |            - |               - |
| _PrimitiveBoolDeserialize         | Nino               |           169.40 ns |     NA |          - |            - |            - |            - |               - |
| **_PrimitiveBoolSerialize**       | **MessagePack_v2** |        **86.29 ns** | **NA** |    **1 B** |   **0.0153** |        **-** |        **-** |        **96 B** |
| _PrimitiveBoolSerialize           | ProtobufNet        |           177.25 ns |     NA |        2 B |       0.0598 |            - |            - |           376 B |
| _PrimitiveBoolSerialize           | JsonNet            |           533.26 ns |     NA |        8 B |       0.9556 |       0.0191 |            - |         6,000 B |
| _PrimitiveBoolSerialize           | BinaryFormatter    |         1,384.57 ns |     NA |       53 B |       0.5512 |       0.0057 |            - |         3,464 B |
| _PrimitiveBoolSerialize           | DataContract       |           612.20 ns |     NA |       84 B |       0.2737 |            - |            - |         1,720 B |
| _PrimitiveBoolSerialize           | Hyperion           |           138.95 ns |     NA |        2 B |       0.0789 |            - |            - |           496 B |
| _PrimitiveBoolSerialize           | Jil                |            83.92 ns |     NA |        5 B |       0.0267 |            - |            - |           168 B |
| _PrimitiveBoolSerialize           | SpanJson           |            47.27 ns |     NA |        5 B |       0.0051 |            - |            - |            32 B |
| _PrimitiveBoolSerialize           | UTF8Json           |            34.05 ns |     NA |        5 B |       0.0051 |            - |            - |            32 B |
| _PrimitiveBoolSerialize           | FsPickler          |           464.03 ns |     NA |       27 B |       0.2394 |            - |            - |         1,504 B |
| _PrimitiveBoolSerialize           | Ceras              |           253.33 ns |     NA |        1 B |       0.6614 |            - |            - |         4,152 B |
| _PrimitiveBoolSerialize           | OdinSerializer_    |           342.56 ns |     NA |        2 B |       0.0048 |            - |            - |            32 B |
| _PrimitiveBoolSerialize           | Nino               |            95.91 ns |     NA |        1 B |       0.0050 |            - |            - |            32 B |
| **_PrimitiveByteDeserialize**     | **MessagePack_v2** |       **132.95 ns** | **NA** |      **-** |   **0.0100** |        **-** |        **-** |        **64 B** |
| _PrimitiveByteDeserialize         | ProtobufNet        |           272.56 ns |     NA |          - |       0.0138 |            - |            - |            88 B |
| _PrimitiveByteDeserialize         | JsonNet            |           656.27 ns |     NA |          - |       0.9108 |       0.0105 |            - |         5,720 B |
| _PrimitiveByteDeserialize         | BinaryFormatter    |         5,124.63 ns |     NA |          - |       0.6561 |            - |            - |         4,120 B |
| _PrimitiveByteDeserialize         | DataContract       |         1,191.06 ns |     NA |          - |       0.6580 |       0.0057 |            - |         4,136 B |
| _PrimitiveByteDeserialize         | Hyperion           |            54.67 ns |     NA |          - |       0.0306 |            - |            - |           192 B |
| _PrimitiveByteDeserialize         | Jil                |            62.40 ns |     NA |          - |       0.0204 |            - |            - |           128 B |
| _PrimitiveByteDeserialize         | SpanJson           |            17.36 ns |     NA |          - |            - |            - |            - |               - |
| _PrimitiveByteDeserialize         | UTF8Json           |            29.05 ns |     NA |          - |            - |            - |            - |               - |
| _PrimitiveByteDeserialize         | FsPickler          |           379.82 ns |     NA |          - |       0.1616 |       0.0005 |            - |         1,016 B |
| _PrimitiveByteDeserialize         | Ceras              |            64.26 ns |     NA |          - |            - |            - |            - |               - |
| _PrimitiveByteDeserialize         | OdinSerializer_    |           297.27 ns |     NA |          - |            - |            - |            - |               - |
| _PrimitiveByteDeserialize         | Nino               |           171.51 ns |     NA |          - |            - |            - |            - |               - |
| **_PrimitiveByteSerialize**       | **MessagePack_v2** |       **137.91 ns** | **NA** |    **2 B** |   **0.0153** |        **-** |        **-** |        **96 B** |
| _PrimitiveByteSerialize           | ProtobufNet        |           216.26 ns |     NA |        3 B |       0.0596 |            - |            - |           376 B |
| _PrimitiveByteSerialize           | JsonNet            |           655.60 ns |     NA |        6 B |       0.9708 |       0.0076 |            - |         6,096 B |
| _PrimitiveByteSerialize           | BinaryFormatter    |         1,336.74 ns |     NA |       50 B |       0.5512 |       0.0057 |            - |         3,464 B |
| _PrimitiveByteSerialize           | DataContract       |           628.09 ns |     NA |       92 B |       0.2747 |            - |            - |         1,728 B |
| _PrimitiveByteSerialize           | Hyperion           |           148.50 ns |     NA |        2 B |       0.0789 |            - |            - |           496 B |
| _PrimitiveByteSerialize           | Jil                |            99.59 ns |     NA |        3 B |       0.0421 |            - |            - |           264 B |
| _PrimitiveByteSerialize           | SpanJson           |            58.51 ns |     NA |        3 B |       0.0050 |            - |            - |            32 B |
| _PrimitiveByteSerialize           | UTF8Json           |            38.59 ns |     NA |        3 B |       0.0051 |            - |            - |            32 B |
| _PrimitiveByteSerialize           | FsPickler          |           493.15 ns |     NA |       24 B |       0.2384 |       0.0010 |            - |         1,496 B |
| _PrimitiveByteSerialize           | Ceras              |           259.61 ns |     NA |        1 B |       0.6614 |            - |            - |         4,152 B |
| _PrimitiveByteSerialize           | OdinSerializer_    |           291.82 ns |     NA |        2 B |       0.0048 |            - |            - |            32 B |
| _PrimitiveByteSerialize           | Nino               |           111.18 ns |     NA |        1 B |       0.0048 |            - |            - |            32 B |
| **_PrimitiveCharDeserialize**     | **MessagePack_v2** |       **126.02 ns** | **NA** |      **-** |   **0.0100** |        **-** |        **-** |        **64 B** |
| _PrimitiveCharDeserialize         | ProtobufNet        |           274.45 ns |     NA |          - |       0.0138 |            - |            - |            88 B |
| _PrimitiveCharDeserialize         | JsonNet            |           562.59 ns |     NA |          - |       0.9108 |       0.0105 |            - |         5,720 B |
| _PrimitiveCharDeserialize         | BinaryFormatter    |         5,345.82 ns |     NA |          - |       0.6561 |            - |            - |         4,120 B |
| _PrimitiveCharDeserialize         | DataContract       |         1,148.68 ns |     NA |          - |       0.6580 |       0.0057 |            - |         4,136 B |
| _PrimitiveCharDeserialize         | Hyperion           |            68.62 ns |     NA |          - |       0.0305 |            - |            - |           192 B |
| _PrimitiveCharDeserialize         | Jil                |            50.88 ns |     NA |          - |       0.0051 |            - |            - |            32 B |
| _PrimitiveCharDeserialize         | SpanJson           |            20.66 ns |     NA |          - |            - |            - |            - |               - |
| _PrimitiveCharDeserialize         | UTF8Json           |            67.33 ns |     NA |          - |       0.0038 |            - |            - |            24 B |
| _PrimitiveCharDeserialize         | FsPickler          |           418.11 ns |     NA |          - |       0.1616 |       0.0005 |            - |         1,016 B |
| _PrimitiveCharDeserialize         | Ceras              |            64.32 ns |     NA |          - |            - |            - |            - |               - |
| _PrimitiveCharDeserialize         | OdinSerializer_    |           284.42 ns |     NA |          - |            - |            - |            - |               - |
| _PrimitiveCharDeserialize         | Nino               |           178.88 ns |     NA |          - |            - |            - |            - |               - |
| **_PrimitiveCharSerialize**       | **MessagePack_v2** |        **91.77 ns** | **NA** |    **1 B** |   **0.0153** |        **-** |        **-** |        **96 B** |
| _PrimitiveCharSerialize           | ProtobufNet        |           177.40 ns |     NA |        2 B |       0.0598 |            - |            - |           376 B |
| _PrimitiveCharSerialize           | JsonNet            |           686.35 ns |     NA |        6 B |       1.0118 |       0.0153 |            - |         6,352 B |
| _PrimitiveCharSerialize           | BinaryFormatter    |         1,585.65 ns |     NA |       50 B |       0.5512 |       0.0057 |            - |         3,464 B |
| _PrimitiveCharSerialize           | DataContract       |           652.48 ns |     NA |       75 B |       0.2728 |            - |            - |         1,712 B |
| _PrimitiveCharSerialize           | Hyperion           |           146.15 ns |     NA |        3 B |       0.0789 |            - |            - |           496 B |
| _PrimitiveCharSerialize           | Jil                |           103.69 ns |     NA |        3 B |       0.0267 |            - |            - |           168 B |
| _PrimitiveCharSerialize           | SpanJson           |            50.63 ns |     NA |        3 B |       0.0051 |            - |            - |            32 B |
| _PrimitiveCharSerialize           | UTF8Json           |            64.75 ns |     NA |        3 B |       0.0088 |            - |            - |            56 B |
| _PrimitiveCharSerialize           | FsPickler          |           502.91 ns |     NA |       24 B |       0.2384 |       0.0010 |            - |         1,496 B |
| _PrimitiveCharSerialize           | Ceras              |           252.64 ns |     NA |        2 B |       0.6614 |            - |            - |         4,152 B |
| _PrimitiveCharSerialize           | OdinSerializer_    |           286.21 ns |     NA |        3 B |       0.0048 |            - |            - |            32 B |
| _PrimitiveCharSerialize           | Nino               |           104.00 ns |     NA |        2 B |       0.0050 |            - |            - |            32 B |
| **_PrimitiveDateTimeDeserialize** | **MessagePack_v2** |       **172.72 ns** | **NA** |      **-** |   **0.0100** |        **-** |        **-** |        **64 B** |
| _PrimitiveDateTimeDeserialize     | ProtobufNet        |           332.77 ns |     NA |          - |       0.0138 |            - |            - |            88 B |
| _PrimitiveDateTimeDeserialize     | JsonNet            |           935.11 ns |     NA |          - |       0.9108 |       0.0105 |            - |         5,720 B |
| _PrimitiveDateTimeDeserialize     | BinaryFormatter    |         7,670.28 ns |     NA |          - |       0.9308 |            - |            - |         5,840 B |
| _PrimitiveDateTimeDeserialize     | DataContract       |         1,501.37 ns |     NA |          - |       0.6828 |       0.0076 |            - |         4,288 B |
| _PrimitiveDateTimeDeserialize     | Hyperion           |            80.02 ns |     NA |          - |       0.0305 |            - |            - |           192 B |
| _PrimitiveDateTimeDeserialize     | Jil                |           181.72 ns |     NA |          - |       0.0267 |            - |            - |           168 B |
| _PrimitiveDateTimeDeserialize     | SpanJson           |           249.72 ns |     NA |          - |            - |            - |            - |               - |
| _PrimitiveDateTimeDeserialize     | UTF8Json           |           215.25 ns |     NA |          - |            - |            - |            - |               - |
| _PrimitiveDateTimeDeserialize     | FsPickler          |           492.05 ns |     NA |          - |       0.1631 |            - |            - |         1,024 B |
| _PrimitiveDateTimeDeserialize     | Ceras              |           158.47 ns |     NA |          - |            - |            - |            - |               - |
| _PrimitiveDateTimeDeserialize     | OdinSerializer_    |           649.56 ns |     NA |          - |       0.0162 |            - |            - |           104 B |
| _PrimitiveDateTimeDeserialize     | Nino               |           175.16 ns |     NA |          - |            - |            - |            - |               - |
| **_PrimitiveDateTimeSerialize**   | **MessagePack_v2** |       **550.87 ns** | **NA** |    **6 B** |   **0.0153** |        **-** |        **-** |        **96 B** |
| _PrimitiveDateTimeSerialize       | ProtobufNet        |           240.02 ns |     NA |        6 B |       0.0596 |            - |            - |           376 B |
| _PrimitiveDateTimeSerialize       | JsonNet            |           878.39 ns |     NA |       30 B |       0.9747 |       0.0086 |            - |         6,120 B |
| _PrimitiveDateTimeSerialize       | BinaryFormatter    |         1,866.35 ns |     NA |       78 B |       0.6447 |       0.0057 |            - |         4,048 B |
| _PrimitiveDateTimeSerialize       | DataContract       |         1,076.96 ns |     NA |      106 B |       0.3414 |            - |            - |         2,144 B |
| _PrimitiveDateTimeSerialize       | Hyperion           |           149.72 ns |     NA |       10 B |       0.0801 |            - |            - |           504 B |
| _PrimitiveDateTimeSerialize       | Jil                |           443.71 ns |     NA |       22 B |       0.0672 |            - |            - |           424 B |
| _PrimitiveDateTimeSerialize       | SpanJson           |           296.12 ns |     NA |       27 B |       0.0086 |            - |            - |            56 B |
| _PrimitiveDateTimeSerialize       | UTF8Json           |           363.82 ns |     NA |       27 B |       0.0086 |            - |            - |            56 B |
| _PrimitiveDateTimeSerialize       | FsPickler          |           687.94 ns |     NA |       44 B |       0.2422 |       0.0010 |            - |         1,520 B |
| _PrimitiveDateTimeSerialize       | Ceras              |           448.63 ns |     NA |        8 B |       0.6614 |            - |            - |         4,152 B |
| _PrimitiveDateTimeSerialize       | OdinSerializer_    |           613.01 ns |     NA |       99 B |       0.0200 |            - |            - |           128 B |
| _PrimitiveDateTimeSerialize       | Nino               |           107.33 ns |     NA |        8 B |       0.0050 |            - |            - |            32 B |
| **_PrimitiveIntDeserialize**      | **MessagePack_v2** |       **139.24 ns** | **NA** |      **-** |   **0.0100** |        **-** |        **-** |        **64 B** |
| _PrimitiveIntDeserialize          | ProtobufNet        |           288.16 ns |     NA |          - |       0.0138 |            - |            - |            88 B |
| _PrimitiveIntDeserialize          | JsonNet            |           735.15 ns |     NA |          - |       0.9069 |       0.0067 |            - |         5,696 B |
| _PrimitiveIntDeserialize          | BinaryFormatter    |         5,419.00 ns |     NA |          - |       0.6561 |            - |            - |         4,120 B |
| _PrimitiveIntDeserialize          | DataContract       |         1,139.15 ns |     NA |          - |       0.6580 |       0.0057 |            - |         4,136 B |
| _PrimitiveIntDeserialize          | Hyperion           |            76.60 ns |     NA |          - |       0.0305 |            - |            - |           192 B |
| _PrimitiveIntDeserialize          | Jil                |            89.37 ns |     NA |          - |       0.0229 |            - |            - |           144 B |
| _PrimitiveIntDeserialize          | SpanJson           |            37.06 ns |     NA |          - |            - |            - |            - |               - |
| _PrimitiveIntDeserialize          | UTF8Json           |            39.58 ns |     NA |          - |            - |            - |            - |               - |
| _PrimitiveIntDeserialize          | FsPickler          |           384.40 ns |     NA |          - |       0.1616 |       0.0005 |            - |         1,016 B |
| _PrimitiveIntDeserialize          | Ceras              |            71.57 ns |     NA |          - |            - |            - |            - |               - |
| _PrimitiveIntDeserialize          | OdinSerializer_    |           287.65 ns |     NA |          - |            - |            - |            - |               - |
| _PrimitiveIntDeserialize          | Nino               |           184.95 ns |     NA |          - |            - |            - |            - |               - |
| **_PrimitiveIntSerialize**        | **MessagePack_v2** |       **102.49 ns** | **NA** |    **5 B** |   **0.0153** |        **-** |        **-** |        **96 B** |
| _PrimitiveIntSerialize            | ProtobufNet        |           211.47 ns |     NA |       11 B |       0.0610 |            - |            - |           384 B |
| _PrimitiveIntSerialize            | JsonNet            |           616.99 ns |     NA |       14 B |       0.9727 |       0.0105 |            - |         6,104 B |
| _PrimitiveIntSerialize            | BinaryFormatter    |         1,491.96 ns |     NA |       54 B |       0.5512 |       0.0057 |            - |         3,464 B |
| _PrimitiveIntSerialize            | DataContract       |         1,126.11 ns |     NA |       82 B |       0.2737 |            - |            - |         1,720 B |
| _PrimitiveIntSerialize            | Hyperion           |           173.25 ns |     NA |        5 B |       0.0789 |            - |            - |           496 B |
| _PrimitiveIntSerialize            | Jil                |           110.85 ns |     NA |       11 B |       0.0459 |            - |            - |           288 B |
| _PrimitiveIntSerialize            | SpanJson           |            72.83 ns |     NA |       11 B |       0.0063 |            - |            - |            40 B |
| _PrimitiveIntSerialize            | UTF8Json           |            56.03 ns |     NA |       11 B |       0.0063 |            - |            - |            40 B |
| _PrimitiveIntSerialize            | FsPickler          |           509.36 ns |     NA |       28 B |       0.2394 |       0.0010 |            - |         1,504 B |
| _PrimitiveIntSerialize            | Ceras              |           616.50 ns |     NA |        5 B |       0.6609 |            - |            - |         4,152 B |
| _PrimitiveIntSerialize            | OdinSerializer_    |           375.11 ns |     NA |        5 B |       0.0048 |            - |            - |            32 B |
| _PrimitiveIntSerialize            | Nino               |           147.45 ns |     NA |        5 B |       0.0050 |            - |            - |            32 B |
| **_PrimitiveLongDeserialize**     | **MessagePack_v2** |       **138.26 ns** | **NA** |      **-** |   **0.0100** |        **-** |        **-** |        **64 B** |
| _PrimitiveLongDeserialize         | ProtobufNet        |           287.92 ns |     NA |          - |       0.0138 |            - |            - |            88 B |
| _PrimitiveLongDeserialize         | JsonNet            |           714.84 ns |     NA |          - |       0.9069 |       0.0067 |            - |         5,696 B |
| _PrimitiveLongDeserialize         | BinaryFormatter    |         5,305.73 ns |     NA |          - |       0.6561 |            - |            - |         4,120 B |
| _PrimitiveLongDeserialize         | DataContract       |         1,244.76 ns |     NA |          - |       0.6580 |       0.0057 |            - |         4,136 B |
| _PrimitiveLongDeserialize         | Hyperion           |            86.42 ns |     NA |          - |       0.0305 |            - |            - |           192 B |
| _PrimitiveLongDeserialize         | Jil                |           128.98 ns |     NA |          - |       0.0253 |            - |            - |           160 B |
| _PrimitiveLongDeserialize         | SpanJson           |            50.62 ns |     NA |          - |            - |            - |            - |               - |
| _PrimitiveLongDeserialize         | UTF8Json           |            54.59 ns |     NA |          - |            - |            - |            - |               - |
| _PrimitiveLongDeserialize         | FsPickler          |           424.98 ns |     NA |          - |       0.1616 |       0.0005 |            - |         1,016 B |
| _PrimitiveLongDeserialize         | Ceras              |            64.59 ns |     NA |          - |            - |            - |            - |               - |
| _PrimitiveLongDeserialize         | OdinSerializer_    |           298.84 ns |     NA |          - |            - |            - |            - |               - |
| _PrimitiveLongDeserialize         | Nino               |           185.47 ns |     NA |          - |            - |            - |            - |               - |
| **_PrimitiveLongSerialize**       | **MessagePack_v2** |       **123.26 ns** | **NA** |    **9 B** |   **0.0165** |        **-** |        **-** |       **104 B** |
| _PrimitiveLongSerialize           | ProtobufNet        |           248.27 ns |     NA |       10 B |       0.0610 |            - |            - |           384 B |
| _PrimitiveLongSerialize           | JsonNet            |           774.58 ns |     NA |       22 B |       0.9737 |       0.0095 |            - |         6,112 B |
| _PrimitiveLongSerialize           | BinaryFormatter    |         1,613.17 ns |     NA |       58 B |       0.5531 |       0.0038 |            - |         3,472 B |
| _PrimitiveLongSerialize           | DataContract       |           676.24 ns |     NA |       92 B |       0.2747 |            - |            - |         1,728 B |
| _PrimitiveLongSerialize           | Hyperion           |           156.32 ns |     NA |        9 B |       0.0801 |            - |            - |           504 B |
| _PrimitiveLongSerialize           | Jil                |           172.95 ns |     NA |       19 B |       0.0663 |            - |            - |           416 B |
| _PrimitiveLongSerialize           | SpanJson           |            92.30 ns |     NA |       19 B |       0.0076 |            - |            - |            48 B |
| _PrimitiveLongSerialize           | UTF8Json           |            75.83 ns |     NA |       19 B |       0.0076 |            - |            - |            48 B |
| _PrimitiveLongSerialize           | FsPickler          |           478.51 ns |     NA |       32 B |       0.2394 |            - |            - |         1,504 B |
| _PrimitiveLongSerialize           | Ceras              |           248.51 ns |     NA |        8 B |       0.6614 |            - |            - |         4,152 B |
| _PrimitiveLongSerialize           | OdinSerializer_    |           304.86 ns |     NA |        9 B |       0.0062 |            - |            - |            40 B |
| _PrimitiveLongSerialize           | Nino               |           107.44 ns |     NA |        9 B |       0.0063 |            - |            - |            40 B |
| **_PrimitiveSByteDeserialize**    | **MessagePack_v2** |       **137.07 ns** | **NA** |      **-** |   **0.0100** |        **-** |        **-** |        **64 B** |
| _PrimitiveSByteDeserialize        | ProtobufNet        |           296.74 ns |     NA |          - |       0.0138 |            - |            - |            88 B |
| _PrimitiveSByteDeserialize        | JsonNet            |           671.79 ns |     NA |          - |       0.9108 |       0.0105 |            - |         5,720 B |
| _PrimitiveSByteDeserialize        | BinaryFormatter    |         5,675.61 ns |     NA |          - |       0.6561 |            - |            - |         4,120 B |
| _PrimitiveSByteDeserialize        | DataContract       |         1,169.85 ns |     NA |          - |       0.6580 |       0.0057 |            - |         4,136 B |
| _PrimitiveSByteDeserialize        | Hyperion           |            58.29 ns |     NA |          - |       0.0306 |            - |            - |           192 B |
| _PrimitiveSByteDeserialize        | Jil                |            62.27 ns |     NA |          - |       0.0204 |            - |            - |           128 B |
| _PrimitiveSByteDeserialize        | SpanJson           |            22.53 ns |     NA |          - |            - |            - |            - |               - |
| _PrimitiveSByteDeserialize        | UTF8Json           |            28.09 ns |     NA |          - |            - |            - |            - |               - |
| _PrimitiveSByteDeserialize        | FsPickler          |           388.32 ns |     NA |          - |       0.1616 |       0.0005 |            - |         1,016 B |
| _PrimitiveSByteDeserialize        | Ceras              |            63.29 ns |     NA |          - |            - |            - |            - |               - |
| _PrimitiveSByteDeserialize        | OdinSerializer_    |           284.40 ns |     NA |          - |            - |            - |            - |               - |
| _PrimitiveSByteDeserialize        | Nino               |           178.55 ns |     NA |          - |            - |            - |            - |               - |
| **_PrimitiveSByteSerialize**      | **MessagePack_v2** |        **98.48 ns** | **NA** |    **2 B** |   **0.0153** |        **-** |        **-** |        **96 B** |
| _PrimitiveSByteSerialize          | ProtobufNet        |           237.03 ns |     NA |       11 B |       0.0610 |            - |            - |           384 B |
| _PrimitiveSByteSerialize          | JsonNet            |           586.39 ns |     NA |        7 B |       0.9708 |       0.0076 |            - |         6,096 B |
| _PrimitiveSByteSerialize          | BinaryFormatter    |         1,522.07 ns |     NA |       51 B |       0.5512 |       0.0038 |            - |         3,464 B |
| _PrimitiveSByteSerialize          | DataContract       |           655.16 ns |     NA |       77 B |       0.2728 |            - |            - |         1,712 B |
| _PrimitiveSByteSerialize          | Hyperion           |           145.31 ns |     NA |        2 B |       0.0789 |            - |            - |           496 B |
| _PrimitiveSByteSerialize          | Jil                |           104.30 ns |     NA |        4 B |       0.0421 |            - |            - |           264 B |
| _PrimitiveSByteSerialize          | SpanJson           |            63.58 ns |     NA |        4 B |       0.0050 |            - |            - |            32 B |
| _PrimitiveSByteSerialize          | UTF8Json           |            42.38 ns |     NA |        4 B |       0.0051 |            - |            - |            32 B |
| _PrimitiveSByteSerialize          | FsPickler          |           467.40 ns |     NA |       25 B |       0.2394 |       0.0010 |            - |         1,504 B |
| _PrimitiveSByteSerialize          | Ceras              |           259.68 ns |     NA |        1 B |       0.6614 |            - |            - |         4,152 B |
| _PrimitiveSByteSerialize          | OdinSerializer_    |           279.31 ns |     NA |        2 B |       0.0048 |            - |            - |            32 B |
| _PrimitiveSByteSerialize          | Nino               |           103.17 ns |     NA |        1 B |       0.0050 |            - |            - |            32 B |
| **_PrimitiveShortDeserialize**    | **MessagePack_v2** |       **147.29 ns** | **NA** |      **-** |   **0.0100** |        **-** |        **-** |        **64 B** |
| _PrimitiveShortDeserialize        | ProtobufNet        |           276.31 ns |     NA |          - |       0.0138 |            - |            - |            88 B |
| _PrimitiveShortDeserialize        | JsonNet            |           710.34 ns |     NA |          - |       0.9108 |       0.0057 |            - |         5,720 B |
| _PrimitiveShortDeserialize        | BinaryFormatter    |         5,026.04 ns |     NA |          - |       0.6561 |            - |            - |         4,120 B |
| _PrimitiveShortDeserialize        | DataContract       |         1,285.88 ns |     NA |          - |       0.6580 |       0.0057 |            - |         4,136 B |
| _PrimitiveShortDeserialize        | Hyperion           |            74.02 ns |     NA |          - |       0.0305 |            - |            - |           192 B |
| _PrimitiveShortDeserialize        | Jil                |            71.07 ns |     NA |          - |       0.0204 |            - |            - |           128 B |
| _PrimitiveShortDeserialize        | SpanJson           |            28.17 ns |     NA |          - |            - |            - |            - |               - |
| _PrimitiveShortDeserialize        | UTF8Json           |            29.51 ns |     NA |          - |            - |            - |            - |               - |
| _PrimitiveShortDeserialize        | FsPickler          |           394.16 ns |     NA |          - |       0.1616 |       0.0005 |            - |         1,016 B |
| _PrimitiveShortDeserialize        | Ceras              |            64.10 ns |     NA |          - |            - |            - |            - |               - |
| _PrimitiveShortDeserialize        | OdinSerializer_    |           300.01 ns |     NA |          - |            - |            - |            - |               - |
| _PrimitiveShortDeserialize        | Nino               |           183.21 ns |     NA |          - |            - |            - |            - |               - |
| **_PrimitiveShortSerialize**      | **MessagePack_v2** |       **100.34 ns** | **NA** |    **3 B** |   **0.0153** |        **-** |        **-** |        **96 B** |
| _PrimitiveShortSerialize          | ProtobufNet        |           187.57 ns |     NA |        4 B |       0.0598 |            - |            - |           376 B |
| _PrimitiveShortSerialize          | JsonNet            |           545.00 ns |     NA |        8 B |       0.9708 |       0.0076 |            - |         6,096 B |
| _PrimitiveShortSerialize          | BinaryFormatter    |         1,503.13 ns |     NA |       52 B |       0.5512 |       0.0057 |            - |         3,464 B |
| _PrimitiveShortSerialize          | DataContract       |           756.69 ns |     NA |       80 B |       0.2728 |            - |            - |         1,712 B |
| _PrimitiveShortSerialize          | Hyperion           |           147.32 ns |     NA |        3 B |       0.0789 |            - |            - |           496 B |
| _PrimitiveShortSerialize          | Jil                |           121.33 ns |     NA |        5 B |       0.0421 |            - |            - |           264 B |
| _PrimitiveShortSerialize          | SpanJson           |            73.07 ns |     NA |        5 B |       0.0050 |            - |            - |            32 B |
| _PrimitiveShortSerialize          | UTF8Json           |            43.02 ns |     NA |        5 B |       0.0051 |            - |            - |            32 B |
| _PrimitiveShortSerialize          | FsPickler          |           512.71 ns |     NA |       26 B |       0.2394 |       0.0010 |            - |         1,504 B |
| _PrimitiveShortSerialize          | Ceras              |           261.51 ns |     NA |        2 B |       0.6614 |            - |            - |         4,152 B |
| _PrimitiveShortSerialize          | OdinSerializer_    |           294.52 ns |     NA |        3 B |       0.0048 |            - |            - |            32 B |
| _PrimitiveShortSerialize          | Nino               |           105.81 ns |     NA |        2 B |       0.0050 |            - |            - |            32 B |
| **_PrimitiveStringDeserialize**   | **MessagePack_v2** |       **624.01 ns** | **NA** |      **-** |   **0.0458** |        **-** |        **-** |       **288 B** |
| _PrimitiveStringDeserialize       | ProtobufNet        |           364.97 ns |     NA |          - |       0.0496 |            - |            - |           312 B |
| _PrimitiveStringDeserialize       | JsonNet            |           752.49 ns |     NA |          - |       0.9394 |       0.0124 |            - |         5,896 B |
| _PrimitiveStringDeserialize       | BinaryFormatter    |         3,406.47 ns |     NA |          - |       0.4044 |            - |            - |         2,560 B |
| _PrimitiveStringDeserialize       | DataContract       |         1,557.42 ns |     NA |          - |       0.7420 |       0.0095 |            - |         4,664 B |
| _PrimitiveStringDeserialize       | Hyperion           |           138.30 ns |     NA |          - |       0.0827 |            - |            - |           520 B |
| _PrimitiveStringDeserialize       | Jil                |           430.76 ns |     NA |          - |       0.1326 |            - |            - |           832 B |
| _PrimitiveStringDeserialize       | SpanJson           |           160.87 ns |     NA |          - |       0.0355 |            - |            - |           224 B |
| _PrimitiveStringDeserialize       | UTF8Json           |           328.46 ns |     NA |          - |       0.0353 |            - |            - |           224 B |
| _PrimitiveStringDeserialize       | FsPickler          |           483.99 ns |     NA |          - |       0.1974 |            - |            - |         1,240 B |
| _PrimitiveStringDeserialize       | Ceras              |           145.82 ns |     NA |          - |       0.0355 |            - |            - |           224 B |
| _PrimitiveStringDeserialize       | OdinSerializer_    |           342.13 ns |     NA |          - |       0.0353 |            - |            - |           224 B |
| _PrimitiveStringDeserialize       | Nino               |        13,692.03 ns |     NA |          - |       0.0305 |       0.0153 |            - |           256 B |
| **_PrimitiveStringSerialize**     | **MessagePack_v2** |       **485.16 ns** | **NA** |   **21 B** |   **0.0172** |        **-** |        **-** |       **112 B** |
| _PrimitiveStringSerialize         | ProtobufNet        |           262.61 ns |     NA |      102 B |       0.0749 |            - |            - |           472 B |
| _PrimitiveStringSerialize         | JsonNet            |           681.49 ns |     NA |      105 B |       0.9842 |       0.0114 |            - |         6,176 B |
| _PrimitiveStringSerialize         | BinaryFormatter    |           937.29 ns |     NA |      124 B |       0.4549 |       0.0019 |            - |         2,856 B |
| _PrimitiveStringSerialize         | DataContract       |           816.23 ns |     NA |      177 B |       0.2851 |       0.0010 |            - |         1,792 B |
| _PrimitiveStringSerialize         | Hyperion           |           209.85 ns |     NA |      102 B |       0.1109 |            - |            - |           696 B |
| _PrimitiveStringSerialize         | Jil                |           606.86 ns |     NA |      102 B |       0.1440 |            - |            - |           904 B |
| _PrimitiveStringSerialize         | SpanJson           |           253.58 ns |     NA |      102 B |       0.0200 |            - |            - |           128 B |
| _PrimitiveStringSerialize         | UTF8Json           |           182.71 ns |     NA |      102 B |       0.0203 |            - |            - |           128 B |
| _PrimitiveStringSerialize         | FsPickler          |           561.91 ns |     NA |      127 B |       0.2546 |       0.0010 |            - |         1,600 B |
| _PrimitiveStringSerialize         | Ceras              |           331.38 ns |     NA |      101 B |       0.6766 |            - |            - |         4,248 B |
| _PrimitiveStringSerialize         | OdinSerializer_    |           333.51 ns |     NA |      206 B |       0.0367 |            - |            - |           232 B |
| _PrimitiveStringSerialize         | Nino               |         4,248.66 ns |     NA |        7 B |       0.0076 |            - |            - |            64 B |
| **_PrimitiveUIntDeserialize**     | **MessagePack_v2** |       **136.92 ns** | **NA** |      **-** |   **0.0100** |        **-** |        **-** |        **64 B** |
| _PrimitiveUIntDeserialize         | ProtobufNet        |           275.12 ns |     NA |          - |       0.0138 |            - |            - |            88 B |
| _PrimitiveUIntDeserialize         | JsonNet            |           619.16 ns |     NA |          - |       0.9108 |       0.0105 |            - |         5,720 B |
| _PrimitiveUIntDeserialize         | BinaryFormatter    |         4,946.14 ns |     NA |          - |       0.6561 |            - |            - |         4,120 B |
| _PrimitiveUIntDeserialize         | DataContract       |         1,156.28 ns |     NA |          - |       0.6580 |       0.0057 |            - |         4,136 B |
| _PrimitiveUIntDeserialize         | Hyperion           |            71.70 ns |     NA |          - |       0.0305 |            - |            - |           192 B |
| _PrimitiveUIntDeserialize         | Jil                |            53.14 ns |     NA |          - |       0.0191 |            - |            - |           120 B |
| _PrimitiveUIntDeserialize         | SpanJson           |            15.32 ns |     NA |          - |            - |            - |            - |               - |
| _PrimitiveUIntDeserialize         | UTF8Json           |            24.39 ns |     NA |          - |            - |            - |            - |               - |
| _PrimitiveUIntDeserialize         | FsPickler          |           378.73 ns |     NA |          - |       0.1616 |       0.0005 |            - |         1,016 B |
| _PrimitiveUIntDeserialize         | Ceras              |            64.26 ns |     NA |          - |            - |            - |            - |               - |
| _PrimitiveUIntDeserialize         | OdinSerializer_    |           299.52 ns |     NA |          - |            - |            - |            - |               - |
| _PrimitiveUIntDeserialize         | Nino               |           180.75 ns |     NA |          - |            - |            - |            - |               - |
| **_PrimitiveUIntSerialize**       | **MessagePack_v2** |        **87.69 ns** | **NA** |    **1 B** |   **0.0153** |        **-** |        **-** |        **96 B** |
| _PrimitiveUIntSerialize           | ProtobufNet        |           184.89 ns |     NA |        2 B |       0.0598 |            - |            - |           376 B |
| _PrimitiveUIntSerialize           | JsonNet            |           514.46 ns |     NA |        4 B |       0.9556 |       0.0191 |            - |         6,000 B |
| _PrimitiveUIntSerialize           | BinaryFormatter    |         1,444.28 ns |     NA |       55 B |       0.5512 |       0.0057 |            - |         3,464 B |
| _PrimitiveUIntSerialize           | DataContract       |           616.00 ns |     NA |       88 B |       0.2737 |            - |            - |         1,720 B |
| _PrimitiveUIntSerialize           | Hyperion           |           144.80 ns |     NA |        5 B |       0.0789 |            - |            - |           496 B |
| _PrimitiveUIntSerialize           | Jil                |            93.52 ns |     NA |        1 B |       0.0408 |            - |            - |           256 B |
| _PrimitiveUIntSerialize           | SpanJson           |            48.83 ns |     NA |        1 B |       0.0051 |            - |            - |            32 B |
| _PrimitiveUIntSerialize           | UTF8Json           |            34.17 ns |     NA |        1 B |       0.0051 |            - |            - |            32 B |
| _PrimitiveUIntSerialize           | FsPickler          |           495.89 ns |     NA |       29 B |       0.2394 |            - |            - |         1,504 B |
| _PrimitiveUIntSerialize           | Ceras              |           264.94 ns |     NA |        1 B |       0.6614 |            - |            - |         4,152 B |
| _PrimitiveUIntSerialize           | OdinSerializer_    |           284.56 ns |     NA |        5 B |       0.0048 |            - |            - |            32 B |
| _PrimitiveUIntSerialize           | Nino               |           108.00 ns |     NA |        2 B |       0.0050 |            - |            - |            32 B |
| **_PrimitiveULongDeserialize**    | **MessagePack_v2** |       **135.19 ns** | **NA** |      **-** |   **0.0100** |        **-** |        **-** |        **64 B** |
| _PrimitiveULongDeserialize        | ProtobufNet        |           277.98 ns |     NA |          - |       0.0138 |            - |            - |            88 B |
| _PrimitiveULongDeserialize        | JsonNet            |         2,119.86 ns |     NA |          - |       1.1787 |       0.0114 |            - |         7,400 B |
| _PrimitiveULongDeserialize        | BinaryFormatter    |         5,671.05 ns |     NA |          - |       0.6561 |            - |            - |         4,120 B |
| _PrimitiveULongDeserialize        | DataContract       |         1,467.42 ns |     NA |          - |       0.6790 |       0.0057 |            - |         4,264 B |
| _PrimitiveULongDeserialize        | Hyperion           |            78.86 ns |     NA |          - |       0.0305 |            - |            - |           192 B |
| _PrimitiveULongDeserialize        | Jil                |           123.67 ns |     NA |          - |       0.0253 |            - |            - |           160 B |
| _PrimitiveULongDeserialize        | SpanJson           |            50.52 ns |     NA |          - |            - |            - |            - |               - |
| _PrimitiveULongDeserialize        | UTF8Json           |            57.14 ns |     NA |          - |            - |            - |            - |               - |
| _PrimitiveULongDeserialize        | FsPickler          |           407.80 ns |     NA |          - |       0.1616 |       0.0005 |            - |         1,016 B |
| _PrimitiveULongDeserialize        | Ceras              |            64.85 ns |     NA |          - |            - |            - |            - |               - |
| _PrimitiveULongDeserialize        | OdinSerializer_    |           293.22 ns |     NA |          - |            - |            - |            - |               - |
| _PrimitiveULongDeserialize        | Nino               |           179.89 ns |     NA |          - |            - |            - |            - |               - |
| **_PrimitiveULongSerialize**      | **MessagePack_v2** |        **94.27 ns** | **NA** |    **9 B** |   **0.0166** |        **-** |        **-** |       **104 B** |
| _PrimitiveULongSerialize          | ProtobufNet        |           206.13 ns |     NA |       11 B |       0.0610 |            - |            - |           384 B |
| _PrimitiveULongSerialize          | JsonNet            |           580.04 ns |     NA |       23 B |       0.9737 |       0.0095 |            - |         6,112 B |
| _PrimitiveULongSerialize          | BinaryFormatter    |         1,397.11 ns |     NA |       59 B |       0.5531 |       0.0038 |            - |         3,472 B |
| _PrimitiveULongSerialize          | DataContract       |           688.83 ns |     NA |      109 B |       0.2880 |       0.0010 |            - |         1,808 B |
| _PrimitiveULongSerialize          | Hyperion           |           162.45 ns |     NA |        9 B |       0.0801 |            - |            - |           504 B |
| _PrimitiveULongSerialize          | Jil                |           159.11 ns |     NA |       20 B |       0.0663 |            - |            - |           416 B |
| _PrimitiveULongSerialize          | SpanJson           |            85.84 ns |     NA |       20 B |       0.0076 |            - |            - |            48 B |
| _PrimitiveULongSerialize          | UTF8Json           |            78.69 ns |     NA |       20 B |       0.0076 |            - |            - |            48 B |
| _PrimitiveULongSerialize          | FsPickler          |           479.23 ns |     NA |       33 B |       0.2408 |       0.0005 |            - |         1,512 B |
| _PrimitiveULongSerialize          | Ceras              |           254.65 ns |     NA |        8 B |       0.6614 |            - |            - |         4,152 B |
| _PrimitiveULongSerialize          | OdinSerializer_    |           317.66 ns |     NA |        9 B |       0.0062 |            - |            - |            40 B |
| _PrimitiveULongSerialize          | Nino               |           111.02 ns |     NA |        9 B |       0.0063 |            - |            - |            40 B |
| **_PrimitiveUShortDeserialize**   | **MessagePack_v2** |       **144.22 ns** | **NA** |      **-** |   **0.0100** |        **-** |        **-** |        **64 B** |
| _PrimitiveUShortDeserialize       | ProtobufNet        |           284.22 ns |     NA |          - |       0.0138 |            - |            - |            88 B |
| _PrimitiveUShortDeserialize       | JsonNet            |           657.02 ns |     NA |          - |       0.9108 |       0.0105 |            - |         5,720 B |
| _PrimitiveUShortDeserialize       | BinaryFormatter    |         5,095.99 ns |     NA |          - |       0.6561 |            - |            - |         4,120 B |
| _PrimitiveUShortDeserialize       | DataContract       |         1,207.95 ns |     NA |          - |       0.6580 |       0.0057 |            - |         4,136 B |
| _PrimitiveUShortDeserialize       | Hyperion           |            75.77 ns |     NA |          - |       0.0305 |            - |            - |           192 B |
| _PrimitiveUShortDeserialize       | Jil                |            70.78 ns |     NA |          - |       0.0204 |            - |            - |           128 B |
| _PrimitiveUShortDeserialize       | SpanJson           |            32.45 ns |     NA |          - |            - |            - |            - |               - |
| _PrimitiveUShortDeserialize       | UTF8Json           |            32.17 ns |     NA |          - |            - |            - |            - |               - |
| _PrimitiveUShortDeserialize       | FsPickler          |           368.74 ns |     NA |          - |       0.1616 |       0.0005 |            - |         1,016 B |
| _PrimitiveUShortDeserialize       | Ceras              |            63.49 ns |     NA |          - |            - |            - |            - |               - |
| _PrimitiveUShortDeserialize       | OdinSerializer_    |           323.71 ns |     NA |          - |            - |            - |            - |               - |
| _PrimitiveUShortDeserialize       | Nino               |           194.32 ns |     NA |          - |            - |            - |            - |               - |
| **_PrimitiveUShortSerialize**     | **MessagePack_v2** |        **93.99 ns** | **NA** |    **3 B** |   **0.0153** |        **-** |        **-** |        **96 B** |
| _PrimitiveUShortSerialize         | ProtobufNet        |           200.42 ns |     NA |        4 B |       0.0598 |            - |            - |           376 B |
| _PrimitiveUShortSerialize         | JsonNet            |           625.27 ns |     NA |        8 B |       0.9708 |       0.0076 |            - |         6,096 B |
| _PrimitiveUShortSerialize         | BinaryFormatter    |         1,380.41 ns |     NA |       53 B |       0.5512 |       0.0057 |            - |         3,464 B |
| _PrimitiveUShortSerialize         | DataContract       |           665.99 ns |     NA |       96 B |       0.2747 |            - |            - |         1,728 B |
| _PrimitiveUShortSerialize         | Hyperion           |           159.35 ns |     NA |        3 B |       0.0789 |            - |            - |           496 B |
| _PrimitiveUShortSerialize         | Jil                |           121.10 ns |     NA |        5 B |       0.0421 |            - |            - |           264 B |
| _PrimitiveUShortSerialize         | SpanJson           |            60.70 ns |     NA |        5 B |       0.0050 |            - |            - |            32 B |
| _PrimitiveUShortSerialize         | UTF8Json           |            41.84 ns |     NA |        5 B |       0.0051 |            - |            - |            32 B |
| _PrimitiveUShortSerialize         | FsPickler          |           484.03 ns |     NA |       27 B |       0.2394 |            - |            - |         1,504 B |
| _PrimitiveUShortSerialize         | Ceras              |           248.89 ns |     NA |        2 B |       0.6614 |            - |            - |         4,152 B |
| _PrimitiveUShortSerialize         | OdinSerializer_    |           291.88 ns |     NA |        3 B |       0.0048 |            - |            - |            32 B |
| _PrimitiveUShortSerialize         | Nino               |           111.62 ns |     NA |        2 B |       0.0050 |            - |            - |            32 B |
| **AccessTokenDeserialize**        | **MessagePack_v2** |       **268.36 ns** | **NA** |      **-** |   **0.0176** |        **-** |        **-** |       **112 B** |
| AccessTokenDeserialize            | ProtobufNet        |           411.96 ns |     NA |          - |       0.0215 |            - |            - |           136 B |
| AccessTokenDeserialize            | JsonNet            |         1,897.80 ns |     NA |          - |       0.9232 |       0.0076 |            - |         5,792 B |
| AccessTokenDeserialize            | BinaryFormatter    |         6,814.71 ns |     NA |          - |       0.8316 |       0.0076 |            - |         5,240 B |
| AccessTokenDeserialize            | DataContract       |         3,691.35 ns |     NA |          - |       1.3733 |       0.0229 |            - |         8,632 B |
| AccessTokenDeserialize            | Hyperion           |           353.91 ns |     NA |          - |       0.0710 |            - |            - |           448 B |
| AccessTokenDeserialize            | Jil                |           403.60 ns |     NA |          - |       0.0520 |            - |            - |           328 B |
| AccessTokenDeserialize            | SpanJson           |           127.49 ns |     NA |          - |       0.0076 |            - |            - |            48 B |
| AccessTokenDeserialize            | UTF8Json           |           349.49 ns |     NA |          - |       0.0076 |            - |            - |            48 B |
| AccessTokenDeserialize            | FsPickler          |           519.98 ns |     NA |          - |       0.1974 |            - |            - |         1,240 B |
| AccessTokenDeserialize            | Ceras              |           223.16 ns |     NA |          - |       0.0076 |            - |            - |            48 B |
| AccessTokenDeserialize            | OdinSerializer_    |         2,140.74 ns |     NA |          - |       0.0992 |            - |            - |           632 B |
| AccessTokenDeserialize            | Nino               |        16,687.07 ns |     NA |          - |            - |            - |            - |           112 B |
| **AccessTokenSerialize**          | **MessagePack_v2** |       **388.17 ns** | **NA** |   **19 B** |   **0.0176** |        **-** |        **-** |       **112 B** |
| AccessTokenSerialize              | ProtobufNet        |           318.21 ns |     NA |        6 B |       0.0596 |            - |            - |           376 B |
| AccessTokenSerialize              | JsonNet            |         1,099.93 ns |     NA |       82 B |       0.9956 |       0.0114 |            - |         6,256 B |
| AccessTokenSerialize              | BinaryFormatter    |         3,143.96 ns |     NA |      392 B |       0.8392 |       0.0114 |            - |         5,280 B |
| AccessTokenSerialize              | DataContract       |         1,721.30 ns |     NA |      333 B |       0.4253 |       0.0019 |            - |         2,680 B |
| AccessTokenSerialize              | Hyperion           |           233.97 ns |     NA |       69 B |       0.1044 |            - |            - |           656 B |
| AccessTokenSerialize              | Jil                |           606.24 ns |     NA |       80 B |       0.1478 |            - |            - |           928 B |
| AccessTokenSerialize              | SpanJson           |           133.58 ns |     NA |       53 B |       0.0126 |            - |            - |            80 B |
| AccessTokenSerialize              | UTF8Json           |           216.86 ns |     NA |       79 B |       0.0165 |            - |            - |           104 B |
| AccessTokenSerialize              | FsPickler          |           641.01 ns |     NA |       67 B |       0.2546 |       0.0010 |            - |         1,600 B |
| AccessTokenSerialize              | Ceras              |         1,468.64 ns |     NA |       12 B |       0.6618 |            - |            - |         4,160 B |
| AccessTokenSerialize              | OdinSerializer_    |         2,488.87 ns |     NA |      440 B |       0.0801 |            - |            - |           512 B |
| AccessTokenSerialize              | Nino               |         3,622.44 ns |     NA |        8 B |       0.0076 |            - |            - |            64 B |
| **AccountMergeDeserialize**       | **MessagePack_v2** |       **222.08 ns** | **NA** |      **-** |   **0.0153** |        **-** |        **-** |        **96 B** |
| AccountMergeDeserialize           | ProtobufNet        |           392.37 ns |     NA |          - |       0.0191 |            - |            - |           120 B |
| AccountMergeDeserialize           | JsonNet            |         1,728.22 ns |     NA |          - |       0.9232 |       0.0057 |            - |         5,800 B |
| AccountMergeDeserialize           | BinaryFormatter    |         6,233.89 ns |     NA |          - |       0.7706 |       0.0076 |            - |         4,848 B |
| AccountMergeDeserialize           | DataContract       |         3,161.56 ns |     NA |          - |       1.9951 |       0.0572 |            - |        12,536 B |
| AccountMergeDeserialize           | Hyperion           |           338.03 ns |     NA |          - |       0.0687 |            - |            - |           432 B |
| AccountMergeDeserialize           | Jil                |           364.89 ns |     NA |          - |       0.0467 |            - |            - |           296 B |
| AccountMergeDeserialize           | SpanJson           |           152.18 ns |     NA |          - |       0.0050 |            - |            - |            32 B |
| AccountMergeDeserialize           | UTF8Json           |           308.59 ns |     NA |          - |       0.0048 |            - |            - |            32 B |
| AccountMergeDeserialize           | FsPickler          |           490.84 ns |     NA |          - |       0.1955 |            - |            - |         1,232 B |
| AccountMergeDeserialize           | Ceras              |           203.95 ns |     NA |          - |       0.0050 |            - |            - |            32 B |
| AccountMergeDeserialize           | OdinSerializer_    |         1,861.64 ns |     NA |          - |       0.0916 |            - |            - |           576 B |
| AccountMergeDeserialize           | Nino               |        16,520.30 ns |     NA |          - |            - |            - |            - |            64 B |
| **AccountMergeSerialize**         | **MessagePack_v2** |       **363.68 ns** | **NA** |   **18 B** |   **0.0176** |        **-** |        **-** |       **112 B** |
| AccountMergeSerialize             | ProtobufNet        |           334.75 ns |     NA |        6 B |       0.0596 |            - |            - |           376 B |
| AccountMergeSerialize             | JsonNet            |         1,092.77 ns |     NA |       72 B |       0.9975 |       0.0095 |            - |         6,264 B |
| AccountMergeSerialize             | BinaryFormatter    |         2,336.13 ns |     NA |      250 B |       0.6790 |       0.0076 |            - |         4,264 B |
| AccountMergeSerialize             | DataContract       |         1,312.73 ns |     NA |      253 B |       0.3929 |       0.0019 |            - |         2,472 B |
| AccountMergeSerialize             | Hyperion           |           227.66 ns |     NA |       72 B |       0.0994 |            - |            - |           624 B |
| AccountMergeSerialize             | Jil                |           566.19 ns |     NA |       70 B |       0.1144 |            - |            - |           720 B |
| AccountMergeSerialize             | SpanJson           |           152.42 ns |     NA |       69 B |       0.0153 |            - |            - |            96 B |
| AccountMergeSerialize             | UTF8Json           |           184.57 ns |     NA |       69 B |       0.0153 |            - |            - |            96 B |
| AccountMergeSerialize             | FsPickler          |           640.86 ns |     NA |       67 B |       0.2546 |       0.0010 |            - |         1,600 B |
| AccountMergeSerialize             | Ceras              |         1,467.56 ns |     NA |       11 B |       0.6618 |            - |            - |         4,160 B |
| AccountMergeSerialize             | OdinSerializer_    |         2,262.32 ns |     NA |      408 B |       0.0801 |            - |            - |           504 B |
| AccountMergeSerialize             | Nino               |         3,929.04 ns |     NA |        7 B |       0.0076 |            - |            - |            64 B |
| **AnswerDeserialize**             | **MessagePack_v2** |     **1,034.95 ns** | **NA** |      **-** |   **0.0324** |        **-** |        **-** |       **208 B** |
| AnswerDeserialize                 | ProtobufNet        |           712.57 ns |     NA |          - |       0.0362 |            - |            - |           232 B |
| AnswerDeserialize                 | JsonNet            |         7,000.23 ns |     NA |          - |       0.9995 |       0.0076 |            - |         6,296 B |
| AnswerDeserialize                 | BinaryFormatter    |        12,179.86 ns |     NA |          - |       1.3885 |       0.0153 |            - |         8,784 B |
| AnswerDeserialize                 | DataContract       |        10,940.16 ns |     NA |          - |       2.1210 |       0.0458 |            - |        13,392 B |
| AnswerDeserialize                 | Hyperion           |           534.16 ns |     NA |          - |       0.0849 |            - |            - |           536 B |
| AnswerDeserialize                 | Jil                |         2,449.85 ns |     NA |          - |       0.1869 |            - |            - |         1,184 B |
| AnswerDeserialize                 | SpanJson           |           660.70 ns |     NA |          - |       0.0229 |            - |            - |           144 B |
| AnswerDeserialize                 | UTF8Json           |         1,668.10 ns |     NA |          - |       0.0229 |            - |            - |           144 B |
| AnswerDeserialize                 | FsPickler          |           726.72 ns |     NA |          - |       0.2108 |            - |            - |         1,328 B |
| AnswerDeserialize                 | Ceras              |           308.64 ns |     NA |          - |       0.0229 |            - |            - |           144 B |
| AnswerDeserialize                 | OdinSerializer_    |         7,010.42 ns |     NA |          - |       0.3815 |            - |            - |         2,416 B |
| AnswerDeserialize                 | Nino               |        15,620.81 ns |     NA |          - |       0.0305 |            - |            - |           240 B |
| **AnswerSerialize**               | **MessagePack_v2** |       **920.33 ns** | **NA** |   **53 B** |   **0.0229** |        **-** |        **-** |       **144 B** |
| AnswerSerialize                   | ProtobufNet        |           710.36 ns |     NA |       30 B |       0.0629 |            - |            - |           400 B |
| AnswerSerialize                   | JsonNet            |         4,429.17 ns |     NA |      458 B |       1.1902 |       0.0229 |            - |         7,480 B |
| AnswerSerialize                   | BinaryFormatter    |        10,294.14 ns |     NA |     1117 B |       1.7395 |       0.0305 |            - |        10,944 B |
| AnswerSerialize                   | DataContract       |         4,803.80 ns |     NA |      883 B |       0.9155 |       0.0076 |            - |         5,768 B |
| AnswerSerialize                   | Hyperion           |           460.03 ns |     NA |      129 B |       0.1345 |            - |            - |           848 B |
| AnswerSerialize                   | Jil                |         2,294.16 ns |     NA |      460 B |       0.4730 |            - |            - |         2,984 B |
| AnswerSerialize                   | SpanJson           |           478.29 ns |     NA |      353 B |       0.0610 |            - |            - |           384 B |
| AnswerSerialize                   | UTF8Json           |           910.53 ns |     NA |      455 B |       0.0763 |            - |            - |           480 B |
| AnswerSerialize                   | FsPickler          |           920.36 ns |     NA |      130 B |       0.2651 |            - |            - |         1,664 B |
| AnswerSerialize                   | Ceras              |         1,535.40 ns |     NA |       58 B |       0.6695 |            - |            - |         4,208 B |
| AnswerSerialize                   | OdinSerializer_    |         5,521.99 ns |     NA |     1584 B |       0.3128 |            - |            - |         1,968 B |
| AnswerSerialize                   | Nino               |         4,327.21 ns |     NA |       20 B |       0.0076 |            - |            - |            80 B |
| **BadgeDeserialize**              | **MessagePack_v2** |       **266.48 ns** | **NA** |      **-** |   **0.0176** |        **-** |        **-** |       **112 B** |
| BadgeDeserialize                  | ProtobufNet        |           295.76 ns |     NA |          - |       0.0215 |            - |            - |           136 B |
| BadgeDeserialize                  | JsonNet            |         1,995.34 ns |     NA |          - |       0.9193 |       0.0038 |            - |         5,768 B |
| BadgeDeserialize                  | BinaryFormatter    |         6,625.49 ns |     NA |          - |       0.8011 |       0.0076 |            - |         5,072 B |
| BadgeDeserialize                  | DataContract       |         3,542.08 ns |     NA |          - |       1.3351 |       0.0076 |            - |         8,400 B |
| BadgeDeserialize                  | Hyperion           |           344.78 ns |     NA |          - |       0.0701 |            - |            - |           440 B |
| BadgeDeserialize                  | Jil                |           300.61 ns |     NA |          - |       0.0496 |            - |            - |           312 B |
| BadgeDeserialize                  | SpanJson           |            84.92 ns |     NA |          - |       0.0076 |            - |            - |            48 B |
| BadgeDeserialize                  | UTF8Json           |           275.87 ns |     NA |          - |       0.0076 |            - |            - |            48 B |
| BadgeDeserialize                  | FsPickler          |           521.38 ns |     NA |          - |       0.1955 |            - |            - |         1,232 B |
| BadgeDeserialize                  | Ceras              |           221.36 ns |     NA |          - |       0.0076 |            - |            - |            48 B |
| BadgeDeserialize                  | OdinSerializer_    |         1,915.66 ns |     NA |          - |       0.0896 |            - |            - |           568 B |
| BadgeDeserialize                  | Nino               |        16,600.78 ns |     NA |          - |            - |            - |            - |            80 B |
| **BadgeSerialize**                | **MessagePack_v2** |       **411.23 ns** | **NA** |    **9 B** |   **0.0162** |        **-** |        **-** |       **104 B** |
| BadgeSerialize                    | ProtobufNet        |           193.09 ns |     NA |        0 B |       0.0100 |            - |            - |            64 B |
| BadgeSerialize                    | JsonNet            |         1,147.95 ns |     NA |       74 B |       0.9804 |       0.0095 |            - |         6,152 B |
| BadgeSerialize                    | BinaryFormatter    |         2,807.13 ns |     NA |      278 B |       0.7782 |       0.0114 |            - |         4,896 B |
| BadgeSerialize                    | DataContract       |         1,422.44 ns |     NA |      250 B |       0.3376 |            - |            - |         2,120 B |
| BadgeSerialize                    | Hyperion           |           233.56 ns |     NA |       59 B |       0.1135 |            - |            - |           712 B |
| BadgeSerialize                    | Jil                |           408.01 ns |     NA |       71 B |       0.1440 |            - |            - |           904 B |
| BadgeSerialize                    | SpanJson           |            76.96 ns |     NA |       28 B |       0.0088 |            - |            - |            56 B |
| BadgeSerialize                    | UTF8Json           |            98.75 ns |     NA |       71 B |       0.0153 |            - |            - |            96 B |
| BadgeSerialize                    | FsPickler          |           645.11 ns |     NA |       54 B |       0.2518 |       0.0010 |            - |         1,584 B |
| BadgeSerialize                    | Ceras              |         1,645.48 ns |     NA |        6 B |       0.6599 |            - |            - |         4,152 B |
| BadgeSerialize                    | OdinSerializer_    |         2,258.11 ns |     NA |      382 B |       0.0725 |            - |            - |           456 B |
| BadgeSerialize                    | Nino               |         3,645.54 ns |     NA |        9 B |       0.0114 |            - |            - |            72 B |
| **CommentDeserialize**            | **MessagePack_v2** |       **335.51 ns** | **NA** |      **-** |   **0.0200** |        **-** |        **-** |       **128 B** |
| CommentDeserialize                | ProtobufNet        |           395.92 ns |     NA |          - |       0.0238 |            - |            - |           152 B |
| CommentDeserialize                | JsonNet            |         3,149.97 ns |     NA |          - |       0.9384 |       0.0076 |            - |         5,904 B |
| CommentDeserialize                | BinaryFormatter    |         8,018.71 ns |     NA |          - |       0.9155 |            - |            - |         5,832 B |
| CommentDeserialize                | DataContract       |         5,338.70 ns |     NA |          - |       2.0218 |       0.0381 |            - |        12,728 B |
| CommentDeserialize                | Hyperion           |           367.76 ns |     NA |          - |       0.0725 |            - |            - |           456 B |
| CommentDeserialize                | Jil                |           645.89 ns |     NA |          - |       0.0763 |            - |            - |           480 B |
| CommentDeserialize                | SpanJson           |           226.20 ns |     NA |          - |       0.0100 |            - |            - |            64 B |
| CommentDeserialize                | UTF8Json           |           577.66 ns |     NA |          - |       0.0095 |            - |            - |            64 B |
| CommentDeserialize                | FsPickler          |           592.31 ns |     NA |          - |       0.1984 |            - |            - |         1,248 B |
| CommentDeserialize                | Ceras              |           244.11 ns |     NA |          - |       0.0100 |            - |            - |            64 B |
| CommentDeserialize                | OdinSerializer_    |         3,264.95 ns |     NA |          - |       0.1717 |            - |            - |         1,080 B |
| CommentDeserialize                | Nino               |        17,379.18 ns |     NA |          - |            - |            - |            - |            96 B |
| **CommentSerialize**              | **MessagePack_v2** |       **425.08 ns** | **NA** |   **27 B** |   **0.0191** |        **-** |        **-** |       **120 B** |
| CommentSerialize                  | ProtobufNet        |           353.10 ns |     NA |        6 B |       0.0596 |            - |            - |           376 B |
| CommentSerialize                  | JsonNet            |         1,803.09 ns |     NA |      151 B |       1.0223 |       0.0114 |            - |         6,416 B |
| CommentSerialize                  | BinaryFormatter    |         4,097.21 ns |     NA |      403 B |       0.8545 |       0.0153 |            - |         5,408 B |
| CommentSerialize                  | DataContract       |         1,970.73 ns |     NA |      361 B |       0.4272 |            - |            - |         2,696 B |
| CommentSerialize                  | Hyperion           |           280.90 ns |     NA |       76 B |       0.1159 |            - |            - |           728 B |
| CommentSerialize                  | Jil                |           765.04 ns |     NA |      149 B |       0.1898 |            - |            - |         1,192 B |
| CommentSerialize                  | SpanJson           |           165.38 ns |     NA |      104 B |       0.0203 |            - |            - |           128 B |
| CommentSerialize                  | UTF8Json           |           261.76 ns |     NA |      148 B |       0.0277 |            - |            - |           176 B |
| CommentSerialize                  | FsPickler          |           719.55 ns |     NA |       71 B |       0.2546 |            - |            - |         1,600 B |
| CommentSerialize                  | Ceras              |         1,468.02 ns |     NA |       17 B |       0.6638 |            - |            - |         4,168 B |
| CommentSerialize                  | OdinSerializer_    |         3,037.72 ns |     NA |      708 B |       0.1373 |            - |            - |           880 B |
| CommentSerialize                  | Nino               |         3,864.43 ns |     NA |       12 B |       0.0076 |            - |            - |            72 B |
| **NestedDataDeserialize**         | **MessagePack_v2** | **3,613,563.52 ns** | **NA** |      **-** | **375.0000** | **292.9688** | **148.4375** | **2,030,342 B** |
| NestedDataDeserialize             | ProtobufNet        |     3,479,566.45 ns |     NA |          - |     226.5625 |     113.2813 |            - |     1,441,205 B |
| NestedDataDeserialize             | JsonNet            |    30,474,882.00 ns |     NA |          - |     812.5000 |     312.5000 |     125.0000 |     4,908,629 B |
| NestedDataDeserialize             | BinaryFormatter    |    58,800,464.22 ns |     NA |          - |    2666.6667 |    1222.2222 |     555.5556 |    13,916,087 B |
| NestedDataDeserialize             | DataContract       |    31,342,046.75 ns |     NA |          - |     531.2500 |     218.7500 |      93.7500 |     3,075,812 B |
| NestedDataDeserialize             | Hyperion           |     4,401,444.55 ns |     NA |          - |     375.0000 |     187.5000 |            - |     2,401,112 B |
| NestedDataDeserialize             | Jil                |    12,299,184.41 ns |     NA |          - |     640.6250 |     453.1250 |     281.2500 |     5,283,287 B |
| NestedDataDeserialize             | SpanJson           |     6,555,297.98 ns |     NA |          - |     226.5625 |     109.3750 |            - |     1,442,144 B |
| NestedDataDeserialize             | UTF8Json           |    13,356,754.66 ns |     NA |          - |     359.3750 |     156.2500 |      46.8750 |     2,121,567 B |
| NestedDataDeserialize             | FsPickler          |     3,701,069.02 ns |     NA |          - |     437.5000 |     218.7500 |     218.7500 |     2,383,296 B |
| NestedDataDeserialize             | Ceras              |     2,115,749.07 ns |     NA |          - |     226.5625 |     113.2813 |            - |     1,440,093 B |
| NestedDataDeserialize             | OdinSerializer_    |    34,392,958.07 ns |     NA |          - |    1200.0000 |     600.0000 |     266.6667 |     8,105,632 B |
| NestedDataDeserialize             | Nino               |     1,849,973.09 ns |     NA |          - |     228.5156 |     113.2813 |            - |     1,440,156 B |
| **NestedDataSerialize**           | **MessagePack_v2** | **1,759,646.94 ns** | **NA** | **2383 B** |        **-** |        **-** |        **-** |     **4,521 B** |
| NestedDataSerialize               | ProtobufNet        |     3,576,022.67 ns |     NA |   630006 B |     601.5625 |     582.0313 |     582.0313 |     2,708,501 B |
| NestedDataSerialize               | JsonNet            |    18,718,156.84 ns |     NA |  1220025 B |    1500.0000 |     968.7500 |     968.7500 |     8,634,666 B |
| NestedDataSerialize               | BinaryFormatter    |    34,527,235.75 ns |     NA |   890394 B |    1812.5000 |     937.5000 |     937.5000 |    10,688,644 B |
| NestedDataSerialize               | DataContract       |    15,449,519.38 ns |     NA |  1520173 B |     906.2500 |     656.2500 |     656.2500 |     6,977,282 B |
| NestedDataSerialize               | Hyperion           |     3,955,557.83 ns |     NA |   710203 B |     820.3125 |     664.0625 |     648.4375 |     3,769,873 B |
| NestedDataSerialize               | Jil                |     9,483,393.96 ns |     NA |  1310022 B |    1265.6250 |    1062.5000 |     625.0000 |     8,008,395 B |
| NestedDataSerialize               | SpanJson           |     7,057,591.90 ns |     NA |  1310022 B |     414.0625 |     414.0625 |     414.0625 |     3,407,350 B |
| NestedDataSerialize               | UTF8Json           |     8,951,755.55 ns |     NA |  1310022 B |    1109.3750 |     968.7500 |     968.7500 |     6,255,843 B |
| NestedDataSerialize               | FsPickler          |     3,962,245.26 ns |     NA |   690066 B |     929.6875 |     882.8125 |     871.0938 |     3,820,085 B |
| NestedDataSerialize               | Ceras              |     1,579,523.73 ns |     NA |   650009 B |     517.5781 |     498.0469 |     498.0469 |     2,737,163 B |
| NestedDataSerialize               | OdinSerializer_    |    16,805,724.53 ns |     NA |  1910351 B |    1093.7500 |     718.7500 |     718.7500 |    10,876,088 B |
| NestedDataSerialize               | Nino               |     2,534,503.20 ns |     NA |     1825 B |            - |            - |            - |         1,925 B |



### 说明

非Unity平台下，ZLib压缩解压算法的耗时较大，导致Nino耗时比较高，但是数据大的时候序列化和反序列化Nino都会比其他方案快，于此同时Nino的GC也比其他方案小，基础类型甚至做到了无GC序列化反序列化

### 性能

- **Nino无论在任何平台，GC都把控的很出色**
- Nino在**Unity平台**是目前当之无愧的**性能最卓越**的二进制序列化库，且真正意义上实现了**全平台都低GC**，并且在**Unity平台下**的**序列化速度远超同类**，甚至**在非Unity平台的反序列化速度都比MsgPack快**
- Nino**补上Emit后**，**非Unity平台**的序列化性能和GC可以**与MsgPack持平**
- Nino提供不同的压缩方案后，能做到在Net Core下也高性能

### 体积

- Nino的体积依然是最小的，**体积**是MsgPack LZ4压缩的**一半**，是Protobuf的**数百分之一**

- 在**全部C#平台**下，Nino序列化结果的**体积**是**当之无愧最小**的，最利于数据存储和网络通信的。

