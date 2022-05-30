using System;
using ProtoBuf;
using System.IO;
using System.Text;
using System.Diagnostics;
using Nino.Serialization;

public class Test
{

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
    private static string GetString(int len)
    {
        StringBuilder sb = new StringBuilder();
        sb.Append('a', len);
        return sb.ToString();
    }

    public static void Main(string[] args)
    {
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
                name = GetString(20)
            };
        }
        Datas points = new Datas()
        {
            name = "测试",
            ps = ps
        };
        #endregion

        #region Test
        long len;
        long tm;

        Console.WriteLine($"testing {max} objs");
        Console.WriteLine("======================================");

        //Nino
        Stopwatch sw = new Stopwatch();
        sw.Restart();
        var bs = Nino.Serialization.Serializer.Serialize(points);
        sw.Stop();
        Console.WriteLine($"Nino: {bs.Length} bytes in {sw.ElapsedMilliseconds}ms");
        len = bs.Length;
        tm = sw.ElapsedMilliseconds;
        //Console.WriteLine(string.Join(",", bs));

        //Protobuf-net
        var ms = new MemoryStream();
        sw.Restart();
        ProtoBuf.Serializer.Serialize(ms, points);
        sw.Stop();
        bs = ms.ToArray();
        Console.WriteLine($"Protobuf-net: {bs.Length} bytes in {sw.ElapsedMilliseconds}ms");
        //Console.WriteLine(string.Join(",", bs));

        Console.WriteLine("======================================");
        Console.WriteLine($"size diff (nino - protobuf): {len - bs.Length} bytes");
        Console.WriteLine($"size diff pct => diff/protobuf : {((float)(len - bs.Length) * 100f / (float)bs.Length):F2}%");

        Console.WriteLine("======================================");
        Console.WriteLine($"time diff (nino - protobuf): {tm - sw.ElapsedMilliseconds} ms");
        Console.WriteLine($"time diff pct => time/protobuf : {((float)(tm - sw.ElapsedMilliseconds) * 100f / (float)sw.ElapsedMilliseconds):F2}%");
        #endregion
    }
}