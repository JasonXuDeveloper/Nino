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
    
      [Test1](Nino/Assets/Nino/Test/Editor/Serialization/Test1.cs)，无生成代码
    
      | 测试库            | 对比库       | 预热 | 测试对象数量 | 序列化耗时<br />(测试库:对比库，ms) | 序列化结果体积<br />(测试库:对比库，bytes) |
      | ----------------- | ------------ | ---- | ------------ | ----------------------------------- | ------------------------------------------ |
      | Nino (Reflection) | Protobuf-net | 否   | 10           | 15:43<br />（Nino快65.12%）         | 71:638<br />（Nino小88.87%）               |
      | Nino (Reflection) | Protobuf-net | 是   | 100          | 2:2<br />（两者速度一致）           | 112:6308<br />（Nino小98.22%）             |
      | Nino (Reflection) | Protobuf-net | 是   | 1000         | 17:17<br />（两者速度一致）         | 272:63008<br />（Nino小99.57%）            |
      | Nino (Reflection) | Protobuf-net | 是   | 10000        | 173:177<br />（Nino快2.26%）        | 1844:630008<br />（Nino小99.71%）          |
      | Nino (Reflection) | Protobuf-net | 是   | 100000       | 1799:1800<br />（Nino快0.06%）      | 17556:6300008<br />（Nino小99.72%）        |
    
      > 可以看到，**Nino未生成代码的情况下**，如果Nino和Protobuf-net都没预热（指第一次序列化一个类型或结构），Nino序列化速度更快
      >
      > 如果双方都预热了，在未生成代码的情况下，Nino的序列化速度与Protobuf-net持平
      >
      > 但是，无论何时，Nino序列化的体积都比Protobuf-net的小很多很多很多
    
      [Test1](Nino/Assets/Nino/Test/Editor/Serialization/Test1.cs)，生成代码（Unity打开工程，菜单栏，Nino/Generator/Serialization Code）
    
      | 测试库          | 对比库       | 预热 | 测试对象数量 | 序列化耗时<br />(测试库:对比库，ms) | 序列化结果体积<br />(测试库:对比库，bytes) |
      | --------------- | ------------ | ---- | ------------ | ----------------------------------- | ------------------------------------------ |
      | Nino (Code Gen) | Protobuf-net | 否   | 10           | 15:43<br />（Nino快65.12%）         | 71:638<br />（Nino小88.87%）               |
      | Nino (Code Gen) | Protobuf-net | 是   | 100          | 1:2<br />（Nino快50%）              | 112:6308<br />（Nino小98.22%）             |
      | Nino (Code Gen) | Protobuf-net | 是   | 1000         | 10:17<br />（Nino快41.18%）         | 272:63008<br />（Nino小99.57%）            |
      | Nino (Code Gen) | Protobuf-net | 是   | 10000        | 92:177<br />（Nino快46.20%）        | 1844:630008<br />（Nino小99.71%）          |
      | Nino (Code Gen) | Protobuf-net | 是   | 100000       | 862:1800<br />（Nino快49.53%）      | 17556:6300008<br />（Nino小99.72%）        |
    
      > 可以看到，**Nino生成代码的情况下**，无论是否有预热，序列化速度都是Nino更胜一筹，甚至在哪怕双方都进行了预热，Nino都能比Protobuf-net快50%
      >
      > 在快的同时，Nino还大幅度的缩小了体积，保证了又快又稳
    
  - 反序列化【预计2022年6月完成】
  

