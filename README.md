# Nino

Definite useful and high performance serialisation library for C# projects, especially for Unity. 

实用的高性能C#序列化库，尤其在Unity平台能带来令人难以置信的效益。

![build](https://img.shields.io/github/actions/workflow/status/JasonXuDeveloper/Nino/.github/workflows/dotnet.yml?branch=main)![license](https://img.shields.io/github/license/JasonXuDeveloper/Nino)

## 功能列表

[使用教程](Docs/Serialization.md) [![nino.nuget](https://img.shields.io/nuget/v/Nino?label=Nino)](https://www.nuget.org/packages/Nino)

> Protobuf-net/MsgPack/BinaryFormatter/Bson/JSON等序列化库的平替方案，优势是更小体积，更高性能，支持多线程，支持多态
> 
> **注意**，该模块的序列化数据，仅支持在C#平台使用该库进行序列化和反序列化，无法跨语言使用
> 
> ```Nino.Serialization v2.0.0```与所有**1.x**版本都**不兼容**，详细请查看使用教程

- 支持**全部非托管**类型（int/float/datetime/vector/etc)

- 支持任意**Nullable**类型

- 支持任意**ICollection**类型（list/dictonary/hashset/etc）

- 支持任意**Span**类型

- 支持**自定义Nino序列化类型**

- 支持**嵌套**上述类型（dictionary<int, list<自定义nino类型[]>>）

- 支持**多态**

- 支持**数据校验**

- 性能高，GC低！

- 测试案例
  
  - [Test1](Nino_Unity/Assets/Nino/Test/Editor/Serialization/Test1.cs) Nino VS Protobuf-net 序列化
  
  - [Test2](Nino_Unity/Assets/Nino/Test/Editor/Serialization/Test2.cs) Nino VS BinaryFormatter 序列化
  
  - [Test3](Nino_Unity/Assets/Nino/Test/Editor/Serialization/Test3.cs) Nino VS Protobuf-net 反序列化
  
  - [Test4](Nino_Unity/Assets/Nino/Test/Editor/Serialization/Test4.cs) Nino VS BinaryFormatter 反序列化
  
  - [Test5](Nino_Unity/Assets/Nino/Test/Editor/Serialization/Test5.cs) Nino VS MongoDB.Bson 序列化以及反序列化
  
  - [Test6](Nino_Unity/Assets/Nino/Test/Editor/Serialization/Test6.cs) Nino VS MsgPack 序列化以及反序列化
  
  - [Test7](Nino_Unity/Assets/Nino/Test/Editor/Serialization/Test7.cs) 自定义Nino序列化反序列化委托
  
  - [Test8](Nino_Unity/Assets/Nino/Test/Editor/Serialization/Test8.cs) 自动收集全部字段进行序列化/反序列化（无需给单个字段或属性打标签）
  
  - [Test9](Nino_Unity/Assets/Nino/Test/Editor/Serialization/Test9.cs) 基础类型序列化反序列化
  
  - [Test10](Nino_Unity/Assets/Nino/Test/Editor/Serialization/Test10.cs) 复杂结构类型序列化反序列化
  
  - [Test11](Nino_Unity/Assets/Nino/Test/Editor/Serialization/Test11.cs) ILRuntime测试（需要搭配使用教程启用ILRuntime）
  
  - [真机测试](Nino_Unity/Assets/Nino/Test/BuildTest.cs) 可以打IL2CPP或Mono包进行测试（对比了Nino Reflection/Code Gen与Protobuf-net/BinaryFormatter/Bson/MsgPack Code Gen的序列化性能、序列化体积、反序列化性能）
    
    > Protobuf-net与Bson在IL2CPP下暂不支持字典序列化

- [性能报告](Performance/Serialization.md)

## 目录结构

- Docs，文档
- src，Nino源码
- Nino_Unity，Nino Unity 2022.3及以上版本的工程，包含源码和测试代码
- Performance，性能报告
- Nino.unitypackage，Unity包

## 在Unity平台使用

参考[使用教程](Docs/Serialization.md)

## 在非Unity平台使用

- 使用NuGet
  
  NuGet里搜```Nino```
  
  ```bash
  PM> Install-Package Nino -Version 2.0.2
  ```
