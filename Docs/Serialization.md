# 序列化模块使用方法

## 非Unity平台

非Unity平台使用Nino，可以**根据需求开启**原生压缩解压代码，**开启后序列化和反序列化时的GC会变得非常的低（KB级别甚至Bytes级别）**

只需要使用前设置```Nino.Shared.Mgr.ConstMgr.EnableNativeDeflate = true;```即可

**使用该功能需要编译C++代码**，如果使用时报错DLLNotFound（因为Nino只自带了x64的Mac下的dylib以及x64的windows的dll），需要用CMake编译一下```Native/deflate```内的C++代码即可，编译出来的```dll```、```so```或```dylib```文件放入```Nino.Shared```目录内即可，记得在IDE内配置一下编译时复制该文件

> 在非Mac平台下编译原生DLL，可能需要手动修改CMake文件，使其在make的时候包含zlib代码（Cmake文件内有注释）
>
> 在M1的Mac平台下可能需要重新编译DLL，用cmake编译出libDeflate.dylib后放入指定目录即可
>
> 在Windows平台下编译能用的原生DLL有些难度，建议有经验的用户再去使用，编译出来的Deflate.dll放入指定目录即可
>
> 在某些Linux平台下编译原生DLL，可能需要定义一些东西（比如z_size_t），具体参考```Native/deflate/library.h```内的注释，编译出来的libDeflate.so放入指定目录即可

注意，nuget下载nino的用户需要把编译的dll放到项目根目录，并且配置生成项目时自动复制

## 注意事项

```Nino.Serialization v1.1.0```与其之前的**所有版本**都**不兼容**，升级Nino后需要用新版```Writer/Serializer```  **重新导出** 一份数据，才能被最新版的```Reader/Deserializer```正常解析！！！

> 这个版本开始支持了null对象，所以二进制格式有变化





```Nino.Serialization v1.0.21```与其之前的**所有版本**都**不兼容**，升级Nino后需要用新版```Writer/Serializer```  **重新导出** 一份数据，才能被最新版的```Reader/Deserializer```正常解析！！！（```v1.0.21```有个Log忘删了，所以补发了```v1.0.21.2```）

从这个版本开始，```序列化```和```反序列化```时不再需要提供```Encoding```参数！！！

> 出现以上变更的原因是：从这个版本开始，1）字符串直接用C#底层的Utf16编码，直接把```byte*```与字符串的```char*```互转；2）压缩数据时，正整数统一采用无符号类型压缩，负整数统一采用有符号类型压缩，这样可以直接在读取时对读取字段的指针赋值为对应原数据（负整数额外在有符号类型的二进制内进行```memset```填充255（```11111111```）来解决符号问题



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
> **```IncludeAllClass```的写法（自动收集），会导致生成出来的体积较大，序列化反序列化速度慢，但是可以通过生成代码优化**（非ILRuntime下）
>
> **自动收集的类型或结构体，强烈建议通过生成代码来提高性能，以及优化体积，但是需要注意，每次更新字段或属性后需要重新生成代码来更新**



## 支持类型

支持序列化的成员类型（底层自带支持）：

- byte, sbyte, short, ushort, int, uint, long, ulong, double, float, decimal, char, string, bool, enum, DateTime
- Nullable<任意支持Nino序列化的struct>
- List<上述类型>，HashSet<上述类型>，Queue<上述类型>，Stack<上述类型>，上述类型[]
- List<可Nino序列化类型>，HashSet<可Nino序列化类型>，Queue<可Nino序列化类型>，Stack<可Nino序列化类型>，可Nino序列化类型[]
- List<注册委托类型>，HashSet<注册委托类型>，Queue<注册委托类型>，Stack<注册委托类型>，注册委托类型[]
- Dictionary<Nino支持类型,Nino支持类型>
- Dictionary<注册委托类型,注册委托类型>
- 可Nino序列化类型
- null

不支持序列化的成员类型（可以通过注册自定义委托实现）：

- 任何非上述类型（ConcurrentQueue等）

**针对某个类型注册自定义序列化委托后，记得注册该类型的自定义反序列化委托，不然会导致反序列化出错**



## 限制

- 不支持给非partial或Nested类型和结构体生成代码
- 暂时不支持给泛型序列化类型和结构体生成代码



## ILRuntime

Nino支持ILRuntime的使用，但需要初始化ILRuntime：

