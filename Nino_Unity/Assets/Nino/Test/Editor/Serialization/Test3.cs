using System.IO;
using System.Text;
using Nino.Shared.Util;
using System.Diagnostics;
using System.Collections.Generic;
// ReSharper disable RedundantJumpStatement

namespace Nino.Test.Editor.Serialization
{
    public class Test3
    {
        private const string SerializationTest3 = "Nino/Test/Serialization/Test3 - Deserialize (Nino vs Protobuf-net)";

        private static string GetString(int len)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append('a', len);
            return sb.ToString();
        }

#if UNITY_2017_1_OR_NEWER
        [UnityEditor.MenuItem(SerializationTest3,priority=3)]
#endif
        public static void Main()
        {
            Logger.W("1/5");
            DoTest(10);
            Logger.W("2/5");
            DoTest(100);
            Logger.W("3/5");
            DoTest(1000);
            Logger.W("4/5");
            DoTest(10000);
            Logger.W("5/5");
            DoTest(100000);
        }

        private static void BeginSample(string name)
        {
#if UNITY_2017_1_OR_NEWER
            UnityEngine.Profiling.Profiler.BeginSample(name);
#endif
            return;
        }

        private static void EndSample()
        {
#if UNITY_2017_1_OR_NEWER
            UnityEngine.Profiling.Profiler.EndSample();
#endif
            return;
        }

        private static void DoTest(int max)
        {
            #region Test data

            Data[] ps = new Data[max];
            for (int i = 0, cnt = max; i < cnt; i++)
            {
                ps[i] = new Data()
                {
                    x = short.MaxValue,
                    y = byte.MaxValue,
                    z = short.MaxValue,
                    f = 1234.56789f,
                    d = 66.66666666m,
                    db = 999.999999999999,
                    bo = true,
                    en = TestEnum.A,
                };
            }

            NestedData points = new NestedData()
            {
                name = "测试",
                ps = ps
            };

            #endregion

            #region Test

            Logger.D("Deserialization Test", $"<color=cyan>testing {max} objs</color>");
            NestedData2 d;

            //Nino
            var sw = new Stopwatch();
            var bs = Nino.Serialization.Serializer.Serialize(points);
            BeginSample("Nino");
            sw.Restart();
            d = Nino.Serialization.Deserializer.Deserialize<NestedData2>(bs);
            sw.Stop();
            EndSample();
            Logger.D("Deserialization Test", d);
            Logger.D("Deserialization Test",
                $"Nino: extracted {bs.Length} bytes and deserialized {points.ps.Length} entries in {sw.ElapsedMilliseconds}ms");
            var tm = sw.ElapsedMilliseconds;

            //Protobuf-net
            //we want byte[], pbnet returns stream
            //to be able to make it fair, we need to convert stream to byte[]
            using (var ms = new MemoryStream())
            {
                ProtoBuf.Serializer.Serialize(ms, points);
                bs = ms.ToArray();
            }

            BeginSample("PB-net");
            sw.Restart();
            d = ProtoBuf.Serializer.Deserialize<NestedData2>(new MemoryStream(bs));
            sw.Stop();
            EndSample();
            Logger.D("Deserialization Test", d);
            Logger.D("Deserialization Test",
                $"Protobuf-net: extracted {bs.Length} bytes and deserialized {points.ps.Length} entries in {sw.ElapsedMilliseconds}ms");

            Logger.D("Deserialization Test", "======================================");
            Logger.D("Deserialization Test", $"time diff (nino - protobuf): {tm - sw.ElapsedMilliseconds} ms");
            Logger.D("Deserialization Test",
                $"time diff pct => time/protobuf : {((tm - sw.ElapsedMilliseconds) * 100f / sw.ElapsedMilliseconds):F2}%");

            #endregion
        }
    }
}