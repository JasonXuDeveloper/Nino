# Extensible Buffer 性能报告

> 测试结果表明ExtensibleBuffer在扩容的时候，不会因为其ElementType所占用的字节不同而产生不同的GC（List会，比如测试数据里List int的GC是List byte的四倍）
>
> 于此同时，测试时的V1是一般的**标准写法**，V2则是**优化写法**
>
> 最后，测试结果表明ExtensibleBuffer在插入次数**小于10万次（严格意义上65536）**时，插入**性能比List慢3倍**左右，**GC少2~150倍**
>
> 在插入次数**大于10万（严格来讲是65536）**次时，如果ElementType是**byte**，则比List**慢2倍**左右，**其他类型均接近List的性能或者更快**，ElementType所占用的**字节越多**，ExtensibleBuffer快的**速度越多**，同时**GC比List少50~上万倍**（```ExtensibleBuffer<long>的GC差距能和List<long>的GC相差数十万倍```，因为Long占用8字节）
>
> 注意，ExtensibleBuffer仅支持非托管ElementType（byte/short/int/long等基础类型）

``` ini
BenchmarkDotNet=v0.13.1, OS=macOS Monterey 12.0.1 (21A559) [Darwin 21.1.0]
Intel Core i9-8950HK CPU 2.90GHz (Coffee Lake), 1 CPU, 12 logical and 6 physical cores
.NET SDK=6.0.301
  [Host]   : .NET 5.0.11 (5.0.1121.47308), X64 RyuJIT
  ShortRun : .NET 5.0.11 (5.0.1121.47308), X64 RyuJIT

Job=ShortRun  Platform=AnyCpu  Runtime=.NET 5.0  
IterationCount=1  LaunchCount=1  WarmupCount=1  

```

