using System;
using System.Text;
using Nino.Serialization;

namespace Benchmark.Serializers
{
	public class NinoSerializer : SerializerBase
    {
        public override T Deserialize<T>(object input)
        {
            return (T)Deserializer.Deserialize(typeof(T),Activator.CreateInstance<T>(),(byte[])input, Encoding.UTF8);
        }

        public override object Serialize<T>(T input)
        {
            return Serializer.Serialize<T>(input);
        }

        public override string ToString()
        {
            return "Nino";
        }
    }
}

