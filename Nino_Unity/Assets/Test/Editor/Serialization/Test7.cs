using System.Collections.Generic;
using Test.Editor.NinoGen;
using UnityEngine;

// ReSharper disable RedundantTypeArgumentsOfMethod
namespace Nino.Test.Editor.Serialization
{
    public class Test7
    {
        private const string SerializationTest7 = "Nino/Test/Serialization/Test7 - Primitive Type Serialization";

#if UNITY_2017_1_OR_NEWER
        [UnityEditor.MenuItem(SerializationTest7, priority = 7)]
#endif
        public static void Main()
        {
            //custom type
            PrimitiveTypeTest c = new PrimitiveTypeTest()
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
            Deserializer.Deserialize(bs, out PrimitiveTypeTest cc);
            Logger.D($"deserialized as cc: {cc}");
        }
    }
}
// ReSharper restore RedundantTypeArgumentsOfMethod