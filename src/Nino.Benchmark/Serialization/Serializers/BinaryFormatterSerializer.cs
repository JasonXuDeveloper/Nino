// Copyright (c) All contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.IO;
using System.Runtime.Serialization.Formatters.Binary;
#pragma warning disable 618
#pragma warning disable 8604
// #pragma warning disable 618

namespace Nino.Benchmark.Serializers
{
    public class BinaryFormatterSerializer : SerializerBase
    {
        public override T Deserialize<T>(object input)
        {
            using (var ms = new MemoryStream((byte[])input))
            {
                 BinaryFormatter formatter = new BinaryFormatter();
                 return (T)formatter.Deserialize(ms);
            }
        }

        public override object Serialize<T>(T input)
        {
            using (var ms = new MemoryStream())
            {
                BinaryFormatter formatter = new BinaryFormatter();
                formatter.Serialize(ms, input);
                return ms.ToArray();
            }
        }

        public override string ToString()
        {
            return "BinaryFormatter";
        }
    }
}
