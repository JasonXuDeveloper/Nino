# Extensible Buffer 性能报告

> 测试结果表明ExtensibleBuffer在扩容的时候，不会因为其ElementType所占用的字节不同而产生不同的GC（List会，比如测试数据里List int的GC是List byte的四倍）
>
> 于此同时，测试时的V1是一般的**标准写法**，V2则是**优化写法**
>
> 最后，测试结果表明ExtensibleBuffer在插入次数**小于1000次**时，插入**性能比List略慢**，但是**GC要少几十倍**
>
> 在插入次数**大于等于1000**次时，**任何类型的插入性能均接近List的性能或者更快**，ElementType所占用的**字节越多**，ExtensibleBuffer快的**速度越多**（测试结果表明可以快1~5倍），同时**GC比List少50~上百万倍**
>
> 注意，ExtensibleBuffer目前仅支持非托管ElementType（byte/short/int/long等基础类型）

``` ini
BenchmarkDotNet=v0.13.1, OS=macOS Monterey 12.0.1 (21A559) [Darwin 21.1.0]
Intel Core i9-8950HK CPU 2.90GHz (Coffee Lake), 1 CPU, 12 logical and 6 physical cores
.NET SDK=6.0.301
  [Host]   : .NET 5.0.11 (5.0.1121.47308), X64 RyuJIT
  ShortRun : .NET 5.0.11 (5.0.1121.47308), X64 RyuJIT

Job=ShortRun  Platform=AnyCpu  Runtime=.NET 5.0  
IterationCount=1  LaunchCount=1  WarmupCount=1  

```

