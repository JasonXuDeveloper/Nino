# Nino
Useful Unity Modules | 实用的Unity模块


## 功能列表

- 序列化模块，[使用教程](Docs/Serialization.md)
  > Protobuf-net/JSON平替方案，目标是更小体积，更高性能
  >
  > **注意**，该模块的序列化数据，仅支持在C#平台使用该库进行序列化和反序列化，无法跨语言使用
  - 序列化【2022.05.30完成】
    - 优化GC【2022.06.04完成】
    - 优化体积【2022.06.04已完成】
    - 代码生成【2022.06.04已完成】
    - 自定义序列化委托注册【2022.06.11完成】
  - 反序列化【2022.06.10完成】
  
    - 优化GC【2022.06.10完成】
    - 代码生成【2022.06.11完成】
    - 自定义反序列化委托注册【2022.06.11完成】
  - 测试案例
    - [Test1](Nino/Assets/Nino/Test/Editor/Serialization/Test1.cs) Nino VS Protobuf-net 序列化
    - [Test2](Nino/Assets/Nino/Test/Editor/Serialization/Test2.cs) Nino VS BinaryFormatter 序列化
    - [Test3](Nino/Assets/Nino/Test/Editor/Serialization/Test3.cs) Nino VS Protobuf-net 反序列化
    - [Test4](Nino/Assets/Nino/Test/Editor/Serialization/Test4.cs) Nino VS BinaryFormatter 反序列化
    - [Test5](Nino/Assets/Nino/Test/Editor/Serialization/Test5.cs) Nino VS MongoDB.Bson 序列化以及反序列化
    - [Test6](Nino/Assets/Nino/Test/Editor/Serialization/Test6.cs) Nino VS MsgPack 序列化以及反序列化
    - [Test7](Nino/Assets/Nino/Test/Editor/Serialization/Test7.cs) 自定义Nino序列化反序列化委托
    - [Test8](Nino/Assets/Nino/Test/Editor/Serialization/Test8.cs) 自动收集全部字段进行序列化/反序列化（无需给单个字段或属性打标签）
  - [性能报告](Performance/Serialization.md)
  - 可删除目录：
    - Nino/Nino/Assets/Nino/Test，测试代码
    - Nino/Nino/Asset/ThirdParty，测试用的第三方库
