using System;
using ProtoBuf;
using System.IO;
using System.Text;
using System.Diagnostics;
using Nino.Serialization.Attributes;

[ProtoContract]
[NinoSerialize]
public struct Point
{
    [ProtoMember(1)]
    [SerializeProperty(0)] public long x;
    [ProtoMember(2)]
    [SerializeProperty(1)] private long y;
    [ProtoMember(3)]
    [SerializeProperty(2)] private string c;

    public Point(int x, long y,string c)
    {
        this.x = x;
        this.y = y;
        this.c = c;
    }
}

[ProtoContract]
[NinoSerialize]
public class Points
{
    [ProtoMember(1)]
    [SerializeProperty(0)] public string name;
    [ProtoMember(2)]
    [SerializeProperty(1)] public Point[] ps;
}

public class Program
{
    public static void Main(string[] args)
    {
        #region Test data
        int max = 300;
        Point[] ps = new Point[max];
        for (int i = 0, cnt = max; i < cnt; i++)
        {
            ps[i] = new Point(i, max - i, GetStr(i * i));
        }
        Points points = new Points()
        {
            name = "测试",
            ps = ps
        };
        #endregion

        #region Test
        long len;
        long tm;

        Console.WriteLine($"testing 1*string + {max}*point");
        Console.WriteLine("point = long+long+string");
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

    public static string GetStr(int len)
    {
        StringBuilder builder = new StringBuilder();
        for (int i = 0; i < len; i++)
        {
            builder.Append((char)i);
        }
        return builder.ToString();
    }
}