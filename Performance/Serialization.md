# Serilization 性能报告

#### [**测试数据**](/Nino_Unity/Assets/Nino/Test/Data.cs)

## Unity平台性能测试

新版数据待补充



## 非Unity平台性能测试

#### [**测试数据**](/src/Nino.Benchmark/Serialization/Models)

```ini
BenchmarkDotNet=v0.13.1, OS=macOS 14.4 (23E214) [Darwin 23.4.0]
Apple M1, 1 CPU, 8 logical and 8 physical cores
.NET SDK=8.0.302
  [Host]   : .NET 6.0.7 (6.0.722.32202), Arm64 RyuJIT
  ShortRun : .NET 6.0.7 (6.0.722.32202), Arm64 RyuJIT

Job=ShortRun  Platform=AnyCpu  Runtime=.NET 6.0  
IterationCount=1  LaunchCount=1  WarmupCount=1  
```

| Method                            | Serializer          | Mean                 | Error  | DataSize   | Gen 0        | Gen 1        | Gen 2        | Allocated     |
| --------------------------------- | ------------------- | --------------------:| ------:| ----------:| ------------:| ------------:| ------------:| -------------:|
| **_PrimitiveBoolDeserialize**     | **MessagePack_Lz4** | **138.154 ns**       | **NA** | **-**      | **0.0305**   | **-**        | **-**        | **64 B**      |
| _PrimitiveBoolDeserialize         | MessagePack_NoComp  | 34.835 ns            | NA     | -          | -            | -            | -            | -             |
| _PrimitiveBoolDeserialize         | ProtobufNet         | 228.947 ns           | NA     | -          | 0.0420       | -            | -            | 88 B          |
| _PrimitiveBoolDeserialize         | JsonNet             | 698.559 ns           | NA     | -          | 2.7094       | -            | -            | 5,672 B       |
| _PrimitiveBoolDeserialize         | BinaryFormatter     | 1,840.803 ns         | NA     | -          | 1.9741       | -            | -            | 4,128 B       |
| _PrimitiveBoolDeserialize         | DataContract        | 1,371.300 ns         | NA     | -          | 1.9932       | -            | -            | 4,168 B       |
| _PrimitiveBoolDeserialize         | Jil                 | 67.400 ns            | NA     | -          | 0.0612       | -            | -            | 128 B         |
| _PrimitiveBoolDeserialize         | SpanJson            | 8.642 ns             | NA     | -          | -            | -            | -            | -             |
| _PrimitiveBoolDeserialize         | UTF8Json            | 17.746 ns            | NA     | -          | -            | -            | -            | -             |
| _PrimitiveBoolDeserialize         | FsPickler           | 436.889 ns           | NA     | -          | 0.4897       | -            | -            | 1,024 B       |
| _PrimitiveBoolDeserialize         | Ceras               | 81.393 ns            | NA     | -          | -            | -            | -            | -             |
| _PrimitiveBoolDeserialize         | Odin                | 336.371 ns           | NA     | -          | -            | -            | -            | -             |
| _PrimitiveBoolDeserialize         | Nino                | 6.792 ns             | NA     | -          | -            | -            | -            | -             |
| **_PrimitiveBoolSerialize**       | **MessagePack_Lz4** | **79.599 ns**        | **NA** | **1 B**    | **0.0459**   | **-**        | **-**        | **96 B**      |
| _PrimitiveBoolSerialize           | MessagePack_NoComp  | 55.110 ns            | NA     | 1 B        | 0.0153       | -            | -            | 32 B          |
| _PrimitiveBoolSerialize           | ProtobufNet         | 151.542 ns           | NA     | 2 B        | 0.1798       | -            | -            | 376 B         |
| _PrimitiveBoolSerialize           | JsonNet             | 357.001 ns           | NA     | 8 B        | 1.3847       | -            | -            | 2,896 B       |
| _PrimitiveBoolSerialize           | BinaryFormatter     | 1,081.531 ns         | NA     | 53 B       | 1.4687       | -            | -            | 3,072 B       |
| _PrimitiveBoolSerialize           | DataContract        | 504.433 ns           | NA     | 84 B       | 0.8221       | -            | -            | 1,720 B       |
| _PrimitiveBoolSerialize           | Jil                 | 81.348 ns            | NA     | 5 B        | 0.0802       | -            | -            | 168 B         |
| _PrimitiveBoolSerialize           | SpanJson            | 60.337 ns            | NA     | 5 B        | 0.0153       | -            | -            | 32 B          |
| _PrimitiveBoolSerialize           | UTF8Json            | 39.744 ns            | NA     | 5 B        | 0.0153       | -            | -            | 32 B          |
| _PrimitiveBoolSerialize           | FsPickler           | 384.877 ns           | NA     | 27 B       | 0.5279       | -            | -            | 1,104 B       |
| _PrimitiveBoolSerialize           | Ceras               | 311.272 ns           | NA     | 1 B        | 1.9841       | -            | -            | 4,152 B       |
| _PrimitiveBoolSerialize           | Odin                | 296.972 ns           | NA     | 2 B        | 0.0153       | -            | -            | 32 B          |
| _PrimitiveBoolSerialize           | Nino                | 45.895 ns            | NA     | 1 B        | 0.0153       | -            | -            | 32 B          |
| **_PrimitiveByteDeserialize**     | **MessagePack_Lz4** | **140.927 ns**       | **NA** | **-**      | **0.0305**   | **-**        | **-**        | **64 B**      |
| _PrimitiveByteDeserialize         | MessagePack_NoComp  | 37.846 ns            | NA     | -          | -            | -            | -            | -             |
| _PrimitiveByteDeserialize         | ProtobufNet         | 237.784 ns           | NA     | -          | 0.0420       | -            | -            | 88 B          |
| _PrimitiveByteDeserialize         | JsonNet             | 766.116 ns           | NA     | -          | 2.7342       | -            | -            | 5,720 B       |
| _PrimitiveByteDeserialize         | BinaryFormatter     | 1,879.898 ns         | NA     | -          | 1.9703       | -            | -            | 4,120 B       |
| _PrimitiveByteDeserialize         | DataContract        | 1,404.626 ns         | NA     | -          | 1.9779       | -            | -            | 4,136 B       |
| _PrimitiveByteDeserialize         | Jil                 | 72.669 ns            | NA     | -          | 0.0612       | -            | -            | 128 B         |
| _PrimitiveByteDeserialize         | SpanJson            | 13.104 ns            | NA     | -          | -            | -            | -            | -             |
| _PrimitiveByteDeserialize         | UTF8Json            | 22.138 ns            | NA     | -          | -            | -            | -            | -             |
| _PrimitiveByteDeserialize         | FsPickler           | 397.637 ns           | NA     | -          | 0.4859       | -            | -            | 1,016 B       |
| _PrimitiveByteDeserialize         | Ceras               | 84.118 ns            | NA     | -          | -            | -            | -            | -             |
| _PrimitiveByteDeserialize         | Odin                | 330.478 ns           | NA     | -          | -            | -            | -            | -             |
| _PrimitiveByteDeserialize         | Nino                | 7.143 ns             | NA     | -          | -            | -            | -            | -             |
| **_PrimitiveByteSerialize**       | **MessagePack_Lz4** | **86.356 ns**        | **NA** | **2 B**    | **0.0459**   | **-**        | **-**        | **96 B**      |
| _PrimitiveByteSerialize           | MessagePack_NoComp  | 55.695 ns            | NA     | 2 B        | 0.0153       | -            | -            | 32 B          |
| _PrimitiveByteSerialize           | ProtobufNet         | 152.647 ns           | NA     | 3 B        | 0.1798       | -            | -            | 376 B         |
| _PrimitiveByteSerialize           | JsonNet             | 362.860 ns           | NA     | 6 B        | 1.4305       | -            | -            | 2,992 B       |
| _PrimitiveByteSerialize           | BinaryFormatter     | 1,104.899 ns         | NA     | 50 B       | 1.4687       | -            | -            | 3,072 B       |
| _PrimitiveByteSerialize           | DataContract        | 521.876 ns           | NA     | 92 B       | 0.8259       | -            | -            | 1,728 B       |
| _PrimitiveByteSerialize           | Jil                 | 94.967 ns            | NA     | 3 B        | 0.1262       | -            | -            | 264 B         |
| _PrimitiveByteSerialize           | SpanJson            | 71.482 ns            | NA     | 3 B        | 0.0153       | -            | -            | 32 B          |
| _PrimitiveByteSerialize           | UTF8Json            | 47.159 ns            | NA     | 3 B        | 0.0153       | -            | -            | 32 B          |
| _PrimitiveByteSerialize           | FsPickler           | 358.660 ns           | NA     | 24 B       | 0.5240       | -            | -            | 1,096 B       |
| _PrimitiveByteSerialize           | Ceras               | 297.265 ns           | NA     | 1 B        | 1.9841       | -            | -            | 4,152 B       |
| _PrimitiveByteSerialize           | Odin                | 295.767 ns           | NA     | 2 B        | 0.0153       | -            | -            | 32 B          |
| _PrimitiveByteSerialize           | Nino                | 45.686 ns            | NA     | 1 B        | 0.0153       | -            | -            | 32 B          |
| **_PrimitiveCharDeserialize**     | **MessagePack_Lz4** | **138.571 ns**       | **NA** | **-**      | **0.0305**   | **-**        | **-**        | **64 B**      |
| _PrimitiveCharDeserialize         | MessagePack_NoComp  | 35.597 ns            | NA     | -          | -            | -            | -            | -             |
| _PrimitiveCharDeserialize         | ProtobufNet         | 226.587 ns           | NA     | -          | 0.0420       | -            | -            | 88 B          |
| _PrimitiveCharDeserialize         | JsonNet             | 741.717 ns           | NA     | -          | 2.7342       | -            | -            | 5,720 B       |
| _PrimitiveCharDeserialize         | BinaryFormatter     | 1,835.992 ns         | NA     | -          | 1.9703       | -            | -            | 4,120 B       |
| _PrimitiveCharDeserialize         | DataContract        | 1,302.357 ns         | NA     | -          | 1.9760       | -            | -            | 4,136 B       |
| _PrimitiveCharDeserialize         | Jil                 | 59.750 ns            | NA     | -          | 0.0153       | -            | -            | 32 B          |
| _PrimitiveCharDeserialize         | SpanJson            | 15.584 ns            | NA     | -          | -            | -            | -            | -             |
| _PrimitiveCharDeserialize         | UTF8Json            | 41.326 ns            | NA     | -          | 0.0114       | -            | -            | 24 B          |
| _PrimitiveCharDeserialize         | FsPickler           | 411.508 ns           | NA     | -          | 0.4854       | -            | -            | 1,016 B       |
| _PrimitiveCharDeserialize         | Ceras               | 80.834 ns            | NA     | -          | -            | -            | -            | -             |
| _PrimitiveCharDeserialize         | Odin                | 324.539 ns           | NA     | -          | -            | -            | -            | -             |
| _PrimitiveCharDeserialize         | Nino                | 6.892 ns             | NA     | -          | -            | -            | -            | -             |
| **_PrimitiveCharSerialize**       | **MessagePack_Lz4** | **85.758 ns**        | **NA** | **1 B**    | **0.0459**   | **-**        | **-**        | **96 B**      |
| _PrimitiveCharSerialize           | MessagePack_NoComp  | 69.001 ns            | NA     | 1 B        | 0.0153       | -            | -            | 32 B          |
| _PrimitiveCharSerialize           | ProtobufNet         | 152.231 ns           | NA     | 2 B        | 0.1798       | -            | -            | 376 B         |
| _PrimitiveCharSerialize           | JsonNet             | 492.932 ns           | NA     | 6 B        | 1.5526       | -            | -            | 3,248 B       |
| _PrimitiveCharSerialize           | BinaryFormatter     | 1,103.618 ns         | NA     | 50 B       | 1.4687       | -            | -            | 3,072 B       |
| _PrimitiveCharSerialize           | DataContract        | 510.479 ns           | NA     | 75 B       | 0.8183       | -            | -            | 1,712 B       |
| _PrimitiveCharSerialize           | Jil                 | 89.164 ns            | NA     | 3 B        | 0.0802       | -            | -            | 168 B         |
| _PrimitiveCharSerialize           | SpanJson            | 69.630 ns            | NA     | 3 B        | 0.0153       | -            | -            | 32 B          |
| _PrimitiveCharSerialize           | UTF8Json            | 56.711 ns            | NA     | 3 B        | 0.0268       | -            | -            | 56 B          |
| _PrimitiveCharSerialize           | FsPickler           | 370.472 ns           | NA     | 24 B       | 0.5240       | -            | -            | 1,096 B       |
| _PrimitiveCharSerialize           | Ceras               | 300.466 ns           | NA     | 2 B        | 1.9841       | -            | -            | 4,152 B       |
| _PrimitiveCharSerialize           | Odin                | 293.254 ns           | NA     | 3 B        | 0.0153       | -            | -            | 32 B          |
| _PrimitiveCharSerialize           | Nino                | 46.393 ns            | NA     | 2 B        | 0.0153       | -            | -            | 32 B          |
| **_PrimitiveDateTimeDeserialize** | **MessagePack_Lz4** | **156.941 ns**       | **NA** | **-**      | **0.0305**   | **-**        | **-**        | **64 B**      |
| _PrimitiveDateTimeDeserialize     | MessagePack_NoComp  | 48.308 ns            | NA     | -          | -            | -            | -            | -             |
| _PrimitiveDateTimeDeserialize     | ProtobufNet         | 262.173 ns           | NA     | -          | 0.0420       | -            | -            | 88 B          |
| _PrimitiveDateTimeDeserialize     | JsonNet             | 879.942 ns           | NA     | -          | 2.7342       | -            | -            | 5,720 B       |
| _PrimitiveDateTimeDeserialize     | BinaryFormatter     | 2,997.075 ns         | NA     | -          | 2.7847       | -            | -            | 5,829 B       |
| _PrimitiveDateTimeDeserialize     | DataContract        | 1,525.927 ns         | NA     | -          | 2.0504       | -            | -            | 4,288 B       |
| _PrimitiveDateTimeDeserialize     | Jil                 | 173.105 ns           | NA     | -          | 0.0801       | -            | -            | 168 B         |
| _PrimitiveDateTimeDeserialize     | SpanJson            | 131.820 ns           | NA     | -          | -            | -            | -            | -             |
| _PrimitiveDateTimeDeserialize     | UTF8Json            | 142.324 ns           | NA     | -          | -            | -            | -            | -             |
| _PrimitiveDateTimeDeserialize     | FsPickler           | 459.902 ns           | NA     | -          | 0.4892       | -            | -            | 1,024 B       |
| _PrimitiveDateTimeDeserialize     | Ceras               | 143.418 ns           | NA     | -          | -            | -            | -            | -             |
| _PrimitiveDateTimeDeserialize     | Odin                | 603.361 ns           | NA     | -          | 0.0496       | -            | -            | 104 B         |
| _PrimitiveDateTimeDeserialize     | Nino                | 6.552 ns             | NA     | -          | -            | -            | -            | -             |
| **_PrimitiveDateTimeSerialize**   | **MessagePack_Lz4** | **332.269 ns**       | **NA** | **6 B**    | **0.0458**   | **-**        | **-**        | **96 B**      |
| _PrimitiveDateTimeSerialize       | MessagePack_NoComp  | 135.973 ns           | NA     | 6 B        | 0.0153       | -            | -            | 32 B          |
| _PrimitiveDateTimeSerialize       | ProtobufNet         | 201.055 ns           | NA     | 6 B        | 0.1798       | -            | -            | 376 B         |
| _PrimitiveDateTimeSerialize       | JsonNet             | 481.834 ns           | NA     | 30 B       | 1.4410       | -            | -            | 3,016 B       |
| _PrimitiveDateTimeSerialize       | BinaryFormatter     | 1,487.415 ns         | NA     | 78 B       | 1.7471       | -            | -            | 3,656 B       |
| _PrimitiveDateTimeSerialize       | DataContract        | 733.898 ns           | NA     | 106 B      | 1.0252       | -            | -            | 2,144 B       |
| _PrimitiveDateTimeSerialize       | Jil                 | 261.569 ns           | NA     | 22 B       | 0.2027       | -            | -            | 424 B         |
| _PrimitiveDateTimeSerialize       | SpanJson            | 169.704 ns           | NA     | 27 B       | 0.0267       | -            | -            | 56 B          |
| _PrimitiveDateTimeSerialize       | UTF8Json            | 179.136 ns           | NA     | 27 B       | 0.0267       | -            | -            | 56 B          |
| _PrimitiveDateTimeSerialize       | FsPickler           | 438.445 ns           | NA     | 44 B       | 0.5355       | -            | -            | 1,120 B       |
| _PrimitiveDateTimeSerialize       | Ceras               | 429.250 ns           | NA     | 8 B        | 1.9841       | -            | -            | 4,152 B       |
| _PrimitiveDateTimeSerialize       | Odin                | 526.029 ns           | NA     | 99 B       | 0.0610       | -            | -            | 128 B         |
| _PrimitiveDateTimeSerialize       | Nino                | 52.666 ns            | NA     | 8 B        | 0.0153       | -            | -            | 32 B          |
| **_PrimitiveIntDeserialize**      | **MessagePack_Lz4** | **149.839 ns**       | **NA** | **-**      | **0.0305**   | **-**        | **-**        | **64 B**      |
| _PrimitiveIntDeserialize          | MessagePack_NoComp  | 41.892 ns            | NA     | -          | -            | -            | -            | -             |
| _PrimitiveIntDeserialize          | ProtobufNet         | 266.465 ns           | NA     | -          | 0.0420       | -            | -            | 88 B          |
| _PrimitiveIntDeserialize          | JsonNet             | 1,223.894 ns         | NA     | -          | 2.7227       | -            | -            | 5,696 B       |
| _PrimitiveIntDeserialize          | BinaryFormatter     | 1,951.010 ns         | NA     | -          | 1.9684       | -            | -            | 4,120 B       |
| _PrimitiveIntDeserialize          | DataContract        | 1,382.555 ns         | NA     | -          | 1.9779       | -            | -            | 4,136 B       |
| _PrimitiveIntDeserialize          | Jil                 | 90.209 ns            | NA     | -          | 0.0688       | -            | -            | 144 B         |
| _PrimitiveIntDeserialize          | SpanJson            | 33.476 ns            | NA     | -          | -            | -            | -            | -             |
| _PrimitiveIntDeserialize          | UTF8Json            | 68.596 ns            | NA     | -          | -            | -            | -            | -             |
| _PrimitiveIntDeserialize          | FsPickler           | 424.458 ns           | NA     | -          | 0.4859       | -            | -            | 1,016 B       |
| _PrimitiveIntDeserialize          | Ceras               | 97.100 ns            | NA     | -          | -            | -            | -            | -             |
| _PrimitiveIntDeserialize          | Odin                | 436.772 ns           | NA     | -          | -            | -            | -            | -             |
| _PrimitiveIntDeserialize          | Nino                | 7.357 ns             | NA     | -          | -            | -            | -            | -             |
| **_PrimitiveIntSerialize**        | **MessagePack_Lz4** | **81.509 ns**        | **NA** | **5 B**    | **0.0459**   | **-**        | **-**        | **96 B**      |
| _PrimitiveIntSerialize            | MessagePack_NoComp  | 57.925 ns            | NA     | 5 B        | 0.0153       | -            | -            | 32 B          |
| _PrimitiveIntSerialize            | ProtobufNet         | 162.813 ns           | NA     | 11 B       | 0.1836       | -            | -            | 384 B         |
| _PrimitiveIntSerialize            | JsonNet             | 401.783 ns           | NA     | 14 B       | 1.4343       | -            | -            | 3,000 B       |
| _PrimitiveIntSerialize            | BinaryFormatter     | 1,107.493 ns         | NA     | 54 B       | 1.4687       | -            | -            | 3,072 B       |
| _PrimitiveIntSerialize            | DataContract        | 547.554 ns           | NA     | 82 B       | 0.8221       | -            | -            | 1,720 B       |
| _PrimitiveIntSerialize            | Jil                 | 103.493 ns           | NA     | 11 B       | 0.1377       | -            | -            | 288 B         |
| _PrimitiveIntSerialize            | SpanJson            | 74.425 ns            | NA     | 11 B       | 0.0191       | -            | -            | 40 B          |
| _PrimitiveIntSerialize            | UTF8Json            | 54.700 ns            | NA     | 11 B       | 0.0191       | -            | -            | 40 B          |
| _PrimitiveIntSerialize            | FsPickler           | 362.363 ns           | NA     | 28 B       | 0.5279       | -            | -            | 1,104 B       |
| _PrimitiveIntSerialize            | Ceras               | 304.973 ns           | NA     | 5 B        | 1.9841       | -            | -            | 4,152 B       |
| _PrimitiveIntSerialize            | Odin                | 300.540 ns           | NA     | 5 B        | 0.0153       | -            | -            | 32 B          |
| _PrimitiveIntSerialize            | Nino                | 50.565 ns            | NA     | 4 B        | 0.0153       | -            | -            | 32 B          |
| **_PrimitiveLongDeserialize**     | **MessagePack_Lz4** | **150.910 ns**       | **NA** | **-**      | **0.0305**   | **-**        | **-**        | **64 B**      |
| _PrimitiveLongDeserialize         | MessagePack_NoComp  | 47.484 ns            | NA     | -          | -            | -            | -            | -             |
| _PrimitiveLongDeserialize         | ProtobufNet         | 252.726 ns           | NA     | -          | 0.0420       | -            | -            | 88 B          |
| _PrimitiveLongDeserialize         | JsonNet             | 902.901 ns           | NA     | -          | 2.7227       | -            | -            | 5,696 B       |
| _PrimitiveLongDeserialize         | BinaryFormatter     | 2,093.677 ns         | NA     | -          | 1.9684       | -            | -            | 4,120 B       |
| _PrimitiveLongDeserialize         | DataContract        | 1,432.751 ns         | NA     | -          | 1.9760       | -            | -            | 4,136 B       |
| _PrimitiveLongDeserialize         | Jil                 | 107.435 ns           | NA     | -          | 0.0764       | -            | -            | 160 B         |
| _PrimitiveLongDeserialize         | SpanJson            | 52.870 ns            | NA     | -          | -            | -            | -            | -             |
| _PrimitiveLongDeserialize         | UTF8Json            | 42.496 ns            | NA     | -          | -            | -            | -            | -             |
| _PrimitiveLongDeserialize         | FsPickler           | 409.521 ns           | NA     | -          | 0.4859       | -            | -            | 1,016 B       |
| _PrimitiveLongDeserialize         | Ceras               | 85.765 ns            | NA     | -          | -            | -            | -            | -             |
| _PrimitiveLongDeserialize         | Odin                | 341.088 ns           | NA     | -          | -            | -            | -            | -             |
| _PrimitiveLongDeserialize         | Nino                | 7.468 ns             | NA     | -          | -            | -            | -            | -             |
| **_PrimitiveLongSerialize**       | **MessagePack_Lz4** | **87.856 ns**        | **NA** | **9 B**    | **0.0497**   | **-**        | **-**        | **104 B**     |
| _PrimitiveLongSerialize           | MessagePack_NoComp  | 61.159 ns            | NA     | 9 B        | 0.0191       | -            | -            | 40 B          |
| _PrimitiveLongSerialize           | ProtobufNet         | 161.365 ns           | NA     | 10 B       | 0.1836       | -            | -            | 384 B         |
| _PrimitiveLongSerialize           | JsonNet             | 393.757 ns           | NA     | 22 B       | 1.4377       | -            | -            | 3,008 B       |
| _PrimitiveLongSerialize           | BinaryFormatter     | 1,110.127 ns         | NA     | 58 B       | 1.4725       | -            | -            | 3,080 B       |
| _PrimitiveLongSerialize           | DataContract        | 566.216 ns           | NA     | 92 B       | 0.8259       | -            | -            | 1,728 B       |
| _PrimitiveLongSerialize           | Jil                 | 175.156 ns           | NA     | 19 B       | 0.1988       | -            | -            | 416 B         |
| _PrimitiveLongSerialize           | SpanJson            | 89.119 ns            | NA     | 19 B       | 0.0229       | -            | -            | 48 B          |
| _PrimitiveLongSerialize           | UTF8Json            | 67.362 ns            | NA     | 19 B       | 0.0229       | -            | -            | 48 B          |
| _PrimitiveLongSerialize           | FsPickler           | 368.727 ns           | NA     | 32 B       | 0.5279       | -            | -            | 1,104 B       |
| _PrimitiveLongSerialize           | Ceras               | 297.324 ns           | NA     | 8 B        | 1.9841       | -            | -            | 4,152 B       |
| _PrimitiveLongSerialize           | Odin                | 313.398 ns           | NA     | 9 B        | 0.0191       | -            | -            | 40 B          |
| _PrimitiveLongSerialize           | Nino                | 47.426 ns            | NA     | 8 B        | 0.0153       | -            | -            | 32 B          |
| **_PrimitiveSByteDeserialize**    | **MessagePack_Lz4** | **173.292 ns**       | **NA** | **-**      | **0.0305**   | **-**        | **-**        | **64 B**      |
| _PrimitiveSByteDeserialize        | MessagePack_NoComp  | 52.830 ns            | NA     | -          | -            | -            | -            | -             |
| _PrimitiveSByteDeserialize        | ProtobufNet         | 273.530 ns           | NA     | -          | 0.0420       | -            | -            | 88 B          |
| _PrimitiveSByteDeserialize        | JsonNet             | 956.921 ns           | NA     | -          | 2.7332       | -            | -            | 5,720 B       |
| _PrimitiveSByteDeserialize        | BinaryFormatter     | 2,406.986 ns         | NA     | -          | 1.9684       | -            | -            | 4,120 B       |
| _PrimitiveSByteDeserialize        | DataContract        | 1,538.804 ns         | NA     | -          | 1.9760       | -            | -            | 4,136 B       |
| _PrimitiveSByteDeserialize        | Jil                 | 108.764 ns           | NA     | -          | 0.0612       | -            | -            | 128 B         |
| _PrimitiveSByteDeserialize        | SpanJson            | 19.181 ns            | NA     | -          | -            | -            | -            | -             |
| _PrimitiveSByteDeserialize        | UTF8Json            | 24.775 ns            | NA     | -          | -            | -            | -            | -             |
| _PrimitiveSByteDeserialize        | FsPickler           | 432.665 ns           | NA     | -          | 0.4854       | -            | -            | 1,016 B       |
| _PrimitiveSByteDeserialize        | Ceras               | 92.021 ns            | NA     | -          | -            | -            | -            | -             |
| _PrimitiveSByteDeserialize        | Odin                | 350.089 ns           | NA     | -          | -            | -            | -            | -             |
| _PrimitiveSByteDeserialize        | Nino                | 9.722 ns             | NA     | -          | -            | -            | -            | -             |
| **_PrimitiveSByteSerialize**      | **MessagePack_Lz4** | **87.291 ns**        | **NA** | **2 B**    | **0.0459**   | **-**        | **-**        | **96 B**      |
| _PrimitiveSByteSerialize          | MessagePack_NoComp  | 59.358 ns            | NA     | 2 B        | 0.0153       | -            | -            | 32 B          |
| _PrimitiveSByteSerialize          | ProtobufNet         | 168.891 ns           | NA     | 11 B       | 0.1836       | -            | -            | 384 B         |
| _PrimitiveSByteSerialize          | JsonNet             | 378.217 ns           | NA     | 7 B        | 1.4305       | -            | -            | 2,992 B       |
| _PrimitiveSByteSerialize          | BinaryFormatter     | 1,102.299 ns         | NA     | 51 B       | 1.4687       | -            | -            | 3,072 B       |
| _PrimitiveSByteSerialize          | DataContract        | 518.520 ns           | NA     | 77 B       | 0.8183       | -            | -            | 1,712 B       |
| _PrimitiveSByteSerialize          | Jil                 | 97.455 ns            | NA     | 4 B        | 0.1262       | -            | -            | 264 B         |
| _PrimitiveSByteSerialize          | SpanJson            | 78.699 ns            | NA     | 4 B        | 0.0153       | -            | -            | 32 B          |
| _PrimitiveSByteSerialize          | UTF8Json            | 43.862 ns            | NA     | 4 B        | 0.0153       | -            | -            | 32 B          |
| _PrimitiveSByteSerialize          | FsPickler           | 376.530 ns           | NA     | 25 B       | 0.5279       | -            | -            | 1,104 B       |
| _PrimitiveSByteSerialize          | Ceras               | 318.932 ns           | NA     | 1 B        | 1.9841       | -            | -            | 4,152 B       |
| _PrimitiveSByteSerialize          | Odin                | 298.178 ns           | NA     | 2 B        | 0.0153       | -            | -            | 32 B          |
| _PrimitiveSByteSerialize          | Nino                | 46.545 ns            | NA     | 1 B        | 0.0153       | -            | -            | 32 B          |
| **_PrimitiveShortDeserialize**    | **MessagePack_Lz4** | **160.890 ns**       | **NA** | **-**      | **0.0305**   | **-**        | **-**        | **64 B**      |
| _PrimitiveShortDeserialize        | MessagePack_NoComp  | 44.612 ns            | NA     | -          | -            | -            | -            | -             |
| _PrimitiveShortDeserialize        | ProtobufNet         | 361.034 ns           | NA     | -          | 0.0420       | -            | -            | 88 B          |
| _PrimitiveShortDeserialize        | JsonNet             | 1,075.247 ns         | NA     | -          | 2.7342       | -            | -            | 5,720 B       |
| _PrimitiveShortDeserialize        | BinaryFormatter     | 2,022.474 ns         | NA     | -          | 1.9684       | -            | -            | 4,120 B       |
| _PrimitiveShortDeserialize        | DataContract        | 1,445.760 ns         | NA     | -          | 1.9760       | -            | -            | 4,136 B       |
| _PrimitiveShortDeserialize        | Jil                 | 80.609 ns            | NA     | -          | 0.0612       | -            | -            | 128 B         |
| _PrimitiveShortDeserialize        | SpanJson            | 23.109 ns            | NA     | -          | -            | -            | -            | -             |
| _PrimitiveShortDeserialize        | UTF8Json            | 52.577 ns            | NA     | -          | -            | -            | -            | -             |
| _PrimitiveShortDeserialize        | FsPickler           | 609.941 ns           | NA     | -          | 0.4845       | -            | -            | 1,016 B       |
| _PrimitiveShortDeserialize        | Ceras               | 86.731 ns            | NA     | -          | -            | -            | -            | -             |
| _PrimitiveShortDeserialize        | Odin                | 431.929 ns           | NA     | -          | -            | -            | -            | -             |
| _PrimitiveShortDeserialize        | Nino                | 7.423 ns             | NA     | -          | -            | -            | -            | -             |
| **_PrimitiveShortSerialize**      | **MessagePack_Lz4** | **84.547 ns**        | **NA** | **3 B**    | **0.0459**   | **-**        | **-**        | **96 B**      |
| _PrimitiveShortSerialize          | MessagePack_NoComp  | 58.656 ns            | NA     | 3 B        | 0.0153       | -            | -            | 32 B          |
| _PrimitiveShortSerialize          | ProtobufNet         | 153.442 ns           | NA     | 4 B        | 0.1798       | -            | -            | 376 B         |
| _PrimitiveShortSerialize          | JsonNet             | 372.307 ns           | NA     | 8 B        | 1.4305       | -            | -            | 2,992 B       |
| _PrimitiveShortSerialize          | BinaryFormatter     | 1,090.255 ns         | NA     | 52 B       | 1.4687       | -            | -            | 3,072 B       |
| _PrimitiveShortSerialize          | DataContract        | 514.747 ns           | NA     | 80 B       | 0.8183       | -            | -            | 1,712 B       |
| _PrimitiveShortSerialize          | Jil                 | 102.524 ns           | NA     | 5 B        | 0.1262       | -            | -            | 264 B         |
| _PrimitiveShortSerialize          | SpanJson            | 67.962 ns            | NA     | 5 B        | 0.0153       | -            | -            | 32 B          |
| _PrimitiveShortSerialize          | UTF8Json            | 44.620 ns            | NA     | 5 B        | 0.0153       | -            | -            | 32 B          |
| _PrimitiveShortSerialize          | FsPickler           | 374.226 ns           | NA     | 26 B       | 0.5279       | -            | -            | 1,104 B       |
| _PrimitiveShortSerialize          | Ceras               | 310.373 ns           | NA     | 2 B        | 1.9841       | -            | -            | 4,152 B       |
| _PrimitiveShortSerialize          | Odin                | 293.746 ns           | NA     | 3 B        | 0.0153       | -            | -            | 32 B          |
| _PrimitiveShortSerialize          | Nino                | 48.027 ns            | NA     | 2 B        | 0.0153       | -            | -            | 32 B          |
| **_PrimitiveStringDeserialize**   | **MessagePack_Lz4** | **544.289 ns**       | **NA** | **-**      | **0.1373**   | **-**        | **-**        | **288 B**     |
| _PrimitiveStringDeserialize       | MessagePack_NoComp  | 103.104 ns           | NA     | -          | 0.1070       | -            | -            | 224 B         |
| _PrimitiveStringDeserialize       | ProtobufNet         | 305.879 ns           | NA     | -          | 0.1488       | -            | -            | 312 B         |
| _PrimitiveStringDeserialize       | JsonNet             | 914.114 ns           | NA     | -          | 2.8152       | -            | -            | 5,896 B       |
| _PrimitiveStringDeserialize       | BinaryFormatter     | 757.687 ns           | NA     | -          | 1.2236       | -            | -            | 2,560 B       |
| _PrimitiveStringDeserialize       | DataContract        | 1,679.119 ns         | NA     | -          | 2.2297       | -            | -            | 4,664 B       |
| _PrimitiveStringDeserialize       | Jil                 | 416.432 ns           | NA     | -          | 0.3977       | -            | -            | 832 B         |
| _PrimitiveStringDeserialize       | SpanJson            | 135.532 ns           | NA     | -          | 0.1070       | -            | -            | 224 B         |
| _PrimitiveStringDeserialize       | UTF8Json            | 264.365 ns           | NA     | -          | 0.1068       | -            | -            | 224 B         |
| _PrimitiveStringDeserialize       | FsPickler           | 491.160 ns           | NA     | -          | 0.5927       | -            | -            | 1,240 B       |
| _PrimitiveStringDeserialize       | Ceras               | 157.259 ns           | NA     | -          | 0.1070       | -            | -            | 224 B         |
| _PrimitiveStringDeserialize       | Odin                | 374.535 ns           | NA     | -          | 0.1068       | -            | -            | 224 B         |
| _PrimitiveStringDeserialize       | Nino                | 36.583 ns            | NA     | -          | 0.1071       | -            | -            | 224 B         |
| **_PrimitiveStringSerialize**     | **MessagePack_Lz4** | **411.009 ns**       | **NA** | **21 B**   | **0.0534**   | **-**        | **-**        | **112 B**     |
| _PrimitiveStringSerialize         | MessagePack_NoComp  | 81.912 ns            | NA     | 102 B      | 0.0612       | -            | -            | 128 B         |
| _PrimitiveStringSerialize         | ProtobufNet         | 305.328 ns           | NA     | 102 B      | 0.2255       | -            | -            | 472 B         |
| _PrimitiveStringSerialize         | JsonNet             | 453.027 ns           | NA     | 105 B      | 1.4682       | -            | -            | 3,072 B       |
| _PrimitiveStringSerialize         | BinaryFormatter     | 736.647 ns           | NA     | 124 B      | 1.1778       | -            | -            | 2,464 B       |
| _PrimitiveStringSerialize         | DataContract        | 654.075 ns           | NA     | 177 B      | 0.8564       | -            | -            | 1,792 B       |
| _PrimitiveStringSerialize         | Jil                 | 503.522 ns           | NA     | 102 B      | 0.4320       | -            | -            | 904 B         |
| _PrimitiveStringSerialize         | SpanJson            | 174.507 ns           | NA     | 102 B      | 0.0610       | -            | -            | 128 B         |
| _PrimitiveStringSerialize         | UTF8Json            | 135.569 ns           | NA     | 102 B      | 0.0610       | -            | -            | 128 B         |
| _PrimitiveStringSerialize         | FsPickler           | 462.445 ns           | NA     | 127 B      | 0.5736       | -            | -            | 1,200 B       |
| _PrimitiveStringSerialize         | Ceras               | 347.374 ns           | NA     | 101 B      | 2.0280       | -            | -            | 4,248 B       |
| _PrimitiveStringSerialize         | Odin                | 315.643 ns           | NA     | 206 B      | 0.1106       | -            | -            | 232 B         |
| _PrimitiveStringSerialize         | Nino                | 56.208 ns            | NA     | 206 B      | 0.1109       | -            | -            | 232 B         |
| **_PrimitiveUIntDeserialize**     | **MessagePack_Lz4** | **139.739 ns**       | **NA** | **-**      | **0.0305**   | **-**        | **-**        | **64 B**      |
| _PrimitiveUIntDeserialize         | MessagePack_NoComp  | 36.001 ns            | NA     | -          | -            | -            | -            | -             |
| _PrimitiveUIntDeserialize         | ProtobufNet         | 289.657 ns           | NA     | -          | 0.0420       | -            | -            | 88 B          |
| _PrimitiveUIntDeserialize         | JsonNet             | 762.139 ns           | NA     | -          | 2.7218       | -            | -            | 5,696 B       |
| _PrimitiveUIntDeserialize         | BinaryFormatter     | 1,893.910 ns         | NA     | -          | 1.9684       | -            | -            | 4,120 B       |
| _PrimitiveUIntDeserialize         | DataContract        | 1,376.241 ns         | NA     | -          | 1.9760       | -            | -            | 4,136 B       |
| _PrimitiveUIntDeserialize         | Jil                 | 66.243 ns            | NA     | -          | 0.0573       | -            | -            | 120 B         |
| _PrimitiveUIntDeserialize         | SpanJson            | 11.823 ns            | NA     | -          | -            | -            | -            | -             |
| _PrimitiveUIntDeserialize         | UTF8Json            | 19.989 ns            | NA     | -          | -            | -            | -            | -             |
| _PrimitiveUIntDeserialize         | FsPickler           | 404.997 ns           | NA     | -          | 0.4854       | -            | -            | 1,016 B       |
| _PrimitiveUIntDeserialize         | Ceras               | 84.606 ns            | NA     | -          | -            | -            | -            | -             |
| _PrimitiveUIntDeserialize         | Odin                | 339.061 ns           | NA     | -          | -            | -            | -            | -             |
| _PrimitiveUIntDeserialize         | Nino                | 7.123 ns             | NA     | -          | -            | -            | -            | -             |
| **_PrimitiveUIntSerialize**       | **MessagePack_Lz4** | **89.775 ns**        | **NA** | **1 B**    | **0.0459**   | **-**        | **-**        | **96 B**      |
| _PrimitiveUIntSerialize           | MessagePack_NoComp  | 56.141 ns            | NA     | 1 B        | 0.0153       | -            | -            | 32 B          |
| _PrimitiveUIntSerialize           | ProtobufNet         | 153.254 ns           | NA     | 2 B        | 0.1798       | -            | -            | 376 B         |
| _PrimitiveUIntSerialize           | JsonNet             | 343.112 ns           | NA     | 4 B        | 1.3847       | -            | -            | 2,896 B       |
| _PrimitiveUIntSerialize           | BinaryFormatter     | 1,088.794 ns         | NA     | 55 B       | 1.4687       | -            | -            | 3,072 B       |
| _PrimitiveUIntSerialize           | DataContract        | 528.015 ns           | NA     | 88 B       | 0.8221       | -            | -            | 1,720 B       |
| _PrimitiveUIntSerialize           | Jil                 | 96.772 ns            | NA     | 1 B        | 0.1224       | -            | -            | 256 B         |
| _PrimitiveUIntSerialize           | SpanJson            | 65.505 ns            | NA     | 1 B        | 0.0153       | -            | -            | 32 B          |
| _PrimitiveUIntSerialize           | UTF8Json            | 40.335 ns            | NA     | 1 B        | 0.0153       | -            | -            | 32 B          |
| _PrimitiveUIntSerialize           | FsPickler           | 370.443 ns           | NA     | 29 B       | 0.5279       | -            | -            | 1,104 B       |
| _PrimitiveUIntSerialize           | Ceras               | 305.704 ns           | NA     | 1 B        | 1.9841       | -            | -            | 4,152 B       |
| _PrimitiveUIntSerialize           | Odin                | 298.573 ns           | NA     | 5 B        | 0.0153       | -            | -            | 32 B          |
| _PrimitiveUIntSerialize           | Nino                | 48.857 ns            | NA     | 4 B        | 0.0153       | -            | -            | 32 B          |
| **_PrimitiveULongDeserialize**    | **MessagePack_Lz4** | **143.216 ns**       | **NA** | **-**      | **0.0305**   | **-**        | **-**        | **64 B**      |
| _PrimitiveULongDeserialize        | MessagePack_NoComp  | 40.888 ns            | NA     | -          | -            | -            | -            | -             |
| _PrimitiveULongDeserialize        | ProtobufNet         | 236.009 ns           | NA     | -          | 0.0420       | -            | -            | 88 B          |
| _PrimitiveULongDeserialize        | JsonNet             | 1,120.918 ns         | NA     | -          | 2.8839       | -            | -            | 6,032 B       |
| _PrimitiveULongDeserialize        | BinaryFormatter     | 1,874.674 ns         | NA     | -          | 1.9684       | -            | -            | 4,120 B       |
| _PrimitiveULongDeserialize        | DataContract        | 1,530.888 ns         | NA     | -          | 2.0390       | -            | -            | 4,264 B       |
| _PrimitiveULongDeserialize        | Jil                 | 118.374 ns           | NA     | -          | 0.0763       | -            | -            | 160 B         |
| _PrimitiveULongDeserialize        | SpanJson            | 89.839 ns            | NA     | -          | -            | -            | -            | -             |
| _PrimitiveULongDeserialize        | UTF8Json            | 55.491 ns            | NA     | -          | -            | -            | -            | -             |
| _PrimitiveULongDeserialize        | FsPickler           | 401.665 ns           | NA     | -          | 0.4854       | -            | -            | 1,016 B       |
| _PrimitiveULongDeserialize        | Ceras               | 84.624 ns            | NA     | -          | -            | -            | -            | -             |
| _PrimitiveULongDeserialize        | Odin                | 338.834 ns           | NA     | -          | -            | -            | -            | -             |
| _PrimitiveULongDeserialize        | Nino                | 7.162 ns             | NA     | -          | -            | -            | -            | -             |
| **_PrimitiveULongSerialize**      | **MessagePack_Lz4** | **87.655 ns**        | **NA** | **9 B**    | **0.0497**   | **-**        | **-**        | **104 B**     |
| _PrimitiveULongSerialize          | MessagePack_NoComp  | 58.862 ns            | NA     | 9 B        | 0.0191       | -            | -            | 40 B          |
| _PrimitiveULongSerialize          | ProtobufNet         | 160.003 ns           | NA     | 11 B       | 0.1836       | -            | -            | 384 B         |
| _PrimitiveULongSerialize          | JsonNet             | 388.431 ns           | NA     | 23 B       | 1.4381       | -            | -            | 3,008 B       |
| _PrimitiveULongSerialize          | BinaryFormatter     | 1,165.139 ns         | NA     | 59 B       | 1.4725       | -            | -            | 3,080 B       |
| _PrimitiveULongSerialize          | DataContract        | 569.190 ns           | NA     | 109 B      | 0.8640       | -            | -            | 1,808 B       |
| _PrimitiveULongSerialize          | Jil                 | 154.808 ns           | NA     | 20 B       | 0.1988       | -            | -            | 416 B         |
| _PrimitiveULongSerialize          | SpanJson            | 88.067 ns            | NA     | 20 B       | 0.0229       | -            | -            | 48 B          |
| _PrimitiveULongSerialize          | UTF8Json            | 64.812 ns            | NA     | 20 B       | 0.0229       | -            | -            | 48 B          |
| _PrimitiveULongSerialize          | FsPickler           | 361.423 ns           | NA     | 33 B       | 0.5317       | -            | -            | 1,112 B       |
| _PrimitiveULongSerialize          | Ceras               | 304.939 ns           | NA     | 8 B        | 1.9841       | -            | -            | 4,152 B       |
| _PrimitiveULongSerialize          | Odin                | 301.618 ns           | NA     | 9 B        | 0.0191       | -            | -            | 40 B          |
| _PrimitiveULongSerialize          | Nino                | 46.257 ns            | NA     | 8 B        | 0.0153       | -            | -            | 32 B          |
| **_PrimitiveUShortDeserialize**   | **MessagePack_Lz4** | **147.326 ns**       | **NA** | **-**      | **0.0305**   | **-**        | **-**        | **64 B**      |
| _PrimitiveUShortDeserialize       | MessagePack_NoComp  | 40.951 ns            | NA     | -          | -            | -            | -            | -             |
| _PrimitiveUShortDeserialize       | ProtobufNet         | 233.048 ns           | NA     | -          | 0.0420       | -            | -            | 88 B          |
| _PrimitiveUShortDeserialize       | JsonNet             | 856.226 ns           | NA     | -          | 2.7342       | -            | -            | 5,720 B       |
| _PrimitiveUShortDeserialize       | BinaryFormatter     | 1,893.253 ns         | NA     | -          | 1.9684       | -            | -            | 4,120 B       |
| _PrimitiveUShortDeserialize       | DataContract        | 1,403.789 ns         | NA     | -          | 1.9760       | -            | -            | 4,136 B       |
| _PrimitiveUShortDeserialize       | Jil                 | 74.119 ns            | NA     | -          | 0.0612       | -            | -            | 128 B         |
| _PrimitiveUShortDeserialize       | SpanJson            | 16.704 ns            | NA     | -          | -            | -            | -            | -             |
| _PrimitiveUShortDeserialize       | UTF8Json            | 24.610 ns            | NA     | -          | -            | -            | -            | -             |
| _PrimitiveUShortDeserialize       | FsPickler           | 399.789 ns           | NA     | -          | 0.4854       | -            | -            | 1,016 B       |
| _PrimitiveUShortDeserialize       | Ceras               | 81.677 ns            | NA     | -          | -            | -            | -            | -             |
| _PrimitiveUShortDeserialize       | Odin                | 370.366 ns           | NA     | -          | -            | -            | -            | -             |
| _PrimitiveUShortDeserialize       | Nino                | 6.996 ns             | NA     | -          | -            | -            | -            | -             |
| **_PrimitiveUShortSerialize**     | **MessagePack_Lz4** | **85.480 ns**        | **NA** | **3 B**    | **0.0459**   | **-**        | **-**        | **96 B**      |
| _PrimitiveUShortSerialize         | MessagePack_NoComp  | 57.257 ns            | NA     | 3 B        | 0.0153       | -            | -            | 32 B          |
| _PrimitiveUShortSerialize         | ProtobufNet         | 154.073 ns           | NA     | 4 B        | 0.1798       | -            | -            | 376 B         |
| _PrimitiveUShortSerialize         | JsonNet             | 368.378 ns           | NA     | 8 B        | 1.4305       | -            | -            | 2,992 B       |
| _PrimitiveUShortSerialize         | BinaryFormatter     | 1,098.065 ns         | NA     | 53 B       | 1.4687       | -            | -            | 3,072 B       |
| _PrimitiveUShortSerialize         | DataContract        | 520.625 ns           | NA     | 96 B       | 0.8259       | -            | -            | 1,728 B       |
| _PrimitiveUShortSerialize         | Jil                 | 100.528 ns           | NA     | 5 B        | 0.1262       | -            | -            | 264 B         |
| _PrimitiveUShortSerialize         | SpanJson            | 79.631 ns            | NA     | 5 B        | 0.0153       | -            | -            | 32 B          |
| _PrimitiveUShortSerialize         | UTF8Json            | 43.899 ns            | NA     | 5 B        | 0.0153       | -            | -            | 32 B          |
| _PrimitiveUShortSerialize         | FsPickler           | 365.194 ns           | NA     | 27 B       | 0.5279       | -            | -            | 1,104 B       |
| _PrimitiveUShortSerialize         | Ceras               | 296.994 ns           | NA     | 2 B        | 1.9841       | -            | -            | 4,152 B       |
| _PrimitiveUShortSerialize         | Odin                | 293.916 ns           | NA     | 3 B        | 0.0153       | -            | -            | 32 B          |
| _PrimitiveUShortSerialize         | Nino                | 46.019 ns            | NA     | 2 B        | 0.0153       | -            | -            | 32 B          |
| **AccessTokenDeserialize**        | **MessagePack_Lz4** | **249.550 ns**       | **NA** | **-**      | **0.0534**   | **-**        | **-**        | **112 B**     |
| AccessTokenDeserialize            | MessagePack_NoComp  | 145.882 ns           | NA     | -          | 0.0229       | -            | -            | 48 B          |
| AccessTokenDeserialize            | ProtobufNet         | 353.008 ns           | NA     | -          | 0.0648       | -            | -            | 136 B         |
| AccessTokenDeserialize            | JsonNet             | 1,689.592 ns         | NA     | -          | 2.7542       | -            | -            | 5,768 B       |
| AccessTokenDeserialize            | BinaryFormatter     | 2,731.956 ns         | NA     | -          | 2.5024       | -            | -            | 5,240 B       |
| AccessTokenDeserialize            | DataContract        | 3,667.565 ns         | NA     | -          | 4.1237       | -            | -            | 8,632 B       |
| AccessTokenDeserialize            | Jil                 | 313.271 ns           | NA     | -          | 0.1564       | -            | -            | 328 B         |
| AccessTokenDeserialize            | SpanJson            | 95.724 ns            | NA     | -          | 0.0229       | -            | -            | 48 B          |
| AccessTokenDeserialize            | UTF8Json            | 271.678 ns           | NA     | -          | 0.0229       | -            | -            | 48 B          |
| AccessTokenDeserialize            | FsPickler           | 531.018 ns           | NA     | -          | 0.5922       | -            | -            | 1,240 B       |
| AccessTokenDeserialize            | Ceras               | 273.794 ns           | NA     | -          | 0.0229       | -            | -            | 48 B          |
| AccessTokenDeserialize            | Odin                | 1,890.117 ns         | NA     | -          | 0.3014       | -            | -            | 632 B         |
| AccessTokenDeserialize            | Nino                | 41.652 ns            | NA     | -          | 0.0229       | -            | -            | 48 B          |
| **AccessTokenSerialize**          | **MessagePack_Lz4** | **352.738 ns**       | **NA** | **19 B**   | **0.0534**   | **-**        | **-**        | **112 B**     |
| AccessTokenSerialize              | MessagePack_NoComp  | 132.470 ns           | NA     | 19 B       | 0.0229       | -            | -            | 48 B          |
| AccessTokenSerialize              | ProtobufNet         | 319.258 ns           | NA     | 6 B        | 0.1798       | -            | -            | 376 B         |
| AccessTokenSerialize              | JsonNet             | 905.380 ns           | NA     | 82 B       | 1.5059       | -            | -            | 3,152 B       |
| AccessTokenSerialize              | BinaryFormatter     | 2,763.771 ns         | NA     | 392 B      | 2.3346       | -            | -            | 4,888 B       |
| AccessTokenSerialize              | DataContract        | 1,583.432 ns         | NA     | 333 B      | 1.2798       | -            | -            | 2,680 B       |
| AccessTokenSerialize              | Jil                 | 454.528 ns           | NA     | 80 B       | 0.4435       | -            | -            | 928 B         |
| AccessTokenSerialize              | SpanJson            | 108.356 ns           | NA     | 53 B       | 0.0381       | -            | -            | 80 B          |
| AccessTokenSerialize              | UTF8Json            | 195.958 ns           | NA     | 79 B       | 0.0496       | -            | -            | 104 B         |
| AccessTokenSerialize              | FsPickler           | 616.381 ns           | NA     | 67 B       | 0.5732       | -            | -            | 1,200 B       |
| AccessTokenSerialize              | Ceras               | 565.154 ns           | NA     | 12 B       | 1.9875       | -            | -            | 4,160 B       |
| AccessTokenSerialize              | Odin                | 1,355.360 ns         | NA     | 440 B      | 0.2441       | -            | -            | 512 B         |
| AccessTokenSerialize              | Nino                | 67.938 ns            | NA     | 18 B       | 0.0229       | -            | -            | 48 B          |
| **AccountMergeDeserialize**       | **MessagePack_Lz4** | **225.228 ns**       | **NA** | **-**      | **0.0458**   | **-**        | **-**        | **96 B**      |
| AccountMergeDeserialize           | MessagePack_NoComp  | 243.476 ns           | NA     | -          | 0.0153       | -            | -            | 32 B          |
| AccountMergeDeserialize           | ProtobufNet         | 842.079 ns           | NA     | -          | 0.0572       | -            | -            | 120 B         |
| AccountMergeDeserialize           | JsonNet             | 1,630.125 ns         | NA     | -          | 2.7466       | -            | -            | 5,752 B       |
| AccountMergeDeserialize           | BinaryFormatter     | 3,492.414 ns         | NA     | -          | 2.3155       | -            | -            | 4,848 B       |
| AccountMergeDeserialize           | DataContract        | 8,672.817 ns         | NA     | -          | 5.9814       | -            | -            | 12,536 B      |
| AccountMergeDeserialize           | Jil                 | 353.636 ns           | NA     | -          | 0.1411       | -            | -            | 296 B         |
| AccountMergeDeserialize           | SpanJson            | 119.976 ns           | NA     | -          | 0.0153       | -            | -            | 32 B          |
| AccountMergeDeserialize           | UTF8Json            | 242.910 ns           | NA     | -          | 0.0153       | -            | -            | 32 B          |
| AccountMergeDeserialize           | FsPickler           | 505.434 ns           | NA     | -          | 0.5884       | -            | -            | 1,232 B       |
| AccountMergeDeserialize           | Ceras               | 269.913 ns           | NA     | -          | 0.0153       | -            | -            | 32 B          |
| AccountMergeDeserialize           | Odin                | 1,600.680 ns         | NA     | -          | 0.2747       | -            | -            | 576 B         |
| AccountMergeDeserialize           | Nino                | 14.258 ns            | NA     | -          | 0.0153       | -            | -            | 32 B          |
| **AccountMergeSerialize**         | **MessagePack_Lz4** | **342.455 ns**       | **NA** | **18 B**   | **0.0534**   | **-**        | **-**        | **112 B**     |
| AccountMergeSerialize             | MessagePack_NoComp  | 95.773 ns            | NA     | 18 B       | 0.0229       | -            | -            | 48 B          |
| AccountMergeSerialize             | ProtobufNet         | 335.132 ns           | NA     | 6 B        | 0.1798       | -            | -            | 376 B         |
| AccountMergeSerialize             | JsonNet             | 895.350 ns           | NA     | 72 B       | 1.5106       | -            | -            | 3,160 B       |
| AccountMergeSerialize             | BinaryFormatter     | 2,409.453 ns         | NA     | 250 B      | 1.8501       | -            | -            | 3,872 B       |
| AccountMergeSerialize             | DataContract        | 1,288.189 ns         | NA     | 253 B      | 1.1806       | -            | -            | 2,472 B       |
| AccountMergeSerialize             | Jil                 | 420.881 ns           | NA     | 70 B       | 0.3443       | -            | -            | 720 B         |
| AccountMergeSerialize             | SpanJson            | 125.590 ns           | NA     | 69 B       | 0.0458       | -            | -            | 96 B          |
| AccountMergeSerialize             | UTF8Json            | 173.364 ns           | NA     | 69 B       | 0.0458       | -            | -            | 96 B          |
| AccountMergeSerialize             | FsPickler           | 616.774 ns           | NA     | 67 B       | 0.5732       | -            | -            | 1,200 B       |
| AccountMergeSerialize             | Ceras               | 518.293 ns           | NA     | 11 B       | 1.9875       | -            | -            | 4,160 B       |
| AccountMergeSerialize             | Odin                | 1,242.706 ns         | NA     | 408 B      | 0.2403       | -            | -            | 504 B         |
| AccountMergeSerialize             | Nino                | 60.147 ns            | NA     | 18 B       | 0.0229       | -            | -            | 48 B          |
| **AnswerDeserialize**             | **MessagePack_Lz4** | **822.464 ns**       | **NA** | **-**      | **0.0992**   | **-**        | **-**        | **208 B**     |
| AnswerDeserialize                 | MessagePack_NoComp  | 411.544 ns           | NA     | -          | 0.0687       | -            | -            | 144 B         |
| AnswerDeserialize                 | ProtobufNet         | 585.853 ns           | NA     | -          | 0.1106       | -            | -            | 232 B         |
| AnswerDeserialize                 | JsonNet             | 15,239.784 ns        | NA     | -          | 2.8839       | -            | -            | 6,056 B       |
| AnswerDeserialize                 | BinaryFormatter     | 7,828.262 ns         | NA     | -          | 4.1962       | -            | -            | 8,784 B       |
| AnswerDeserialize                 | DataContract        | 9,294.727 ns         | NA     | -          | 6.4087       | -            | -            | 13,392 B      |
| AnswerDeserialize                 | Jil                 | 1,712.727 ns         | NA     | -          | 0.5646       | -            | -            | 1,184 B       |
| AnswerDeserialize                 | SpanJson            | 572.279 ns           | NA     | -          | 0.0687       | -            | -            | 144 B         |
| AnswerDeserialize                 | UTF8Json            | 1,214.343 ns         | NA     | -          | 0.0687       | -            | -            | 144 B         |
| AnswerDeserialize                 | FsPickler           | 675.320 ns           | NA     | -          | 0.6342       | -            | -            | 1,328 B       |
| AnswerDeserialize                 | Ceras               | 543.900 ns           | NA     | -          | 0.0687       | -            | -            | 144 B         |
| AnswerDeserialize                 | Odin                | 6,083.301 ns         | NA     | -          | 1.1520       | -            | -            | 2,416 B       |
| AnswerDeserialize                 | Nino                | 63.392 ns            | NA     | -          | 0.0688       | -            | -            | 144 B         |
| **AnswerSerialize**               | **MessagePack_Lz4** | **1,116.501 ns**     | **NA** | **53 B**   | **0.0687**   | **-**        | **-**        | **144 B**     |
| AnswerSerialize                   | MessagePack_NoComp  | 290.072 ns           | NA     | 97 B       | 0.0610       | -            | -            | 128 B         |
| AnswerSerialize                   | ProtobufNet         | 530.705 ns           | NA     | 30 B       | 0.1907       | -            | -            | 400 B         |
| AnswerSerialize                   | JsonNet             | 3,340.096 ns         | NA     | 458 B      | 3.5744       | -            | -            | 7,480 B       |
| AnswerSerialize                   | BinaryFormatter     | 7,903.100 ns         | NA     | 1117 B     | 5.0354       | -            | -            | 10,552 B      |
| AnswerSerialize                   | DataContract        | 3,938.859 ns         | NA     | 883 B      | 2.7542       | -            | -            | 5,768 B       |
| AnswerSerialize                   | Jil                 | 1,518.566 ns         | NA     | 460 B      | 1.4248       | -            | -            | 2,984 B       |
| AnswerSerialize                   | SpanJson            | 430.867 ns           | NA     | 353 B      | 0.1836       | -            | -            | 384 B         |
| AnswerSerialize                   | UTF8Json            | 766.091 ns           | NA     | 455 B      | 0.2289       | -            | -            | 480 B         |
| AnswerSerialize                   | FsPickler           | 818.440 ns           | NA     | 130 B      | 0.6037       | -            | -            | 1,264 B       |
| AnswerSerialize                   | Ceras               | 536.302 ns           | NA     | 58 B       | 2.0113       | -            | -            | 4,208 B       |
| AnswerSerialize                   | Odin                | 4,359.471 ns         | NA     | 1584 B     | 0.9384       | -            | -            | 1,968 B       |
| AnswerSerialize                   | Nino                | 176.799 ns           | NA     | 84 B       | 0.0534       | -            | -            | 112 B         |
| **BadgeDeserialize**              | **MessagePack_Lz4** | **257.866 ns**       | **NA** | **-**      | **0.0534**   | **-**        | **-**        | **112 B**     |
| BadgeDeserialize                  | MessagePack_NoComp  | 151.698 ns           | NA     | -          | 0.0229       | -            | -            | 48 B          |
| BadgeDeserialize                  | ProtobufNet         | 247.663 ns           | NA     | -          | 0.0648       | -            | -            | 136 B         |
| BadgeDeserialize                  | JsonNet             | 1,631.568 ns         | NA     | -          | 2.7332       | -            | -            | 5,720 B       |
| BadgeDeserialize                  | BinaryFormatter     | 2,614.842 ns         | NA     | -          | 2.4223       | -            | -            | 5,072 B       |
| BadgeDeserialize                  | DataContract        | 3,308.971 ns         | NA     | -          | 4.0131       | -            | -            | 8,400 B       |
| BadgeDeserialize                  | Jil                 | 205.040 ns           | NA     | -          | 0.1490       | -            | -            | 312 B         |
| BadgeDeserialize                  | SpanJson            | 56.100 ns            | NA     | -          | 0.0229       | -            | -            | 48 B          |
| BadgeDeserialize                  | UTF8Json            | 193.397 ns           | NA     | -          | 0.0229       | -            | -            | 48 B          |
| BadgeDeserialize                  | FsPickler           | 473.412 ns           | NA     | -          | 0.5889       | -            | -            | 1,232 B       |
| BadgeDeserialize                  | Ceras               | 244.302 ns           | NA     | -          | 0.0229       | -            | -            | 48 B          |
| BadgeDeserialize                  | Odin                | 1,548.200 ns         | NA     | -          | 0.2708       | -            | -            | 568 B         |
| BadgeDeserialize                  | Nino                | 22.069 ns            | NA     | -          | 0.0229       | -            | -            | 48 B          |
| **BadgeSerialize**                | **MessagePack_Lz4** | **370.105 ns**       | **NA** | **9 B**    | **0.0496**   | **-**        | **-**        | **104 B**     |
| BadgeSerialize                    | MessagePack_NoComp  | 136.893 ns           | NA     | 9 B        | 0.0191       | -            | -            | 40 B          |
| BadgeSerialize                    | ProtobufNet         | 201.237 ns           | NA     | 0 B        | 0.0305       | -            | -            | 64 B          |
| BadgeSerialize                    | JsonNet             | 911.790 ns           | NA     | 74 B       | 1.4572       | -            | -            | 3,048 B       |
| BadgeSerialize                    | BinaryFormatter     | 2,540.477 ns         | NA     | 278 B      | 2.1515       | -            | -            | 4,504 B       |
| BadgeSerialize                    | DataContract        | 1,337.075 ns         | NA     | 250 B      | 1.0128       | -            | -            | 2,120 B       |
| BadgeSerialize                    | Jil                 | 360.179 ns           | NA     | 71 B       | 0.4320       | -            | -            | 904 B         |
| BadgeSerialize                    | SpanJson            | 83.969 ns            | NA     | 28 B       | 0.0267       | -            | -            | 56 B          |
| BadgeSerialize                    | UTF8Json            | 119.922 ns           | NA     | 71 B       | 0.0458       | -            | -            | 96 B          |
| BadgeSerialize                    | FsPickler           | 613.739 ns           | NA     | 54 B       | 0.5655       | -            | -            | 1,184 B       |
| BadgeSerialize                    | Ceras               | 500.318 ns           | NA     | 6 B        | 1.9836       | -            | -            | 4,152 B       |
| BadgeSerialize                    | Odin                | 1,324.928 ns         | NA     | 382 B      | 0.2174       | -            | -            | 456 B         |
| BadgeSerialize                    | Nino                | 70.633 ns            | NA     | 16 B       | 0.0191       | -            | -            | 40 B          |
| **CommentDeserialize**            | **MessagePack_Lz4** | **334.182 ns**       | **NA** | **-**      | **0.0610**   | **-**        | **-**        | **128 B**     |
| CommentDeserialize                | MessagePack_NoComp  | 181.110 ns           | NA     | -          | 0.0305       | -            | -            | 64 B          |
| CommentDeserialize                | ProtobufNet         | 295.744 ns           | NA     | -          | 0.0725       | -            | -            | 152 B         |
| CommentDeserialize                | JsonNet             | 2,383.058 ns         | NA     | -          | 2.7618       | -            | -            | 5,784 B       |
| CommentDeserialize                | BinaryFormatter     | 3,624.930 ns         | NA     | -          | 2.7885       | -            | -            | 5,832 B       |
| CommentDeserialize                | DataContract        | 4,538.462 ns         | NA     | -          | 6.0806       | -            | -            | 12,728 B      |
| CommentDeserialize                | Jil                 | 428.218 ns           | NA     | -          | 0.2294       | -            | -            | 480 B         |
| CommentDeserialize                | SpanJson            | 153.482 ns           | NA     | -          | 0.0305       | -            | -            | 64 B          |
| CommentDeserialize                | UTF8Json            | 381.547 ns           | NA     | -          | 0.0305       | -            | -            | 64 B          |
| CommentDeserialize                | FsPickler           | 477.299 ns           | NA     | -          | 0.5960       | -            | -            | 1,248 B       |
| CommentDeserialize                | Ceras               | 266.978 ns           | NA     | -          | 0.0305       | -            | -            | 64 B          |
| CommentDeserialize                | Odin                | 2,442.528 ns         | NA     | -          | 0.5150       | -            | -            | 1,080 B       |
| CommentDeserialize                | Nino                | 27.568 ns            | NA     | -          | 0.0306       | -            | -            | 64 B          |
| **CommentSerialize**              | **MessagePack_Lz4** | **402.587 ns**       | **NA** | **27 B**   | **0.0572**   | **-**        | **-**        | **120 B**     |
| CommentSerialize                  | MessagePack_NoComp  | 170.648 ns           | NA     | 27 B       | 0.0267       | -            | -            | 56 B          |
| CommentSerialize                  | ProtobufNet         | 335.129 ns           | NA     | 6 B        | 0.1798       | -            | -            | 376 B         |
| CommentSerialize                  | JsonNet             | 1,457.594 ns         | NA     | 151 B      | 1.5831       | -            | -            | 3,312 B       |
| CommentSerialize                  | BinaryFormatter     | 3,649.472 ns         | NA     | 403 B      | 2.3956       | -            | -            | 5,016 B       |
| CommentSerialize                  | DataContract        | 1,828.150 ns         | NA     | 361 B      | 1.2875       | -            | -            | 2,696 B       |
| CommentSerialize                  | Jil                 | 675.614 ns           | NA     | 149 B      | 0.5693       | -            | -            | 1,192 B       |
| CommentSerialize                  | SpanJson            | 148.153 ns           | NA     | 104 B      | 0.0610       | -            | -            | 128 B         |
| CommentSerialize                  | UTF8Json            | 272.405 ns           | NA     | 148 B      | 0.0839       | -            | -            | 176 B         |
| CommentSerialize                  | FsPickler           | 676.867 ns           | NA     | 71 B       | 0.5732       | -            | -            | 1,200 B       |
| CommentSerialize                  | Ceras               | 542.838 ns           | NA     | 17 B       | 1.9913       | -            | -            | 4,168 B       |
| CommentSerialize                  | Odin                | 2,077.209 ns         | NA     | 708 B      | 0.4196       | -            | -            | 880 B         |
| CommentSerialize                  | Nino                | 98.829 ns            | NA     | 30 B       | 0.0267       | -            | -            | 56 B          |
| **NestedDataDeserialize**         | **MessagePack_Lz4** | **1,647,207.397 ns** | **NA** | **-**      | **242.1875** | **242.1875** | **242.1875** | **940,524 B** |
| NestedDataDeserialize             | MessagePack_NoComp  | 1,627,367.430 ns     | NA     | -          | 140.6250     | 140.6250     | 140.6250     | 560,181 B     |
| NestedDataDeserialize             | ProtobufNet         | 2,030,716.145 ns     | NA     | -          | 132.8125     | 132.8125     | 132.8125     | 561,363 B     |
| NestedDataDeserialize             | JsonNet             | 23,441,751.312 ns    | NA     | -          | 2375.0000    | 687.5000     | 562.5000     | 6,082,907 B   |
| NestedDataDeserialize             | BinaryFormatter     | 34,384,927.800 ns    | NA     | -          | 3400.0000    | 1200.0000    | 600.0000     | 12,696,020 B  |
| NestedDataDeserialize             | DataContract        | 22,034,322.938 ns    | NA     | -          | 1500.0000    | 468.7500     | 468.7500     | 4,489,114 B   |
| NestedDataDeserialize             | Jil                 | 5,994,985.031 ns     | NA     | -          | 1492.1875    | 984.3750     | 976.5625     | 5,378,328 B   |
| NestedDataDeserialize             | SpanJson            | 3,181,319.576 ns     | NA     | -          | 97.6563      | 97.6563      | 97.6563      | 560,972 B     |
| NestedDataDeserialize             | UTF8Json            | 8,334,485.031 ns     | NA     | -          | 734.3750     | 468.7500     | 468.7500     | 2,449,194 B   |
| NestedDataDeserialize             | FsPickler           | 736,832.479 ns       | NA     | -          | 133.7891     | 132.8125     | 132.8125     | 562,184 B     |
| NestedDataDeserialize             | Ceras               | 52,568.774 ns        | NA     | -          | 59.0210      | 58.4717      | 58.4717      | 560,549 B     |
| NestedDataDeserialize             | Odin                | 22,131,333.312 ns    | NA     | -          | 2750.0000    | 312.5000     | 312.5000     | 6,996,090 B   |
| NestedDataDeserialize             | Nino                | 52,758.466 ns        | NA     | -          | 81.7871      | 81.1157      | 81.1157      | 560,681 B     |
| **NestedDataSerialize**           | **MessagePack_Lz4** | **1,096,706.258 ns** | **NA** | **1553 B** | **-**        | **-**        | **-**        | **1,650 B**   |
| NestedDataSerialize               | MessagePack_NoComp  | 1,137,632.162 ns     | NA     | 380010 B   | 97.6563      | 97.6563      | 97.6563      | 380,106 B     |
| NestedDataSerialize               | ProtobufNet         | 1,812,959.635 ns     | NA     | 410006 B   | 394.5313     | 335.9375     | 332.0313     | 1,465,476 B   |
| NestedDataSerialize               | JsonNet             | 17,805,178.375 ns    | NA     | 920025 B   | 2625.0000    | 718.7500     | 687.5000     | 6,950,126 B   |
| NestedDataSerialize               | BinaryFormatter     | 61,950,829.100 ns    | NA     | 580388 B   | 3600.0000    | 600.0000     | 500.0000     | 9,165,155 B   |
| NestedDataSerialize               | DataContract        | 19,313,885.438 ns    | NA     | 1190173 B  | 2031.2500    | 625.0000     | 593.7500     | 8,184,938 B   |
| NestedDataSerialize               | Jil                 | 21,180,697.281 ns    | NA     | 1010022 B  | 1250.0000    | 937.5000     | 468.7500     | 6,513,530 B   |
| NestedDataSerialize               | SpanJson            | 7,003,992.676 ns     | NA     | 1010022 B  | 179.6875     | 179.6875     | 179.6875     | 1,010,171 B   |
| NestedDataSerialize               | UTF8Json            | 7,813,969.062 ns     | NA     | 1010022 B  | 1062.5000    | 640.6250     | 640.6250     | 3,857,248 B   |
| NestedDataSerialize               | FsPickler           | 2,363,512.775 ns     | NA     | 470066 B   | 390.6250     | 332.0313     | 328.1250     | 1,520,604 B   |
| NestedDataSerialize               | Ceras               | 191,736.003 ns       | NA     | 560009 B   | 77.8809      | 75.4395      | 75.4395      | 1,612,991 B   |
| NestedDataSerialize               | Odin                | 26,701,454.406 ns    | NA     | 1280351 B  | 4812.5000    | 937.5000     | 875.0000     | 12,728,596 B  |
| NestedDataSerialize               | Nino                | 291,598.938 ns       | NA     | 560022 B   | 126.9531     | 125.4883     | 125.4883     | 561,173 B     |
