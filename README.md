# Nino
Useful Unity Modules | 实用的Unity模块


## 功能列表

- 序列化模块
  > Protobuf-net平替方案，目标是更小体积，更高性能
  >
  > **注意**，该模块的序列化数据，仅支持在C#平台使用该库进行反序列化，无法跨平台使用
  - 序列化【2022.05.30完成】
    - 优化GC【2022.06.04完成】
    
    - 优化体积【2022.06.04已完成】
    
    - 代码生成【2022.06.04已完成】
    
    - 自定义序列化委托注册【预计2022年6月完成】
    
    - 性能对比
    
      测试数据
    
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
      
      [Test1](Nino/Assets/Nino/Test/Editor/Serialization/Test1.cs)，无生成代码性能对比
      
      ![img1](https://s1.ax1x.com/2022/06/05/Xd24C6.png)
      
      > 可以看到，**Nino未生成代码的情况下**，如果Nino和Protobuf-net都没预热，Nino序列化速度更快
      >
      > 如果双方都预热了，在未生成代码的情况下，Nino的序列化速度与Protobuf-net持平
      >
      
      [Test1](Nino/Assets/Nino/Test/Editor/Serialization/Test1.cs)，生成代码性能对比（Unity打开工程，菜单栏，Nino/Generator/Serialization Code）
      
      ![img1-2](https://s1.ax1x.com/2022/06/05/XdWpJx.png)
      
      > 可以看到，**Nino生成代码的情况下**，无论是否有预热，序列化速度都是Nino更胜一筹，甚至在哪怕双方都进行了预热，Nino都能比Protobuf-net快50%
      
      [Test1](Nino/Assets/Nino/Test/Editor/Serialization/Test1.cs)，序列化数据大小对比
      
      ![img1-3](https://s1.ax1x.com/2022/06/05/XdRWLQ.png)
      
      > 显而易见，Nino序列化数据的大小碾压Protobuf-net
      
      | Nino序列化方式 | 对比库       | 预热 | 测试数组长度 | Nino速度 | Nino Code Gen速度 | Protobuf-net速度 | 耗时差距百分比(Reflection) | 耗时差距百分比(Code Gen) | Nino体积 | Protobuf-net体积 | 体积差距百分比 |
      | -------------- | ------------ | ---- | ------------ | -------- | ----------------- | ---------------- | -------------------------- | ------------------------ | -------- | ---------------- | -------------- |
      | Reflection     | Protobuf-net | x    | 10           | 17       | 15                | 45               | -62.22%                    | -66.67%                  | 71       | 638              | -88.87%        |
      | Reflection     | Protobuf-net | √    | 100          | 2        | 0                 | 2                | 0.00%                      | -100.00%                 | 112      | 6308             | -98.22%        |
      | Reflection     | Protobuf-net | √    | 1000         | 17       | 9                 | 16               | 6.25%                      | -43.75%                  | 272      | 63008            | -99.57%        |
      | Reflection     | Protobuf-net | √    | 10000        | 164      | 94                | 181              | -9.39%                     | -48.07%                  | 1844     | 630008           | -99.71%        |
      | Reflection     | Protobuf-net | √    | 100000       | 1651     | 935               | 1667             | -0.96%                     | -43.91%                  | 17556    | 6300008          | -99.72%        |
      
      [Test2](Nino/Assets/Nino/Test/Editor/Serialization/Test2.cs)，无生成代码性能对比
      
      ![img2](https://s1.ax1x.com/2022/06/05/XdRUMD.png)
      
      > 可以看到，**Nino未生成代码的情况下**，Nino序列化速度更快（基本快三分之一）
      >
      
      [Test2](Nino/Assets/Nino/Test/Editor/Serialization/Test2.cs)，生成代码性能对比（Unity打开工程，菜单栏，Nino/Generator/Serialization Code）
      
      ![img2-2](https://s1.ax1x.com/2022/06/05/XdWVwd.png)
      
      > 可以看到，**Nino生成代码的情况下**，无论是否有预热，序列化速度都是Nino更胜一筹，甚至在进行了预热后，Nino能比BinaryFormatter快70%
      
      [Test2](Nino/Assets/Nino/Test/Editor/Serialization/Test2.cs)，序列化数据大小对比
      
      ![img2-3](https://s1.ax1x.com/2022/06/05/XdRTJ0.png)
      
      > 显而易见，Nino序列化数据的大小碾压BinaryFormatter
      
      | Nino序列化方式 | 对比库          | 预热 | 测试数组长度 | Nino速度 | Nino Code Gen速度 | BinaryFormatter速度 | 耗时差距百分比(Reflection) | 耗时差距百分比(Code Gen) | Nino体积 | BinaryFormatter体积 | 体积差距百分比 |
      | -------------- | --------------- | ---- | ------------ | -------- | ----------------- | ------------------- | -------------------------- | ------------------------ | -------- | ------------------- | -------------- |
      | Reflection     | BinaryFormatter | x    | 10           | 17       | 19                | 26                  | -34.62%                    | -26.92%                  | 71       | 1165                | -93.91%        |
      | Reflection     | BinaryFormatter | √    | 100          | 2        | 1                 | 3                   | -33.33%                    | -66.67%                  | 112      | 8725                | -98.72%        |
      | Reflection     | BinaryFormatter | √    | 1000         | 19       | 9                 | 30                  | -36.67%                    | -70.00%                  | 272      | 84325               | -99.68%        |
      | Reflection     | BinaryFormatter | √    | 10000        | 171      | 89                | 275                 | -37.82%                    | -67.64%                  | 1844     | 840325              | -99.78%        |
      | Reflection     | BinaryFormatter | √    | 100000       | 1756     | 850               | 3117                | -43.66%                    | -72.73%                  | 17556    | 8400325             | -99.79%        |
    
  - 反序列化【预计2022年6月完成】
  

