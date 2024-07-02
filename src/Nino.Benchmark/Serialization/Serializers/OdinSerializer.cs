// Copyright (c) All contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using Nino.Benchmark.Models;
using Nino.Benchmark.Serializers;
using OdinSerializer;
using OdinSerializer.Utilities;

#pragma warning disable SA1649 // File name should match first type name

public class OdinSerializer_ : SerializerBase
{
    public override T Deserialize<T>(byte[] input)
    {
        return DeserializeImpl<T>(input);
    }

    public T DeserializeImpl<T>(byte[] input)
    {
        using (var ctx = Cache<DeserializationContext>.Claim())
        {
            ctx.Value.Config.SerializationPolicy = SerializationPolicies.Everything;
            return SerializationUtility.DeserializeValue<T>((byte[])input, DataFormat.Binary, ctx.Value);
        }
    }

    public byte[] SerializeImpl<T>(T input)
    {
        using (var ctx = Cache<SerializationContext>.Claim())
        {
            ctx.Value.Config.SerializationPolicy = SerializationPolicies.Everything;
            return SerializationUtility.SerializeValue(input, DataFormat.Binary, ctx.Value);
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
        return "Odin";
    }
}