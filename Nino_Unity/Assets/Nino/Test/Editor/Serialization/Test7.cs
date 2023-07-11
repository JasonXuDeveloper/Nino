using Nino.Serialization;
using System.Collections.Generic;
using UnityEngine;
using Logger = Nino.Shared.Util.Logger;

// ReSharper disable RedundantTypeArgumentsOfMethod
namespace Nino.Test.Editor.Serialization
{
    public class Vector3Wrapper : NinoWrapperBase<Vector3>
    {
        public override void Serialize(Vector3 val, ref Writer writer)
        {
            writer.Write(val.x);
            writer.Write(val.y);
            writer.Write(val.z);
        }

        public override Vector3 Deserialize(Reader reader)
        {
            return new Vector3(reader.Read<float>(4), reader.Read<float>(4), reader.Read<float>(4));
        }

        public override int GetSize(Vector3 val)
        {
            return 12;
        }
    }

    public class QuaternionWrapper : NinoWrapperBase<Quaternion>
    {
        public override void Serialize(Quaternion val, ref Writer writer)
        {
            writer.Write(val.x);
            writer.Write(val.y);
            writer.Write(val.z);
            writer.Write(val.w);
        }

        public override Quaternion Deserialize(Reader reader)
        {
            return new Quaternion(reader.Read<float>(4), reader.Read<float>(4), reader.Read<float>(4),
                reader.Read<float>(4));
        }

        public override int GetSize(Quaternion val)
        {
            return 16;
        }
    }

    public class Matrix4x4Wrapper : NinoWrapperBase<Matrix4x4>
    {
        public override void Serialize(Matrix4x4 val, ref Writer writer)
        {
            writer.Write(val.m00);
            writer.Write(val.m01);
            writer.Write(val.m02);
            writer.Write(val.m03);
            writer.Write(val.m10);
            writer.Write(val.m11);
            writer.Write(val.m12);
            writer.Write(val.m13);
            writer.Write(val.m20);
            writer.Write(val.m21);
            writer.Write(val.m22);
            writer.Write(val.m23);
            writer.Write(val.m30);
            writer.Write(val.m31);
            writer.Write(val.m32);
            writer.Write(val.m33);
        }

        public override Matrix4x4 Deserialize(Reader reader)
        {
            return new Matrix4x4(
                new Vector4(reader.Read<float>(4), reader.Read<float>(4), reader.Read<float>(4), reader.Read<float>(4)),
                new Vector4(reader.Read<float>(4), reader.Read<float>(4), reader.Read<float>(4), reader.Read<float>(4)),
                new Vector4(reader.Read<float>(4), reader.Read<float>(4), reader.Read<float>(4), reader.Read<float>(4)),
                new Vector4(reader.Read<float>(4), reader.Read<float>(4), reader.Read<float>(4),
                    reader.Read<float>(4)));
        }

        public override int GetSize(Matrix4x4 val)
        {
            return 64;
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
            WrapperManifest.AddWrapper(typeof(Vector3), new Vector3Wrapper());
            WrapperManifest.AddWrapper(typeof(Quaternion), new QuaternionWrapper());
            WrapperManifest.AddWrapper(typeof(Matrix4x4), new Matrix4x4Wrapper());
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