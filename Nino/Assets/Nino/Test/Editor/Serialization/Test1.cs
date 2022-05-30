using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Nino.Serialization;
using Nino.Shared;
using ProtoBuf;
using UnityEditor;

namespace Nino.Test.Editor.Serialization
{
    public class Test1
    {
        private const string SerializationTest1 = "Nino/Test/Serialization/Test1 (Nino vs Protobuf-net) #S";

        [ProtoContract]
        [NinoSerialize]
        private struct Data
        {
            [ProtoMember(1)] [SerializeProperty(1)]
            public int x;

            [ProtoMember(2)] [SerializeProperty(2)]
            public short y;

            [ProtoMember(3)] [SerializeProperty(3)]
            public long z;

            [ProtoMember(4)] [SerializeProperty(4)]
            public float f;

            [ProtoMember(5)] [SerializeProperty(5)]
            public decimal d;

            [ProtoMember(6)] [SerializeProperty(6)]
            public double db;

            [ProtoMember(7)] [SerializeProperty(7)]
            public bool bo;

            [ProtoMember(8)] [SerializeProperty(8)]
            public TestEnum en;

            [ProtoMember(9)] [SerializeProperty(9)]
            public string name;
        }

        [ProtoContract]
        private enum TestEnum : byte
        {
            A = 1,
            B = 2
        }

        [ProtoContract]
        [NinoSerialize]
        private struct NestedData
        {
            [ProtoMember(1)] [SerializeProperty(1)]
            public string name;

            [ProtoMember(2)] [SerializeProperty(2)]
            public Data[] ps;
        }

        private static string GetString(int len)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append('a', len);
            return sb.ToString();
        }

        [MenuItem(SerializationTest1)]
        public static void Main()
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
                    en = TestEnum.A,
                    name = GetString(20)
                };
            }

            NestedData points = new NestedData()
            {
                name = "测试",
                ps = ps
            };

            #endregion

            #region Test

            Logger.D("Serialization Test", $"testing {max} objs");
            var sizeOfNestedData = Encoding.Default.GetByteCount(points.name) +
                                   Marshal.SizeOf(points.ps[0]) * points.ps.Length;
            Logger.D("Serialization Test", $"marshal.sizeof struct: {sizeOfNestedData} bytes");
            Logger.D("Serialization Test", "======================================");

            //Nino
            var sw = new Stopwatch();
            sw.Restart();
            var bs = Nino.Serialization.Serializer.Serialize(points);
            sw.Stop();
            Logger.D("Serialization Test", $"Nino: {bs.Length} bytes in {sw.ElapsedMilliseconds}ms");
            long len = bs.Length;
            var tm = sw.ElapsedMilliseconds;
            //Logger.D("Serialization Test",string.Join(",", bs));

            //Protobuf-net
            var ms = new MemoryStream();
            sw.Restart();
            ProtoBuf.Serializer.Serialize(ms, points);
            sw.Stop();
            bs = ms.ToArray();
            Logger.D("Serialization Test", $"Protobuf-net: {bs.Length} bytes in {sw.ElapsedMilliseconds}ms");
            //Logger.D("Serialization Test",string.Join(",", bs));

            Logger.D("Serialization Test", "======================================");
            Logger.D("Serialization Test", $"size diff (nino - protobuf): {len - bs.Length} bytes");
            Logger.D("Serialization Test",
                $"size diff pct => diff/protobuf : {((len - bs.Length) * 100f / bs.Length):F2}%");

            Logger.D("Serialization Test", "======================================");
            Logger.D("Serialization Test", $"time diff (nino - protobuf): {tm - sw.ElapsedMilliseconds} ms");
            Logger.D("Serialization Test",
                $"time diff pct => time/protobuf : {((tm - sw.ElapsedMilliseconds) * 100f / sw.ElapsedMilliseconds):F2}%");

            #endregion
        }
    }
}