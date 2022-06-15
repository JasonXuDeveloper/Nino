# Serilization 性能报告

#### [**测试数据**](Nino/Assets/Nino/Test/Data.cs)

*第一次序列化的时候，Nino会对类型进行缓存，达到预热效果，使得同一类型的第二次开始的序列化速度大幅度提升，其他库亦是如此*

> Nino 反序列化还未优化，预计优化后Nino Reflection 反序列化性能提高20%，Nino Code Gen 反序列化性能提高50%

### 总结

体积方面，Nino最小，MsgPack其次，其他库不尽人意

序列化速度方面，Nino Code Gen最快，MsgPack略慢一筹，Nino Reflection基本与Protobuf-net一致，其他库不尽人意

反序列化速度方面，MsgPack最快，Nino Code Gen略慢于MsgPack（还未优化Nino Code Gen反序列化），Nino Reflection基本与Protobuf-net一致，略微逊色于MongoDB.Bson，BinaryFormatter最糟糕

### 易用性

Nino、BinaryFormatter、可以轻松用于Unity或其他C#平台（Mono以及IL2CPP平台），无需针对不同平台进行任何额外操作

MsgPack需要在IL2CPP平台（Unity和Xamarin）进行额外处理（防止AOT问题，需要预生成代码，不然会导致无法使用），该操作十分繁琐

Protobuf-net以及MongoDB.Bson在IL2CPP平台下，字典会无法使用，因为是AOT问题，暂时没找到解决方案

### 备注

- 这里测试用的是MsgPack LZ4压缩，如果不开压缩的话，MsgPack的速度会快10%，但是体积则会变大很多（大概是Protobuf-net的体积的60%，即Nino的数倍）
- MsgPack之所以比较快是因为它用到了Emit以及生成了动态类型进行序列化（高效且低GC），但是在IL2CPP平台下，会遇到限制，所以上面才会提到MsgPack在IL2CPP平台使用起来很繁琐，因为需要预生成这些东西，这也就意味着MsgPack无法搭配现有热更新技术进行热更新数据类型并序列化（如ILRuntime，Huatuo这种C#热更新技术，是无法兼容MsgPack的），Nino Code Gen这边是静态生成进行序列化（高效且低GC），Nino生成的代码可以搭配ILRuntime或Huatuo技术实时热更
- Odin序列化也会针对编辑器/PC Mono平台使用Emit优化性能，以及动态生成序列化方法，在IL2CPP下有和MsgPack一样的限制，故而这里就不做与Odin序列化的性能对比了
  - Odin序列化的性能，比Protobuf-net略快（出自Odin序列化官方），故而该库性能比MsgPack慢，在使用了Emit以及动态代码的情况下，略慢于Nino，在其他情况下，比Nino慢不少比Protobuf-net快
  - Odin序列化的体积，与Protobuf-net相差无几，比Nino大三倍

### 体积（bytes）

![i1](https://s1.ax1x.com/2022/06/15/XowpM4.png)

> Nino < MsgPack (LZ4 Compress) < Protobuf-net < BinaryFormatter < MongoDB.Bson
>
> 体积方面可以忽略是否预热

### 序列化速度（ms）

![i2](https://s1.ax1x.com/2022/06/15/XodP4f.png)

> Nino Code Gen < MsgPack (LZ4 Compress) < MongoDB.Bson < Nino Reflection < Protobuf-net < BinaryFormatter

### 反序列化速度（ms）

![i3](https://s1.ax1x.com/2022/06/15/XodCUP.png)

> MsgPack (LZ4 Compress) < Nino Code Gen < MongoDB.Bson < Protobuf-net < Nino Reflection < BinaryFormatter