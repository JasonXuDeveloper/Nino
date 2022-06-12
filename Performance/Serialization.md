# Serilization 性能报告

#### [**测试数据**](Nino/Assets/Nino/Test/Data.cs)

*第一次序列化的时候，Nino会对类型进行缓存，达到预热效果，使得同一类型的第二次开始的序列化速度大幅度提升，其他库亦是如此*

### 总结

体积方面，Nino最小，MsgPack其次，其他库不尽人意

序列化速度方面，MsgPack最快，Nino Code Gen基本与MsgPack一致，Nino Reflection基本与Protobuf-net一致，其他库不尽人意

反序列化速度方面，MsgPack最快，Nino Code Gen基本与MsgPack一致，Nino Reflection基本与Protobuf-net一致，略微逊色于MongoDB.Bson，BinaryFormatter最糟糕

### 易用性

Nino、Protobuf-net、BinaryFormatter、MongoDB.Bson可以轻松用于Unity或其他C#平台（Mono以及IL2CPP平台）

MsgPack需要在IL2CPP平台（Unity和Xamarin）进行额外处理（防止AOT问题，需要预生成代码，不然会导致无法使用）

### 体积（bytes）

![i1](https://s1.ax1x.com/2022/06/12/X2uB0f.png)

> Nino < MsgPack (LZ4 Compress) < Protobuf-net < BinaryFormatter < MongoDB.Bson

### 序列化速度（ms）

![i2](https://s1.ax1x.com/2022/06/12/X2uD78.png)

> MsgPack (LZ4 Compress) < Nino Code Gen < MongoDB.Bson < Nino Reflection < Protobuf-net < BinaryFormatter

### 反序列化速度（ms）

![i3](https://s1.ax1x.com/2022/06/12/X2usAS.png)

> MsgPack (LZ4 Compress) < Nino Code Gen < MongoDB.Bson < Protobuf-net < Nino Reflection < BinaryFormatter