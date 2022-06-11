# Serilization 性能报告

## 序列化

#### **测试数据**

```csharp
using System;
using ProtoBuf;
using Nino.Serialization;

namespace Nino.Test
{
  [Serializable]
  [ProtoContract]
  [NinoSerialize]
  public partial struct Data
  {
    [ProtoMember(1)] [NinoMember(1)] public int x;

    [ProtoMember(2)] [NinoMember(2)] public short y;

    [ProtoMember(3)] [NinoMember(3)] public long z;

    [ProtoMember(4)] [NinoMember(4)] public float f;

    [ProtoMember(5)] [NinoMember(5)] public decimal d;

    [ProtoMember(6)] [NinoMember(6)] public double db;

    [ProtoMember(7)] [NinoMember(7)] public bool bo;

    [ProtoMember(8)] [NinoMember(8)] public TestEnum en;

    [ProtoMember(9)] [NinoMember(9)] public string name;
  }

  [Serializable]
  [ProtoContract]
  public enum TestEnum : byte
  {
    A = 1,
    B = 2
  }

  [Serializable]
  [ProtoContract]
  [NinoSerialize]
  public partial class NestedData
  {
    [ProtoMember(1)] [NinoMember(1)] public string name;

    [ProtoMember(2)] [NinoMember(2)] public Data[] ps;
  }
}
```

将实例化NestedData，然后对ps赋值（会测试不同长度的ps）并序列化

*第一次序列化的时候，Nino会对类型进行缓存，达到预热效果，使得同一类型的第二次开始的序列化速度大幅度提升，Protobuf-net亦是如此*

#### [Test1](Nino/Assets/Nino/Test/Editor/Serialization/Test1.cs)，无生成代码性能对比

