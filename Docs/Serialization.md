# 序列化模块使用方法

## 定义可序列化类型

- 给需要Nino序列化/反序列化的类或结构体，打上```[NinoSerialize]```标签，如果**需要自动收集全部字段和属性，则该标签内部加入个true参数**，如```[NinoSerialize(true)]```
- 如果**没有自动收集全部字段和属性**，则需要给想序列化/反序列化的字段或属性，打上```[NinoMember()]```标签，标签内部需要传入一个数字参数，即序列化和反序列化时该成员的位置，如```[NinoMember(1)]```
- 如果**开启了自动收集全部字段和属性**，且**需要略过某些字段或属性**，请将其打上```[NinoIgnore]```标签，需要注意的是，如果没开启自动收集，该标签会无效

代码示范：

```csharp
[NinoSerialize(true)]
public partial class IncludeAllClass
{
  public int a;
  public long b;
  public float c;
  public double d;

  public override string ToString()
  {
    return $"{a}, {b}, {c}, {d}";
  }
}

[NinoSerialize]
public partial class NotIncludeAllClass
{
  [NinoMember(1)]
  public int a;
  [NinoMember(2)]
  public long b;
  [NinoMember(3)]
  public float c;
  [NinoMember(4)]
  public double d;

  public override string ToString()
  {
    return $"{a}, {b}, {c}, {d}";
  }
}
```

> 建议每个需要Nino序列化/反序列化的类和结构体用partial定义，这样可以生成代码
>
> 推荐```NotIncludeAllClass```的写法，给每个字段或属性单独打```NinoMember```标签，这样性能最好，体积最小
>
> **```IncludeAllClass```的写法（自动收集），会导致生成出来的体积较大，序列化反序列化速度慢，但是可以通过生成代码优化**
>
> **自动收集的类型或结构体，强烈建议通过生成代码来提高性能，以及优化体积，但是需要注意，每次更新字段或属性后需要重新生成代码来更新**



## 支持类型

支持序列化的成员类型（底层自带支持）：

- byte, sbyte, short, ushort, int, uint, long, ulong, double, float, decimal, char, string, bool, enum
- List<上述类型>，上述类型[]
- List<可Nino序列化类型>，可Nino序列化类型[]
- List<注册委托类型>，注册委托类型[]
- Dictionary<Nino支持类型,Nino支持类型>
- Dictionary<注册委托类型,注册委托类型>
- 可Nino序列化类型

不支持序列化的成员类型（可以通过注册自定义委托实现）：

- 任何非上述类型（HashSet, Nullable, DateTime, Vector3等）

**针对某个类型注册自定义序列化委托后，记得注册该类型的自定义反序列化委托，不然会导致反序列化出错**



## 限制

- 不支持给非partial或Nested类型和结构体生成代码
- 不支持在继承的情况下，序列化父类的可序列化成员
- 暂时不支持给泛型序列化类型和结构体生成代码



## ILRuntime

Nino支持ILRuntime的使用，但需要初始化ILRuntime：

1. 把ILRuntime导入Unity工程，并对ILRuntime目录生成assembly definition文件

2. 进入Nino_Unity/Assets/Nino/Serialization，找到```Nino.Serialization.asmdef```，选中后在Inspector上添加对ILRuntime的assembly definition的引用，并apply变动

3. 在PlayerSetting里Symbol区域添加```ILRuntime```（如果Symbol一栏不是空的，记得两个标签之前要用```;```隔开）

4. 调用ILRuntime解析工具

   ```csharp
   Nino.Serialization.ILRuntimeResolver.RegisterILRuntimeClrRedirection(domain);
   ```

   domain是ILRuntime的AppDomain，该方法应该在domain.LoadAssembly后，进入热更代码前调用，参考Test11

5. 热更工程引用Library/ScriptAssemblies/Nino.Serialization.dll和Library/ScriptAssemblies/Nino.Shared.dll