1. 把ILRuntime导入Unity工程，并对ILRuntime目录生成assembly definition文件

2. 进入Nino_Unity/Assets/Nino/Serialization，找到```Nino.Serialization.asmdef```，选中后在Inspector上添加对ILRuntime的assembly definition的引用，并apply变动

3. 在PlayerSetting里Symbol区域添加```ILRuntime```（如果Symbol一栏不是空的，记得两个标签之间要用```;```隔开）

4. 调用ILRuntime解析工具

   ```csharp
   Nino.Serialization.ILRuntimeResolver.RegisterILRuntimeClrRedirection(domain);
   ```

   domain是ILRuntime的AppDomain，该方法应该在domain.LoadAssembly后，进入热更代码前调用，参考Test11

5. 热更工程引用Library/ScriptAssemblies/Nino.Serialization.dll和Library/ScriptAssemblies/Nino.Shared.dll

6. ILRuntime下**非常不建议**给热更工程的结构体生成静态性能优化代码，**因为原理限制会产生负优化！！！**，如果执意要生产代码，请参考后续步骤，反之可以忽略后面的步骤

7. 如果需要给热更工程生成代码，打开```Nino_Unity/Assets/Nino/Editor/SerializationHelper.cs```，修改```ExternalDLLPath```字段内的```Assets/Nino/Test/Editor/Serialization/Test11.bytes```，变为你的热更工程的DLL文件的路径，记得带后缀，修改生效后，在菜单栏点击```Nino/Generator/Serialization Code```即可给热更工程的Nino序列化类型生成代码

8. 生成热更工程的代码后，需要把生成的热更序列化类型的代码从Unity工程移到热更工程，并且在Unity工程删掉会报错的热更类型代码

> 如果用的是assembly definition来生成热更库的，需要把生成的热更代码放到assembly definition的目录内，把外部会报错的代码挪进去就好
>
> 需要注意的是，ILRuntime下生成与不生成代码的差距不是特别大



## 注册自定义包装器

包装器是Nino提供的一个基类，内部需要实现序列化和反序列化，实现后，全局给该类型序列化或反序列化时，会调用这里的代码

需要注意的是，不需要同时注册自定义包装器和自定义委托，如果同时注册了这两个东西，只有最后注册的那个会生效

使用方法：

- 创建一个类型，并继承```NinoWrapperBase<T>```，T是你需要序列化/反序列化的类型，也可以用泛型代替，可以自由发挥
- 实现```public override void Serialize(T val, Writer writer)```，自行根据```Nino.Serialization.Writer```的公共接口来序列化T类型的数据即可
- 实现```public override T Deserialize(Reader reader)```，自行根据```Nino.Serialization.Reader```的公共接口来实现反序列化T类型的数据即可
- 调用```WrapperManifest.AddWrapper(typeof(T), new NinoWrapperBase<T>());```以注册接口包装器

示例：

```csharp
internal class DateTimeListWrapper : NinoWrapperBase<List<DateTime>>
{
  public override void Serialize(List<DateTime> val, Writer writer)
  {
    writer.CompressAndWrite(val.Count);
    foreach (var v in val)
    {
      writer.Write(v);
    }
  }

  public override List<DateTime> Deserialize(Reader reader)
  {
    int len = reader.ReadLength();
    var arr = new List<DateTime>(len);
    int i = 0;
    while (i++ < len)
    {
      arr.Add(reader.ReadDateTime());
    }
    return arr;
  }
}

//别忘了在某个地方调用下面的代码：
WrapperManifest.AddWrapper(typeof(DateTimeListWrapper), new DateTimeListWrapper());
```



## 注册自定义序列化委托

给指定类型注册该委托后，全局序列化的时候遇到该类型会直接使用委托方法写入二进制数据

需要注意的是，不支持注册底层自带支持的类型的委托，**并且注册委托的方式处理值类型会产生GC和装箱开销，可以通过注册自定义包装器避免这个问题**

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

> 写入(U)Int/(U)Long可以用Write(U)Int32/Write(U)Int64，但是建议用CompressAndWrite接口，可以有效压缩体积
>
> 写入Enum也要使用对应压缩接口，需要声明enum对应的数值类型，并且给enum的值转为ulong，例如writer.CompressAndWriteEnum(typeof(System.Byte), (ulong) value.En);

## 注册自定义反序列化委托

