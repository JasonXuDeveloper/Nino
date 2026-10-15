using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Nino.Core
{
    /// <summary>Serializes values to Nino binary data. All methods are thread safe.</summary>
    public static class NinoSerializer
    {
        [ThreadStatic] private static NinoArrayBufferWriter _scratch;

        /// <summary>Largest scratch buffer kept per thread between calls.</summary>
        private const int MaxRetainedScratch = 1024 * 1024;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static NinoArrayBufferWriter RentScratch()
        {
            var buffer = NinoThreadCache.Take(ref _scratch);
            if (buffer == null)
                return new NinoArrayBufferWriter();
            buffer.Index = 0;
            return buffer;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static void ReturnScratch(NinoArrayBufferWriter buffer)
        {
            if (buffer.Capacity > MaxRetainedScratch)
            {
                buffer.Dispose();
                return;
            }

            _scratch = buffer;
        }

        /// <summary>Serializes <paramref name="value"/> into a new byte array.</summary>
        // The public entry points inline into the caller, where T is usually exact: the formatter and the raw-array
        // check are then static field loads instead of generic dictionary lookups (T a reference type makes any
        // method generic over it shared code), and the out-of-line rest receives the formatter as an argument.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static byte[] Serialize<T>(T value)
        {
            if (NinoRaw.Is<T>())
                return SerializeRaw(value);

            if (RawArray<T>.Info is { } raw && value != null)
                return raw.Serialize(Unsafe.As<Array>(value));

            return SerializeToArray(value, NinoFormatterCache<T>.Instance);
        }

        private static byte[] SerializeRaw<T>(T value)
        {
            var bytes = new byte[Unsafe.SizeOf<T>()];
            NinoLayout<T>.Write(ref bytes[0], value);
            return bytes;
        }

        private static byte[] SerializeToArray<T>(T value, NinoFormatter<T> formatter)
        {
            var buffer = RentScratch();
            try
            {
                WriteFormatted(value, buffer, formatter);
                return buffer.ToArray();
            }
            finally
            {
                ReturnScratch(buffer);
            }
        }

        /// <summary>Writes <paramref name="value"/> with its formatter, then ends the call's reference tracking.</summary>
        private static void WriteFormatted<T>(T value, NinoArrayBufferWriter buffer, NinoFormatter<T> formatter)
        {
            var writer = new Writer(buffer);
            formatter.Serialize(value, ref writer);
            writer.Flush();
            writer.ReleaseReferences();
        }

        /// <summary>
        /// Set when <typeparamref name="T"/> is an array of raw elements. Arrays are reference types, so this code
        /// is shared between instantiations and every static access is a generic dictionary lookup: everything
        /// the fast path needs is behind one.
        /// </summary>
        private static class RawArray<T>
        {
            public static readonly RawArrayWriter Info = Create();

            private static RawArrayWriter Create()
            {
                if (!typeof(T).IsArray || typeof(T).GetArrayRank() != 1)
                    return null;
                try
                {
                    var formatter = NinoFormatterCache<T>.Instance.RawArray;
                    return formatter == null ? null : new RawArrayWriter(formatter.ElementSize, formatter);
                }
                catch (Exception)
                {
                    // No formatter: the regular path reports it.
                    return null;
                }
            }
        }

        /// <summary>
        /// Writes an array of raw elements: its length, then one block copy, in code specialized for the element
        /// type (the array formatter's), so only the lookup of this object is shared code.
        /// </summary>
        private sealed class RawArrayWriter
        {
            private readonly int _elementSize;
            private readonly INinoRawArrayFormatter _formatter;

            public RawArrayWriter(int elementSize, INinoRawArrayFormatter formatter)
            {
                _elementSize = elementSize;
                _formatter = formatter;
            }

            /// <summary>Length plus element bytes.</summary>
            public int Size(Array array) => checked(NinoWire.LengthSize + array.Length * _elementSize);

            /// <summary>The result is allocated at its exact size and filled with one copy.</summary>
            public byte[] Serialize(Array array)
            {
                var bytes = NinoUnsafe.AllocateUninitializedArray<byte>(Size(array));
                _formatter.Write(ref NinoUnsafe.GetArrayDataReference(bytes), array);
                return bytes;
            }

            public void Write(ref byte destination, Array array) => _formatter.Write(ref destination, array);
        }

        /// <summary>
        /// Serializes <paramref name="value"/> into <paramref name="bufferWriter"/>. Writing into a
        /// <see cref="NinoArrayBufferWriter"/> is done in place; other buffer writers receive one copy.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Serialize<T>(T value, IBufferWriter<byte> bufferWriter)
        {
            if (NinoRaw.Is<T>())
            {
                WriteRaw(value, bufferWriter);
                return;
            }

            if (RawArray<T>.Info == null && bufferWriter is NinoArrayBufferWriter nino)
            {
                WriteFormatted(value, nino, NinoFormatterCache<T>.Instance);
                return;
            }

            SerializeOther(value, bufferWriter, NinoFormatterCache<T>.Instance);
        }

        private static void SerializeOther<T>(T value, IBufferWriter<byte> bufferWriter, NinoFormatter<T> formatter)
        {
            if (bufferWriter is NinoArrayBufferWriter nino)
            {
                if (value != null && RawArray<T>.Info is { } raw)
                {
                    var array = Unsafe.As<Array>(value);
                    raw.Write(ref nino.Reserve(raw.Size(array)), array);
                    return;
                }

                WriteFormatted(value, nino, formatter);
                return;
            }

            var buffer = RentScratch();
            try
            {
                WriteFormatted(value, buffer, formatter);
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
            if (NinoRaw.Is<T>())
            {
                writer.WriteValue(value);
                return;
            }

            NinoFormatterCache<T>.Instance.Serialize(value, ref writer);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void WriteRaw<T>(T value, IBufferWriter<byte> bufferWriter)
        {
            int size = Unsafe.SizeOf<T>();
            if (bufferWriter is NinoArrayBufferWriter nino)
            {
                NinoLayout<T>.Write(ref nino.Reserve(size), value);
                return;
            }

            var span = bufferWriter.GetSpan(size);
            if (span.Length < size)
                NinoThrow.EndOfData();
            NinoLayout<T>.Write(ref MemoryMarshal.GetReference(span), value);
            bufferWriter.Advance(size);
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
            // Raw values inline into the caller as a length check and a load.
            if (NinoRaw.Is<T>())
                return ReadRaw<T>(data);
            return DeserializeFormatted<T>(data);
        }

        // Inlined into the caller, where T is usually exact: the formatter cache is then a static field load rather
        // than a generic dictionary lookup (T a reference type makes this method shared code).
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static T DeserializeFormatted<T>(ReadOnlySpan<byte> data)
        {
            var reader = new Reader(data);
            T value = default;
            NinoFormatterCache<T>.Instance.Deserialize(ref value, ref reader);
            reader.ReleaseReferences();
            return value;
        }

        /// <summary>
        /// Deserializes into <paramref name="value"/>, reusing the existing instance (and nested instances) where
        /// possible. Instances that are dropped are passed to <see cref="NinoRecycler{T}"/>.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Deserialize<T>(ReadOnlySpan<byte> data, ref T value)
        {
            if (NinoRaw.Is<T>())
            {
                value = ReadRaw<T>(data);
                return;
            }

            var reader = new Reader(data);
            NinoFormatterCache<T>.Instance.Deserialize(ref value, ref reader);
            reader.ReleaseReferences();
        }

        /// <summary>Same as <see cref="Deserialize{T}(ReadOnlySpan{byte}, ref T)"/>.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void DeserializeRef<T>(ref T value, ReadOnlySpan<byte> data) => Deserialize(data, ref value);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static T ReadRaw<T>(ReadOnlySpan<byte> data)
        {
            if (data.Length < Unsafe.SizeOf<T>())
                NinoThrow.EndOfData();
            return NinoUnsafe.Read<T>(ref MemoryMarshal.GetReference(data));
        }

        /// <summary>Same as <see cref="Deserialize{T}(ref T, ref Reader)"/>.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void DeserializeRef<T>(ref T value, ref Reader reader) => Deserialize(ref value, ref reader);

        /// <summary>Deserializes into <paramref name="value"/> with an existing reader.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Deserialize<T>(ref T value, ref Reader reader)
        {
            if (NinoRaw.Is<T>())
            {
                value = reader.UnsafeRead<T>();
                return;
            }

            NinoFormatterCache<T>.Instance.Deserialize(ref value, ref reader);
        }

        /// <summary>Deserializes a new value with an existing reader.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static T Deserialize<T>(ref Reader reader)
        {
            if (NinoRaw.Is<T>())
                return reader.UnsafeRead<T>();

            T value = default;
            NinoFormatterCache<T>.Instance.Deserialize(ref value, ref reader);
            return value;
        }

        /// <summary>Deserializes a value of <paramref name="type"/> and returns it boxed.</summary>
        public static object Deserialize(ReadOnlySpan<byte> data, Type type)
        {
            object value = null;
            Deserialize(data, type, ref value);
            return value;
        }

        /// <summary>Deserializes a value of <paramref name="type"/> into <paramref name="value"/>.</summary>
        public static void Deserialize(ReadOnlySpan<byte> data, Type type, ref object value)
        {
            if (type == null)
                throw new ArgumentNullException(nameof(type));
            var reader = new Reader(data);
            BoxedFormatter.Get(type).Deserialize(ref value, ref reader);
            reader.ReleaseReferences();
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

        // A lambda rather than a method group: older compilers (Unity's) allocate a delegate per method group use.
        public static BoxedFormatter Get(Type type) => Cache.GetOrAdd(type,
            t => (BoxedFormatter)Activator.CreateInstance(typeof(BoxedFormatter<>).MakeGenericType(t)));

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
