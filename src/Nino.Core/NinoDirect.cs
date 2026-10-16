using System;
using System.Buffers;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Nino.Core
{
    /// <summary>
    /// For generated code: the top-level <see cref="NinoSerializer"/> and <see cref="NinoDeserializer"/> calls with
    /// the formatter the generator resolved for <typeparamref name="T"/> (<typeparamref name="TFormatter"/>), which
    /// it intercepts them with on compilers that support interceptors. The formatter is then called directly
    /// instead of through <see cref="NinoFormatterCache{T}"/> (a virtual call). The bytes are the same; a formatter
    /// registered at run time with <see cref="NinoFormatterCache{T}.Register{TFormatter}"/> still takes precedence.
    /// Raw values and arrays of raw elements are never intercepted: the regular calls copy them directly.
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static class NinoDirect
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static byte[] Serialize<T, TFormatter>(T value) where TFormatter : struct, INinoFormatter<T>
        {
            if (NinoFormatterCache<T>.Replaced)
                return SerializeReplaced(value);
            return SerializeToArray<T, TFormatter>(value);
        }

        // The paths taken when a formatter was registered at run time are calls: inlined, they would use up the
        // JIT's inlining budget of the caller before the generated formatter's own helpers are inlined.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static byte[] SerializeReplaced<T>(T value) => NinoSerializer.Serialize(value);

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void SerializeReplaced<T>(T value, IBufferWriter<byte> bufferWriter) =>
            NinoSerializer.Serialize(value, bufferWriter);

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void SerializeReplaced<T>(T value, ref Writer writer) =>
            NinoSerializer.Serialize(value, ref writer);

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void DeserializeReplaced<T>(ReadOnlySpan<byte> data, ref T value) =>
            NinoDeserializer.Deserialize(data, ref value);

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void DeserializeReplaced<T>(ref T value, ref Reader reader) =>
            NinoDeserializer.Deserialize(ref value, ref reader);

        private static byte[] SerializeToArray<T, TFormatter>(T value) where TFormatter : struct, INinoFormatter<T>
        {
            var buffer = NinoSerializer.RentScratch();
            try
            {
                Write<T, TFormatter>(value, buffer);
                return buffer.ToArray();
            }
            finally
            {
                NinoSerializer.ReturnScratch(buffer);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Serialize<T, TFormatter>(T value, IBufferWriter<byte> bufferWriter)
            where TFormatter : struct, INinoFormatter<T>
        {
            if (!NinoFormatterCache<T>.Replaced && bufferWriter is NinoArrayBufferWriter nino)
                Write<T, TFormatter>(value, nino);
            else
                SerializeReplaced(value, bufferWriter);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Serialize<T, TFormatter>(T value, ref Writer writer)
            where TFormatter : struct, INinoFormatter<T>
        {
            if (NinoFormatterCache<T>.Replaced)
            {
                // Through a copy: the writer's address reaching a call would keep its fields in memory.
                var copy = writer;
                SerializeReplaced(value, ref copy);
                writer = copy;
            }
            else
                default(TFormatter).Serialize(value, ref writer);
        }

        private static void Write<T, TFormatter>(T value, NinoArrayBufferWriter buffer)
            where TFormatter : struct, INinoFormatter<T>
        {
            var writer = new Writer(buffer);
            default(TFormatter).Serialize(value, ref writer);
            writer.Flush();
            writer.ReleaseReferences();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static T Deserialize<T, TFormatter>(ReadOnlySpan<byte> data) where TFormatter : struct, INinoFormatter<T>
        {
            // The replaced-formatter path is out of line: inlined, it takes the address of value and of a second
            // Reader, which keeps value in memory and zeroes both in the prolog of every call.
            if (NinoFormatterCache<T>.Replaced)
                return DeserializeReplaced<T>(data);

            var reader = new Reader(data);
            T value = default;
            default(TFormatter).Deserialize(ref value, ref reader);
            reader.ReleaseReferences();
            return value;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static T DeserializeReplaced<T>(ReadOnlySpan<byte> data) => NinoDeserializer.Deserialize<T>(data);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Deserialize<T, TFormatter>(ReadOnlySpan<byte> data, ref T value)
            where TFormatter : struct, INinoFormatter<T>
        {
            if (NinoFormatterCache<T>.Replaced)
            {
                DeserializeReplaced(data, ref value);
                return;
            }

            var reader = new Reader(data);
            default(TFormatter).Deserialize(ref value, ref reader);
            reader.ReleaseReferences();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Deserialize<T, TFormatter>(ref T value, ref Reader reader)
            where TFormatter : struct, INinoFormatter<T>
        {
            if (NinoFormatterCache<T>.Replaced)
            {
                // Through a copy, as in Serialize.
                var copy = reader;
                DeserializeReplaced(ref value, ref copy);
                reader = copy;
            }
            else
                default(TFormatter).Deserialize(ref value, ref reader);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static T Deserialize<T, TFormatter>(ref Reader reader) where TFormatter : struct, INinoFormatter<T>
        {
            T value = default;
            Deserialize<T, TFormatter>(ref value, ref reader);
            return value;
        }
    }
}
