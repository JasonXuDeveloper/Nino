using System;
using MessagePack;
using System.Text;
using System.Diagnostics;
using Test_Nino;
using MessagePack.Resolvers;

// ReSharper disable RedundantJumpStatement

namespace Nino.Test.Editor.Serialization
{
    public class Test6
    {
        private const string SerializationTest6 =
            "Nino/Test/Serialization/Test6 - Serialize and Deserialize (Nino vs MsgPack)";

        private static string GetString(int len)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append('a', len);
            return sb.ToString();
        }

#if UNITY_2017_1_OR_NEWER
        [UnityEditor.MenuItem(SerializationTest6, priority = 6)]
#endif
        public static void Main()
        {
            try
            {
                StaticCompositeResolver.Instance.Register(
                    GeneratedResolver.Instance,
                    BuiltinResolver.Instance,
                    AttributeFormatterResolver.Instance,
                    MessagePack.Unity.UnityResolver.Instance,
                    PrimitiveObjectResolver.Instance,
                    MessagePack.Unity.Extension.UnityBlitWithPrimitiveArrayResolver.Instance,
                    StandardResolver.Instance
                );
                var option = MessagePackSerializerOptions.Standard.WithResolver(StaticCompositeResolver.Instance);
                MessagePackSerializer.DefaultOptions = option;
            }
            catch
            {
                //ignore
            }

            Logger.W("1/5");
            BeginSample("Array len of 10");
            DoTest(10);
            EndSample();
            Logger.W("2/5");
            BeginSample("Array len of 100");
            DoTest(100);
            EndSample();
            Logger.W("3/5");
            BeginSample("Array len of 1000");
            DoTest(1000);
            EndSample();
            Logger.W("4/5");
            BeginSample("Array len of 10000");
            DoTest(10000);
            EndSample();
            Logger.W("5/5");
            BeginSample("Array len of 100000");
            DoTest(100000);
            EndSample();
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

            Logger.D("Serialization Test", $"<color=cyan>testing {max} objs</color>");
            Logger.D("Serialization Test", "======================================");

            //Nino
            var sw = new Stopwatch();
            BeginSample("Nino - Serialize");
            sw.Restart();
            
                byte[] ret = points.Serialize();
                sw.Stop();
                EndSample();
                Logger.D("Serialization Test", $"Nino: {ret.Length} bytes in {sw.ElapsedMilliseconds}ms");
                var len = ret.Length;
                var tm = sw.ElapsedMilliseconds;

                //MsgPack
                BeginSample("MsgPack - Serialize");
                byte[] bs2;
                sw.Restart();
                var lz4Options = MessagePackSerializerOptions.Standard.WithCompression(MessagePackCompression.None);
                bs2 = MessagePackSerializer.Serialize(points, lz4Options);
                sw.Stop();
                EndSample();

                Logger.D("Serialization Test", $"MsgPack: {bs2.Length} bytes in {sw.ElapsedMilliseconds}ms");
                //Logger.D("Serialization Test",string.Join(",", bs));

                Logger.D("Serialization Test", "======================================");
                Logger.D("Serialization Test", $"size diff (nino - MsgPack): {len - bs2.Length} bytes");
                Logger.D("Serialization Test",
                    $"size diff pct => diff/MsgPack : {((len - bs2.Length) * 100f / bs2.Length):F2}%");

                Logger.D("Serialization Test", "======================================");
                Logger.D("Serialization Test", $"time diff (nino - MsgPack): {tm - sw.ElapsedMilliseconds} ms");
                Logger.D("Serialization Test",
                    $"time diff pct => time/MsgPack : {((tm - sw.ElapsedMilliseconds) * 100f / sw.ElapsedMilliseconds):F2}%");

                BeginSample("Nino - Deserialize");
                sw.Restart();
                Deserializer.Deserialize(ret, out NestedData d);
                sw.Stop();
                EndSample();
                Logger.D("Deserialization Test", d);
                Logger.D("Deserialization Test",
                    $"Nino: extracted {len} bytes and deserialized {points.ps.Length} entries in {sw.ElapsedMilliseconds}ms");
                tm = sw.ElapsedMilliseconds;

                //MsgPack
                BeginSample("MsgPack - Deserialize");
                sw.Restart();
                d = MessagePackSerializer.Deserialize<NestedData>(bs2);
                sw.Stop();
                EndSample();
                Logger.D("Deserialization Test", d);
                Logger.D("Deserialization Test",
                    $"MsgPack: extracted {bs2.Length} bytes and deserialized {points.ps.Length} entries in {sw.ElapsedMilliseconds}ms");

                Logger.D("Deserialization Test", "======================================");
                Logger.D("Deserialization Test", $"time diff (nino - MsgPack): {tm - sw.ElapsedMilliseconds} ms");
                Logger.D("Deserialization Test",
                    $"time diff pct => time/MsgPack : {((tm - sw.ElapsedMilliseconds) * 100f / sw.ElapsedMilliseconds):F2}%");

            //Logger.D("Serialization Test",string.Join(",", bs));

            #endregion
        }
    }
}