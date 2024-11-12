using System.Collections.Generic;
using System.Linq;
using Test.Editor.NinoGen;


// ReSharper disable RedundantTypeArgumentsOfMethod
namespace Nino.Test.Editor.Serialization
{
    public class Test9
    {
        private const string SerializationTest9 = "Nino/Test/Serialization/Test9 - Basic Types";

#if UNITY_2017_1_OR_NEWER
        [UnityEditor.MenuItem(SerializationTest9,priority=9)]
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

            T Deserialize<T>(byte[] bytes) where T : unmanaged
            {
                Deserializer.Deserialize(bytes, out T value);
                return value;
            }
            T[] DeserializeArray<T>(byte[] bytes) where T : unmanaged
            {
                Deserializer.Deserialize(bytes, out T[] value);
                return value;
            }
            List<T> DeserializeList<T>(byte[] bytes) where T : unmanaged
            {
                Deserializer.Deserialize(bytes, out List<T> value);
                return value;
            }
            string DeserializeString(byte[] bytes) 
            {
                Deserializer.Deserialize(bytes, out string value);
                return value;
            }

            Logger.D($"Serialized a as: {(((a)).Serialize().Length)} bytes: {(string.Join(",",((a)).Serialize()))}, deserialized as:{Deserialize<int>(a.Serialize())}");
            Logger.D($"Serialized b as: {(((b)).Serialize().Length)} bytes: {(string.Join(",",((b)).Serialize()))}, deserialized as:{Deserialize<uint>(b.Serialize())}");
            Logger.D($"Serialized c as: {(((c)).Serialize().Length)} bytes: {(string.Join(",",((c)).Serialize()))}, deserialized as:{Deserialize<long>(c.Serialize())}");
            Logger.D($"Serialized d as: {(((d)).Serialize().Length)} bytes: {(string.Join(",",((d)).Serialize()))}, deserialized as:{Deserialize<ulong>(d.Serialize())}");
            Logger.D($"Serialized e as: {(((e)).Serialize().Length)} bytes: {(string.Join(",",((e)).Serialize()))}, deserialized as:{Deserialize<float>(e.Serialize())}");
            Logger.D($"Serialized f as: {(((f)).Serialize().Length)} bytes: {(string.Join(",",((f)).Serialize()))}, deserialized as:{Deserialize<double>(f.Serialize())}");
            Logger.D($"Serialized g as: {(((g)).Serialize().Length)} bytes: {(string.Join(",",((g)).Serialize()))}, deserialized as:{Deserialize<decimal>(g.Serialize())}");
            Logger.D($"Serialized h as: {(((h)).Serialize().Length)} bytes: {(string.Join(",",((h)).Serialize()))}, deserialized as:{DeserializeString(h.Serialize())}");
            Logger.D($"Serialized i as: {(((i)).Serialize().Length)} bytes: {(string.Join(",",((i)).Serialize()))}, deserialized as:{Deserialize<bool>(i.Serialize())}");
            Logger.D($"Serialized j as: {(((j)).Serialize().Length)} bytes: {(string.Join(",",((j)).Serialize()))}, deserialized as:{Deserialize<char>(j.Serialize())}");
            Logger.D($"Serialized k as: {(((k)).Serialize().Length)} bytes: {(string.Join(",",((k)).Serialize()))}, deserialized as:{Deserialize<byte>(k.Serialize())}");
            Logger.D($"Serialized l as: {(((l)).Serialize().Length)} bytes: {(string.Join(",",((l)).Serialize()))}, deserialized as:{Deserialize<sbyte>(l.Serialize())}");
            Logger.D($"Serialized m as: {(((m)).Serialize().Length)} bytes: {(string.Join(",",((m)).Serialize()))}, deserialized as:{Deserialize<ushort>(m.Serialize())}");
            Logger.D($"Serialized n as: {(((n)).Serialize().Length)} bytes: {(string.Join(",",((n)).Serialize()))}, deserialized as:{Deserialize<short>(n.Serialize())}");
            Logger.D($"Serialized o as: {(((o)).Serialize().Length)} bytes: {(string.Join(",",((o)).Serialize()))}, deserialized as:{DeserializeArray<int>(o.Serialize())}");
            Logger.D($"Serialized p as: {(((p)).Serialize().Length)} bytes: {(string.Join(",",((p)).Serialize()))}, deserialized as:{string.Join(",",DeserializeList<int>(p.Serialize()))}");
            Deserializer.Deserialize(q.Serialize(), out Dictionary<string, int> dict);
            Logger.D(
                $"Serialized q as: {(((q)).Serialize().Length)} bytes: {(string.Join(",", ((q)).Serialize()))}, " +
                $"deserialized as:{string.Join(",", dict.ToList().SelectMany(kvp => $"{kvp.Key}-{kvp.Value}"))}");
        }
    }
}
// ReSharper restore RedundantTypeArgumentsOfMethod