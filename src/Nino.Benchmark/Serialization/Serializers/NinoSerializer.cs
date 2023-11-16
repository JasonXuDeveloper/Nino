using System;
using Nino.Serialization;

namespace Nino.Benchmark.Serializers
{
	public class NinoSerializer : SerializerBase
    {
        public override T Deserialize<T>(object input)
        {
            return Deserializer.Deserialize<T>((byte[])input);
        }

        public override object Serialize<T>(T input)
        {
            var size = Serializer.GetSize(input);
            byte[] result = GC.AllocateUninitializedArray<byte>(size);
            Serializer.Serialize(result, input);
            return result;
        }

        public override string ToString()
        {
            return "Nino";
        }
    }
}

