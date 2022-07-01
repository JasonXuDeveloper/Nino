# Nino
Definatly useful and high performance modules for C# projects, especially for Unity.

实用的高性能C#模块，尤其在Unity平台能带来令人难以置信的效益。

![build](https://img.shields.io/github/workflow/status/JasonXuDeveloper/Nino/.NET?logo=github)![license](https://img.shields.io/github/license/JasonXuDeveloper/Nino)






## 功能列表

- 共享模块 [![nino.shared.nuget](https://img.shields.io/nuget/v/Nino.Shared?label=Nino.Shared)](https://www.nuget.org/packages/Nino.Shared/)
  
  - IO
    - 高性能数据压缩流
    - 数组对象池（线程安全）
    - 基础对象池（线程安全）
    - 二进制流对象池（线程安全）
    - 高性能动态扩容Buffer（易用、高效，低GC）
    - 可动态修改Buffer流（包含不需要分配io_buffer去read/write的方法）
  - Collections
    - 无检查List
    - 无检查Stack
  - Mgr
    - 压缩解压助手
  
- 序列化模块，[使用教程](Docs/Serialization.md) [![nino.serialization.nuget](https://img.shields.io/nuget/v/Nino.Serialization?label=Nino.Serialization)](https://www.nuget.org/packages/Nino.Serialization)

  > Protobuf-net/MsgPack/BinaryFormatter/Bson/JSON等序列化库的平替方案，优势是更小体积，更高性能
  >
  > **注意**，该模块的序列化数据，仅支持在C#平台使用该库进行序列化和反序列化，无法跨语言使用
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
      >
      > MsgPack在IL2CPP下不生成代码无法使用
    
  - [性能报告](Performance/Serialization.md)
  
  - RoadMap
  
    - IL Emit -> 非IL2CPP环境
    - ExpressionTree -> 非IL2CPP环境
    - native zstream to zlib -> 全局
    - analyser -> 全局



## 目录结构

- Docs，文档
- src，Nino源码
- Nino_Unity，Nino Unity 2019及以上版本的工程，包含源码和测试代码
- Performance，性能报告



## 在Unity平台使用

有两种方法：

- 直接下载本工程，并用Unity打开Nino目录进行开发

- 将```Nino_Unity/Assets/Nino```复制到自己的Unity项目即可，如果不需要测试案例的话，可以不包含```Nino/Nino/Assets/Nino/Test```

  > 如果需要运行测试案例，记得也需要复制```Nino_Unity/Asset/ThirdParty```到Unity项目



## 在非Unity平台使用

- 使用NuGet

  NuGet里搜Nino就好，目前有```Nino.Shared```和```Nino.Serialization```

  ```bash
  PM> Install-Package Nino.Shared -Version 1.0.2
  ```

  ```bash
  PM> Install-Package Nino.Serialization -Version 1.0.4
  ```

- 使用src下的代码（复制进自己项目即可）

