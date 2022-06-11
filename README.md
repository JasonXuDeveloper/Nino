# Nino
Useful Unity Modules | 实用的Unity模块


## 功能列表

- 序列化模块
  > Protobuf-net/JSON平替方案，目标是更小体积，更高性能
  >
  > - 需要序列化的类或结构体要打```[NinoSerialize]```标签
  >
  > - 需要序列化的字段或属性要打```[NinoMember(1)]```标签，内部参数是序列化的顺序，该数字不能重复
  >
  > **注意**，该模块的序列化数据，仅支持在C#平台使用该库进行反序列化，无法跨平台使用
  >
  > **建议给需要Nino序列化的类或结构加上partial修饰符，否则无法生成代码（生成代码能使性能翻倍）**
  >
  > 支持序列化的成员类型（底层自带支持）：
  >
  > - byte, sbyte, short, ushort, int, uint, long, ulong, double, float, decimal, char, string, bool, enum
  > - List<上述类型>，上述类型[]
  > - List<可Nino序列化类型>，可Nino序列化类型[]
  > - List<注册委托类型>，注册委托类型[]
  > - Dictionary<Nino支持类型,Nino支持类型>
  > - Dictionary<注册委托类型,注册委托类型>
  > - 可Nino序列化类型（代表可以嵌套）
  >
  > 不支持序列化的成员类型（可以通过注册自定义委托实现）：
  >
  > - 任何非上述类型（Nullable, DateTime，Vector3等）
  >
  > **针对某个类型注册自定义序列化委托后，记得注册该类型的自定义反序列化委托，不然会导致反序列化出错**
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
    - [Test5](Nino/Assets/Nino/Test/Editor/Serialization/Test5.cs) 自定义Nino序列化反序列化委托
  - [性能报告](Performance/Serialization.md)
