// Copyright (c) All contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

#pragma warning disable SA1649 // File name should match first type name

using MessagePack;
using MessagePack.Formatters;

namespace Nino.Benchmark.Serializers
{
    public class MessagePack_Lz4 : SerializerBase
    {
        public override T Deserialize<T>(object input)
        {
            var lz4Options = MessagePackSerializerOptions.Standard.WithCompression(MessagePackCompression.Lz4Block);
            return MessagePack.MessagePackSerializer.Deserialize<T>((byte[])input, lz4Options);
        }

        public override object Serialize<T>(T input)
        {
            var lz4Options = MessagePackSerializerOptions.Standard.WithCompression(MessagePackCompression.Lz4Block);
            return MessagePack.MessagePackSerializer.Serialize(input, lz4Options);
        }

        public override string ToString()
        {
            return "MessagePack_Lz4";
        }
    }
    public class MessagePack_NoCompression : SerializerBase
    {
        public override T Deserialize<T>(object input)
        {
            return MessagePack.MessagePackSerializer.Deserialize<T>((byte[])input);
        }

        public override object Serialize<T>(T input)
        {
            return MessagePack.MessagePackSerializer.Serialize(input);
        }

        public override string ToString()
        {
            return "MessagePack_NoCompression";
        }
    }
}
