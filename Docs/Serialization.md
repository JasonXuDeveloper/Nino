# 序列化模块使用方法

## 非Unity平台（`v1.2.0`之前）

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

```Nino.Serialization v1.2.0```与其**之前**的**所有版本**都**不兼容**，升级Nino后需要用新版```Writer/Serializer```  **重新导出** 一份数据，才能被最新版的```Reader/Deserializer```正常解析，同时需要生成新的代码！！！

> 这个版本去掉了多态，同时去掉了数字类型压缩，改造了代码生成





```Nino.Serialization v1.1.2```与其**之前**的**所有版本**都**不兼容**，升级Nino后需要用新版```Writer/Serializer```  **重新导出** 一份数据，才能被最新版的```Reader/Deserializer```正常解析！！！

> 这个版本开始支持了多态了，所以Array/List/HashSet/Queue/Stack等集合类型的二进制格式有变化



```Nino.Serialization v1.1.0```与其**之前**的**所有版本**都**不兼容**，升级Nino后需要用新版```Writer/Serializer```  **重新导出** 一份数据，才能被最新版的```Reader/Deserializer```正常解析！！！

> 这个版本开始支持了null对象，所以二进制格式有变化





```Nino.Serialization v1.0.21```与其**之前**的**所有版本**都**不兼容**，升级Nino后需要用新版```Writer/Serializer```  **重新导出** 一份数据，才能被最新版的```Reader/Deserializer```正常解析！！！（```v1.0.21```有个Log忘删了，所以补发了```v1.0.21.2```）

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
> **强烈建议通过生成代码来提高性能，但是需要注意，每次更新字段或属性后需要重新生成代码来更新**



## 支持类型

支持序列化的成员类型（底层自带支持）：

- byte, sbyte, short, ushort, int, uint, long, ulong, double, float, decimal, char, string, bool, enum, DateTime
- Nullable<任意支持Nino序列化的struct>
- List<可Nino序列化类型>，HashSet<可Nino序列化类型>，Queue<可Nino序列化类型>，Stack<可Nino序列化类型>，可Nino序列化类型[]， ICollection<可Nino序列化>
- Dictionary<Nino支持类型,Nino支持类型>，IDictionary<可Nino序列化>
- 可Nino序列化类型
- null

不支持序列化的成员类型（可以通过注册自定义包装器实现）：

- 任何非上述类型

**不支持多态！！！**

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
>
> ILRuntime下也不支持多态！



## 注册自定义包装器

包装器是Nino提供的一个基类，内部需要实现序列化和反序列化，实现后，全局给该类型序列化或反序列化时，会调用这里的代码

需要注意的是，不需要同时注册自定义包装器和自定义委托，如果同时注册了这两个东西，只有最后注册的那个会生效

使用方法：

- 创建一个类型，并继承```NinoWrapperBase<T>```，T是你需要序列化/反序列化的类型，也可以用泛型代替，可以自由发挥
- 实现```public override void Serialize(T val, ref Writer writer)```，自行根据```Nino.Serialization.Writer```的公共接口来序列化T类型的数据即可
- 实现```public override T Deserialize(Reader reader)```，自行根据```Nino.Serialization.Reader```的公共接口来实现反序列化T类型的数据即可
- 实现`public overrid int GetSize(T val)`，自行返回该类型的长度即可
- 调用```WrapperManifest.AddWrapper(typeof(T), new NinoWrapperBase<T>());```以注册接口包装器

示例：

```csharp
public class Vector3Wrapper : NinoWrapperBase<Vector3>
{
  public override void Serialize(Vector3 val, ref Writer writer)
  {
    writer.Write(val.x);
    writer.Write(val.y);
    writer.Write(val.z);
  }

  public override Vector3 Deserialize(Reader reader)
  {
    return new Vector3(reader.Read<float>(4), reader.Read<float>(4), reader.Read<float>(4));
  }

  public override int GetSize(Vector3 val)
  {
    return 12;
  }
}

//别忘了在某个地方调用下面的代码：
WrapperManifest.AddWrapper(typeof(Vector3), new Vector3Wrapper());
```



## 代码生成

不生成代码也不会影响使用，但是生成后性能快很多很多很多（ILRuntime反而会慢很多很多，因为原理问题）

- Unity下直接在菜单栏点击```Nino/Generator/Serialization Code```即可，代码会生成到```Assets/Nino/Generated```，也可以打开```Assets/Nino/Editor/SerializationHelper.cs```并修改内部的```ExportPath```参数
- 非Unity下调用```CodeGenerator.GenerateSerializationCodeForAllTypePossible```接口即可
- **开启了自动收集字段和属性，生成代码和没生成代码，序列化的结果是一样的**