| Method                           | testCount     |                 Mean |  Error |      Gen 0 |      Gen 1 |      Gen 2 |       Allocated |
| -------------------------------- | ------------- | -------------------: | -----: | ---------: | ---------: | ---------: | --------------: |
| **ByteExtensibleBufferInsertV1** | **100**       |         **336.7 ns** | **NA** | **0.0067** | **0.0033** | **0.0005** |        **40 B** |
| ByteExtensibleBufferInsertV2     | 100           |             266.1 ns |     NA |     0.0062 |     0.0029 |     0.0010 |            40 B |
| ByteListInsertV1                 | 100           |             263.3 ns |     NA |     0.0687 |          - |          - |           432 B |
| ByteListInsertV2                 | 100           |             158.2 ns |     NA |     0.0253 |          - |          - |           160 B |
| IntExtensibleBufferInsertV1      | 100           |             414.9 ns |     NA |     0.0062 |     0.0029 |     0.0010 |            40 B |
| IntExtensibleBufferInsertV2      | 100           |             388.6 ns |     NA |     0.0062 |     0.0029 |     0.0010 |            40 B |
| IntListInsertV1                  | 100           |             299.4 ns |     NA |     0.1884 |          - |          - |         1,184 B |
| IntListInsertV2                  | 100           |             174.9 ns |     NA |     0.0725 |          - |          - |           456 B |
| **ByteExtensibleBufferInsertV1** | **1000**      |       **1,993.4 ns** | **NA** | **0.0038** |      **-** |      **-** |        **40 B** |
| ByteExtensibleBufferInsertV2     | 1000          |           1,263.9 ns |     NA |     0.0057 |     0.0019 |          - |            40 B |
| ByteListInsertV1                 | 1000          |           1,745.1 ns |     NA |     0.3643 |          - |          - |         2,296 B |
| ByteListInsertV2                 | 1000          |           1,524.0 ns |     NA |     0.1678 |          - |          - |         1,056 B |
| IntExtensibleBufferInsertV1      | 1000          |           3,004.0 ns |     NA |     0.0038 |          - |          - |            40 B |
| IntExtensibleBufferInsertV2      | 1000          |           1,984.6 ns |     NA |     0.0038 |          - |          - |            40 B |
| IntListInsertV1                  | 1000          |           2,133.6 ns |     NA |     1.3390 |          - |          - |         8,424 B |
| IntListInsertV2                  | 1000          |           1,644.5 ns |     NA |     0.6447 |          - |          - |         4,056 B |
| **ByteExtensibleBufferInsertV1** | **10000**     |      **19,268.8 ns** | **NA** |      **-** |      **-** |      **-** |        **40 B** |
| ByteExtensibleBufferInsertV2     | 10000         |          10,564.6 ns |     NA |          - |          - |          - |            40 B |
| ByteListInsertV1                 | 10000         |          16,149.6 ns |     NA |     5.2490 |     0.0610 |          - |        33,112 B |
| ByteListInsertV2                 | 10000         |          14,426.8 ns |     NA |     1.6022 |          - |          - |        10,056 B |
| IntExtensibleBufferInsertV1      | 10000         |          20,047.8 ns |     NA |          - |          - |          - |            40 B |
| IntExtensibleBufferInsertV2      | 10000         |          12,251.8 ns |     NA |          - |          - |          - |            40 B |
| IntListInsertV1                  | 10000         |          20,068.5 ns |     NA |    20.8130 |     0.0305 |          - |       131,400 B |
| IntListInsertV2                  | 10000         |          15,270.7 ns |     NA |     6.3629 |          - |          - |        40,056 B |
| **ByteExtensibleBufferInsertV1** | **100000**    |     **132,670.4 ns** | **NA** |      **-** |      **-** |      **-** |        **40 B** |
| ByteExtensibleBufferInsertV2     | 100000        |          60,098.9 ns |     NA |          - |          - |          - |            41 B |
| ByteListInsertV1                 | 100000        |         192,424.0 ns |     NA |    41.5039 |    41.5039 |    41.5039 |       262,574 B |
| ByteListInsertV2                 | 100000        |         180,816.9 ns |     NA |    31.0059 |    31.0059 |    31.0059 |       100,069 B |
| IntExtensibleBufferInsertV1      | 100000        |         164,444.5 ns |     NA |          - |          - |          - |            40 B |
| IntExtensibleBufferInsertV2      | 100000        |         106,641.1 ns |     NA |          - |          - |          - |            40 B |
| IntListInsertV1                  | 100000        |         484,184.1 ns |     NA |   285.6445 |   285.6445 |   285.6445 |     1,049,077 B |
| IntListInsertV2                  | 100000        |         313,635.5 ns |     NA |   124.5117 |   124.5117 |   124.5117 |       400,098 B |
| **ByteExtensibleBufferInsertV1** | **1000000**   |   **1,261,051.5 ns** | **NA** |      **-** |      **-** |      **-** |        **42 B** |
| ByteExtensibleBufferInsertV2     | 1000000       |         869,128.0 ns |     NA |          - |          - |          - |            41 B |
| ByteListInsertV1                 | 1000000       |       2,336,066.3 ns |     NA |   496.0938 |   496.0938 |   496.0938 |     2,097,808 B |
| ByteListInsertV2                 | 1000000       |       1,992,171.6 ns |     NA |   248.0469 |   248.0469 |   248.0469 |     1,000,157 B |
| IntExtensibleBufferInsertV1      | 1000000       |       1,746,490.8 ns |     NA |          - |          - |          - |            44 B |
| IntExtensibleBufferInsertV2      | 1000000       |       1,056,615.8 ns |     NA |          - |          - |          - |            41 B |
| IntListInsertV1                  | 1000000       |       5,295,862.7 ns |     NA |  1996.0938 |  1996.0938 |  1996.0938 |     8,389,768 B |
| IntListInsertV2                  | 1000000       |       3,480,617.8 ns |     NA |   996.0938 |   996.0938 |   996.0938 |     4,000,392 B |
| **ByteExtensibleBufferInsertV1** | **10000000**  |  **18,716,193.7 ns** | **NA** |      **-** |      **-** |      **-** |        **50 B** |
| ByteExtensibleBufferInsertV2     | 10000000      |       7,778,869.9 ns |     NA |          - |          - |          - |            45 B |
| ByteListInsertV1                 | 10000000      |      27,874,372.8 ns |     NA |  1468.7500 |  1468.7500 |  1468.7500 |    33,555,781 B |
| ByteListInsertV2                 | 10000000      |      14,637,528.1 ns |     NA |   500.0000 |   500.0000 |   500.0000 |    10,000,217 B |
| IntExtensibleBufferInsertV1      | 10000000      |      33,108,877.6 ns |     NA |          - |          - |          - |           510 B |
| IntExtensibleBufferInsertV2      | 10000000      |       9,219,745.7 ns |     NA |          - |          - |          - |            50 B |
| IntListInsertV1                  | 10000000      |      70,517,701.4 ns |     NA |  3857.1429 |  3857.1429 |  3857.1429 |   134,219,966 B |
| IntListInsertV2                  | 10000000      |      31,683,073.7 ns |     NA |   937.5000 |   937.5000 |   937.5000 |    40,000,443 B |
| **ByteExtensibleBufferInsertV1** | **100000000** | **174,848,366.3 ns** | **NA** |      **-** |      **-** |      **-** |       **152 B** |
| ByteExtensibleBufferInsertV2     | 100000000     |      54,386,794.6 ns |     NA |          - |          - |          - |            99 B |
| ByteListInsertV1                 | 100000000     |     254,879,998.0 ns |     NA |  4500.0000 |  4500.0000 |  4500.0000 |   268,437,768 B |
| ByteListInsertV2                 | 100000000     |     171,825,885.0 ns |     NA |   666.6667 |   666.6667 |   666.6667 |   100,000,520 B |
| IntExtensibleBufferInsertV1      | 100000000     |     332,814,853.0 ns |     NA |          - |          - |          - |           512 B |
| IntExtensibleBufferInsertV2      | 100000000     |      90,691,119.0 ns |     NA |          - |          - |          - |           123 B |
| IntListInsertV1                  | 100000000     |     608,906,342.0 ns |     NA |  6000.0000 |  6000.0000 |  6000.0000 | 1,073,744,912 B |
| IntListInsertV2                  | 100000000     |     368,851,437.0 ns |     NA |   500.0000 |   500.0000 |   500.0000 |   400,000,540 B |

