# 可动态扩容数组使用方法

### 须知

目前```ExtensibleBuffer```仅支持非托管类型，即（[来源](https://docs.microsoft.com/zh-cn/dotnet/csharp/language-reference/builtin-types/unmanaged-types)）：

- `sbyte`、`byte`、`short`、`ushort`、`int`、`uint`、`long`、`ulong`、`char`、`float`、`double`、`decimal` 或 `bool`
- 任何[枚举](https://docs.microsoft.com/zh-cn/dotnet/csharp/language-reference/builtin-types/enum)类型
- 任何[指针](https://docs.microsoft.com/zh-cn/dotnet/csharp/language-reference/unsafe-code#pointer-types)类型
- 任何用户定义的 [struct](https://docs.microsoft.com/zh-cn/dotnet/csharp/language-reference/builtin-types/struct) 类型，只包含非托管类型的字段，并且在 C# 7.3 及更早版本中，不是构造类型（包含至少一个类型参数的类型）

### 性能

插入性能：ExtensibleBuffer在长度小于1000时，比List慢```5~30%```，长度大于等于1000时，比List快```0~500%```，数组元素的内存大小越大，性能越卓越，详见测试数据

读取性能：暂未测试，根据其原理，预估在任何情况下比List快```10~100```%，数组元素的内存大小越大，性能越卓越，

GC：是List所产生的GC的几十到几百万**分之一**，数组元素的内存大小越大，GC差异越大（即List的GC会比ExtensibleBuffer的GC大更多）

### 构造函数

- 空构造函数
  - 会创建一个默认扩容长度为```DefaultBufferSize```个元素（128）的ExtensibleBuffer
- 带扩容长度的构造函数
  - 可以传一个Int字段，代表该数组每次动态扩容时的扩容长度

> 构造ExtensibleBuffer后，会申请一个扩容长度内存大小的非托管内存，该非托管内存会在折构函数被释放，用户无需担心造成内存泄漏

### 字段

- Data
  - IntPtr，该数组实际数据的指针，可以转(T*)，T为该数组的参数类型
  - 该字段只可读
- ExpandSize
  - Int，扩容长度，即该数组每次扩容时增加的元素长度，注意，是元素长度，比如ExpandSize为1024的```ExtensibleBuffer<int>```，每次扩容会增加1024个int元素的空间，即扩容1024 * 4 = 4096字节的内存
  - 该字段只可读
- TotalLength
  - Int，该数组的有效总元素长度，注意，是元素长度，如果该数组能容纳2048个int，那就会返回2048，而不是返回2048 * 4 = 8192
  - 该字段只可读
- 索引器（```ExtensibleBuffer<T>[index]```）
  - T，该数组第index个元素
  - 注意，读取数组数据时，index可以超出TotalLength范围，但是返回的结果会有问题，因为请求的指针地址要么没开辟要么被其他数据持有了
  - 该字段可读并可写
  - 写入时，如果index超出了TotalLength范围，会自动扩容（基本无GC）

### 方法

- ToArray(int startIndex, int length)
  - startIndex: 该数组的第几个元素开始
  - length: 共拷贝多少个元素
  - 返回```T[]```，会产生GC
  - 注意，如果startIndex+length超过了TotalLength，会动态扩容，会自动填充超出范围的内存（不会出现数组越界异常）
- AsSpan(int startIndex, int length)
  - startIndex: 该数组的第几个元素开始
  - length: 共拷贝多少个元素
  - 返回```Span<T>```，**不会产生GC**，**操作该Span相当于直接操作该数组内的数据**
  - 注意，如果startIndex+length超过了TotalLength，会动态扩容，会自动填充超出范围的内存（不会出现数组越界异常）
  - **支持隐式**转换ExtensibleBuffer至Span，**隐式转换时默认从0开始拷贝转换时该buffer的TotalLength个元素**
- CopyFrom(T[] src, int srcIndex, int dstIndex, int length)
  - src: 原数据
  - srcIndex: 原数据开始拷贝的偏移
  - dstIndex: 从该ExtensibleBuffer的第几个元素开始写入拷贝数据
  - length: 拷贝长度
  - 注意，如果dstIndex+length超过了TotalLength，会动态扩容，会自动填充超出范围的内存（不会出现数组越界异常）
  - 该方法会把src内的数据复制到该数组，并且无需担心越界问题
- CopyFrom(T* src, int srcIndex, int dstIndex, int length)
  - 同上CopyFrom(T[] src, int srcIndex, int dstIndex, int length)，唯一的区别是可以传数组指针
- CopyTo(ref T[] dst, int srcIndex, int length)
  - dst: 将数据拷贝到的目的地
  - srcIndex: 从该ExtensibleBuffer的第几个元素开始拷贝到dst
  - length: 拷贝多少个元素到dst
  - 注意，如果srcIndex+length超过了TotalLength，会动态扩容，会自动填充超出范围的内存（不会出现数组越界异常）
  - 会把数据拷贝到dst，且一定是从dst[0]开始
- CopyTo(T* dst, int srcIndex, int length)
  - 同上CopyTo(ref T[] dst, int srcIndex, int length)，唯一的区别是可以传数组指针





### 推荐用法

- 建议手动写个字段记录有效长度
- CopyFrom类似List的AddRange

- CopyTo等同于List的CopyTo

- ToArray等同于List（或Linq）的ToArray

- AsSpan非常强大，且无GC，能直接操作该数组元素的指针，并且完成很多工作（比如遍历和切割）

  - 因为ExtensibleBuffer自身不支持foreach，但是可以转```Span<T>```后进行foreach遍历元素（这样可以直接在内存上访问，操作元素就可以直接操作到内存）：

    ```csharp
    ExtensibleBuffer<byte> test = new ExtensibleBuffer<byte>();
    foreach (byte v in (Span<byte>)test)
    {
      Console.WriteLine(v);
    }
    ```

    > 需要注意，这里会遍历TotalLength次

  - 如果需要指定范围遍历，使用Span也很方便：

    ```csharp
    ExtensibleBuffer<byte> test = new ExtensibleBuffer<byte>();
    foreach (byte v in test.AsSpan(0,10))
    {
      Console.WriteLine(v);
    }
    ```

    > 这样写就会从test数组的第0个元素开始，访问总10个元素