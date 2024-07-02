// Copyright (c) All contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.IO;
using System.Runtime.Serialization.Formatters.Binary;
using Nino.Benchmark.Models;

#pragma warning disable 618
#pragma warning disable 8604
// #pragma warning disable 618

namespace Nino.Benchmark.Serializers
{
    public class BinaryFormatterSerializer : SerializerBase
    {
        public T Deserialize<T>(object input)
        {
            using (var ms = new MemoryStream((byte[])input))
            {
                BinaryFormatter formatter = new BinaryFormatter();
                return (T)formatter.Deserialize(ms);
            }
        }

        public byte[] SerializeImpl<T>(T input)
        {
            using (var ms = new MemoryStream())
            {
                BinaryFormatter formatter = new BinaryFormatter();
                formatter.Serialize(ms, input);
                return ms.ToArray();
            }
        }

        public override byte[] SerializeAccessToken(AccessToken token)
        {
            return SerializeImpl(token);
        }

        public override AccessToken DeserializeAccessToken(byte[] data)
        {
            return Deserialize<AccessToken>(data);
        }

        public override byte[] SerializeAccountMerge(AccountMerge merge)
        {
            return SerializeImpl(merge);
        }

        public override AccountMerge DeserializeAccountMerge(byte[] data)
        {
            return Deserialize<AccountMerge>(data);
        }

        public override byte[] SerializeAnswer(Answer answer)
        {
            return SerializeImpl(answer);
        }

        public override Answer DeserializeAnswer(byte[] data)
        {
            return Deserialize<Answer>(data);
        }

        public override byte[] SerializeBadge(Badge badge)
        {
            return SerializeImpl(badge);
        }

        public override Badge DeserializeBadge(byte[] data)
        {
            return Deserialize<Badge>(data);
        }

        public override byte[] SerializeComment(Comment comment)
        {
            return SerializeImpl(comment);
        }

        public override Comment DeserializeComment(byte[] data)
        {
            return Deserialize<Comment>(data);
        }

        public override byte[] SerializeData(Data data)
        {
            return SerializeImpl(data);
        }

        public override Data DeserializeData(byte[] data)
        {
            return Deserialize<Data>(data);
        }

        public override byte[] SerializeString(string str)
        {
            return SerializeImpl(str);
        }

        public override string DeserializeString(byte[] data)
        {
            return Deserialize<string>(data);
        }

        public override byte[] SerializeNestedData(NestedData data)
        {
            return SerializeImpl(data);
        }

        public override NestedData DeserializeNestedData(byte[] data)
        {
            return Deserialize<NestedData>(data);
        }

        public override byte[] Serialize<T>(T input)
        {
            return SerializeImpl(input);
        }

        public override T Deserialize<T>(byte[] data)
        {
            using (var ms = new MemoryStream(data))
            {
                BinaryFormatter formatter = new BinaryFormatter();
                return (T)formatter.Deserialize(ms);
            }
        }

        public override string ToString()
        {
            return "BinaryFormatter";
        }
    }
}