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
            if (!NinoUnsafe.IsReferenceOrContainsReferences<T>() && NinoRaw<T>.IsRaw)
            {
                var bytes = new byte[Unsafe.SizeOf<T>()];
                NinoUnsafe.Write(ref bytes[0], value);
                NinoLayout<T>.ClearPadding(ref bytes[0]);
                return bytes;
            }

            if (RawArray<T>.ElementSize != 0 && value != null)
                return SerializeRawArray(value, RawArray<T>.ElementSize);

            var buffer = RentScratch();
            try
            {
                var writer = new Writer(buffer);
                NinoFormatterCache<T>.Instance.Serialize(value, ref writer);
                writer.Flush();
                writer.ReleaseReferences();
                return buffer.ToArray();
            }
            finally
            {
                ReturnScratch(buffer);
            }
        }

        /// <summary>Element size when <typeparamref name="T"/> is an array of raw elements, else 0.</summary>
        private static class RawArray<T>
        {
            public static readonly int ElementSize = Compute();

            public static readonly INinoRawArrayFormatter? Padded = ElementSize == 0 ? null : NinoFormatterCache<T>.Instance.RawArrayPadding;

            private static int Compute()
            {
                if (!typeof(T).IsArray || typeof(T).GetArrayRank() != 1)
                    return 0;
                try
                {
                    return NinoFormatterCache<T>.Instance.RawArrayElementSize;
                }
                catch (Exception)
                {
                    // No formatter: the regular path reports it.
                    return 0;
                }
            }
        }

        /// <summary>An array of raw elements: the result is allocated at its exact size and filled with one copy.</summary>
        private static byte[] SerializeRawArray<T>(T value, int elementSize)
        {
            // Any single-dimension array has the same layout as byte[] up to the elements.
            var array = Unsafe.As<byte[]>(value);
            int length = array.Length;
            int size = checked(length * elementSize);
            var bytes = NinoUnsafe.AllocateUninitializedArray<byte>(4 + size);
            ref byte dst = ref NinoUnsafe.GetArrayDataReference(bytes);
            NinoUnsafe.Write(ref dst, length);
            if (size != 0)
            {
                ref byte src = ref NinoUnsafe.GetArrayDataReference(array);
                if (RawArray<T>.Padded is { } padded)
                    padded.CopyToWire(ref Unsafe.Add(ref dst, 4), ref src, length);
                else
                    Unsafe.CopyBlockUnaligned(ref Unsafe.Add(ref dst, 4), ref src, (uint)size);
            }

            return bytes;
        }

        /// <summary>
        /// Serializes <paramref name="value"/> into <paramref name="bufferWriter"/>. Writing into a
        /// <see cref="NinoArrayBufferWriter"/> is done in place; other buffer writers receive one copy.
        /// </summary>
        public static void Serialize<T>(T value, IBufferWriter<byte> bufferWriter)
        {
            if (!NinoUnsafe.IsReferenceOrContainsReferences<T>() && NinoRaw<T>.IsRaw)
            {
                WriteRaw(value, bufferWriter);
                return;
            }

            if (bufferWriter is NinoArrayBufferWriter nino)
            {
                var writer = new Writer(nino);
                NinoFormatterCache<T>.Instance.Serialize(value, ref writer);
                writer.Flush();
                writer.ReleaseReferences();
                return;
            }

            var buffer = RentScratch();
            try
            {
                var writer = new Writer(buffer);
                NinoFormatterCache<T>.Instance.Serialize(value, ref writer);
                writer.Flush();
                writer.ReleaseReferences();
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
            if (!NinoUnsafe.IsReferenceOrContainsReferences<T>() && NinoRaw<T>.IsRaw)
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
                int index = nino.Index;
                if (size > nino.Buffer.Length - index)
                {
                    nino.Grow(size);
                }

                ref byte dst = ref Unsafe.Add(ref NinoUnsafe.GetArrayDataReference(nino.Buffer), index);
                NinoUnsafe.Write(ref dst, value);
                NinoLayout<T>.ClearPadding(ref dst);
                nino.Index = index + size;
                return;
            }

            var span = bufferWriter.GetSpan(size);
            if (span.Length < size)
                NinoThrow.EndOfData();
            ref byte first = ref System.Runtime.InteropServices.MemoryMarshal.GetReference(span);
            NinoUnsafe.Write(ref first, value);
            NinoLayout<T>.ClearPadding(ref first);
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
            if (!NinoUnsafe.IsReferenceOrContainsReferences<T>() && NinoRaw<T>.IsRaw)
                return ReadRaw<T>(data);

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
            if (!NinoUnsafe.IsReferenceOrContainsReferences<T>() && NinoRaw<T>.IsRaw)
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
            return NinoUnsafe.Read<T>(ref System.Runtime.InteropServices.MemoryMarshal.GetReference(data));
        }

        /// <summary>Deserializes into <paramref name="value"/> with an existing reader.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void DeserializeRef<T>(ref T value, ref Reader reader)
        {
            if (!NinoUnsafe.IsReferenceOrContainsReferences<T>() && NinoRaw<T>.IsRaw)
            {
                value = reader.UnsafeRead<T>();
                return;
            }

            NinoFormatterCache<T>.Instance.Deserialize(ref value, ref reader);
        }

        /// <summary>Deserializes into <paramref name="value"/> with an existing reader.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Deserialize<T>(ref T value, ref Reader reader)
        {
            if (!NinoUnsafe.IsReferenceOrContainsReferences<T>() && NinoRaw<T>.IsRaw)
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
            if (!NinoUnsafe.IsReferenceOrContainsReferences<T>() && NinoRaw<T>.IsRaw)
                return reader.UnsafeRead<T>();

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
            reader.ReleaseReferences();
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
