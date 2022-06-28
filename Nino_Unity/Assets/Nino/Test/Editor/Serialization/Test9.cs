using Nino.Shared.Util;
using Nino.Serialization;
using System.Collections.Generic;
using System.Linq;

// ReSharper disable RedundantTypeArgumentsOfMethod
namespace Nino.Test.Editor.Serialization
{
    public class Test9
    {
        private const string SerializationTest9 = "Nino/Test/Serialization/Test9 - Basic Types";

#if UNITY_2017_1_OR_NEWER
        [UnityEditor.MenuItem(SerializationTest9)]
#endif
        public static void Main()
        {
            int a = int.MaxValue;
            uint b = uint.MaxValue;
            long c = long.MaxValue;
            ulong d = ulong.MaxValue;
            float e = float.MaxValue;
            double f = double.MaxValue;
            decimal g = decimal.MaxValue;
            string h = "Hello World";
            bool i = true;
            char j = 'a';
            byte k = byte.MaxValue;
            sbyte l = sbyte.MaxValue;
            ushort m = ushort.MaxValue;
            short n = short.MaxValue;
            int[] o = new int[]{ 1, 2, 3, 4, 5 };
            List<int> p = new List<int>() { 1, 2, 3, 4, 5 };
            Dictionary<string, int> q = new Dictionary<string, int>()
                { { "a", 1 }, { "b", 2 }, { "c", 3 }, { "d", 4 }, { "e", 5 } };

            Logger.D($"Serialized a as: {(Serializer.Serialize(((a))).Length)} bytes: {(string.Join(",",Serializer.Serialize(((a)))))}, deserialized as:{Deserializer.Deserialize<int>(Serializer.Serialize(a))}");
            Logger.D($"Serialized b as: {(Serializer.Serialize(((b))).Length)} bytes: {(string.Join(",",Serializer.Serialize(((b)))))}, deserialized as:{Deserializer.Deserialize<uint>(Serializer.Serialize(b))}");
            Logger.D($"Serialized c as: {(Serializer.Serialize(((c))).Length)} bytes: {(string.Join(",",Serializer.Serialize(((c)))))}, deserialized as:{Deserializer.Deserialize<long>(Serializer.Serialize(c))}");
            Logger.D($"Serialized d as: {(Serializer.Serialize(((d))).Length)} bytes: {(string.Join(",",Serializer.Serialize(((d)))))}, deserialized as:{Deserializer.Deserialize<ulong>(Serializer.Serialize(d))}");
            Logger.D($"Serialized e as: {(Serializer.Serialize(((e))).Length)} bytes: {(string.Join(",",Serializer.Serialize(((e)))))}, deserialized as:{Deserializer.Deserialize<float>(Serializer.Serialize(e))}");
            Logger.D($"Serialized f as: {(Serializer.Serialize(((f))).Length)} bytes: {(string.Join(",",Serializer.Serialize(((f)))))}, deserialized as:{Deserializer.Deserialize<double>(Serializer.Serialize(f))}");
            Logger.D($"Serialized g as: {(Serializer.Serialize(((g))).Length)} bytes: {(string.Join(",",Serializer.Serialize(((g)))))}, deserialized as:{Deserializer.Deserialize<decimal>(Serializer.Serialize(g))}");
            Logger.D($"Serialized h as: {(Serializer.Serialize(((h))).Length)} bytes: {(string.Join(",",Serializer.Serialize(((h)))))}, deserialized as:{Deserializer.Deserialize<string>(Serializer.Serialize(h))}");
            Logger.D($"Serialized i as: {(Serializer.Serialize(((i))).Length)} bytes: {(string.Join(",",Serializer.Serialize(((i)))))}, deserialized as:{Deserializer.Deserialize<bool>(Serializer.Serialize(i))}");
            Logger.D($"Serialized j as: {(Serializer.Serialize(((j))).Length)} bytes: {(string.Join(",",Serializer.Serialize(((j)))))}, deserialized as:{Deserializer.Deserialize<char>(Serializer.Serialize(j))}");
            Logger.D($"Serialized k as: {(Serializer.Serialize(((k))).Length)} bytes: {(string.Join(",",Serializer.Serialize(((k)))))}, deserialized as:{Deserializer.Deserialize<byte>(Serializer.Serialize(k))}");
            Logger.D($"Serialized l as: {(Serializer.Serialize(((l))).Length)} bytes: {(string.Join(",",Serializer.Serialize(((l)))))}, deserialized as:{Deserializer.Deserialize<sbyte>(Serializer.Serialize(l))}");
            Logger.D($"Serialized m as: {(Serializer.Serialize(((m))).Length)} bytes: {(string.Join(",",Serializer.Serialize(((m)))))}, deserialized as:{Deserializer.Deserialize<ushort>(Serializer.Serialize(m))}");
            Logger.D($"Serialized n as: {(Serializer.Serialize(((n))).Length)} bytes: {(string.Join(",",Serializer.Serialize(((n)))))}, deserialized as:{Deserializer.Deserialize<short>(Serializer.Serialize(n))}");
            Logger.D($"Serialized o as: {(Serializer.Serialize(((o))).Length)} bytes: {(string.Join(",",Serializer.Serialize(((o)))))}, deserialized as:{Deserializer.Deserialize<int[]>(Serializer.Serialize(o))}");
            Logger.D($"Serialized p as: {(Serializer.Serialize(((p))).Length)} bytes: {(string.Join(",",Serializer.Serialize(((p)))))}, deserialized as:{string.Join(",",Deserializer.Deserialize<List<int>>(Serializer.Serialize(p)))}");
            Logger.D(
                $"Serialized q as: {(Serializer.Serialize(((q))).Length)} bytes: {(string.Join(",", Serializer.Serialize(((q)))))}, " +
                $"deserialized as:{string.Join(",", Deserializer.Deserialize<Dictionary<string, int>>(Serializer.Serialize(q)).ToList().SelectMany(kvp => $"{kvp.Key}-{kvp.Value}"))}");
        }
    }
}
// ReSharper restore RedundantTypeArgumentsOfMethod