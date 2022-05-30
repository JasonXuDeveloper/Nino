# Nino
Useful C# Tools | 实用的C#工具


# 功能列表

- 序列化模块
  > Protobuf-net平替方案，目标是更小体积，更高性能
  - 序列化【于2022年5月30日完成】
  - 反序列化【预计2022年6月完成】



# 测试结果

- 序列化模块

  测试类型：
  ```csharp
  [ProtoContract]
  [NinoSerialize]
  public struct Data
  {
      [ProtoMember(1)]
      [SerializeProperty(1)]
      public int x;

      [ProtoMember(2)]
      [SerializeProperty(2)]
      public short y;

      [ProtoMember(3)]
      [SerializeProperty(3)]
      public long z;

      [ProtoMember(4)]
      [SerializeProperty(4)]
      public float f;

      [ProtoMember(5)]
      [SerializeProperty(5)]
      public decimal d;

      [ProtoMember(6)]
      [SerializeProperty(6)]
      public double db;

      [ProtoMember(7)]
      [SerializeProperty(7)]
      public bool bo;

      [ProtoMember(8)]
      [SerializeProperty(8)]
      public TestEnum en;

      [ProtoMember(9)]
      [SerializeProperty(9)]
      public string name;
  }
  [ProtoContract]
  public enum TestEnum : byte
  {
      a = 1,
      b = 2
  }

  [ProtoContract]
  [NinoSerialize]
  public class Datas
  {
      [ProtoMember(1)]
      [SerializeProperty(1)] public string name;
      [ProtoMember(2)]
      [SerializeProperty(2)] public Data[] ps;
  }
  ```
  
  测试数据：
  ```csharp
  #region Test data
  int max = 10000;
  Data[] ps = new Data[max];
  for (int i = 0, cnt = max; i < cnt; i++)
  {
      ps[i] = new Data()
      {
          x = short.MaxValue,
          y = byte.MaxValue,
          z = int.MaxValue,
          f = 1234.56789f,
          d = 66.66666666m,
          db = 999.999999999999,
          bo = true,
          en = TestEnum.a,
          name = GetString(20)//长度20的字符串
      };
  }
  Datas points = new Datas()
  {
      name = "测试",
      ps = ps
  };
  #endregion
  ```
  测试结果：
  
  > *注：测试过程无预热无Emit，模拟IL2CPP下游戏运行时进行数据序列化的场景*
  
  ```txt
  testing 10000 objs 
  ======================================
  Nino: 560008 bytes in 65ms
  Protobuf—net: 650008 bytes in 168ms
  ======================================
  size diff (nino — protobuf): —90000 bytes
  size diff pct => diff/protobuf : —13.85% 
  ======================================
  time diff (nino — protobuf): —103 ms
  time diff pct => time/protobuf : —61.31% 

  ```