![img1](https://s1.ax1x.com/2022/06/05/Xd24C6.png)

> 可以看到，**Nino未生成代码的情况下**，如果Nino和Protobuf-net都没预热，Nino序列化速度更快
>
> 如果双方都预热了，在未生成代码的情况下，Nino的序列化速度与Protobuf-net持平

#### [Test1](Nino/Assets/Nino/Test/Editor/Serialization/Test1.cs)，生成代码性能对比（Unity打开工程，菜单栏，Nino/Generator/Serialization Code）

![img1-2](https://s1.ax1x.com/2022/06/05/XdWpJx.png)

> 可以看到，**Nino生成代码的情况下**，无论是否有预热，序列化速度都是Nino更胜一筹，甚至在哪怕双方都进行了预热，Nino都能比Protobuf-net快50%

#### [Test1](Nino/Assets/Nino/Test/Editor/Serialization/Test1.cs)，序列化数据大小对比

![img1-3](https://s1.ax1x.com/2022/06/11/XcOrEF.png)

> 显而易见，Nino序列化数据的大小碾压Protobuf-net

| 对比库       | 预热 | 测试数组长度 | Nino | Nino Code Gen | Protobuf-net | 耗时差距百分比(Reflection) | 耗时差距百分比(Code Gen) | Nino  | Protobuf-net | 体积差距百分比 |
| ------------ | ---- | ------------ | ---- | ------------- | ------------ | -------------------------- | ------------------------ | ----- | ------------ | -------------- |
| Protobuf-net | x    | 10           | 17   | 15            | 45           | -62.22%                    | -66.67%                  | 53    | 638          | -91.69%        |
| Protobuf-net | √    | 100          | 2    | 0             | 2            | 0.00%                      | -100.00%                 | 94    | 6308         | -98.51%        |
| Protobuf-net | √    | 1000         | 17   | 9             | 16           | 6.25%                      | -43.75%                  | 254   | 63008        | -99.60%        |
| Protobuf-net | √    | 10000        | 164  | 94            | 181          | -9.39%                     | -48.07%                  | 1826  | 630008       | -99.71%        |
| Protobuf-net | √    | 100000       | 1651 | 935           | 1667         | -0.96%                     | -43.91%                  | 17538 | 6300008      | -99.72%        |

#### [Test2](Nino/Assets/Nino/Test/Editor/Serialization/Test2.cs)，无生成代码性能对比

![img2](https://s1.ax1x.com/2022/06/05/XdRUMD.png)

> 可以看到，**Nino未生成代码的情况下**，Nino序列化速度更快（基本快三分之一）

#### [Test2](Nino/Assets/Nino/Test/Editor/Serialization/Test2.cs)，生成代码性能对比（Unity打开工程，菜单栏，Nino/Generator/Serialization Code）

![img2-2](https://s1.ax1x.com/2022/06/05/XdWVwd.png)

> 可以看到，**Nino生成代码的情况下**，无论是否有预热，序列化速度都是Nino更胜一筹，甚至在进行了预热后，Nino能比BinaryFormatter快70%

#### [Test2](Nino/Assets/Nino/Test/Editor/Serialization/Test2.cs)，序列化数据大小对比

![img2-3](https://s1.ax1x.com/2022/06/11/XcOBHU.png)

> 显而易见，Nino序列化数据的大小碾压BinaryFormatter

| 对比库          | 预热 | 测试数组长度 | Nino | Nino Code Gen | BinaryFormatter | 耗时差距百分比(Reflection) | 耗时差距百分比(Code Gen) | Nino  | BinaryFormatter | 体积差距百分比 |
| --------------- | ---- | ------------ | ---- | ------------- | --------------- | -------------------------- | ------------------------ | ----- | --------------- | -------------- |
| BinaryFormatter | x    | 10           | 17   | 19            | 26              | -34.62%                    | -26.92%                  | 53    | 1165            | -95.45%        |
| BinaryFormatter | √    | 100          | 2    | 1             | 3               | -33.33%                    | -66.67%                  | 94    | 8725            | -98.92%        |
| BinaryFormatter | √    | 1000         | 19   | 9             | 30              | -36.67%                    | -70.00%                  | 254   | 84325           | -99.70%        |
| BinaryFormatter | √    | 10000        | 171  | 89            | 275             | -37.82%                    | -67.64%                  | 1826  | 840325          | -99.78%        |
| BinaryFormatter | √    | 100000       | 1756 | 850           | 3117            | -43.66%                    | -72.73%                  | 17538 | 8400325         | -99.79%        |

## 反序列化

#### **测试数据**

```csharp
using System;
using ProtoBuf;
using Nino.Serialization;

namespace Nino.Test
{
  [Serializable]
  [ProtoContract]
  [NinoSerialize]
  public partial class NestedData2
  {
    [ProtoMember(1)] [NinoMember(1)] public string name;

    [ProtoMember(2)] [NinoMember(2)] public Data[] ps;

    [ProtoMember(3)] [NinoMember(3)] public List<int> vs;
  }
}
```

将实例化NestedData2，然后对ps赋值（会测试不同长度的ps）并序列化

*第一次反序列化的时候，Nino会对类型进行缓存，达到预热效果，使得同一类型的第二次开始的序列化速度大幅度提升，Protobuf-net亦是如此*

#### [Test3](Nino/Assets/Nino/Test/Editor/Serialization/Test3.cs)，无生成代码性能对比

![img3-1](https://s1.ax1x.com/2022/06/11/XcNR9f.png)

> 可以看到，**Nino未生成代码的情况下**，Nino的反序列化速度与Protobuf-net基本持平（数据较多的情况下慢10%）
>

#### [Test3](Nino/Assets/Nino/Test/Editor/Serialization/Test3.cs)，生成代码性能对比（Unity打开工程，菜单栏，Nino/Generator/Serialization Code）

![img3-2](https://s1.ax1x.com/2022/06/11/XcNg4P.png)

> 可以看到，**Nino生成代码的情况下**，无论是否有预热，反序列化速度都是Nino更胜一筹，甚至在哪怕双方都进行了预热，Nino都能比Protobuf-net快50%

| 对比库       | 预热 | 测试数组长度 | Nino | Nino Code Gen | Protobuf-net | 耗时差距百分比(Reflection) | 耗时差距百分比(Code Gen) |
| ------------ | ---- | ------------ | ---- | ------------- | ------------ | -------------------------- | ------------------------ |
| Protobuf-net | x    | 10           | 7    | 6             | 8            | -12.50%                    | -25.00%                  |
| Protobuf-net | √    | 100          | 3    | 1             | 3            | 0.00%                      | -66.67%                  |
| Protobuf-net | √    | 1000         | 30   | 14            | 27           | 11.11%                     | -48.15%                  |
| Protobuf-net | √    | 10000        | 322  | 139           | 281          | 14.59%                     | -50.53%                  |
| Protobuf-net | √    | 100000       | 3091 | 1414          | 2826         | 9.38%                      | -49.96%                  |



#### [Test4](Nino/Assets/Nino/Test/Editor/Serialization/Test4.cs)，无生成代码性能对比

![img4-1](https://s1.ax1x.com/2022/06/11/XcaCLQ.png)

> 可以看到，**Nino未生成代码的情况下**，Nino的反序列化速度与BinaryFormatter基本持平（数据较多的情况下还能快10%），甚至在无预热的情况下要快60%

#### [Test4](Nino/Assets/Nino/Test/Editor/Serialization/Test4.cs)，生成代码性能对比（Unity打开工程，菜单栏，Nino/Generator/Serialization Code）

![img4-2](https://s1.ax1x.com/2022/06/11/XcaiZj.png)

> 可以看到，**Nino生成代码的情况下**，无论是否有预热，反序列化速度都是Nino更胜一筹，甚至在哪怕双方都进行了预热，Nino都能比BinaryFormatter快50~60%

| 对比库          | 预热 | 测试数组长度 | Nino | Nino Code Gen | BinaryFormatter | 耗时差距百分比(Reflection) | 耗时差距百分比(Code Gen) |
| --------------- | ---- | ------------ | ---- | ------------- | --------------- | -------------------------- | ------------------------ |
| BinaryFormatter | x    | 10           | 10   | 8             | 26              | -61.54%                    | -69.23%                  |
| BinaryFormatter | √    | 100          | 3    | 1             | 3               | 0.00%                      | -66.67%                  |
| BinaryFormatter | √    | 1000         | 32   | 12            | 30              | 6.67%                      | -60.00%                  |
| BinaryFormatter | √    | 10000        | 317  | 129           | 307             | 3.26%                      | -57.98%                  |
| BinaryFormatter | √    | 100000       | 3478 | 1389          | 4394            | -20.85%                    | -68.39%                  |