给指定类型注册该委托后，全局翻序列化的时候遇到该类型会直接使用委托方法读取二进制数据并转为对象

需要注意的是，不支持注册底层自带支持的类型的委托，**并且注册委托的方式处理值类型会产生GC和拆箱开销，可以通过注册自定义包装器避免这个问题**

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

>如果写入(U)Int/(U)Long时用了压缩接口，那么读的时候要用DecompressAndReadNumber接口读取，并且需要转换类型，如(int)reader.DecompressAndReadNumber()
>
>读取Enum要用DecompressAndReadEnum(enum声明类型)，enum默认是int类型的

## 代码生成

不生成代码也不会影响使用，但是生成后性能快很多很多很多（ILRuntime反而会慢很多很多，因为原理问题）

- Unity下直接在菜单栏点击```Nino/Generator/Serialization Code```即可，代码会生成到```Assets/Nino/Generated```，也可以打开```Assets/Nino/Editor/SerializationHelper.cs```并修改内部的```ExportPath```参数
- 非Unity下调用```CodeGenerator.GenerateSerializationCodeForAllTypePossible```接口即可
- **如果开启了自动收集字段和属性，生成代码和没生成代码，序列化的结果是不一样的，因此在导表读表的使用场景里，如果使用了自动收集，那么在生成代码后需要重新再导出一次文件（强烈建议不要开启自动收集，建议手动标记顺序）**

> 不想生成代码的类或结构体可以打```[CodeGenIgnore]```标签到该类或结构体上，可以在性能对比的时候用这个（例如[这个真机测试](../Nino_Unity/Assets/Nino/Test/BuildTest.cs)）



## 压缩方式

Nino支持以下三种压缩方式：

- Zlib(高压缩率低性能)
- Lz4(平均压缩率高性能)【正在开发】
- 无压缩(高性能但体积很大)



> 序列化和反序列化的时候可以选择压缩方式，但是需要注意反序列化数据的时候，需要用和序列化时相同的压缩方式去反序列化



## 序列化

```csharp
Nino.Serialization.Serializer.Serialize<T>(T val, CompressOption option = CompressOption.Zlib);
```

```csharp
Nino.Serialization.Serializer.Serialize(object val, CompressOption option = CompressOption.Zlib);
```



> 如果没有指定的压缩模式，会使用Zlib
>
> 需要注意的是，涉及到字符串时，请确保序列化和反序列化的时候用的是同样的编码和同样的压缩方式
>
> 老版本（1.1.0以下），需要指定Encoding参数，默认是UTF8

示范：

```csharp
byte[] byteArr = Nino.Serialization.Serializer.Serialize<ObjClass>(obj);
```

传入需要序列化的类型作为泛型参数，以及该类型的实例，会返回二进制数组

## 反序列化

```csharp
Nino.Serialization.Deserializer.Deserialize<T>(byte[] data, CompressOption option = CompressOption.Zlib);
Nino.Serialization.Deserializer.DeserializeArray<T>(byte[] data, CompressOption option = CompressOption.Zlib);
Nino.Serialization.Deserializer.DeserializeNullable<T>(byte[] data, CompressOption option = CompressOption.Zlib);
Nino.Serialization.Deserializer.DeserializeList<T>(byte[] data, CompressOption option = CompressOption.Zlib);
Nino.Serialization.Deserializer.DeserializeHashSet<T>(byte[] data, CompressOption option = CompressOption.Zlib);
Nino.Serialization.Deserializer.DeserializeQueue<T>(byte[] data, CompressOption option = CompressOption.Zlib);
Nino.Serialization.Deserializer.DeserializeStack<T>(byte[] data, CompressOption option = CompressOption.Zlib);
```

```csharp
Nino.Serialization.Deserializer.Deserialize(Type type, byte[] data, CompressOption option = CompressOption.Zlib);
```



> data可以传```byte[]```或```ArraySegment<byte>```或```Span<byte>```
>
> 如果没有指定的压缩模式，会使用Zlib
>
> 需要注意压缩模式问题，并且反序列化的对象需要能够创建（包含无参数的构造函数）
>
> 老版本（1.1.0以下），需要指定Encoding参数，默认是UTF8

示范：

```csharp
var obj = Nino.Serialization.Deserializer.Deserialize<ObjClass>(byteArr);
```

传入需要反序列化的类型作为泛型参数，以及序列化结果的二进制数组，会返回反序列化出的对象实例



