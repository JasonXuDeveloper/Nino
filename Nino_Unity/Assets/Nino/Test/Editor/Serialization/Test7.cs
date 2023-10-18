using Nino.Serialization;
using System.Collections.Generic;
using UnityEngine;
using Logger = Nino.Shared.Util.Logger;

// ReSharper disable RedundantTypeArgumentsOfMethod
namespace Nino.Test.Editor.Serialization
{
    public class CustomTypeTestWrapper : NinoWrapperBase<CustomTypeTest>
    {
        public override void Serialize(CustomTypeTest val, ref Writer writer)
        {
            writer.Write(ref val.v3, sizeof(float) * 3);
            writer.Write(ref val.m, sizeof(float) * 16);
            writer.Write(val.ni);
            writer.Write(val.qs);
            writer.Write(val.dict);
            writer.Write(val.dict2);
        }

        public override CustomTypeTest Deserialize(Reader reader)
        {
            var ret = new CustomTypeTest();
            reader.Read(ref ret.v3, sizeof(float) * 3);
            reader.Read(ref ret.m, sizeof(float) * 16);
            ret.ni = reader.ReadNullable<int>();
            ret.qs = reader.ReadList<Quaternion>();
            ret.dict = reader.ReadDictionary<string, int>();
            ret.dict2 = reader.ReadDictionary<string, Data>();
            return ret;
        }

        public override int GetSize(CustomTypeTest val)
        {
            int ret = 1;
            ret += sizeof(float) * 3;
            ret += sizeof(float) * 16;
            ret += Serializer.GetSize(val.ni);
            ret += Serializer.GetSize(val.qs);
            ret += Serializer.GetSize(val.dict);
            ret += Serializer.GetSize(val.dict2);
            return ret;
        }
    }

    public class Test7
    {
        private const string SerializationTest7 = "Nino/Test/Serialization/Test7 - Custom Type Importer Exporter";

#if UNITY_2017_1_OR_NEWER
        [UnityEditor.MenuItem(SerializationTest7, priority = 7)]
#endif
        public static void Main()
        {
            //register wrappers
            WrapperManifest.AddWrapper(typeof(CustomTypeTest), new CustomTypeTestWrapper());
            //custom type
            CustomTypeTest c = new CustomTypeTest()
            {
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
                },
                dict2 = new Dictionary<string, Data>()
                {
                    { "dict2.entry1", new Data() },
                    { "dict2.entry2", new Data() },
                    { "dict2.entry3", new Data() },
                }
            };

            Logger.D($"will serialize c: {c}");
            var bs = Serializer.Serialize(c);
            Logger.D($"serialized to {bs.Length} bytes: {string.Join(",", bs)}");
            Logger.D("will deserialize");
            var cc = Deserializer.Deserialize<CustomTypeTest>(bs);
            Logger.D($"deserialized as cc: {cc}");
        }
    }
}
// ReSharper restore RedundantTypeArgumentsOfMethod