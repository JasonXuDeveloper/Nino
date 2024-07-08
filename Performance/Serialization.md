# Serilization 性能报告

## 非Unity平台性能测试

#### [**测试数据**](/src/Nino.Benchmark/Data.cs)

```
BenchmarkDotNet v0.13.12, macOS Sonoma 14.4 (23E214) [Darwin 23.4.0]
Apple M1, 1 CPU, 8 logical and 8 physical cores
.NET SDK 8.0.302
  [Host]     : .NET 8.0.6 (8.0.624.26715), Arm64 RyuJIT AdvSIMD
  Job-QCQJSF : .NET 8.0.6 (8.0.624.26715), Arm64 RyuJIT AdvSIMD

Runtime=.NET 8.0  IterationCount=10  WarmupCount=3  
```

| Method                              | Categories               | Mean           | Error       | StdDev      | Median         | Min            | Max            | Ratio | RatioSD | Payload  |
| ----------------------------------- | ------------------------ | --------------:| -----------:| -----------:| --------------:| --------------:| --------------:| -----:| -------:| --------:|
| MessagePackDeserializeSimpleClass   | SimpleClassDeserialize   | 1,148.2660 ns  | 8.8036 ns   | 4.6045 ns   | 1,149.4530 ns  | 1,139.8289 ns  | 1,154.8804 ns  | 1.00  | 0.00    | -        |
| MemoryPackDeserializeSimpleClass    | SimpleClassDeserialize   | 449.8288 ns    | 80.2214 ns  | 53.0615 ns  | 417.1767 ns    | 414.1244 ns    | 552.1924 ns    | 0.40  | 0.05    | -        |
| NinoDeserializeSimpleClass          | SimpleClassDeserialize   | 298.9580 ns    | 4.5691 ns   | 2.3897 ns   | 297.8766 ns    | 296.8175 ns    | 302.7422 ns    | 0.26  | 0.00    | -        |
|                                     |                          |                |             |             |                |                |                |       |         |          |
| MessagePackSerializeSimpleClass     | SimpleClassSerialize     | 1,296.4875 ns  | 8.5089 ns   | 5.6281 ns   | 1,294.8236 ns  | 1,288.4005 ns  | 1,308.2755 ns  | 1.00  | 0.00    | 674 B    |
| MemoryPackSerializeSimpleClass      | SimpleClassSerialize     | 425.2460 ns    | 13.3104 ns  | 7.9208 ns   | 422.3036 ns    | 419.2683 ns    | 443.3696 ns    | 0.33  | 0.01    | 730 B    |
| NinoSerializeSimpleClass            | SimpleClassSerialize     | 207.5860 ns    | 2.8659 ns   | 1.7055 ns   | 207.1263 ns    | 205.9621 ns    | 211.1342 ns    | 0.16  | 0.00    | 738 B    |
|                                     |                          |                |             |             |                |                |                |       |         |          |
| MessagePackDeserializeSimpleClasses | SimpleClassesDeserialize | 34,063.6150 ns | 188.5014 ns | 112.1741 ns | 34,046.4960 ns | 33,931.1091 ns | 34,243.4489 ns | 1.00  | 0.00    | -        |
| MemoryPackDeserializeSimpleClasses  | SimpleClassesDeserialize | 12,436.6578 ns | 230.5653 ns | 137.2057 ns | 12,428.7631 ns | 12,297.8020 ns | 12,669.8284 ns | 0.37  | 0.00    | -        |
| NinoDeserializeSimpleClasses        | SimpleClassesDeserialize | 9,177.5588 ns  | 48.4882 ns  | 25.3603 ns  | 9,178.1489 ns  | 9,129.7035 ns  | 9,208.5476 ns  | 0.27  | 0.00    | -        |
|                                     |                          |                |             |             |                |                |                |       |         |          |
| MessagePackSerializeSimpleClasses   | SimpleClassesSerialize   | 37,935.0473 ns | 122.6102 ns | 72.9634 ns  | 37,967.4072 ns | 37,833.4935 ns | 38,030.5862 ns | 1.00  | 0.00    | 19.75 KB |
| MemoryPackSerializeSimpleClasses    | SimpleClassesSerialize   | 12,568.2318 ns | 167.2008 ns | 99.4985 ns  | 12,513.6725 ns | 12,480.6849 ns | 12,734.0075 ns | 0.33  | 0.00    | 21.39 KB |
| NinoSerializeSimpleClasses          | SimpleClassesSerialize   | 5,404.4797 ns  | 24.6760 ns  | 12.9060 ns  | 5,401.7556 ns  | 5,388.4586 ns  | 5,423.2680 ns  | 0.14  | 0.00    | 21.63 KB |
|                                     |                          |                |             |             |                |                |                |       |         |          |
| MessagePackDeserializeSimpleStruct  | SimpleStructDeserialize  | 47.3220 ns     | 0.1848 ns   | 0.1100 ns   | 47.3206 ns     | 47.1874 ns     | 47.5376 ns     | 1.00  | 0.00    | -        |
| MemoryPackDeserializeSimpleStruct   | SimpleStructDeserialize  | 1.5991 ns      | 0.0052 ns   | 0.0031 ns   | 1.5996 ns      | 1.5946 ns      | 1.6045 ns      | 0.03  | 0.00    | -        |
| NinoDeserializeSimpleStruct         | SimpleStructDeserialize  | 0.5910 ns      | 0.0424 ns   | 0.0280 ns   | 0.5705 ns      | 0.5690 ns      | 0.6393 ns      | 0.01  | 0.00    | -        |
|                                     |                          |                |             |             |                |                |                |       |         |          |
| MessagePackSerializeSimpleStruct    | SimpleStructSerialize    | 93.2237 ns     | 0.0910 ns   | 0.0602 ns   | 93.2200 ns     | 93.1300 ns     | 93.3208 ns     | 1.00  | 0.00    | 16 B     |
| MemoryPackSerializeSimpleStruct     | SimpleStructSerialize    | 4.2233 ns      | 0.0841 ns   | 0.0500 ns   | 4.2158 ns      | 4.1699 ns      | 4.3188 ns      | 0.05  | 0.00    | 16 B     |
| NinoSerializeSimpleStruct           | SimpleStructSerialize    | 4.1687 ns      | 2.1087 ns   | 1.3948 ns   | 3.2187 ns      | 3.1891 ns      | 6.9510 ns      | 0.04  | 0.01    | 16 B     |
|                                     |                          |                |             |             |                |                |                |       |         |          |
| MessagePackDeserializeSimpleStructs | SimpleStructsDeserialize | 924.2507 ns    | 91.8532 ns  | 54.6604 ns  | 895.5899 ns    | 872.0001 ns    | 1,039.7354 ns  | 1.00  | 0.00    | -        |
| MemoryPackDeserializeSimpleStructs  | SimpleStructsDeserialize | 53.9092 ns     | 12.0889 ns  | 7.9961 ns   | 49.8358 ns     | 48.1084 ns     | 68.8055 ns     | 0.06  | 0.01    | -        |
| NinoDeserializeSimpleStructs        | SimpleStructsDeserialize | 31.7620 ns     | 0.4291 ns   | 0.2554 ns   | 31.6444 ns     | 31.4062 ns     | 32.0834 ns     | 0.03  | 0.00    | -        |
|                                     |                          |                |             |             |                |                |                |       |         |          |
| MessagePackSerializeSimpleStructs   | SimpleStructsSerialize   | 2,264.9154 ns  | 2.6982 ns   | 1.7847 ns   | 2,264.6615 ns  | 2,262.4969 ns  | 2,267.9900 ns  | 1.00  | 0.00    | 483 B    |
| MemoryPackSerializeSimpleStructs    | SimpleStructsSerialize   | 36.5122 ns     | 0.8214 ns   | 0.4296 ns   | 36.5239 ns     | 35.8662 ns     | 37.0155 ns     | 0.02  | 0.00    | 484 B    |
| NinoSerializeSimpleStructs          | SimpleStructsSerialize   | 29.5231 ns     | 0.4328 ns   | 0.2863 ns   | 29.4973 ns     | 29.1184 ns     | 30.0484 ns     | 0.01  | 0.00    | 486 B    |
