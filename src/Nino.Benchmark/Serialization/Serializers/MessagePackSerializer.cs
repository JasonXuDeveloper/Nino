// Copyright (c) All contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

#pragma warning disable SA1649 // File name should match first type name

using MessagePack;

namespace Nino.Benchmark.Serializers
{
    public class MessagePack_v2 : SerializerBase
    {
        public override T Deserialize<T>(object input)
        {
            var lz4Options = MessagePackSerializerOptions.Standard.WithCompression(MessagePackCompression.Lz4BlockArray);
            return MessagePack.MessagePackSerializer.Deserialize<T>((byte[])input, lz4Options);
        }

        public override object Serialize<T>(T input)
        {
            var lz4Options = MessagePackSerializerOptions.Standard.WithCompression(MessagePackCompression.Lz4BlockArray);
            return MessagePack.MessagePackSerializer.Serialize(input, lz4Options);
        }

        public override string ToString()
        {
            return "MessagePack_v2";
        }
    }

    public class OptimizedResolver : MessagePack.IFormatterResolver
    {
        public static readonly MessagePack.IFormatterResolver Instance = new OptimizedResolver();

        // configure your custom resolvers.
        private static readonly MessagePack.IFormatterResolver[] Resolvers = new MessagePack.IFormatterResolver[]
        {
            MessagePack.Resolvers.NativeGuidResolver.Instance, MessagePack.Resolvers.NativeDecimalResolver.Instance,
            MessagePack.Resolvers.NativeDateTimeResolver.Instance, MessagePack.Resolvers.StandardResolver.Instance,
        };

        private OptimizedResolver()
        {
        }

        public MessagePack.Formatters.IMessagePackFormatter<T> GetFormatter<T>()
        {
            return Cache<T>.Formatter;
        }

        private static class Cache<T>
        {
            #pragma warning disable SA1401 // Fields should be private
            public static MessagePack.Formatters.IMessagePackFormatter<T> Formatter;
            #pragma warning restore SA1401 // Fields should be private

            static Cache()
            {
                foreach (var resolver in Resolvers)
                {
                    var f = resolver.GetFormatter<T>();
                    if (f != null)
                    {
                        Formatter = f;
                        return;
                    }
                }
            }
        }
    }
}
