// Copyright (c) All contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using Nino.Benchmark.Models;

namespace Nino.Benchmark.Serializers
{
    public abstract class SerializerBase
    {
        public abstract byte[] SerializeAccessToken(AccessToken token);
        public abstract AccessToken DeserializeAccessToken(byte[] data);

        public abstract byte[] SerializeAccountMerge(AccountMerge merge);
        public abstract AccountMerge DeserializeAccountMerge(byte[] data);

        public abstract byte[] SerializeAnswer(Answer answer);
        public abstract Answer DeserializeAnswer(byte[] data);

        public abstract byte[] SerializeBadge(Badge badge);
        public abstract Badge DeserializeBadge(byte[] data);

        public abstract byte[] SerializeComment(Comment comment);
        public abstract Comment DeserializeComment(byte[] data);

        public abstract byte[] SerializeData(Data data);
        public abstract Data DeserializeData(byte[] data);

        public abstract byte[] SerializeString(string str);
        public abstract string DeserializeString(byte[] data);

        public abstract byte[] SerializeNestedData(NestedData data);
        public abstract NestedData DeserializeNestedData(byte[] data);

        public abstract byte[] Serialize<T>(T input) where T : unmanaged;
        public abstract T Deserialize<T>(byte[] data) where T : unmanaged;
    }
}