> 不想生成代码的类或结构体可以打```[CodeGenIgnore]```标签到该类或结构体上，可以在性能对比的时候用这个（例如[这个真机测试](../Nino_Unity/Assets/Nino/Test/BuildTest.cs)）



## 压缩方式

Nino支持以下三种压缩方式：

- Zlib(高压缩率低性能)
- Lz4(平均压缩率高性能)
- 无压缩(高性能但体积很大)



> 序列化和反序列化的时候可以选择压缩方式，但是需要注意反序列化数据的时候，需要用和序列化时相同的压缩方式去反序列化
>
> 注意，v1.2.0暂时仅支持无压缩



## 序列化

```csharp
byte[] Nino.Serialization.Serializer.Serialize<T>(T val);
byte[] Nino.Serialization.Serializer.Serialize(object val);
```

```csharp
int Nino.Serialization.Serializer.Serialize<T>(Span<byte> buffer, in T val);
int Nino.Serialization.Serializer.Serialize(Span<byte> buffer, object val)
```

示范：

```csharp
//懒人写法
byte[] byteArr = Nino.Serialization.Serializer.Serialize<ObjClass>(obj);

//进阶写法：速度快且将近0GC的写法：

//也可以搭配stackalloc使用
Span<byte> stackMemory = stackalloc byte[1024];//请确保这个对象不可能超过1024字节
int writtenSize = Nino.Serialization.Serializer.Serialize<ObjClass>(stackMemory, obj);
//将stackMemory.Slice(writtenSize) 写入网络流之类的
...
//也可以搭配ArrayPool使用
int size = Nino.Serialization.Serializer.GetSize<ObjClass>(obj);
byte[] arr = ArrayPool<byte>.Shared.Rent(size);
Nino.Serialization.Serializer.Serialize<ObjClass>(new Span<byte>(arr, 0, size), obj);
//将arr的第0个到第size个字节写入流
ArrayPool<byte>.Shared.Return(arr);
```

传入需要序列化的类型作为泛型参数，以及该类型的实例，会返回二进制数组

还有其他类型的序列化：

```csharp
Serialize<T>(T[] val);
Serialize<T>(T? val);
Serialize<T>(List<T> val);
Serialize<T>(HashSet<T> val);
Serialize<T>(Queue<T> val);
Serialize<T>(Stack<T> val);
Serialize<TKey, TValue>(Dictionary<TKey, TValue> val);
```



## 反序列化

```csharp
Nino.Serialization.Deserializer.Deserialize<T>(byte[] data);
Nino.Serialization.Deserializer.Deserialize(Type type, byte[] data);
Nino.Serialization.Deserializer.DeserializeArray<T>(byte[] data);
Nino.Serialization.Deserializer.DeserializeNullable<T>(byte[] data);
Nino.Serialization.Deserializer.DeserializeList<T>(byte[] data);
Nino.Serialization.Deserializer.DeserializeHashSet<T>(byte[] data);
Nino.Serialization.Deserializer.DeserializeQueue<T>(byte[] data);
Nino.Serialization.Deserializer.DeserializeStack<T>(byte[] data);
```



> data不仅可以传```byte[]```，还可以```ArraySegment<byte>```或```Span<byte>```
>

示范：

```csharp
//假设这里byteArr是byte[]
var obj = Nino.Serialization.Deserializer.Deserialize<ObjClass>(byteArr);
...
//高级用法，假设网络层传来了数据（比如Pipeline），我们收到了ReadOnlySequence<byte>
//这样写性能最最最最好
ReadOnlySequence<byte> data = xxxxx;
ObjClass obj;
if(data.IsSingleSegment)
{
  Span<byte> dataSpan = data.FirstSpan;
  obj = Nino.Serialization.Deserializer.Deserialize<ObjClass>(dataSpan);
}
else
{
  if(data.Length <= 1024)
  {
    Span<byte> stackMemory = stackalloc byte[(int)data.Length];
    data.CopyTo(stackMemory);
    obj = Nino.Serialization.Deserializer.Deserialize<ObjClass>(stackMemory);
  }
  else
  {
    byte[] arr = ArrayPool<byte>.Shared.Rent((int)data.Length);
    obj = Nino.Serialization.Deserializer.Deserialize<ObjClass>(new Span<byte>(arr, 0, (int)data.Length));
    ArrayPool<byte>.Shared.Return(arr);
  }
}
```

传入需要反序列化的类型作为泛型参数，以及序列化结果的二进制数组，会返回反序列化出的对象实例



