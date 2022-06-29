using System.IO;
using System.Text;
using Nino.Shared.Util;
using System.Diagnostics;
using System.Collections.Generic;
using System.Runtime.Serialization.Formatters.Binary;
// ReSharper disable RedundantJumpStatement

namespace Nino.Test.Editor.Serialization
{
    public class Test4
    {
        private const string SerializationTest4 = "Nino/Test/Serialization/Test4 - Deserialize (Nino vs BinaryFormatter)";

        private static string GetString(int len)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append('a', len);
            return sb.ToString();
        }

#if UNITY_2017_1_OR_NEWER
        [UnityEditor.MenuItem(SerializationTest4,priority=4)]
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
                    name = GetString(20)
                };
            }

            NestedData2 points = new NestedData2()
            {
                name = "测试",
                ps = ps,
                vs = new List<int>(){1,65535,65536,1234567,int.MaxValue}
            };

            #endregion

            #region Test

            Logger.D("Deserialization Test", $"<color=cyan>testing {max} objs</color>");
            var sizeOfNestedData = Encoding.Default.GetByteCount(points.name) +
                                   (sizeof(int) + sizeof(short) + sizeof(long) + sizeof(float) + sizeof(double) +
                                    sizeof(decimal) + sizeof(bool) + sizeof(byte) +
                                    Encoding.Default.GetByteCount(points.ps[0].name)) * points.ps.Length+
                                   5 * sizeof(int);
            Logger.D("Deserialization Test", $"marshal.sizeof struct: {sizeOfNestedData} bytes");
            Logger.D("Deserialization Test", "======================================");

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

            //BinaryFormatter
            using (var ms = new MemoryStream())
            {
                BinaryFormatter bFormatter = new BinaryFormatter();
                bFormatter.Serialize(ms, points);
                bs = ms.ToArray();
            }

            BeginSample("BinaryFormatter");
            sw.Restart();
            using (var ms = new MemoryStream(bs))
            {
                BinaryFormatter bFormatter = new BinaryFormatter();
                d = (NestedData2)bFormatter.Deserialize(ms);
            }
            sw.Stop();
            EndSample();
            Logger.D("Deserialization Test", d);
            Logger.D("Deserialization Test",
                $"BinaryFormatter: extracted {bs.Length} bytes and deserialized {points.ps.Length} entries in {sw.ElapsedMilliseconds}ms");

            Logger.D("Deserialization Test", "======================================");
            Logger.D("Deserialization Test", $"time diff (nino - BinaryFormatter): {tm - sw.ElapsedMilliseconds} ms");
            Logger.D("Deserialization Test",
                $"time diff pct => time/BinaryFormatter : {((tm - sw.ElapsedMilliseconds) * 100f / sw.ElapsedMilliseconds):F2}%");

            #endregion
        }
    }
}