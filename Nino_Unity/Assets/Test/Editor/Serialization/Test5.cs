using System;
using System.IO;
using System.Text;
using MongoDB.Bson.IO;
using System.Diagnostics;
using Test.Editor.NinoGen;

using MongoDB.Bson.Serialization;


// ReSharper disable RedundantJumpStatement

namespace Nino.Test.Editor.Serialization
{
    public class Test5
    {
        private const string SerializationTest5 =
            "Nino/Test/Serialization/Test5 - Serialize and Deserialize (Nino vs MongoDB.Bson)";

        private static string GetString(int len)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append('a', len);
            return sb.ToString();
        }

#if UNITY_2017_1_OR_NEWER
        [UnityEditor.MenuItem(SerializationTest5, priority = 5)]
#endif
        public static void Main()
        {
            //reg
            BsonClassMap.RegisterClassMap<Data>();
            BsonClassMap.RegisterClassMap<NestedData>();
            BsonClassMap.RegisterClassMap<TestEnum>();
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

            Logger.D("Serialization Test", $"<color=cyan>testing {max} objs</color>");
            Logger.D("Serialization Test", "======================================");

            //Nino
            var sw = new Stopwatch();
            BeginSample("Nino - Serialize");
            sw.Restart();
            byte[] ret = Serializer.Serialize(points);
            sw.Stop();
            EndSample();
            Logger.D("Serialization Test", $"Nino: {ret.Length} bytes in {sw.ElapsedMilliseconds}ms");
            var len = ret.Length;
            var tm = sw.ElapsedMilliseconds;
            //Logger.D("Serialization Test",string.Join(",", bs));

            //MongoDB.Bson
            BeginSample("MongoDB.Bson - Serialize");
            byte[] bs2;
            sw.Restart();
            //we want byte[], MongoDB.Bson returns stream
            //to be able to make it fair, we need to convert stream to byte[]
            using (MemoryStream ms = new MemoryStream())
            {
                using (BsonBinaryWriter bsonWriter = new BsonBinaryWriter(ms, BsonBinaryWriterSettings.Defaults))
                {
                    BsonSerializationContext context = BsonSerializationContext.CreateRoot(bsonWriter);
                    BsonSerializationArgs args = default;
                    args.NominalType = typeof(object);
                    IBsonSerializer serializer = BsonSerializer.LookupSerializer(args.NominalType);
                    serializer.Serialize(context, args, points);
                    bs2 = ms.ToArray();
                }
            }

            sw.Stop();
            EndSample();

            Logger.D("Serialization Test", $"MongoDB.Bson: {bs2.Length} bytes in {sw.ElapsedMilliseconds}ms");
            //Logger.D("Serialization Test",string.Join(",", bs));

            Logger.D("Serialization Test", "======================================");
            Logger.D("Serialization Test", $"size diff (nino - MongoDB.Bson): {len - bs2.Length} bytes");
            Logger.D("Serialization Test",
                $"size diff pct => diff/MongoDB.Bson : {((len - bs2.Length) * 100f / bs2.Length):F2}%");

            Logger.D("Serialization Test", "======================================");
            Logger.D("Serialization Test", $"time diff (nino - MongoDB.Bson): {tm - sw.ElapsedMilliseconds} ms");
            Logger.D("Serialization Test",
                $"time diff pct => time/MongoDB.Bson : {((tm - sw.ElapsedMilliseconds) * 100f / sw.ElapsedMilliseconds):F2}%");

            BeginSample("Nino - Deserialize");
            sw.Restart();
            Deserializer.Deserialize(ret, out NestedData d);
            sw.Stop();
            EndSample();
            Logger.D("Deserialization Test", d);
            Logger.D("Deserialization Test",
                $"Nino: extracted {ret.Length} bytes and deserialized {points.ps.Length} entries in {sw.ElapsedMilliseconds}ms");
            tm = sw.ElapsedMilliseconds;

            //MongoDB.Bson
            BeginSample("MongoDB.Bson - Deserialize");
            sw.Restart();
            d = (NestedData)BsonSerializer.Deserialize(bs2, typeof(NestedData));
            sw.Stop();
            EndSample();
            Logger.D("Deserialization Test", d);
            Logger.D("Deserialization Test",
                $"MongoDB.Bson: extracted {bs2.Length} bytes and deserialized {points.ps.Length} entries in {sw.ElapsedMilliseconds}ms");

            Logger.D("Deserialization Test", "======================================");
            Logger.D("Deserialization Test", $"time diff (nino - MongoDB.Bson): {tm - sw.ElapsedMilliseconds} ms");
            Logger.D("Deserialization Test",
                $"time diff pct => time/MongoDB.Bson : {((tm - sw.ElapsedMilliseconds) * 100f / sw.ElapsedMilliseconds):F2}%");

            GC.Collect();
            
            #endregion
        }
    }
}