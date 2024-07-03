# 序列化模块使用方法

## 定义可序列化类型

- 给需要Nino序列化/反序列化的类或结构体，打上```[NinoType]```标签，会自动收集所有public字段和包含getter与setter的属性
- 如果**不需要自动收集全部字段和属性，则该标签内部加入个false参数**，如```[NinoType(false)]```（默认是true）
- 如果**没有自动收集全部字段和属性**，则需要给想序列化/反序列化的字段或属性，打上```[NinoMember()]```标签，标签内部需要传入一个数字参数，即序列化和反序列化时该成员的位置，如```[NinoMember(1)]```，收集顺序是按标签的数字从小到大排序的
- 如果**开启了自动收集全部字段和属性**，且**需要略过某些字段或属性**，请将其打上```[NinoIgnore]```标签，需要注意的是，如果没开启自动收集，该标签会无效

代码示范：

```csharp
[NinoType]
public struct AutoCollectStruct
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

[NinoType(false)]
public partial struct NotAutoCollectStruct
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

> 推荐使用字段而非属性，性能略好

## 版本兼容

- 可以给已序列化的相同类型的字段/属性改名
- 可以给已序列化的字段/属性改成相同内存大小的类型（`int`->`uint`，`int`->`float`，`List<long>`->`List<double`，`List<int[]>`->`List<float[]`）
- 可以加入新的字段/属性（需要确保index在老字段/属性的后面，自动收集的话则将新的字段/属性确保是最后定义的即可）
- **不可以**删除被收集的字段/属性
- **不可以**添加收集字段/属性
- **可以添加**不被收集的字段/属性

## 支持类型

支持序列化的成员类型（底层自带支持）：

- byte, sbyte, short, ushort, int, uint, long, ulong, double, float, decimal, char, string, bool, enum, DateTime, [任意UnmanagedType](https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/builtin-types/unmanaged-types)
- 标记了NinoType的类型
- Nullable<任意支持Nino序列化的struct>
- List<可Nino序列化类型>，HashSet<可Nino序列化类型>，可Nino序列化类型[]， ICollection<可Nino序列化>
- Dictionary<Nino支持类型,Nino支持类型>，IDictionary<可Nino序列化>
- 可Nino序列化类型
- null

不支持序列化的成员类型：

- 任何非上述类型

**支持多态！！！**

## 限制

- 需要搭配Source Generator使用

- 不支持使用NinoType去修饰自定义泛型类型

- 无泛型序列化/反序列化非托管类型的代码

- 无法序列化/反序列化非public字段/属性

- 需要有空参数的构造函数

- 如果定义了不支持序列化的类型（如Queue）则会导致编译错误

- 暂时需要序列化端和反序列化端需要使用Nino序列化的类型一一对应（即假设我在A项目里用了Nino去序列化，有10个NinoType类型，那么我在B项目里反序列化A项目生成的二进制时，需确保B项目里也不多不少只有这10个NinoType类型）
  
  > 该限制目前可以通过把一个工程生成的代码复制出来给另一个工程使用（NinoSerializerExtension.(Ext.)g.cs和NinoDeserializerExtension.(Ext.)g.cs）
  > 
  > 这样就不需要去管两个工程之间类型数量是否对照了
  > 
  > 这个问题出现的原因是因为要兼容多态所以需要给每个类型分配一个TypeId，目前是按全部NinoType的类型名字按顺序分配的ID，所以才需要保证不同工程之间的Nino类型名称一致，不然两个工程生成的TypeId会不一致从而导致无法跨工程反序列化数据，如果有朋友有解决方案欢迎PR

## Unity支持

> 需要Unity2022.3及以上版本

1. 下载[Nino.unitypackage](Nino.unitypackage)

2. 导入到Unity

3. 把导入后的Nino文件夹移动到一个带有asmdef的目录内（如Nino_Unity工程里的Test目录），因为Unity有Bug，必须把Nino源码丢到一个asmdef目录内才能对这个目录内类型生成代码

4. 如果报错`Nino.Core references netstandard, Version=2.1.0.0, Culture=neutral, PublicKeyToken=cc7b13ffcd2ddd51. A roslyn analyzer should reference netstandard version 2.0`请忽略，unity的bug

5. 如果需要多个asmdef使用，就把导入的Nino目录复制到多个带有不同asmdef的目录内

## 代码热更（Unity）

- Nino支持**HybridCLR**与**ILRuntime**
- 如果需要另外创建一个C#工程（不是Unity内的asmdef），请创建Net Core工程并通过NuGet安装Nino库，再将编译出来的DLL在HybridCLR或ILRuntime中使用

## 序列化

```csharp
byte[] Nino.Serializer.Serialize(可Nino序列化类型 val);
void Nino.Serializer.Serialize(可Nino序列化类型 val, IBufferWriter<byte> bufferWriter);
```

示范：

```csharp
//懒人写法
ObjClass obj = new ObjClass();
byte[] byteArr = Nino.Serializer.Serialize(obj);
//或
byteArr = obj.Serialize();

//进阶写法：速度快且将近0GC的写法：请自己根据用法封装个实现了IBufferWritter<byte>的类型，这样的话不一定需要在序列化结束后分配新的二进制数组
```

## 反序列化

```csharp
void Nino.Deserializer.Deserialize(ReadOnlySpan<byte> data, out 可Nino序列化类型 value);
```

> data不仅可以传```byte[]```，还可以```ArraySegment<byte>```或```Span<byte>```

示范：

```csharp
//假设这里byteArr是byte[]
Nino.Deserializer.Deserialize(byteArr, out ObjClass obj);
...
//高级用法，假设网络层传来了数据（比如Pipeline），我们收到了ReadOnlySequence<byte>
//这样写性能最最最最好
ReadOnlySequence<byte> data = xxxxx;
ObjClass obj;
if(data.IsSingleSegment)
{
  Span<byte> dataSpan = data.FirstSpan;
  Nino.Deserializer.Deserialize(dataSpan, out ObjClass obj);
}
else
{
  if(data.Length <= 1024)
  {
    Span<byte> stackMemory = stackalloc byte[(int)data.Length];
    data.CopyTo(stackMemory);
    Nino.Deserializer.Deserialize(stackMemory, out ObjClass obj);
  }
  else
  {
    byte[] arr = ArrayPool<byte>.Shared.Rent((int)data.Length);
    data.CopyTo(arr);
    Nino.Deserializer.Deserialize(arr, out ObjClass obj);
    ArrayPool<byte>.Shared.Return(arr);
  }
}
```
