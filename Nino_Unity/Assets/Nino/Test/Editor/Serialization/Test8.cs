using Nino.Shared.Util;
using Nino.Serialization;

// ReSharper disable RedundantTypeArgumentsOfMethod
namespace Nino.Test.Editor.Serialization
{
    public class Test8
    {
        private const string SerializationTest8 = "Nino/Test/Serialization/Test8 - Include All Member Class";

#if UNITY_2017_1_OR_NEWER
        [UnityEditor.MenuItem(SerializationTest8)]
#endif
        public static void Main()
        {
            IncludeAllClass c = new IncludeAllClass()
            {
                a = 100,
                b = 199,
                c = 5.5f,
                d = 1.23456
            };
            Logger.D(
                "serialize an 'include all' class will make serialization " +
                "and deserialization result larger and slower, it is recommended to use NinoMember Attributes");
            Logger.D($"will serialize c: {c}");
            var bs = Serializer.Serialize(c);
            Logger.D($"serialized to {bs.Length} bytes: {string.Join(",", bs)}");

            Logger.D("will deserialize");
            var cc = Deserializer.Deserialize<IncludeAllClass>(bs);
            Logger.D($"deserialized as cc: {cc}");
            
            NotIncludeAllClass d = new NotIncludeAllClass()
            {
                a = 100,
                b = 199,
                c = 5.5f,
                d = 1.23456
            };
            Logger.D(
                "Now in comparison, we serialize a class with the same structure and same value");
            Logger.D($"will serialize d: {d}");
            bs = Serializer.Serialize(d);
            Logger.D($"serialized to {bs.Length} bytes: {string.Join(",", bs)}");

            Logger.D("will deserialize");
            var dd = Deserializer.Deserialize<NotIncludeAllClass>(bs);
            Logger.D($"deserialized as dd: {dd}");
        }
    }
}
// ReSharper restore RedundantTypeArgumentsOfMethod