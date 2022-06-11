using System;
using Nino.Serialization;
using System.Collections.Generic;
using Logger = Nino.Shared.Logger;

// ReSharper disable RedundantTypeArgumentsOfMethod
namespace Nino.Test.Editor.Serialization
{
    public class Test5
    {
        private const string SerializationTest5 = "Nino/Test/Serialization/Test5 - Custom Type Importer Exporter";

#if UNITY_2017_1_OR_NEWER
        [UnityEditor.MenuItem(SerializationTest5)]
#endif
        public static void Main()
        {
            //custom type
            CustomTypeTest c = new CustomTypeTest()
            {
                dt = DateTime.Now,
                ni = null,
                v3 = UnityEngine.Vector3.one,
                m = UnityEngine.Matrix4x4.zero,
                qs = new List<UnityEngine.Quaternion>()
                {
                    new UnityEngine.Quaternion(100.99f, 299.31f, 45.99f, 0.5f),
                    new UnityEngine.Quaternion(100.99f, 299.31f, 45.99f, 0.5f),
                    new UnityEngine.Quaternion(100.99f, 299.31f, 45.99f, 0.5f)
                },
                dict = new Dictionary<string, int>()
                {
                    { "test1", 1 },
                    { "test2", 2 },
                    { "test3", 3 },
                    { "test4", 4 },
                }
            };

            //register importer (custom way to write those objects)
            Serializer.AddCustomImporter<DateTime>((datetime, writer) =>
            {
                //write long
                writer.Write(datetime.ToBinary());
            });
            Serializer.AddCustomImporter<int?>((val, writer) =>
            {
                //write int
                writer.Write(val.GetValueOrDefault());
            });
            Serializer.AddCustomImporter<UnityEngine.Vector3>((val, writer) =>
            {
                //write 3 float
                writer.Write(val.x);
                writer.Write(val.y);
                writer.Write(val.z);
            });
            Serializer.AddCustomImporter<UnityEngine.Quaternion>((val, writer) =>
            {
                //write 4 float
                writer.Write(val.x);
                writer.Write(val.y);
                writer.Write(val.z);
                writer.Write(val.w);
            });
            Serializer.AddCustomImporter<UnityEngine.Matrix4x4>((val, writer) =>
            {
                void WriteV4(UnityEngine.Vector4 v)
                {
                    writer.Write(v.x);
                    writer.Write(v.y);
                    writer.Write(v.z);
                    writer.Write(v.w);
                }

                //write 4 rows
                WriteV4(val.GetRow(0));
                WriteV4(val.GetRow(1));
                WriteV4(val.GetRow(2));
                WriteV4(val.GetRow(3));
            });
            Logger.D($"will serialize c: {c}");
            var bs = Serializer.Serialize(c);
            Logger.D($"serialized to {bs.Length}bytes: {string.Join(",", bs)}");

            //register exporter (custom way to export bytes to object)
            //as when writing datetime, we wrote long, here we read long and parse back to datetime
            Deserializer.AddCustomExporter<DateTime>(reader => DateTime.FromBinary(reader.ReadInt64()));
            //as when writing nullable<int>, we wrote int, here we read int
            Deserializer.AddCustomExporter<int?>(reader => reader.ReadInt32());
            //as we wrote 3 floats with vector3, now we read 3 floats and parse to vector
            Deserializer.AddCustomExporter<UnityEngine.Vector3>(reader =>
                new UnityEngine.Vector3(reader.ReadFloat(), reader.ReadFloat(), reader.ReadFloat()));
            //read 4 floats and parse to Quaternion
            Deserializer.AddCustomExporter<UnityEngine.Quaternion>(reader =>
                new UnityEngine.Quaternion(reader.ReadFloat(), reader.ReadFloat(), reader.ReadFloat(),
                    reader.ReadFloat()));
            //read 4 rows and parse to matrix 4x4
            Deserializer.AddCustomExporter<UnityEngine.Matrix4x4>(reader =>
            {
                UnityEngine.Vector4 ReadV4()
                {
                    return new UnityEngine.Vector4(reader.ReadFloat(), reader.ReadFloat(), reader.ReadFloat(),
                        reader.ReadFloat());
                }

                //result
                var ret = new UnityEngine.Matrix4x4();
                //read 4 rows
                ret.SetRow(0, ReadV4());
                ret.SetRow(1, ReadV4());
                ret.SetRow(2, ReadV4());
                ret.SetRow(3, ReadV4());
                return ret;
            });

            Logger.D("will deserialize");
            var cc = Deserializer.Deserialize<CustomTypeTest>(bs);
            Logger.D($"deserialized as cc: {cc}");
        }
    }
}
// ReSharper restore RedundantTypeArgumentsOfMethod