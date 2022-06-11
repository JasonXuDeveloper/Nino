using System;
using Nino.Shared;
using Nino.Serialization;

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
                v3 = UnityEngine.Vector3.one
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

            Logger.D("will deserialize");
            var cc = Deserializer.Deserialize<CustomTypeTest>(bs);
            Logger.D($"deserialized as cc: {cc}");
        }
    }
}
// ReSharper restore RedundantTypeArgumentsOfMethod