| Method                           | testCount     |                 Mean |  Error |         Gen 0 |         Gen 1 |         Gen 2 |        Allocated |
| -------------------------------- | ------------- | -------------------: | -----: | ------------: | ------------: | ------------: | ---------------: |
| **ByteExtensibleBufferInsertV1** | **100**       |         **823.6 ns** | **NA** |    **0.0267** |    **0.0134** |         **-** |        **176 B** |
| ByteExtensibleBufferInsertV2     | 100           |           1,012.4 ns |     NA |        0.0305 |        0.0153 |             - |            200 B |
| ByteListInsertV1                 | 100           |             274.0 ns |     NA |        0.0687 |             - |             - |            432 B |
| ByteListInsertV2                 | 100           |             180.2 ns |     NA |        0.0253 |             - |             - |            160 B |
| IntExtensibleBufferInsertV1      | 100           |           1,211.4 ns |     NA |        0.0267 |        0.0134 |             - |            176 B |
| IntExtensibleBufferInsertV2      | 100           |           1,695.8 ns |     NA |        0.0305 |        0.0153 |             - |            200 B |
| IntListInsertV1                  | 100           |             290.5 ns |     NA |        0.1884 |             - |             - |          1,184 B |
| IntListInsertV2                  | 100           |             165.0 ns |     NA |        0.0725 |             - |             - |            456 B |
| **ByteExtensibleBufferInsertV1** | **1000**      |       **4,788.5 ns** | **NA** |    **0.0229** |    **0.0076** |         **-** |        **176 B** |
| ByteExtensibleBufferInsertV2     | 1000          |           5,584.1 ns |     NA |        0.0305 |        0.0153 |             - |            200 B |
| ByteListInsertV1                 | 1000          |           1,700.8 ns |     NA |        0.3643 |             - |             - |          2,296 B |
| ByteListInsertV2                 | 1000          |           1,418.6 ns |     NA |        0.1678 |             - |             - |          1,056 B |
| IntExtensibleBufferInsertV1      | 1000          |           6,415.9 ns |     NA |        0.0229 |        0.0076 |             - |            176 B |
| IntExtensibleBufferInsertV2      | 1000          |           7,670.7 ns |     NA |        0.0305 |        0.0153 |             - |            200 B |
| IntListInsertV1                  | 1000          |           1,981.5 ns |     NA |        1.3390 |             - |             - |          8,424 B |
| IntListInsertV2                  | 1000          |           1,564.0 ns |     NA |        0.6447 |             - |             - |          4,056 B |
| **ByteExtensibleBufferInsertV1** | **10000**     |      **46,036.5 ns** | **NA** |    **0.1831** |    **0.0610** |         **-** |      **1,369 B** |
| ByteExtensibleBufferInsertV2     | 10000         |          44,662.3 ns |     NA |             - |             - |             - |            200 B |
| ByteListInsertV1                 | 10000         |          16,259.2 ns |     NA |        5.2490 |        0.0610 |             - |         33,112 B |
| ByteListInsertV2                 | 10000         |          16,330.9 ns |     NA |        1.6022 |             - |             - |         10,056 B |
| IntExtensibleBufferInsertV1      | 10000         |          61,869.6 ns |     NA |        0.1221 |             - |             - |          1,368 B |
| IntExtensibleBufferInsertV2      | 10000         |          47,299.7 ns |     NA |             - |             - |             - |            200 B |
| IntListInsertV1                  | 10000         |          20,297.2 ns |     NA |       20.8130 |        0.0305 |             - |        131,400 B |
| IntListInsertV2                  | 10000         |          14,838.2 ns |     NA |        6.3629 |             - |             - |         40,056 B |
| **ByteExtensibleBufferInsertV1** | **100000**    |     **466,540.3 ns** | **NA** |    **2.9297** |    **1.4648** |         **-** |     **20,664 B** |
| ByteExtensibleBufferInsertV2     | 100000        |         504,977.0 ns |     NA |             - |             - |             - |            288 B |
| ByteListInsertV1                 | 100000        |         200,201.0 ns |     NA |       41.5039 |       41.5039 |       41.5039 |        262,574 B |
| ByteListInsertV2                 | 100000        |         210,353.8 ns |     NA |       31.0059 |       31.0059 |       31.0059 |        100,067 B |
| IntExtensibleBufferInsertV1      | 100000        |         594,262.2 ns |     NA |        2.9297 |        0.9766 |             - |         20,665 B |
| IntExtensibleBufferInsertV2      | 100000        |         499,490.2 ns |     NA |             - |             - |             - |            288 B |
| IntListInsertV1                  | 100000        |         473,989.1 ns |     NA |      285.6445 |      285.6445 |      285.6445 |      1,049,072 B |
| IntListInsertV2                  | 100000        |         324,478.3 ns |     NA |      124.5117 |      124.5117 |      124.5117 |        400,098 B |
| **ByteExtensibleBufferInsertV1** | **1000000**   |   **4,451,299.5 ns** | **NA** |   **23.4375** |    **7.8125** |         **-** |    **164,099 B** |
| ByteExtensibleBufferInsertV2     | 1000000       |       4,202,388.1 ns |     NA |             - |             - |             - |            987 B |
| ByteListInsertV1                 | 1000000       |       2,382,689.6 ns |     NA |      496.0938 |      496.0938 |      496.0938 |      2,097,808 B |
| ByteListInsertV2                 | 1000000       |       1,742,086.7 ns |     NA |      248.0469 |      248.0469 |      248.0469 |      1,000,140 B |
| IntExtensibleBufferInsertV1      | 1000000       |       5,813,807.9 ns |     NA |       23.4375 |        7.8125 |             - |        164,099 B |
| IntExtensibleBufferInsertV2      | 1000000       |       4,986,703.6 ns |     NA |             - |             - |             - |            987 B |
| IntListInsertV1                  | 1000000       |       5,564,624.0 ns |     NA |     1992.1875 |     1992.1875 |     1992.1875 |      8,389,700 B |
| IntListInsertV2                  | 1000000       |       3,215,156.8 ns |     NA |      996.0938 |      996.0938 |      996.0938 |      4,000,392 B |
| **ByteExtensibleBufferInsertV1** | **10000000**  |  **44,818,452.5 ns** | **NA** |  **250.0000** |  **250.0000** |  **250.0000** |  **1,311,156 B** |
| ByteExtensibleBufferInsertV2     | 10000000      |      42,373,214.8 ns |     NA |             - |             - |             - |          8,740 B |
| ByteListInsertV1                 | 10000000      |      41,161,919.3 ns |     NA |     1468.7500 |     1468.7500 |     1468.7500 |     33,555,508 B |
| ByteListInsertV2                 | 10000000      |      17,174,547.6 ns |     NA |      500.0000 |      500.0000 |      500.0000 |     10,000,222 B |
| IntExtensibleBufferInsertV1      | 10000000      |      57,987,273.1 ns |     NA |      222.2222 |      222.2222 |      222.2222 |      1,311,157 B |
| IntExtensibleBufferInsertV2      | 10000000      |      47,366,625.7 ns |     NA |             - |             - |             - |          8,687 B |
| IntListInsertV1                  | 10000000      |      76,718,383.7 ns |     NA |     3857.1429 |     3857.1429 |     3857.1429 |    134,219,966 B |
| IntListInsertV2                  | 10000000      |      32,623,680.2 ns |     NA |      937.5000 |      937.5000 |      937.5000 |     40,000,932 B |
| **ByteExtensibleBufferInsertV1** | **100000000** | **449,033,616.0 ns** | **NA** | **1000.0000** | **1000.0000** | **1000.0000** | **20,972,592 B** |
| ByteExtensibleBufferInsertV2     | 100000000     |     439,392,083.0 ns |     NA |             - |             - |             - |         85,984 B |
| ByteListInsertV1                 | 100000000     |     265,987,334.0 ns |     NA |     4500.0000 |     4500.0000 |     4500.0000 |    268,437,768 B |
| ByteListInsertV2                 | 100000000     |     177,378,428.0 ns |     NA |      666.6667 |      666.6667 |      666.6667 |    100,000,520 B |
| IntExtensibleBufferInsertV1      | 100000000     |     588,923,928.0 ns |     NA |     1000.0000 |     1000.0000 |     1000.0000 |     20,972,592 B |
| IntExtensibleBufferInsertV2      | 100000000     |     474,822,606.0 ns |     NA |             - |             - |             - |         85,984 B |
| IntListInsertV1                  | 100000000     |     630,709,796.0 ns |     NA |     6000.0000 |     6000.0000 |     6000.0000 |  1,073,744,792 B |
| IntListInsertV2                  | 100000000     |     316,833,538.0 ns |     NA |      500.0000 |      500.0000 |      500.0000 |    400,000,540 B |

