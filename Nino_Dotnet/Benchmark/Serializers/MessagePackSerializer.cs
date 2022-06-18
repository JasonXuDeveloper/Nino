// Copyright (c) All contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

#pragma warning disable SA1649 // File name should match first type name

using MessagePack;

namespace Benchmark.Serializers
{
    public class MessagePack_v1 : SerializerBase
    {
        public override T Deserialize<T>(object input)
        {
            return MessagePack.MessagePackSerializer.Deserialize<T>((byte[])input);
        }

        public override object Serialize<T>(T input)
        {
            return MessagePack.MessagePackSerializer.Serialize<T>(input);
        }

        public override string ToString()
        {
            return "MessagePack_v1";
        }
    }

    public class MessagePack_v2 : SerializerBase
    {
        public override T Deserialize<T>(object input)
        {
            return MessagePack.MessagePackSerializer.Deserialize<T>((byte[])input);
        }

        public override object Serialize<T>(T input)
        {
            return MessagePack.MessagePackSerializer.Serialize<T>(input);
        }

        public override string ToString()
        {
            return "MessagePack_v2";
        }
    }

    public class MsgPack_v2_string : SerializerBase
    {
        private static readonly MessagePack.MessagePackSerializerOptions Options =
            MessagePack.MessagePackSerializerOptions.Standard.WithResolver(MessagePack.Resolvers.ContractlessStandardResolver.Instance);

        public override T Deserialize<T>(object input)
        {
            return MessagePack.MessagePackSerializer.Deserialize<T>((byte[])input, options: Options);
        }

        public override object Serialize<T>(T input)
        {
            return MessagePack.MessagePackSerializer.Serialize<T>(input, options: Options);
        }

        public override string ToString()
        {
            return "MsgPack_v2_string";
        }
    }

    public class MessagePackLz4_v1 : SerializerBase
    {
        public override T Deserialize<T>(object input)
        {
            var lz4Options = MessagePackSerializerOptions.Standard.WithCompression(MessagePackCompression.Lz4BlockArray);
            return MessagePackSerializer.Deserialize<T>((byte[])input, lz4Options);
        }

        public override object Serialize<T>(T input)
        {
            var lz4Options = MessagePackSerializerOptions.Standard.WithCompression(MessagePackCompression.Lz4BlockArray);
            return MessagePackSerializer.Serialize<T>(input, lz4Options);
        }

        public override string ToString()
        {
            return "MessagePackLz4_v1";
        }
    }

    public class MessagePackLz4_v2 : SerializerBase
    {
        private static readonly MessagePack.MessagePackSerializerOptions LZ4BlockArray =
            MessagePack.MessagePackSerializerOptions.Standard.WithCompression(MessagePack.MessagePackCompression.Lz4BlockArray);

        public override T Deserialize<T>(object input)
        {
            return MessagePack.MessagePackSerializer.Deserialize<T>((byte[])input, LZ4BlockArray);
        }

        public override object Serialize<T>(T input)
        {
            return MessagePack.MessagePackSerializer.Serialize<T>(input, LZ4BlockArray);
        }

        public override string ToString()
        {
            return "MessagePackLz4_v2";
        }
    }


    public class MsgPack_v2_str_lz4 : SerializerBase
    {
        private static readonly MessagePack.MessagePackSerializerOptions Options = MessagePack.MessagePackSerializerOptions.Standard
            .WithResolver(MessagePack.Resolvers.ContractlessStandardResolver.Instance)
            .WithCompression(MessagePack.MessagePackCompression.Lz4BlockArray);

        public override T Deserialize<T>(object input)
        {
            return MessagePack.MessagePackSerializer.Deserialize<T>((byte[])input, Options);
        }

        public override object Serialize<T>(T input)
        {
            return MessagePack.MessagePackSerializer.Serialize<T>(input, Options);
        }

        public override string ToString()
        {
            return "MsgPack_v2_str_lz4";
        }
    }

    public class MsgPack_v2_opt : SerializerBase
    {
        private static readonly MessagePack.MessagePackSerializerOptions Options =
            MessagePack.MessagePackSerializerOptions.Standard.WithResolver(OptimizedResolver.Instance);

        public override T Deserialize<T>(object input)
        {
            return MessagePack.MessagePackSerializer.Deserialize<T>((byte[])input, Options);
        }

        public override object Serialize<T>(T input)
        {
            return MessagePack.MessagePackSerializer.Serialize<T>(input, Options);
        }

        public override string ToString()
        {
            return "MsgPack_v2_opt";
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