6. 如果需要给热更工程生成代码，打开```Nino_Unity/Assets/Nino/Editor/SerializationHelper.cs```，修改```ExternalDLLPath```字段内的```Assets/Nino/Test/Editor/Serialization/Test11.bytes```，变为你的热更工程的DLL文件的路径，记得带后缀，修改生效后，在菜单栏点击```Nino/Generator/Serialization Code```即可给热更工程的Nino序列化类型生成代码

7. 生成热更工程的代码后，需要把生成的热更序列化类型的代码从Unity工程移到热更工程，并且在Unity工程删掉会报错的热更类型代码

> 如果用的是assembly definition来生成热更库的，需要把生成的热更代码放到assembly definition的目录内，把外部会报错的代码挪进去就好



## 注册序列化委托

给指定类型注册该委托后，全局序列化的时候遇到该类型会直接使用委托方法写入二进制数据

需要注意的是，不支持注册底层自带支持的类型的委托

使用方法：

```csharp
Serializer.AddCustomImporter<T>((val, writer) =>
                                                  {
                                                    //TODO use writer to write
                                                  });
```

T是需要注册的类型的泛型参数，val是T的实例，writer是用来写二进制的工具

示例：

```csharp
Serializer.AddCustomImporter<UnityEngine.Vector3>((val, writer) =>
                                                  {
                                                    //write 3 float
                                                    writer.Write(val.x);
                                                    writer.Write(val.y);
                                                    writer.Write(val.z);
                                                  });
```

这里我们写了个Vector3，将其x,y,z以float的方式写入



## 注册反序列化委托

给指定类型注册该委托后，全局翻序列化的时候遇到该类型会直接使用委托方法读取二进制数据并转为对象

需要注意的是，不支持注册底层自带支持的类型的委托

使用方法：

```csharp
Deserializer.AddCustomExporter<T>(reader =>
                                  //TODO return T instance
                                 );
```

T是需要注册的类型的泛型参数，reader是用来读二进制的工具

示例：

```csharp
Deserializer.AddCustomExporter<UnityEngine.Vector3>(reader =>
                                                    new UnityEngine.Vector3(reader.ReadFloat(), reader.ReadFloat(), reader.ReadFloat()));
```

这里我们读了3个float作为xyz（因为写入的时候写3个float，xyz），创建了Vector3并返回



## 代码生成

不生成代码也不会影响使用，但是生成后性能可以翻倍

- Unity下直接在菜单栏点击```Nino/Generator/Serialization Code```即可，代码会生成到```Assets/Nino/Generated```，也可以打开```Assets/Nino/Editor/SerializationHelper.cs```并修改内部的```ExportPath```参数
- 非Unity下调用```CodeGenerator.GenerateSerializationCodeForAllTypePossible```接口即可

> 不想生成代码的类或结构体可以打```[CodeGenIgnore]```标签到该类或结构体上，可以在性能对比的时候用这个（例如[这个真机测试](../Nino_Unity/Assets/Nino/Test/BuildTest.cs)）

## 序列化

```csharp
Nino.Serialization.Serializer.Serialize<T>(T val);
```

```csharp
Nino.Serialization.Serializer.Serialize<T>(T val, Encoding encoding);
```

> 同时如果没有指定的编码的话，会使用UTF8
>
> 需要注意的是，涉及到字符串时，请确保序列化和反序列化的时候用的是同样的编码

示范：

```csharp
byte[] byteArr = Nino.Serialization.Serializer.Serialize<ObjClass>(obj);
```

传入需要序列化的类型作为泛型参数，以及该类型的实例，会返回二进制数组

## 反序列化

```csharp
Nino.Serialization.Deserializer.Deserialize<T>(byte[] data);
```

> 需要注意编码问题，并且反序列化的对象需要能够通过new()创建（即包含无参数构造函数）

```csharp
var obj = Nino.Serialization.Deserializer.Deserialize<ObjClass>(byteArr);
```

传入需要反序列化的类型作为泛型参数，以及序列化结果的二进制数组，会返回反序列化出的对象实例



