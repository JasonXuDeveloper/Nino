using System;
using Nino.Serialization;

namespace Nino.Test
{
    public class TestClass
    {
        public static void Main(string[] args)
        {
            long key = -100;
            var buffer = Serializer.Serialize(key);
            Console.WriteLine($"serialized int64 as {buffer.Length} bytes: {String.Join(",",buffer)}");
            Console.WriteLine(Deserializer.Deserialize<int>(buffer));
        }
    }
}