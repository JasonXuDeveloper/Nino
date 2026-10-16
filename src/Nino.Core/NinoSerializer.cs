using System;
using System.Buffers;
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

            // The formatter is a static readonly field: Tier1 code calls (and inlines) it as its exact type.
            if (RawArray<T>.Formatter is { } raw && value != null)
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
        /// The formatter of <typeparamref name="T"/> when it is an array of raw elements (null otherwise). A static
        /// readonly field: Tier1 code knows its exact type and calls it directly (inlined, with the element size a
        /// constant), without a type guard or interface dispatch. Arrays are reference types, so code that is not
        /// inlined into a caller of the exact type is shared, and this field is a generic dictionary lookup there.
        /// </summary>
        internal static class RawArray<T>
        {
            public static readonly INinoRawArrayFormatter Formatter = Create();

            private static INinoRawArrayFormatter Create()
            {
                if (!typeof(T).IsArray || typeof(T).GetArrayRank() != 1)
                    return null;
                try
                {
                    return NinoFormatterCache<T>.Instance.RawArray;
                }
                catch (Exception)
                {
                    // No formatter: the regular path reports it.
                    return null;
                }
            }
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

            if (RawArray<T>.Formatter is { } raw)
            {
                // An array of raw elements: one capacity check and one block copy, inlined with the element size
                // a constant (the formatter is a static readonly field: Tier1 code knows its exact type).
                if (value != null && bufferWriter is NinoArrayBufferWriter target)
                {
                    raw.Write(target, Unsafe.As<Array>(value));
                    return;
                }
            }
            else if (bufferWriter is NinoArrayBufferWriter nino)
            {
                WriteFormatted(value, nino, NinoFormatterCache<T>.Instance);
                return;
            }

            SerializeOther(value, bufferWriter, NinoFormatterCache<T>.Instance);
        }

        // Out of line: inlined, it would take the caller's inlining budget for paths the calls above do not take.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void SerializeOther<T>(T value, IBufferWriter<byte> bufferWriter, NinoFormatter<T> formatter)
        {
            if (bufferWriter is NinoArrayBufferWriter nino)
            {
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
}
