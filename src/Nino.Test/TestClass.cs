using System;
using Nino.Serialization;

namespace Nino.Test
{
    public class TestClass
    {
        public static void Main(string[] args)
        {
            long key = -234567;
            Console.WriteLine(Deserializer.Deserialize<int>(Serializer.Serialize(key)));
        }
    }
}