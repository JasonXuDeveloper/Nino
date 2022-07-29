using System.Text;
using Nino.Serialization;

namespace Nino.Benchmark.Serializers
{
	public class NinoSerializer_ZLib : SerializerBase
    {
        public override T Deserialize<T>(object input)
        {
            return Deserializer.Deserialize<T>((byte[])input, Encoding.UTF8);
        }

        public override object Serialize<T>(T input)
        {
            return Serializer.Serialize(input, Encoding.UTF8);
        }

        public override string ToString()
        {
            return "Nino_Zlib";
        }
    }
	public class NinoSerializer_NoCompression : SerializerBase
    {
        public override T Deserialize<T>(object input)
        {
            return Deserializer.Deserialize<T>((byte[])input, Encoding.UTF8, CompressOption.NoCompression);
        }

        public override object Serialize<T>(T input)
        {
            return Serializer.Serialize(input, Encoding.UTF8, CompressOption.NoCompression);
        }

        public override string ToString()
        {
            return "Nino_NoComp";
        }
    }
}

