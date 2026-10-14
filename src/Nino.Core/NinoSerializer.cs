using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace Nino.Core
{
    /// <summary>Serializes values to Nino binary data. All methods are thread safe.</summary>
    public static class NinoSerializer
    {
        [ThreadStatic] private static NinoArrayBufferWriter _scratch;

        /// <summary>Largest scratch buffer kept per thread between calls.</summary>
        private const int MaxRetainedScratch = 1024 * 1024;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static NinoArrayBufferWriter RentScratch()
        {
            // Taking the buffer out of the slot makes nested calls (e.g. from a custom formatter) safe: they see an
            // empty slot and use their own buffer.
            var buffer = _scratch;
            if (buffer == null)
                return new NinoArrayBufferWriter();
            _scratch = null;
            buffer.Index = 0;
            return buffer;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void ReturnScratch(NinoArrayBufferWriter buffer)
        {
            if (buffer.Capacity > MaxRetainedScratch)
            {
                buffer.Dispose();
                return;
            }

            _scratch = buffer;
        }

        /// <summary>Serializes <paramref name="value"/> into a new byte array.</summary>
        public static byte[] Serialize<T>(T value)
        {
            var buffer = RentScratch();
            try
            {
                var writer = new Writer(buffer);
                NinoFormatterCache<T>.Instance.Serialize(value, ref writer);
                writer.Flush();
                return buffer.ToArray();
            }
            finally
            {
                ReturnScratch(buffer);
            }
        }

        /// <summary>
        /// Serializes <paramref name="value"/> into <paramref name="bufferWriter"/>. Writing into a
        /// <see cref="NinoArrayBufferWriter"/> is done in place; other buffer writers receive one copy.
        /// </summary>
        public static void Serialize<T>(T value, IBufferWriter<byte> bufferWriter)
        {
            if (bufferWriter is NinoArrayBufferWriter nino)
            {
                var writer = new Writer(nino);
                NinoFormatterCache<T>.Instance.Serialize(value, ref writer);
                writer.Flush();
                return;
            }

            var buffer = RentScratch();
            try
            {
                var writer = new Writer(buffer);
                NinoFormatterCache<T>.Instance.Serialize(value, ref writer);
                writer.Flush();
                bufferWriter.Write(buffer.WrittenSpan);
            }
            finally
            {
                ReturnScratch(buffer);
            }
        }

        /// <summary>Serializes <paramref name="value"/> with an existing writer.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Serialize<T>(T value, ref Writer writer)
        {
            NinoFormatterCache<T>.Instance.Serialize(value, ref writer);
        }

        /// <summary>
        /// Serializes a boxed value as its runtime type. Read it back with
        /// <see cref="NinoDeserializer.Deserialize(ReadOnlySpan{byte}, Type)"/> and the same type.
        /// </summary>
        public static byte[] Serialize(object value)
        {
            if (value == null)
                throw new ArgumentNullException(nameof(value), "Use Serialize<T> to serialize null values.");
            return BoxedFormatter.Get(value.GetType()).Serialize(value);
        }

        /// <summary>Serializes a boxed value as <paramref name="type"/>.</summary>
        public static void SerializeBoxed(object value, ref Writer writer, Type type)
        {
            if (type == null)
                throw new ArgumentNullException(nameof(type));
            BoxedFormatter.Get(type).Serialize(value, ref writer);
        }

        /// <summary>Serializes a boxed value as <paramref name="type"/> into <paramref name="bufferWriter"/>.</summary>
        public static void SerializeBoxed(object value, IBufferWriter<byte> bufferWriter, Type type)
        {
            if (type == null)
                throw new ArgumentNullException(nameof(type));
            BoxedFormatter.Get(type).Serialize(value, bufferWriter);
        }
    }

    /// <summary>Deserializes Nino binary data. All methods are thread safe.</summary>
    public static class NinoDeserializer
    {
        /// <summary>Deserializes a new value.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static T Deserialize<T>(ReadOnlySpan<byte> data)
        {
            var reader = new Reader(data);
            T value = default;
            NinoFormatterCache<T>.Instance.Deserialize(ref value, ref reader);
            return value;
        }

        /// <summary>
        /// Deserializes into <paramref name="value"/>, reusing the existing instance (and nested instances) where
        /// possible. Instances that are dropped are passed to <see cref="NinoRecycler{T}"/>.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Deserialize<T>(ReadOnlySpan<byte> data, ref T value)
        {
            var reader = new Reader(data);
            NinoFormatterCache<T>.Instance.Deserialize(ref value, ref reader);
        }

        /// <summary>Same as <see cref="Deserialize{T}(ReadOnlySpan{byte}, ref T)"/>.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void DeserializeRef<T>(ref T value, ReadOnlySpan<byte> data)
        {
            var reader = new Reader(data);
            NinoFormatterCache<T>.Instance.Deserialize(ref value, ref reader);
        }

        /// <summary>Deserializes into <paramref name="value"/> with an existing reader.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Deserialize<T>(ref T value, ref Reader reader)
        {
            NinoFormatterCache<T>.Instance.Deserialize(ref value, ref reader);
        }

        /// <summary>Deserializes a new value with an existing reader.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static T Deserialize<T>(ref Reader reader)
        {
            T value = default;
            NinoFormatterCache<T>.Instance.Deserialize(ref value, ref reader);
            return value;
        }

        /// <summary>Deserializes a value of <paramref name="type"/> and returns it boxed.</summary>
        public static object Deserialize(ReadOnlySpan<byte> data, Type type)
        {
            if (type == null)
                throw new ArgumentNullException(nameof(type));
            var reader = new Reader(data);
            object value = null;
            BoxedFormatter.Get(type).Deserialize(ref value, ref reader);
            return value;
        }

        /// <summary>Deserializes a value of <paramref name="type"/> into <paramref name="value"/>.</summary>
        public static void Deserialize(ReadOnlySpan<byte> data, Type type, ref object value)
        {
            if (type == null)
                throw new ArgumentNullException(nameof(type));
            var reader = new Reader(data);
            BoxedFormatter.Get(type).Deserialize(ref value, ref reader);
        }

        /// <summary>Deserializes a value of <paramref name="type"/> with an existing reader.</summary>
        public static void DeserializeBoxed(ref object value, ref Reader reader, Type type)
        {
            if (type == null)
                throw new ArgumentNullException(nameof(type));
            BoxedFormatter.Get(type).Deserialize(ref value, ref reader);
        }
    }

    /// <summary>Non-generic entry point to <see cref="NinoFormatterCache{T}"/> for a runtime type.</summary>
    internal abstract class BoxedFormatter
    {
        private static readonly ConcurrentDictionary<Type, BoxedFormatter> Cache =
            new ConcurrentDictionary<Type, BoxedFormatter>();

        public static BoxedFormatter Get(Type type)
        {
            return Cache.TryGetValue(type, out var formatter)
                ? formatter
                : Cache.GetOrAdd(type,
                    t => (BoxedFormatter)Activator.CreateInstance(typeof(BoxedFormatter<>).MakeGenericType(t)));
        }

        public abstract byte[] Serialize(object value);
        public abstract void Serialize(object value, IBufferWriter<byte> bufferWriter);
        public abstract void Serialize(object value, ref Writer writer);
        public abstract void Deserialize(ref object value, ref Reader reader);
    }

    internal sealed class BoxedFormatter<T> : BoxedFormatter
    {
        public override byte[] Serialize(object value) => NinoSerializer.Serialize((T)value);

        public override void Serialize(object value, IBufferWriter<byte> bufferWriter) =>
            NinoSerializer.Serialize((T)value, bufferWriter);

        public override void Serialize(object value, ref Writer writer) =>
            NinoFormatterCache<T>.Instance.Serialize((T)value, ref writer);

        public override void Deserialize(ref object value, ref Reader reader)
        {
            T typed = value is T existing ? existing : default;
            NinoFormatterCache<T>.Instance.Deserialize(ref typed, ref reader);
            value = typed;
        }
    }
}
