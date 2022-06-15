using System.IO;
using System.Text;
using Nino.Shared.Util;
using System.Diagnostics;
using System.Runtime.Serialization.Formatters.Binary;
// ReSharper disable RedundantJumpStatement

namespace Nino.Test.Editor.Serialization
{
    public class Test2
    {
        private const string SerializationTest2 = "Nino/Test/Serialization/Test2 - Serialize (Nino vs BinaryFormatter)";

        private static string GetString(int len)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append('a', len);
            return sb.ToString();
        }

#if UNITY_2017_1_OR_NEWER
        [UnityEditor.MenuItem(SerializationTest2)]
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

            NestedData points = new NestedData()
            {
                name = "测试",
                ps = ps
            };

            #endregion

            #region Test

            Logger.D("Serialization Test", $"<color=cyan>testing {max} objs</color>");
            var sizeOfNestedData = Encoding.Default.GetByteCount(points.name) +
                                   (sizeof(int) + sizeof(short) + sizeof(long) + sizeof(float) + sizeof(double) +
                                    sizeof(decimal) + sizeof(bool) + sizeof(byte) +
                                    Encoding.Default.GetByteCount(points.ps[0].name)) * points.ps.Length;
            Logger.D("Serialization Test", $"marshal.sizeof struct: {sizeOfNestedData} bytes");
            Logger.D("Serialization Test", "======================================");

            //Nino
            var sw = new Stopwatch();
            BeginSample("Nino");
            sw.Restart();
            var bs = Nino.Serialization.Serializer.Serialize(points);
            sw.Stop();
            EndSample();
            Logger.D("Serialization Test", $"Nino: {bs.Length} bytes in {sw.ElapsedMilliseconds}ms");
            long len = bs.Length;
            var tm = sw.ElapsedMilliseconds;
            //Logger.D("Serialization Test",string.Join(",", bs));

            //BinaryFormatter
            BeginSample("BinaryFormatter");
            sw.Restart();
            //we want byte[], BinaryFormatter returns stream
            //to be able to make it fair, we need to convert stream to byte[]
            using (var ms = new MemoryStream())
            {
                BinaryFormatter bFormatter = new BinaryFormatter();
                bFormatter.Serialize(ms, points);
                bs = ms.ToArray();
            }
            sw.Stop();
            EndSample();

            Logger.D("Serialization Test", $"BinaryFormatter: {bs.Length} bytes in {sw.ElapsedMilliseconds}ms");
            //Logger.D("Serialization Test",string.Join(",", bs));

            Logger.D("Serialization Test", "======================================");
            Logger.D("Serialization Test", $"size diff (nino - BinaryFormatter): {len - bs.Length} bytes");
            Logger.D("Serialization Test",
                $"size diff pct => diff/BinaryFormatter : {((len - bs.Length) * 100f / bs.Length):F2}%");

            Logger.D("Serialization Test", "======================================");
            Logger.D("Serialization Test", $"time diff (nino - BinaryFormatter): {tm - sw.ElapsedMilliseconds} ms");
            Logger.D("Serialization Test",
                $"time diff pct => time/BinaryFormatter : {((tm - sw.ElapsedMilliseconds) * 100f / sw.ElapsedMilliseconds):F2}%");

            #endregion
        }
    }
}
