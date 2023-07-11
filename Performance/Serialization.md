# Serilization 性能报告

#### [**测试数据**](/Nino_Unity/Assets/Nino/Test/Data.cs)

## Unity平台性能测试

新版数据待补充



## 非Unity平台性能测试

``` ini
BenchmarkDotNet=v0.13.1, OS=macOS 13.0.1 (22A400) [Darwin 22.1.0]
Intel Core i9-8950HK CPU 2.90GHz (Coffee Lake), 1 CPU, 12 logical and 6 physical cores
.NET SDK=8.0.100-preview.5.23303.2
  [Host]   : .NET 6.0.12 (6.0.1222.56807), X64 RyuJIT
  ShortRun : .NET 6.0.12 (6.0.1222.56807), X64 RyuJIT

Job=ShortRun  Platform=AnyCpu  Runtime=.NET 6.0  
IterationCount=1  LaunchCount=1  WarmupCount=1  

```

| Method                            | Serializer          |                Mean |  Error |   DataSize |        Gen 0 |        Gen 1 |        Gen 2 |     Allocated |
| --------------------------------- | ------------------- | ------------------: | -----: | ---------: | -----------: | -----------: | -----------: | ------------: |
| **_PrimitiveBoolDeserialize**     | **MessagePack_Lz4** |       **262.17 ns** | **NA** |      **-** |   **0.0100** |        **-** |        **-** |      **64 B** |
| _PrimitiveBoolDeserialize         | MessagePack_NoComp  |           108.33 ns |     NA |          - |            - |            - |            - |             - |
| _PrimitiveBoolDeserialize         | ProtobufNet         |           437.98 ns |     NA |          - |       0.0138 |            - |            - |          88 B |
| _PrimitiveBoolDeserialize         | JsonNet             |         1,216.66 ns |     NA |          - |       0.9041 |       0.0057 |            - |       5,672 B |
| _PrimitiveBoolDeserialize         | BinaryFormatter     |         3,140.05 ns |     NA |          - |       0.6561 |       0.0038 |            - |       4,128 B |
| _PrimitiveBoolDeserialize         | DataContract        |         2,193.95 ns |     NA |          - |       0.6638 |       0.0076 |            - |       4,168 B |
| _PrimitiveBoolDeserialize         | Hyperion            |            95.48 ns |     NA |          - |       0.0305 |            - |            - |         192 B |
| _PrimitiveBoolDeserialize         | Jil                 |            92.74 ns |     NA |          - |       0.0204 |            - |            - |         128 B |
| _PrimitiveBoolDeserialize         | SpanJson            |            20.85 ns |     NA |          - |            - |            - |            - |             - |
| _PrimitiveBoolDeserialize         | UTF8Json            |            34.63 ns |     NA |          - |            - |            - |            - |             - |
| _PrimitiveBoolDeserialize         | FsPickler           |           503.17 ns |     NA |          - |       0.1631 |            - |            - |       1,024 B |
| _PrimitiveBoolDeserialize         | Ceras               |            87.45 ns |     NA |          - |            - |            - |            - |             - |
| _PrimitiveBoolDeserialize         | OdinSerializer_     |           353.01 ns |     NA |          - |            - |            - |            - |             - |
| _PrimitiveBoolDeserialize         | Nino                |            86.40 ns |     NA |          - |            - |            - |            - |             - |
| **_PrimitiveBoolSerialize**       | **MessagePack_Lz4** |       **126.90 ns** | **NA** |    **1 B** |   **0.0153** |        **-** |        **-** |      **96 B** |
| _PrimitiveBoolSerialize           | MessagePack_NoComp  |           112.23 ns |     NA |        1 B |       0.0050 |            - |            - |          32 B |
| _PrimitiveBoolSerialize           | ProtobufNet         |           270.50 ns |     NA |        2 B |       0.0596 |            - |            - |         376 B |
| _PrimitiveBoolSerialize           | JsonNet             |           661.18 ns |     NA |        8 B |       0.4616 |       0.0019 |            - |       2,896 B |
| _PrimitiveBoolSerialize           | BinaryFormatter     |         1,901.34 ns |     NA |       53 B |       0.4883 |       0.0038 |            - |       3,072 B |
| _PrimitiveBoolSerialize           | DataContract        |           827.86 ns |     NA |       84 B |       0.2737 |            - |            - |       1,720 B |
| _PrimitiveBoolSerialize           | Hyperion            |           180.55 ns |     NA |        2 B |       0.0789 |            - |            - |         496 B |
| _PrimitiveBoolSerialize           | Jil                 |           110.33 ns |     NA |        5 B |       0.0267 |            - |            - |         168 B |
| _PrimitiveBoolSerialize           | SpanJson            |            64.60 ns |     NA |        5 B |       0.0050 |            - |            - |          32 B |
| _PrimitiveBoolSerialize           | UTF8Json            |            44.52 ns |     NA |        5 B |       0.0051 |            - |            - |          32 B |
| _PrimitiveBoolSerialize           | FsPickler           |           472.89 ns |     NA |       27 B |       0.1760 |            - |            - |       1,104 B |
| _PrimitiveBoolSerialize           | Ceras               |           349.18 ns |     NA |        1 B |       0.6614 |            - |            - |       4,152 B |
| _PrimitiveBoolSerialize           | OdinSerializer_     |           316.79 ns |     NA |        2 B |       0.0048 |            - |            - |          32 B |
| _PrimitiveBoolSerialize           | Nino                |           146.27 ns |     NA |        1 B |       0.0050 |            - |            - |          32 B |
| **_PrimitiveByteDeserialize**     | **MessagePack_Lz4** |       **173.70 ns** | **NA** |      **-** |   **0.0100** |        **-** |        **-** |      **64 B** |
| _PrimitiveByteDeserialize         | MessagePack_NoComp  |            76.77 ns |     NA |          - |            - |            - |            - |             - |
| _PrimitiveByteDeserialize         | ProtobufNet         |           324.39 ns |     NA |          - |       0.0138 |            - |            - |          88 B |
| _PrimitiveByteDeserialize         | JsonNet             |           901.83 ns |     NA |          - |       0.9108 |       0.0057 |            - |       5,720 B |
| _PrimitiveByteDeserialize         | BinaryFormatter     |         2,311.94 ns |     NA |          - |       0.6561 |       0.0038 |            - |       4,120 B |
| _PrimitiveByteDeserialize         | DataContract        |         1,811.74 ns |     NA |          - |       0.6580 |       0.0057 |            - |       4,136 B |
| _PrimitiveByteDeserialize         | Hyperion            |            82.76 ns |     NA |          - |       0.0305 |            - |            - |         192 B |
| _PrimitiveByteDeserialize         | Jil                 |            87.40 ns |     NA |          - |       0.0204 |            - |            - |         128 B |
| _PrimitiveByteDeserialize         | SpanJson            |            29.69 ns |     NA |          - |            - |            - |            - |             - |
| _PrimitiveByteDeserialize         | UTF8Json            |            42.17 ns |     NA |          - |            - |            - |            - |             - |
| _PrimitiveByteDeserialize         | FsPickler           |           510.09 ns |     NA |          - |       0.1612 |            - |            - |       1,016 B |
| _PrimitiveByteDeserialize         | Ceras               |            95.83 ns |     NA |          - |            - |            - |            - |             - |
| _PrimitiveByteDeserialize         | OdinSerializer_     |           408.88 ns |     NA |          - |            - |            - |            - |             - |
| _PrimitiveByteDeserialize         | Nino                |            98.67 ns |     NA |          - |            - |            - |            - |             - |
| **_PrimitiveByteSerialize**       | **MessagePack_Lz4** |       **127.84 ns** | **NA** |    **2 B** |   **0.0153** |        **-** |        **-** |      **96 B** |
| _PrimitiveByteSerialize           | MessagePack_NoComp  |            91.36 ns |     NA |        2 B |       0.0050 |            - |            - |          32 B |
| _PrimitiveByteSerialize           | ProtobufNet         |           268.41 ns |     NA |        3 B |       0.0596 |            - |            - |         376 B |
| _PrimitiveByteSerialize           | JsonNet             |           577.13 ns |     NA |        6 B |       0.4768 |       0.0010 |            - |       2,992 B |
| _PrimitiveByteSerialize           | BinaryFormatter     |         1,623.59 ns |     NA |       50 B |       0.4883 |       0.0038 |            - |       3,072 B |
| _PrimitiveByteSerialize           | DataContract        |           752.26 ns |     NA |       92 B |       0.2747 |            - |            - |       1,728 B |
| _PrimitiveByteSerialize           | Hyperion            |           160.05 ns |     NA |        2 B |       0.0789 |            - |            - |         496 B |
| _PrimitiveByteSerialize           | Jil                 |           137.56 ns |     NA |        3 B |       0.0420 |            - |            - |         264 B |
| _PrimitiveByteSerialize           | SpanJson            |            84.48 ns |     NA |        3 B |       0.0050 |            - |            - |          32 B |
| _PrimitiveByteSerialize           | UTF8Json            |            45.01 ns |     NA |        3 B |       0.0051 |            - |            - |          32 B |
| _PrimitiveByteSerialize           | FsPickler           |           493.11 ns |     NA |       24 B |       0.1745 |            - |            - |       1,096 B |
| _PrimitiveByteSerialize           | Ceras               |           367.07 ns |     NA |        1 B |       0.6614 |            - |            - |       4,152 B |
| _PrimitiveByteSerialize           | OdinSerializer_     |           324.68 ns |     NA |        2 B |       0.0048 |            - |            - |          32 B |
| _PrimitiveByteSerialize           | Nino                |           135.00 ns |     NA |        1 B |       0.0050 |            - |            - |          32 B |
| **_PrimitiveCharDeserialize**     | **MessagePack_Lz4** |       **150.04 ns** | **NA** |      **-** |   **0.0100** |        **-** |        **-** |      **64 B** |
| _PrimitiveCharDeserialize         | MessagePack_NoComp  |            69.56 ns |     NA |          - |            - |            - |            - |             - |
| _PrimitiveCharDeserialize         | ProtobufNet         |           312.59 ns |     NA |          - |       0.0138 |            - |            - |          88 B |
| _PrimitiveCharDeserialize         | JsonNet             |           762.04 ns |     NA |          - |       0.9117 |       0.0095 |            - |       5,720 B |
| _PrimitiveCharDeserialize         | BinaryFormatter     |         2,261.20 ns |     NA |          - |       0.6561 |       0.0038 |            - |       4,120 B |
| _PrimitiveCharDeserialize         | DataContract        |         1,441.26 ns |     NA |          - |       0.6580 |       0.0057 |            - |       4,136 B |
| _PrimitiveCharDeserialize         | Hyperion            |            82.89 ns |     NA |          - |       0.0305 |            - |            - |         192 B |
| _PrimitiveCharDeserialize         | Jil                 |            61.32 ns |     NA |          - |       0.0050 |            - |            - |          32 B |
| _PrimitiveCharDeserialize         | SpanJson            |            34.24 ns |     NA |          - |            - |            - |            - |             - |
| _PrimitiveCharDeserialize         | UTF8Json            |            72.87 ns |     NA |          - |       0.0038 |            - |            - |          24 B |
| _PrimitiveCharDeserialize         | FsPickler           |           495.26 ns |     NA |          - |       0.1612 |            - |            - |       1,016 B |
| _PrimitiveCharDeserialize         | Ceras               |            80.93 ns |     NA |          - |            - |            - |            - |             - |
| _PrimitiveCharDeserialize         | OdinSerializer_     |           334.60 ns |     NA |          - |            - |            - |            - |             - |
| _PrimitiveCharDeserialize         | Nino                |            87.82 ns |     NA |          - |            - |            - |            - |             - |
| **_PrimitiveCharSerialize**       | **MessagePack_Lz4** |       **108.94 ns** | **NA** |    **1 B** |   **0.0153** |        **-** |        **-** |      **96 B** |
| _PrimitiveCharSerialize           | MessagePack_NoComp  |            83.64 ns |     NA |        1 B |       0.0050 |            - |            - |          32 B |
| _PrimitiveCharSerialize           | ProtobufNet         |           215.61 ns |     NA |        2 B |       0.0598 |            - |            - |         376 B |
| _PrimitiveCharSerialize           | JsonNet             |           650.56 ns |     NA |        6 B |       0.5169 |       0.0019 |            - |       3,248 B |
| _PrimitiveCharSerialize           | BinaryFormatter     |         1,533.47 ns |     NA |       50 B |       0.4883 |       0.0038 |            - |       3,072 B |
| _PrimitiveCharSerialize           | DataContract        |           706.93 ns |     NA |       75 B |       0.2728 |            - |            - |       1,712 B |
| _PrimitiveCharSerialize           | Hyperion            |           162.62 ns |     NA |        3 B |       0.0789 |            - |            - |         496 B |
| _PrimitiveCharSerialize           | Jil                 |           113.40 ns |     NA |        3 B |       0.0267 |            - |            - |         168 B |
| _PrimitiveCharSerialize           | SpanJson            |            64.72 ns |     NA |        3 B |       0.0050 |            - |            - |          32 B |
| _PrimitiveCharSerialize           | UTF8Json            |            87.20 ns |     NA |        3 B |       0.0088 |            - |            - |          56 B |
| _PrimitiveCharSerialize           | FsPickler           |           477.69 ns |     NA |       24 B |       0.1745 |            - |            - |       1,096 B |
| _PrimitiveCharSerialize           | Ceras               |           336.91 ns |     NA |        2 B |       0.6614 |            - |            - |       4,152 B |
| _PrimitiveCharSerialize           | OdinSerializer_     |           321.80 ns |     NA |        3 B |       0.0048 |            - |            - |          32 B |
| _PrimitiveCharSerialize           | Nino                |           136.12 ns |     NA |        2 B |       0.0050 |            - |            - |          32 B |
| **_PrimitiveDateTimeDeserialize** | **MessagePack_Lz4** |       **197.56 ns** | **NA** |      **-** |   **0.0100** |        **-** |        **-** |      **64 B** |
| _PrimitiveDateTimeDeserialize     | MessagePack_NoComp  |            84.56 ns |     NA |          - |            - |            - |            - |             - |
| _PrimitiveDateTimeDeserialize     | ProtobufNet         |           352.64 ns |     NA |          - |       0.0138 |            - |            - |          88 B |
| _PrimitiveDateTimeDeserialize     | JsonNet             |         1,044.40 ns |     NA |          - |       0.9098 |       0.0057 |            - |       5,720 B |
| _PrimitiveDateTimeDeserialize     | BinaryFormatter     |         3,880.64 ns |     NA |          - |       0.9232 |       0.0076 |            - |       5,801 B |
| _PrimitiveDateTimeDeserialize     | DataContract        |         1,819.97 ns |     NA |          - |       0.6828 |       0.0095 |            - |       4,288 B |
| _PrimitiveDateTimeDeserialize     | Hyperion            |            93.05 ns |     NA |          - |       0.0305 |            - |            - |         192 B |
| _PrimitiveDateTimeDeserialize     | Jil                 |           217.84 ns |     NA |          - |       0.0267 |            - |            - |         168 B |
| _PrimitiveDateTimeDeserialize     | SpanJson            |           295.83 ns |     NA |          - |            - |            - |            - |             - |
| _PrimitiveDateTimeDeserialize     | UTF8Json            |           235.23 ns |     NA |          - |            - |            - |            - |             - |
| _PrimitiveDateTimeDeserialize     | FsPickler           |           589.08 ns |     NA |          - |       0.1631 |            - |            - |       1,024 B |
| _PrimitiveDateTimeDeserialize     | Ceras               |           175.09 ns |     NA |          - |            - |            - |            - |             - |
| _PrimitiveDateTimeDeserialize     | OdinSerializer_     |           706.02 ns |     NA |          - |       0.0162 |            - |            - |         104 B |
| _PrimitiveDateTimeDeserialize     | Nino                |            82.33 ns |     NA |          - |            - |            - |            - |             - |
| **_PrimitiveDateTimeSerialize**   | **MessagePack_Lz4** |       **558.48 ns** | **NA** |    **6 B** |   **0.0153** |        **-** |        **-** |      **96 B** |
| _PrimitiveDateTimeSerialize       | MessagePack_NoComp  |           306.01 ns |     NA |        6 B |       0.0048 |            - |            - |          32 B |
| _PrimitiveDateTimeSerialize       | ProtobufNet         |           302.47 ns |     NA |        6 B |       0.0596 |            - |            - |         376 B |
| _PrimitiveDateTimeSerialize       | JsonNet             |           803.17 ns |     NA |       30 B |       0.4807 |            - |            - |       3,016 B |
| _PrimitiveDateTimeSerialize       | BinaryFormatter     |         2,012.68 ns |     NA |       78 B |       0.5798 |       0.0038 |            - |       3,656 B |
| _PrimitiveDateTimeSerialize       | DataContract        |         1,145.24 ns |     NA |      106 B |       0.3414 |            - |            - |       2,144 B |
| _PrimitiveDateTimeSerialize       | Hyperion            |           171.93 ns |     NA |       10 B |       0.0801 |            - |            - |         504 B |
| _PrimitiveDateTimeSerialize       | Jil                 |           447.85 ns |     NA |       22 B |       0.0672 |            - |            - |         424 B |
| _PrimitiveDateTimeSerialize       | SpanJson            |           316.81 ns |     NA |       27 B |       0.0086 |            - |            - |          56 B |
| _PrimitiveDateTimeSerialize       | UTF8Json            |           374.99 ns |     NA |       27 B |       0.0086 |            - |            - |          56 B |
| _PrimitiveDateTimeSerialize       | FsPickler           |           712.63 ns |     NA |       44 B |       0.1783 |            - |            - |       1,120 B |
| _PrimitiveDateTimeSerialize       | Ceras               |           581.55 ns |     NA |        8 B |       0.6609 |            - |            - |       4,152 B |
| _PrimitiveDateTimeSerialize       | OdinSerializer_     |           694.13 ns |     NA |       99 B |       0.0200 |            - |            - |         128 B |
| _PrimitiveDateTimeSerialize       | Nino                |           137.24 ns |     NA |        8 B |       0.0050 |            - |            - |          32 B |
| **_PrimitiveIntDeserialize**      | **MessagePack_Lz4** |       **148.41 ns** | **NA** |      **-** |   **0.0100** |        **-** |        **-** |      **64 B** |
| _PrimitiveIntDeserialize          | MessagePack_NoComp  |            76.11 ns |     NA |          - |            - |            - |            - |             - |
| _PrimitiveIntDeserialize          | ProtobufNet         |           324.10 ns |     NA |          - |       0.0138 |            - |            - |          88 B |
| _PrimitiveIntDeserialize          | JsonNet             |           842.86 ns |     NA |          - |       0.9079 |       0.0067 |            - |       5,696 B |
| _PrimitiveIntDeserialize          | BinaryFormatter     |         2,035.29 ns |     NA |          - |       0.6561 |       0.0038 |            - |       4,120 B |
| _PrimitiveIntDeserialize          | DataContract        |         1,449.67 ns |     NA |          - |       0.6580 |       0.0057 |            - |       4,136 B |
| _PrimitiveIntDeserialize          | Hyperion            |            83.81 ns |     NA |          - |       0.0305 |            - |            - |         192 B |
| _PrimitiveIntDeserialize          | Jil                 |            95.71 ns |     NA |          - |       0.0229 |            - |            - |         144 B |
| _PrimitiveIntDeserialize          | SpanJson            |            50.59 ns |     NA |          - |            - |            - |            - |             - |
| _PrimitiveIntDeserialize          | UTF8Json            |            46.04 ns |     NA |          - |            - |            - |            - |             - |
| _PrimitiveIntDeserialize          | FsPickler           |           472.08 ns |     NA |          - |       0.1612 |            - |            - |       1,016 B |
| _PrimitiveIntDeserialize          | Ceras               |            96.58 ns |     NA |          - |            - |            - |            - |             - |
| _PrimitiveIntDeserialize          | OdinSerializer_     |           410.72 ns |     NA |          - |            - |            - |            - |             - |
| _PrimitiveIntDeserialize          | Nino                |           107.12 ns |     NA |          - |            - |            - |            - |             - |
| **_PrimitiveIntSerialize**        | **MessagePack_Lz4** |       **408.25 ns** | **NA** |    **5 B** |   **0.0153** |        **-** |        **-** |      **96 B** |
| _PrimitiveIntSerialize            | MessagePack_NoComp  |           333.21 ns |     NA |        5 B |       0.0048 |            - |            - |          32 B |
| _PrimitiveIntSerialize            | ProtobufNet         |           778.21 ns |     NA |       11 B |       0.0610 |            - |            - |         384 B |
| _PrimitiveIntSerialize            | JsonNet             |         2,366.20 ns |     NA |       14 B |       0.4768 |            - |            - |       3,000 B |
| _PrimitiveIntSerialize            | BinaryFormatter     |         5,830.16 ns |     NA |       54 B |       0.4883 |            - |            - |       3,072 B |
| _PrimitiveIntSerialize            | DataContract        |         2,536.22 ns |     NA |       82 B |       0.2708 |            - |            - |       1,720 B |
| _PrimitiveIntSerialize            | Hyperion            |           549.32 ns |     NA |        5 B |       0.0782 |            - |            - |         496 B |
| _PrimitiveIntSerialize            | Jil                 |           501.79 ns |     NA |       11 B |       0.0458 |            - |            - |         288 B |
| _PrimitiveIntSerialize            | SpanJson            |           633.02 ns |     NA |       11 B |       0.0062 |            - |            - |          40 B |
| _PrimitiveIntSerialize            | UTF8Json            |           323.87 ns |     NA |       11 B |       0.0062 |            - |            - |          40 B |
| _PrimitiveIntSerialize            | FsPickler           |         1,831.85 ns |     NA |       28 B |       0.1755 |            - |            - |       1,104 B |
| _PrimitiveIntSerialize            | Ceras               |         1,389.47 ns |     NA |        5 B |       0.6599 |            - |            - |       4,152 B |
| _PrimitiveIntSerialize            | OdinSerializer_     |         1,497.75 ns |     NA |        5 B |       0.0038 |            - |            - |          32 B |
| _PrimitiveIntSerialize            | Nino                |           507.27 ns |     NA |        4 B |       0.0048 |            - |            - |          32 B |
| **_PrimitiveLongDeserialize**     | **MessagePack_Lz4** |       **191.03 ns** | **NA** |      **-** |   **0.0100** |        **-** |        **-** |      **64 B** |
| _PrimitiveLongDeserialize         | MessagePack_NoComp  |            94.85 ns |     NA |          - |            - |            - |            - |             - |
| _PrimitiveLongDeserialize         | ProtobufNet         |           366.65 ns |     NA |          - |       0.0138 |            - |            - |          88 B |
| _PrimitiveLongDeserialize         | JsonNet             |         1,111.65 ns |     NA |          - |       0.9079 |       0.0057 |            - |       5,696 B |
| _PrimitiveLongDeserialize         | BinaryFormatter     |         2,593.48 ns |     NA |          - |       0.6561 |       0.0038 |            - |       4,120 B |
| _PrimitiveLongDeserialize         | DataContract        |         1,869.81 ns |     NA |          - |       0.6580 |       0.0057 |            - |       4,136 B |
| _PrimitiveLongDeserialize         | Hyperion            |           106.53 ns |     NA |          - |       0.0305 |            - |            - |         192 B |
| _PrimitiveLongDeserialize         | Jil                 |           136.81 ns |     NA |          - |       0.0253 |            - |            - |         160 B |
| _PrimitiveLongDeserialize         | SpanJson            |            71.58 ns |     NA |          - |            - |            - |            - |             - |
| _PrimitiveLongDeserialize         | UTF8Json            |            61.51 ns |     NA |          - |            - |            - |            - |             - |
| _PrimitiveLongDeserialize         | FsPickler           |           513.67 ns |     NA |          - |       0.1612 |            - |            - |       1,016 B |
| _PrimitiveLongDeserialize         | Ceras               |            91.18 ns |     NA |          - |            - |            - |            - |             - |
| _PrimitiveLongDeserialize         | OdinSerializer_     |           382.98 ns |     NA |          - |            - |            - |            - |             - |
| _PrimitiveLongDeserialize         | Nino                |            99.65 ns |     NA |          - |            - |            - |            - |             - |
| **_PrimitiveLongSerialize**       | **MessagePack_Lz4** |       **458.17 ns** | **NA** |    **9 B** |   **0.0162** |        **-** |        **-** |     **104 B** |
| _PrimitiveLongSerialize           | MessagePack_NoComp  |           218.31 ns |     NA |        9 B |       0.0062 |            - |            - |          40 B |
| _PrimitiveLongSerialize           | ProtobufNet         |           256.52 ns |     NA |       10 B |       0.0610 |            - |            - |         384 B |
| _PrimitiveLongSerialize           | JsonNet             |           838.98 ns |     NA |       22 B |       0.4787 |       0.0038 |            - |       3,008 B |
| _PrimitiveLongSerialize           | BinaryFormatter     |         1,825.85 ns |     NA |       58 B |       0.4883 |       0.0038 |            - |       3,080 B |
| _PrimitiveLongSerialize           | DataContract        |           900.76 ns |     NA |       92 B |       0.2747 |            - |            - |       1,728 B |
| _PrimitiveLongSerialize           | Hyperion            |           244.66 ns |     NA |        9 B |       0.0801 |            - |            - |         504 B |
| _PrimitiveLongSerialize           | Jil                 |           272.82 ns |     NA |       19 B |       0.0663 |            - |            - |         416 B |
| _PrimitiveLongSerialize           | SpanJson            |           214.51 ns |     NA |       19 B |       0.0076 |            - |            - |          48 B |
| _PrimitiveLongSerialize           | UTF8Json            |           117.40 ns |     NA |       19 B |       0.0076 |            - |            - |          48 B |
| _PrimitiveLongSerialize           | FsPickler           |           584.20 ns |     NA |       32 B |       0.1755 |            - |            - |       1,104 B |
| _PrimitiveLongSerialize           | Ceras               |           434.96 ns |     NA |        8 B |       0.6614 |            - |            - |       4,152 B |
| _PrimitiveLongSerialize           | OdinSerializer_     |           405.56 ns |     NA |        9 B |       0.0062 |            - |            - |          40 B |
| _PrimitiveLongSerialize           | Nino                |           150.53 ns |     NA |        8 B |       0.0050 |            - |            - |          32 B |
| **_PrimitiveSByteDeserialize**    | **MessagePack_Lz4** |       **163.35 ns** | **NA** |      **-** |   **0.0100** |        **-** |        **-** |      **64 B** |
| _PrimitiveSByteDeserialize        | MessagePack_NoComp  |            79.37 ns |     NA |          - |            - |            - |            - |             - |
| _PrimitiveSByteDeserialize        | ProtobufNet         |           292.83 ns |     NA |          - |       0.0138 |            - |            - |          88 B |
| _PrimitiveSByteDeserialize        | JsonNet             |           886.23 ns |     NA |          - |       0.9108 |       0.0057 |            - |       5,720 B |
| _PrimitiveSByteDeserialize        | BinaryFormatter     |         2,111.91 ns |     NA |          - |       0.6561 |       0.0038 |            - |       4,120 B |
| _PrimitiveSByteDeserialize        | DataContract        |         1,470.46 ns |     NA |          - |       0.6580 |       0.0057 |            - |       4,136 B |
| _PrimitiveSByteDeserialize        | Hyperion            |            72.53 ns |     NA |          - |       0.0305 |            - |            - |         192 B |
| _PrimitiveSByteDeserialize        | Jil                 |            77.67 ns |     NA |          - |       0.0204 |            - |            - |         128 B |
| _PrimitiveSByteDeserialize        | SpanJson            |            30.20 ns |     NA |          - |            - |            - |            - |             - |
| _PrimitiveSByteDeserialize        | UTF8Json            |            32.34 ns |     NA |          - |            - |            - |            - |             - |
| _PrimitiveSByteDeserialize        | FsPickler           |           454.18 ns |     NA |          - |       0.1616 |       0.0005 |            - |       1,016 B |
| _PrimitiveSByteDeserialize        | Ceras               |            82.68 ns |     NA |          - |            - |            - |            - |             - |
| _PrimitiveSByteDeserialize        | OdinSerializer_     |           334.05 ns |     NA |          - |            - |            - |            - |             - |
| _PrimitiveSByteDeserialize        | Nino                |            88.79 ns |     NA |          - |            - |            - |            - |             - |
| **_PrimitiveSByteSerialize**      | **MessagePack_Lz4** |        **90.83 ns** | **NA** |    **2 B** |   **0.0153** |        **-** |        **-** |      **96 B** |
| _PrimitiveSByteSerialize          | MessagePack_NoComp  |            90.67 ns |     NA |        2 B |       0.0050 |            - |            - |          32 B |
| _PrimitiveSByteSerialize          | ProtobufNet         |           223.00 ns |     NA |       11 B |       0.0610 |            - |            - |         384 B |
| _PrimitiveSByteSerialize          | JsonNet             |           599.03 ns |     NA |        7 B |       0.4768 |       0.0010 |            - |       2,992 B |
| _PrimitiveSByteSerialize          | BinaryFormatter     |         1,410.25 ns |     NA |       51 B |       0.4883 |       0.0038 |            - |       3,072 B |
| _PrimitiveSByteSerialize          | DataContract        |           664.72 ns |     NA |       77 B |       0.2728 |            - |            - |       1,712 B |
| _PrimitiveSByteSerialize          | Hyperion            |           152.05 ns |     NA |        2 B |       0.0789 |            - |            - |         496 B |
| _PrimitiveSByteSerialize          | Jil                 |           120.99 ns |     NA |        4 B |       0.0420 |            - |            - |         264 B |
| _PrimitiveSByteSerialize          | SpanJson            |            70.48 ns |     NA |        4 B |       0.0050 |            - |            - |          32 B |
| _PrimitiveSByteSerialize          | UTF8Json            |            47.41 ns |     NA |        4 B |       0.0051 |            - |            - |          32 B |
| _PrimitiveSByteSerialize          | FsPickler           |           465.54 ns |     NA |       25 B |       0.1760 |            - |            - |       1,104 B |
| _PrimitiveSByteSerialize          | Ceras               |         1,126.48 ns |     NA |        1 B |       0.6614 |            - |            - |       4,152 B |
| _PrimitiveSByteSerialize          | OdinSerializer_     |           331.74 ns |     NA |        2 B |       0.0048 |            - |            - |          32 B |
| _PrimitiveSByteSerialize          | Nino                |           158.76 ns |     NA |        1 B |       0.0050 |            - |            - |          32 B |
| **_PrimitiveShortDeserialize**    | **MessagePack_Lz4** |       **175.02 ns** | **NA** |      **-** |   **0.0100** |        **-** |        **-** |      **64 B** |
| _PrimitiveShortDeserialize        | MessagePack_NoComp  |            79.92 ns |     NA |          - |            - |            - |            - |             - |
| _PrimitiveShortDeserialize        | ProtobufNet         |           334.96 ns |     NA |          - |       0.0138 |            - |            - |          88 B |
| _PrimitiveShortDeserialize        | JsonNet             |           912.80 ns |     NA |          - |       0.9108 |       0.0057 |            - |       5,720 B |
| _PrimitiveShortDeserialize        | BinaryFormatter     |         2,267.03 ns |     NA |          - |       0.6561 |       0.0038 |            - |       4,120 B |
| _PrimitiveShortDeserialize        | DataContract        |         1,482.83 ns |     NA |          - |       0.6580 |       0.0057 |            - |       4,136 B |
| _PrimitiveShortDeserialize        | Hyperion            |            84.74 ns |     NA |          - |       0.0305 |            - |            - |         192 B |
| _PrimitiveShortDeserialize        | Jil                 |            76.02 ns |     NA |          - |       0.0204 |            - |            - |         128 B |
| _PrimitiveShortDeserialize        | SpanJson            |            34.33 ns |     NA |          - |            - |            - |            - |             - |
| _PrimitiveShortDeserialize        | UTF8Json            |            32.36 ns |     NA |          - |            - |            - |            - |             - |
| _PrimitiveShortDeserialize        | FsPickler           |           430.14 ns |     NA |          - |       0.1616 |       0.0005 |            - |       1,016 B |
| _PrimitiveShortDeserialize        | Ceras               |            75.62 ns |     NA |          - |            - |            - |            - |             - |
| _PrimitiveShortDeserialize        | OdinSerializer_     |           310.98 ns |     NA |          - |            - |            - |            - |             - |
| _PrimitiveShortDeserialize        | Nino                |            81.78 ns |     NA |          - |            - |            - |            - |             - |
| **_PrimitiveShortSerialize**      | **MessagePack_Lz4** |       **117.52 ns** | **NA** |    **3 B** |   **0.0153** |        **-** |        **-** |      **96 B** |
| _PrimitiveShortSerialize          | MessagePack_NoComp  |           112.40 ns |     NA |        3 B |       0.0050 |            - |            - |          32 B |
| _PrimitiveShortSerialize          | ProtobufNet         |         1,199.30 ns |     NA |        4 B |       0.0591 |            - |            - |         376 B |
| _PrimitiveShortSerialize          | JsonNet             |         2,141.40 ns |     NA |        8 B |       0.4768 |            - |            - |       2,992 B |
| _PrimitiveShortSerialize          | BinaryFormatter     |         2,133.40 ns |     NA |       52 B |       0.4883 |       0.0038 |            - |       3,072 B |
| _PrimitiveShortSerialize          | DataContract        |         2,307.27 ns |     NA |       80 B |       0.2708 |            - |            - |       1,712 B |
| _PrimitiveShortSerialize          | Hyperion            |           528.66 ns |     NA |        3 B |       0.0782 |            - |            - |         496 B |
| _PrimitiveShortSerialize          | Jil                 |           530.41 ns |     NA |        5 B |       0.0420 |            - |            - |         264 B |
| _PrimitiveShortSerialize          | SpanJson            |           356.18 ns |     NA |        5 B |       0.0048 |            - |            - |          32 B |
| _PrimitiveShortSerialize          | UTF8Json            |           216.68 ns |     NA |        5 B |       0.0050 |            - |            - |          32 B |
| _PrimitiveShortSerialize          | FsPickler           |         1,829.01 ns |     NA |       26 B |       0.1755 |            - |            - |       1,104 B |
| _PrimitiveShortSerialize          | Ceras               |         1,756.26 ns |     NA |        2 B |       0.6599 |            - |            - |       4,152 B |
| _PrimitiveShortSerialize          | OdinSerializer_     |         1,209.57 ns |     NA |        3 B |       0.0038 |            - |            - |          32 B |
| _PrimitiveShortSerialize          | Nino                |           573.01 ns |     NA |        2 B |       0.0048 |            - |            - |          32 B |
| **_PrimitiveStringDeserialize**   | **MessagePack_Lz4** |       **702.68 ns** | **NA** |      **-** |   **0.0458** |        **-** |        **-** |     **288 B** |
| _PrimitiveStringDeserialize       | MessagePack_NoComp  |           173.30 ns |     NA |          - |       0.0355 |            - |            - |         224 B |
| _PrimitiveStringDeserialize       | ProtobufNet         |           411.17 ns |     NA |          - |       0.0496 |            - |            - |         312 B |
| _PrimitiveStringDeserialize       | JsonNet             |         1,060.70 ns |     NA |          - |       0.9384 |       0.0114 |            - |       5,896 B |
| _PrimitiveStringDeserialize       | BinaryFormatter     |           807.73 ns |     NA |          - |       0.4072 |       0.0029 |            - |       2,560 B |
| _PrimitiveStringDeserialize       | DataContract        |         1,922.67 ns |     NA |          - |       0.7401 |       0.0076 |            - |       4,664 B |
| _PrimitiveStringDeserialize       | Hyperion            |           166.16 ns |     NA |          - |       0.0827 |            - |            - |         520 B |
| _PrimitiveStringDeserialize       | Jil                 |           420.00 ns |     NA |          - |       0.1326 |            - |            - |         832 B |
| _PrimitiveStringDeserialize       | SpanJson            |           184.15 ns |     NA |          - |       0.0355 |            - |            - |         224 B |
| _PrimitiveStringDeserialize       | UTF8Json            |           336.03 ns |     NA |          - |       0.0353 |            - |            - |         224 B |
| _PrimitiveStringDeserialize       | FsPickler           |           556.87 ns |     NA |          - |       0.1974 |            - |            - |       1,240 B |
| _PrimitiveStringDeserialize       | Ceras               |           168.98 ns |     NA |          - |       0.0355 |            - |            - |         224 B |
| _PrimitiveStringDeserialize       | OdinSerializer_     |           398.28 ns |     NA |          - |       0.0353 |            - |            - |         224 B |
| _PrimitiveStringDeserialize       | Nino                |           128.60 ns |     NA |          - |       0.0355 |            - |            - |         224 B |
| **_PrimitiveStringSerialize**     | **MessagePack_Lz4** |       **820.00 ns** | **NA** |   **21 B** |   **0.0172** |        **-** |        **-** |     **112 B** |
| _PrimitiveStringSerialize         | MessagePack_NoComp  |           189.62 ns |     NA |      102 B |       0.0203 |            - |            - |         128 B |
| _PrimitiveStringSerialize         | ProtobufNet         |           383.55 ns |     NA |      102 B |       0.0749 |            - |            - |         472 B |
| _PrimitiveStringSerialize         | JsonNet             |           813.87 ns |     NA |      105 B |       0.4892 |            - |            - |       3,072 B |
| _PrimitiveStringSerialize         | BinaryFormatter     |         1,115.42 ns |     NA |      124 B |       0.3910 |       0.0019 |            - |       2,464 B |
| _PrimitiveStringSerialize         | DataContract        |         1,060.32 ns |     NA |      177 B |       0.2842 |            - |            - |       1,792 B |
| _PrimitiveStringSerialize         | Hyperion            |           289.40 ns |     NA |      102 B |       0.1106 |            - |            - |         696 B |
| _PrimitiveStringSerialize         | Jil                 |           799.98 ns |     NA |      102 B |       0.1440 |            - |            - |         904 B |
| _PrimitiveStringSerialize         | SpanJson            |           325.26 ns |     NA |      102 B |       0.0200 |            - |            - |         128 B |
| _PrimitiveStringSerialize         | UTF8Json            |           201.56 ns |     NA |      102 B |       0.0203 |            - |            - |         128 B |
| _PrimitiveStringSerialize         | FsPickler           |           685.57 ns |     NA |      127 B |       0.1907 |            - |            - |       1,200 B |
| _PrimitiveStringSerialize         | Ceras               |           429.81 ns |     NA |      101 B |       0.6766 |            - |            - |       4,248 B |
| _PrimitiveStringSerialize         | OdinSerializer_     |           412.05 ns |     NA |      206 B |       0.0367 |            - |            - |         232 B |
| _PrimitiveStringSerialize         | Nino                |           211.95 ns |     NA |      205 B |       0.0370 |            - |            - |         232 B |
| **_PrimitiveUIntDeserialize**     | **MessagePack_Lz4** |       **157.59 ns** | **NA** |      **-** |   **0.0100** |        **-** |        **-** |      **64 B** |
| _PrimitiveUIntDeserialize         | MessagePack_NoComp  |            73.96 ns |     NA |          - |            - |            - |            - |             - |
| _PrimitiveUIntDeserialize         | ProtobufNet         |           328.39 ns |     NA |          - |       0.0138 |            - |            - |          88 B |
| _PrimitiveUIntDeserialize         | JsonNet             |           930.75 ns |     NA |          - |       0.9079 |       0.0067 |            - |       5,696 B |
| _PrimitiveUIntDeserialize         | BinaryFormatter     |         2,540.67 ns |     NA |          - |       0.6561 |       0.0038 |            - |       4,120 B |
| _PrimitiveUIntDeserialize         | DataContract        |         1,867.91 ns |     NA |          - |       0.6580 |       0.0057 |            - |       4,136 B |
| _PrimitiveUIntDeserialize         | Hyperion            |           111.25 ns |     NA |          - |       0.0305 |            - |            - |         192 B |
| _PrimitiveUIntDeserialize         | Jil                 |            85.69 ns |     NA |          - |       0.0191 |            - |            - |         120 B |
| _PrimitiveUIntDeserialize         | SpanJson            |            23.02 ns |     NA |          - |            - |            - |            - |             - |
| _PrimitiveUIntDeserialize         | UTF8Json            |            30.73 ns |     NA |          - |            - |            - |            - |             - |
| _PrimitiveUIntDeserialize         | FsPickler           |           535.68 ns |     NA |          - |       0.1612 |            - |            - |       1,016 B |
| _PrimitiveUIntDeserialize         | Ceras               |            93.66 ns |     NA |          - |            - |            - |            - |             - |
| _PrimitiveUIntDeserialize         | OdinSerializer_     |           401.44 ns |     NA |          - |            - |            - |            - |             - |
| _PrimitiveUIntDeserialize         | Nino                |           100.82 ns |     NA |          - |            - |            - |            - |             - |
| **_PrimitiveUIntSerialize**       | **MessagePack_Lz4** |       **111.23 ns** | **NA** |    **1 B** |   **0.0153** |        **-** |        **-** |      **96 B** |
| _PrimitiveUIntSerialize           | MessagePack_NoComp  |            89.36 ns |     NA |        1 B |       0.0050 |            - |            - |          32 B |
| _PrimitiveUIntSerialize           | ProtobufNet         |           257.15 ns |     NA |        2 B |       0.0596 |            - |            - |         376 B |
| _PrimitiveUIntSerialize           | JsonNet             |           564.64 ns |     NA |        4 B |       0.4616 |       0.0019 |            - |       2,896 B |
| _PrimitiveUIntSerialize           | BinaryFormatter     |         1,993.48 ns |     NA |       55 B |       0.4883 |       0.0019 |            - |       3,072 B |
| _PrimitiveUIntSerialize           | DataContract        |           854.89 ns |     NA |       88 B |       0.2737 |            - |            - |       1,720 B |
| _PrimitiveUIntSerialize           | Hyperion            |           216.72 ns |     NA |        5 B |       0.0789 |            - |            - |         496 B |
| _PrimitiveUIntSerialize           | Jil                 |           117.08 ns |     NA |        1 B |       0.0408 |            - |            - |         256 B |
| _PrimitiveUIntSerialize           | SpanJson            |            58.77 ns |     NA |        1 B |       0.0050 |            - |            - |          32 B |
| _PrimitiveUIntSerialize           | UTF8Json            |            42.10 ns |     NA |        1 B |       0.0051 |            - |            - |          32 B |
| _PrimitiveUIntSerialize           | FsPickler           |           522.95 ns |     NA |       29 B |       0.1755 |            - |            - |       1,104 B |
| _PrimitiveUIntSerialize           | Ceras               |           361.05 ns |     NA |        1 B |       0.6614 |            - |            - |       4,152 B |
| _PrimitiveUIntSerialize           | OdinSerializer_     |           366.13 ns |     NA |        5 B |       0.0048 |            - |            - |          32 B |
| _PrimitiveUIntSerialize           | Nino                |           156.86 ns |     NA |        4 B |       0.0050 |            - |            - |          32 B |
| **_PrimitiveULongDeserialize**    | **MessagePack_Lz4** |       **176.60 ns** | **NA** |      **-** |   **0.0100** |        **-** |        **-** |      **64 B** |
| _PrimitiveULongDeserialize        | MessagePack_NoComp  |            85.79 ns |     NA |          - |            - |            - |            - |             - |
| _PrimitiveULongDeserialize        | ProtobufNet         |           368.50 ns |     NA |          - |       0.0138 |            - |            - |          88 B |
| _PrimitiveULongDeserialize        | JsonNet             |         1,520.99 ns |     NA |          - |       0.9613 |       0.0114 |            - |       6,032 B |
| _PrimitiveULongDeserialize        | BinaryFormatter     |         2,263.35 ns |     NA |          - |       0.6561 |       0.0038 |            - |       4,120 B |
| _PrimitiveULongDeserialize        | DataContract        |         1,886.31 ns |     NA |          - |       0.6790 |       0.0076 |            - |       4,264 B |
| _PrimitiveULongDeserialize        | Hyperion            |            96.14 ns |     NA |          - |       0.0305 |            - |            - |         192 B |
| _PrimitiveULongDeserialize        | Jil                 |           130.89 ns |     NA |          - |       0.0253 |            - |            - |         160 B |
| _PrimitiveULongDeserialize        | SpanJson            |            67.90 ns |     NA |          - |            - |            - |            - |             - |
| _PrimitiveULongDeserialize        | UTF8Json            |            68.53 ns |     NA |          - |            - |            - |            - |             - |
| _PrimitiveULongDeserialize        | FsPickler           |           675.51 ns |     NA |          - |       0.1612 |            - |            - |       1,016 B |
| _PrimitiveULongDeserialize        | Ceras               |           135.51 ns |     NA |          - |            - |            - |            - |             - |
| _PrimitiveULongDeserialize        | OdinSerializer_     |           607.82 ns |     NA |          - |            - |            - |            - |             - |
| _PrimitiveULongDeserialize        | Nino                |           176.12 ns |     NA |          - |            - |            - |            - |             - |
| **_PrimitiveULongSerialize**      | **MessagePack_Lz4** |       **118.16 ns** | **NA** |    **9 B** |   **0.0166** |        **-** |        **-** |     **104 B** |
| _PrimitiveULongSerialize          | MessagePack_NoComp  |            86.94 ns |     NA |        9 B |       0.0063 |            - |            - |          40 B |
| _PrimitiveULongSerialize          | ProtobufNet         |           252.73 ns |     NA |       11 B |       0.0610 |            - |            - |         384 B |
| _PrimitiveULongSerialize          | JsonNet             |           614.58 ns |     NA |       23 B |       0.4787 |       0.0038 |            - |       3,008 B |
| _PrimitiveULongSerialize          | BinaryFormatter     |         1,487.88 ns |     NA |       59 B |       0.4902 |       0.0038 |            - |       3,080 B |
| _PrimitiveULongSerialize          | DataContract        |           803.46 ns |     NA |      109 B |       0.2880 |            - |            - |       1,808 B |
| _PrimitiveULongSerialize          | Hyperion            |           201.69 ns |     NA |        9 B |       0.0801 |            - |            - |         504 B |
| _PrimitiveULongSerialize          | Jil                 |           217.15 ns |     NA |       20 B |       0.0663 |            - |            - |         416 B |
| _PrimitiveULongSerialize          | SpanJson            |           107.84 ns |     NA |       20 B |       0.0076 |            - |            - |          48 B |
| _PrimitiveULongSerialize          | UTF8Json            |           102.53 ns |     NA |       20 B |       0.0076 |            - |            - |          48 B |
| _PrimitiveULongSerialize          | FsPickler           |           506.13 ns |     NA |       33 B |       0.1764 |            - |            - |       1,112 B |
| _PrimitiveULongSerialize          | Ceras               |           525.88 ns |     NA |        8 B |       0.6614 |            - |            - |       4,152 B |
| _PrimitiveULongSerialize          | OdinSerializer_     |           419.36 ns |     NA |        9 B |       0.0062 |            - |            - |          40 B |
| _PrimitiveULongSerialize          | Nino                |           195.84 ns |     NA |        8 B |       0.0050 |            - |            - |          32 B |
| **_PrimitiveUShortDeserialize**   | **MessagePack_Lz4** |       **185.75 ns** | **NA** |      **-** |   **0.0100** |        **-** |        **-** |      **64 B** |
| _PrimitiveUShortDeserialize       | MessagePack_NoComp  |            86.30 ns |     NA |          - |            - |            - |            - |             - |
| _PrimitiveUShortDeserialize       | ProtobufNet         |           337.07 ns |     NA |          - |       0.0138 |            - |            - |          88 B |
| _PrimitiveUShortDeserialize       | JsonNet             |           995.58 ns |     NA |          - |       0.9108 |       0.0057 |            - |       5,720 B |
| _PrimitiveUShortDeserialize       | BinaryFormatter     |         2,415.21 ns |     NA |          - |       0.6561 |       0.0038 |            - |       4,120 B |
| _PrimitiveUShortDeserialize       | DataContract        |         1,612.51 ns |     NA |          - |       0.6580 |       0.0057 |            - |       4,136 B |
| _PrimitiveUShortDeserialize       | Hyperion            |            88.46 ns |     NA |          - |       0.0305 |            - |            - |         192 B |
| _PrimitiveUShortDeserialize       | Jil                 |            82.67 ns |     NA |          - |       0.0204 |            - |            - |         128 B |
| _PrimitiveUShortDeserialize       | SpanJson            |            36.12 ns |     NA |          - |            - |            - |            - |             - |
| _PrimitiveUShortDeserialize       | UTF8Json            |            35.30 ns |     NA |          - |            - |            - |            - |             - |
| _PrimitiveUShortDeserialize       | FsPickler           |           500.83 ns |     NA |          - |       0.1612 |            - |            - |       1,016 B |
| _PrimitiveUShortDeserialize       | Ceras               |            83.13 ns |     NA |          - |            - |            - |            - |             - |
| _PrimitiveUShortDeserialize       | OdinSerializer_     |           375.82 ns |     NA |          - |            - |            - |            - |             - |
| _PrimitiveUShortDeserialize       | Nino                |            94.70 ns |     NA |          - |            - |            - |            - |             - |
| **_PrimitiveUShortSerialize**     | **MessagePack_Lz4** |       **123.93 ns** | **NA** |    **3 B** |   **0.0153** |        **-** |        **-** |      **96 B** |
| _PrimitiveUShortSerialize         | MessagePack_NoComp  |           124.18 ns |     NA |        3 B |       0.0050 |            - |            - |          32 B |
| _PrimitiveUShortSerialize         | ProtobufNet         |           375.15 ns |     NA |        4 B |       0.0591 |            - |            - |         376 B |
| _PrimitiveUShortSerialize         | JsonNet             |           567.28 ns |     NA |        8 B |       0.4768 |       0.0010 |            - |       2,992 B |
| _PrimitiveUShortSerialize         | BinaryFormatter     |         2,202.36 ns |     NA |       53 B |       0.4883 |       0.0038 |            - |       3,072 B |
| _PrimitiveUShortSerialize         | DataContract        |           877.34 ns |     NA |       96 B |       0.2747 |            - |            - |       1,728 B |
| _PrimitiveUShortSerialize         | Hyperion            |           224.12 ns |     NA |        3 B |       0.0789 |            - |            - |         496 B |
| _PrimitiveUShortSerialize         | Jil                 |           116.58 ns |     NA |        5 B |       0.0420 |            - |            - |         264 B |
| _PrimitiveUShortSerialize         | SpanJson            |            75.19 ns |     NA |        5 B |       0.0050 |            - |            - |          32 B |
| _PrimitiveUShortSerialize         | UTF8Json            |            52.63 ns |     NA |        5 B |       0.0051 |            - |            - |          32 B |
| _PrimitiveUShortSerialize         | FsPickler           |           504.66 ns |     NA |       27 B |       0.1755 |            - |            - |       1,104 B |
| _PrimitiveUShortSerialize         | Ceras               |           358.84 ns |     NA |        2 B |       0.6614 |            - |            - |       4,152 B |
| _PrimitiveUShortSerialize         | OdinSerializer_     |           404.87 ns |     NA |        3 B |       0.0048 |            - |            - |          32 B |
| _PrimitiveUShortSerialize         | Nino                |           130.35 ns |     NA |        2 B |       0.0050 |            - |            - |          32 B |
| **AccessTokenDeserialize**        | **MessagePack_Lz4** |       **316.43 ns** | **NA** |      **-** |   **0.0176** |        **-** |        **-** |     **112 B** |
| AccessTokenDeserialize            | MessagePack_NoComp  |           221.01 ns |     NA |          - |       0.0076 |            - |            - |          48 B |
| AccessTokenDeserialize            | ProtobufNet         |           457.79 ns |     NA |          - |       0.0215 |            - |            - |         136 B |
| AccessTokenDeserialize            | JsonNet             |         2,294.51 ns |     NA |          - |       0.9193 |       0.0076 |            - |       5,768 B |
| AccessTokenDeserialize            | BinaryFormatter     |         3,523.19 ns |     NA |          - |       0.8316 |       0.0076 |            - |       5,240 B |
| AccessTokenDeserialize            | DataContract        |         4,765.78 ns |     NA |          - |       1.3733 |       0.0229 |            - |       8,632 B |
| AccessTokenDeserialize            | Hyperion            |           413.93 ns |     NA |          - |       0.0710 |            - |            - |         448 B |
| AccessTokenDeserialize            | Jil                 |           425.25 ns |     NA |          - |       0.0520 |            - |            - |         328 B |
| AccessTokenDeserialize            | SpanJson            |           168.87 ns |     NA |          - |       0.0076 |            - |            - |          48 B |
| AccessTokenDeserialize            | UTF8Json            |           348.10 ns |     NA |          - |       0.0076 |            - |            - |          48 B |
| AccessTokenDeserialize            | FsPickler           |           592.29 ns |     NA |          - |       0.1974 |            - |            - |       1,240 B |
| AccessTokenDeserialize            | Ceras               |           250.57 ns |     NA |          - |       0.0076 |            - |            - |          48 B |
| AccessTokenDeserialize            | OdinSerializer_     |         2,443.11 ns |     NA |          - |       0.0992 |            - |            - |         632 B |
| AccessTokenDeserialize            | Nino                |           123.04 ns |     NA |          - |       0.0076 |            - |            - |          48 B |
| **AccessTokenSerialize**          | **MessagePack_Lz4** |       **456.46 ns** | **NA** |   **19 B** |   **0.0176** |        **-** |        **-** |     **112 B** |
| AccessTokenSerialize              | MessagePack_NoComp  |           165.94 ns |     NA |       19 B |       0.0076 |            - |            - |          48 B |
| AccessTokenSerialize              | ProtobufNet         |           381.50 ns |     NA |        6 B |       0.0596 |            - |            - |         376 B |
| AccessTokenSerialize              | JsonNet             |         1,223.19 ns |     NA |       82 B |       0.5016 |       0.0019 |            - |       3,152 B |
| AccessTokenSerialize              | BinaryFormatter     |         3,770.73 ns |     NA |      392 B |       0.7782 |       0.0076 |            - |       4,888 B |
| AccessTokenSerialize              | DataContract        |         1,964.62 ns |     NA |      333 B |       0.4234 |            - |            - |       2,680 B |
| AccessTokenSerialize              | Hyperion            |           292.70 ns |     NA |       69 B |       0.1044 |            - |            - |         656 B |
| AccessTokenSerialize              | Jil                 |           658.46 ns |     NA |       80 B |       0.1478 |            - |            - |         928 B |
| AccessTokenSerialize              | SpanJson            |           139.64 ns |     NA |       53 B |       0.0126 |            - |            - |          80 B |
| AccessTokenSerialize              | UTF8Json            |           232.84 ns |     NA |       79 B |       0.0165 |            - |            - |         104 B |
| AccessTokenSerialize              | FsPickler           |           640.62 ns |     NA |       67 B |       0.1907 |            - |            - |       1,200 B |
| AccessTokenSerialize              | Ceras               |           449.39 ns |     NA |       12 B |       0.6628 |            - |            - |       4,160 B |
| AccessTokenSerialize              | OdinSerializer_     |         1,787.99 ns |     NA |      440 B |       0.0801 |            - |            - |         512 B |
| AccessTokenSerialize              | Nino                |           363.26 ns |     NA |       15 B |       0.0062 |            - |            - |          40 B |
| **AccountMergeDeserialize**       | **MessagePack_Lz4** |       **292.78 ns** | **NA** |      **-** |   **0.0153** |        **-** |        **-** |      **96 B** |
| AccountMergeDeserialize           | MessagePack_NoComp  |           183.28 ns |     NA |          - |       0.0050 |            - |            - |          32 B |
| AccountMergeDeserialize           | ProtobufNet         |           509.81 ns |     NA |          - |       0.0191 |            - |            - |         120 B |
| AccountMergeDeserialize           | JsonNet             |         2,410.99 ns |     NA |          - |       0.9155 |       0.0076 |            - |       5,752 B |
| AccountMergeDeserialize           | BinaryFormatter     |         4,045.96 ns |     NA |          - |       0.7706 |       0.0076 |            - |       4,848 B |
| AccountMergeDeserialize           | DataContract        |         4,617.67 ns |     NA |          - |       1.9913 |       0.0534 |            - |      12,536 B |
| AccountMergeDeserialize           | Hyperion            |           452.35 ns |     NA |          - |       0.0687 |            - |            - |         432 B |
| AccountMergeDeserialize           | Jil                 |           415.74 ns |     NA |          - |       0.0467 |            - |            - |         296 B |
| AccountMergeDeserialize           | SpanJson            |           245.11 ns |     NA |          - |       0.0048 |            - |            - |          32 B |
| AccountMergeDeserialize           | UTF8Json            |           369.16 ns |     NA |          - |       0.0048 |            - |            - |          32 B |
| AccountMergeDeserialize           | FsPickler           |           726.89 ns |     NA |          - |       0.1955 |            - |            - |       1,232 B |
| AccountMergeDeserialize           | Ceras               |           285.84 ns |     NA |          - |       0.0048 |            - |            - |          32 B |
| AccountMergeDeserialize           | OdinSerializer_     |         2,342.40 ns |     NA |          - |       0.0916 |            - |            - |         576 B |
| AccountMergeDeserialize           | Nino                |           129.25 ns |     NA |          - |       0.0050 |            - |            - |          32 B |
| **AccountMergeSerialize**         | **MessagePack_Lz4** |       **390.86 ns** | **NA** |   **18 B** |   **0.0176** |        **-** |        **-** |     **112 B** |
| AccountMergeSerialize             | MessagePack_NoComp  |           130.24 ns |     NA |       18 B |       0.0076 |            - |            - |          48 B |
| AccountMergeSerialize             | ProtobufNet         |           384.64 ns |     NA |        6 B |       0.0596 |            - |            - |         376 B |
| AccountMergeSerialize             | JsonNet             |         1,073.55 ns |     NA |       72 B |       0.5035 |       0.0019 |            - |       3,160 B |
| AccountMergeSerialize             | BinaryFormatter     |         2,546.03 ns |     NA |      250 B |       0.6142 |       0.0038 |            - |       3,872 B |
| AccountMergeSerialize             | DataContract        |         1,467.83 ns |     NA |      253 B |       0.3929 |       0.0019 |            - |       2,472 B |
| AccountMergeSerialize             | Hyperion            |           256.34 ns |     NA |       72 B |       0.0992 |            - |            - |         624 B |
| AccountMergeSerialize             | Jil                 |           582.03 ns |     NA |       70 B |       0.1144 |            - |            - |         720 B |
| AccountMergeSerialize             | SpanJson            |           213.05 ns |     NA |       69 B |       0.0153 |            - |            - |          96 B |
| AccountMergeSerialize             | UTF8Json            |           292.99 ns |     NA |       69 B |       0.0153 |            - |            - |          96 B |
| AccountMergeSerialize             | FsPickler           |         1,148.42 ns |     NA |       67 B |       0.1907 |            - |            - |       1,200 B |
| AccountMergeSerialize             | Ceras               |           827.40 ns |     NA |       11 B |       0.6628 |            - |            - |       4,160 B |
| AccountMergeSerialize             | OdinSerializer_     |         3,250.38 ns |     NA |      408 B |       0.0801 |            - |            - |         504 B |
| AccountMergeSerialize             | Nino                |           725.92 ns |     NA |       17 B |       0.0076 |            - |            - |          48 B |
| **AnswerDeserialize**             | **MessagePack_Lz4** |     **1,326.61 ns** | **NA** |      **-** |   **0.0324** |        **-** |        **-** |     **208 B** |
| AnswerDeserialize                 | MessagePack_NoComp  |           703.88 ns |     NA |          - |       0.0229 |            - |            - |         144 B |
| AnswerDeserialize                 | ProtobufNet         |           936.46 ns |     NA |          - |       0.0362 |            - |            - |         232 B |
| AnswerDeserialize                 | JsonNet             |        10,047.23 ns |     NA |          - |       0.9460 |            - |            - |       6,056 B |
| AnswerDeserialize                 | BinaryFormatter     |        11,984.01 ns |     NA |          - |       1.3885 |       0.0153 |            - |       8,784 B |
| AnswerDeserialize                 | DataContract        |        13,221.88 ns |     NA |          - |       2.1210 |       0.0305 |            - |      13,392 B |
| AnswerDeserialize                 | Hyperion            |           662.89 ns |     NA |          - |       0.0849 |            - |            - |         536 B |
| AnswerDeserialize                 | Jil                 |         2,622.56 ns |     NA |          - |       0.1869 |            - |            - |       1,184 B |
| AnswerDeserialize                 | SpanJson            |           978.31 ns |     NA |          - |       0.0229 |            - |            - |         144 B |
| AnswerDeserialize                 | UTF8Json            |         1,991.88 ns |     NA |          - |       0.0229 |            - |            - |         144 B |
| AnswerDeserialize                 | FsPickler           |           987.08 ns |     NA |          - |       0.2117 |            - |            - |       1,328 B |
| AnswerDeserialize                 | Ceras               |           423.92 ns |     NA |          - |       0.0229 |            - |            - |         144 B |
| AnswerDeserialize                 | OdinSerializer_     |         8,844.30 ns |     NA |          - |       0.3815 |            - |            - |       2,416 B |
| AnswerDeserialize                 | Nino                |           237.18 ns |     NA |          - |       0.0229 |            - |            - |         144 B |
| **AnswerSerialize**               | **MessagePack_Lz4** |     **1,591.24 ns** | **NA** |   **53 B** |   **0.0229** |        **-** |        **-** |     **144 B** |
| AnswerSerialize                   | MessagePack_NoComp  |           685.54 ns |     NA |       97 B |       0.0200 |            - |            - |         128 B |
| AnswerSerialize                   | ProtobufNet         |         1,112.38 ns |     NA |       30 B |       0.0629 |            - |            - |         400 B |
| AnswerSerialize                   | JsonNet             |         6,465.29 ns |     NA |      458 B |       1.1902 |       0.0076 |            - |       7,480 B |
| AnswerSerialize                   | BinaryFormatter     |        12,973.97 ns |     NA |     1117 B |       1.6785 |       0.0458 |            - |      10,552 B |
| AnswerSerialize                   | DataContract        |         6,201.87 ns |     NA |      883 B |       0.9155 |       0.0076 |            - |       5,768 B |
| AnswerSerialize                   | Hyperion            |           708.94 ns |     NA |      129 B |       0.1345 |            - |            - |         848 B |
| AnswerSerialize                   | Jil                 |         2,866.13 ns |     NA |      460 B |       0.4730 |            - |            - |       2,984 B |
| AnswerSerialize                   | SpanJson            |           659.48 ns |     NA |      353 B |       0.0610 |            - |            - |         384 B |
| AnswerSerialize                   | UTF8Json            |         1,181.20 ns |     NA |      455 B |       0.0763 |            - |            - |         480 B |
| AnswerSerialize                   | FsPickler           |         1,177.14 ns |     NA |      130 B |       0.2003 |            - |            - |       1,264 B |
| AnswerSerialize                   | Ceras               |           582.16 ns |     NA |       58 B |       0.6704 |            - |            - |       4,208 B |
| AnswerSerialize                   | OdinSerializer_     |         5,573.59 ns |     NA |     1584 B |       0.3128 |            - |            - |       1,968 B |
| AnswerSerialize                   | Nino                |         1,249.43 ns |     NA |       76 B |       0.0153 |            - |            - |         104 B |
| **BadgeDeserialize**              | **MessagePack_Lz4** |       **395.57 ns** | **NA** |      **-** |   **0.0176** |        **-** |        **-** |     **112 B** |
| BadgeDeserialize                  | MessagePack_NoComp  |           250.70 ns |     NA |          - |       0.0076 |            - |            - |          48 B |
| BadgeDeserialize                  | ProtobufNet         |           340.47 ns |     NA |          - |       0.0215 |            - |            - |         136 B |
| BadgeDeserialize                  | JsonNet             |         2,501.09 ns |     NA |          - |       0.9079 |       0.0038 |            - |       5,720 B |
| BadgeDeserialize                  | BinaryFormatter     |         3,852.13 ns |     NA |          - |       0.8011 |       0.0076 |            - |       5,072 B |
| BadgeDeserialize                  | DataContract        |         5,511.62 ns |     NA |          - |       1.3351 |       0.0076 |            - |       8,400 B |
| BadgeDeserialize                  | Hyperion            |           413.71 ns |     NA |          - |       0.0701 |            - |            - |         440 B |
| BadgeDeserialize                  | Jil                 |           308.57 ns |     NA |          - |       0.0496 |            - |            - |         312 B |
| BadgeDeserialize                  | SpanJson            |           117.73 ns |     NA |          - |       0.0076 |            - |            - |          48 B |
| BadgeDeserialize                  | UTF8Json            |           293.01 ns |     NA |          - |       0.0076 |            - |            - |          48 B |
| BadgeDeserialize                  | FsPickler           |           648.55 ns |     NA |          - |       0.1955 |            - |            - |       1,232 B |
| BadgeDeserialize                  | Ceras               |           293.78 ns |     NA |          - |       0.0076 |            - |            - |          48 B |
| BadgeDeserialize                  | OdinSerializer_     |         2,532.88 ns |     NA |          - |       0.0877 |            - |            - |         568 B |
| BadgeDeserialize                  | Nino                |           132.14 ns |     NA |          - |       0.0076 |            - |            - |          48 B |
| **BadgeSerialize**                | **MessagePack_Lz4** |       **478.37 ns** | **NA** |    **9 B** |   **0.0162** |        **-** |        **-** |     **104 B** |
| BadgeSerialize                    | MessagePack_NoComp  |           174.82 ns |     NA |        9 B |       0.0062 |            - |            - |          40 B |
| BadgeSerialize                    | ProtobufNet         |           231.86 ns |     NA |        0 B |       0.0100 |            - |            - |          64 B |
| BadgeSerialize                    | JsonNet             |         1,262.12 ns |     NA |       74 B |       0.4845 |       0.0019 |            - |       3,048 B |
| BadgeSerialize                    | BinaryFormatter     |         2,943.65 ns |     NA |      278 B |       0.7172 |       0.0076 |            - |       4,504 B |
| BadgeSerialize                    | DataContract        |         1,702.01 ns |     NA |      250 B |       0.3376 |            - |            - |       2,120 B |
| BadgeSerialize                    | Hyperion            |           314.61 ns |     NA |       59 B |       0.1135 |            - |            - |         712 B |
| BadgeSerialize                    | Jil                 |           502.98 ns |     NA |       71 B |       0.1440 |            - |            - |         904 B |
| BadgeSerialize                    | SpanJson            |            99.93 ns |     NA |       28 B |       0.0088 |            - |            - |          56 B |
| BadgeSerialize                    | UTF8Json            |           149.48 ns |     NA |       71 B |       0.0153 |            - |            - |          96 B |
| BadgeSerialize                    | FsPickler           |           798.78 ns |     NA |       54 B |       0.1879 |            - |            - |       1,184 B |
| BadgeSerialize                    | Ceras               |           582.07 ns |     NA |        6 B |       0.6609 |            - |            - |       4,152 B |
| BadgeSerialize                    | OdinSerializer_     |         1,886.66 ns |     NA |      382 B |       0.0725 |            - |            - |         456 B |
| BadgeSerialize                    | Nino                |           435.72 ns |     NA |       12 B |       0.0062 |            - |            - |          40 B |
| **CommentDeserialize**            | **MessagePack_Lz4** |       **486.44 ns** | **NA** |      **-** |   **0.0200** |        **-** |        **-** |     **128 B** |
| CommentDeserialize                | MessagePack_NoComp  |           323.48 ns |     NA |          - |       0.0100 |            - |            - |          64 B |
| CommentDeserialize                | ProtobufNet         |           696.60 ns |     NA |          - |       0.0238 |            - |            - |         152 B |
| CommentDeserialize                | JsonNet             |         4,598.12 ns |     NA |          - |       0.9155 |            - |            - |       5,784 B |
| CommentDeserialize                | BinaryFormatter     |         6,282.90 ns |     NA |          - |       0.9232 |       0.0076 |            - |       5,832 B |
| CommentDeserialize                | DataContract        |         8,445.41 ns |     NA |          - |       2.0142 |       0.0458 |            - |      12,728 B |
| CommentDeserialize                | Hyperion            |           552.15 ns |     NA |          - |       0.0725 |            - |            - |         456 B |
| CommentDeserialize                | Jil                 |           825.58 ns |     NA |          - |       0.0763 |            - |            - |         480 B |
| CommentDeserialize                | SpanJson            |           385.60 ns |     NA |          - |       0.0100 |            - |            - |          64 B |
| CommentDeserialize                | UTF8Json            |           773.54 ns |     NA |          - |       0.0095 |            - |            - |          64 B |
| CommentDeserialize                | FsPickler           |           821.49 ns |     NA |          - |       0.1984 |            - |            - |       1,248 B |
| CommentDeserialize                | Ceras               |           375.46 ns |     NA |          - |       0.0100 |            - |            - |          64 B |
| CommentDeserialize                | OdinSerializer_     |         5,035.95 ns |     NA |          - |       0.1678 |            - |            - |       1,080 B |
| CommentDeserialize                | Nino                |           186.19 ns |     NA |          - |       0.0100 |            - |            - |          64 B |
| **CommentSerialize**              | **MessagePack_Lz4** |       **611.46 ns** | **NA** |   **27 B** |   **0.0191** |        **-** |        **-** |     **120 B** |
| CommentSerialize                  | MessagePack_NoComp  |           271.17 ns |     NA |       27 B |       0.0086 |            - |            - |          56 B |
| CommentSerialize                  | ProtobufNet         |           483.57 ns |     NA |        6 B |       0.0591 |            - |            - |         376 B |
| CommentSerialize                  | JsonNet             |         2,185.71 ns |     NA |      151 B |       0.5264 |       0.0038 |            - |       3,312 B |
| CommentSerialize                  | BinaryFormatter     |         5,169.11 ns |     NA |      403 B |       0.7935 |       0.0076 |            - |       5,016 B |
| CommentSerialize                  | DataContract        |         2,733.44 ns |     NA |      361 B |       0.4272 |            - |            - |       2,696 B |
| CommentSerialize                  | Hyperion            |           385.13 ns |     NA |       76 B |       0.1159 |            - |            - |         728 B |
| CommentSerialize                  | Jil                 |           904.00 ns |     NA |      149 B |       0.1898 |            - |            - |       1,192 B |
| CommentSerialize                  | SpanJson            |           203.51 ns |     NA |      104 B |       0.0203 |            - |            - |         128 B |
| CommentSerialize                  | UTF8Json            |           368.54 ns |     NA |      148 B |       0.0277 |            - |            - |         176 B |
| CommentSerialize                  | FsPickler           |         1,231.04 ns |     NA |       71 B |       0.1907 |            - |            - |       1,200 B |
| CommentSerialize                  | Ceras               |           807.36 ns |     NA |       17 B |       0.6638 |            - |            - |       4,168 B |
| CommentSerialize                  | OdinSerializer_     |         4,542.29 ns |     NA |      708 B |       0.1373 |            - |            - |         880 B |
| CommentSerialize                  | Nino                |         1,276.17 ns |     NA |       26 B |       0.0076 |            - |            - |          56 B |
| **NestedDataDeserialize**         | **MessagePack_Lz4** | **4,325,684.66 ns** | **NA** |      **-** | **242.1875** | **242.1875** | **242.1875** | **940,355 B** |
| NestedDataDeserialize             | MessagePack_NoComp  |     4,122,675.18 ns |     NA |          - |     132.8125 |     132.8125 |     132.8125 |     560,180 B |
| NestedDataDeserialize             | ProtobufNet         |     4,467,512.12 ns |     NA |          - |     234.3750 |     234.3750 |     234.3750 |     989,190 B |
| NestedDataDeserialize             | JsonNet             |    52,505,701.30 ns |     NA |          - |    1100.0000 |     500.0000 |     500.0000 |   6,083,663 B |
| NestedDataDeserialize             | BinaryFormatter     |    71,770,976.14 ns |     NA |          - |    2285.7143 |     857.1429 |     428.5714 |  12,695,021 B |
| NestedDataDeserialize             | DataContract        |    43,894,391.58 ns |     NA |          - |     750.0000 |     416.6667 |     416.6667 |   4,487,180 B |
| NestedDataDeserialize             | Hyperion            |     4,058,719.35 ns |     NA |          - |     398.4375 |     132.8125 |     132.8125 |   2,241,259 B |
| NestedDataDeserialize             | Jil                 |    15,136,395.33 ns |     NA |          - |    1125.0000 |     968.7500 |     953.1250 |   5,377,251 B |
| NestedDataDeserialize             | SpanJson            |     8,253,926.22 ns |     NA |          - |     203.1250 |     203.1250 |     203.1250 |     955,881 B |
| NestedDataDeserialize             | UTF8Json            |    17,345,135.78 ns |     NA |          - |     546.8750 |     468.7500 |     468.7500 |   2,449,894 B |
| NestedDataDeserialize             | FsPickler           |     2,035,366.98 ns |     NA |          - |     134.7656 |     134.7656 |     134.7656 |     561,865 B |
| NestedDataDeserialize             | Ceras               |       247,460.37 ns |     NA |          - |      75.9277 |      75.6836 |      75.6836 |     560,673 B |
| NestedDataDeserialize             | OdinSerializer_     |    41,429,573.50 ns |     NA |          - |     916.6667 |     166.6667 |     166.6667 |   6,994,322 B |
| NestedDataDeserialize             | Nino                |     1,313,632.30 ns |     NA |          - |     138.6719 |     138.6719 |     138.6719 |     560,180 B |
| **NestedDataSerialize**           | **MessagePack_Lz4** | **2,455,911.87 ns** | **NA** | **1553 B** |        **-** |        **-** |        **-** |   **1,653 B** |
| NestedDataSerialize               | MessagePack_NoComp  |     2,406,048.39 ns |     NA |   380010 B |      97.6563 |      97.6563 |      97.6563 |     380,109 B |
| NestedDataSerialize               | ProtobufNet         |     3,384,039.64 ns |     NA |   410006 B |     351.5625 |     332.0313 |     332.0313 |   1,465,474 B |
| NestedDataSerialize               | JsonNet             |    25,376,169.72 ns |     NA |   920025 B |    1250.0000 |     656.2500 |     625.0000 |   6,950,205 B |
| NestedDataSerialize               | BinaryFormatter     |    35,539,531.00 ns |     NA |   580388 B |    1571.4286 |     571.4286 |     571.4286 |   9,164,829 B |
| NestedDataSerialize               | DataContract        |    19,413,209.16 ns |     NA |  1190173 B |    1187.5000 |     750.0000 |     718.7500 |   8,185,236 B |
| NestedDataSerialize               | Hyperion            |     5,051,485.30 ns |     NA |   500203 B |     640.6250 |     367.1875 |     351.5625 |   3,230,001 B |
| NestedDataSerialize               | Jil                 |     9,324,166.25 ns |     NA |  1010022 B |    1031.2500 |     921.8750 |     484.3750 |   6,513,292 B |
| NestedDataSerialize               | SpanJson            |     6,884,080.73 ns |     NA |  1010022 B |     164.0625 |     164.0625 |     164.0625 |   1,010,161 B |
| NestedDataSerialize               | UTF8Json            |     9,239,658.41 ns |     NA |  1010022 B |     781.2500 |     640.6250 |     640.6250 |   3,858,242 B |
| NestedDataSerialize               | FsPickler           |     2,004,896.36 ns |     NA |   470066 B |     351.5625 |     332.0313 |     332.0313 |   1,521,334 B |
| NestedDataSerialize               | Ceras               |       464,136.09 ns |     NA |   560009 B |     162.1094 |     161.1328 |     161.1328 |   1,613,471 B |
| NestedDataSerialize               | OdinSerializer_     |    19,509,833.66 ns |     NA |  1280351 B |    1812.5000 |     500.0000 |     500.0000 |  12,728,135 B |
| NestedDataSerialize               | Nino                |       604,728.28 ns |     NA |   450019 B |     124.0234 |     124.0234 |     124.0234 |     450,162 B |



### 说明

因为别的方案大多是用`byte[]`的，没办法发挥Nino的长处（使用`Span<byte>`，包含`stackalloc`分配的内存）
