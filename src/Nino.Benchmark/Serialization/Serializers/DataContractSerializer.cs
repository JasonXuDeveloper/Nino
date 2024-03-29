﻿// Copyright (c) All contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.IO;

namespace Nino.Benchmark.Serializers
{
    public class DataContractSerializer : SerializerBase
    {
        public override T Deserialize<T>(object input)
        {
            using (var ms = new MemoryStream((byte[])input))
            {
                var obj = new System.Runtime.Serialization.DataContractSerializer(typeof(T)).ReadObject(ms);
                if (obj is null)
                {
                    return Activator.CreateInstance<T>();
                }

                return (T)obj;
            }
        }

        public override object Serialize<T>(T input)
        {
            using (var ms = new MemoryStream())
            {
                new System.Runtime.Serialization.DataContractSerializer(typeof(T)).WriteObject(ms, input);
                ms.Flush();
                return ms.ToArray();
            }
        }

        public override string ToString()
        {
            return "DataContract";
        }
    }
}
