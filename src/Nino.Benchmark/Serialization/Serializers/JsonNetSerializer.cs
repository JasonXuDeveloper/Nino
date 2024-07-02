// Copyright (c) All contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using Nino.Benchmark.Models;

namespace Nino.Benchmark.Serializers
{
    public class JsonNetSerializer : SerializerBase
    {
        private static readonly JsonSerializer Serializer = new JsonSerializer();
        
        public override T Deserialize<T>(byte[] input)
        {
            return DeserializeImpl<T>(input);
        }

        public T DeserializeImpl<T>(byte[] input)
        {
            using (var ms = new MemoryStream((byte[])input))
            using (var sr = new StreamReader(ms, Encoding.UTF8))
            using (var jr = new JsonTextReader(sr))
            {
                return Serializer.Deserialize<T>(jr) ?? Activator.CreateInstance<T>();
            }
        }

        public byte[] SerializeImpl<T>(T input)
        {
            using (var ms = new MemoryStream())
            {
                using (var sw = new StreamWriter(ms, Encoding.UTF8))
                using (var jw = new JsonTextWriter(sw))
                {
                    Serializer.Serialize(jw, input);
                }

                ms.Flush();
                return ms.ToArray();
            }
        }

        public override byte[] SerializeAccessToken(AccessToken token)
        {
            return SerializeImpl(token);
        }

        public override AccessToken DeserializeAccessToken(byte[] data)
        {
            return DeserializeImpl<AccessToken>(data);
        }

        public override byte[] SerializeAccountMerge(AccountMerge merge)
        {
            return SerializeImpl(merge);
        }

        public override AccountMerge DeserializeAccountMerge(byte[] data)
        {
            return DeserializeImpl<AccountMerge>(data);
        }

        public override byte[] SerializeAnswer(Answer answer)
        {
            return SerializeImpl(answer);
        }

        public override Answer DeserializeAnswer(byte[] data)
        {
            return DeserializeImpl<Answer>(data);
        }

        public override byte[] SerializeBadge(Badge badge)
        {
            return SerializeImpl(badge);
        }

        public override Badge DeserializeBadge(byte[] data)
        {
            return DeserializeImpl<Badge>(data);
        }

        public override byte[] SerializeComment(Comment comment)
        {
            return SerializeImpl(comment);
        }

        public override Comment DeserializeComment(byte[] data)
        {
            return DeserializeImpl<Comment>(data);
        }

        public override byte[] SerializeData(Data data)
        {
            return SerializeImpl(data);
        }

        public override Data DeserializeData(byte[] data)
        {
            return DeserializeImpl<Data>(data);
        }

        public override byte[] SerializeString(string str)
        {
            return SerializeImpl(str);
        }

        public override string DeserializeString(byte[] data)
        {
            return DeserializeImpl<string>(data);
        }

        public override byte[] SerializeNestedData(NestedData data)
        {
            return SerializeImpl(data);
        }

        public override NestedData DeserializeNestedData(byte[] data)
        {
            return DeserializeImpl<NestedData>(data);
        }

        public override byte[] Serialize<T>(T input)
        {
            return SerializeImpl(input);
        }

        public override string ToString()
        {
            return "JsonNet";
        }
    }
}
